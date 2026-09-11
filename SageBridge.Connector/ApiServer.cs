using System;
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

            // Routes
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

    public class CreateCustomerRequest
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
    }
}
