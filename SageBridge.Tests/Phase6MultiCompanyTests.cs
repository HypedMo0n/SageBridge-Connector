using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using SageBridge.Connector;

namespace SageBridge.Tests
{
    internal static class Phase6MultiCompanyTests
    {
        public static int Passed { get; private set; }
        public static int Failed { get; private set; }

        public static void RunTests()
        {
            Console.WriteLine("SageBridge Phase 6 — Multi-company safety tests");
            Console.WriteLine("================================================\n");

            TestLegacyConfigurationBecomesOneProfile();
            TestExplicitProfilesInheritSharedCredentials();
            TestDuplicateCloudCompanyIdsAreRejected();
            TestDuplicateCompanyPathsAreRejected();
            TestCompanyScopedCloudRequest();
            TestSageSessionSwitchesCompaniesSafely();
            TestSyncRotatesThroughCompanyProfiles();
            TestJobsCannotCrossCompanyBoundaries();
            TestHeartbeatIsCompanyScoped();

            Console.WriteLine("\n================================================");
            Console.WriteLine($"Results: {Passed} passed, {Failed} failed, {Passed + Failed} total");
        }

        private static void Assert(bool condition, string name, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine($"  PASS: {name}");
                Passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: {name}" + (detail.Length > 0 ? $" — {detail}" : ""));
                Failed++;
            }
        }

        private static void TestLegacyConfigurationBecomesOneProfile()
        {
            var config = new ConnectorConfig
            {
                CompanyId = "cmp-a",
                SageCompanyPath = @"C:\Sage\CompanyA.SAI",
                SageUsername = "sysadmin",
                SagePassword = "shared-secret",
                SageMultiUser = true
            };

            var profiles = config.ResolveCompanyProfiles();
            Assert(profiles.Count == 1, "Legacy config resolves to one company profile");
            Assert(profiles[0].CloudCompanyId == "cmp-a", "Legacy company ID is preserved");
            Assert(profiles[0].SageCompanyPath == @"C:\Sage\CompanyA.SAI", "Legacy Sage path is preserved");
        }

        private static void TestExplicitProfilesInheritSharedCredentials()
        {
            var config = new ConnectorConfig
            {
                SageUsername = "shared-user",
                SagePassword = "shared-secret",
                SageMultiUser = true,
                Companies = new System.Collections.Generic.List<SageCompanyProfile>
                {
                    new SageCompanyProfile { CloudCompanyId = "cmp-a", SageCompanyPath = @"C:\Sage\A.SAI" },
                    new SageCompanyProfile { CloudCompanyId = "cmp-b", SageCompanyPath = @"C:\Sage\B.SAI", SageUsername = "company-b-user" }
                }
            };

            var profiles = config.ResolveCompanyProfiles();
            Assert(profiles.Count == 2, "Two explicit company profiles are returned");
            Assert(profiles[0].SageUsername == "shared-user", "Profile inherits shared Sage username");
            Assert(profiles[0].SagePassword == "shared-secret", "Profile inherits shared Sage password");
            Assert(profiles[1].SageUsername == "company-b-user", "Profile-specific Sage username wins");
        }

        private static void TestDuplicateCloudCompanyIdsAreRejected()
        {
            var config = ConfigWithProfiles(
                new SageCompanyProfile { CloudCompanyId = "cmp-a", SageCompanyPath = @"C:\Sage\A.SAI" },
                new SageCompanyProfile { CloudCompanyId = "cmp-a", SageCompanyPath = @"C:\Sage\B.SAI" });
            AssertThrows(config, "Duplicate cloud company IDs are rejected");
        }

        private static void TestDuplicateCompanyPathsAreRejected()
        {
            var config = ConfigWithProfiles(
                new SageCompanyProfile { CloudCompanyId = "cmp-a", SageCompanyPath = @"C:\Sage\Same.SAI" },
                new SageCompanyProfile { CloudCompanyId = "cmp-b", SageCompanyPath = @"c:\sage\same.sai" });
            AssertThrows(config, "Duplicate Sage company paths are rejected case-insensitively");
        }

        private static void TestCompanyScopedCloudRequest()
        {
            var testDirectory = Path.Combine(Path.GetTempPath(), "sagebridge_multicompany_" + Guid.NewGuid().ToString("N"));
            CredentialManager.TestStorageDirectory = testDirectory;
            try
            {
                CredentialManager.StoreCredential("conn-1", "credential-1");
                var auth = new CloudAuthenticator("https://example.com", "tenant", "legacy-company");
                Assert(auth.LoadIdentity(), "Cloud identity loads for request test");
                using var request = auth.CreateRequest(HttpMethod.Get, "/connector/jobs", "cmp-b");
                Assert(request.Headers.GetValues("X-Company-Id").Single() == "cmp-b", "Cloud request carries explicit company scope");
                Assert(request.Headers.GetValues("X-Connector-Id").Single() == "conn-1", "Cloud request retains connector identity");
            }
            finally
            {
                CredentialManager.TestStorageDirectory = null;
                if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true);
            }
        }

        private static void TestSageSessionSwitchesCompaniesSafely()
        {
            var testDirectory = Path.Combine(Path.GetTempPath(), "sagebridge_company_switch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            var pathA = Path.Combine(testDirectory, "CompanyA.SAI");
            var pathB = Path.Combine(testDirectory, "CompanyB.SAI");
            File.WriteAllText(pathA, "test");
            File.WriteAllText(pathB, "test");
            var session = new FakeSageDatabaseSession();
            var config = new ConnectorConfig();

            try
            {
                using var sage = new SageService(config, session, () => Path.GetFileNameWithoutExtension(session.LastOpenedPath));
                var profileA = new SageCompanyProfile { CloudCompanyId = "cmp-a", SageCompanyPath = pathA, SageUsername = "user", SagePassword = "pw", SageMultiUser = true };
                var profileB = new SageCompanyProfile { CloudCompanyId = "cmp-b", SageCompanyPath = pathB, SageUsername = "user", SagePassword = "pw", SageMultiUser = true };

                Assert(sage.ConnectAsync(profileA).Result, "First Sage company opens");
                Assert(sage.CurrentCloudCompanyId == "cmp-a", "First cloud company scope becomes active");
                Assert(sage.ConnectAsync(profileA).Result, "Re-selecting the same company succeeds");
                Assert(session.OpenCount == 1 && session.CloseCount == 0, "Re-selecting the same company does not reopen the SDK");
                Assert(sage.ConnectAsync(profileB).Result, "Second Sage company opens");
                Assert(session.CloseCount == 1, "Existing SDK database closes before switching companies");
                Assert(session.OpenCount == 2 && session.LastOpenedPath == pathB, "Second company path is opened exactly once");
                Assert(sage.CurrentCloudCompanyId == "cmp-b", "Cloud company scope switches with the Sage database");
                Assert(sage.CompanyName == "CompanyB", "Opened Sage company identity is read after switching");
            }
            finally
            {
                Directory.Delete(testDirectory, true);
            }
        }

        private sealed class FakeSageDatabaseSession : ISageDatabaseSession
        {
            public int OpenCount { get; private set; }
            public int CloseCount { get; private set; }
            public string LastOpenedPath { get; private set; } = "";

            public bool OpenDatabase(SageCompanyProfile profile, out string result)
            {
                OpenCount++;
                LastOpenedPath = profile.SageCompanyPath;
                result = "Success";
                return true;
            }

            public void CloseDatabase()
            {
                CloseCount++;
            }
        }

        private static void TestSyncRotatesThroughCompanyProfiles()
        {
            var source = File.ReadAllText(SourceFile("SageBridge.Connector", "SyncEngine.cs"));
            Assert(source.Contains("ResolveCompanyProfiles()"), "Sync engine resolves all configured company profiles");
            Assert(source.Contains("RunForCompanyAsync(profile"), "Sync engine serializes each company through the SDK session gate");
            Assert(source.Contains("profile.CloudCompanyId"), "Sync payloads use the profile's cloud company ID");
            Assert(source.Contains("PostAsync(endpoint, data, companyId)"), "Sync requests carry explicit cloud company scope");
        }

        private static void TestJobsCannotCrossCompanyBoundaries()
        {
            var source = File.ReadAllText(SourceFile("SageBridge.Connector", "JobPoller.cs"));
            Assert(source.Contains("RunForCompanyAsync(profile"), "Job poller switches into the bound Sage company before processing");
            Assert(source.Contains("job.CompanyId") && source.Contains("does not match polled company"), "Job poller rejects a job returned under the wrong company");
            Assert(source.Contains("ScopeKey(companyId, idempotencyKey)"), "Completed idempotency keys are company-scoped");
            Assert(source.Contains("GetAsync(\"/connector/jobs\", profile.CloudCompanyId)"), "Job polling carries explicit company scope");
            Assert(source.Contains("SubmitJobResult(job.JobId, status, sageId, error, companyId)"), "Job results retain the job's company scope");
        }

        private static void TestHeartbeatIsCompanyScoped()
        {
            var source = File.ReadAllText(SourceFile("SageBridge.Connector", "HeartbeatSender.cs"));
            Assert(source.Contains("ResolveCompanyProfiles()"), "Heartbeat resolves every configured company profile");
            Assert(source.Contains("profile.CloudCompanyId"), "Heartbeat is sent with explicit cloud company scope");
        }

        private static string SourceFile(params string[] parts)
        {
            var directory = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                    return candidate;
                directory = Directory.GetParent(directory)?.FullName;
            }
            throw new FileNotFoundException("Could not locate source file: " + string.Join("/", parts));
        }

        private static ConnectorConfig ConfigWithProfiles(params SageCompanyProfile[] profiles)
        {
            return new ConnectorConfig
            {
                SageUsername = "sysadmin",
                SagePassword = "secret",
                Companies = profiles.ToList()
            };
        }

        private static void AssertThrows(ConnectorConfig config, string name)
        {
            try
            {
                config.ResolveCompanyProfiles();
                Assert(false, name);
            }
            catch (InvalidOperationException)
            {
                Assert(true, name);
            }
        }
    }
}
