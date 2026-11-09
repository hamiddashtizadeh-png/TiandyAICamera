@echo off
chcp 65001 >nul
echo ==============================================================================
echo سامانه نظارت تصویری هوشمند مهرگان (Mehrgan AI Surveillance)
echo در حال اجرای سامانه دسکتاپ ویندوز...
echo ==============================================================================
echo.

cd /d "%~dp0desktop\MehrganSurveillance"

powershell -ExecutionPolicy Bypass -File .\run_desktop_poc.ps1
pause
