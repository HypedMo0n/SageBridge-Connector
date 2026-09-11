# 🌉 SageBridge Connector

**Windows Service that bridges Sage 50 Canada to the cloud via REST API**

---

## 🎯 What This Does

This connector:
1. ✅ Connects to Sage 50 Canada using the official SDK
2. ✅ Exposes a local REST API (`http://localhost:5001`)
3. ✅ Optionally syncs data to Cloudflare Workers
4. ✅ Optionally establishes a Cloudflare Tunnel for remote access

---

## 📋 Prerequisites

### Required on Windows Machine:
1. **Sage 50 Canada 2026.2** (or compatible version)
2. **Sage 50 Canada SDK 2026.2** ([Download from Sage](https://developer.sage.com/))
3. **Visual Studio 2022** (Community edition is free)
4. **.NET Framework 4.8** (should be installed with Windows)

### Optional (for cloud sync):
5. **Cloudflared CLI** ([Download](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/))

---

## 🚀 Quick Start (POC Mode)

### Step 1: Open Solution
```bash
# Open in Visual Studio
SageBridge.Connector.sln
```

### Step 2: Build (x86)
1. Set build configuration to **Debug | x86**
2. Build → Build Solution (Ctrl+Shift+B)

### Step 3: Run
1. Press F5 or click "Start"
2. You should see:
```
╔════════════════════════════════════════╗
║   SageBridge Connector v1.0.0          ║
║   Sage 50 Canada → Cloudflare Bridge   ║
╚════════════════════════════════════════╝

✓ REST API started on http://localhost:5001
✓ Connected to Sage 50: Demo Construction Ltd. (POC Mode)
✓ Sync engine started (interval: 300s)

═══════════════════════════════════════
Connector is LIVE! Available endpoints:
  GET  http://localhost:5001/health
  GET  http://localhost:5001/api/company
  GET  http://localhost:5001/api/customers
  GET  http://localhost:5001/api/customers/{id}
  GET  http://localhost:5001/api/invoices
  GET  http://localhost:5001/api/products
  POST http://localhost:5001/api/customers
═══════════════════════════════════════
```

### Step 4: Test API
Open PowerShell or Command Prompt:

```powershell
# Health check
curl http://localhost:5001/health

# Get company info
curl http://localhost:5001/api/company

# Get all customers
curl http://localhost:5001/api/customers

# Get specific customer
curl http://localhost:5001/api/customers/CUST001

# Get invoices
curl http://localhost:5001/api/invoices

# Get products
curl http://localhost:5001/api/products

# Create test customer (POST)
curl -X POST http://localhost:5001/api/customers ^
  -H "Content-Type: application/json" ^
  -d "{\"name\":\"Test Mobile Customer\",\"email\":\"test@mobile.com\",\"phone\":\"604-555-9999\"}"
```

---

## 🔧 Adding Sage 50 SDK

**Currently running in POC mode with demo data.** To connect to real Sage 50:

### Step 1: Install Sage 50 SDK
1. Download **Sage 50 Canada SDK 2026.2** from Sage Developer Portal
2. Install the SDK (typically installs to `C:\Program Files (x86)\Sage\Sage 50 SDK\`)

### Step 2: Add SDK References
1. Open `SageBridge.Connector.csproj`
2. Uncomment the SDK references section:
```xml
<Reference Include="Sage.Peachtree.API">
  <HintPath>C:\Program Files (x86)\Sage\Sage 50 SDK\Sage.Peachtree.API.dll</HintPath>
  <Private>True</Private>
</Reference>
<Reference Include="Sage.Peachtree.API.Collections">
  <HintPath>C:\Program Files (x86)\Sage\Sage 50 SDK\Sage.Peachtree.API.Collections.dll</HintPath>
  <Private>True</Private>
</Reference>
```

### Step 3: Enable Real SDK Code
1. Open `SageService.cs`
2. Uncomment the `using Sage.Peachtree.API;` statements
3. Replace POC simulation code with actual SDK calls (marked with `// TODO:`)

### Step 4: Configure Company Path
Edit `config.json`:
```json
{
  "SageCompanyPath": "C:\\Sage\\Sage50\\Data\\YourCompany.SAI",
  "AutoConnect": true
}
```

---

## 🌩️ Cloudflare Integration

### Enable Cloud Sync
Edit `config.json`:
```json
{
  "EnableCloudflare": true,
  "CloudflareWorkerUrl": "https://api.sagebridge.workers.dev",
  "ApiKey": "your-api-key-here",
  "TenantId": "your-tenant-id",
  "CompanyId": "your-company-id",
  "SyncIntervalSeconds": 300
}
```

### Enable Cloudflare Tunnel (Remote Access)
1. Install cloudflared: https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/
2. The connector will automatically start a tunnel when `EnableCloudflare: true`
3. Mobile app can access via the tunnel URL (printed in logs)

---

## 📊 API Endpoints

### GET /health
Health check and connection status
```json
{
  "Status": "healthy",
  "Connected": true,
  "Company": "Demo Construction Ltd.",
  "Timestamp": "2026-09-10T19:00:00Z",
  "Version": "1.0.0"
}
```

### GET /api/company
Company information
```json
{
  "Name": "Demo Construction Ltd.",
  "Address": "123 Demo Street, Vancouver, BC",
  "Phone": "604-555-0100",
  "FiscalYear": 2026,
  "BaseCurrency": "CAD"
}
```

### GET /api/customers
List all customers
```json
{
  "customers": [
    {
      "Id": "CUST001",
      "Name": "ABC Construction",
      "Email": "contact@abc-const.ca",
      "Phone": "604-555-0101",
      "Balance": 2450.75,
      "Status": "Active"
    }
  ]
}
```

### GET /api/customers/{id}
Get single customer by ID

### GET /api/invoices
List all invoices

### GET /api/products
List all products/services

### POST /api/customers
Create new customer
```json
{
  "name": "New Customer",
  "email": "customer@example.com",
  "phone": "604-555-0123"
}
```

---

## 🔒 Security Notes

- **Local only by default** - API only accessible from localhost
- **Cloudflare Tunnel** - Secure outbound-only connection (no open ports)
- **API Key required** - For cloud sync endpoints
- **Tenant isolation** - All data tagged with tenant_id + company_id

---

## 🐛 Troubleshooting

### "Failed to connect to Sage 50"
1. Make sure Sage 50 is installed
2. Make sure a company is open in Sage 50
3. Check that SDK DLLs are referenced correctly
4. Run Visual Studio as Administrator

### "x86 platform target not available"
1. In Visual Studio: Build → Configuration Manager
2. Create new platform: x86
3. Set Active solution platform to x86

### "cloudflared not found"
1. Download from https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/
2. Add to PATH or place in project directory

---

## 📝 Next Steps

1. ✅ **Test POC locally** - Verify API endpoints work
2. ⏳ **Add Sage SDK** - Connect to real Sage 50 data
3. ⏳ **Build Cloudflare Workers** - Cloud API layer
4. ⏳ **Update mobile app** - Connect to connector API
5. ⏳ **Package as Windows Service** - Auto-start on boot

---

## 🎯 Architecture

```
Mobile App (https://app.sagebridge.io)
    ↓
Cloudflare Workers API
    ↓
Cloudflare D1 Database
    ↑
Cloudflare Tunnel (secure outbound)
    ↑
Windows Connector (this project)
    ↓
Sage 50 SDK
    ↓
Sage 50 Canada Company Database
```

---

## 📚 Resources

- [Sage 50 SDK Documentation](https://developer.sage.com/)
- [Cloudflare Tunnels Guide](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/)
- [SageBridge Mobile App](https://github.com/your-repo/sagebridge-mobile)

---

**Built with ❤️ for Sage 50 Canada users who need mobile access**
