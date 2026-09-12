using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace SageBridge.Connector
{
    public class SyncEngine
    {
        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private readonly CloudAuthenticator _auth;
        private Timer? _timer;

        public SyncEngine(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, config.CompanyId);
        }

        public Task StartAsync()
        {
            if (!_config.EnableCloudflare)
            {
                Log.Information("Cloudflare sync disabled (running in local-only mode)");
                return Task.CompletedTask;
            }

            if (!_auth.LoadIdentity())
            {
                Log.Error("Cannot start sync: connector not paired. Run with --pair first.");
                return Task.CompletedTask;
            }

            _timer = new Timer(
                async _ => await SyncToCloudAsync(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(_config.SyncIntervalSeconds)
            );

            return Task.CompletedTask;
        }

        private async Task SyncToCloudAsync()
        {
            try
            {
                Log.Debug("Starting sync to Cloudflare...");

                // Drive the cloud's provisioning state machine from actual sync
                // progress. Every state here is attempted on every sync cycle,
                // not gated by a "first sync" flag: the cloud's transition
                // table (src/handlers/phase1.ts NEXT) only accepts the exact
                // next state, so once a company is past a given stage every
                // earlier attempt below is a harmless no-op/409, and repeating
                // the sequence is what makes this self-healing across
                // connector restarts and partial previous failures - the
                // source of truth is always "what did we just actually sync",
                // never a local flag or simulated timer.
                await ReportProvisioningAsync("company_selected", 30);
                await ReportProvisioningAsync("provisioning", 40);

                // Sync customers
                await ReportProvisioningAsync("syncing_customers", 55);
                var customers = await _sageService.GetCustomersAsync();
                await PostToCloudAsync("/sync/customers", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Customers = customers,
                    Timestamp = DateTime.UtcNow
                });

                // Sync invoices
                await ReportProvisioningAsync("syncing_invoices", 65);
                var invoices = await _sageService.GetInvoicesAsync();
                await PostToCloudAsync("/sync/invoices", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Invoices = invoices,
                    Timestamp = DateTime.UtcNow
                });

                // Sync products
                await ReportProvisioningAsync("syncing_products", 75);
                var products = await _sageService.GetProductsAsync();
                await PostToCloudAsync("/sync/products", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Products = products,
                    Timestamp = DateTime.UtcNow
                });

                // Sync quotes
                await ReportProvisioningAsync("syncing_quotes", 85);
                var quotes = await _sageService.GetQuotesAsync();
                await PostToCloudAsync("/sync/quotes", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Quotes = quotes,
                    Timestamp = DateTime.UtcNow
                });

                // Sync invoice summary
                var invoiceSummary = await _sageService.GetInvoiceSummaryAsync();
                await PostToCloudAsync("/sync/invoice-summary", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    InvoiceSummary = invoiceSummary,
                    Timestamp = DateTime.UtcNow
                });

                await ReportProvisioningAsync("finalizing", 95);
                await ReportProvisioningAsync("ready", 100);

                Log.Information("✓ Sync completed successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Sync failed");
                // A failed initial sync must leave the workspace in a real
                // failed/retry state, never silently stuck or reported as
                // ready. NEXT[*] always includes 'failed', so this is a valid
                // transition from wherever the cycle above got to.
                await ReportProvisioningAsync("failed", 0, "SYNC_FAILED", ex.Message);
            }
        }

        /// <summary>
        /// Reports actual sync progress to the cloud's provisioning state
        /// machine (POST /connector/provisioning). Never throws: an attempt
        /// that the cloud rejects (e.g. the state machine has already moved
        /// past this point, or provisioning hasn't been started by the user
        /// yet) is expected and harmless - this method's only job is to keep
        /// the UI's provisioning panel in sync with what the connector is
        /// actually doing, not to enforce the state machine itself.
        /// </summary>
        private async Task ReportProvisioningAsync(string state, int progress, string? errorCode = null, string? errorMessage = null)
        {
            try
            {
                var response = await _auth.PostAsync("/connector/provisioning", new { state, progress, errorCode, errorMessage });
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Log.Debug("Provisioning state {State} not accepted ({StatusCode}): {Body}", state, response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to report provisioning state {State}", state);
            }
        }

        private async Task PostToCloudAsync(string endpoint, object data)
        {
            var response = await _auth.PostAsync(endpoint, data);
            response.EnsureSuccessStatusCode();
        }

        public void Stop()
        {
            _timer?.Dispose();
        }
    }
}
