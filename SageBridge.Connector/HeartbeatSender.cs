using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Sends a periodic, machine-credential-authenticated heartbeat to the
    /// cloud's existing POST /connector/heartbeat endpoint (already
    /// implemented and tested server-side - see sagebridge-api
    /// src/handlers/phase1.ts heartbeat() - no cloud change needed here).
    ///
    /// This is the only thing that updates companies.last_seen_at, which the
    /// UI's online/offline indicator (companyDto.online) and
    /// startProvisioning's connector-online check both read from. Neither
    /// /connector/jobs polling nor /sync/* calls touch that column (they
    /// only touch connectors.last_sync_at / companies.last_sync_at), which
    /// is why a connector that was actively syncing could still show
    /// Offline: nothing was ever calling this endpoint.
    ///
    /// Interval is well below the server's 120-second staleness threshold
    /// (see the heartbeat response's staleAfterSeconds and
    /// connectorIsStale's default) so a single missed beat does not flip the
    /// UI to offline. Uses CloudAuthenticator exactly like JobPoller and
    /// SyncEngine, so the connectorId/credential are the same per-connector
    /// machine identity established at pairing - no human or Firebase
    /// identity is involved.
    /// </summary>
    public class HeartbeatSender
    {
        private const int IntervalSeconds = 30;


        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private readonly CloudAuthenticator _auth;
        private Timer? _timer;

        public HeartbeatSender(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
            _auth = new CloudAuthenticator(config.CloudflareWorkerUrl, config.TenantId, config.CompanyId);
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

        /// <summary>
        /// A heartbeat failure must never take down sync or job processing,
        /// which run on their own independent timers/tasks - this method
        /// never lets an exception escape, and a Timer callback that threw
        /// would otherwise terminate the process.
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            try
            {
                var response = await _auth.PostAsync("/connector/heartbeat", new
                {
                    connectorVersion = ConnectorVersion.Current,
                    sageVersion = (string?)null,
                    sageConnected = _sageService.IsConnected
                });

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    // A revoked connector's credential is rejected with 401 by
                    // requireConnector before this handler even runs - that is
                    // expected here (not an error to retry past), so it is
                    // logged at the same level as any other rejection rather
                    // than treated specially.
                    Log.Warning("Heartbeat rejected: {StatusCode} {Body}", response.StatusCode, body);
                }
                else
                {
                    Log.Debug("Heartbeat sent (sageConnected={SageConnected})", _sageService.IsConnected);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat failed - will retry on next interval");
            }
        }

        public void Stop()
        {
            _timer?.Dispose();
        }
    }
}
