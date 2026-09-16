<#
.SYNOPSIS
    Installs the SageBridge Connector as a Windows Service using NSSM.
.DESCRIPTION
    Downloads NSSM (if not present), registers the connector as an auto-start
    service, and configures logging. The service runs under the current user
    so DPAPI credentials are accessible.

USAGE:
    .\install-service.ps1
#>

param(
    [string]$InstallDir = $PSScriptRoot,
    [string]$ServiceName = "SageBridgeConnector"
)

$ErrorActionPreference = "Stop"

# Resolve paths
$ConnectorExe = Join-Path $InstallDir "SageBridgeConnector.exe"
$NssmExe = Join-Path $InstallDir "nssm.exe"
$LogsDir = Join-Path $InstallDir "logs"

# Ensure logs directory exists
if (-not (Test-Path $LogsDir)) { New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null }

# Download NSSM if not present
if (-not (Test-Path $NssmExe)) {
    Write-Host "Downloading NSSM..."
    $NssmUrl = "https://nssm.io/release/nssm-2.24.zip"
    $NssmZip = Join-Path $env:TEMP "nssm.zip"
    $NssmExtract = Join-Path $env:TEMP "nssm"

    try {
        Invoke-WebRequest -Uri $NssmUrl -OutFile $NssmZip
        Expand-Archive -Path $NssmZip -DestinationPath $NssmExtract -Force

        # Copy the x86 version (connector is x86)
        $NssmSource = Join-Path (Join-Path $NssmExtract "win32") "nssm.exe"
        if (Test-Path $NssmSource) {
            Copy-Item $NssmSource $NssmExe
        } else {
            # Fallback to win64 if x86 not found
            $NssmSource = Join-Path (Join-Path $NssmExtract "win64") "nssm.exe"
            Copy-Item $NssmSource $NssmExe
        }

        Remove-Item $NssmZip -Force
        Remove-Item $NssmExtract -Recurse -Force
        Write-Host "NSSM installed."
    } catch {
        Write-Warning "Could not download NSSM. Please download it manually from https://nssm.io and place nssm.exe in $InstallDir"
    }
}

# Remove existing service if present
$Existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($Existing) {
    Write-Host "Removing existing service..."
    & $NssmExe remove $ServiceName confirm | Out-Null
    Start-Sleep -Seconds 2
}

# Register the service
Write-Host "Installing SageBridge Connector as Windows Service..."
& $NssmExe install $ServiceName $ConnectorExe | Out-Null

# Configure the service
& $NssmExe set $ServiceName AppDirectory $InstallDir | Out-Null
& $NssmExe set $ServiceName AppStdout (Join-Path $LogsDir "service.log") | Out-Null
& $NssmExe set $ServiceName AppStderr (Join-Path $LogsDir "service-error.log") | Out-Null
& $NssmExe set $ServiceName AppRotateFiles 1 | Out-Null
& $NssmExe set $ServiceName AppRotateBytes 10485760 | Out-Null
& $NssmExe set $ServiceName Start SERVICE_AUTO_START | Out-Null
& $NssmExe set $ServiceName AppRestartDelay 5000 | Out-Null
& $NssmExe set $ServiceName AppExit Default Restart | Out-Null

Write-Host ""
Write-Host "Service installed successfully!" -ForegroundColor Green
Write-Host "  Name: $ServiceName"
Write-Host "  Executable: $ConnectorExe"
Write-Host "  Logs: $LogsDir"
Write-Host ""
Write-Host "Start with: net start $ServiceName"
Write-Host "Stop with:  net stop $ServiceName"
Write-Host "View logs:  Get-Content '$LogsDir\service.log' -Wait"
