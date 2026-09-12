using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SageBridge.Connector
{
    public class JobPoller
    {
        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private readonly CloudAuthenticator _auth;
        private readonly Dictionary<string, bool> _processedJobs;
        private readonly HashSet<string> _completedIdempotencyKeys;
        private OperationLedger _ledger;
        private CancellationTokenSource _cancellationToken;
        private Task _pollingTask;

        public JobPoller(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, config.CompanyId);
            _processedJobs = new Dictionary<string, bool>();
            _completedIdempotencyKeys = new HashSet<string>();
        }

        public void Start()
        {
            Log.Information("Starting job poller (polling every 3 seconds)...");
            var ledgerPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(JobPoller).Assembly.Location) ?? ".",
                "operation_ledger.db");
            _ledger = new OperationLedger(ledgerPath);
            Log.Information("Operation ledger initialized at {Path}", ledgerPath);
            ReconcileUncertainOperations();
            _cancellationToken = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollLoop(_cancellationToken.Token));
        }

        public void Stop()
        {
            Log.Information("Stopping job poller...");
            _cancellationToken?.Cancel();
            _pollingTask?.Wait(TimeSpan.FromSeconds(10));
            _ledger?.Dispose();
        }

        private void ReconcileUncertainOperations()
        {
            Log.Information("Reconciling uncertain operations from previous session...");
            try
            {
                var uncertainOps = _ledger.GetOperationsByState("processing");
                foreach (var op in uncertainOps)
                {
                    Log.Information("Found uncertain operation: {Key} - checking Sage", op.IdempotencyKey);
                    if (op.Action == "quote.create")
                    {
                        ReconcileUncertainQuote(op);
                        continue;
                    }

                    var existingCustomer = _sageService.GetCustomerByNameAsync(op.Action.Contains("customer") ? GetCustomerNameFromPayload(op) : "").Result;
                    if (existingCustomer != null)
                    {
                        var sageId = existingCustomer.GetType().GetProperty("Id")?.GetValue(existingCustomer)?.ToString();
                        _ledger.MarkSucceeded(op.IdempotencyKey, op.CompanyId);
                        Log.Information("Reconciled: customer exists in Sage (ID: {Id}), marked succeeded", sageId);
                    }
                    else
                    {
                        _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
                        Log.Information("Reconciled: customer not found in Sage, marked uncertain for manual review");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during reconciliation");
            }
        }

        private void ReconcileUncertainQuote(OperationRecord op)
        {
            var sageId = op.SageRecordId;
            if (string.IsNullOrWhiteSpace(sageId))
            {
                Log.Warning(
                    "Uncertain quote.create operation {Key} has no sage_record_id - cannot reconcile automatically",
                    op.IdempotencyKey);
                _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
                return;
            }

            try
            {
                bool exists = _sageService.QuoteExistsInSage(sageId);
                if (exists)
                {
                    _ledger.MarkSucceeded(op.IdempotencyKey, op.CompanyId);
                    Log.Information(
                        "Reconciled uncertain quote {QuoteNumber}: found in Sage, marked succeeded",
                        sageId);
                }
                else
                {
                    _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
                    Log.Warning(
                        "Reconciled uncertain quote {QuoteNumber}: not found in Sage, left uncertain for manual review",
                        sageId);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed to reconcile uncertain quote {QuoteNumber} - leaving uncertain",
                    sageId);
                _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
            }
        }

        private string GetCustomerNameFromPayload(OperationRecord op)
        {
            try
            {
                var payload = JObject.Parse(op.SageRecordId ?? "{}");
                return payload["name"]?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private async Task PollLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PollAndProcessJobs();
                    await Task.Delay(3000, cancellationToken); // Poll every 3 seconds
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in polling loop");
                    await Task.Delay(5000, cancellationToken); // Wait longer on error
                }
            }
        }

        private async Task PollAndProcessJobs()
        {
            if (!_config.EnableCloudflare || string.IsNullOrEmpty(_config.CloudflareWorkerUrl))
            {
                return;
            }

            if (!_auth.LoadIdentity())
            {
                Log.Warning("Cannot poll jobs: connector not paired");
                return;
            }

            try
            {
                var response = await _auth.GetAsync("/connector/jobs");

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to fetch jobs: {Status}", response.StatusCode);
                    return;
                }

                var jsonString = await response.Content.ReadAsStringAsync();
                var jobsResponse = JObject.Parse(jsonString);
                var jobs = jobsResponse["jobs"]?.ToObject<List<Job>>();

                if (jobs == null || jobs.Count == 0)
                {
                    return;
                }

                Log.Information("Received {Count} job(s) to process", jobs.Count);

                foreach (var job in jobs)
                {
                    await ProcessJob(job);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error polling for jobs");
            }
        }

        private async Task ProcessJob(Job job)
        {
            try
            {
                var idempotencyKey = job.Payload?["idempotencyKey"]?.ToString()
                                    ?? job.RequestId;

                if (string.IsNullOrEmpty(idempotencyKey))
                {
                    Log.Warning("Job {JobId} has no idempotencyKey - skipping", job.JobId);
                    return;
                }

                var companyId = _config.CompanyId ?? "default";
                var action = job.Action;

                if (_completedIdempotencyKeys.Contains(idempotencyKey))
                {
                    Log.Information("Job {JobId} with idempotencyKey {Key} already completed - skipping",
                        job.JobId, idempotencyKey);
                    return;
                }

                var existingOp = _ledger.GetOperation(idempotencyKey, companyId);
                if (existingOp != null)
                {
                    Log.Information("Found existing operation for idempotencyKey {Key}: state={State}",
                        idempotencyKey, existingOp.State);

                    switch (existingOp.State)
                    {
                        case "succeeded":
                            Log.Information("Operation already succeeded (SageRecordId: {Id}) - skipping",
                                existingOp.SageRecordId);
                            return;

                        case "processing":
                            Log.Warning("Operation in processing state - reconciling with Sage");
                            if (existingOp.Action == "quote.create")
                            {
                                ReconcileUncertainQuote(existingOp);
                                return;
                            }

                            var existingCustomer = await _sageService.GetCustomerByNameAsync(GetCustomerName(existingOp));
                            if (existingCustomer != null)
                            {
                                var sageId = existingCustomer.GetType().GetProperty("Id")?.GetValue(existingCustomer)?.ToString();
                                _ledger.MarkSucceeded(idempotencyKey, companyId);
                                _completedIdempotencyKeys.Add(idempotencyKey);
                                Log.Information("Reconciled: customer exists in Sage (ID: {Id})", sageId);
                            }
                            else
                            {
                                _ledger.MarkUncertain(idempotencyKey, companyId);
                                Log.Warning("Reconciled: customer not found in Sage - marked uncertain for manual review");
                            }
                            return;

                        case "failed":
                            Log.Information("Operation previously failed - allowing retry");
                            break;

                        case "uncertain":
                            Log.Warning("Operation marked uncertain - stopping for manual review");
                            return;

                        default:
                            Log.Warning("Unknown state {State} - skipping", existingOp.State);
                            return;
                    }
                }

                if (existingOp != null && existingOp.Action == action)
                {
                    var payloadHash = OperationLedger.ComputeHash(action, job.Payload);
                    if (existingOp.PayloadHash != payloadHash)
                    {
                        Log.Warning("Idempotency conflict for key {Key}: action or payload differs", idempotencyKey);
                        return;
                    }
                }

                Log.Information("Processing job {JobId}: {Action} (idempotencyKey: {Key})",
                    job.JobId, action, idempotencyKey);

                var hash = OperationLedger.ComputeHash(action, job.Payload);
                var payloadJson = job.Payload?.ToString(Newtonsoft.Json.Formatting.None);
                _ledger.CreateOperation(idempotencyKey, companyId, action, hash, payloadJson, job.JobId);
                Log.Information("Recorded operation in ledger: {Key}", idempotencyKey);

                await MarkJobStarted(job.JobId);

                object result = null;
                string error = null;
                string status = "succeeded";

                try
                {
                    switch (job.Action)
                    {
                        case "customer.create":
                            result = await HandleCreateCustomer(job.Payload);
                            break;

                        case "quote.create":
                            result = await HandleCreateQuote(job.Payload);
                            break;

                        default:
                            throw new NotImplementedException($"Action not implemented: {job.Action}");
                    }

                    var sageId = ((dynamic)result).Id;
                    _ledger.UpdateSageRecordId(idempotencyKey, companyId, sageId);
                    Log.Information("Saved Sage record ID {Id} to ledger", sageId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Job {JobId} failed", job.JobId);
                    error = ex.Message;
                    status = "failed";
                    _ledger.MarkFailed(idempotencyKey, companyId);
                }

                await SubmitJobResult(job.JobId, status, result, error);

                if (status == "succeeded")
                {
                    _ledger.MarkSucceeded(idempotencyKey, companyId);
                    _completedIdempotencyKeys.Add(idempotencyKey);
                    Log.Information("IdempotencyKey {Key} recorded as succeeded", idempotencyKey);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fatal error processing job {JobId}", job.JobId);
            }
        }

        private string GetCustomerName(OperationRecord op)
        {
            try
            {
                var payload = JObject.Parse(op.PayloadJson ?? "{}");
                return payload["name"]?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<object> HandleCreateCustomer(JObject payload)
        {
            var name = payload["name"]?.ToString();
            var email = payload["email"]?.ToString();
            var phone = payload["phone"]?.ToString();

            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Customer name is required");
            }

            Log.Information("Creating customer in Sage: {Name}", name);

            var customer = await _sageService.CreateCustomerAsync(name, email, phone);

            Log.Information("Customer created in Sage 50: {Name} (ID: {Id})",
                customer.GetType().GetProperty("Name")?.GetValue(customer),
                customer.GetType().GetProperty("Id")?.GetValue(customer));

            return customer;
        }

        private async Task<object> HandleCreateQuote(JObject payload)
        {
            var customerId = payload["customerId"]?.ToString();
            if (string.IsNullOrWhiteSpace(customerId))
                throw new ArgumentException("customerId is required.");

            var linesToken = payload["lines"];
            if (linesToken == null || linesToken.Type != JTokenType.Array)
                throw new ArgumentException("lines is required and must be an array.");

            var lines = linesToken.ToObject<List<QuoteLineRequest>>();
            if (lines == null)
                throw new ArgumentException("lines could not be parsed.");

            if (lines.Count < 1 || lines.Count > 100)
                throw new ArgumentException("lines must contain between 1 and 100 entries.");

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line.Sku))
                    throw new ArgumentException("Each line must include a non-empty sku.");

                if (line.Quantity <= 0)
                    throw new ArgumentException(
                        $"Line for sku '{line.Sku}' has an invalid quantity. Quantity must be greater than 0.");

                if (line.UnitPrice < 0)
                    throw new ArgumentException(
                        $"Line for sku '{line.Sku}' has an invalid unitPrice. Unit price must be greater than or equal to 0.");
            }

            var customerName = await _sageService.ResolveCustomerNameAsync(customerId);
            if (string.IsNullOrWhiteSpace(customerName))
                throw new ArgumentException(
                    $"No Sage customer found for customerId '{customerId}'.", "customerId");

            foreach (var line in lines)
            {
                var product = await _sageService.GetProductBySkuAsync(line.Sku);
                if (product == null)
                    throw new ArgumentException(
                        $"Sage has no active or inactive item with sku '{line.Sku}'.", "lines");
            }

            var quoteNumber = _sageService.GenerateQuoteNumber();

            Log.Information(
                "Generated quote number {QuoteNumber} for customerId {CustomerId}",
                quoteNumber, customerId);

            var result = await _sageService.CreateQuoteAsync(customerId, lines, quoteNumber);

            return result;
        }

        private async Task MarkJobStarted(string jobId)
        {
            try
            {
                await _auth.PostAsync($"/connector/jobs/{jobId}/start", new { });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to mark job started");
            }
        }

        private async Task SubmitJobResult(string jobId, string status, object result, string error)
        {
            try
            {
                var resultData = new
                {
                    status,
                    result,
                    error
                };

                await _auth.PostAsync($"/connector/jobs/{jobId}/result", resultData);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error submitting job result");
            }
        }

        private class Job
        {
            public string JobId { get; set; }
            public string Action { get; set; }
            public JObject Payload { get; set; }
            public string RequestId { get; set; }
            public string IdempotencyKey { get; set; }
            public int Attempts { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}
