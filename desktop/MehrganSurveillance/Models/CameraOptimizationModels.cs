using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// انواع پروفایل‌های بهینه‌سازی هوشمند تصویر دوربین
    /// </summary>
    public enum CameraOptimizationProfileType
    {
        /// <summary>
        /// پلاک‌خوانی خودرو در روشنایی روز (شاتر بالا، حداقل نویز)
        /// </summary>
        DaytimeLicensePlate,

        /// <summary>
        /// پلاک‌خوانی خودرو در تاریکی شب (ضد نور چراغ HLC، شاتر کنترل‌شده برای جلوگیری از تاری و کشیدگی)
        /// </summary>
        NighttimeLicensePlate,

        /// <summary>
        /// وسایل نقلیه با سرعت بالا (اولویت حداکثری سرعت شاتر)
        /// </summary>
        HighSpeedVehicle,

        /// <summary>
        /// محیط‌های کم‌نور و شب تاریک (تعادل حساسیت سنسور، WDR و مادون قرمز)
        /// </summary>
        LowLightEnvironment,

        /// <summary>
        /// نظارت عمومی محیطی (تنظیمات متعادل و روان)
        /// </summary>
        GeneralSurveillance
    }

    /// <summary>
    /// وضعیت و پروفایل قابلیت‌های شناسایی‌شده دوربین
    /// هیچ قابلیتی بدون تایید واقعی سخت‌افزار فعال نشان داده نمی‌شود.
    /// </summary>
    public class CameraCapabilityProfile
    {
        public string Manufacturer { get; set; } = "Generic";
        public string Model { get; set; } = "ONVIF Camera";
        public string FirmwareVersion { get; set; } = "";
        public string ProviderName { get; set; } = "ONVIF Imaging";

        // قابلیت‌های کنترل شاتر / نوردهی
        public bool CanControlShutter { get; set; }
        public float MinShutterSeconds { get; set; } = 0.0001f; // 1/10000s
        public float MaxShutterSeconds { get; set; } = 0.04f;   // 1/25s
        public List<string> SupportedExposureModes { get; set; } = new();

        // قابلیت‌های گین سنسور
        public bool CanControlGain { get; set; }
        public float MinGain { get; set; } = 0;
        public float MaxGain { get; set; } = 100;

        // قابلیت WDR (دامنه دینامیکی گسترده)
        public bool CanControlWdr { get; set; }
        public float MinWdrLevel { get; set; } = 0;
        public float MaxWdrLevel { get; set; } = 100;

        // قابلیت HLC (Highlight Compensation - بسیار حیاتی برای پلاک‌خوانی در شب)
        public bool CanControlHlc { get; set; }

        // قابلیت BLC (Backlight Compensation)
        public bool CanControlBlc { get; set; }

        // قابلیت تغییر وضعیت روز/شب و فیلتر ICR
        public bool CanControlDayNightMode { get; set; }
        public List<string> SupportedDayNightModes { get; set; } = new();

        // قابلیت مادون قرمز (IR)
        public bool CanControlIr { get; set; }
        public bool SupportsSmartIr { get; set; }

        // قابلیت فوکوس اپتیکی
        public bool CanControlFocus { get; set; }

        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
        public bool IsVerified { get; set; }
        public string VerificationSummary { get; set; } = "";
    }

    /// <summary>
    /// اسنپ‌شات وضعیت تنظیمات دوربین برای مقایسه، اعمال و امکان Rollback امن
    /// </summary>
    public class CameraSettingsSnapshot
    {
        public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
        public string CameraId { get; set; } = "";
        public string CameraIp { get; set; } = "";
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

        // تنظیمات شاتر / اکسپوژر
        public float ShutterSpeedSeconds { get; set; } = 0.02f; // مثلا 1/50s پیش‌فرض
        public string ExposureMode { get; set; } = "Auto";      // Auto, Manual

        // گین سنسور
        public float Gain { get; set; } = 25f;

        // WDR
        public bool WdrEnabled { get; set; }
        public float WdrLevel { get; set; } = 50f;

        // HLC (مهار نور چراغ‌ها در شب)
        public bool HlcEnabled { get; set; }
        public float HlcLevel { get; set; } = 60f;

        // BLC
        public bool BlcEnabled { get; set; }

        // حالت شب و روز
        public string DayNightMode { get; set; } = "Auto"; // Auto, Day, Night

        // وضعیت مادون قرمز
        public string IrMode { get; set; } = "Auto";       // Auto, Off, Smart
        public float IrIntensity { get; set; } = 50f;

        // فوکوس
        public string FocusMode { get; set; } = "Auto";

        // داده‌های اختصاصی سازنده در صورت وجود
        public Dictionary<string, string> VendorSpecificParameters { get; set; } = new();

        public CameraSettingsSnapshot Clone()
        {
            return new CameraSettingsSnapshot
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                CameraId = this.CameraId,
                CameraIp = this.CameraIp,
                CapturedAt = DateTime.UtcNow,
                ShutterSpeedSeconds = this.ShutterSpeedSeconds,
                ExposureMode = this.ExposureMode,
                Gain = this.Gain,
                WdrEnabled = this.WdrEnabled,
                WdrLevel = this.WdrLevel,
                HlcEnabled = this.HlcEnabled,
                HlcLevel = this.HlcLevel,
                BlcEnabled = this.BlcEnabled,
                DayNightMode = this.DayNightMode,
                IrMode = this.IrMode,
                IrIntensity = this.IrIntensity,
                FocusMode = this.FocusMode,
                VendorSpecificParameters = new Dictionary<string, string>(this.VendorSpecificParameters)
            };
        }

        public string FormatShutterSpeed()
        {
            if (ShutterSpeedSeconds <= 0) return "Auto";
            if (ShutterSpeedSeconds < 1.0f)
            {
                int denominator = (int)Math.Round(1.0f / ShutterSpeedSeconds);
                return $"1/{denominator} ثانیه";
            }
            return $"{ShutterSpeedSeconds:F2} ثانیه";
        }
    }

    /// <summary>
    /// متریک‌های کیفیت بصری تصویر و فیدبک دریافتی از پلاک‌خوان ANPR
    /// </summary>
    public class ImageAnalysisMetrics
    {
        public DateTime SampledAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// میانگین روشنایی فریم (0 تا 255)
        /// </summary>
        public float AverageLuminance { get; set; } = 128f;

        /// <summary>
        /// انحراف معیار کنتراست تصویر
        /// </summary>
        public float ContrastStdDev { get; set; } = 45f;

        /// <summary>
        /// امتیاز تاری و کشیدگی حرکتی (0 = کاملا واضح، 100 = کاملا تار و کشیده)
        /// </summary>
        public float MotionBlurScore { get; set; } = 15f;

        /// <summary>
        /// تخمین نویز ناشی از گین بالا (0 تا 100)
        /// </summary>
        public float EstimatedNoiseLevel { get; set; } = 10f;

        /// <summary>
        /// آیا تصویر دچار سوختگی ناشی از نور شدید است؟
        /// </summary>
        public bool IsOverExposed { get; set; }

        /// <summary>
        /// آیا تصویر دچار تاریکی مفرط است؟
        /// </summary>
        public bool IsUnderExposed { get; set; }

        /// <summary>
        /// میانگین درصد اطمینان خواندن پلاک‌های اخیر (از ANPR)
        /// </summary>
        public float RecentAnprConfidence { get; set; } = 0.85f;

        /// <summary>
        /// تشخیص محیطی شب یا روز بر اساس لوکس روشنایی
        /// </summary>
        public string EstimatedAmbientLighting { get; set; } = "Day";
    }

    /// <summary>
    /// پیشنهاد بهینه‌سازی تولیدشده توسط موتور هوشمند با ذکر دلایل فنی فارسی
    /// </summary>
    public class CameraOptimizationRecommendation
    {
        public string RecommendationId { get; set; } = Guid.NewGuid().ToString("N");
        public string CameraId { get; set; } = "";
        public CameraOptimizationProfileType TargetProfile { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public CameraSettingsSnapshot CurrentSettings { get; set; } = new();
        public CameraSettingsSnapshot ProposedSettings { get; set; } = new();
        public ImageAnalysisMetrics EnvironmentalMetrics { get; set; } = new();

        /// <summary>
        /// دلایل مجزا و تحلیلی برای هر پارامتر تغییریافته
        /// </summary>
        public Dictionary<string, string> ParameterReasons { get; set; } = new();

        /// <summary>
        /// توضیح خلاصه و مهندسی کلی به زبان فارسی
        /// </summary>
        public string ExecutiveSummary { get; set; } = "";

        /// <summary>
        /// امتیاز اطمینان موتور هوش مصنوعی به این تغییر
        /// </summary>
        public float EngineConfidence { get; set; } = 0.92f;
    }

    /// <summary>
    /// سابقه ذخیره‌شده تغییرات جهت حسابرسی و بازگشت (Rollback)
    /// </summary>
    public class OptimizationAuditRecord
    {
        public string RecordId { get; set; } = Guid.NewGuid().ToString("N");
        public string CameraId { get; set; } = "";
        public string CameraIp { get; set; } = "";
        public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
        public string ProfileApplied { get; set; } = "";
        public string AdminUser { get; set; } = "مدیر سیستم";
        public bool Success { get; set; }
        public string ResultSummary { get; set; } = "";
        public CameraSettingsSnapshot BeforeSnapshot { get; set; } = new();
        public CameraSettingsSnapshot AfterSnapshot { get; set; } = new();
        public bool WasRolledBack { get; set; }
        public DateTime? RolledBackAt { get; set; }
    }
}
