using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    public class CloudflareTunnelManager
    {
        private readonly ConnectorConfig _config;
        private Process? _tunnelProcess;

        public string TunnelUrl { get; private set; } = "";

        public CloudflareTunnelManager(ConnectorConfig config)
        {
            _config = config;
        }

        public Task StartTunnelAsync()
        {
            if (!_config.EnableCloudflare)
            {
                Log.Information("Cloudflare tunnel disabled");
                return Task.CompletedTask;
            }

            try
            {
                Log.Information("Starting Cloudflare tunnel...");

                // Check if cloudflared is installed
                var checkProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "cloudflared",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (checkProcess == null)
                {
                    Log.Warning("cloudflared not found. Install from: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/");
                    return Task.CompletedTask;
                }

                checkProcess.WaitForExit();

                // Start tunnel
                _tunnelProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "cloudflared",
                    Arguments = $"tunnel --url http://localhost:{_config.ApiPort}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (_tunnelProcess != null)
                {
                    _tunnelProcess.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data?.Contains("https://") == true)
                        {
                            var url = e.Data.Substring(e.Data.IndexOf("https://"));
                            TunnelUrl = url.Trim();
                            Log.Information("Tunnel URL: {Url}", TunnelUrl);
                        }
                    };

                    _tunnelProcess.BeginOutputReadLine();
                }

                Log.Information("✓ Cloudflare tunnel started");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to start Cloudflare tunnel (continuing without it)");
            }

            return Task.CompletedTask;
        }

        public void Stop()
        {
            _tunnelProcess?.Kill();
            _tunnelProcess?.Dispose();
        }
    }
}
