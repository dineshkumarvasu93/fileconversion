# Telerik Document Processing - License Setup Script (PowerShell)
# This script helps configure your Telerik trial license

Write-Host "`n"
Write-Host "╔════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   Telerik Document Processing - License Setup Guide        ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

Write-Host "STEP 1: Get Your 30-Day Trial License" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""
Write-Host "1. Open browser: https://www.telerik.com/account/your-licenses/" -ForegroundColor White
Write-Host ""
Write-Host "2. If you don't have a Telerik account:" -ForegroundColor Cyan
Write-Host "   • Go to: https://www.telerik.com/account/register" -ForegroundColor Gray
Write-Host "   • Create a free account" -ForegroundColor Gray
Write-Host "   • Verify your email" -ForegroundColor Gray
Write-Host ""
Write-Host "3. Sign in to your account:" -ForegroundColor Cyan
Write-Host "   • Visit: https://www.telerik.com/account/" -ForegroundColor Gray
Write-Host "   • Click 'Your Licenses'" -ForegroundColor Gray
Write-Host ""
Write-Host "4. Register for trial:" -ForegroundColor Cyan
Write-Host "   • Click 'Register Trial License'" -ForegroundColor Gray
Write-Host "   • Select 'Telerik Document Processing'" -ForegroundColor Gray
Write-Host "   • Accept terms & conditions" -ForegroundColor Gray
Write-Host "   • Click 'Activate'" -ForegroundColor Gray
Write-Host ""
Write-Host "5. Download license:" -ForegroundColor Cyan
Write-Host "   • You'll see the trial license in your account" -ForegroundColor Gray
Write-Host "   • Download the 'telerik-license.txt' file" -ForegroundColor Gray
Write-Host ""

Write-Host "`nSTEP 2: Install License File" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""

Write-Host "Option A (Recommended): Project directory" -ForegroundColor Cyan
Write-Host "  Copy license file to project folder" -ForegroundColor Gray
Write-Host ""

Write-Host "Option B (Global): User AppData" -ForegroundColor Cyan
Write-Host "  Create folder: AppData\Roaming\Telerik" -ForegroundColor Gray
Write-Host ""

Write-Host "Option C (Environment): System variable" -ForegroundColor Cyan
Write-Host "  Set TELERIK_LICENSE_PATH to license path" -ForegroundColor Gray
Write-Host ""

Write-Host "STEP 3: Verify Installation" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""

$licensePathA = "D:\FileConversionPOC\FileConversionConsoleApp\telerik-license.txt"
$licensePathB = "$env:APPDATA\Telerik\telerik-license.txt"

if (Test-Path $licensePathA) 
{
    Write-Host "License file found at: $licensePathA" -ForegroundColor Green
}
elseif (Test-Path $licensePathB) 
{
    Write-Host "License file found at: $licensePathB" -ForegroundColor Green
}
else 
{
    Write-Host "License file not found yet" -ForegroundColor Red
    Write-Host "Please download and place your license file" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "STEP 4: Build and Run Application" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""
Write-Host "Run these commands:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  cd D:\FileConversionPOC\FileConversionConsoleApp" -ForegroundColor Yellow
Write-Host "  dotnet clean" -ForegroundColor Yellow
Write-Host "  dotnet build" -ForegroundColor Yellow
Write-Host "  dotnet run" -ForegroundColor Yellow
Write-Host ""

Write-Host "STEP 5: Expected Results" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""
Write-Host "✓ Build succeeds with 0 errors" -ForegroundColor Green
Write-Host "✓ No license warnings in console" -ForegroundColor Green
Write-Host "✓ Application menu displays options 1-5" -ForegroundColor Green
Write-Host "✓ XHTML to RTF conversion works" -ForegroundColor Green
Write-Host ""
Write-Host "NEED HELP?" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════════"
Write-Host ""
Write-Host "Trial Duration: 30 days from registration" -ForegroundColor Gray
Write-Host "Telerik Docs: https://docs.telerik.com/devtools/document-processing/" -ForegroundColor Gray
Write-Host "Support: https://www.telerik.com/support" -ForegroundColor Gray
Write-Host "Forums: https://www.telerik.com/forums/document-processing" -ForegroundColor Gray
Write-Host ""
Write-Host "Setup complete! Run 'dotnet build' to proceed." -ForegroundColor Green
