using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Sage 50 Canada customer repository.
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
    public sealed class SageCustomerRepository : ISageCustomerRepository
    {
        private readonly SageService _sageService;

        public SageCustomerRepository(SageService sageService)
        {
            _sageService = sageService ?? throw new ArgumentNullException(nameof(sageService));
        }

        /// <summary>
        /// Get all customers from Sage.
        /// Uses tCustomr table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<List<CustomerRecord>> GetCustomersAsync()
        {
            // Sage internal schema: tCustomr stores customer records.
            // tCusTr stores customer transactions.
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
       c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
       c.sFax, c.sEmail, c.dCrLimit, c.bInactive,
       COALESCE(SUM(
           CASE WHEN h.nTranType = 0 THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) THEN -h.dPreTaxAmt
                ELSE 0
           END
       ), 0) AS dBalance
FROM tCustomr c
LEFT JOIN tCusTr h ON h.lCusId = c.lId
GROUP BY c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
         c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
         c.sFax, c.sEmail, c.dCrLimit, c.bInactive
ORDER BY c.sName";

            var customers = new List<CustomerRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                customers.Add(new CustomerRecord
                {
                    Id = GetText(row, "lId"),
                    Name = GetText(row, "sName"),
                    Contact = GetText(row, "sCntcName"),
                    Email = GetText(row, "sEmail"),
                    Phone = GetText(row, "sPhone1"),
                    AlternatePhone = GetText(row, "sPhone2"),
                    Fax = GetText(row, "sFax"),
                    Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvState", "sPostalZip", "sCountry"),
                    CreditLimit = GetDecimal(row, "dCrLimit"),
                    Balance = GetDecimal(row, "dBalance"),
                    Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
                });
            }

            return customers;
        }

        /// <summary>
        /// Get a single customer by ID.
        /// Uses tCustomr table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<CustomerRecord?> GetCustomerByIdAsync(string id)
        {
            if (!long.TryParse(id, out long customerId))
                return null;

            var sql = $@"
SELECT c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
       c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
       c.sFax, c.sEmail, c.dCrLimit, c.bInactive,
       COALESCE(SUM(
           CASE WHEN h.nTranType = 0 THEN h.dPreTaxAmt
                WHEN h.nTranType IN (1, 2) THEN -h.dPreTaxAmt
                ELSE 0
           END
       ), 0) AS dBalance
FROM tCustomr c
LEFT JOIN tCusTr h ON h.lCusId = c.lId
WHERE c.lId = {customerId}
GROUP BY c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
         c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
         c.sFax, c.sEmail, c.dCrLimit, c.bInactive";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            return new CustomerRecord
            {
                Id = GetText(row, "lId"),
                Name = GetText(row, "sName"),
                Contact = GetText(row, "sCntcName"),
                Email = GetText(row, "sEmail"),
                Phone = GetText(row, "sPhone1"),
                AlternatePhone = GetText(row, "sPhone2"),
                Fax = GetText(row, "sFax"),
                Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvState", "sPostalZip", "sCountry"),
                CreditLimit = GetDecimal(row, "dCrLimit"),
                Balance = GetDecimal(row, "dBalance"),
                Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
            };
        }

        /// <summary>
        /// Get a single customer by exact name.
        /// Uses tCustomr table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<CustomerRecord?> GetCustomerByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var sql = $@"
SELECT c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
       c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
       c.sFax, c.sEmail, c.dCrLimit, c.bInactive
FROM tCustomr c
WHERE c.sName = '{SanitizeSql(name.Trim())}'
ORDER BY c.sName";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            return new CustomerRecord
            {
                Id = GetText(row, "lId"),
                Name = GetText(row, "sName"),
                Contact = GetText(row, "sCntcName"),
                Email = GetText(row, "sEmail"),
                Phone = GetText(row, "sPhone1"),
                AlternatePhone = GetText(row, "sPhone2"),
                Fax = GetText(row, "sFax"),
                Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvState", "sPostalZip", "sCountry"),
                CreditLimit = GetDecimal(row, "dCrLimit"),
                Balance = 0m,
                Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
            };
        }

        /// <summary>
        /// Resolve a customer ID to the exact Sage customer name.
        /// Uses tCustomr table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<string?> ResolveCustomerNameAsync(string customerId)
        {
            if (!long.TryParse(customerId, out long id))
                return null;

            var sql = $@"
SELECT sName
FROM tCustomr
WHERE lId = {id}
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            return GetText(table.Rows[0], "sName");
        }

        /// <summary>
        /// Find the most recently created customer by name.
        /// Used after SDK Save() to retrieve the new customer's ID.
        /// Uses tCustomr table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<CustomerRecord?> FindLastCreatedCustomerAsync(string name)
        {
            var sql = $@"
SELECT c.lId, c.sName, c.sCntcName, c.sStreet1, c.sStreet2, c.sCity,
       c.sProvState, c.sCountry, c.sPostalZip, c.sPhone1, c.sPhone2,
       c.sFax, c.sEmail, c.dCrLimit, c.bInactive
FROM tCustomr c
WHERE c.sName = '{SanitizeSql(name.Trim())}'
ORDER BY lId DESC
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            return new CustomerRecord
            {
                Id = GetText(row, "lId"),
                Name = GetText(row, "sName"),
                Contact = GetText(row, "sCntcName"),
                Email = GetText(row, "sEmail"),
                Phone = GetText(row, "sPhone1"),
                AlternatePhone = GetText(row, "sPhone2"),
                Fax = GetText(row, "sFax"),
                Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvState", "sPostalZip", "sCountry"),
                CreditLimit = GetDecimal(row, "dCrLimit"),
                Balance = 0m,
                Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
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

        private static bool GetBoolean(DataRow row, string column)
        {
            object value = row[column];
            return value != DBNull.Value && Convert.ToBoolean(value);
        }

        private static string JoinAddress(DataRow row, params string[] columns)
        {
            var values = new List<string>();
            foreach (string column in columns)
            {
                string value = GetText(row, column).Trim();
                if (value.Length > 0)
                    values.Add(value);
            }
            return string.Join(", ", values);
        }

        private static string SanitizeSql(string value)
        {
            return value.Replace("'", "''");
        }
    }

    /// <summary>
    /// Sage 50 Canada product repository.
    /// 
    /// IMPORTANT: This class contains Sage 50 internal schema details (table names,
    /// column names). These are implementation details specific to Sage 50 Canada 2026
    /// and may vary by Sage version.
    /// 
    /// This repository uses read-only SQL queries via the SDK database utility.
    /// No writes are performed - all writes go through the official Sage SDK.
    /// </summary>
    public sealed class SageProductRepository : ISageProductRepository
    {
        private readonly SageService _sageService;

        public SageProductRepository(SageService sageService)
        {
            _sageService = sageService ?? throw new ArgumentNullException(nameof(sageService));
        }

        /// <summary>
        /// Get all products from Sage.
        /// Uses tInvent table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<List<ProductRecord>> GetProductsAsync()
        {
            // Sage internal schema: tInvent stores inventory items.
            // tInvPrc stores item prices.
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT i.lId, i.sPartCode, i.sName, i.sStockUnit, i.bService, i.bInactive,
       COALESCE((SELECT MAX(p.dPrice) FROM tInvPrc p WHERE p.lInventId = i.lId), 0) AS dPrice
FROM tInvent i
ORDER BY i.sPartCode, i.sName";

            var products = new List<ProductRecord>();
            var table = await Task.Run(() => _sageService.Select(sql));

            foreach (DataRow row in table.Rows)
            {
                products.Add(new ProductRecord
                {
                    Id = GetText(row, "lId"),
                    SKU = GetText(row, "sPartCode"),
                    Name = GetText(row, "sName"),
                    Unit = GetText(row, "sStockUnit"),
                    Price = GetDecimal(row, "dPrice"),
                    IsService = GetBoolean(row, "bService"),
                    Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
                });
            }

            return products;
        }

        /// <summary>
        /// Get a single product by SKU.
        /// Uses tInvent table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<ProductRecord?> GetProductBySkuAsync(string sku)
        {
            if (string.IsNullOrWhiteSpace(sku))
                return null;

            var sql = $@"
SELECT i.lId, i.sPartCode, i.sName, i.sStockUnit, i.bService, i.bInactive,
       COALESCE((SELECT MAX(p.dPrice) FROM tInvPrc p WHERE p.lInventId = i.lId), 0) AS dPrice
FROM tInvent i
WHERE i.sPartCode = '{SanitizeSql(sku.Trim())}'
LIMIT 1";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                return null;

            var row = table.Rows[0];
            return new ProductRecord
            {
                Id = GetText(row, "lId"),
                SKU = GetText(row, "sPartCode"),
                Name = GetText(row, "sName"),
                Unit = GetText(row, "sStockUnit"),
                Price = GetDecimal(row, "dPrice"),
                IsService = GetBoolean(row, "bService"),
                Status = GetBoolean(row, "bInactive") ? "Inactive" : "Active"
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

        private static bool GetBoolean(DataRow row, string column)
        {
            object value = row[column];
            return value != DBNull.Value && Convert.ToBoolean(value);
        }

        private static string SanitizeSql(string value)
        {
            return value.Replace("'", "''");
        }
    }

    /// <summary>
    /// Sage 50 Canada company repository.
    /// 
    /// IMPORTANT: This class contains Sage 50 internal schema details (table names,
    /// column names). These are implementation details specific to Sage 50 Canada 2026
    /// and may vary by Sage version.
    /// 
    /// This repository uses read-only SQL queries via the SDK database utility.
    /// No writes are performed - all writes go through the official Sage SDK.
    /// </summary>
    public sealed class SageCompanyRepository : ISageCompanyRepository
    {
        private readonly SageService _sageService;

        public SageCompanyRepository(SageService sageService)
        {
            _sageService = sageService ?? throw new ArgumentNullException(nameof(sageService));
        }

        /// <summary>
        /// Get company information from Sage.
        /// Uses tCompany table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<CompanyRecord> GetCompanyInfoAsync()
        {
            // Sage internal schema: tCompany stores company information.
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT sCompName, sStreet1, sStreet2, sCity, sProvStat, sCountry,
       sPostZip, sPhone1, sPhone2, sFax, sEmail, dtSDate, dtCDate, dtFDate
FROM tCompany";

            var table = await Task.Run(() => _sageService.Select(sql));

            if (table.Rows.Count == 0)
                throw new InvalidOperationException("Sage returned no company information.");

            var row = table.Rows[0];
            return new CompanyRecord
            {
                Name = GetText(row, "sCompName"),
                Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvStat", "sPostZip", "sCountry"),
                Phone = GetText(row, "sPhone1"),
                AlternatePhone = GetText(row, "sPhone2"),
                Fax = GetText(row, "sFax"),
                Email = GetText(row, "sEmail"),
                FiscalStart = GetNullableDate(row, "dtSDate"),
                FiscalEnd = GetNullableDate(row, "dtFDate"),
                SessionDate = GetNullableDate(row, "dtCDate")
            };
        }

        // Helper methods for safe data access
        private static string GetText(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        }

        private static DateTime? GetNullableDate(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
        }

        private static string JoinAddress(DataRow row, params string[] columns)
        {
            var values = new List<string>();
            foreach (string column in columns)
            {
                string value = GetText(row, column).Trim();
                if (value.Length > 0)
                    values.Add(value);
            }
            return string.Join(", ", values);
        }
        /// <summary>
        /// Read the company name from Sage.
        /// Uses tCompany table (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<string> ReadCompanyNameAsync()
        {
            // Sage internal schema: tCompany stores company information.
            // This is an implementation detail of Sage 50 Canada 2026.
            const string sql = @"
SELECT sCompName 
FROM tCompany";

            var table = await Task.Run(() => _sageService.Select(sql));

            return table.Rows.Count > 0 ? GetText(table.Rows[0], "sCompName") : string.Empty;
        }

        /// <summary>
        /// Get the schema of any Sage table.
        /// Uses SDK database utility (Sage 50 Canada 2026 internal schema).
        /// </summary>
        public async Task<DataTable> GetTableSchemaAsync(string tableName)
        {
            var sql = $"SELECT * FROM {SanitizeSql(tableName)} LIMIT 1";
            var table = await Task.Run(() => _sageService.Select(sql));
            return table;
        }

        private static string SanitizeSql(string value)
        {
            return value.Replace("'", "''");
        }
    }
}
