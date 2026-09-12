using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using SimplySDK;
using SimplySDK.ReceivableModule;
using SimplySDK.Support;

namespace SageBridge.Connector
{
    public class QuoteCreateRequest
    {
        public string CustomerId { get; set; } = "";
        public List<QuoteLineRequest> Lines { get; set; } = new List<QuoteLineRequest>();
    }

    public class QuoteLineRequest
    {
        public string Sku { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
