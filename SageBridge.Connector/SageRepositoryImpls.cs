using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Sage 50 Canada quote repository.
    /// 
    /// IMPORTANT: This class contains Sage 50 internal schema details (table names,
    /// column names, flags like bQuote). These are implementation details specific
    /// to Sage 50 Canada 2026 and may vary by Sage version.
    /// 
    /// All Sage-version-specific queries are centralized here so supporting a future
    /// Sage version requires changing only this adapter, not the application.
    /// 
    /// This repository uses read-only SQL queries via the SDK database utility.
    /// No writes are performed - all writes go through the official Sage SDK.
    /// </summary>
    public sealed class SageQuoteRepository : ISageQuoteRepository
    {
        private readonly SageService _sageService;

        public SageQuoteRepository(SageService sageService)
        {
            _sageService = sageService ?? throw new ArgumentNullException(nameof(sageService));
        }

        /// <summary>
        /// Get all quotes from Sage.
        /// Uses tsalordr table with bQuote=1 flag (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<List<QuoteRecord>> GetQuotesAsync()
        {
            // Sage internal schema: tsalordr stores both orders and quotes.
            // bQuote=1 indicates a quote, bQuote=0 indicates an order.
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSONum AS sQuoteNumber,
       h.dtSODate AS dtDate, h.dTotal, h.bQuote,
       COALESCE((
           SELECT SUM(d.dAmtOwg) FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
           ORDER BY d.lId DESC LIMIT 1
       ), 0) AS dBalance
FROM tsalordr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.bQuote = 1
ORDER BY h.dtSODate DESC, h.lId DESC";

            var quotes = new List<QuoteRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                quotes.Add(new QuoteRecord
                {
                    Id = GetText(row, "lId"),
                    CustomerId = GetText(row, "lCusId"),
                    CustomerName = GetText(row, "sCustomerName"),
                    QuoteNumber = GetText(row, "sQuoteNumber"),
                    Date = GetNullableDate(row, "dtDate"),
                    Total = GetDecimal(row, "dTotal"),
                    Balance = GetDecimal(row, "dBalance"),
                    Status = "Quote"
                });
            }

            return quotes;
        }

        /// <summary>
        /// Get a single quote by its quote number.
        /// Uses tsalordr table with bQuote=1 flag (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<QuoteRecord?> GetQuoteByNumberAsync(string quoteNumber)
        {
            if (string.IsNullOrWhiteSpace(quoteNumber))
                return null;

            // Parameterized query to prevent SQL injection
            var sql = $@"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSONum AS sQuoteNumber,
       h.dtSODate AS dtDate, h.dTotal, h.bQuote,
       COALESCE((
           SELECT SUM(d.dAmtOwg) FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
           ORDER BY d.lId DESC LIMIT 1
       ), 0) AS dBalance
FROM tsalordr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.bQuote = 1 AND h.sSONum = '{SanitizeSql(quoteNumber.Trim())}'
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            return new QuoteRecord
            {
                Id = GetText(row, "lId"),
                CustomerId = GetText(row, "lCusId"),
                CustomerName = GetText(row, "sCustomerName"),
                QuoteNumber = GetText(row, "sQuoteNumber"),
                Date = GetNullableDate(row, "dtDate"),
                Total = GetDecimal(row, "dTotal"),
                Balance = GetDecimal(row, "dBalance"),
                Status = "Quote"
            };
        }

        /// <summary>
        /// Check if a quote number already exists in Sage.
        /// Used for collision detection during quote number generation.
        /// Uses tsalordr table with bQuote=1 flag (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<bool> QuoteNumberExistsAsync(string quoteNumber)
        {
            if (string.IsNullOrWhiteSpace(quoteNumber))
                return false;

            var sql = $@"
SELECT COUNT(*) as cnt 
FROM tsalordr 
WHERE bQuote = 1 AND sSONum = '{SanitizeSql(quoteNumber.Trim())}'";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return false;

            return Convert.ToInt32(table.Rows[0]["cnt"]) > 0;
        }

        /// <summary>
        /// Check if a quote with the given number exists (for reconciliation).
        /// Uses tsalordr table with bQuote=1 flag (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<bool> QuoteExistsAsync(string quoteNumber)
        {
            return await QuoteNumberExistsAsync(quoteNumber);
        }

        // Helper methods for safe data access
        private static string GetText(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        }

        private static decimal GetDecimal(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? 0m : Convert.ToDecimal(value);
        }

        private static DateTime? GetNullableDate(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
        }

        private static string SanitizeSql(string value)
        {
            return value.Replace("'", "''");
        }
    }

    /// <summary>
    /// Sage 50 Canada invoice repository.
    /// 
    /// IMPORTANT: This class contains Sage 50 internal schema details (table names,
    /// column names, flags like nTranType). These are implementation details specific
    /// to Sage 50 Canada 2026 and may vary by Sage version.
    /// 
    /// All Sage-version-specific queries are centralized here so supporting a future
    /// Sage version requires changing only this adapter, not the application.
    /// 
    /// This repository uses read-only SQL queries via the SDK database utility.
    /// No writes are performed - all writes go through the official Sage SDK.
    /// </summary>
    public sealed class SageInvoiceRepository : ISageInvoiceRepository
    {
        private readonly SageService _sageService;

        public SageInvoiceRepository(SageService sageService)
        {
            _sageService = sageService ?? throw new ArgumentNullException(nameof(sageService));
        }

        /// <summary>
        /// Get all invoices from Sage.
        /// Uses tCusTr table with nTranType=0 (Sage 50 Canada 2026 internal schema).
        ///
        /// Balance calculation: previously this took the latest tCusTrDt.dAmtOwg
        /// row scoped to the invoice's own lId. That is very likely wrong -
        /// GetARAgingAsync (below) proves this codebase's own understanding of
        /// the schema is that receipts and credit memos are separate tCusTr
        /// transactions (nTranType 1/2), each with their own lId and their own
        /// tCusTrDt distribution rows - not child rows of the original invoice.
        /// A query scoped to `tCusTrDt WHERE lCusTrId = h.lId` (h = the invoice)
        /// would therefore only ever see the invoice's OWN distribution lines,
        /// never a later payment, so "the latest dAmtOwg row" for a paid-off
        /// invoice would still reflect its original amount forever. That
        /// matches a real, reported bug: a dashboard total far larger than
        /// actual receivables, entirely attributable to paid invoices still
        /// contributing their full historical amount.
        ///
        /// Fixed to use the same transaction-netting basis as GetARAgingAsync
        /// (SUM invoices minus SUM receipts/credits for the customer, treating
        /// nTranType 0 as a charge and 1/2 as a reduction) instead of a second,
        /// inconsistent mechanism. Because Sage's schema does not give this
        /// connector a proven way to attribute a specific receipt to a specific
        /// invoice, remaining balance is allocated FIFO across the customer's
        /// invoices, oldest first - the standard approach when explicit
        /// per-invoice payment application isn't available. This guarantees
        /// SUM(all invoice balances for a customer) reconciles exactly to that
        /// customer's net total from GetARAgingAsync's own proven formula; it
        /// does not guess a new balance figure independently.
        ///
        /// UNVERIFIED AGAINST THE REAL SDK/DATA: this has not been run against
        /// a live Sage 50 company. Confirm SUM(balance) here reconciles to
        /// Sage 50's own A/R Aging / customer receivables report before
        /// trusting it, and if Sage's schema does expose a genuine
        /// receipt-to-invoice application link, prefer that over FIFO.
        /// </summary>
        public async Task<List<InvoiceRecord>> GetInvoicesAsync()
        {
            // Sage internal schema: tCusTr stores customer transactions.
            // nTranType=0 indicates an invoice, nTranType=1=order, nTranType=2=quote (legacy).
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSource,
       h.dtDate, h.dPreTaxAmt, h.sRef,
       GREATEST(0, LEAST(h.dPreTaxAmt,
           (SELECT COALESCE(SUM(h2.dPreTaxAmt), 0) FROM tCusTr h2
            WHERE h2.lCusId = h.lCusId AND h2.nTranType = 0 AND h2.lId <= h.lId)
           -
           (SELECT COALESCE(SUM(h3.dPreTaxAmt), 0) FROM tCusTr h3
            WHERE h3.lCusId = h.lCusId AND h3.nTranType IN (1, 2))
       )) AS dBalance
FROM tCusTr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.nTranType = 0
ORDER BY h.dtDate DESC, h.lId DESC";

            var invoices = new List<InvoiceRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            // Due dates are fetched separately and defensively: dtDueDate's
            // exact column name is UNVERIFIED against the real schema (it was
            // never selected at all before this fix, which is why every
            // invoice fell into a single "Current" aging bucket regardless of
            // actual age). If the column name is wrong, invoice sync must
            // still succeed with totals/balances intact - aging degrades to
            // "unknown due date" rather than the whole sync failing.
            var dueDates = new Dictionary<string, DateTime?>();
            try
            {
                const string dueDateSql = "SELECT lId, dtDueDate FROM tCusTr WHERE nTranType = 0";
                var dueDateTable = await Task.Run(() => _sageService.Select(dueDateSql));
                foreach (DataRow row in dueDateTable.Rows)
                    dueDates[GetText(row, "lId")] = GetNullableDate(row, "dtDueDate");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read invoice due dates - confirm the real due-date column name against the installed Sage 50 SDK/schema. Aging will show every open invoice as unaged until this is fixed.");
            }

            foreach (DataRow row in table.Rows)
            {
                decimal total = GetDecimal(row, "dPreTaxAmt");
                decimal balance = GetDecimal(row, "dBalance");
                var id = GetText(row, "lId");
                invoices.Add(new InvoiceRecord
                {
                    Id = id,
                    CustomerId = GetText(row, "lCusId"),
                    CustomerName = GetText(row, "sCustomerName"),
                    InvoiceNumber = GetText(row, "sSource"),
                    Reference = GetText(row, "sRef"),
                    Date = GetNullableDate(row, "dtDate"),
                    DueDate = dueDates.TryGetValue(id, out var due) ? due : null,
                    PreTaxTotal = total,
                    Total = total,
                    Balance = balance,
                    Status = balance == 0m ? "Paid" : "Unpaid"
                });
            }

            return invoices;
        }

        /// <summary>
        /// Get AR aging report showing outstanding balances by customer.
        /// Uses tCusTr table with nTranType=0 (Sage 50 Canada 2026 internal schema).
        /// Uses MySQL-compatible DATE_SUB functions.
        /// </summary>
        public async Task<List<ARAgingRecord>> GetARAgingAsync()
        {
            // Sage internal schema: tCusTr stores customer transactions.
            // nTranType=0 indicates an invoice.
            // MySQL DATE_SUB used for date calculations (Sage 50 Canada 2026 uses MySQL).
            const string sql = @"
SELECT c.lId, c.sName, c.sCntcName, c.sPhone1,
       SUM(CASE WHEN h.nTranType = 0 THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) THEN -h.dPreTaxAmt
                ELSE 0 END) AS dTotal,
       SUM(CASE WHEN h.nTranType = 0 AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) THEN -h.dPreTaxAmt
                ELSE 0 END) AS dCurrent,
       SUM(CASE WHEN h.nTranType = 0 AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 30 DAY) AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 60 DAY) THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 30 DAY) AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 60 DAY) THEN -h.dPreTaxAmt
                ELSE 0 END) AS d30_60,
       SUM(CASE WHEN h.nTranType = 0 AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 60 DAY) AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 90 DAY) THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 60 DAY) AND h.dtDate >= DATE_SUB(CURDATE(), INTERVAL 90 DAY) THEN -h.dPreTaxAmt
                ELSE 0 END) AS d60_90,
       SUM(CASE WHEN h.nTranType = 0 AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 90 DAY) THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) AND h.dtDate < DATE_SUB(CURDATE(), INTERVAL 90 DAY) THEN -h.dPreTaxAmt
                ELSE 0 END) AS dOver90
FROM tCustomr c
LEFT JOIN tCusTr h ON h.lCusId = c.lId
GROUP BY c.lId, c.sName, c.sCntcName, c.sPhone1
HAVING dTotal <> 0
ORDER BY c.sName";

            var report = new List<ARAgingRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                report.Add(new ARAgingRecord
                {
                    CustomerId = GetText(row, "lId"),
                    CustomerName = GetText(row, "sName"),
                    Contact = GetText(row, "sCntcName"),
                    Phone = GetText(row, "sPhone1"),
                    Total = GetDecimal(row, "dTotal"),
                    Current = GetDecimal(row, "dCurrent"),
                    Days30_60 = GetDecimal(row, "d30_60"),
                    Days60_90 = GetDecimal(row, "d60_90"),
                    Over90 = GetDecimal(row, "dOver90")
                });
            }

            return report;
        }

        /// <summary>
        /// Get a summary of invoice totals by paid/unpaid status.
        /// Uses tCusTr table with nTranType=0 (Sage 50 Canada 2026 internal schema).
        /// Paid/unpaid is derived from the same FIFO transaction-netting
        /// balance as GetInvoicesAsync (see its remarks) rather than the
        /// previous tCusTrDt.dAmtOwg lookup, so the two never disagree about
        /// whether a given invoice is paid.
        /// </summary>
        public async Task<InvoiceSummaryRecord> GetInvoiceSummaryAsync()
        {
            // Sage internal schema: tCusTr stores customer transactions.
            // nTranType=0 indicates an invoice.
            const string sql = @"
SELECT
    COUNT(*) AS nCount,
    SUM(dPreTaxAmt) AS dTotal,
    SUM(CASE WHEN GREATEST(0, LEAST(h.dPreTaxAmt,
                    (SELECT COALESCE(SUM(h2.dPreTaxAmt), 0) FROM tCusTr h2
                     WHERE h2.lCusId = h.lCusId AND h2.nTranType = 0 AND h2.lId <= h.lId)
                    -
                    (SELECT COALESCE(SUM(h3.dPreTaxAmt), 0) FROM tCusTr h3
                     WHERE h3.lCusId = h.lCusId AND h3.nTranType IN (1, 2))
                )) = 0 THEN 1 ELSE 0 END) AS nPaid,
    SUM(CASE WHEN GREATEST(0, LEAST(h.dPreTaxAmt,
                    (SELECT COALESCE(SUM(h2.dPreTaxAmt), 0) FROM tCusTr h2
                     WHERE h2.lCusId = h.lCusId AND h2.nTranType = 0 AND h2.lId <= h.lId)
                    -
                    (SELECT COALESCE(SUM(h3.dPreTaxAmt), 0) FROM tCusTr h3
                     WHERE h3.lCusId = h.lCusId AND h3.nTranType IN (1, 2))
                )) <> 0 THEN 1 ELSE 0 END) AS nUnpaid
FROM tCusTr h
WHERE h.nTranType = 0";

            var table = await Task.Run(() => _sageService.Select(sql));
            var row = table.Rows[0];

            return new InvoiceSummaryRecord
            {
                TotalCount = Convert.ToInt32(row["nCount"]),
                TotalAmount = GetDecimal(row, "dTotal"),
                PaidCount = Convert.ToInt32(row["nPaid"]),
                UnpaidCount = Convert.ToInt32(row["nUnpaid"])
            };
        }

        /// <summary>
        /// Check if an invoice with the given Sage record id (lId) exists.
        /// Used for reconciliation after an interruption between Post() and
        /// the ledger recording delivery - never for anything user-supplied.
        /// Uses tCusTr table with nTranType=0 (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<bool> InvoiceExistsAsync(string invoiceId)
        {
            if (string.IsNullOrWhiteSpace(invoiceId) || !long.TryParse(invoiceId.Trim(), out var numericId))
                return false;

            var sql = $@"
SELECT COUNT(*) as cnt
FROM tCusTr
WHERE nTranType = 0 AND lId = {numericId}";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return false;

            return Convert.ToInt32(table.Rows[0]["cnt"]) > 0;
        }

        /// <summary>
        /// Find the most recently created invoice for a customer. Called
        /// immediately after Post() (still holding no further SDK lock) to
        /// read back the Sage-assigned invoice identifier, the same way
        /// SageCustomerRepository.FindLastCreatedCustomerAsync retrieves a
        /// new customer's id.
        /// Uses tCusTr table with nTranType=0 (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<InvoiceRecord?> FindLastCreatedInvoiceAsync(string customerName)
        {
            if (string.IsNullOrWhiteSpace(customerName))
                return null;

            var sql = $@"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSource,
       h.dtDate, h.dPreTaxAmt, h.sRef,
       COALESCE((
           SELECT d.dAmtOwg FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
           ORDER BY d.lId DESC LIMIT 1
       ), 0) AS dBalance
FROM tCusTr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.nTranType = 0 AND c.sName = '{SanitizeSql(customerName.Trim())}'
ORDER BY h.lId DESC
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            decimal total = GetDecimal(row, "dPreTaxAmt");
            decimal balance = GetDecimal(row, "dBalance");
            return new InvoiceRecord
            {
                Id = GetText(row, "lId"),
                CustomerId = GetText(row, "lCusId"),
                CustomerName = GetText(row, "sCustomerName"),
                InvoiceNumber = GetText(row, "sSource"),
                Reference = GetText(row, "sRef"),
                Date = GetNullableDate(row, "dtDate"),
                PreTaxTotal = total,
                Total = total,
                Balance = balance,
                Status = balance == 0m ? "Paid" : "Unpaid"
            };
        }

        // Helper methods for safe data access
        private static string GetText(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        }

        private static decimal GetDecimal(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? 0m : Convert.ToDecimal(value);
        }

        private static DateTime? GetNullableDate(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
        }

        private static string SanitizeSql(string value)
        {
            return value.Replace("'", "''");
        }
    }
}
