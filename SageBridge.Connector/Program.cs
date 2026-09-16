using System;
using System.Collections.Generic;
using System.Linq;
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
                Log.Information("║   SageBridge Connector v{Version}          ║", ConnectorVersion.Current);
                Log.Information("║   Sage 50 Canada → Cloudflare Bridge   ║");
                Log.Information("╚════════════════════════════════════════╝");
                Log.Information("");

                // Initialize components
                var config = ConnectorConfig.Load();
                List<SageCompanyProfile> companyProfiles;
                try
                {
                    companyProfiles = config.ResolveCompanyProfiles().ToList();
                }
                catch (InvalidOperationException)
                {
                    // --setup can rescue a config with incomplete profiles.
                    companyProfiles = new List<SageCompanyProfile>();
                }

                if (companyProfiles.Count == 0 &&
                    (args.Length == 0 || (args[0] != "--setup" && args[0] != "--pair")))
                {
                    Console.WriteLine("No Sage company profiles are configured or one is incomplete.");
                    Console.WriteLine("Run: SageBridgeConnector.exe --setup");
                    return;
                }

                using var sageService = new SageService(config);
                var apiServer = new ApiServer(config, sageService);
                var syncEngine = new SyncEngine(config, sageService);
                var tunnelManager = new CloudflareTunnelManager(config);
                var heartbeatSender = new HeartbeatSender(config, sageService);

                // Handle pairing mode
                if (args.Length > 0 && args[0] == "--pair")
                {
                    var wizard = new PairingWizard(config.CloudflareWorkerUrl);
                    var paired = await wizard.RunAsync();
                    Environment.Exit(paired ? 0 : 1);
                    return;
                }

                // Handle company setup mode
                if (args.Length > 0 && args[0] == "--setup")
                {
                    var setupWizard = new CompanySetupWizard(config);
                    var setup = setupWizard.Run();
                    Environment.Exit(setup ? 0 : 1);
                    return;
                }

                // Check if connector is paired. If not, show the first-run GUI.
                if (!CredentialManager.HasStoredIdentity())
                {
                    Log.Information("Connector is not paired. Showing first-run pairing GUI...");
                    var guiResult = SageBridge.GuiPanel.FirstRunWindow.Run(config.CloudflareWorkerUrl);
                    if (guiResult != 0)
                    {
                        Log.Error("Pairing was not completed.");
                        return;
                    }
                    Log.Information("Pairing successful.");
                }

                // Start API server
                await apiServer.StartAsync();
                Log.Information("✓ REST API started on http://localhost:{Port}", config.ApiPort);

                // Verify every configured Sage/cloud binding before background work starts.
                // SimplySDK supports one database per process, so this intentionally
                // closes and reopens the SDK session for each profile in sequence.
                var connectedCompanies = 0;
                foreach (var profile in companyProfiles)
                {
                    if (await sageService.ConnectAsync(profile))
                    {
                        connectedCompanies++;
                        Log.Information("Connected cloud company {CompanyId} to Sage company {CompanyName}",
                            profile.CloudCompanyId, sageService.CompanyName);
                    }
                    else
                    {
                        Log.Error("Could not open Sage profile for cloud company {CompanyId}", profile.CloudCompanyId);
                    }
                }

                if (connectedCompanies == 0)
                {
                    Log.Error("Failed to connect any configured Sage 50 company");
                    Log.Information("Confirm each configured .SAI path and Sage credential, then restart the connector");
                    Console.ReadLine();
                    return;
                }
                Log.Information("Verified {Connected}/{Configured} Sage company binding(s)", connectedCompanies, companyProfiles.Count);

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

                // Start heartbeat (keeps companies.last_seen_at fresh so the
                // UI's online/offline indicator reflects reality)
                heartbeatSender.Start();
                Log.Information("✓ Heartbeat started (every 30s)");

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
                heartbeatSender.Stop();
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
