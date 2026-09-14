using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SageBridge.Connector
{
    public class SageCompanyProfile
    {
        public string CloudCompanyId { get; set; } = "";
        public string SageCompanyPath { get; set; } = "";
        public string SageUsername { get; set; } = "";
        public string SagePassword { get; set; } = "";
        public bool? SageMultiUser { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Non-secret connector configuration. Connector credentials are DPAPI-protected.
    /// Sage credentials retain their existing config.json behavior for backward compatibility.
    /// </summary>
    public class ConnectorConfig
    {
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
        public List<SageCompanyProfile> Companies { get; set; } = new List<SageCompanyProfile>();

        [JsonIgnore]
        public string ApiKey { get; set; } = "";

        public IReadOnlyList<SageCompanyProfile> ResolveCompanyProfiles()
        {
            var configured = (Companies ?? new List<SageCompanyProfile>())
                .Where(profile => profile != null && profile.Enabled)
                .ToList();

            if (configured.Count == 0 && (!string.IsNullOrWhiteSpace(CompanyId) || !string.IsNullOrWhiteSpace(SageCompanyPath)))
            {
                configured.Add(new SageCompanyProfile
                {
                    CloudCompanyId = CompanyId,
                    SageCompanyPath = SageCompanyPath,
                    SageUsername = SageUsername,
                    SagePassword = SagePassword,
                    SageMultiUser = SageMultiUser
                });
            }

            var resolved = configured.Select(profile => new SageCompanyProfile
            {
                CloudCompanyId = (profile.CloudCompanyId ?? "").Trim(),
                SageCompanyPath = (profile.SageCompanyPath ?? "").Trim(),
                SageUsername = string.IsNullOrWhiteSpace(profile.SageUsername) ? SageUsername : profile.SageUsername.Trim(),
                SagePassword = string.IsNullOrEmpty(profile.SagePassword) ? SagePassword : profile.SagePassword,
                SageMultiUser = profile.SageMultiUser ?? SageMultiUser,
                Enabled = true
            }).ToList();

            foreach (var profile in resolved)
            {
                if (string.IsNullOrWhiteSpace(profile.CloudCompanyId))
                    throw new InvalidOperationException("Every Sage company profile requires a CloudCompanyId.");
                if (string.IsNullOrWhiteSpace(profile.SageCompanyPath))
                    throw new InvalidOperationException($"Company '{profile.CloudCompanyId}' requires a SageCompanyPath.");
                if (string.IsNullOrWhiteSpace(profile.SageUsername))
                    throw new InvalidOperationException($"Company '{profile.CloudCompanyId}' requires a Sage username.");
            }

            if (resolved.GroupBy(profile => profile.CloudCompanyId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("CloudCompanyId values must be unique.");
            if (resolved.GroupBy(profile => Path.GetFullPath(profile.SageCompanyPath), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("SageCompanyPath values must be unique.");

            return resolved;
        }

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
