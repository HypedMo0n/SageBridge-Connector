using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SageBridge.Connector
{
    /// <summary>
    /// Represents a quote record returned from Sage.
    /// This is a normalized DTO - Sage internal schema details are hidden.
    /// </summary>
    public class QuoteRecord
    {
        public string Id { get; set; }
        public string CustomerId { get; set; }
        public string CustomerName { get; set; }
        public string QuoteNumber { get; set; }
        public DateTime? Date { get; set; }
        public decimal Total { get; set; }
    }

    /// <summary>
    /// Represents an invoice record returned from Sage.
    /// This is a normalized DTO - Sage internal schema details are hidden.
    /// </summary>
    public class InvoiceRecord
    {
        public string Id { get; set; }
        public string CustomerId { get; set; }
        public string CustomerName { get; set; }
        public string InvoiceNumber { get; set; }
        public string Reference { get; set; }
        public DateTime? Date { get; set; }
        public decimal PreTaxTotal { get; set; }
        // Total and Balance are home/reporting-currency values.
        public decimal Total { get; set; }
        public decimal Balance { get; set; }
        public decimal TransactionCurrencyTotal { get; set; }
        public decimal TransactionCurrencyBalance { get; set; }
        public decimal HomeCurrencyTotal { get; set; }
        public decimal HomeCurrencyBalance { get; set; }
    }

    /// <summary>
    /// Represents safe aggregations of authoritative invoice balances.
    /// </summary>
    public class InvoiceSummaryRecord
    {
        public int TotalCount { get; set; }
        // TotalAmount is the home/reporting-currency A/R total.
        public decimal TotalAmount { get; set; }
        public decimal TransactionCurrencyTotal { get; set; }
        public decimal HomeCurrencyTotal { get; set; }
    }

    /// <summary>
    /// Repository interface for Sage quote operations.
    /// All Sage internal schema details (table names, column names, flags)
    /// are implementation details of the concrete repository.
    /// </summary>
    public interface ISageQuoteRepository
    {
        /// <summary>
        /// Get all quotes from Sage.
        /// </summary>
        Task<List<QuoteRecord>> GetQuotesAsync();

        /// <summary>
        /// Get a single quote by its quote number.
        /// </summary>
        Task<QuoteRecord?> GetQuoteByNumberAsync(string quoteNumber);

        /// <summary>
        /// Check if a quote number already exists in Sage.
        /// Used for collision detection during quote number generation.
        /// </summary>
        Task<bool> QuoteNumberExistsAsync(string quoteNumber);

        /// <summary>
        /// Check if a quote with the given number exists (for reconciliation).
        /// </summary>
        Task<bool> QuoteExistsAsync(string quoteNumber);
    }

    /// <summary>
    /// Repository interface for Sage invoice operations.
    /// All Sage internal schema details (table names, column names, flags)
    /// are implementation details of the concrete repository.
    /// </summary>
    public interface ISageInvoiceRepository
    {
        /// <summary>
        /// Get all invoices from Sage.
        /// </summary>
        Task<List<InvoiceRecord>> GetInvoicesAsync();

        /// <summary>
        /// Get safe aggregations of authoritative invoice balances.
        /// </summary>
        Task<InvoiceSummaryRecord> GetInvoiceSummaryAsync();

        /// <summary>
        /// Check if an invoice with the given number exists (for crash-safety
        /// reconciliation, mirroring ISageQuoteRepository.QuoteExistsAsync).
        /// </summary>
        Task<bool> InvoiceExistsAsync(string invoiceNumber);

        /// <summary>
        /// Find the most recently created invoice for a customer. Used
        /// immediately after Post() to read back the Sage-assigned invoice
        /// number, the same way FindLastCreatedCustomerAsync retrieves a new
        /// customer's assigned id.
        /// </summary>
        Task<InvoiceRecord?> FindLastCreatedInvoiceAsync(string customerName);
    }
}
