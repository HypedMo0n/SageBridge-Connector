using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Hosting;
using Owin;
using System.Web.Http;
using Serilog;
using Newtonsoft.Json;
using System.Net.Http;

namespace SageBridge.Connector
{
    public class ApiServer
    {
        private readonly ConnectorConfig _config;
        private readonly SageService _sageService;
        private IDisposable? _webApp;

        public ApiServer(ConnectorConfig config, SageService sageService)
        {
            _config = config;
            _sageService = sageService;
        }

        public Task StartAsync()
        {
            var baseAddress = $"http://localhost:{_config.ApiPort}/";
            
            _webApp = WebApp.Start<Startup>(baseAddress);
            Startup.SageService = _sageService;
            
            return Task.CompletedTask;
        }

        public void Stop()
        {
            _webApp?.Dispose();
        }
    }

    public class Startup
    {
        public static SageService? SageService { get; set; }

        public void Configuration(IAppBuilder app)
        {
            var config = new HttpConfiguration();
            
            // Enable CORS
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Add("Access-Control-Allow-Origin", new[] { "*" });
                context.Response.Headers.Add("Access-Control-Allow-Methods", new[] { "GET, POST, PUT, DELETE, OPTIONS" });
                context.Response.Headers.Add("Access-Control-Allow-Headers", new[] { "Content-Type, Authorization" });
                
                if (context.Request.Method == "OPTIONS")
                {
                    context.Response.StatusCode = 200;
                    return;
                }
                
                await next();
            });

            // Enable attribute routing
            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "Health",
                routeTemplate: "health",
                defaults: new { controller = "Api", action = "Health" }
            );

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            app.UseWebApi(config);
        }
    }

    public class ApiController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("health")]
        public IHttpActionResult Health()
        {
            return Ok(new
            {
                Status = "healthy",
                Connected = Sage.IsConnected,
                Company = Sage.CompanyName,
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0"
            });
        }
    }

    public class CompanyController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/company")]
        public async Task<IHttpActionResult> Get()
        {
            try
            {
                var company = await Sage.GetCompanyInfoAsync();
                return Ok(company);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting company info");
                return InternalServerError(ex);
            }
        }
    }

    public class CustomersController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/customers")]
        public async Task<IHttpActionResult> GetAll()
        {
            try
            {
                var customers = await Sage.GetCustomersAsync();
                return Ok(new { customers });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting customers");
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("api/customers/{id}")]
        public async Task<IHttpActionResult> GetById(string id)
        {
            try
            {
                var customer = await Sage.GetCustomerByIdAsync(id);
                if (customer == null)
                    return NotFound();
                
                return Ok(customer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting customer {Id}", id);
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("api/customers")]
        public async Task<IHttpActionResult> Create([FromBody] CreateCustomerRequest request)
        {
            try
            {
                var customer = await Sage.CreateCustomerAsync(
                    request.Name,
                    request.Email,
                    request.Phone
                );
                
                return Created($"api/customers/{((dynamic)customer).Id}", customer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating customer");
                return InternalServerError(ex);
            }
        }
    }

    public class InvoicesController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/invoices")]
        public async Task<IHttpActionResult> GetAll()
        {
            try
            {
                var invoices = await Sage.GetInvoicesAsync();
                return Ok(new { invoices });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting invoices");
                return InternalServerError(ex);
            }
        }
    }

    public class ProductsController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/products")]
        public async Task<IHttpActionResult> GetAll()
        {
            try
            {
                var products = await Sage.GetProductsAsync();
                return Ok(new { products });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting products");
                return InternalServerError(ex);
            }
        }
    }

    public class QuotesController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/quotes")]
        public async Task<IHttpActionResult> GetAll()
        {
            try
            {
                var quotes = await Sage.GetQuotesAsync();
                return Ok(new { quotes });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting quotes");
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("api/quotes/{id}")]
        public async Task<IHttpActionResult> GetByNumber(string id)
        {
            try
            {
                var quote = await Sage.GetQuoteByNumberAsync(id);
                if (quote == null)
                    return NotFound();

                return Ok(quote);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting quote {Id}", id);
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("api/quotes")]
        public async Task<IHttpActionResult> Create([FromBody] CreateQuoteRequest request)
        {
            try
            {
                // Forward to Cloudflare for job processing
                using var httpClient = new HttpClient();
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Get the Cloudflare worker URL from config
                var workerUrl = Startup.SageService; // We'll need to pass config
                // For now, return a placeholder - the actual job dispatch happens via Cloudflare Worker
                return Ok(new { message = "Quote creation dispatched", jobId = "pending" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating quote");
                return InternalServerError(ex);
            }
        }
    }

    public class ReportsController : System.Web.Http.ApiController
    {
        private SageService Sage => Startup.SageService!;

        [HttpGet]
        [Route("api/reports/ar-aging")]
        public async Task<IHttpActionResult> ArAging()
        {
            try
            {
                var report = await Sage.GetARAgingReportAsync();
                return Ok(new { report });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting AR aging report");
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("api/reports/invoice-summary")]
        public async Task<IHttpActionResult> InvoiceSummary()
        {
            try
            {
                var summary = await Sage.GetInvoiceSummaryAsync();
                return Ok(summary);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting invoice summary");
                return InternalServerError(ex);
            }
        }
    }

    public class CreateCustomerRequest
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
    }

    public class CreateQuoteRequest
    {
        public string CustomerId { get; set; } = "";
        public List<QuoteLineRequest> Lines { get; set; } = new List<QuoteLineRequest>();
        public string IdempotencyKey { get; set; } = "";
    }
}
