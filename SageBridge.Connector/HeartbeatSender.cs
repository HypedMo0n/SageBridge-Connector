using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Sends one heartbeat for every company bound to this connector installation.
    /// Each request carries an explicit cloud company scope. A successful heartbeat
    /// confirms that the connector could switch the single SimplySDK session to the
    /// matching local Sage company; failures are reported for that company only.
    /// </summary>
    public class HeartbeatSender
    {
        private const int IntervalSeconds = 30;

        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private readonly CloudAuthenticator _auth;
        private readonly IReadOnlyList<SageCompanyProfile> _profiles;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private Timer? _timer;

        public HeartbeatSender(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _profiles = config.ResolveCompanyProfiles();
            var defaultCompanyId = _profiles.FirstOrDefault()?.CloudCompanyId ?? config.CompanyId;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, defaultCompanyId);
        }

        public void Start()
        {
            if (!_config.EnableCloudflare)
            {
                Log.Information("Cloudflare heartbeat disabled (running in local-only mode)");
                return;
            }

            if (!_auth.LoadIdentity())
            {
                Log.Warning("Cannot start heartbeat: connector not paired");
                return;
            }

            _timer = new Timer(
                async _ => await SendHeartbeatAsync(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(IntervalSeconds)
            );
        }

        private async Task SendHeartbeatAsync()
        {
            if (!await _sendGate.WaitAsync(0))
                return;

            try
            {
                foreach (var profile in _profiles)
                {
                    try
                    {
                        await _sageService.RunForCompanyAsync(profile, () => SendCompanyHeartbeatAsync(profile, true));
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not open Sage company for heartbeat {CompanyId}", profile.CloudCompanyId);
                        await SendCompanyHeartbeatAsync(profile, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat failed - will retry on next interval");
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task SendCompanyHeartbeatAsync(SageCompanyProfile profile, bool sageConnected)
        {
            try
            {
                var response = await _auth.PostAsync("/connector/heartbeat", new
                {
                    connectorVersion = ConnectorVersion.Current,
                    sageVersion = (string?)null,
                    sageConnected,
                    companyName = profile.SageCompanyPath != null
                        ? System.IO.Path.GetFileNameWithoutExtension(profile.SageCompanyPath)
                        : null,
                    sageCompanyName = _sageService.CompanyName,
                    supportedActions = ConnectorCapabilities.SupportedActions,
                    supportedSync = ConnectorCapabilities.SupportedSync
                }, profile.CloudCompanyId);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Log.Warning("Heartbeat for {CompanyId} rejected: {StatusCode} {Body}",
                        profile.CloudCompanyId, response.StatusCode, body);
                }
                else
                {
                    Log.Debug("Heartbeat sent for {CompanyId} (sageConnected={SageConnected})",
                        profile.CloudCompanyId, sageConnected);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat failed for {CompanyId} - will retry on next interval", profile.CloudCompanyId);
            }
        }

        public void Stop()
        {
            _timer?.Dispose();
            _sendGate.Dispose();
        }
    }
}
