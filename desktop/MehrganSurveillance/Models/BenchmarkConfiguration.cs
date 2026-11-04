using System;

namespace MehrganSurveillance.Models;

/// <summary>
/// پیکربندی جامع و دقیق برای اجرای بنچمارک ظرفیت و پایداری هوش مصنوعی
/// </summary>
public class BenchmarkConfiguration
{
    /// <summary>
    /// رده زمانی اجرای بنچمارک
    /// </summary>
    public BenchmarkDurationTier Tier { get; set; } = BenchmarkDurationTier.SmokeTest30s;

    /// <summary>
    /// مدت‌زمان هدف برای فاز اندازه‌گیری فعال (در صورت تعیین، بر پیش‌فرض Tier اولویت دارد)
    /// </summary>
    public TimeSpan? CustomMeasurementDuration { get; set; }

    /// <summary>
    /// مدت‌زمان گرم کردن اولیه سامانه (Warmup) جهت تثبیت JIT و کش حافظه (داده‌های این فاز در نتایج نهایی ثبت نمی‌شوند)
    /// </summary>
    public TimeSpan WarmupDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// مدت‌زمان تخلیه و خنک‌سازی سامانه (Cooldown) در انتهای آزمون
    /// </summary>
    public TimeSpan CooldownDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// نوع بار کاری هوش مصنوعی (پلاک‌خوان، چهره، یا ترکیبی)
    /// </summary>
    public AiWorkloadType Workload { get; set; } = AiWorkloadType.AnprOnly;

    /// <summary>
    /// تعداد کانال‌های همزمان مورد ارزیابی
    /// </summary>
    public int ChannelCount { get; set; } = 2;

    /// <summary>
    /// ظرفیت صف فریم برای هر کانال قبل از فعال شدن خط‌مشی سرریز
    /// </summary>
    public int QueueCapacityPerChannel { get; set; } = 25;

    /// <summary>
    /// خط‌مشی رفتار صف هنگام پر شدن ظرفیت
    /// </summary>
    public QueueOverflowPolicy OverflowPolicy { get; set; } = QueueOverflowPolicy.DropOldest;

    /// <summary>
    /// منشأ فریم‌های ورودی
    /// </summary>
    public BenchmarkInputProvenance InputProvenance { get; set; } = BenchmarkInputProvenance.SyntheticMemoryBuffer;

    /// <summary>
    /// سطح اصالت معماری مدل هوش مصنوعی
    /// </summary>
    public BenchmarkModelFidelity ModelFidelity { get; set; } = BenchmarkModelFidelity.SyntheticDiagnostic;

    /// <summary>
    /// حداکثر تاخیر مجاز دیسپچر واسط کاربری (میلی‌ثانیه) جهت تضمین عدم فریز UI زنده
    /// </summary>
    public double MaxAllowedUiDispatcherLagMs { get; set; } = 50.0;

    /// <summary>
    /// آدرس منبع ویدیو برای حالت فایل محلی یا استریم زنده RTSP
    /// </summary>
    public string? VideoSourcePathOrUri { get; set; }

    /// <summary>
    /// وضوح تصویر فریم‌ها در طول آزمون
    /// </summary>
    public int FrameWidth { get; set; } = 1920;
    public int FrameHeight { get; set; } = 1080;

    /// <summary>
    /// نرخ فریم نامی تولیدکننده (فریم بر ثانیه برای هر کانال)
    /// </summary>
    public int NominalFps { get; set; } = 25;

    /// <summary>
    /// شناسه آداپتور DirectML در صورت استفاده از GPU
    /// </summary>
    public int DirectMlDeviceId { get; set; } = 0;

    /// <summary>
    /// اثرانگشت کالیبراسیون فعال که آزمون بر اساس آن سنجیده می‌شود
    /// </summary>
    public CalibrationFingerprint? Fingerprint { get; set; }

    /// <summary>
    /// محاسبه مدت‌زمان فعال فاز اندازه‌گیری بر اساس پیکربندی
    /// </summary>
    public TimeSpan GetMeasurementDuration()
    {
        if (CustomMeasurementDuration.HasValue && CustomMeasurementDuration.Value > TimeSpan.Zero)
        {
            return CustomMeasurementDuration.Value;
        }

        return Tier switch
        {
            BenchmarkDurationTier.SmokeTest30s => TimeSpan.FromSeconds(30),
            BenchmarkDurationTier.Calibration5to10m => TimeSpan.FromMinutes(5),
            BenchmarkDurationTier.SustainedValidation30to60m => TimeSpan.FromMinutes(30),
            BenchmarkDurationTier.MemoryStability1to2h => TimeSpan.FromHours(1),
            _ => TimeSpan.FromSeconds(30)
        };
    }
}
