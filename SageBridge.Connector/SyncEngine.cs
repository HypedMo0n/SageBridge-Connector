using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    public class SyncEngine
    {
        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private readonly CloudAuthenticator _auth;
        private readonly IReadOnlyList<SageCompanyProfile> _profiles;
        private readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);
        private Timer? _timer;

        public SyncEngine(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _profiles = config.ResolveCompanyProfiles();
            var defaultCompanyId = _profiles.FirstOrDefault()?.CloudCompanyId ?? config.CompanyId;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, defaultCompanyId);
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
            if (!await _syncGate.WaitAsync(0))
            {
                Log.Debug("Skipping overlapping multi-company sync cycle");
                return;
            }

            try
            {
                foreach (var profile in _profiles)
                {
                    try
                    {
                        await _sageService.RunForCompanyAsync(profile, () => SyncCompanyToCloudAsync(profile));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Sync failed for cloud company {CompanyId}", profile.CloudCompanyId);
                        await ReportProvisioningAsync("failed", 0, profile.CloudCompanyId, "SYNC_FAILED", ex.Message);
                    }
                }
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private async Task SyncCompanyToCloudAsync(SageCompanyProfile profile)
        {
            var companyId = profile.CloudCompanyId;
            Log.Debug("Starting Sage sync for cloud company {CompanyId}...", companyId);

            await ReportProvisioningAsync("checking_sage", 20, companyId);
            await ReportProvisioningAsync("company_selected", 30, companyId);
            await ReportProvisioningAsync("provisioning", 40, companyId);

            await ReportProvisioningAsync("syncing_customers", 55, companyId);
            var customers = await _sageService.GetCustomersAsync();
            await PostToCloudAsync("/sync/customers", new
            {
                TenantId = _config.TenantId,
                CompanyId = profile.CloudCompanyId,
                Customers = customers,
                Timestamp = DateTime.UtcNow
            }, companyId);

            await ReportProvisioningAsync("syncing_invoices", 65, companyId);
            var invoices = await _sageService.GetInvoicesAsync();
            await PostToCloudAsync("/sync/invoices", new
            {
                TenantId = _config.TenantId,
                CompanyId = profile.CloudCompanyId,
                Invoices = invoices,
                Timestamp = DateTime.UtcNow
            }, companyId);

            await ReportProvisioningAsync("syncing_products", 75, companyId);
            var products = await _sageService.GetProductsAsync();
            await PostToCloudAsync("/sync/products", new
            {
                TenantId = _config.TenantId,
                CompanyId = profile.CloudCompanyId,
                Products = products,
                Timestamp = DateTime.UtcNow
            }, companyId);

            await ReportProvisioningAsync("syncing_quotes", 85, companyId);
            var quotes = await _sageService.GetQuotesAsync();
            await PostToCloudAsync("/sync/quotes", new
            {
                TenantId = _config.TenantId,
                CompanyId = profile.CloudCompanyId,
                Quotes = quotes,
                Timestamp = DateTime.UtcNow
            }, companyId);

            var invoiceSummary = await _sageService.GetInvoiceSummaryAsync();
            await PostToCloudAsync("/sync/invoice-summary", new
            {
                TenantId = _config.TenantId,
                CompanyId = profile.CloudCompanyId,
                InvoiceSummary = invoiceSummary,
                Timestamp = DateTime.UtcNow
            }, companyId);

            await ReportProvisioningAsync("finalizing", 95, companyId);
            await ReportProvisioningAsync("ready", 100, companyId);
            Log.Information("Sync completed for cloud company {CompanyId} ({CompanyName})", companyId, _sageService.CompanyName);
        }

        private async Task ReportProvisioningAsync(string state, int progress, string companyId, string? errorCode = null, string? errorMessage = null)
        {
            try
            {
                var response = await _auth.PostAsync(
                    "/connector/provisioning",
                    new { state, progress, errorCode, errorMessage },
                    companyId);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Log.Debug("Provisioning state {State} for {CompanyId} not accepted ({StatusCode}): {Body}",
                        state, companyId, response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to report provisioning state {State} for {CompanyId}", state, companyId);
            }
        }

        private async Task PostToCloudAsync(string endpoint, object data, string companyId)
        {
            var response = await _auth.PostAsync(endpoint, data, companyId);
            response.EnsureSuccessStatusCode();
        }

        public void Stop()
        {
            _timer?.Dispose();
            _syncGate.Dispose();
        }
    }
}
