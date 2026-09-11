# 🔧 SageBridge Connector Setup Guide

**Step-by-step instructions to get the connector running**

---

## Phase 1: POC Testing (No Sage SDK - Just Demo Data)

### 1. Install Prerequisites
- [ ] **Visual Studio 2022** (Community edition) - https://visualstudio.microsoft.com/downloads/
  - Select ".NET desktop development" workload
- [ ] **.NET Framework 4.8 Developer Pack** (usually included with VS)

### 2. Open & Build
```bash
# 1. Extract the solution
# 2. Open SageBridge.Connector.sln in Visual Studio

# 3. Set build configuration
Build → Configuration Manager → Active solution platform → x86

# 4. Build the solution
Build → Build Solution (or press Ctrl+Shift+B)
```

### 3. Run the Connector
```bash
# Press F5 or click "Start" button
# You should see console output with:
✓ REST API started on http://localhost:5001
✓ Connected to Sage 50: Demo Construction Ltd. (POC Mode)
```

### 4. Test API Endpoints
Open a new PowerShell window:

```powershell
# Test 1: Health check
curl http://localhost:5001/health

# Expected response:
# {
#   "Status": "healthy",
#   "Connected": true,
#   "Company": "Demo Construction Ltd. (POC Mode)",
#   ...
# }

# Test 2: Get customers
curl http://localhost:5001/api/customers

# Expected: JSON with 4 demo customers

# Test 3: Create customer (POST)
curl -X POST http://localhost:5001/api/customers `
  -H "Content-Type: application/json" `
  -d '{"name":"Mobile Test Customer","email":"test@example.com","phone":"604-555-9999"}'

# Expected: New customer created successfully
```

### 5. Test from Mobile App
Update your mobile app to point to the connector:

```javascript
// In your Next.js app
const API_URL = 'http://localhost:5001/api';

fetch(`${API_URL}/customers`)
  .then(r => r.json())
  .then(data => console.log(data.customers));
```

**✅ If all tests pass, your connector is working in POC mode!**

---

## Phase 2: Connecting to Real Sage 50

### 1. Install Sage 50 Canada
- [ ] Install **Sage 50 Canada 2026.2** (or compatible version)
- [ ] Open a test/demo company file
- [ ] Verify Sage 50 is running

### 2. Install Sage 50 SDK
- [ ] Download **Sage 50 Canada SDK 2026.2** from:
  - https://developer.sage.com/ (requires Sage Developer account)
- [ ] Run the SDK installer
- [ ] Default location: `C:\Program Files (x86)\Sage\Sage 50 SDK\`

### 3. Add SDK DLLs to Project
Edit `SageBridge.Connector.csproj`:

```xml
<ItemGroup>
  <!-- Remove the comments around these lines: -->
  <Reference Include="Sage.Peachtree.API">
    <HintPath>C:\Program Files (x86)\Sage\Sage 50 SDK\Sage.Peachtree.API.dll</HintPath>
    <Private>True</Private>
  </Reference>
  <Reference Include="Sage.Peachtree.API.Collections">
    <HintPath>C:\Program Files (x86)\Sage\Sage 50 SDK\Sage.Peachtree.API.Collections.dll</HintPath>
    <Private>True</Private>
  </Reference>
</ItemGroup>
```

### 4. Enable SDK Code
Edit `SageService.cs`:

**Line 5-6:** Uncomment:
```csharp
using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;
```

**In `ConnectAsync()` method:** Replace POC code with:
```csharp
// Initialize Sage session
_session = new PeachtreeSession();

// Set application info
var appInfo = new ApplicationIdentity
{
    CompanyName = "SageBridge",
    ProductName = "SageBridge Connector",
    ProductVersion = "1.0.0"
};

// Request authorization
var authResult = _session.RequestAuthorization(appInfo);
if (authResult != AuthorizationResult.Granted)
{
    Log.Error("Sage 50 authorization denied");
    return false;
}

// Open currently active company
_company = _session.Open();
CompanyName = _company.Name;
IsConnected = true;

Log.Information("✓ Connected to: {CompanyName}", CompanyName);
return true;
```

### 5. Update Other Methods
In `SageService.cs`, replace each `// TODO:` section with actual SDK calls.

**Example - GetCustomersAsync():**
```csharp
public async Task<List<object>> GetCustomersAsync()
{
    var customers = new List<object>();
    
    foreach (var customer in _company.Customers)
    {
        customers.Add(new
        {
            Id = customer.ID,
            Name = customer.Name,
            Email = customer.Email,
            Phone = customer.Phone,
            Balance = customer.Balance,
            Status = customer.Status.ToString()
        });
    }
    
    return customers;
}
```

### 6. Configure & Test
Edit `config.json`:
```json
{
  "ApiPort": 5001,
  "AutoConnect": true,
  "EnableCloudflare": false,
  "SyncIntervalSeconds": 300
}
```

**Run the connector:**
1. Make sure Sage 50 is running with a company open
2. Press F5 in Visual Studio
3. **First run:** Sage 50 will show an authorization dialog - click "Accept"
4. Check logs - you should see your actual company name

**Test with real data:**
```powershell
curl http://localhost:5001/api/customers
# Should now show YOUR actual Sage 50 customers!
```

**✅ If you see real Sage data, the SDK integration is working!**

---

## Phase 3: Cloudflare Integration

### 1. Install Cloudflared
```powershell
# Download from:
https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/

# Or use winget:
winget install cloudflare.cloudflared
```

### 2. Enable Tunnel
Edit `config.json`:
```json
{
  "EnableCloudflare": true,
  "CloudflareWorkerUrl": "https://api.sagebridge.workers.dev",
  "ApiKey": "your-api-key-here",
  "TenantId": "demo-tenant",
  "CompanyId": "demo-company"
}
```

### 3. Run & Get Tunnel URL
```bash
# Start connector
# Look for output like:
✓ Cloudflare Tunnel established: https://abc-def-ghi.trycloudflare.com
```

### 4. Test Remote Access
From your phone or any device:
```bash
curl https://abc-def-ghi.trycloudflare.com/health
# Should return your connector status!
```

**✅ Your connector is now accessible from anywhere!**

---

## Phase 4: Production Deployment

### 1. Build as Windows Service
(Coming soon - for now, run as console app)

### 2. Configure Auto-Start
Create scheduled task to run on Windows startup

### 3. Set Up Monitoring
Configure logging to file and cloud

---

## 🐛 Common Issues

### "Assembly not found: Sage.Peachtree.API"
**Fix:** 
1. Check SDK is installed
2. Verify DLL path in .csproj matches your installation
3. Rebuild solution

### "Sage 50 authorization denied"
**Fix:**
1. Make sure Sage 50 is running
2. Make sure a company is open
3. Click "Accept" when authorization dialog appears
4. Check you're running as same user who runs Sage

### "Port 5001 already in use"
**Fix:**
1. Change `ApiPort` in config.json to different port (e.g., 5002)
2. Rebuild and run

### "Cloudflared not found"
**Fix:**
1. Install cloudflared
2. Add to PATH: `C:\Program Files\cloudflared\`
3. Or place cloudflared.exe in connector directory

---

## ✅ Verification Checklist

- [ ] POC mode runs successfully
- [ ] All API endpoints return data
- [ ] POST /api/customers creates test customer
- [ ] Mobile app can connect to localhost:5001
- [ ] Sage 50 SDK is installed
- [ ] SDK DLLs referenced in project
- [ ] Connector connects to real Sage 50
- [ ] Real customer data appears in API
- [ ] Cloudflared is installed (optional)
- [ ] Tunnel established successfully (optional)
- [ ] Mobile app works via tunnel URL (optional)

---

## 📞 Support

If you get stuck:
1. Check logs in `logs/sagebridge-{date}.txt`
2. Verify all prerequisites are installed
3. Check Visual Studio Output window for build errors
4. Review Sage SDK documentation

---

**Next:** Once connector is working, move to building the Cloudflare Workers API layer!
