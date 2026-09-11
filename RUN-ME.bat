@echo off
echo ========================================
echo SageBridge SDK Setup
echo ========================================
echo.
echo Starting PowerShell script...
echo.

PowerShell.exe -ExecutionPolicy Bypass -File "%~dp0Setup-SageSDK.ps1"

echo.
echo ========================================
echo Press any key to close...
pause >nul
