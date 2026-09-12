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
    /// Handles the connector pairing flow with the cloud.
    /// 
    /// Pairing flow:
    /// 1. Connector calls ValidatePairingCode with a user-entered code
    /// 2. Cloud validates the code and returns a connector_id + credential
    /// 3. Connector stores the credential securely via DPAPI
    /// 4. Connector uses the credential for all future authentication
    /// 
    /// Security note: The connector does NOT supply tenant/company IDs.
    /// These are derived server-side from the pairing code to prevent
    /// connector-side tampering with organization/company binding.
    /// </summary>
    public class PairingClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _workerUrl;

        public PairingClient(string workerUrl)
        {
            _workerUrl = workerUrl;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Validates a pairing code with the cloud.
        /// The connector submits only the pairing code and version info.
        /// Organization and company are assigned server-side.
        /// Returns pairing result on success, throws on failure.
        /// </summary>
        public async Task<PairingResult> ValidatePairingCodeAsync(string pairingCode, string? machineName = null)
        {
            if (string.IsNullOrWhiteSpace(pairingCode))
                throw new ArgumentException("Pairing code is required.", nameof(pairingCode));

            var request = CreatePairingRequest(pairingCode, machineName);

            var json = JsonConvert.SerializeObject(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            Log.Information("Validating pairing code...");

            var response = await _httpClient.PostAsync($"{_workerUrl}/connector/pairing/validate", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("Pairing failed: {Status} - {Body}", response.StatusCode, responseBody);
                throw new PairingException($"Pairing failed: {response.StatusCode}");
            }

            var result = JObject.Parse(responseBody);
            return new PairingResult
            {
                ConnectorId = result["connectorId"]?.ToString() ?? "",
                Credential = result["credential"]?.ToString() ?? "",
                OrganizationId = result["organizationId"]?.ToString() ?? "",
                CompanyId = result["companyId"]?.ToString() ?? "",
                PairedAt = result["pairedAt"]?.ToObject<DateTime>() ?? DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets the stored connector identity, or null if not paired.
        /// </summary>
        public static ConnectorIdentity? GetStoredIdentity()
        {
            var stored = CredentialManager.RetrieveCredential();
            if (stored == null)
                return null;

            return new ConnectorIdentity
            {
                ConnectorId = stored.ConnectorId,
                Credential = stored.Credential,
                PairedAt = stored.StoredAt
            };
        }

        internal static PairingRequest CreatePairingRequest(string pairingCode, string? machineName = null)
        {
            return new PairingRequest
            {
                pairingCode = pairingCode.Trim().ToUpperInvariant(),
                connectorVersion = "1.1.0",
                machineName = machineName ?? Environment.MachineName,
                installationId = CredentialManager.GetOrCreateInstallationId()
            };
        }
    }

    internal class PairingRequest
    {
        public string pairingCode { get; set; } = "";
        public string connectorVersion { get; set; } = "";
        public string machineName { get; set; } = "";
        public string installationId { get; set; } = "";
    }

    public class PairingResult
    {
        public string ConnectorId { get; set; } = "";
        public string Credential { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public string CompanyId { get; set; } = "";
        public DateTime PairedAt { get; set; }
    }

    public class PairingException : Exception
    {
        public PairingException(string message) : base(message) { }
        public PairingException(string message, Exception inner) : base(message, inner) { }
    }
}
