using System;
using System.Threading.Tasks;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Console-based pairing wizard for the connector.
    /// Guides the user through entering a pairing code and stores the credential securely.
    /// 
    /// The connector does NOT supply tenant/company IDs - these are assigned server-side
    /// based on the pairing code.
    /// </summary>
    public class PairingWizard
    {
        private readonly string _workerUrl;

        public PairingWizard(string workerUrl)
        {
            _workerUrl = workerUrl;
        }

        /// <summary>
        /// Runs the pairing wizard interactively.
        /// Returns true if pairing succeeded.
        /// </summary>
        public async Task<bool> RunAsync()
        {
            Console.WriteLine();
            Console.WriteLine("╔════════════════════════════════════════╗");
            Console.WriteLine("║   SageBridge Connector Pairing         ║");
            Console.WriteLine("╚════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("A pairing code is required to connect this instance.");
            Console.WriteLine("Generate one from your SageBridge dashboard.");
            Console.WriteLine();
            Console.Write("Enter pairing code: ");

            var code = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                Console.WriteLine("Pairing cancelled.");
                return false;
            }

            try
            {
                var client = new PairingClient(_workerUrl);
                var result = await client.ValidatePairingCodeAsync(code);

                // Store the credential securely
                CredentialManager.StoreCredential(result.ConnectorId, result.Credential);

                Console.WriteLine();
                Console.WriteLine("✓ Pairing successful!");
                Console.WriteLine($"  Connector ID: {result.ConnectorId}");
                Console.WriteLine($"  Organization: {result.OrganizationId}");
                Console.WriteLine($"  Company: {result.CompanyId}");
                Console.WriteLine($"  Paired at: {result.PairedAt:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine();
                Console.WriteLine("You can now start the connector normally.");
                return true;
            }
            catch (PairingException ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Pairing failed: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Please check the code and try again.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Unexpected error: {ex.Message}");
                Log.Error(ex, "Pairing failed with unexpected error");
                return false;
            }
        }
    }
}
