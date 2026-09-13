using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace SageBridge.Connector
{
    /// <summary>
    /// Represents a customer record returned from Sage.
    /// This is a normalized DTO - Sage internal schema details are hidden.
    /// </summary>
    public class CustomerRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Contact { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string AlternatePhone { get; set; }
        public string Fax { get; set; }
        public string Address { get; set; }
        public decimal CreditLimit { get; set; }
        // Balance is the home/reporting-currency customer A/R balance.
        public decimal Balance { get; set; }
        public decimal HomeCurrencyBalance { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Represents a product/item record returned from Sage.
    /// This is a normalized DTO - Sage internal schema details are hidden.
    /// </summary>
    public class ProductRecord
    {
        public string Id { get; set; }
        public string SKU { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
        public decimal Price { get; set; }
        public bool IsService { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Represents company information from Sage.
    /// This is a normalized DTO - Sage internal schema details are hidden.
    /// </summary>
    public class CompanyRecord
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string Phone { get; set; }
        public string AlternatePhone { get; set; }
        public string Fax { get; set; }
        public string Email { get; set; }
        public DateTime? FiscalStart { get; set; }
        public DateTime? FiscalEnd { get; set; }
        public DateTime? SessionDate { get; set; }
    }

    /// <summary>
    /// Repository interface for Sage customer operations.
    /// All Sage internal schema details (table names, column names, flags)
    /// are implementation details of the concrete repository.
    /// </summary>
    public interface ISageCustomerRepository
    {
        Task<List<CustomerRecord>> GetCustomersAsync();
        Task<CustomerRecord?> GetCustomerByIdAsync(string id);
        Task<CustomerRecord?> GetCustomerByNameAsync(string name);
        Task<string?> ResolveCustomerNameAsync(string customerId);
        Task<CustomerRecord?> FindLastCreatedCustomerAsync(string name);
    }

    /// <summary>
    /// Repository interface for Sage product operations.
    /// All Sage internal schema details (table names, column names, flags)
    /// are implementation details of the concrete repository.
    /// </summary>
    public interface ISageProductRepository
    {
        Task<List<ProductRecord>> GetProductsAsync();
        Task<ProductRecord?> GetProductBySkuAsync(string sku);
    }

    /// <summary>
    /// Repository interface for Sage company operations.
    /// All Sage internal schema details (table names, column names, flags)
    /// are implementation details of the concrete repository.
    /// </summary>
    public interface ISageCompanyRepository
    {
        Task<CompanyRecord> GetCompanyInfoAsync();
        Task<string> ReadCompanyNameAsync();
        Task<DataTable> GetTableSchemaAsync(string tableName);
    }
}
