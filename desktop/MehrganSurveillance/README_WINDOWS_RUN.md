# سامانه نظارت تصویری مهرگان (Mehrgan AI Surveillance)
## فاز ۱: پایلوت پخش زنده ویدیو در ویندوز با LibVLCSharp و WPF

این پروژه، پیاده‌سازی مستقل و نیتیو ویندوز برای پخش ویدیوی زنده دوربین‌های تحت شبکه (IP Camera) به‌ویژه تیاندی (Tiandy) بر بستر پروتکل RTSP با لایه انتقال اجباری TCP است.

---

### مشخصات فنی پروژه (Technical Specifications)

- **چارچوب توسعه:** C# و .NET 10 LTS (`net10.0-windows`)
- **رابط کاربری:** WPF (Windows Presentation Foundation) با طراحی کاملاً راست‌به‌چپ (RTL)، تم روشن (Light Theme) و تایپوگرافی وزیرمتن (Vazirmatn)
- **موتور رندرینگ ویدیو:** `LibVLCSharp.WPF` (نسخه ۳.۹.۰) همراه با باینری‌های محلی `VideoLAN.LibVLC.Windows` (نسخه ۳.۰.۲۱)
- **پلتفرم اجرایی:** Windows 64-bit (`x64`)
- **پروتکل انتقال استریم:** اجبار به **RTSP over TCP** (`--rtsp-tcp`) جهت جلوگیری از افت بسته‌های UDP، شطرنجی شدن تصویر و تاخیر انباشته
- **شتاب‌دهنده سخت‌افزاری:** `--avcodec-hw=any` (استفاده از Direct3D11 / DirectX Video Acceleration کارت گرافیک سیستم)

---

### فرمت استریم‌های دوربین تیاندی (Tiandy RTSP Paths)

بر اساس مستندات رسمی تیاندی، ساختار آدرس استریم‌ها به صورت زیر است:
```text
rtsp://USERNAME:PASSWORD@IP:PORT/CHANNEL/STREAM
```

- **کانال (Channel):** شماره کانال دوربین (معمولاً `1` در اتصال مستقیم و `1` الی `N` در دستگاه NVR)
- **استریم ۱ (Main Stream):** مسیر `/1/1` - بالاترین کیفیت و رزولوشن، مناسب ضبط و مانیتورینگ اصلی
- **استریم ۲ (Sub Stream):** مسیر `/1/2` - کیفیت و بیت‌ریت سبک‌تر، مناسب وب و پیش‌نمایش چند دوربینه
- **استریم ۳ (Third Stream):** مسیر `/1/3` - استریم سوم جهت مصارف خاص انتقال شبکه
- **پورت RTSP:** پیش‌فرض `554`
- **پورت وب کمرا:** `3000` (صرفاً پورت مدیریت پنل وب است و نباید به جای پورت ۵۵۴ قرار گیرد)

---

### پیش‌نیازهای اجرا در سیستم ویندوز (Windows Requirements)

1. سیستم عامل: Windows 10 یا Windows 11 (نسخه 64 بیتی)
2. کیت توسعه: **.NET 10 Desktop SDK** (یا .NET 8 / 9 Windows Desktop SDK)
3. در صورت استفاده از محیط گرافیکی: **Visual Studio 2022 / 2026** با بار کاری **.NET Desktop Development**

---

### نحوه اجرا (How to Run)

#### روش ۱: اجرای سریع با اسکریپت خودکار PowerShell
در پوشه `desktop/MehrganSurveillance/`، روی فایل `run_desktop_poc.ps1` کلیک راست کرده و گزینه **Run with PowerShell** را انتخاب کنید یا دستور زیر را در ترمینال ویندوز اجرا نمایید:

```powershell
cd desktop\MehrganSurveillance
powershell -ExecutionPolicy Bypass -File .\run_desktop_poc.ps1
```

#### روش ۲: اجرای خط فرمان (CLI)
```cmd
cd desktop\MehrganSurveillance
dotnet restore -r win-x64
dotnet build -c Debug -r win-x64
dotnet run -c Debug -r win-x64 --no-build
```

#### روش ۳: باز کردن در Visual Studio
فایل `MehrganSurveillance.sln` را در Visual Studio باز کرده، تارگت کامپایل را روی **Debug | x64** قرار داده و کلید `F5` را بفشارید. پکیج‌های نیتیو LibVLC به صورت خودکار بازیابی و به همراه باینری‌های `libvlc.dll` و پوشه `plugins` در مسیر خروجی قرار می‌گیرند.

---

### معیارهای تمایز رندر واقعی تصویر از شبیه‌سازی (Real vs. Simulated Playback)

این برنامه هیچ‌گونه عنصر شبیه‌سازی (مانند Canvas مرورگر، فریم‌ریت ساختگی، یا ویدیوی از پیش ذخیره شده) ندارد:
1. **ارتباط شبکه و لایه انتقال:** موتور LibVLC مستقیماً سوکت TCP برقرار کرده و درخواست‌های `OPTIONS`, `DESCRIBE`, `SETUP`, `PLAY` را به دوربین یا NVR ارسال می‌کند.
2. **وقایع بومی دیکودر (Native Decoder Events):** پیام‌های بخش لاگ مستقیماً از موتور هسته C++ LibVLC تولید می‌شوند (`Opening` -> `Buffering` -> `Playing`).
3. **اطلاعات فنی فریم (Real Track Metadata):** ابعاد واقعی تصویر (مانند `1920x1080` یا `2560x1440`) و کدک چهارحرفی سخت‌افزاری (مانند `H264` یا `hevc`) پس از دریافت و بازگشایی نخستین بسته RTP استخراج و در نوار وضعیت درج می‌گردد.
4. **توقف کامل در نبود دوربین:** در صورتی که آدرس نامعتبر باشد یا دوربین در شبکه دردسترس نباشد، هیچ فریمی نمایش داده نمی‌شود و خطای واقعی سوکت یا تایم‌اوت شبکه لاگ می‌گردد.
