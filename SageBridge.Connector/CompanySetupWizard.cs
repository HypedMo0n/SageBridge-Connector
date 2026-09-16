using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace SageBridge.Connector
{
    /// <summary>
    /// Interactive wizard to configure per-company Sage credentials.
    /// Prompts for Sage path, username, and password for each company profile,
    /// then persists them to config.json.
    /// </summary>
    public class CompanySetupWizard
    {
        private readonly ConnectorConfig _config;

        public CompanySetupWizard(ConnectorConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Returns the actual config.Companies list for editing. If empty but
        /// legacy single-company fields exist, builds a one-entry list from them.
        /// </summary>
        private List<SageCompanyProfile> EnsureCompanyProfiles()
        {
            if (_config.Companies != null && _config.Companies.Count > 0)
                return _config.Companies;

            if (!string.IsNullOrWhiteSpace(_config.SageCompanyPath))
            {
                _config.Companies = new List<SageCompanyProfile>
                {
                    new SageCompanyProfile
                    {
                        CloudCompanyId = _config.CompanyId,
                        SageCompanyPath = _config.SageCompanyPath,
                        SageUsername = _config.SageUsername,
                        SagePassword = _config.SagePassword,
                        SageMultiUser = _config.SageMultiUser,
                        Enabled = true
                    }
                };
                return _config.Companies;
            }

            return new List<SageCompanyProfile>();
        }

        public bool Run()
        {
            Console.WriteLine();
            Console.WriteLine("╔════════════════════════════════════════╗");
            Console.WriteLine("║   SageBridge Company Setup             ║");
            Console.WriteLine("╚════════════════════════════════════════╝");
            Console.WriteLine();

            // Work on the actual config.Companies list (or build one from legacy fields)
            var profiles = EnsureCompanyProfiles();
            if (profiles.Count == 0)
            {
                Console.WriteLine("No company profiles found in config.json.");
                Console.WriteLine("Add a Companies array or set SageCompanyPath/SageUsername/SagePassword.");
                return false;
            }

            Console.WriteLine($"Found {profiles.Count} company profile(s) to configure.");
            Console.WriteLine("For each company, enter the Sage 50 login that works in that company.");
            Console.WriteLine("Leave a field blank to keep the current value.");
            Console.WriteLine();

            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                Console.WriteLine($"── Company {i + 1} of {profiles.Count} ──");
                Console.WriteLine($"  Cloud Company ID: {profile.CloudCompanyId}");
                Console.WriteLine($"  Current path:     {profile.SageCompanyPath}");
                Console.WriteLine($"  Current username: {profile.SageUsername}");
                Console.WriteLine();

                // Sage path
                Console.Write("Sage company path (.SAI) [" + (string.IsNullOrWhiteSpace(profile.SageCompanyPath) ? "none" : profile.SageCompanyPath) + "]: ");
                var path = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"  ⚠ File not found: {path}");
                        Console.Write("  Use anyway? (y/N): ");
                        if ((Console.ReadLine()?.Trim().ToLowerInvariant() ?? "") != "y")
                        {
                            Console.WriteLine("  Skipped.");
                            continue;
                        }
                    }
                    profile.SageCompanyPath = path;
                }

                // Username
                Console.Write("Sage username [" + (string.IsNullOrWhiteSpace(profile.SageUsername) ? "none" : profile.SageUsername) + "]: ");
                var username = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(username))
                    profile.SageUsername = username;

                // Password
                Console.Write("Sage password (hidden, press Enter to keep current): ");
                var password = ReadPassword();
                if (!string.IsNullOrEmpty(password))
                    profile.SagePassword = password;

                Console.WriteLine();
            }

            // Write back
            try
            {
                _config.Save();
                Console.WriteLine("✓ Company credentials saved to config.json.");
                Console.WriteLine("  Restart the connector to apply the new credentials.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to save config: {ex.Message}");
                Log.Error(ex, "Failed to save company setup");
                return false;
            }
        }

        /// <summary>
        /// Reads a password from the console without echoing characters.
        /// </summary>
        private static string ReadPassword()
        {
            var password = "";
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password = password.Substring(0, password.Length - 1);
                        Console.Write("\b \b");
                    }
                }
                else
                {
                    password += key.KeyChar;
                    Console.Write("*");
                }
            }
            return password;
        }
    }
}
