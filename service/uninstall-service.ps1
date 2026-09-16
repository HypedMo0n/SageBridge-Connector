<#
.SYNOPSIS
    Removes the SageBridge Connector Windows Service and optionally the program files.
.PARAMETER InstallDir   Directory where the connector was installed. Default: script directory.
.PARAMETER RemoveData   If set, also deletes config.json, logs/, and credential storage.
#>

param(
    [string]$InstallDir = $PSScriptRoot,
    [switch]$RemoveData
)

$ServiceName = "SageBridgeConnector"
$NssmExe = Join-Path $InstallDir "nssm.exe"

Write-Host "Stopping and removing service..."
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Test-Path $NssmExe) {
        & $NssmExe remove $ServiceName confirm | Out-Null
    }
    Write-Host "Service removed."
} else {
    Write-Host "Service not found (may already be removed)."
}

if ($RemoveData) {
    Remove-Item (Join-Path $InstallDir "config.json") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $InstallDir "logs") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA "SageBridgeConnector") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Local data removed."
}

Write-Host ""
Write-Host "SageBridge Connector has been uninstalled." -ForegroundColor Green
