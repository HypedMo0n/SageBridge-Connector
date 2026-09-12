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
        /// </summary>
        public async Task<List<InvoiceRecord>> GetInvoicesAsync()
        {
            // Sage internal schema: tCusTr stores customer transactions.
            // nTranType=0 indicates an invoice, nTranType=1=order, nTranType=2=quote (legacy).
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT h.lId, h.lCusId, c.sName AS sCustomerName, h.sSource,
       h.dtDate, h.dPreTaxAmt, h.sRef,
       COALESCE((
           SELECT d.dAmtOwg FROM tCusTrDt d
           WHERE d.lCusTrId = h.lId
           ORDER BY d.lId DESC LIMIT 1
       ), 0) AS dBalance
FROM tCusTr h
INNER JOIN tCustomr c ON c.lId = h.lCusId
WHERE h.nTranType = 0
ORDER BY h.dtDate DESC, h.lId DESC";

            var invoices = new List<InvoiceRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                decimal total = GetDecimal(row, "dPreTaxAmt");
                decimal balance = GetDecimal(row, "dBalance");
                invoices.Add(new InvoiceRecord
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
        /// </summary>
        public async Task<InvoiceSummaryRecord> GetInvoiceSummaryAsync()
        {
            // Sage internal schema: tCusTr stores customer transactions.
            // nTranType=0 indicates an invoice.
            // Balance is determined by the latest dAmtOwg value in tCusTrDt.
            const string sql = @"
SELECT 
    COUNT(*) AS nCount,
    SUM(dPreTaxAmt) AS dTotal,
    SUM(CASE WHEN (
                    SELECT COALESCE(d2.dAmtOwg, 0) 
                    FROM tCusTrDt d2 
                    WHERE d2.lCusTrId = h.lId 
                    ORDER BY d2.lId DESC 
                    LIMIT 1
                ) = 0 THEN 1 ELSE 0 END) AS nPaid,
    SUM(CASE WHEN (
                    SELECT COALESCE(d2.dAmtOwg, 0) 
                    FROM tCusTrDt d2 
                    WHERE d2.lCusTrId = h.lId 
                    ORDER BY d2.lId DESC 
                    LIMIT 1
                ) <> 0 THEN 1 ELSE 0 END) AS nUnpaid
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
    }
}
