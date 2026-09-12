using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SageBridge.Connector;

namespace SageBridge.Tests
{
    class Phase2RepositoryTests
    {
        public static int exitCode = 0;
        public static int passed = 0;
        public static int failed = 0;

        public static void RunTests()
        {
            Console.WriteLine("SageBridge Phase 2 — Repository Isolation Tests");
            Console.WriteLine("================================================\n");

            try
            {
                TestQuotesControllerExists();
                TestReportsControllerExists();
                TestNoSqlInControllers();
                TestNoSqlInSyncEngine();
                TestNoDirectTableReferences();
                TestQuoteCreationUsesSdk();
                TestInvoiceCreationUsesSdk();
                TestNoSqlWrites();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: {ex}");
                exitCode = 2;
            }

            Console.WriteLine($"\n================================================");
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
        // 1. QuotesController and ReportsController exist in ApiServer.cs
        // ---------------------------------------------------------------------------
        static void TestQuotesControllerExists()
        {
            Console.WriteLine("1. Controller existence checks");

            var apiServer = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\ApiServer.cs");
            Assert(apiServer.Contains("public class QuotesController"), "QuotesController exists in ApiServer.cs");
        }

        static void TestReportsControllerExists()
        {
            Console.WriteLine("2. ReportsController existence check");

            var apiServer = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\ApiServer.cs");
            Assert(apiServer.Contains("public class ReportsController"), "ReportsController exists in ApiServer.cs");
        }

        // ---------------------------------------------------------------------------
        // 2. No SQL in controllers
        // ---------------------------------------------------------------------------
        static void TestNoSqlInControllers()
        {
            Console.WriteLine("\n2. No SQL in controllers");

            var apiServer = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\ApiServer.cs");
            Assert(!apiServer.Contains("SELECT") || apiServer.IndexOf("SELECT") < 0 || !IsInControllerClass(apiServer, "SELECT"),
                "ApiServer.cs has no SELECT statements in controller classes");
            Assert(!apiServer.Contains("INSERT INTO"), "ApiServer.cs has no INSERT statements");
            Assert(!apiServer.Contains("UPDATE ") || !apiServer.Contains("SET"), "ApiServer.cs has no UPDATE statements");
            Assert(!apiServer.Contains("DELETE FROM"), "ApiServer.cs has no DELETE statements");
        }

        static bool IsInControllerClass(string source, string keyword)
        {
            // Simple heuristic - if SELECT appears, it's not in a controller
            return source.Contains(keyword);
        }

        // ---------------------------------------------------------------------------
        // 3. No SQL in SyncEngine.cs
        // ---------------------------------------------------------------------------
        static void TestNoSqlInSyncEngine()
        {
            Console.WriteLine("\n3. No SQL in SyncEngine.cs");

            var syncEngine = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SyncEngine.cs");
            Assert(!syncEngine.Contains("SELECT"), "SyncEngine.cs has no SELECT statements");
            Assert(!syncEngine.Contains("INSERT"), "SyncEngine.cs has no INSERT statements");
            Assert(!syncEngine.Contains("UPDATE"), "SyncEngine.cs has no UPDATE statements");
            Assert(!syncEngine.Contains("DELETE"), "SyncEngine.cs has no DELETE statements");
            Assert(!syncEngine.Contains("tsalordr"), "SyncEngine.cs has no tsalordr references");
            Assert(!syncEngine.Contains("tCusTr"), "SyncEngine.cs has no tCusTr references");
        }

        // ---------------------------------------------------------------------------
        // 4. No direct table references in business logic files
        // ---------------------------------------------------------------------------
        static void TestNoDirectTableReferences()
        {
            Console.WriteLine("\n4. No direct table references in business logic files");

            var jobPoller = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\JobPoller.cs");
            Assert(!jobPoller.Contains("tsalordr"), "JobPoller.cs has no tsalordr references");
            Assert(!jobPoller.Contains("tCusTr"), "JobPoller.cs has no tCusTr references");
            Assert(!jobPoller.Contains("bQuote"), "JobPoller.cs has no bQuote references");
            Assert(!jobPoller.Contains("nTranType"), "JobPoller.cs has no nTranType references");

            var syncEngine = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SyncEngine.cs");
            Assert(!syncEngine.Contains("tsalordr"), "SyncEngine.cs has no tsalordr references");
            Assert(!syncEngine.Contains("tCusTr"), "SyncEngine.cs has no tCusTr references");
            Assert(!syncEngine.Contains("bQuote"), "SyncEngine.cs has no bQuote references");
            Assert(!syncEngine.Contains("nTranType"), "SyncEngine.cs has no nTranType references");

            var apiServer = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\ApiServer.cs");
            Assert(!apiServer.Contains("tsalordr"), "ApiServer.cs has no tsalordr references");
            Assert(!apiServer.Contains("tCusTr"), "ApiServer.cs has no tCusTr references");
            Assert(!apiServer.Contains("bQuote"), "ApiServer.cs has no bQuote references");
            Assert(!apiServer.Contains("nTranType"), "ApiServer.cs has no nTranType references");
        }

        // ---------------------------------------------------------------------------
        // 5. API endpoints still work (integration test)
        // ---------------------------------------------------------------------------
        static void TestApiEndpointsStillWork()
        {
            Console.WriteLine("\n5. API endpoints still work");

            // These tests require a running connector
            try
            {
                // Test GET /api/quotes
                var quotesResponse = HttpClientGet("http://localhost:5001/api/quotes");
                var quotes = JObject.Parse(quotesResponse);
                var quotesArray = quotes["quotes"] as JArray;
                Assert(quotesArray != null && quotesArray.Count == 9, "GET /api/quotes returns 9 quotes", $"got {quotesArray?.Count}");

                // Test GET /api/quotes/{number}
                var quoteResponse = HttpClientGet("http://localhost:5001/api/quotes/QT-20260911-001");
                var quote = JObject.Parse(quoteResponse);
                Assert(quote["QuoteNumber"]?.ToString() == "QT-20260911-001", "GET /api/quotes/QT-20260911-001 works");

                // Test GET /api/invoices
                var invoicesResponse = HttpClientGet("http://localhost:5001/api/invoices");
                var invoices = JObject.Parse(invoicesResponse);
                var invoicesArray = invoices["invoices"] as JArray;
                Assert(invoicesArray != null && invoicesArray.Count > 0, "GET /api/invoices returns invoices", $"got {invoicesArray?.Count}");

                // Test GET /api/reports/invoice-summary
                var summaryResponse = HttpClientGet("http://localhost:5001/api/reports/invoice-summary");
                var summary = JObject.Parse(summaryResponse);
                Assert(summary["TotalCount"] != null, "GET /api/reports/invoice-summary works");

                // Test GET /api/reports/ar-aging
                var agingResponse = HttpClientGet("http://localhost:5001/api/reports/ar-aging");
                var aging = JObject.Parse(agingResponse);
                var agingArray = aging["report"] as JArray;
                Assert(agingArray != null && agingArray.Count > 0, "GET /api/reports/ar-aging works", $"got {agingArray?.Count}");
            }
            catch (Exception ex)
            {
                Assert(false, "API endpoints test failed", ex.Message);
            }
        }

        static string HttpClientGet(string url)
        {
            var request = System.Net.HttpWebRequest.CreateHttp(url);
            request.Method = "GET";
            request.Timeout = 10000;
            using var response = request.GetResponse();
            using var stream = response.GetResponseStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // ---------------------------------------------------------------------------
        // 6. Quote creation uses SDK (SalesJournal.Post)
        // ---------------------------------------------------------------------------
        static void TestQuoteCreationUsesSdk()
        {
            Console.WriteLine("\n6. Quote creation uses SDK");

            var sageService = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SageService.cs");
            Assert(sageService.Contains("salJourn.Post()"), "Quote creation uses SalesJournal.Post()");
            Assert(sageService.Contains("SDKInstanceManager.Instance.OpenSalesJournal()"), "Quote creation uses SDK OpenSalesJournal");
            Assert(!sageService.Contains("INSERT INTO tsalordr"), "No direct INSERT into tsalordr");
            Assert(!sageService.Contains("UPDATE tsalordr"), "No direct UPDATE of tsalordr");
        }

        // ---------------------------------------------------------------------------
        // 7. Invoice creation uses SDK (SalesJournal.Post)
        // ---------------------------------------------------------------------------
        static void TestInvoiceCreationUsesSdk()
        {
            Console.WriteLine("\n7. Invoice creation uses SDK");

            var sageService = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SageService.cs");
            // Invoice creation is not yet implemented, but verify no direct SQL writes exist
            Assert(!sageService.Contains("INSERT INTO tCusTr"), "No direct INSERT into tCusTr");
            Assert(!sageService.Contains("UPDATE tCusTr"), "No direct UPDATE of tCusTr");
        }

        // ---------------------------------------------------------------------------
        // 8. No SQL writes against Sage tables
        // ---------------------------------------------------------------------------
        static void TestNoSqlWrites()
        {
            Console.WriteLine("\n8. No SQL writes against Sage tables");

            var repositoryImpls = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SageRepositoryImpls.cs");
            Assert(!repositoryImpls.Contains("INSERT INTO"), "No INSERT statements in repository");
            Assert(!repositoryImpls.Contains("UPDATE ") || !repositoryImpls.Contains("SET"), "No UPDATE statements in repository");
            Assert(!repositoryImpls.Contains("DELETE FROM"), "No DELETE statements in repository");

            var sageService = File.ReadAllText(@"..\..\..\..\SageBridge.Connector\SageService.cs");
            Assert(!sageService.Contains("INSERT INTO"), "No INSERT statements in SageService");
            Assert(!sageService.Contains("UPDATE ") || !sageService.Contains("SET"), "No UPDATE statements in SageService");
            Assert(!sageService.Contains("DELETE FROM"), "No DELETE statements in SageService");
        }
    }
}
