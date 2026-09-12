-- ============================================================================
-- A/R payment-application investigation
-- ============================================================================
-- Read-only diagnostic queries only. Nothing here writes to the company
-- database. This is NOT wired into the connector's running process or its
-- HTTP API - run it manually, offline, against a real Sage 50 Canada company
-- database (MySQL-compatible, matching this codebase's existing DATE_SUB /
-- GREATEST / LEAST usage in SageRepositoryImpls.cs) via any SQL client that
-- can reach it, or by pasting individual statements into the same
-- SDKDatabaseUtility.RunSelectQuery() path SageService.Select() already uses.
--
-- Goal: determine whether Sage 50 Canada exposes a genuine
-- receipt/credit-to-invoice application relationship, so the connector's
-- current FIFO balance approximation (SageRepositoryImpls.cs,
-- SageInvoiceRepository.GetInvoicesAsync) can be replaced with something
-- authoritative instead of an allocation guess.
--
-- Prefer running this against a sample/test company, not production data,
-- for Step 4 onward (it asks you to post real test transactions).
-- ============================================================================


-- ----------------------------------------------------------------------------
-- STEP 1: Enumerate every table. Don't guess a payment/application table's
-- name - list them all and eyeball anything containing Pmt/Pay/Rcpt/Receipt/
-- Apply/Applic/Settle/Alloc/Link.
-- ----------------------------------------------------------------------------
SHOW TABLES;


-- ----------------------------------------------------------------------------
-- STEP 2: Full column list of every table this codebase already touches.
-- Only a handful of columns from tCusTrDt (dAmtOwg, lCusTrId) have ever
-- actually been read - the rest of its schema is unknown until this runs.
-- Repeat "SHOW COLUMNS FROM <name>;" for anything promising found in Step 1.
-- ----------------------------------------------------------------------------
SHOW COLUMNS FROM tCusTr;
SHOW COLUMNS FROM tCusTrDt;
SHOW COLUMNS FROM tCustomr;


-- ----------------------------------------------------------------------------
-- STEP 3: What does nTranType actually mean? SageRepositoryImpls.cs
-- contradicts itself: one comment says nTranType 1=order, 2=quote (legacy);
-- another says nTranType 1/2 = receipt/credit memo (the basis the current
-- balance-netting formula relies on). Settle it against real data: compare
-- these counts/date ranges to Sage 50's own Receipts List, Credit Memos
-- List, and Sales Invoice List for the same company.
-- ----------------------------------------------------------------------------
SELECT nTranType,
       COUNT(*)              AS cnt,
       MIN(dtDate)           AS earliest,
       MAX(dtDate)           AS latest,
       SUM(dPreTaxAmt)       AS total
FROM tCusTr
GROUP BY nTranType
ORDER BY nTranType;


-- ----------------------------------------------------------------------------
-- STEP 4: The Invoice A / Invoice B test.
--   Invoice A = older, unpaid.  Invoice B = newer.
--   A receipt will be applied ONLY to Invoice B.
-- ----------------------------------------------------------------------------

-- 4a. Pick (or set up) Customer X, and list their open invoices to identify
--     A (older) and B (newer). Replace <CustomerX_lId>.
SELECT lId, sSource AS invoiceNumber, dtDate, dPreTaxAmt, dtDueDate
FROM tCusTr
WHERE lCusId = <CustomerX_lId> AND nTranType = 0
ORDER BY dtDate;

-- 4b. --- MANUAL STEP, NOT SQL ---
--     In the Sage 50 UI, post a receipt for Customer X applied explicitly
--     and ONLY to Invoice B. Then, in Sage 50's own Customer Ledgers / AR
--     Aging screen, record Sage's DISPLAYED balance for Invoice A and
--     Invoice B. This answers report points 1-2 and cannot be obtained by
--     SQL alone - it's the ground truth this whole investigation is
--     checking against.

-- 4c. Find the new receipt's own tCusTr row.
SELECT * FROM tCusTr
WHERE lCusId = <CustomerX_lId>
ORDER BY lId DESC
LIMIT 5;

-- 4d. Pull EVERY column (not just dAmtOwg) of the new receipt's own
--     distribution rows. Replace <receipt_lId> with the lId found in 4c.
--     Inspect every value for anything matching Invoice B's lId
--     specifically (not just lCusTrId, which is already known to be the
--     receipt's own header id, not a link to what it was applied to).
SELECT * FROM tCusTrDt
WHERE lCusTrId = <receipt_lId>;

-- 4e. Per the "don't infer from names alone" rule: confirm any candidate
--     link column found in 4d by repeating steps 4b-4d with a second
--     receipt applied to a DIFFERENT invoice, and checking that the
--     column's value changes to match. A column whose value never
--     correlates with which invoice was actually paid is not a link field,
--     regardless of its name.

-- 4f. Compare Sage's real answer (from step 4b) against what the connector's
--     CURRENT formula computes for the same two invoices (this is the exact
--     query in SageRepositoryImpls.cs, SageInvoiceRepository.GetInvoicesAsync
--     - copied here verbatim so this script stays self-contained):
SELECT h.lId, h.sSource AS invoiceNumber, h.dPreTaxAmt,
       GREATEST(0, LEAST(h.dPreTaxAmt,
           (SELECT COALESCE(SUM(h2.dPreTaxAmt), 0) FROM tCusTr h2
            WHERE h2.lCusId = h.lCusId AND h2.nTranType = 0 AND h2.lId <= h.lId)
           -
           (SELECT COALESCE(SUM(h3.dPreTaxAmt), 0) FROM tCusTr h3
            WHERE h3.lCusId = h.lCusId AND h3.nTranType IN (1, 2))
       )) AS fifoBalance
FROM tCusTr h
WHERE h.lCusId = <CustomerX_lId> AND h.nTranType = 0
ORDER BY h.dtDate;

-- Record, for both A and B: Sage's real displayed balance (4b) vs.
-- fifoBalance (4f). Any difference is a confirmed FIFO/Sage disagreement
-- for that invoice - not an assumption, a measured one.


-- ----------------------------------------------------------------------------
-- STEP 5: Repeat the post-a-real-transaction-then-diff protocol (4b, 4c, 4d,
-- 4f) for each of the following. For every scenario, record: original
-- amount, Sage's own displayed balance, the fifoBalance query's output, and
-- whether they agree.
-- ----------------------------------------------------------------------------
-- 5a. Partial payment: a receipt smaller than the invoice, applied to one
--     specific open invoice while an older invoice remains open.
-- 5b. Credit note applied to a specific invoice (confirm its nTranType via
--     Step 3 first).
-- 5c. Unapplied customer credit: post a credit/receipt on account WITHOUT
--     applying it to any invoice. Check whether Sage's AR total/aging
--     report changes at all, and whether fifoBalance changes for any
--     invoice (it will, per the code - the reduction pool in 4f/Step 3 is
--     not scoped to "applied" transactions at all).
-- 5d. Overpayment: a receipt larger than the invoice it's applied to. Check
--     whether Sage carries the excess as a distinct on-account credit, and
--     whether the connector's reduction pool silently consumes it against
--     whatever invoice comes next in lId order.
-- 5e. One receipt applied across multiple invoices, deliberately skipping
--     one open invoice in between (by posting date/lId) that the receipt
--     was NOT applied to.
-- ============================================================================
