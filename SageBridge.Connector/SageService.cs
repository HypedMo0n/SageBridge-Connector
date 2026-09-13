using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using SimplySDK;
using SimplySDK.ReceivableModule;
using SimplySDK.Support;

namespace SageBridge.Connector
{
    /// <summary>
    /// Sage 50 Canada SDK integration. The Canadian SDK uses SimplySDK and
    /// Sage_SA.SDK.dll; PeachtreeSession belongs to the separate Sage 50 US SDK.
    /// 
    /// This service provides a clean separation between:
    /// - SDK-based operations (writes, lookups via official SDK)
    /// - Repository-based operations (read-only SQL via SDK database utility)
    /// 
    /// All writes go through the official Sage SDK. Read operations that require
    /// SQL queries are delegated to repository implementations which encapsulate
    /// Sage internal schema details.
    /// </summary>
    public sealed class SageService : IDisposable
    {
        private const string ThirdPartyApplicationName = "SageBridge Connector";
        private const string ThirdPartyApplicationCode = "SGBRDG"; // SDK maximum: 6 characters
        private const short ThirdPartyApplicationVersion = 1;

        private readonly ConnectorConfig _config;
        private readonly object _sdkLock = new object();
        private bool _disposed;

        // Repository instances for SQL-based read operations
        private readonly ISageQuoteRepository _quoteRepository;
        private readonly ISageInvoiceRepository _invoiceRepository;
        private readonly ISageCustomerRepository _customerRepository;
        private readonly ISageProductRepository _productRepository;
        private readonly ISageCompanyRepository _companyRepository;

        public string CompanyName { get; private set; } = "Not connected";
        public bool IsConnected { get; private set; }

        public SageService(ConnectorConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            // Repositories are initialized but will only work after ConnectAsync()
            _quoteRepository = new SageQuoteRepository(this);
            _invoiceRepository = new SageInvoiceRepository(this);
            _customerRepository = new SageCustomerRepository(this);
            _productRepository = new SageProductRepository(this);
            _companyRepository = new SageCompanyRepository(this);
        }

        public Task<bool> ConnectAsync()
        {
            lock (_sdkLock)
            {
                ThrowIfDisposed();

                if (IsConnected)
                    return Task.FromResult(true);

                if (string.IsNullOrWhiteSpace(_config.SageCompanyPath))
                {
                    Log.Error("SageCompanyPath is empty. Set it to the full path of the Sage company .SAI file in config.json.");
                    return Task.FromResult(false);
                }

                if (!File.Exists(_config.SageCompanyPath))
                {
                    Log.Error("Sage company file does not exist: {CompanyPath}", _config.SageCompanyPath);
                    return Task.FromResult(false);
                }

                try
                {
                    Log.Information("Opening Sage 50 Canada company {CompanyPath} as {Username}...",
                        _config.SageCompanyPath, _config.SageUsername);

                    SDKInstanceManager.SDKResult result;
                    bool opened = SDKInstanceManager.Instance.OpenDatabase(
                        _config.SageCompanyPath,
                        _config.SageUsername,
                        _config.SagePassword,
                        _config.SageMultiUser,
                        ThirdPartyApplicationName,
                        ThirdPartyApplicationCode,
                        ThirdPartyApplicationVersion,
                        out result);

                    if (!opened)
                    {
                        Log.Error("Sage 50 Canada rejected the connection. SDK result: {SdkResult}", result);
                        return Task.FromResult(false);
                    }

                    IsConnected = true;
                    CompanyName = ReadCompanyName();
                    Log.Information("Connected to Sage 50 Canada company: {CompanyName}", CompanyName);
                    return Task.FromResult(true);
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    CompanyName = "Not connected";
                    Log.Error(ex, "Failed to connect to Sage 50 Canada");
                    return Task.FromResult(false);
                }
            }
        }

        /// <summary>
        /// Get company information from Sage.
        /// Delegates to the company repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object> GetCompanyInfoAsync()
        {
            var company = await _companyRepository.GetCompanyInfoAsync();
            return new
            {
                company.Name,
                company.Address,
                company.Phone,
                company.AlternatePhone,
                company.Fax,
                company.Email,
                company.FiscalStart,
                company.FiscalEnd,
                company.SessionDate
            };
        }

        /// <summary>
        /// Get all customers from Sage.
        /// Delegates to the customer repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<List<object>> GetCustomersAsync()
        {
            var customers = await _customerRepository.GetCustomersAsync();
            return customers.Select(c => (object)new
            {
                c.Id,
                c.Name,
                c.Contact,
                c.Email,
                c.Phone,
                c.AlternatePhone,
                c.Fax,
                c.Address,
                c.CreditLimit,
                c.Balance,
                c.Status
            }).ToList();
        }

        /// <summary>
        /// Get a single customer by ID.
        /// Delegates to the customer repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object?> GetCustomerByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Customer id is required.", nameof(id));

            var customer = await _customerRepository.GetCustomerByIdAsync(id);
            if (customer == null)
                return null;

            return new
            {
                customer.Id,
                customer.Name,
                customer.Contact,
                customer.Email,
                customer.Phone,
                customer.AlternatePhone,
                customer.Fax,
                customer.Address,
                customer.CreditLimit,
                customer.Balance,
                customer.Status
            };
        }

        /// <summary>
        /// Get all invoices from Sage.
        /// Delegates to the invoice repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<List<object>> GetInvoicesAsync()
        {
            var invoices = await _invoiceRepository.GetInvoicesAsync();
            return invoices.Select(i => (object)new
            {
                i.Id,
                i.CustomerId,
                i.CustomerName,
                i.InvoiceNumber,
                i.Reference,
                i.Date,
                i.DueDate,
                i.PreTaxTotal,
                i.Total,
                i.Balance,
                i.Status
            }).ToList();
        }

        public async Task<DataTable> DebugSchema(string table)
        {
            return await _companyRepository.GetTableSchemaAsync(table);
        }

        /// <summary>
        /// Get all products from Sage.
        /// Delegates to the product repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<List<object>> GetProductsAsync()
        {
            var products = await _productRepository.GetProductsAsync();
            return products.Select(p => (object)new
            {
                p.Id,
                p.SKU,
                p.Name,
                p.Unit,
                p.Price,
                p.IsService,
                p.Status
            }).ToList();
        }

        /// <summary>
        /// Get a single product by SKU.
        /// Delegates to the product repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object?> GetProductBySkuAsync(string sku)
        {
            if (string.IsNullOrWhiteSpace(sku))
                return null;

            var product = await _productRepository.GetProductBySkuAsync(sku);
            if (product == null)
                return null;

            return new
            {
                product.Id,
                product.SKU,
                product.Name,
                product.Unit,
                product.Price,
                product.IsService,
                product.Status
            };
        }

        /// <summary>
        /// Get all sales quotes from Sage.
        /// Delegates to the quote repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<List<object>> GetQuotesAsync()
        {
            var quotes = await _quoteRepository.GetQuotesAsync();
            return quotes.Select(q => (object)new
            {
                q.Id,
                q.CustomerId,
                q.CustomerName,
                q.QuoteNumber,
                q.Date,
                q.Total,
                q.Balance,
                q.Status
            }).ToList();
        }

        /// <summary>
        /// Get a single quote by its quote number.
        /// Delegates to the quote repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object?> GetQuoteByNumberAsync(string quoteNumber)
        {
            var quote = await _quoteRepository.GetQuoteByNumberAsync(quoteNumber);
            if (quote == null)
                return null;

            return new
            {
                quote.Id,
                quote.CustomerId,
                quote.CustomerName,
                quote.QuoteNumber,
                quote.Date,
                quote.Total,
                quote.Balance,
                quote.Status
            };
        }

        /// <summary>
        /// Generate a collision-checked quote number in format QT-YYYYMMDD-NNN.
        /// Synchronous version - creates a task and waits for completion.
        /// </summary>
        public string GenerateQuoteNumber()
        {
            return Task.Run(() => GenerateQuoteNumberAsync()).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of quote number generation.
        /// </summary>
        public async Task<string> GenerateQuoteNumberAsync()
        {
            var today = DateTime.Today;
            string prefix = $"QT-{today:yyyyMMdd}-";

            for (int n = 1; n <= 999; n++)
            {
                string candidate = $"{prefix}{n:D3}";
                bool exists = await _quoteRepository.QuoteNumberExistsAsync(candidate);
                if (!exists)
                    return candidate;
            }

            throw new InvalidOperationException(
                $"Unable to generate a unique quote number for {today:yyyy-MM-dd}. " +
                "All sequence numbers 001-999 are occupied.");
        }

        /// <summary>
        /// Resolve a Sage record ID (customer lId as a string) to the exact
        /// Sage customer name. Returns null when the ID does not match a customer.
        /// Delegates to the customer repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<string?> ResolveCustomerNameAsync(string customerId)
        {
            return await _customerRepository.ResolveCustomerNameAsync(customerId);
        }

        /// <summary>
        /// Create a sales quote in Sage following the installed SDK sample
        /// (ProcessOrdersQuotesExample, sales quote section) exactly:
        /// OpenSalesJournal; SelectTransType(2); SelectAPARLedger(exact name);
        /// set OrderQuoteNum; SetShipDate(GetJournalDate()); for each 1-based line
        /// call SetItemNumber, SetOrdered, SetPrice; Post(); always CloseSalesJournal
        /// in finally.
        ///
        /// Does not create description-only lines, does not set tax manually,
        /// and does not set revenue accounts unless the SDK forces it.
        ///
        /// Returns the generated quote number as sageId on success.
        /// </summary>
        public async Task<object> CreateQuoteAsync(
            string customerId,
            List<QuoteLineRequest> lines,
            string quoteNumber)
        {
            if (string.IsNullOrWhiteSpace(quoteNumber))
                throw new ArgumentException("Quote number is required.", nameof(quoteNumber));

            if (lines == null || lines.Count == 0)
                throw new ArgumentException("At least one quote line is required.", nameof(lines));

            // Resolve the customer ID to the exact Sage customer name. Never trust
            // a supplied name; the mobile client may send a display name that differs
            // from the Sage record.
            string? customerName = await ResolveCustomerNameAsync(customerId);
            if (string.IsNullOrWhiteSpace(customerName))
                throw new ArgumentException(
                    $"No Sage customer found for id '{customerId}'.", nameof(customerId));

            Log.Information(
                "Creating sales quote {QuoteNumber} for Sage customer {CustomerId} ({CustomerName})",
                quoteNumber, customerId, customerName);

            SalesJournal salJourn = null;
            try
            {
                salJourn = SDKInstanceManager.Instance.OpenSalesJournal();
                try
                {
                    salJourn.SelectTransType(2); // quote
                }
                catch (SimplyNoAccessException ex)
                {
                    throw new InvalidOperationException(
                        "Sales quotes are not enabled in this Sage company. " +
                        "Enable them in Sage 50 before creating quotes.", ex);
                }

                salJourn.SelectAPARLedger(customerName);
                salJourn.OrderQuoteNum = quoteNumber;
                salJourn.SetShipDate(salJourn.GetJournalDate());

                int lineIndex = 0;
                foreach (var line in lines)
                {
                    lineIndex++;
                    salJourn.SetItemNumber(line.Sku, lineIndex);
                    salJourn.SetOrdered((double)line.Quantity, lineIndex);
                    salJourn.SetPrice((double)line.UnitPrice, lineIndex);
                }

                bool posted = salJourn.Post();
                if (!posted)
                    throw new InvalidOperationException(
                        $"Sage accepted the quote setup but Post() returned false for quote '{quoteNumber}'.");

                Log.Information(
                    "Sales quote posted in Sage 50: {QuoteNumber} (customer {CustomerName})",
                    quoteNumber, customerName);

                // Return a simple payload whose Id is the generated quote number.
                // This matches the existing customer.create shape where Id is the
                // Sage record identifier.
                return new
                {
                    Id = quoteNumber,
                    CustomerId = customerId,
                    CustomerName = customerName,
                    LineCount = lines.Count,
                    Number = quoteNumber
                };
            }
            finally
            {
                if (salJourn != null)
                    SDKInstanceManager.Instance.CloseSalesJournal();
            }
        }

        /// <summary>
        /// Create a narrow sales invoice in Sage: a customer plus one or
        /// more existing items, following the same SalesJournal SDK pattern
        /// as CreateQuoteAsync (OpenSalesJournal; SelectTransType;
        /// SelectAPARLedger; SetShipDate(GetJournalDate()); per-line
        /// SetItemNumber/SetQuantity/SetPrice; Post(); CloseSalesJournal in
        /// finally). Does not set tax manually and does not set revenue
        /// accounts unless the SDK forces it - identical scope to
        /// CreateQuoteAsync's write path.
        ///
        /// UNVERIFIED AGAINST THE REAL SDK: SelectTransType(0) is used here
        /// as the standard SimplySDK "Invoice/Sale" transaction type,
        /// consistent with the 0=Invoice/1=Order/2=Quote sequence this file
        /// already relies on for quotes (CreateQuoteAsync uses
        /// SelectTransType(2)). This has not been compiled or run against
        /// the installed Sage 50 Canada SDK. Confirm the exact transaction
        /// type value - and that no other required field differs for a
        /// plain invoice versus a quote/order - on the Windows connector
        /// machine with the real SDK before relying on this in production.
        ///
        /// Unlike a quote, an invoice number is not pre-assigned by the
        /// connector: Sage assigns it on Post(). Once Post() returns true
        /// the write is durable and MUST NOT be retried, so - exactly like
        /// CreateCustomerAsync - any failure reading the identifier back
        /// degrades to an empty Id rather than throwing (throwing here would
        /// be misread upstream as "the write failed" and trigger a retry,
        /// which would double-post the invoice).
        /// </summary>
        public async Task<object> CreateInvoiceAsync(
            string customerId,
            List<QuoteLineRequest> lines)
        {
            if (lines == null || lines.Count == 0)
                throw new ArgumentException("At least one invoice line is required.", nameof(lines));

            string? customerName = await ResolveCustomerNameAsync(customerId);
            if (string.IsNullOrWhiteSpace(customerName))
                throw new ArgumentException(
                    $"No Sage customer found for id '{customerId}'.", nameof(customerId));

            Log.Information(
                "Creating sales invoice for Sage customer {CustomerId} ({CustomerName})",
                customerId, customerName);

            SalesJournal salJourn = null;
            try
            {
                salJourn = SDKInstanceManager.Instance.OpenSalesJournal();
                try
                {
                    salJourn.SelectTransType(0); // invoice/sale - see UNVERIFIED note above
                }
                catch (SimplyNoAccessException ex)
                {
                    throw new InvalidOperationException(
                        "Sales invoices are not enabled in this Sage company. " +
                        "Enable them in Sage 50 before creating invoices.", ex);
                }

                salJourn.SelectAPARLedger(customerName);
                salJourn.SetShipDate(salJourn.GetJournalDate());

                int lineIndex = 0;
                foreach (var line in lines)
                {
                    lineIndex++;
                    salJourn.SetItemNumber(line.Sku, lineIndex);
                    salJourn.SetQuantity((double)line.Quantity, lineIndex);
                    salJourn.SetPrice((double)line.UnitPrice, lineIndex);
                }

                bool posted = salJourn.Post();
                if (!posted)
                    throw new InvalidOperationException(
                        $"Sage accepted the invoice setup but Post() returned false for customer '{customerName}'.");

                Log.Information(
                    "Sales invoice posted in Sage 50 for customer {CustomerName}",
                    customerName);
            }
            finally
            {
                if (salJourn != null)
                    SDKInstanceManager.Instance.CloseSalesJournal();
            }

            // Post() has committed the write; nothing below this point may
            // throw out of this method. Best-effort read-back only.
            string id = string.Empty;
            string invoiceNumber = string.Empty;
            try
            {
                var created = await _invoiceRepository.FindLastCreatedInvoiceAsync(customerName);
                if (created != null)
                {
                    id = created.Id;
                    invoiceNumber = created.InvoiceNumber;
                }
                else
                {
                    Log.Warning(
                        "Invoice for {CustomerName} posted in Sage but could not be read back immediately after Post(). " +
                        "Verify manually in Sage 50 - do NOT retry this idempotency key, the write already succeeded.",
                        customerName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Invoice for {CustomerName} posted in Sage but the read-back query failed. " +
                    "Verify manually in Sage 50 - do NOT retry this idempotency key, the write already succeeded.",
                    customerName);
            }

            return new
            {
                Id = id,
                CustomerId = customerId,
                CustomerName = customerName,
                InvoiceNumber = invoiceNumber,
                LineCount = lines.Count
            };
        }

        /// <summary>
        /// Reconcile an uncertain invoice.create by checking whether an
        /// invoice with the given Sage record id already exists.
        /// Synchronous version for backward compatibility.
        /// </summary>
        public bool InvoiceExistsInSage(string invoiceId)
        {
            return Task.Run(() => InvoiceExistsInSageAsync(invoiceId)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of invoice existence check.
        /// Delegates to the invoice repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<bool> InvoiceExistsInSageAsync(string invoiceId)
        {
            return await _invoiceRepository.InvoiceExistsAsync(invoiceId);
        }

        /// <summary>
        /// Reconcile an uncertain quote by checking whether a quote with the
        /// given number already exists in Sage.
        /// Synchronous version for backward compatibility.
        /// </summary>
        public bool QuoteExistsInSage(string quoteNumber)
        {
            return Task.Run(() => QuoteExistsInSageAsync(quoteNumber)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of quote existence check.
        /// Delegates to the quote repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<bool> QuoteExistsInSageAsync(string quoteNumber)
        {
            return await _quoteRepository.QuoteExistsAsync(quoteNumber);
        }

        /// <summary>
        /// Get a simple AR aging report from Sage.
        /// Delegates to the invoice repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<List<object>> GetARAgingReportAsync()
        {
            var report = await _invoiceRepository.GetARAgingAsync();
            return report.Select(r => (object)new
            {
                r.CustomerId,
                r.CustomerName,
                r.Contact,
                r.Phone,
                r.Total,
                r.Current,
                r.Days30_60,
                r.Days60_90,
                r.Over90
            }).ToList();
        }

        /// <summary>
        /// Get a summary of invoice totals by status.
        /// Delegates to the invoice repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object> GetInvoiceSummaryAsync()
        {
            var summary = await _invoiceRepository.GetInvoiceSummaryAsync();
            return new
            {
                summary.TotalCount,
                summary.TotalAmount,
                summary.PaidCount,
                summary.UnpaidCount
            };
        }

        /// <summary>
        /// Get a customer by name.
        /// Delegates to the customer repository which encapsulates Sage internal schema details.
        /// </summary>
        public async Task<object?> GetCustomerByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var customer = await _customerRepository.GetCustomerByNameAsync(name);
            if (customer == null)
                return null;

            return new
            {
                customer.Id,
                customer.Name,
                customer.Contact,
                customer.Email,
                customer.Phone,
                customer.AlternatePhone,
                customer.Fax,
                customer.Address,
                customer.CreditLimit,
                customer.Balance,
                customer.Status
            };
        }

        public async Task<object> CreateCustomerAsync(string name, string email, string phone)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Customer name is required.", nameof(name));

            string id;
            lock (_sdkLock)
            {
                EnsureConnected();
                CustomerLedger? ledger = null;
                try
                {
                    ledger = SDKInstanceManager.Instance.OpenCustomerLedger();
                    ledger.InitializeNew();
                    ledger.Name = name.Trim();
                    ledger.Email = email?.Trim() ?? string.Empty;
                    ledger.Phone1 = phone?.Trim() ?? string.Empty;

                    if (!ledger.Save())
                        throw new InvalidOperationException("The Sage SDK did not save the customer.");
                }
                finally
                {
                    if (ledger != null)
                        SDKInstanceManager.Instance.CloseCustomerLedger();
                }
            }

            // Retrieve the new customer ID via the repository (outside SDK lock)
            var savedCustomer = await _customerRepository.FindLastCreatedCustomerAsync(name.Trim());
            id = savedCustomer?.Id ?? string.Empty;
            Log.Information("Created Sage customer {CustomerName} ({CustomerId})", name, id);

            return new
            {
                Id = id,
                Name = name.Trim(),
                Email = email?.Trim() ?? string.Empty,
                Phone = phone?.Trim() ?? string.Empty,
                Status = "Active"
            };
        }

        private string ReadCompanyName()
        {
            return Task.Run(() => _companyRepository.ReadCompanyNameAsync()).GetAwaiter().GetResult();
        }

        public DataTable Select(string sql)
        {
            var utility = new SDKDatabaseUtility();
            utility.RunSelectQuery(sql);
            DataSet dataSet = utility.GetDataSetFromLastSelectQuery();
            if (dataSet.Tables.Count == 0)
                throw new InvalidOperationException("The Sage query returned no result table.");
            return dataSet.Tables[0];
        }

        private static object MapCustomer(DataRow row)
        {
            return new
            {
                Id = Text(row, "lId"),
                Name = Text(row, "sName"),
                Contact = Text(row, "sCntcName"),
                Email = Text(row, "sEmail"),
                Phone = Text(row, "sPhone1"),
                AlternatePhone = Text(row, "sPhone2"),
                Fax = Text(row, "sFax"),
                Address = JoinAddress(row, "sStreet1", "sStreet2", "sCity", "sProvState", "sPostalZip", "sCountry"),
                CreditLimit = DecimalValue(row, "dCrLimit"),
                Balance = DecimalValue(row, "dBalance"),
                Status = BooleanValue(row, "bInactive") ? "Inactive" : "Active"
            };
        }

        private static string Text(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        }

        private static decimal DecimalValue(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? 0m : Convert.ToDecimal(value);
        }

        private static bool BooleanValue(DataRow row, string column)
        {
            object value = row[column];
            return value != DBNull.Value && Convert.ToBoolean(value);
        }

        private static DateTime? NullableDate(DataRow row, string column)
        {
            object value = row[column];
            return value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
        }

        private static string JoinAddress(DataRow row, params string[] columns)
        {
            var values = new List<string>();
            foreach (string column in columns)
            {
                string value = Text(row, column).Trim();
                if (value.Length > 0)
                    values.Add(value);
            }
            return string.Join(", ", values);
        }

        private static string SqlLiteral(string value) => value.Replace("'", "''");

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Sage 50 Canada.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SageService));
        }

        public void Dispose()
        {
            lock (_sdkLock)
            {
                if (_disposed)
                    return;

                if (IsConnected)
                    SDKInstanceManager.Instance.CloseDatabase();

                IsConnected = false;
                CompanyName = "Not connected";
                _disposed = true;
            }
        }
    }
}
