using System;
using System.IO;
using Newtonsoft.Json;

namespace SageBridge.Connector
{
    public class ConnectorConfig
    {
        public int ApiPort { get; set; } = 5001;
        public string TenantId { get; set; } = "demo-tenant";
        public string CompanyId { get; set; } = "demo-company";
        public bool EnableCloudflare { get; set; } = false;
        public string CloudflareWorkerUrl { get; set; } = "https://api.sagebridge.workers.dev";
        public string ApiKey { get; set; } = "";
        public int SyncIntervalSeconds { get; set; } = 300; // 5 minutes
        public string SageCompanyPath { get; set; } = "";
        public bool AutoConnect { get; set; } = true;

        public static ConnectorConfig Load()
        {
            var configPath = "config.json";
            
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<ConnectorConfig>(json) ?? new ConnectorConfig();
            }

            // Create default config
            var config = new ConnectorConfig();
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configPath, json);
            
            return config;
        }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText("config.json", json);
        }
    }
}
