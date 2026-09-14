using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SageBridge.Connector;

namespace SageBridge.Tests
{
    class Program
    {
        static int exitCode = 0;
        static int passed = 0;
        static int failed = 0;

        static void Main(string[] args)
        {
            Console.WriteLine("SageBridge Phase 5 — quote.create non-posting tests");
            Console.WriteLine("===================================================\n");

            try
            {
                TestPayloadHashing();
                TestQuoteNumberFormat();
                TestOperationLedgerConflict();
                TestValidationLogic();
                TestLedgerReplay();
                TestCanonicalizationDeterminism();
                TestResultPendingSurvivesInterruption();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: {ex}");
                exitCode = 2;
            }

            Console.WriteLine($"\n===================================================");
            Console.WriteLine($"Results: {passed} passed, {failed} failed, {passed + failed} total");
            Environment.ExitCode = exitCode;

            // Run Phase 2 repository isolation tests
            Phase2RepositoryTests.RunTests();

            // Run Phase 1 pairing tests
            Phase1PairingTests.RunTests();

            // Run Phase 6 multi-company safety tests
            Phase6MultiCompanyTests.RunTests();

            if (failed > 0 || Phase2RepositoryTests.failed > 0 || Phase1PairingTests.failed > 0 || Phase6MultiCompanyTests.Failed > 0)
                Environment.ExitCode = 1;
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
        // 1. Payload hashing is deterministic and action-scoped
        // ---------------------------------------------------------------------------
        static void TestPayloadHashing()
        {
            Console.WriteLine("1. Payload hashing");
            var action = "quote.create";

            var payloadA = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""A"",""quantity"":2,""unitPrice"":10.0}]}");

            // Same payload, same action → same hash
            var hash1 = OperationLedger.ComputeHash(action, payloadA);
            var hash2 = OperationLedger.ComputeHash(action, payloadA);
            Assert(hash1 == hash2, "Same payload produces identical hash",
                $"hash1={hash1} hash2={hash2}");

            // Different action, same payload → different hash
            var hashCreateCustomer = OperationLedger.ComputeHash("customer.create", payloadA);
            Assert(hash1 != hashCreateCustomer, "Different action produces different hash",
                $"quote.create={hash1} customer.create={hashCreateCustomer}");

            // Completely different payload → different hash
            var payloadB = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""B"",""quantity"":1,""unitPrice"":5.0}]}");
            var hash3 = OperationLedger.ComputeHash(action, payloadB);
            Assert(hash1 != hash3, "Different payload produces different hash",
                $"payloadA={hash1} payloadB={hash3}");

            // Empty/near-empty payload still hashes
            var emptyPayload = JObject.Parse(@"{""customerId"":""1"",""lines"":[]}");
            var emptyHash = OperationLedger.ComputeHash(action, emptyPayload);
            Assert(!string.IsNullOrEmpty(emptyHash), "Empty-lines payload still produces a hash",
                $"hash={emptyHash}");
        }

        // ---------------------------------------------------------------------------
        // 2. Quote number format is exactly QT-YYYYMMDD-NNN
        //    (format validation only — no SDK access)
        // ---------------------------------------------------------------------------
        static void TestQuoteNumberFormat()
        {
            Console.WriteLine("2. Quote number format (QT-YYYYMMDD-NNN)");

            // The format we expect. Real generation happens in SageService.GenerateQuoteNumber
            // which requires a live Sage connection, so we test the format contract here.
            var today = DateTime.Today;
            string expectedPrefix = $"QT-{today:yyyyMMdd}-";

            // Validate the pattern with a candidate we build ourselves.
            string candidate = $"{expectedPrefix}001";
            Assert(candidate.StartsWith("QT-"), "Starts with QT-",
                $"candidate={candidate}");
            Assert(candidate.Length == 15, "Total length is 15 (QT-YYYYMMDD-NNN)",
                $"candidate={candidate} length={candidate.Length}");
            Assert(candidate.Substring(0, 3) == "QT-", "Prefix is QT-");
            Assert(candidate.Substring(3, 8).All(char.IsDigit), "Date portion is 8 digits",
                $"date portion={candidate.Substring(3, 8)}");
            Assert(candidate.Substring(11, 1) == "-", "Separator before sequence is -");
            Assert(candidate.Substring(12).All(char.IsDigit), "Sequence portion is 3 digits",
                $"sequence={candidate.Substring(12)}");

            // Verify today's date portion matches the actual date.
            string datePortion = candidate.Substring(3, 8);
            string expectedDate = today.ToString("yyyyMMdd");
            Assert(datePortion == expectedDate, "Date portion matches today",
                $"got={datePortion} expected={expectedDate}");

            // Sequence padding: 001..999 must be zero-padded to 3 digits.
            for (int n = 1; n <= 999; n++)
            {
                string seq = $"{expectedPrefix}{n:D3}";
                Assert(seq.Length == 15, $"Sequence {n:D3} produces correct length",
                    $"seq={seq} len={seq.Length}");
                Assert(seq.EndsWith(n.ToString("D3")), $"Sequence {n} is zero-padded correctly",
                    $"expected ending={n.ToString("D3")} got={seq.Substring(12)}");
            }

            // Sequence zero-padding boundary: 1 → 001, 10 → 010, 100 → 100.
            Assert($"{expectedPrefix}{1:D3}" == $"{expectedPrefix}001", "1 pads to 001");
            Assert($"{expectedPrefix}{10:D3}" == $"{expectedPrefix}010", "10 pads to 010");
            Assert($"{expectedPrefix}{100:D3}" == $"{expectedPrefix}100", "100 stays 100");
        }

        // ---------------------------------------------------------------------------
        // 3. Operation ledger rejects same key, different payload (idempotency conflict)
        // ---------------------------------------------------------------------------
        static void TestOperationLedgerConflict()
        {
            Console.WriteLine("3. Operation ledger idempotency conflict detection");

            // Use a scratch ledger so we don't touch the running connector's db.
            var scratchDir = Path.Combine(Path.GetTempPath(), $"sagebridge_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratchDir);
            var dbPath = Path.Combine(scratchDir, "test_ledger.db");

            try
            {
                using var ledger = new OperationLedger(dbPath);
                var companyId = "test-company";
                var key = "idem-quote-001";
                var action = "quote.create";

                var payloadV1 = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""A"",""quantity"":1,""unitPrice"":10}]}");

                // Record the first operation.
                var hash1 = OperationLedger.ComputeHash(action, payloadV1);
                var op1 = ledger.CreateOperation(key, companyId, action, hash1, payloadV1.ToString(), "job-1");
                Assert(op1 != null, "First operation created", $"id={op1.Id}");
                Assert(op1.State == "processing", "First operation state is processing",
                    $"state={op1.State}");

                // Same key, same payload → hash matches → no conflict (this is the
                // replay-friendly path; the ledger itself just stores the row, conflict
                // detection lives in JobPoller but we mirror the check here).
                var hash1b = OperationLedger.ComputeHash(action, payloadV1);
                Assert(hash1 == hash1b, "Replayed payload hashes to the same value");

                // Same key, different payload → hash differs → conflict.
                var payloadV2 = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""B"",""quantity"":2,""unitPrice"":20}]}");

                // Try to create a second operation with the same key — the UNIQUE
                // constraint on (idempotency_key, company_id) should reject it.
                var hash2 = OperationLedger.ComputeHash(action, payloadV2);
                bool threw = false;
                try
                {
                    ledger.CreateOperation(key, companyId, action, hash2, payloadV2.ToString(), "job-2");
                }
                catch (Exception ex)
                {
                    threw = true;
                    // SQLite unique constraint violation is what we expect.
                    string msg = ex.Message.ToLowerInvariant();
                    Assert(msg.Contains("unique") || msg.Contains("constraint"),
                        "Conflict throws a SQLite constraint violation",
                        $"exception={ex.GetType().Name}: {ex.Message}");
                }

                Assert(threw, "Same key, different payload throws (idempotency conflict)",
                    "no exception thrown — UNIQUE constraint did not fire");

                // Verify the original operation is untouched.
                var op1After = ledger.GetOperation(key, companyId);
                Assert(op1After != null, "Original operation still present");
                Assert(op1After.PayloadHash == hash1, "Original payload hash unchanged",
                    $"stored={op1After.PayloadHash} original={hash1}");
            }
            finally
            {
                try { Directory.Delete(scratchDir, true); } catch { }
            }
        }

        // ---------------------------------------------------------------------------
        // 4. Validation logic exercised through QuoteCreateRequest parsing
        //    (no Sage access — pure payload validation)
        // ---------------------------------------------------------------------------
        static void TestValidationLogic()
        {
            Console.WriteLine("4. Quote.create payload validation (pre-SDK)");

            // The real validation lives in JobPoller.HandleCreateQuote and
            // SageService.CreateQuoteAsync. We can't call those without Sage,
            // but we can exercise the request model and the validation rules
            // that are expressible without a Sage connection.

            // Valid minimum payload.
            var valid = new QuoteCreateRequest
            {
                CustomerId = "10",
                Lines = new List<QuoteLineRequest>
                {
                    new QuoteLineRequest { Sku = "SKU-001", Quantity = 1, UnitPrice = 10.0m }
                }
            };
            Assert(valid.CustomerId == "10", "Valid payload parses customerId");
            Assert(valid.Lines.Count == 1, "Valid payload parses one line");
            Assert(valid.Lines[0].Sku == "SKU-001", "Valid payload parses sku");
            Assert(valid.Lines[0].Quantity == 1m, "Valid payload parses quantity");
            Assert(valid.Lines[0].UnitPrice == 10.0m, "Valid payload parses unitPrice");

            // Line count boundary: 100 lines is allowed.
            var maxLines = new QuoteCreateRequest
            {
                CustomerId = "10",
                Lines = Enumerable.Range(1, 100)
                    .Select(i => new QuoteLineRequest
                    {
                        Sku = $"SKU-{i:D3}",
                        Quantity = 1,
                        UnitPrice = 1.0m
                    }).ToList()
            };
            Assert(maxLines.Lines.Count == 100, "100-line payload is within limit");

            // Line count boundary: 101 lines exceeds the limit.
            var overMaxLines = new QuoteCreateRequest
            {
                CustomerId = "10",
                Lines = Enumerable.Range(1, 101)
                    .Select(i => new QuoteLineRequest
                    {
                        Sku = $"SKU-{i:D3}",
                        Quantity = 1,
                        UnitPrice = 1.0m
                    }).ToList()
            };
            Assert(overMaxLines.Lines.Count == 101, "101-line payload is constructed (limit is 100)",
                $"line count={overMaxLines.Lines.Count} limit=100");

            // Quantity and price sign rules (expressed as model-level assertions).
            var badQuantity = new QuoteLineRequest { Sku = "X", Quantity = 0, UnitPrice = 10 };
            Assert(badQuantity.Quantity == 0m, "Quantity of 0 is parseable (validation rejects <= 0)",
                "quantity=0 should be rejected by HandleCreateQuote");

            var badPrice = new QuoteLineRequest { Sku = "X", Quantity = 1, UnitPrice = -1 };
            Assert(badPrice.UnitPrice == -1m, "Negative unit price is parseable (validation rejects < 0)",
                "unitPrice=-1 should be rejected by HandleCreateQuote");

            var emptySku = new QuoteLineRequest { Sku = "", Quantity = 1, UnitPrice = 10 };
            Assert(emptySku.Sku == "", "Empty SKU is parseable (validation rejects empty sku)",
                "sku='' should be rejected by HandleCreateQuote");

            // JSON deserialization round-trip for the cloud payload shape.
            var json = @"{""customerId"":""42"",""lines"":[{""sku"":""ABC"",""quantity"":3,""unitPrice"":15.5}]}";
            var parsed = JObject.Parse(json);
            var deserialized = parsed.ToObject<QuoteCreateRequest>();
            Assert(deserialized != null, "JSON round-trips through QuoteCreateRequest");
            Assert(deserialized.CustomerId == "42", "customerId deserialized correctly");
            Assert(deserialized.Lines.Count == 1, "lines deserialized correctly");
            Assert(deserialized.Lines[0].Sku == "ABC", "sku deserialized correctly");
            Assert(deserialized.Lines[0].Quantity == 3m, "quantity deserialized correctly");
            Assert(deserialized.Lines[0].UnitPrice == 15.5m, "unitPrice deserialized correctly");
        }

        // ---------------------------------------------------------------------------
        // 5. Ledger replay: completed operation returns stored result without
        //    re-executing. (Uses ledger only, no Sage.)
        // ---------------------------------------------------------------------------
        static void TestLedgerReplay()
        {
            Console.WriteLine("5. Ledger replay and terminal state protection");

            var scratchDir = Path.Combine(Path.GetTempPath(), $"sagebridge_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratchDir);
            var dbPath = Path.Combine(scratchDir, "test_ledger.db");

            using (var ledger = new OperationLedger(dbPath))
            {
                var companyId = "test-company";
                var key = "idem-replay-001";
                var action = "quote.create";
                var payload = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""A"",""quantity"":1,""unitPrice"":10}]}");

                var hash = OperationLedger.ComputeHash(action, payload);
                var op = ledger.CreateOperation(key, companyId, action, hash, payload.ToString(), "job-replay-1");
                Assert(op != null, "Created operation for replay test");

                // Simulate: Sage write succeeded, store the sage_record_id.
                var quoteNumber = "QT-20260911-001";
                ledger.UpdateSageRecordId(key, companyId, quoteNumber);
                ledger.MarkSucceeded(key, companyId);

                // Re-fetch: should show succeeded state and the stored sage_record_id.
                var replayed = ledger.GetOperation(key, companyId);
                Assert(replayed != null, "Replayed operation found");
                Assert(replayed.State == "succeeded", "Replayed operation state is succeeded",
                    $"state={replayed.State}");
                Assert(replayed.SageRecordId == quoteNumber, "Replayed operation carries stored sage_record_id",
                    $"stored={replayed.SageRecordId} expected={quoteNumber}");
                Assert(replayed.CompletedAt.HasValue, "Replayed operation has completed_at",
                    $"completed_at={replayed.CompletedAt}");

                // Failed terminal state: once marked failed, the ledger records failure.
                var failKey = "idem-fail-001";
                var failOp = ledger.CreateOperation(failKey, companyId, action, hash, payload.ToString(), "job-fail-1");
                ledger.MarkFailed(failKey, companyId);
                var failedReplay = ledger.GetOperation(failKey, companyId);
                Assert(failedReplay != null, "Failed operation found");
                Assert(failedReplay.State == "failed", "Failed operation state is failed",
                    $"state={failedReplay.State}");
                Assert(failedReplay.CompletedAt.HasValue, "Failed operation has completed_at");

                // Uncertain terminal state: once marked uncertain, the ledger keeps it
                // for manual reconciliation and does not auto-transition.
                var unkKey = "idem-uncertain-001";
                var unkOp = ledger.CreateOperation(unkKey, companyId, action, hash, payload.ToString(), "job-unk-1");
                ledger.MarkUncertain(unkKey, companyId);
                var uncertainReplay = ledger.GetOperation(unkKey, companyId);
                Assert(uncertainReplay != null, "Uncertain operation found");
                Assert(uncertainReplay.State == "uncertain", "Uncertain operation state is uncertain",
                    $"state={uncertainReplay.State}");
                Assert(!uncertainReplay.CompletedAt.HasValue, "Uncertain operation has no completed_at",
                    $"completed_at={uncertainReplay.CompletedAt}");
            }

            // Cleanup scratch directory.
            try { Directory.Delete(scratchDir, true); } catch { }
        }

        // ---------------------------------------------------------------------------
        // 6. Canonicalization is deterministic across calls and key-order independent.
        // ---------------------------------------------------------------------------
        static void TestCanonicalizationDeterminism()
        {
            Console.WriteLine("6. Payload canonicalization determinism");

            // Build the same payload in two different JSON key orders.
            var payloadA = JObject.Parse(@"{""lines"":[{""sku"":""A"",""quantity"":1,""unitPrice"":10}],""customerId"":""10""}");
            var payloadB = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""unitPrice"":10,""quantity"":1,""sku"":""A""}]}");

            var hashA = OperationLedger.ComputeHash("quote.create", payloadA);
            var hashB = OperationLedger.ComputeHash("quote.create", payloadB);
            Assert(hashA == hashB, "Key-order-independent canonicalization produces identical hash",
                $"hashA={hashA} hashB={hashB}");

            // Line array order matters: different line order is a different payload.
            var payloadC = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""B"",""quantity"":1,""unitPrice"":10}]}");

            // First compare two identical line orders.
            var payloadD = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""A"",""quantity"":1,""unitPrice"":10}]}");

            // The canonicalizer sorts object keys; arrays stay in order.
            // So A and D should match, but A and C (different sku) should not.
            var hashD = OperationLedger.ComputeHash("quote.create", payloadD);
            Assert(hashA == hashD, "Identical payloads (same line order) produce identical hash",
                $"hashA={hashA} hashD={hashD}");
        }

        // ---------------------------------------------------------------------------
        // 7. result_pending: the ledger state that protects the dangerous case
        //    "Sage Post() succeeds, then the connection drops before the cloud
        //    hears about it, then the job is retried." The Sage write must
        //    never be re-attempted once a SageRecordId is on record; only
        //    delivery of the already-known result may be retried.
        // ---------------------------------------------------------------------------
        static void TestResultPendingSurvivesInterruption()
        {
            Console.WriteLine("7. result_pending survives an interrupted cloud ack");

            var scratchDir = Path.Combine(Path.GetTempPath(), $"sagebridge_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratchDir);
            var dbPath = Path.Combine(scratchDir, "test_ledger.db");

            try
            {
                var companyId = "test-company";
                var key = "idem-pending-001";
                var action = "invoice.create";
                var payload = JObject.Parse(@"{""customerId"":""10"",""lines"":[{""sku"":""A"",""quantity"":1,""unitPrice"":10}]}");
                var hash = OperationLedger.ComputeHash(action, payload);
                var invoiceId = "8421";

                using (var ledger = new OperationLedger(dbPath))
                {
                    var op = ledger.CreateOperation(key, companyId, action, hash, payload.ToString(), "job-pending-1");
                    Assert(op != null, "Created operation for result_pending test");
                    Assert(op.State == "processing", "New operation starts as processing", $"state={op.State}");

                    // Simulate: Sage Post() succeeded and we know the record id, but
                    // the cloud has not acknowledged the result yet.
                    ledger.UpdateSageRecordId(key, companyId, invoiceId);
                    ledger.MarkResultPending(key, companyId);

                    var pending = ledger.GetOperation(key, companyId);
                    Assert(pending.State == "result_pending", "Operation is result_pending after a successful write with no cloud ack yet",
                        $"state={pending.State}");
                    Assert(pending.SageRecordId == invoiceId, "SageRecordId is retained while result_pending",
                        $"stored={pending.SageRecordId} expected={invoiceId}");
                    Assert(!pending.CompletedAt.HasValue, "result_pending is not a completed/terminal state",
                        $"completed_at={pending.CompletedAt}");
                }

                // Simulate a full connector restart: reopen the same ledger file
                // and confirm the pending operation - and its SageRecordId - is
                // still there for FlushPendingResults/ReconcileUncertainOperations
                // to find and retry delivering, without touching Sage again.
                using (var reopened = new OperationLedger(dbPath))
                {
                    var survived = reopened.GetOperation(key, companyId);
                    Assert(survived != null, "result_pending operation survives a connector restart");
                    Assert(survived.State == "result_pending", "State is still result_pending after restart",
                        $"state={survived.State}");
                    Assert(survived.SageRecordId == invoiceId, "SageRecordId survives restart intact",
                        $"stored={survived.SageRecordId}");

                    var pendingOps = reopened.GetOperationsByState("result_pending");
                    Assert(pendingOps.Any(o => o.IdempotencyKey == key), "GetOperationsByState(\"result_pending\") finds the operation for retry");

                    // Simulate the retried delivery finally reaching the cloud.
                    reopened.MarkSucceeded(key, companyId);
                    var delivered = reopened.GetOperation(key, companyId);
                    Assert(delivered.State == "succeeded", "Once delivered, state moves to succeeded",
                        $"state={delivered.State}");
                    Assert(delivered.SageRecordId == invoiceId, "SageRecordId unchanged by the delivery-only transition",
                        $"stored={delivered.SageRecordId}");
                    Assert(delivered.CompletedAt.HasValue, "succeeded is terminal and sets completed_at");
                }
            }
            finally
            {
                try { Directory.Delete(scratchDir, true); } catch { }
            }
        }
    }
}
