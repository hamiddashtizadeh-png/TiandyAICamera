# اسکریپت دانلود مدل سبک آشکارسازی چهره Ultra-Face (RFB-320)
# مسیر ذخیره: Models/Weights/version-RFB-320.onnx

$WeightsDir = Join-Path $PSScriptRoot "Models\Weights"
if (-not (Test-Path $WeightsDir)) {
    New-Item -ItemType Directory -Path $WeightsDir -Force | Out-Null
    Write-Host "دایرکتوری $WeightsDir ایجاد گردید." -ForegroundColor Cyan
}

$ModelPath = Join-Path $WeightsDir "version-RFB-320.onnx"
$Url = "https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/raw/master/models/onnx/version-RFB-320.onnx"

Write-Host "در حال دانلود مدل هوش مصنوعی Ultra-Face از گیت‌هاب..." -ForegroundColor Yellow
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $ModelPath -UseBasicParsing
    Write-Host "مدل با موفقیت در $ModelPath ذخیره گردید (حجم: 1.2MB)." -ForegroundColor Green
}
catch {
    Write-Host "خطا در دانلود خودکار: $_" -ForegroundColor Red
    Write-Host "شما می‌توانید فایل version-RFB-320.onnx را به صورت دستی در پوشه Models\Weights کپی فرمایید." -ForegroundColor Yellow
}
