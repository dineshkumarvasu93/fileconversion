@echo off
REM Telerik License Setup Script
REM This script helps you set up your Telerik license

echo.
echo ╔════════════════════════════════════════════════════════════╗
echo ║       Telerik Document Processing - License Setup          ║
echo ╚════════════════════════════════════════════════════════════╝
echo.

echo Step 1: Get Your Trial License
echo ═══════════════════════════════════════════════════════════
echo.
echo Visit: https://www.telerik.com/account/your-licenses/
echo.
echo If you don't have a Telerik account:
echo   1. Go to https://www.telerik.com/account/register
echo   2. Create a free account
echo.
echo If you already have an account:
echo   1. Sign in to https://www.telerik.com/account/
echo   2. Go to "Your Licenses" section
echo   3. Click "Register Trial License"
echo   4. Choose "Telerik Document Processing"
echo   5. Accept terms and register
echo   6. Download the license file (telerik-license.txt)
echo.
echo Step 2: Place License File
echo ═══════════════════════════════════════════════════════════
echo.
echo Option A (Recommended - Project Directory):
echo   Copy the license file to:
echo   D:\FileConversionPOC\FileConversionConsoleApp\telerik-license.txt
echo.
echo Option B (User AppData - Global for all projects):
echo   1. Create folder: %%APPDATA%%\Telerik
echo   2. Copy license file there as: telerik-license.txt
echo   Path: C:\Users\<YourUsername>\AppData\Roaming\Telerik\telerik-license.txt
echo.
echo Option C (Environment Variable):
echo   Set system environment variable TELERIK_LICENSE_PATH to the license file path
echo.
echo Step 3: Build and Run
echo ═══════════════════════════════════════════════════════════
echo.
echo Once license is in place, run:
echo   dotnet clean
echo   dotnet build
echo   dotnet run
echo.
echo Step 4: Verify
echo ═══════════════════════════════════════════════════════════
echo.
echo After running, you should see:
echo   ✓ Build succeeds with 0 errors
echo   ✓ Application menu displays
echo   ✓ Conversion features work
echo.
pause
