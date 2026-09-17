using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Handles authenticated communication with the cloud.
    /// Uses per-connector credentials instead of a shared API key.
    /// 
    /// Cloud credential contract:
    /// - The cloud stores a ONE-WAY HASH of the credential (bcrypt, Argon2, or PBKDF2).
    /// - The cloud NEVER stores the plaintext credential.
    /// - Authentication: connector sends credential over HTTPS, cloud hashes and compares.
    /// - Revocation: cloud deletes the hash, credential becomes invalid.
    /// 
    /// This matches standard password/API secret verification patterns.
    /// </summary>
    public class CloudAuthenticator
    {
        private readonly string _workerUrl;
        private readonly string _tenantId;
        private readonly string _companyId;
        private string _connectorId = "";
        private string _credential = "";

        public CloudAuthenticator(string workerUrl, string tenantId, string companyId)
        {
            _workerUrl = workerUrl;
            _tenantId = tenantId;
            _companyId = companyId;
        }

        /// <summary>
        /// Loads the connector identity from secure storage.
        /// Returns true if identity was loaded successfully.
        /// </summary>
        public bool LoadIdentity()
        {
            var identity = PairingClient.GetStoredIdentity();
            if (identity == null)
                return false;

            _connectorId = identity.ConnectorId;
            _credential = identity.Credential;
            return true;
        }

        /// <summary>
        /// Sends an authenticated POST request to the cloud.
        /// </summary>
        public async Task<HttpResponseMessage> PostAsync(string endpoint, object data, string? companyId = null)
        {
            var json = JsonConvert.SerializeObject(data);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await PostAsync(endpoint, content, companyId);
        }

        /// <summary>
        /// Sends an authenticated POST request with pre-serialized content.
        /// </summary>
        public async Task<HttpResponseMessage> PostAsync(string endpoint, HttpContent content, string? companyId = null)
        {
            var request = CreateRequest(HttpMethod.Post, endpoint, companyId);
            request.Content = content;
            return await SendAsync(request);
        }

        /// <summary>
        /// Sends an authenticated GET request to the cloud.
        /// </summary>
        public async Task<HttpResponseMessage> GetAsync(string endpoint, string? companyId = null)
        {
            var request = CreateRequest(HttpMethod.Get, endpoint, companyId);
            return await SendAsync(request);
        }

        internal HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, string? companyId = null)
        {
            var scope = string.IsNullOrWhiteSpace(companyId) ? _companyId : companyId.Trim();
            if (string.IsNullOrWhiteSpace(scope))
                throw new InvalidOperationException("A cloud company scope is required.");

            var request = new HttpRequestMessage(method, $"{_workerUrl}{endpoint}");
            AddAuthentication(request);
            request.Headers.Add("X-Company-Id", scope);
            return request;
        }

        private void AddAuthentication(HttpRequestMessage request)
        {
            // Use the connector credential for authentication
            // The credential is a high-entropy token issued during pairing
            request.Headers.Add("X-Connector-Id", _connectorId);
            request.Headers.Add("X-Connector-Credential", _credential);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await client.SendAsync(request);

            // A 401 here means the cloud rejected this machine's credential
            // outright (revoked/disconnected, or never valid) - distinct from
            // a transient network or server error. Every caller
            // (HeartbeatSender, JobPoller, SyncEngine) already logs the raw
            // status on failure; this adds one unambiguous line so "credential
            // revoked, needs re-pairing" is never confused with a transient
            // fault in those logs. Never throws and never changes what is
            // returned - callers keep handling the response exactly as before.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Warning("This connector's credential was rejected by the cloud (not paired, or pairing was revoked). Re-run pairing with --pair to reconnect.");
            }

            return response;
        }
    }
}
