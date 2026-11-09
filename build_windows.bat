@echo off
chcp 65001 >nul
echo ==============================================================================
echo سامانه نظارت تصویری هوشمند مهرگان (Mehrgan AI Surveillance)
echo در حال کامپایل و بیلد پروژه دسکتاپ ویندوزی (C# / .NET 10 / WPF)...
echo ==============================================================================
echo.

cd /d "%~dp0desktop\MehrganSurveillance"

echo [1/2] بازیابی پکیج‌های نیتیو (dotnet restore)...
dotnet restore MehrganSurveillance.csproj -r win-x64
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ❌ خطا در بازیابی پکیج‌ها!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] کامپایل پروژه (dotnet build)...
dotnet build MehrganSurveillance.csproj -c Debug -r win-x64 --no-restore
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ❌ خطا در کامپایل پروژه!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ==============================================================================
echo ✅ پروژه با موفقیت کامپایل شد!
echo جهت اجرای برنامه می‌توانید از دستور زیر یا فایل run_windows.bat استفاده کنید:
echo dotnet run --project desktop\MehrganSurveillance\MehrganSurveillance.csproj -c Debug -r win-x64 --no-build
echo ==============================================================================
pause
