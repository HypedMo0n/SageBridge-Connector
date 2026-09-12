using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Setup logging
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs/sagebridge-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                Log.Information("╔════════════════════════════════════════╗");
                Log.Information("║   SageBridge Connector v1.0.0          ║");
                Log.Information("║   Sage 50 Canada → Cloudflare Bridge   ║");
                Log.Information("╚════════════════════════════════════════╝");
                Log.Information("");

                // Initialize components
                var config = ConnectorConfig.Load();
                using var sageService = new SageService(config);
                var apiServer = new ApiServer(config, sageService);
                var syncEngine = new SyncEngine(config, sageService);
                var tunnelManager = new CloudflareTunnelManager(config);

                // Handle pairing mode
                if (args.Length > 0 && args[0] == "--pair")
                {
                    var wizard = new PairingWizard(config.CloudflareWorkerUrl);
                    var paired = await wizard.RunAsync();
                    Environment.Exit(paired ? 0 : 1);
                    return;
                }

                // Check if connector is paired
                if (!CredentialManager.HasStoredIdentity())
                {
                    Log.Error("Connector is not paired. Run with --pair to pair this connector.");
                    Console.WriteLine();
                    Console.WriteLine("This connector needs to be paired with your SageBridge account.");
                    Console.WriteLine("Run: SageBridgeConnector.exe --pair");
                    Console.WriteLine();
                    Console.WriteLine("Or generate a pairing code from your SageBridge dashboard.");
                    Console.ReadLine();
                    return;
                }

                // Start API server
                await apiServer.StartAsync();
                Log.Information("✓ REST API started on http://localhost:{Port}", config.ApiPort);

                // Connect to Sage 50
                var connected = await sageService.ConnectAsync();
                if (!connected)
                {
                    Log.Error("✗ Failed to connect to Sage 50");
                    Log.Information("Make sure Sage 50 is installed and a company is open");
                    Console.ReadLine();
                    return;
                }
                Log.Information("✓ Connected to Sage 50: {CompanyName}", sageService.CompanyName);

                // Establish Cloudflare Tunnel (optional for POC)
                if (config.EnableCloudflare)
                {
                    await tunnelManager.StartTunnelAsync();
                    Log.Information("✓ Cloudflare Tunnel established: {Url}", tunnelManager.TunnelUrl);
                }

                // Start sync engine
                await syncEngine.StartAsync();
                Log.Information("✓ Sync engine started (interval: {Interval}s)", config.SyncIntervalSeconds);

                // Start job poller (for write operations from Cloudflare)
                var jobPoller = new JobPoller(config, sageService);
                jobPoller.Start();
                Log.Information("✓ Job poller started (polling for write operations)");

                Log.Information("");
                Log.Information("═══════════════════════════════════════");
                Log.Information("Connector is LIVE! Available endpoints:");
                Log.Information("  GET  http://localhost:{Port}/health", config.ApiPort);
                Log.Information("  GET  http://localhost:{Port}/api/company", config.ApiPort);
                Log.Information("  GET  http://localhost:{Port}/api/customers", config.ApiPort);
                Log.Information("  GET  http://localhost:{Port}/api/customers/{id}", config.ApiPort);
                Log.Information("  GET  http://localhost:{Port}/api/invoices", config.ApiPort);
                Log.Information("  GET  http://localhost:{Port}/api/products", config.ApiPort);
                Log.Information("  POST http://localhost:{Port}/api/customers", config.ApiPort);
                Log.Information("═══════════════════════════════════════");
                Log.Information("");

                Log.Information("Press Ctrl+C to stop...");

                // Keep running
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    Log.Information("Shutting down...");
                    e.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (TaskCanceledException) when (cts.IsCancellationRequested)
                {
                    // Normal Ctrl+C shutdown.
                }

                syncEngine.Stop();
                jobPoller.Stop();
                apiServer.Stop();
                Log.Information("Connector stopped cleanly");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error in connector");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
}
