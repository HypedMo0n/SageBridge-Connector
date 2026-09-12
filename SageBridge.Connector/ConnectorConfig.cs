using System.IO;
using Newtonsoft.Json;

namespace SageBridge.Connector
{
    /// <summary>
    /// Non-secret connector configuration.
    /// Secrets (credential) are stored separately in Windows Credential Manager.
    /// </summary>
    public class ConnectorConfig
    {
        // Non-secret configuration
        public int ApiPort { get; set; } = 5001;
        public string TenantId { get; set; } = "demo-tenant";
        public string CompanyId { get; set; } = "demo-company";
        public bool EnableCloudflare { get; set; }
        public string CloudflareWorkerUrl { get; set; } = "https://sagebridge-api.cheikhmounirk.workers.dev";
        public int SyncIntervalSeconds { get; set; } = 300;
        public string SageCompanyPath { get; set; } = "";
        public string SageUsername { get; set; } = "sysadmin";
        public string SagePassword { get; set; } = "";
        public bool SageMultiUser { get; set; } = true;
        public bool AutoConnect { get; set; } = true;
        
        // Legacy API key - no longer used for connector authentication
        // Kept for backward compatibility during migration
        [JsonIgnore]
        public string ApiKey { get; set; } = "";

        public static ConnectorConfig Load()
        {
            const string configPath = "config.json";
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<ConnectorConfig>(json) ?? new ConnectorConfig();
            }

            var config = new ConnectorConfig();
            config.Save();
            return config;
        }

        public void Save()
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText("config.json", json);
        }
    }
}
