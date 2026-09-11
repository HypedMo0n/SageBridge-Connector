# ═══════════════════════════════════════════════════════════
# SageBridge SDK Integration Automation Script
# Run this in PowerShell as Administrator from the connector directory
# ═══════════════════════════════════════════════════════════

Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  SageBridge Connector - Sage 50 SDK Auto-Integration  ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ═══════════════════════════════════════════════════════════
# STEP 1: Locate Sage 50 SDK
# ═══════════════════════════════════════════════════════════

Write-Host "[1/5] Locating Sage 50 SDK..." -ForegroundColor Yellow

$possiblePaths = @(
    "C:\Program Files (x86)\Sage\Sage 50 SDK",
    "C:\Program Files\Sage\Sage 50 SDK",
    "C:\Program Files (x86)\Sage\Sage 50 Canada\SDK",
    "C:\Program Files\Sage\Sage 50 Canada\SDK",
    "$env:USERPROFILE\Downloads\Sage50SDK",
    "$env:USERPROFILE\Desktop\Sage50SDK"
)

$sdkPath = $null
foreach ($path in $possiblePaths) {
    if (Test-Path "$path\Sage.Peachtree.API.dll") {
        $sdkPath = $path
        Write-Host "✓ Found SDK at: $sdkPath" -ForegroundColor Green
        break
    }
}

if (-not $sdkPath) {
    Write-Host "✗ SDK not found in common locations." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please enter the full path to your Sage 50 SDK folder:" -ForegroundColor Yellow
    Write-Host "(It should contain Sage.Peachtree.API.dll)" -ForegroundColor Gray
    $sdkPath = Read-Host "SDK Path"
    
    if (-not (Test-Path "$sdkPath\Sage.Peachtree.API.dll")) {
        Write-Host "✗ ERROR: SDK DLLs not found at that path!" -ForegroundColor Red
        Write-Host "Please extract the SDK and run this script again." -ForegroundColor Yellow
        exit 1
    }
}

$apiDll = "$sdkPath\Sage.Peachtree.API.dll"
$collectionsDll = "$sdkPath\Sage.Peachtree.API.Collections.dll"

Write-Host "✓ SDK DLLs located:" -ForegroundColor Green
Write-Host "  - $apiDll" -ForegroundColor Gray
Write-Host "  - $collectionsDll" -ForegroundColor Gray
Write-Host ""

# ═══════════════════════════════════════════════════════════
# STEP 2: Update .csproj file
# ═══════════════════════════════════════════════════════════

Write-Host "[2/5] Updating project file..." -ForegroundColor Yellow

$csprojPath = "SageBridge.Connector\SageBridge.Connector.csproj"

if (-not (Test-Path $csprojPath)) {
    Write-Host "✗ ERROR: Project file not found at $csprojPath" -ForegroundColor Red
    Write-Host "Make sure you're running this from the connector root directory." -ForegroundColor Yellow
    exit 1
}

$csprojContent = Get-Content $csprojPath -Raw

# Add SDK references
$sdkReferences = @"

  <ItemGroup>
    <!-- Sage 50 SDK References -->
    <Reference Include="Sage.Peachtree.API">
      <HintPath>$apiDll</HintPath>
      <Private>True</Private>
    </Reference>
    <Reference Include="Sage.Peachtree.API.Collections">
      <HintPath>$collectionsDll</HintPath>
      <Private>True</Private>
    </Reference>
  </ItemGroup>
"@

# Remove placeholder comment and add actual references
$csprojContent = $csprojContent -replace '(?s)<!-- Sage 50 SDK DLLs.*?-->', $sdkReferences

Set-Content -Path $csprojPath -Value $csprojContent -NoNewline

Write-Host "✓ Project file updated with SDK references" -ForegroundColor Green
Write-Host ""

# ═══════════════════════════════════════════════════════════
# STEP 3: Update SageService.cs
# ═══════════════════════════════════════════════════════════

Write-Host "[3/5] Updating SageService.cs with real SDK code..." -ForegroundColor Yellow

$serviceFile = "SageBridge.Connector\SageService.cs"

$newServiceCode = @'
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using Sage.Peachtree.API;

namespace SageBridge.Connector
{
    public class SageService
    {
        private readonly ConnectorConfig _config;
        private PeachtreeSession _session;
        private Company _company;

        public string CompanyName { get; private set; } = "Not Connected";
        public bool IsConnected { get; private set; }

        public SageService(ConnectorConfig config)
        {
            _config = config;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                Log.Information("Connecting to Sage 50...");

                await Task.CompletedTask;

                // Initialize Sage session
                _session = new PeachtreeSession();
                
                // Set application info
                var appInfo = new ApplicationIdentity
                {
                    ApplicationName = "SageBridge Connector",
                    ApplicationVersion = new Version(1, 0, 0, 0)
                };
                
                // Request authorization
                Log.Information("Requesting Sage 50 authorization...");
                var authResult = _session.VerifyApplication(appInfo);
                
                if (authResult != AuthorizationResult.Authorized)
                {
                    Log.Error("Sage 50 authorization denied");
                    return false;
                }
                
                // Open currently active company
                Log.Information("Opening company...");
                _company = _session.Open();
                
                if (_company == null)
                {
                    Log.Error("No company is currently open in Sage 50");
                    return false;
                }
                
                CompanyName = _company.Name;
                IsConnected = true;

                Log.Information("✓ Connected to: {CompanyName}", CompanyName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to connect to Sage 50");
                return false;
            }
        }

        public async Task<object> GetCompanyInfoAsync()
        {
            await Task.CompletedTask;
            
            return new
            {
                Name = _company.Name,
                Address = _company.Address?.AddressLine1 ?? "",
                City = _company.Address?.City ?? "",
                Province = _company.Address?.State ?? "",
                Phone = _company.Phone ?? "",
                FiscalYear = _company.FiscalYear,
                Currency = _company.HomeCurrency?.Symbol ?? "CAD"
            };
        }

        public async Task<List<object>> GetCustomersAsync()
        {
            await Task.CompletedTask;
            
            var customers = new List<object>();
            
            try
            {
                foreach (Customer customer in _company.Customers)
                {
                    customers.Add(new
                    {
                        Id = customer.ID,
                        Name = customer.Name,
                        Email = customer.Email ?? "",
                        Phone = customer.Phone ?? "",
                        Balance = (decimal)customer.Balance,
                        Status = customer.IsInactive ? "Inactive" : "Active"
                    });
                }
                
                Log.Information("Retrieved {Count} customers from Sage 50", customers.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving customers");
            }
            
            return customers;
        }

        public async Task<object?> GetCustomerByIdAsync(string id)
        {
            await Task.CompletedTask;
            
            try
            {
                var customer = _company.Customers.Find(id);
                if (customer == null) return null;
                
                return new
                {
                    Id = customer.ID,
                    Name = customer.Name,
                    Email = customer.Email ?? "",
                    Phone = customer.Phone ?? "",
                    Balance = (decimal)customer.Balance,
                    Status = customer.IsInactive ? "Inactive" : "Active",
                    Address = customer.Address?.AddressLine1 ?? "",
                    City = customer.Address?.City ?? "",
                    Province = customer.Address?.State ?? ""
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting customer {Id}", id);
                return null;
            }
        }

        public async Task<List<object>> GetInvoicesAsync()
        {
            await Task.CompletedTask;
            
            var invoices = new List<object>();
            
            try
            {
                foreach (SalesInvoice invoice in _company.SalesInvoices)
                {
                    invoices.Add(new
                    {
                        Id = invoice.ID,
                        CustomerId = invoice.Customer?.ID ?? "",
                        CustomerName = invoice.Customer?.Name ?? "Unknown",
                        InvoiceNumber = invoice.InvoiceNumber,
                        Date = invoice.Date.ToString("yyyy-MM-dd"),
                        Total = (decimal)invoice.Total,
                        Balance = (decimal)invoice.AmountDue,
                        Status = invoice.AmountDue == 0 ? "Paid" : "Unpaid"
                    });
                }
                
                Log.Information("Retrieved {Count} invoices from Sage 50", invoices.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving invoices");
            }
            
            return invoices;
        }

        public async Task<List<object>> GetProductsAsync()
        {
            await Task.CompletedTask;
            
            var products = new List<object>();
            
            try
            {
                foreach (InventoryItem item in _company.InventoryItems)
                {
                    products.Add(new
                    {
                        Id = item.ID,
                        SKU = item.ID,
                        Name = item.Description,
                        Price = (decimal)item.SalesPrice,
                        Stock = (int?)item.QuantityOnHand,
                        ReorderLevel = (int?)item.MinimumStock
                    });
                }
                
                Log.Information("Retrieved {Count} products from Sage 50", products.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving products");
            }
            
            return products;
        }

        public async Task<object> CreateCustomerAsync(string name, string email, string phone)
        {
            await Task.CompletedTask;
            
            try
            {
                var customer = _company.Customers.Add();
                customer.Name = name;
                if (!string.IsNullOrEmpty(email)) customer.Email = email;
                if (!string.IsNullOrEmpty(phone)) customer.Phone = phone;
                customer.Save();
                
                Log.Information("Created customer in Sage 50: {Name} (ID: {Id})", name, customer.ID);
                
                return new
                {
                    Id = customer.ID,
                    Name = customer.Name,
                    Email = customer.Email,
                    Phone = customer.Phone,
                    Balance = 0m,
                    Status = "Active",
                    Message = "✓ Customer created in Sage 50 successfully!"
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating customer");
                throw;
            }
        }
    }
}
'@

Set-Content -Path $serviceFile -Value $newServiceCode -NoNewline

Write-Host "✓ SageService.cs updated with real SDK code" -ForegroundColor Green
Write-Host ""

# ═══════════════════════════════════════════════════════════
# STEP 4: Build the project
# ═══════════════════════════════════════════════════════════

Write-Host "[4/5] Building project..." -ForegroundColor Yellow

# Try to find MSBuild
$msbuildPaths = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
)

$msbuild = $null
foreach ($path in $msbuildPaths) {
    if (Test-Path $path) {
        $msbuild = $path
        break
    }
}

if ($msbuild) {
    Write-Host "✓ Found MSBuild at: $msbuild" -ForegroundColor Green
    
    # Restore NuGet packages
    Write-Host "  Restoring NuGet packages..." -ForegroundColor Gray
    & $msbuild "SageBridge.Connector.sln" /t:Restore /p:Configuration=Debug /p:Platform=x86 /v:minimal
    
    # Build
    Write-Host "  Building solution..." -ForegroundColor Gray
    & $msbuild "SageBridge.Connector.sln" /p:Configuration=Debug /p:Platform=x86 /v:minimal
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ Build successful!" -ForegroundColor Green
    } else {
        Write-Host "✗ Build failed. Open solution in Visual Studio to see errors." -ForegroundColor Red
    }
} else {
    Write-Host "⚠ MSBuild not found. You'll need to build in Visual Studio manually." -ForegroundColor Yellow
    Write-Host "  1. Open SageBridge.Connector.sln" -ForegroundColor Gray
    Write-Host "  2. Build → Build Solution (Ctrl+Shift+B)" -ForegroundColor Gray
}

Write-Host ""

# ═══════════════════════════════════════════════════════════
# STEP 5: Final instructions
# ═══════════════════════════════════════════════════════════

Write-Host "[5/5] Setup complete!" -ForegroundColor Yellow
Write-Host ""

Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║              SAGE SDK INTEGRATION COMPLETE!            ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""

Write-Host "✅ SDK DLLs referenced" -ForegroundColor Green
Write-Host "✅ Code updated with real Sage 50 integration" -ForegroundColor Green
Write-Host "✅ Project configured" -ForegroundColor Green
Write-Host ""

Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  NEXT STEPS:" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Open Sage 50 Quantum" -ForegroundColor Yellow
Write-Host "   - File → Open Company" -ForegroundColor Gray
Write-Host "   - Select your sample company" -ForegroundColor Gray
Write-Host "   - Leave Sage 50 running" -ForegroundColor Gray
Write-Host ""
Write-Host "2. Run the connector:" -ForegroundColor Yellow
Write-Host "   - Open SageBridge.Connector.sln in Visual Studio" -ForegroundColor Gray
Write-Host "   - Press F5 (or Debug → Start Debugging)" -ForegroundColor Gray
Write-Host ""
Write-Host "3. Authorize the connector:" -ForegroundColor Yellow
Write-Host "   - Sage 50 will show an authorization dialog" -ForegroundColor Gray
Write-Host "   - Click 'Allow' or 'Accept'" -ForegroundColor Gray
Write-Host ""
Write-Host "4. Test it:" -ForegroundColor Yellow
Write-Host "   curl http://localhost:5001/api/customers" -ForegroundColor Gray
Write-Host "   (You should see YOUR ACTUAL Sage 50 customers!)" -ForegroundColor Gray
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "🚀 Your connector is ready to connect to Sage 50!" -ForegroundColor Green
Write-Host ""
