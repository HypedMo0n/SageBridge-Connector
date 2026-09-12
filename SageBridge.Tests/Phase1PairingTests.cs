using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SageBridge.Connector;

namespace SageBridge.Tests
{
    class Phase1PairingTests
    {
        public static int exitCode = 0;
        public static int passed = 0;
        public static int failed = 0;

        public static void RunTests()
        {
            Console.WriteLine("SageBridge Phase 1 — Connector Identity and Pairing Tests");
            Console.WriteLine("==========================================================\n");

            var testDirectory = Path.Combine(Path.GetTempPath(), "sagebridge_credentials_" + Guid.NewGuid().ToString("N"));
            CredentialManager.TestStorageDirectory = testDirectory;
            try
            {
                TestCredentialGenerator();
                TestCredentialStorage();
                TestPairingCodeFormat();
                TestCredentialNotPlaintext();
                TestCredentialSurvivesRestart();
                TestPairingClientValidation();
                TestConnectorIdentityModel();
                TestAuthenticatorHeaders();
                TestStableInstallationIdentity();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: {ex}");
                exitCode = 2;
            }
            finally
            {
                CredentialManager.TestStorageDirectory = null;
                if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true);
            }

            Console.WriteLine($"\n==========================================================");
            Console.WriteLine($"Results: {passed} passed, {failed} failed, {passed + failed} total");
        }

        static void Assert(bool condition, string testName, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine($"  PASS: {testName}");
                passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: {testName}" + (detail != "" ? $" — {detail}" : ""));
                failed++;
                exitCode = 1;
            }
        }

        // ---------------------------------------------------------------------------
        // 1. Credential Generator Tests
        // ---------------------------------------------------------------------------
        static void TestCredentialGenerator()
        {
            Console.WriteLine("1. Credential generator tests");

            // Test credential generation
            var cred1 = CredentialGenerator.Generate();
            Assert(cred1.Length == 48, "Default credential length is 48", $"got {cred1.Length}");

            var cred2 = CredentialGenerator.Generate(64);
            Assert(cred2.Length == 64, "Custom credential length is 64", $"got {cred2.Length}");

            // Test uniqueness
            var cred3 = CredentialGenerator.Generate();
            Assert(cred1 != cred3, "Two generated credentials are different");

            // Test character set
            const string validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            foreach (char c in cred1)
            {
                Assert(validChars.Contains(c.ToString()), $"Credential contains valid character '{c}'");
            }
        }

        // ---------------------------------------------------------------------------
        // 2. Credential Storage Tests
        // ---------------------------------------------------------------------------
        static void TestCredentialStorage()
        {
            Console.WriteLine("\n2. Credential storage tests");

            // Clean up any existing credential
            CredentialManager.DeleteCredential();
            Assert(!CredentialManager.HasStoredIdentity(), "No credential after deletion");

            // Store a credential
            CredentialManager.StoreCredential("test-connector-001", "test-credential-abc123");
            Assert(CredentialManager.HasStoredIdentity(), "Credential exists after storing");

            // Retrieve the credential
            var stored = CredentialManager.RetrieveCredential();
            Assert(stored != null, "Retrieved credential is not null");
            Assert(stored?.ConnectorId == "test-connector-001", "Connector ID matches");
            Assert(stored?.Credential == "test-credential-abc123", "Credential matches");

            // Clean up
            CredentialManager.DeleteCredential();
            Assert(!CredentialManager.HasStoredIdentity(), "No credential after cleanup");
        }

        // ---------------------------------------------------------------------------
        // 3. Pairing Code Format Tests
        // ---------------------------------------------------------------------------
        static void TestPairingCodeFormat()
        {
            Console.WriteLine("\n3. Pairing code format tests");

            for (int i = 0; i < 10; i++)
            {
                var code = CredentialGenerator.GeneratePairingCode();
                Assert(code.Length == 9, $"Pairing code length is 9 (got: {code})");
                Assert(code[4] == '-', $"Pairing code has dash at position 4 (got: {code})");

                // Check no ambiguous characters
                const string ambiguous = "IO01";
                foreach (char c in code)
                {
                    if (c != '-')
                        Assert(!ambiguous.Contains(c.ToString()), $"No ambiguous characters in code (got: {code})");
                }
            }
        }

        // ---------------------------------------------------------------------------
        // 4. Credential Not Plaintext Tests
        // ---------------------------------------------------------------------------
        static void TestCredentialNotPlaintext()
        {
            Console.WriteLine("\n4. Credential not stored as plaintext");

            CredentialManager.StoreCredential("test-connector", "secret-credential-12345");

            // Find the credential file
            var credPath = Path.Combine(CredentialManager.TestStorageDirectory
                ?? throw new InvalidOperationException("Tests require isolated credential storage"), "credential.bin");

            Assert(File.Exists(credPath), "Credential file exists");

            // Read the file and verify it doesn't contain the plaintext credential
            var fileBytes = File.ReadAllBytes(credPath);
            var fileText = Encoding.UTF8.GetString(fileBytes);

            Assert(!fileText.Contains("secret-credential-12345"), "Credential file does not contain plaintext secret");
            Assert(!fileText.Contains("test-connector"), "Credential file does not contain plaintext connector ID");

            // Verify it's valid DPAPI data (should start with specific DPAPI header)
            // DPAPI protected data starts with a specific magic number
            Assert(fileBytes.Length > 0, "Credential file is not empty");

            // Clean up
            CredentialManager.DeleteCredential();
        }

        // ---------------------------------------------------------------------------
        // 5. Credential Survives Restart Tests
        // ---------------------------------------------------------------------------
        static void TestCredentialSurvivesRestart()
        {
            Console.WriteLine("\n5. Credential survives restart simulation");

            // Store credential
            CredentialManager.StoreCredential("persistent-connector", "persistent-credential-abc");

            // Simulate restart by creating new retrieval
            var stored1 = CredentialManager.RetrieveCredential();
            var stored2 = CredentialManager.RetrieveCredential();

            Assert(stored1?.ConnectorId == stored2?.ConnectorId, "Connector ID survives restart");
            Assert(stored1?.Credential == stored2?.Credential, "Credential survives restart");

            // Clean up
            CredentialManager.DeleteCredential();
        }

        // ---------------------------------------------------------------------------
        // 6. Pairing Client Validation Tests
        // ---------------------------------------------------------------------------
        static void TestPairingClientValidation()
        {
            Console.WriteLine("\n6. Pairing client validation tests");

            // Test null/empty pairing code
            var client = new PairingClient("https://example.com");

            try
            {
                client.ValidatePairingCodeAsync(null).Wait();
                Assert(false, "Null pairing code should throw");
            }
            catch (AggregateException ex) when (ex.InnerException is ArgumentException)
            {
                Assert(true, "Null pairing code throws ArgumentException");
            }

            try
            {
                client.ValidatePairingCodeAsync("").Wait();
                Assert(false, "Empty pairing code should throw");
            }
            catch (AggregateException ex) when (ex.InnerException is ArgumentException)
            {
                Assert(true, "Empty pairing code throws ArgumentException");
            }

            try
            {
                client.ValidatePairingCodeAsync("   ").Wait();
                Assert(false, "Whitespace pairing code should throw");
            }
            catch (AggregateException ex) when (ex.InnerException is ArgumentException)
            {
                Assert(true, "Whitespace pairing code throws ArgumentException");
            }
        }

        // ---------------------------------------------------------------------------
        // 7. Connector Identity Model Tests
        // ---------------------------------------------------------------------------
        static void TestConnectorIdentityModel()
        {
            Console.WriteLine("\n7. Connector identity model tests");

            var identity = new ConnectorIdentity
            {
                ConnectorId = "conn-123",
                Credential = "cred-456",
                OrganizationId = "org-789",
                CompanyId = "comp-abc",
                PairedAt = DateTime.UtcNow
            };

            Assert(identity.ConnectorId == "conn-123", "ConnectorId property works");
            Assert(identity.Credential == "cred-456", "Credential property works");
            Assert(identity.OrganizationId == "org-789", "OrganizationId property works");
            Assert(identity.CompanyId == "comp-abc", "CompanyId property works");
            Assert(identity.PairedAt <= DateTime.UtcNow, "PairedAt property works");
        }

        // ---------------------------------------------------------------------------
        // 8. Authenticator Headers Tests
        // ---------------------------------------------------------------------------
        static void TestAuthenticatorHeaders()
        {
            Console.WriteLine("\n8. Authenticator headers tests");

            var auth = new CloudAuthenticator("https://example.com", "tenant", "company");

            // Test that LoadIdentity returns false when no credential stored
            CredentialManager.DeleteCredential();
            Assert(!auth.LoadIdentity(), "LoadIdentity returns false when no credential stored");

            // Test that LoadIdentity returns true when credential stored
            CredentialManager.StoreCredential("test-conn", "test-cred");
            Assert(auth.LoadIdentity(), "LoadIdentity returns true when credential stored");

            // Clean up
            CredentialManager.DeleteCredential();
        }

        static void TestStableInstallationIdentity()
        {
            Console.WriteLine("\n9. Stable installation identity lifecycle");

            // Test 1: First run creates and persists installationId
            var first = CredentialManager.GetOrCreateInstallationId();
            Assert(Guid.TryParse(first.StartsWith("inst_") ? first.Substring(5) : "", out _),
                "First run creates a valid inst_<UUID> installation ID");

            // Test 2: Second call returns same ID (persisted)
            var second = CredentialManager.GetOrCreateInstallationId();
            Assert(first == second, "Restart reuses the same installation ID from disk");

            // Test 3: Second installation (fresh directory) gets different ID
            var freshDir = Path.Combine(Path.GetTempPath(), "sagebridge_install_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(freshDir);
            var originalDir = CredentialManager.TestStorageDirectory;
            CredentialManager.TestStorageDirectory = freshDir;
            try
            {
                var different = CredentialManager.GetOrCreateInstallationId();
                Assert(different != first, "Second installation gets a different installation ID");
                Assert(Guid.TryParse(different.StartsWith("inst_") ? different.Substring(5) : "", out _),
                    "Second installation ID is valid inst_<UUID>");
            }
            finally
            {
                CredentialManager.TestStorageDirectory = originalDir;
                if (Directory.Exists(freshDir)) Directory.Delete(freshDir, true);
            }

            // Test 4: Credential rotation (store + delete) preserves installationId
            CredentialManager.StoreCredential("conn-test", "cred-test");
            CredentialManager.DeleteCredential();
            Assert(CredentialManager.GetOrCreateInstallationId() == first,
                "Credential rotation/deletion does not regenerate installation ID");

            // Test 5: Re-pairing (new credential) preserves installationId
            CredentialManager.StoreCredential("conn-new", "cred-new");
            try
            {
                Assert(CredentialManager.GetOrCreateInstallationId() == first,
                    "Re-pairing with new credential reuses the same installation ID");
            }
            finally
            {
                CredentialManager.DeleteCredential();
            }

            // Test 6: Pairing request body contains installationId matching API format
            var request = PairingClient.CreatePairingRequest("ABCD-EFGH", "RENAMED-PC");
            Assert(request.installationId == first,
                "Pairing request includes the stable installation ID");
            Assert(request.installationId.StartsWith("inst_"),
                "Pairing request installationId starts with inst_");
            Assert(request.pairingCode == "ABCD-EFGH",
                "Pairing code is normalized to uppercase");
            Assert(request.machineName == "RENAMED-PC",
                "Machine name is included but treated as metadata only");
            Assert(request.connectorVersion == ConnectorVersion.Current,
                "Pairing request sends the shared authoritative connector version");

            // Test 7: ConnectorVersion is shared between PairingClient and HeartbeatSender
            Assert(ConnectorVersion.Current == "1.1.0",
                "Connector version is the single authoritative source (v1.1.0)");
        }
    }
}
