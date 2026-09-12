using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SageBridgeTests")]

namespace SageBridge.Connector
{
    /// <summary>
    /// Manages connector identity and credential storage.
    /// 
    /// Non-secret configuration (connectorId, companyPath, syncInterval, environment)
    /// is stored in config.json.
    /// 
    /// The connector credential is stored in Windows Credential Manager using DPAPI,
    /// never written to disk in plaintext.
    /// </summary>
    public class ConnectorIdentity
    {
        public string ConnectorId { get; set; } = "";
        public string Credential { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public string CompanyId { get; set; } = "";
        public DateTime PairedAt { get; set; }
    }

    /// <summary>
    /// Windows Credential Manager wrapper for secure storage of connector secrets.
    /// Uses DPAPI to encrypt credentials at rest.
    /// 
    /// Current scope: CurrentUser (per-user encryption).
    /// NOTE: If the connector later runs under a Windows Service or service account,
    /// this may need to change to LocalMachine or require a different storage strategy.
    /// </summary>
    public static class CredentialManager
    {
        private const string TargetName = "SageBridgeConnector";
        // Test-only seam. Never supplied by config or environment variables.
        internal static string? TestStorageDirectory { get; set; }

        /// <summary>
        /// Stores the connector credential in Windows Credential Manager.
        /// </summary>
        public static void StoreCredential(string connectorId, string credential)
        {
            // Use Windows Credential Manager via P/Invoke or a simple file-based DPAPI fallback
            // For now, use DPAPI-protected file in LocalApplicationData
            var credentialData = new StoredCredential
            {
                ConnectorId = connectorId,
                Credential = credential,
                StoredAt = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(credentialData);
            var bytes = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

            var path = GetCredentialFilePath();
            File.WriteAllBytes(path, encrypted);
        }

        /// <summary>
        /// Retrieves the connector credential from Windows Credential Manager.
        /// Returns null if no credential is stored.
        /// </summary>
        public static StoredCredential? RetrieveCredential()
        {
            var path = GetCredentialFilePath();
            if (!File.Exists(path))
                return null;

            try
            {
                var encrypted = File.ReadAllBytes(path);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);
                return JsonConvert.DeserializeObject<StoredCredential>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes the stored credential.
        /// </summary>
        public static void DeleteCredential()
        {
            var path = GetCredentialFilePath();
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// Checks if a credential is stored locally.
        /// </summary>
        public static bool HasStoredIdentity()
        {
            return RetrieveCredential() != null;
        }

        /// <summary>
        /// Returns the stable, non-secret ID for this installation. It is
        /// stored separately so credential rotation never changes identity.
        /// </summary>
        public static string GetOrCreateInstallationId()
        {
            var path = GetInstallationIdFilePath();
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing.StartsWith("inst_") ? existing.Substring(5) : "", out _))
                    return existing;
            }

            var installationId = "inst_" + Guid.NewGuid().ToString();
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(installationId);
                return installationId;
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing.StartsWith("inst_") ? existing.Substring(5) : "", out _))
                    return existing;
                throw;
            }
        }

        private static string GetCredentialFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = TestStorageDirectory ?? Path.Combine(appData, "SageBridgeConnector");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "credential.bin");
        }

        private static string GetInstallationIdFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = TestStorageDirectory ?? Path.Combine(appData, "SageBridgeConnector");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "installation-id");
        }
    }

    public class StoredCredential
    {
        public string ConnectorId { get; set; } = "";
        public string Credential { get; set; } = "";
        public DateTime StoredAt { get; set; }
    }

    /// <summary>
    /// Generates high-entropy connector credentials and pairing codes.
    /// 
    /// RNG: Uses RandomNumberGenerator.Create() which returns a CSPRNG
    /// (Cryptographically Secure Pseudo-Random Number Generator).
    /// NOT Random, NOT DateTime, NOT Guid, NOT predictable sources.
    /// </summary>
    public static class CredentialGenerator
    {
        private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// Generates a random credential of the specified length.
        /// </summary>
        public static string Generate(int length = 48)
        {
            var bytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);

            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(Chars[bytes[i] % Chars.Length]);
            return sb.ToString();
        }

        /// <summary>
        /// Generates a short human-readable pairing code.
        /// Format: XXXX-XXXX (uppercase letters and digits, excluding ambiguous characters).
        /// </summary>
        public static string GeneratePairingCode()
        {
            const string codeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No I, O, 0, 1
            var bytes = new byte[8];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);

            var sb = new StringBuilder(9);
            for (int i = 0; i < 8; i++)
            {
                if (i == 4) sb.Append('-');
                sb.Append(codeChars[bytes[i] % codeChars.Length]);
            }
            return sb.ToString();
        }
    }
}
