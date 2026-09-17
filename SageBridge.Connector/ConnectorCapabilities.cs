using System.Collections.Generic;

namespace SageBridge.Connector
{
    /// <summary>
    /// What THIS connector build can actually do - advertised to the cloud via
    /// heartbeat so the frontend can tell whether a company's active connector
    /// supports a requested write or dataset sync, distinct from whether the
    /// API contract allows it in principle (see GET /api/capabilities).
    ///
    /// Single source of truth: these two lists must mirror exactly what
    /// JobPoller's action dispatch switch and SyncEngine's sync targets
    /// actually implement. If either changes, update this file in the same
    /// change - never let it silently drift into a false advertisement.
    /// </summary>
    public static class ConnectorCapabilities
    {
        /// <summary>Mirrors the case labels in JobPoller.ProcessJob's action switch.</summary>
        public static readonly IReadOnlyList<string> SupportedActions = new[]
        {
            "customer.create",
            "quote.create",
            "invoice.create",
        };

        /// <summary>Mirrors the datasets SyncEngine.SyncCompanyToCloudAsync posts.</summary>
        public static readonly IReadOnlyList<string> SupportedSync = new[]
        {
            "customers",
            "invoices",
            "products",
            "quotes",
            "invoice-summary",
        };
    }
}
