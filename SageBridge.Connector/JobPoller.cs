using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly IReadOnlyList<SageCompanyProfile> _profiles;
        private readonly Dictionary<string, bool> _processedJobs;
        private readonly HashSet<string> _completedIdempotencyKeys;
        private OperationLedger _ledger;
        private CancellationTokenSource _cancellationToken;
        private Task _pollingTask;

        public JobPoller(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _profiles = config.ResolveCompanyProfiles();
            var defaultCompanyId = _profiles.FirstOrDefault()?.CloudCompanyId ?? config.CompanyId;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, defaultCompanyId);
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

        private void ReconcileUncertainOperations(SageCompanyProfile profile)
        {
            Log.Information("Reconciling uncertain operations from previous session...");
            try
            {
                var uncertainOps = _ledger.GetOperationsByState("processing")
                    .Where(op => string.Equals(op.CompanyId, profile.CloudCompanyId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var op in uncertainOps)
                {
                    Log.Information("Found uncertain operation: {Key} - checking Sage", op.IdempotencyKey);
                    if (op.Action == "quote.create" || op.Action == "invoice.create")
                    {
                        ReconcileUncertainNumberedDocument(op);
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

        /// <summary>
        /// Reconciles a quote.create or invoice.create operation whose local
        /// state is unclear (interrupted after Post() but before the ledger
        /// could confirm delivery) by checking whether the document actually
        /// exists in Sage under the previously recorded SageRecordId. Never
        /// re-runs the Sage write - only decides succeeded vs. uncertain.
        /// </summary>
        private void ReconcileUncertainNumberedDocument(OperationRecord op)
        {
            var sageId = op.SageRecordId;
            var kind = op.Action == "invoice.create" ? "invoice" : "quote";
            if (string.IsNullOrWhiteSpace(sageId))
            {
                Log.Warning(
                    "Uncertain {Action} operation {Key} has no sage_record_id - cannot reconcile automatically",
                    op.Action, op.IdempotencyKey);
                _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
                return;
            }

            try
            {
                bool exists = op.Action == "invoice.create"
                    ? _sageService.InvoiceExistsInSage(sageId)
                    : _sageService.QuoteExistsInSage(sageId);
                if (exists)
                {
                    _ledger.MarkSucceeded(op.IdempotencyKey, op.CompanyId);
                    Log.Information(
                        "Reconciled uncertain {Kind} {Number}: found in Sage, marked succeeded",
                        kind, sageId);
                }
                else
                {
                    _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
                    Log.Warning(
                        "Reconciled uncertain {Kind} {Number}: not found in Sage, left uncertain for manual review",
                        kind, sageId);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed to reconcile uncertain {Kind} {Number} - leaving uncertain",
                    kind, sageId);
                _ledger.MarkUncertain(op.IdempotencyKey, op.CompanyId);
            }
        }

        /// <summary>
        /// Retries delivering a result the connector already knows (Sage
        /// write confirmed succeeded, SageRecordId known) but the cloud has
        /// not yet acknowledged. Re-sends the claim first in case the
        /// original /start call itself never reached the cloud, then
        /// re-submits the terminal result. Never touches Sage again.
        /// </summary>
        private async Task RetryResultDelivery(OperationRecord op)
        {
            if (string.IsNullOrWhiteSpace(op.JobId))
            {
                Log.Warning(
                    "Operation {Key} is result_pending but has no jobId recorded - cannot redeliver",
                    op.IdempotencyKey);
                return;
            }

            await MarkJobStarted(op.JobId, op.CompanyId);
            var delivered = await SubmitJobResult(op.JobId, "succeeded", op.SageRecordId, null, op.CompanyId);
            if (delivered)
            {
                _ledger.MarkSucceeded(op.IdempotencyKey, op.CompanyId);
                _completedIdempotencyKeys.Add(ScopeKey(op.CompanyId, op.IdempotencyKey));
                Log.Information(
                    "Delivered previously-pending result for {Key} (SageRecordId {Id})",
                    op.IdempotencyKey, op.SageRecordId);
            }
            else
            {
                Log.Warning(
                    "Still unable to deliver pending result for {Key} - will retry on next poll",
                    op.IdempotencyKey);
            }
        }

        /// <summary>
        /// Retries delivery for every operation whose Sage write is known to
        /// have succeeded but whose cloud acknowledgement is still missing.
        /// Runs every poll tick (not only when the job reappears in
        /// /connector/jobs) because the cloud only re-lists a claimed job
        /// after its claim expires (up to 2 minutes), which would otherwise
        /// leave a known-good result undelivered for that whole window.
        /// </summary>
        private async Task FlushPendingResults(SageCompanyProfile profile)
        {
            List<OperationRecord> pending;
            try
            {
                pending = _ledger.GetOperationsByState("result_pending")
                    .Where(op => string.Equals(op.CompanyId, profile.CloudCompanyId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read pending results from ledger");
                return;
            }

            foreach (var op in pending)
            {
                if (_completedIdempotencyKeys.Contains(ScopeKey(op.CompanyId, op.IdempotencyKey)))
                    continue;
                await RetryResultDelivery(op);
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
                    foreach (var profile in _profiles)
                    {
                        try
                        {
                            await _sageService.RunForCompanyAsync(profile, async () =>
                            {
                                ReconcileUncertainOperations(profile);
                                await FlushPendingResults(profile);
                                await PollAndProcessJobs(profile);
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Job polling failed for cloud company {CompanyId}", profile.CloudCompanyId);
                        }
                    }
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

        private async Task PollAndProcessJobs(SageCompanyProfile profile)
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
                var response = await _auth.GetAsync("/connector/jobs", profile.CloudCompanyId);

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
                    await ProcessJob(job, profile);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error polling for jobs");
            }
        }

        private async Task ProcessJob(Job job, SageCompanyProfile profile)
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

                var companyId = profile.CloudCompanyId;
                if (!string.Equals(job.CompanyId, companyId, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("Job {JobId} company {JobCompanyId} does not match polled company {CompanyId}; refusing Sage write",
                        job.JobId, job.CompanyId, companyId);
                    return;
                }
                if (!string.Equals(_sageService.CurrentCloudCompanyId, companyId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Sage SDK session is not bound to the job company.");

                var action = job.Action;

                if (_completedIdempotencyKeys.Contains(ScopeKey(companyId, idempotencyKey)))
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

                        case "result_pending":
                            Log.Information("Operation result pending cloud ack - retrying delivery");
                            await RetryResultDelivery(existingOp);
                            return;

                        case "processing":
                            Log.Warning("Operation in processing state - reconciling with Sage");
                            if (existingOp.Action == "quote.create" || existingOp.Action == "invoice.create")
                            {
                                ReconcileUncertainNumberedDocument(existingOp);
                                return;
                            }

                            var existingCustomer = await _sageService.GetCustomerByNameAsync(GetCustomerName(existingOp));
                            if (existingCustomer != null)
                            {
                                var reconciledSageId = existingCustomer.GetType().GetProperty("Id")?.GetValue(existingCustomer)?.ToString();
                                _ledger.MarkSucceeded(idempotencyKey, companyId);
                                _completedIdempotencyKeys.Add(ScopeKey(companyId, idempotencyKey));
                                Log.Information("Reconciled: customer exists in Sage (ID: {Id})", reconciledSageId);
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

                await MarkJobStarted(job.JobId, companyId);

                object result = null;
                string error = null;
                string status = "succeeded";
                string sageId = null;

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

                        case "invoice.create":
                            result = await HandleCreateInvoice(job.Payload);
                            break;

                        default:
                            throw new NotImplementedException($"Action not implemented: {job.Action}");
                    }

                    sageId = ((dynamic)result).Id;
                    _ledger.UpdateSageRecordId(idempotencyKey, companyId, sageId);
                    // The Sage write is now durably known-successful. Mark
                    // result_pending (not succeeded) until the cloud actually
                    // confirms it - this is the boundary that protects the
                    // "Post() succeeds, connection drops before the cloud
                    // hears about it" case: on any interruption from here on,
                    // restart-time reconciliation and the poll loop's
                    // FlushPendingResults retry delivery using this same
                    // SageRecordId, and never call the Sage SDK again for it.
                    _ledger.MarkResultPending(idempotencyKey, companyId);
                    Log.Information("Saved Sage record ID {Id} to ledger; cloud ack pending", sageId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Job {JobId} failed", job.JobId);
                    error = ex.Message;
                    status = "failed";
                    _ledger.MarkFailed(idempotencyKey, companyId);
                }

                var delivered = await SubmitJobResult(job.JobId, status, sageId, error, companyId);

                if (status == "succeeded")
                {
                    if (delivered)
                    {
                        _ledger.MarkSucceeded(idempotencyKey, companyId);
                        _completedIdempotencyKeys.Add(ScopeKey(companyId, idempotencyKey));
                        Log.Information("IdempotencyKey {Key} recorded as succeeded", idempotencyKey);
                    }
                    else
                    {
                        Log.Warning("Cloud did not confirm the result for {Key} yet - will retry delivery", idempotencyKey);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fatal error processing job {JobId}", job.JobId);
            }
        }

        private static string ScopeKey(string companyId, string idempotencyKey)
        {
            return companyId + ":" + idempotencyKey;
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

        /// <summary>
        /// Narrow supported sales invoice: a customer plus one or more
        /// existing Sage items, validated identically to quote.create. Sage
        /// assigns the actual invoice number/id on Post(); it is read back
        /// via SageService.CreateInvoiceAsync rather than pre-generated like
        /// a quote's OrderQuoteNum.
        /// </summary>
        private async Task<object> HandleCreateInvoice(JObject payload)
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

            Log.Information("Creating sales invoice for customerId {CustomerId}", customerId);

            var result = await _sageService.CreateInvoiceAsync(customerId, lines);

            return result;
        }

        private async Task MarkJobStarted(string jobId, string companyId)
        {
            try
            {
                await _auth.PostAsync($"/connector/jobs/{jobId}/start", new { }, companyId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to mark job started");
            }
        }

        /// <summary>
        /// Submits a job's terminal result to the cloud. The cloud API
        /// expects {status, sageId, error} - NOT the raw Sage result object -
        /// and requires a non-empty sageId for a 'succeeded' result. Returns
        /// true only when the cloud actually acknowledged the result (2xx,
        /// which the API also returns for an idempotent replay of an
        /// already-recorded matching result); returns false on any network
        /// failure or rejection so the caller knows to retry later without
        /// re-running the Sage write.
        /// </summary>
        private async Task<bool> SubmitJobResult(string jobId, string status, string sageId, string error, string companyId)
        {
            try
            {
                var resultData = new
                {
                    status,
                    sageId,
                    error
                };

                var response = await _auth.PostAsync($"/connector/jobs/{jobId}/result", resultData, companyId);
                if (response.IsSuccessStatusCode)
                    return true;

                var body = await response.Content.ReadAsStringAsync();
                Log.Warning("Cloud did not accept job result for {JobId}: {StatusCode} {Body}",
                    jobId, response.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error submitting job result for {JobId}", jobId);
                return false;
            }
        }

        private class Job
        {
            public string JobId { get; set; }
            public string CompanyId { get; set; }
            public string Action { get; set; }
            public JObject Payload { get; set; }
            public string RequestId { get; set; }
            public string IdempotencyKey { get; set; }
            public int Attempts { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}
