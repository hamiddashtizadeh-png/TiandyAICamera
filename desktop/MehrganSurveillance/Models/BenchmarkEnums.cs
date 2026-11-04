using System;

namespace MehrganSurveillance.Models;

/// <summary>
/// منشأ و نحوه تامین ورودی فریم‌ها در طول اجرای بنچمارک
/// تفکیک صریح داده‌های مصنوعی، فایل ویدیویی محلی و استریم شبکه واقعی
/// </summary>
public enum BenchmarkInputProvenance
{
    /// <summary>
    /// بافر حافظه تصادفی یا الگوی مصنوعی بدون هزینه دیکود شبکه یا دیسک (صرفاً برای سنجش نظری موتور استنتاج)
    /// </summary>
    SyntheticMemoryBuffer,

    /// <summary>
    /// استخراج و دیکود فریم از فایل ویدیویی محلی از پیش ذخیره‌شده (آزمون بدون نوسانات شبکه)
    /// </summary>
    OfflineRecordedVideo,

    /// <summary>
    /// استریم زنده شبکه از طریق پروتکل RTSP با حضور کامل لایه شبکه، بافرینگ و جیتر
    /// </summary>
    LiveRtspStream
}

/// <summary>
/// سطح دقت و اصالت معماری مدل هوش مصنوعی تحت آزمون
/// مانع از ادعای کاذب کالیبراسیون کامل توسط مدل‌های مینیاتوری یا تشخیصی
/// </summary>
public enum BenchmarkModelFidelity
{
    /// <summary>
    /// مدل مینیاتوری تشخیصی (مانند مدل تست ۱۵۰ بایتی Add) برای سنجش اتصال خط لوله و سلامت کرنل‌ها
    /// هرگز به تنهایی مبنای کالیبره کردن ظرفیت نهایی قرار نمی‌گیرد
    /// </summary>
    SyntheticDiagnostic,

    /// <summary>
    /// مدل تقریب‌زننده تولید (سورگیت) با ابعاد ورودی و لایه‌های ساختاری متناظر اما وزن‌ها یا مراحل سبک‌تر
    /// </summary>
    ProductionModelSurrogate,

    /// <summary>
    /// مدل‌های کامل و واقعی عملیاتی پلاک‌خوان (تشخیص + OCR) یا چهره جهت کالیبراسیون رسمی
    /// </summary>
    FullProductionModel
}

/// <summary>
/// خط‌مشی رفتار صف ورودی در زمان پر شدن ظرفیت صف کانال
/// </summary>
public enum QueueOverflowPolicy
{
    /// <summary>
    /// حذف قدیمی‌ترین فریم موجود در صف برای باز کردن جا برای فریم جدیدتر (ایده‌آل برای نظارت زنده)
    /// </summary>
    DropOldest,

    /// <summary>
    /// رد کردن فریم ورودی جدید در بدو ورود در صورت پر بودن صف
    /// </summary>
    DropNewest,

    /// <summary>
    /// مسدودسازی تولیدکننده تا زمان خالی شدن فضا در صف (Backpressure - صرفاً برای آزمون‌های آفلاین)
    /// </summary>
    BlockProducer
}

/// <summary>
/// فاز چرخه حیات اجرای آزمون بنچمارک
/// </summary>
public enum BenchmarkExecutionPhase
{
    NotStarted,
    Initialization,
    Warmup,
    ActiveMeasurement,
    Cooldown,
    Finalizing,
    Completed,
    Aborted,
    Failed
}

/// <summary>
/// وضعیت ثبت همزمانی واقعی بین بارهای کاری مستقل در حالت ترکیبی (Combined)
/// </summary>
public enum ExecutionConcurrencyMode
{
    /// <summary>
    /// وضعیت نامشخص (قبل از تحلیل یا در بارهای تک‌منظوره)
    /// </summary>
    Unknown,

    /// <summary>
    /// همپوشانی زمانی واقعی اثبات‌شده بین بازه‌های اجرای سشن‌های مستقل
    /// </summary>
    ConcurrentOverlap,

    /// <summary>
    /// اجرای متوالی و سریالی بارهای کاری بدون همپوشانی واقعی (مثلاً به دلیل قفل یا منابع تک‌نخی)
    /// </summary>
    Serialized
}

/// <summary>
/// وضعیت تله‌متری و بافرینگ کتابخانه LibVLC حین بنچمارک
/// </summary>
public enum LibVlcTelemetryStatus
{
    /// <summary>
    /// رویدادهای بافرینگ و ایست LibVLC در این لایه پیاده‌سازی/ابزارگذاری نشده‌اند (عدم ادعای کاذب)
    /// </summary>
    UnknownNotInstrumented,

    /// <summary>
    /// عملکرد سالم بدون کشف هیچ‌گونه رویداد افت فریم در لایه دیکودر
    /// </summary>
    HealthyNoStalls,

    /// <summary>
    /// رخداد توقف بافرینگ یا تاخیر بحرانی در دیکودر گزارش شده است
    /// </summary>
    BufferingStallDetected
}
