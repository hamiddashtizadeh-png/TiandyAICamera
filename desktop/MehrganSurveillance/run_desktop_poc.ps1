# ==============================================================================
# Mehrgan AI Surveillance - Desktop POC Runner (Windows PowerShell)
# سامانه نظارت تصویری مهرگان - اسکریپت اجرای خودکار پایلوت ویندوزی (C# / WPF)
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "سامانه نظارت تصویری مهرگان - فاز ۱"

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "     سامانه نظارت تصویری مهرگان (Mehrgan AI Surveillance)            " -ForegroundColor Yellow
Write-Host "     پایلوت پخش زنده ویدیوی دوربین با LibVLCSharp و WPF (ویندوز)       " -ForegroundColor Yellow
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""

# ۱. بررسی نصب بودن دات‌نت اس‌دی‌کی (Dotnet SDK)
Write-Host "[۱/۴] بررسی پیش‌نیازهای سیستمی و .NET SDK..." -ForegroundColor White
try {
    $dotnetVersion = dotnet --version
    Write-Host "  ✓ .NET SDK شناسایی شد: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "  ✗ خطای عدم شناسایی .NET SDK!" -ForegroundColor Red
    Write-Host "  لطفاً .NET 10 SDK (یا .NET 8/9 Desktop SDK) را از لینک زیر نصب فرمایید:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet" -ForegroundColor Cyan
    Read-Host "جهت خروج کلید Enter را فشار دهید..."
    exit 1
}

# ۲. بازیابی پکیج‌های NuGet بومی LibVLC و LibVLCSharp
Write-Host ""
Write-Host "[۲/۴] بازیابی پکیج‌های نیتیو NuGet (LibVLCSharp.WPF & VideoLAN.LibVLC.Windows)..." -ForegroundColor White
$currentDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $currentDir

dotnet restore MehrganSurveillance.csproj -r win-x64
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ خطا در بازیابی پکیج‌های NuGet!" -ForegroundColor Red
    Read-Host "جهت خروج کلید Enter را فشار دهید..."
    exit $LASTEXITCODE
}
Write-Host "  ✓ پکیج‌های نیتیو و باینری‌های libvlc.dll با موفقیت تایید و دانلود شدند." -ForegroundColor Green

# ۳. کامپایل پروژه برای پلتفرم 64 بیتی ویندوز (x64)
Write-Host ""
Write-Host "[۳/۴] کامپایل پروژه MehrganSurveillance (Configuration: Debug, Platform: x64)..." -ForegroundColor White
dotnet build MehrganSurveillance.csproj -c Debug -r win-x64 --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ خطا در کامپایل پروژه C# WPF!" -ForegroundColor Red
    Read-Host "جهت خروج کلید Enter را فشار دهید..."
    exit $LASTEXITCODE
}
Write-Host "  ✓ کامپایل کدهای بومی با موفقیت پایان یافت." -ForegroundColor Green

# ۴. اجرای برنامه دسکتاپ ویندوزی
Write-Host ""
Write-Host "[۴/۴] اجرای برنامه بومی سامانه نظارت تصویری مهرگان..." -ForegroundColor White
Write-Host "  (پنجره برنامه دسکتاپ با تم روشن و زبان فارسی باز خواهد شد)" -ForegroundColor Cyan
Write-Host "----------------------------------------------------------------------" -ForegroundColor DarkGray

dotnet run --project MehrganSurveillance.csproj -c Debug -r win-x64 --no-build

Write-Host ""
Write-Host "برنامه با موفقیت خاتمه یافت." -ForegroundColor Gray
