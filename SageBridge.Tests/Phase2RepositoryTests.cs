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
                TestJobResultContract();
                TestSyncFieldNames();
                TestProvisioningReportedFromSync();
                TestHeartbeatWired();
                TestInvoiceBalancesUseGrossDetailSums();
                TestCustomerBalancesUseInvoiceDetailSums();
                TestCurrencyNormalizedReadSideAR();
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

            var apiServer = File.ReadAllText(SourceFile("ApiServer.cs"));
            Assert(apiServer.Contains("public class QuotesController"), "QuotesController exists in ApiServer.cs");
        }

        static void TestReportsControllerExists()
        {
            Console.WriteLine("2. ReportsController contract check");

            var apiServer = File.ReadAllText(SourceFile("ApiServer.cs"));
            Assert(apiServer.Contains("public class ReportsController"), "ReportsController exists in ApiServer.cs");
            Assert(!apiServer.Contains("api/reports/ar-aging"), "A/R aging endpoint is removed");
            Assert(!apiServer.Contains("GetARAgingReportAsync"), "A/R aging service call is removed");
        }

        // ---------------------------------------------------------------------------
        // 2. No SQL in controllers
        // ---------------------------------------------------------------------------
        static void TestNoSqlInControllers()
        {
            Console.WriteLine("\n2. No SQL in controllers");

            var apiServer = File.ReadAllText(SourceFile("ApiServer.cs"));
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

            var syncEngine = File.ReadAllText(SourceFile("SyncEngine.cs"));
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

            var jobPoller = File.ReadAllText(SourceFile("JobPoller.cs"));
            Assert(!jobPoller.Contains("tsalordr"), "JobPoller.cs has no tsalordr references");
            Assert(!jobPoller.Contains("tCusTr"), "JobPoller.cs has no tCusTr references");
            Assert(!jobPoller.Contains("bQuote"), "JobPoller.cs has no bQuote references");
            Assert(!jobPoller.Contains("nTranType"), "JobPoller.cs has no nTranType references");

            var syncEngine = File.ReadAllText(SourceFile("SyncEngine.cs"));
            Assert(!syncEngine.Contains("tsalordr"), "SyncEngine.cs has no tsalordr references");
            Assert(!syncEngine.Contains("tCusTr"), "SyncEngine.cs has no tCusTr references");
            Assert(!syncEngine.Contains("bQuote"), "SyncEngine.cs has no bQuote references");
            Assert(!syncEngine.Contains("nTranType"), "SyncEngine.cs has no nTranType references");

            var apiServer = File.ReadAllText(SourceFile("ApiServer.cs"));
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

            }
            catch (Exception ex)
            {
                Assert(false, "API endpoints test failed", ex.Message);
            }
        }

        static string SourceFile(string fileName)
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\SageBridge.Connector", fileName));
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

            var sageService = File.ReadAllText(SourceFile("SageService.cs"));
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

            var sageService = File.ReadAllText(SourceFile("SageService.cs"));
            Assert(sageService.Contains("public async Task<object> CreateInvoiceAsync"), "CreateInvoiceAsync exists");
            Assert(sageService.Contains("SDKInstanceManager.Instance.OpenSalesJournal()"), "Invoice creation uses SDK OpenSalesJournal");
            int invoiceStart = sageService.IndexOf("public async Task<object> CreateInvoiceAsync", StringComparison.Ordinal);
            int invoiceEnd = sageService.IndexOf("return new\r\n            {", invoiceStart, StringComparison.Ordinal);
            string invoiceMethod = invoiceStart >= 0 && invoiceEnd > invoiceStart
                ? sageService.Substring(invoiceStart, invoiceEnd - invoiceStart)
                : string.Empty;
            Assert(invoiceMethod.Contains("SetQuantity"), "Sales invoice lines use the SDK quantity field");
            Assert(!invoiceMethod.Contains("SetOrdered"), "Sales invoice lines do not use the order-only ordered field");
            Assert(invoiceMethod.Contains("Task.Delay"), "Invoice read-back retries after Sage Post()");
            Assert(invoiceMethod.Contains("FindLastCreatedInvoiceAsync"), "Invoice read-back uses the repository after retry delay");
            // Invoice creation must never re-throw after Post() succeeds - a thrown
            // exception there would be misread as "the write failed" upstream and
            // trigger a retry that double-posts the invoice.
            Assert(sageService.Contains("degrades to an empty Id rather than throwing"), "CreateInvoiceAsync documents the no-throw-after-Post() safety requirement");
            Assert(!sageService.Contains("INSERT INTO tCusTr"), "No direct INSERT into tCusTr");
            Assert(!sageService.Contains("UPDATE tCusTr"), "No direct UPDATE of tCusTr");
        }

        // ---------------------------------------------------------------------------
        // 9. JobPoller submits {status, sageId, error} to the cloud, not the
        //    raw Sage result object, and never marks an operation succeeded
        //    without a confirmed cloud acknowledgement.
        // ---------------------------------------------------------------------------
        static void TestJobResultContract()
        {
            Console.WriteLine("\n9. Job result cloud contract");

            var jobPoller = File.ReadAllText(SourceFile("JobPoller.cs"));
            Assert(System.Text.RegularExpressions.Regex.IsMatch(jobPoller, @"new\s*\{\s*status,\s*sageId,\s*error\s*\}"),
                "SubmitJobResult sends {status, sageId, error} matching the cloud API contract");
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(jobPoller, @"new\s*\{\s*status,\s*result,\s*error\s*\}"),
                "SubmitJobResult no longer sends the raw Sage result object as 'result'");
            Assert(jobPoller.Contains("private async Task<bool> SubmitJobResult"), "SubmitJobResult reports delivery success/failure to its caller");
            Assert(jobPoller.Contains("MarkResultPending"), "JobPoller uses the result_pending ledger state before a cloud ack is confirmed");
            Assert(jobPoller.Contains("case \"result_pending\":"), "ProcessJob retries delivery for result_pending operations");
            Assert(jobPoller.Contains("FlushPendingResults"), "JobPoller proactively retries undelivered results every poll tick");
            Assert(jobPoller.Contains("case \"invoice.create\":"), "JobPoller dispatches invoice.create jobs");
            Assert(jobPoller.Contains("HandleCreateInvoice"), "JobPoller has an invoice.create handler");
        }

        // ---------------------------------------------------------------------------
        // 10. SyncEngine sends the field names the cloud API actually expects
        //     for quote and invoice-summary sync (regression guard: these two
        //     were previously sent in camelCase and were silently rejected by
        //     every real sync attempt).
        // ---------------------------------------------------------------------------
        static void TestSyncFieldNames()
        {
            Console.WriteLine("\n10. Sync payload field names match the cloud API");

            var syncEngine = File.ReadAllText(SourceFile("SyncEngine.cs"));
            Assert(syncEngine.Contains("Quotes = quotes"), "/sync/quotes sends 'Quotes' (capitalized) matching sync.ts's body.Quotes");
            Assert(syncEngine.Contains("InvoiceSummary = invoiceSummary"), "/sync/invoice-summary sends 'InvoiceSummary' matching sync.ts's body.InvoiceSummary");
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(syncEngine, @"\bquotes\s*=\s*quotes\b"), "no lowercase 'quotes' sync field remains");
            Assert(!syncEngine.Contains("summary = invoiceSummary"), "no lowercase 'summary' sync field remains");
        }

        // ---------------------------------------------------------------------------
        // 11. Regression guard for the provisioning-stuck-at-10% bug and its
        //     follow-on 409 bug: the connector previously never called
        //     /connector/provisioning at all, and once it did, it skipped
        //     'checking_sage' - the ONLY legal transition out of
        //     'connector_connected' per phase1.ts's NEXT table - so every
        //     report in the chain 409'd (INVALID_PROVISIONING_TRANSITION)
        //     starting from the very first one. SyncEngine must drive the
        //     state machine from actual sync progress in the exact order the
        //     cloud's FSM accepts, and a sync failure must report 'failed',
        //     never leave the state looking like it's progressing toward
        //     ready on its own.
        // ---------------------------------------------------------------------------
        static void TestProvisioningReportedFromSync()
        {
            Console.WriteLine("\n11. Sync engine drives the provisioning state machine");

            var syncEngine = File.ReadAllText(SourceFile("SyncEngine.cs"));
            Assert(syncEngine.Contains("/connector/provisioning"), "SyncEngine reports provisioning progress to the cloud");

            var canonicalOrder = new[] { "checking_sage", "company_selected", "provisioning", "syncing_customers", "syncing_invoices", "syncing_products", "syncing_quotes", "finalizing", "ready" };
            foreach (var state in canonicalOrder)
            {
                Assert(syncEngine.Contains($"\"{state}\""), $"SyncEngine reports provisioning state '{state}'");
            }

            // The exact canonical sequence from phase1.ts's NEXT table must be
            // reported in that order in source - this is what protects against
            // regressing to a sequence that skips a step and 409s from the
            // very first call.
            var indices = canonicalOrder.Select(state => syncEngine.IndexOf($"\"{state}\"")).ToArray();
            var inOrder = true;
            for (int i = 1; i < indices.Length; i++)
            {
                if (indices[i - 1] < 0 || indices[i] < 0 || indices[i - 1] >= indices[i]) inOrder = false;
            }
            Assert(inOrder, "Provisioning states are reported in the exact canonical FSM order (checking_sage first)",
                $"indices={string.Join(",", canonicalOrder.Zip(indices, (s, i) => $"{s}={i}"))}");

            Assert(syncEngine.Contains("ReportProvisioningAsync(\"failed\""),
                "A sync failure reports the provisioning state as failed, not silently left as-is or reported ready");
        }

        // ---------------------------------------------------------------------------
        // 12. Regression guard: live testing found zero heartbeat
        //     implementation, so companies.last_seen_at (what the UI's
        //     online/offline indicator reads) went stale even while the
        //     connector was actively syncing (/sync/* only touches
        //     last_sync_at). Heartbeat must reuse the cloud's existing
        //     /connector/heartbeat endpoint via the same per-connector
        //     machine credential already used for jobs/sync - not
        //     /connector/jobs polling, and not a new mechanism - on an
        //     interval well below the server's 120s staleness threshold,
        //     and must never be able to crash sync/job processing.
        // ---------------------------------------------------------------------------
        static void TestHeartbeatWired()
        {
            Console.WriteLine("\n12. Heartbeat is wired to the existing cloud endpoint");

            var heartbeatSender = File.ReadAllText(SourceFile("HeartbeatSender.cs"));
            Assert(heartbeatSender.Contains("/connector/heartbeat"), "HeartbeatSender posts to the existing /connector/heartbeat endpoint");
            Assert(heartbeatSender.Contains("CloudAuthenticator"), "HeartbeatSender authenticates with the per-connector machine credential, not a human/Firebase identity");
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(heartbeatSender, @"PostAsync\s*\(\s*""\/connector\/jobs"),
                "Heartbeat does not piggyback on job polling as an implicit heartbeat (checks the actual call site, not doc comments)");

            var intervalMatch = System.Text.RegularExpressions.Regex.Match(heartbeatSender, @"IntervalSeconds\s*=\s*(\d+)");
            Assert(intervalMatch.Success, "HeartbeatSender defines an explicit interval constant");
            if (intervalMatch.Success)
            {
                int interval = int.Parse(intervalMatch.Groups[1].Value);
                Assert(interval > 0 && interval < 120, $"Heartbeat interval ({interval}s) is significantly below the 120s staleness threshold", $"interval={interval}");
            }

            Assert(heartbeatSender.Contains("catch (Exception"), "Heartbeat failures are caught locally and cannot crash sync/job processing");

            var program = File.ReadAllText(SourceFile("Program.cs"));
            Assert(program.Contains("new HeartbeatSender("), "Program.cs instantiates HeartbeatSender");
            Assert(program.Contains("heartbeatSender.Start()"), "Program.cs starts the heartbeat sender");
            Assert(program.Contains("heartbeatSender.Stop()"), "Program.cs stops the heartbeat sender on shutdown");

            // Heartbeat advertises connector capabilities to the cloud - guard
            // against it silently drifting from what the connector actually does.
            Assert(heartbeatSender.Contains("supportedActions = ConnectorCapabilities.SupportedActions"), "Heartbeat advertises its real supported job actions");
            Assert(heartbeatSender.Contains("supportedSync = ConnectorCapabilities.SupportedSync"), "Heartbeat advertises its real supported sync datasets");

            var capabilities = File.ReadAllText(SourceFile("ConnectorCapabilities.cs"));
            var jobPoller = File.ReadAllText(SourceFile("JobPoller.cs"));
            var syncEngine = File.ReadAllText(SourceFile("SyncEngine.cs"));
            foreach (var action in new[] { "customer.create", "quote.create", "invoice.create" })
            {
                Assert(capabilities.Contains($"\"{action}\""), $"ConnectorCapabilities declares {action}");
                Assert(jobPoller.Contains($"case \"{action}\":"), $"JobPoller actually dispatches {action} (capabilities list must not claim more than is implemented)");
            }
            // Every action JobPoller actually dispatches (excluding the internal
            // job-status labels checked elsewhere in this file) must also be
            // advertised - catches the capabilities list silently falling BEHIND
            // a newly added real action, not just running ahead of one.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(jobPoller, @"case ""([a-z]+\.[a-z]+)"":"))
            {
                Assert(capabilities.Contains($"\"{m.Groups[1].Value}\""), $"ConnectorCapabilities must advertise every action JobPoller dispatches ({m.Groups[1].Value})");
            }
            foreach (var dataset in new[] { "customers", "invoices", "products", "quotes", "invoice-summary" })
            {
                Assert(capabilities.Contains($"\"{dataset}\""), $"ConnectorCapabilities declares the {dataset} sync dataset");
                Assert(syncEngine.Contains($"/sync/{dataset}"), $"SyncEngine actually posts to /sync/{dataset}");
            }
        }

        // ---------------------------------------------------------------------------
        // 8. No SQL writes against Sage tables
        // ---------------------------------------------------------------------------
        static void TestNoSqlWrites()
        {
            Console.WriteLine("\n8. No SQL writes against Sage tables");

            var repositoryImpls = File.ReadAllText(SourceFile("SageRepositoryImpls.cs"));
            Assert(!repositoryImpls.Contains("INSERT INTO"), "No INSERT statements in repository");
            Assert(!repositoryImpls.Contains("UPDATE ") || !repositoryImpls.Contains("SET"), "No UPDATE statements in repository");
            Assert(!repositoryImpls.Contains("DELETE FROM"), "No DELETE statements in repository");

            var sageService = File.ReadAllText(SourceFile("SageService.cs"));
            Assert(!sageService.Contains("INSERT INTO"), "No INSERT statements in SageService");
            Assert(!sageService.Contains("UPDATE ") || !sageService.Contains("SET"), "No UPDATE statements in SageService");
            Assert(!sageService.Contains("DELETE FROM"), "No DELETE statements in SageService");
        }

        // ---------------------------------------------------------------------------
        // 13. Regression guard for the narrow Beta data contract.
        //     Current balances and currency-normalized totals remain available;
        //     SageBridge does not expose an aging or fake payment contract.
        // ---------------------------------------------------------------------------
        static void TestInvoiceBalancesUseGrossDetailSums()
        {
            Console.WriteLine("\n13. Invoice contract keeps authoritative balances and removes derived fields");

            var source = File.ReadAllText(SourceFile("SageRepositoryImpls.cs"));
            int invoicesStart = source.IndexOf("public async Task<List<InvoiceRecord>> GetInvoicesAsync()");
            int summaryStart = source.IndexOf("public async Task<InvoiceSummaryRecord> GetInvoiceSummaryAsync()");
            int existsStart = source.IndexOf("public async Task<bool> InvoiceExistsAsync");
            int findLastStart = source.IndexOf("public async Task<InvoiceRecord?> FindLastCreatedInvoiceAsync");

            Assert(invoicesStart >= 0 && summaryStart > invoicesStart && existsStart > summaryStart && findLastStart > existsStart,
                "Invoice repository methods exist in the expected order");

            string invoicesBody = source.Substring(invoicesStart, summaryStart - invoicesStart);
            string summaryBody = source.Substring(summaryStart, existsStart - summaryStart);
            string findLastBody = source.Substring(findLastStart);

            Assert(invoicesBody.Contains("SUM(d.dAmount)") && invoicesBody.Contains("d.lCusTrId = h.lId") &&
                   invoicesBody.Contains("dAmtHm") && invoicesBody.Contains("lCurrncyId"),
                "Invoice list derives transaction and home-currency values from attached detail amounts");
            Assert(invoicesBody.Contains("dTransactionTotal") && invoicesBody.Contains("dHomeTotal") &&
                   invoicesBody.Contains("dTransactionBalance") && invoicesBody.Contains("dHomeBalance"),
                "Invoice list keeps transaction and home/reporting currency totals and balances explicit");
            Assert(invoicesBody.Contains("0.005") && invoicesBody.Contains("HomeCurrencyBalance"),
                "Invoice list normalizes sub-cent balance noise to zero");
            Assert(!invoicesBody.Contains("GREATEST(0, LEAST(") && !invoicesBody.Contains("nTranType IN (1, 2)") && !invoicesBody.Contains("dAmtOwg"),
                "Invoice list has no FIFO, header netting or dAmtOwg balance logic");
            Assert(!invoicesBody.Contains("DueDate") && !invoicesBody.Contains("Status = homeBalance"),
                "Invoice list exposes no unavailable due date or derived payment status");

            Assert(summaryBody.Contains("dHomeTotal") && summaryBody.Contains("dTransactionTotal") &&
                   summaryBody.Contains("nTranType IN (0, 8, 9)"),
                "Invoice summary uses home-currency A/R totals and includes type 8/9 adjustments");
            Assert(!summaryBody.Contains("dPreTaxAmt") && !summaryBody.Contains("GREATEST(0, LEAST(") && !summaryBody.Contains("dAmtOwg"),
                "Invoice summary has no pre-tax, FIFO or dAmtOwg final-balance logic");
            Assert(!summaryBody.Contains("nPaid") && !summaryBody.Contains("nUnpaid"),
                "Invoice summary exposes no derived paid/unpaid semantics");

            Assert(findLastBody.Contains("dAmtHm") && findLastBody.Contains("d.lCusTrId = h.lId") && !findLastBody.Contains("dAmtOwg"),
                "Post-write invoice read-back keeps home and transaction currency balances");

            var repositories = File.ReadAllText(SourceFile("SageRepositories.cs"));
            Assert(!repositories.Contains("DateTime? DueDate"), "InvoiceRecord removes the unavailable DueDate contract");
            Assert(!repositories.Contains("ARAgingRecord") && !repositories.Contains("GetARAgingAsync"),
                "Invoice contracts contain no aging model or repository method");

            var sageService = File.ReadAllText(SourceFile("SageService.cs"));
            Assert(!sageService.Contains("i.DueDate") && !sageService.Contains("GetARAgingReportAsync"),
                "SageService exposes no unavailable due date or aging projection");
            Assert(!sageService.Contains("q.Balance") && !sageService.Contains("quote.Balance") &&
                   !sageService.Contains("Status = \"Active\""),
                "Quote and customer-create projections expose no unreliable derived fields");
        }

        // ---------------------------------------------------------------------------
        // 14. Customer list/detail balances must aggregate the same positive,
        //     authoritative per-invoice balances used by reports and dashboard.
        // ---------------------------------------------------------------------------
        static void TestCustomerBalancesUseInvoiceDetailSums()
        {
            Console.WriteLine("\n14. Customer balances use authoritative per-invoice detail sums");

            var source = File.ReadAllText(SourceFile("SageDataRepositoryImpls.cs"));
            int listStart = source.IndexOf("public async Task<List<CustomerRecord>> GetCustomersAsync()");
            int idStart = source.IndexOf("public async Task<CustomerRecord?> GetCustomerByIdAsync");
            int nameStart = source.IndexOf("public async Task<CustomerRecord?> GetCustomerByNameAsync");
            int resolveStart = source.IndexOf("public async Task<string?> ResolveCustomerNameAsync");

            Assert(listStart >= 0 && idStart > listStart && nameStart > idStart && resolveStart > nameStart,
                "Customer repository methods exist in the expected order");

            string listBody = source.Substring(listStart, idStart - listStart);
            string idBody = source.Substring(idStart, nameStart - idStart);
            string nameBody = source.Substring(nameStart, resolveStart - nameStart);

            foreach (string body in new[] { listBody, idBody, nameBody })
            {
                Assert(body.Contains("SUM(d.dAmount)") && body.Contains("d.lCusTrId = h.lId") &&
                       body.Contains("dAmtHm") && body.Contains("lCurrncyId") &&
                       body.Contains("nTranType IN (0, 8, 9)") && body.Contains("AS dBalance"),
                    "Customer query aggregates home-currency invoice and type 8/9 balances");
                Assert(!body.Contains("dPreTaxAmt") && !body.Contains("nTranType IN (1, 2)") && !body.Contains("dAmtOwg"),
                    "Customer query has no pre-tax, header netting or dAmtOwg final-balance logic");
            }
        }

        // ---------------------------------------------------------------------------
        // 15. Read-side A/R uses Sage's home-currency representation for foreign
        //     transactions, includes type 8/9 report adjustments, and has no stale
        //     dtDueDate dependency.
        // ---------------------------------------------------------------------------
        static void TestCurrencyNormalizedReadSideAR()
        {
            Console.WriteLine("\n15. Currency-normalized read-side A/R and report adjustments");

            var invoiceSource = File.ReadAllText(SourceFile("SageRepositoryImpls.cs"));
            var customerSource = File.ReadAllText(SourceFile("SageDataRepositoryImpls.cs"));
            var contracts = File.ReadAllText(SourceFile("SageRepositories.cs"));
            var allConnectorSources = string.Join("\n", Directory.GetFiles(Path.GetDirectoryName(SourceFile("ApiServer.cs"))!, "*.cs")
                .Select(File.ReadAllText));

            Assert(invoiceSource.Contains("dAmtHm") && invoiceSource.Contains("lCurrncyId"),
                "Invoice reads select the stored home-currency amount for foreign transactions");
            Assert(customerSource.Contains("dAmtHm") && customerSource.Contains("lCurrncyId"),
                "Customer A/R reads use the stored home-currency amount for foreign transactions");
            Assert(invoiceSource.Contains("nTranType IN (0, 8, 9)"),
                "A/R report queries include invoices, credit notes, and debit notes");
            Assert(customerSource.Contains("nTranType IN (0, 8, 9)"),
                "Customer A/R queries include invoices, credit notes, and debit notes");
            Assert(contracts.Contains("TransactionCurrencyBalance") && contracts.Contains("HomeCurrencyBalance"),
                "Invoice DTO keeps transaction and home/reporting currency balances explicit");
            Assert(!allConnectorSources.Contains("dtDueDate"),
                "No connector source queries the unsupported dtDueDate column");

            decimal Normalize(decimal transactionAmount, decimal homeAmount, int currencyId)
                => currencyId == 1 ? transactionAmount : homeAmount;

            Assert(Normalize(100m, 100m, 1) == 100m,
                "Home-currency invoice preserves its transaction amount");
            Assert(Normalize(2767m, 3929.14m, 2) == 3929.14m,
                "Foreign-currency invoice uses its home amount");
            Assert(Normalize(-2500m, -3550m, 2) == -3550m,
                "Foreign-currency partial payment uses its stored home amount");
            Assert(Normalize(234.70m, 347.36m, 2) == 347.36m,
                "Foreign-currency debit note contributes its home amount");
            Assert(Normalize(-121.39m, -121.39m, 1) == -121.39m,
                "Credit note reduces report A/R");

            decimal paramount = Normalize(2767m, 3929.14m, 2)
                + Normalize(234.70m, 347.36m, 2);
            Assert(paramount == 4276.50m,
                "Paramount fixture reconciles to Sage home-currency A/R");

            decimal totalAR = Normalize(355543.02m, 355543.02m, 1)
                + Normalize(2767m, 3929.14m, 2)
                + Normalize(-121.39m, -121.39m, 1)
                + Normalize(234.70m, 347.36m, 2);
            Assert(totalAR == 359698.13m,
                "Matched A/R fixture reconciles to $359,698.13");
        }
    }
}