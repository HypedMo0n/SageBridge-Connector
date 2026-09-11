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
        private readonly HttpClient _httpClient;
        private Timer? _timer;

        public SyncEngine(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(config.CloudflareWorkerUrl)
            };
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
        }

        public Task StartAsync()
        {
            if (!_config.EnableCloudflare)
            {
                Log.Information("Cloudflare sync disabled (running in local-only mode)");
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

                // Sync customers
                var customers = await _sageService.GetCustomersAsync();
                await PostToCloudAsync("/sync/customers", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Customers = customers,
                    Timestamp = DateTime.UtcNow
                });

                // Sync invoices
                var invoices = await _sageService.GetInvoicesAsync();
                await PostToCloudAsync("/sync/invoices", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Invoices = invoices,
                    Timestamp = DateTime.UtcNow
                });

                // Sync products
                var products = await _sageService.GetProductsAsync();
                await PostToCloudAsync("/sync/products", new
                {
                    TenantId = _config.TenantId,
                    CompanyId = _config.CompanyId,
                    Products = products,
                    Timestamp = DateTime.UtcNow
                });

                Log.Information("✓ Sync completed successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Sync failed");
            }
        }

        private async Task PostToCloudAsync(string endpoint, object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();
        }

        public void Stop()
        {
            _timer?.Dispose();
        }
    }
}
