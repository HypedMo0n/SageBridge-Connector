# SageBridge Connector — Beta Distribution

## What's Included

- `SageBridgeConnector.exe` — the main connector application
- `config.json` — pre-configured for production (edit with your company paths)
- `install-service.ps1` — one-click installer (run as Administrator)
- `uninstall-service.ps1` — removes the service
- All required DLLs in this directory

## Installation Steps

### 1. Install Sage 50 Canada
   Ensure Sage 50 Canada is installed and you can open your company file.

### 2. Install the Connector
   Copy this entire directory to the PC (e.g. `C:\Program Files\SageBridge\`).

### 3. First Run (Console Mode — Required Once)
   Double-click `SageBridgeConnector.exe`.
   The console opens:
   - Follow the setup wizard prompts to enter your Sage company path and credentials.
   - Follow the pairing wizard pairing code if needed.
   - Close the console once you see "Connector is LIVE!"

### 4. Install as Windows Service (Silent Background)
   Right-click `install-service.ps1` → "Run with PowerShell" (ADMIN).
   The installer:
   - Downloads NSSM automatically
   - Registers the connector as an auto-start service
   - Configures log rotation
   The connector now runs silently in the background and restarts on boot.

### 5. Verify
   Open `http://localhost:5001/health` in a browser on the same PC.
   Expected response: `{"Status":"healthy", ...}`
   View the live log:
   ```
   Get-Content "C:\Program Files\SageBridge\logs\service.log" -Wait
   ```

## Configuration (config.json)

The `Companies` array maps cloud company IDs to local Sage files:

```json
"Companies": [
  {
    "CloudCompanyId": "main",
    "SageCompanyPath": "C:\\...\\Universl.SAI",
    "SageUsername": "Sagebridge",
    "SagePassword": "123"
  }
]
```

To add another company, append a new entry. Then run:
```
SageBridgeConnector.exe --setup
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "No enabled Sage company profile is configured" | Run `SageBridgeConnector.exe --setup` |
| Sage says "Other users are already using this company" | Close Sage 50 on all PCs, then start connector |
| Service won't start | Check `logs/service-error.log` |
| Cloud sync fails | Ensure the connector has a valid pairing (run normally once) |

## Uninstall

Right-click `uninstall-service.ps1` → "Run with PowerShell" (ADMIN).
