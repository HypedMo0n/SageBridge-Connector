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
        public decimal Balance { get; set; }
        public string Status { get; set; }
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
        public DateTime? DueDate { get; set; }
        public decimal PreTaxTotal { get; set; }
        public decimal Total { get; set; }
        public decimal Balance { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Represents an AR aging bucket for a customer.
    /// </summary>
    public class ARAgingRecord
    {
        public string CustomerId { get; set; }
        public string CustomerName { get; set; }
        public string Contact { get; set; }
        public string Phone { get; set; }
        public decimal Total { get; set; }
        public decimal Current { get; set; }
        public decimal Days30_60 { get; set; }
        public decimal Days60_90 { get; set; }
        public decimal Over90 { get; set; }
    }

    /// <summary>
    /// Represents a summary of invoice totals by status.
    /// </summary>
    public class InvoiceSummaryRecord
    {
        public int TotalCount { get; set; }
        public decimal TotalAmount { get; set; }
        public int PaidCount { get; set; }
        public int UnpaidCount { get; set; }
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
        /// Get AR aging report showing outstanding balances by customer.
        /// </summary>
        Task<List<ARAgingRecord>> GetARAgingAsync();

        /// <summary>
        /// Get a summary of invoice totals by paid/unpaid status.
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
