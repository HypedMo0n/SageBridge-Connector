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
        /// Sage's authoritative gross outstanding balance is the sum of every
        /// tCusTrDt.dAmount row owned by the invoice through lCusTrId. This
        /// includes the original gross amount, tax, receipt applications, and
        /// credit adjustments. Sub-cent residuals are normalized to zero.
        /// </summary>
        public async Task<List<InvoiceRecord>> GetInvoicesAsync()
        {
            // Sage internal schema: tCusTr stores customer transaction headers;
            // tCusTrDt rows identify their owning invoice through lCusTrId.
            const string sql = @"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSource,
       h.dtDate, h.dPreTaxAmt, h.sRef, h.lCurrncyId,
       COALESCE((
           SELECT SUM(CASE WHEN d.nTranType = 0 THEN d.dAmount ELSE 0 END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dTransactionTotal,
       COALESCE((
           SELECT SUM(CASE WHEN d.nTranType = 0
                           THEN CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END
                           ELSE 0 END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dHomeTotal,
       COALESCE((
           SELECT SUM(d.dAmount)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dTransactionBalance,
       COALESCE((
           SELECT SUM(CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dHomeBalance
FROM tCusTr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.nTranType = 0
ORDER BY h.dtDate DESC, h.lId DESC";

            var invoices = new List<InvoiceRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                decimal preTaxTotal = GetDecimal(row, "dPreTaxAmt");
                decimal transactionTotal = GetDecimal(row, "dTransactionTotal");
                decimal homeTotal = GetDecimal(row, "dHomeTotal");
                decimal transactionBalance = GetDecimal(row, "dTransactionBalance");
                decimal homeBalance = GetDecimal(row, "dHomeBalance");
                if (Math.Abs(transactionBalance) < 0.005m)
                    transactionBalance = 0m;
                if (Math.Abs(homeBalance) < 0.005m)
                    homeBalance = 0m;
                var id = GetText(row, "lId");
                invoices.Add(new InvoiceRecord
                {
                    Id = id,
                    CustomerId = GetText(row, "lCusId"),
                    CustomerName = GetText(row, "sCustomerName"),
                    InvoiceNumber = GetText(row, "sSource"),
                    Reference = GetText(row, "sRef"),
                    Date = GetNullableDate(row, "dtDate"),
                    DueDate = null,
                    PreTaxTotal = preTaxTotal,
                    Total = homeTotal,
                    Balance = homeBalance,
                    TransactionCurrencyTotal = transactionTotal,
                    TransactionCurrencyBalance = transactionBalance,
                    HomeCurrencyTotal = homeTotal,
                    HomeCurrencyBalance = homeBalance,
                    Status = homeBalance <= 0m ? "Paid" : "Unpaid"
                });
            }

            return invoices;
        }

        /// <summary>
        /// Get A/R aging from each invoice's authoritative gross outstanding balance.
        /// Uses MySQL-compatible DATE_SUB functions.
        /// </summary>
        public async Task<List<ARAgingRecord>> GetARAgingAsync()
        {
            // A/R reporting includes open invoices plus credit/debit notes.
            // Amounts are normalized to Sage's home/reporting currency.
            const string sql = @"
SELECT c.lId, c.sName, c.sCntcName, c.sPhone1,
       COALESCE(SUM(CASE WHEN (i.nTranType = 0 AND i.dHomeBalance >= 0.005)
                              OR i.nTranType IN (8, 9)
                         THEN i.dHomeBalance ELSE 0 END), 0) AS dTotal,
       COALESCE(SUM(CASE WHEN ((i.nTranType = 0 AND i.dHomeBalance >= 0.005)
                              OR i.nTranType IN (8, 9))
                              AND i.dtDate >= DATE_SUB(CURDATE(), INTERVAL 30 DAY)
                         THEN i.dHomeBalance ELSE 0 END), 0) AS dCurrent,
       COALESCE(SUM(CASE WHEN ((i.nTranType = 0 AND i.dHomeBalance >= 0.005)
                              OR i.nTranType IN (8, 9))
                              AND i.dtDate < DATE_SUB(CURDATE(), INTERVAL 30 DAY)
                              AND i.dtDate >= DATE_SUB(CURDATE(), INTERVAL 60 DAY)
                         THEN i.dHomeBalance ELSE 0 END), 0) AS d30_60,
       COALESCE(SUM(CASE WHEN ((i.nTranType = 0 AND i.dHomeBalance >= 0.005)
                              OR i.nTranType IN (8, 9))
                              AND i.dtDate < DATE_SUB(CURDATE(), INTERVAL 60 DAY)
                              AND i.dtDate >= DATE_SUB(CURDATE(), INTERVAL 90 DAY)
                         THEN i.dHomeBalance ELSE 0 END), 0) AS d60_90,
       COALESCE(SUM(CASE WHEN ((i.nTranType = 0 AND i.dHomeBalance >= 0.005)
                              OR i.nTranType IN (8, 9))
                              AND i.dtDate < DATE_SUB(CURDATE(), INTERVAL 90 DAY)
                         THEN i.dHomeBalance ELSE 0 END), 0) AS dOver90
FROM tCustomr c
LEFT JOIN (
    SELECT h.lId, h.lCusId, h.dtDate, h.nTranType,
           COALESCE(SUM(d.dAmount), 0) AS dTransactionBalance,
           COALESCE(SUM(CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END), 0) AS dHomeBalance
    FROM tCusTr h
    LEFT JOIN tCusTrDt d ON d.lCusTrId = h.lId
    WHERE h.nTranType IN (0, 8, 9)
    GROUP BY h.lId, h.lCusId, h.dtDate, h.nTranType
) i ON i.lCusId = c.lId
GROUP BY c.lId, c.sName, c.sCntcName, c.sPhone1
HAVING dTotal >= 0.005
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
        /// Get invoice counts and report-level A/R totals. Invoice counts remain
        /// type 0 only; credit/debit notes contribute to the A/R amount separately.
        /// TotalAmount is normalized to Sage's home/reporting currency.
        /// </summary>
        public async Task<InvoiceSummaryRecord> GetInvoiceSummaryAsync()
        {
            const string sql = @"
SELECT
    SUM(CASE WHEN i.nTranType = 0 THEN 1 ELSE 0 END) AS nCount,
    COALESCE(SUM(CASE WHEN i.nTranType = 0 AND i.dHomeBalance >= 0.005
                           THEN i.dHomeBalance
                      WHEN i.nTranType IN (8, 9) THEN i.dHomeBalance
                      ELSE 0 END), 0) AS dHomeTotal,
    COALESCE(SUM(CASE WHEN i.nTranType = 0 AND i.dTransactionBalance >= 0.005
                           THEN i.dTransactionBalance
                      WHEN i.nTranType IN (8, 9) THEN i.dTransactionBalance
                      ELSE 0 END), 0) AS dTransactionTotal,
    SUM(CASE WHEN i.nTranType = 0 AND (ABS(COALESCE(i.dHomeBalance, 0)) < 0.005
                                      OR i.dHomeBalance < 0)
             THEN 1 ELSE 0 END) AS nPaid,
    SUM(CASE WHEN i.nTranType = 0 AND i.dHomeBalance >= 0.005
             THEN 1 ELSE 0 END) AS nUnpaid
FROM (
    SELECT h.lId, h.nTranType,
           COALESCE(SUM(d.dAmount), 0) AS dTransactionBalance,
           COALESCE(SUM(CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END), 0) AS dHomeBalance
    FROM tCusTr h
    LEFT JOIN tCusTrDt d ON d.lCusTrId = h.lId
    WHERE h.nTranType IN (0, 8, 9)
    GROUP BY h.lId, h.nTranType
) i";

            var table = await Task.Run(() => _sageService.Select(sql));
            var row = table.Rows[0];

            return new InvoiceSummaryRecord
            {
                TotalCount = Convert.ToInt32(row["nCount"]),
                TotalAmount = GetDecimal(row, "dHomeTotal"),
                TransactionCurrencyTotal = GetDecimal(row, "dTransactionTotal"),
                HomeCurrencyTotal = GetDecimal(row, "dHomeTotal"),
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
       h.dtDate, h.dPreTaxAmt, h.sRef, h.lCurrncyId,
       COALESCE((
           SELECT SUM(CASE WHEN d.nTranType = 0 THEN d.dAmount ELSE 0 END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dTransactionTotal,
       COALESCE((
           SELECT SUM(CASE WHEN d.nTranType = 0
                           THEN CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END
                           ELSE 0 END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dHomeTotal,
       COALESCE((
           SELECT SUM(d.dAmount)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dTransactionBalance,
       COALESCE((
           SELECT SUM(CASE WHEN h.lCurrncyId = 1 THEN d.dAmount ELSE d.dAmtHm END)
           FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
       ), 0) AS dHomeBalance
FROM tCusTr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.nTranType = 0 AND c.sName = '{SanitizeSql(customerName.Trim())}'
ORDER BY h.lId DESC
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            decimal preTaxTotal = GetDecimal(row, "dPreTaxAmt");
            decimal transactionTotal = GetDecimal(row, "dTransactionTotal");
            decimal homeTotal = GetDecimal(row, "dHomeTotal");
            decimal transactionBalance = GetDecimal(row, "dTransactionBalance");
            decimal homeBalance = GetDecimal(row, "dHomeBalance");
            if (Math.Abs(transactionBalance) < 0.005m)
                transactionBalance = 0m;
            if (Math.Abs(homeBalance) < 0.005m)
                homeBalance = 0m;
            return new InvoiceRecord
            {
                Id = GetText(row, "lId"),
                CustomerId = GetText(row, "lCusId"),
                CustomerName = GetText(row, "sCustomerName"),
                InvoiceNumber = GetText(row, "sSource"),
                Reference = GetText(row, "sRef"),
                Date = GetNullableDate(row, "dtDate"),
                DueDate = null,
                PreTaxTotal = preTaxTotal,
                Total = homeTotal,
                Balance = homeBalance,
                TransactionCurrencyTotal = transactionTotal,
                TransactionCurrencyBalance = transactionBalance,
                HomeCurrencyTotal = homeTotal,
                HomeCurrencyBalance = homeBalance,
                Status = homeBalance <= 0m ? "Paid" : "Unpaid"
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
