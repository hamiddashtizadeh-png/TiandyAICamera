using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// کادر محاطی چهره آشکارسازی‌شده (صرفاً Bounding Box بدون هیچ‌گونه شناسه یا ویژگی بیومتریک)
/// رعایت اصل حریم خصوصی و امنیت داده‌ها: عدم استخراج یا ذخیره هرگونه embedding یا هویت
/// </summary>
public record FaceBoundingBox
{
    /// <summary>
    /// موقعیت افقی نرمالایز شده (۰.۰ تا ۱.۰) از لبه چپ تصویر
    /// </summary>
    public float NormalizedX { get; init; }

    /// <summary>
    /// موقعیت عمودی نرمالایز شده (۰.۰ تا ۱.۰) از لبه بالای تصویر
    /// </summary>
    public float NormalizedY { get; init; }

    /// <summary>
    /// عرض نرمالایز شده کادر چهره (۰.۰ تا ۱.۰)
    /// </summary>
    public float NormalizedWidth { get; init; }

    /// <summary>
    /// ارتفاع نرمالایز شده کادر چهره (۰.۰ تا ۱.۰)
    /// </summary>
    public float NormalizedHeight { get; init; }

    /// <summary>
    /// ضریب اطمینان تشخیص چهره (بین ۰.۰ و ۱.۰)
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// تبدیل مختصات نرمالایز به پیکسل‌های تصویر واقعی
    /// </summary>
    public (int X, int Y, int Width, int Height) ToPixelCoordinates(int imageWidth, int imageHeight)
    {
        int x = (int)Math.Max(0, Math.Min(imageWidth - 1, NormalizedX * imageWidth));
        int y = (int)Math.Max(0, Math.Min(imageHeight - 1, NormalizedY * imageHeight));
        int w = (int)Math.Max(1, Math.Min(imageWidth - x, NormalizedWidth * imageWidth));
        int h = (int)Math.Max(1, Math.Min(imageHeight - y, NormalizedHeight * imageHeight));
        return (x, y, w, h);
    }
}

/// <summary>
/// نتیجه حاصل از استنتاج مدل آشکارسازی چهره روی یک فریم مشخص
/// غیرهویتی (Strictly Non-Identifying): صرفاً تعداد چهره و موقعیت فضایی کادرها
/// </summary>
public class FaceDetectionResult : IDisposable
{
    private byte[]? _anonymizedPixelBuffer;

    public Guid ResultId { get; init; } = Guid.NewGuid();
    public long FrameId { get; init; }
    public int ChannelIndex { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// لیست کادرهای چهره آشکارسازی‌شده در این فریم
    /// </summary>
    public List<FaceBoundingBox> Faces { get; init; } = new();

    /// <summary>
    /// شمارش کل چهره‌های یافت‌شده (Face Count)
    /// </summary>
    public int FaceCount => Faces.Count;

    // معیارهای زمانی پردازش خط لوله
    public double PreprocessingLatencyMs { get; init; }
    public double InferenceLatencyMs { get; init; }
    public double PostprocessingLatencyMs { get; init; }
    public double TotalLatencyMs => PreprocessingLatencyMs + InferenceLatencyMs + PostprocessingLatencyMs;

    /// <summary>
    /// پرووایدر اجرایی استفاده‌شده (DirectML با شتاب سخت‌افزاری یا CPU به عنوان جایگزین)
    /// </summary>
    public string ExecutionProviderUsed { get; init; } = "DirectML";

    /// <summary>
    /// آیا عملیات محوسازی/حفظ حریم خصوصی (Anonymization Blur) بر روی چهره‌ها اعمال شده است؟
    /// </summary>
    public bool AnonymizationApplied { get; init; }

    /// <summary>
    /// بافر مستقل و اختصاصی کپی تصویر محوسازی‌شده جهت خروجی بدون دستکاری فریم اصلی
    /// </summary>
    public byte[]? AnonymizedPixelBuffer
    {
        get => System.Threading.Volatile.Read(ref _anonymizedPixelBuffer);
        init => _anonymizedPixelBuffer = value;
    }

    public void Dispose()
    {
        byte[]? bufferToReturn = System.Threading.Interlocked.Exchange(ref _anonymizedPixelBuffer, null);
        if (bufferToReturn != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bufferToReturn);
        }
    }
}

/// <summary>
/// پیکربندی خط لوله آشکارسازی چهره محلی
/// مطابق الزامات امنیتی: به صورت پیش‌فرض غیرفعال است (IsEnabled = false) مگر آنکه توسط مدیر سیستم فعال گردد
/// </summary>
public class FaceDetectionConfig
{
    /// <summary>
    /// وضعیت فعال‌بودن خط لوله. پیش‌فرض: فعال (Enabled for live monitoring)
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// مدل انتخاب‌شده: Ultra-Face (RFB-320)
    /// لایسنس: MIT License (آزاد و قابل بازتوزیع)
    /// اندازه مدل: ۱.۱ مگابایت، بسیار سبک و سازگار با DirectML و CPU
    /// </summary>
    public string ModelName { get; set; } = "Ultra-Face-RFB-320";
    public string ModelPath { get; set; } = "Models/Weights/version-RFB-320.onnx";

    /// <summary>
    /// ابعاد ورودی تنسور استاندارد مدل RFB-320
    /// </summary>
    public int InputWidth { get; set; } = 320;
    public int InputHeight { get; set; } = 240;

    /// <summary>
    /// آستانه ضریب اطمینان جهت فیلتر کادرهای ضعیف (بین ۰.۰ و ۱.۰)
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.65f;

    /// <summary>
    /// آستانه انطباق هندسی برای حذف کادرهای همپوشان (Non-Maximum Suppression IoU)
    /// </summary>
    public float IouThreshold { get; set; } = 0.40f;

    /// <summary>
    /// حداکثر تعداد چهره‌های مجاز برای آشکارسازی در هر فریم (جهت پیشگیری از بار محاسباتی اضافی)
    /// </summary>
    public int MaxFacesPerFrame { get; set; } = 50;

    /// <summary>
    /// فعال‌سازی محوسازی خودکار نواحی چهره جهت انطباق با قوانین حفظ حریم خصوصی (GDPR / حفاظت داده)
    /// </summary>
    public bool EnableFaceAnonymizationBlur { get; set; } = false;

    /// <summary>
    /// شدت محوسازی کادر چهره (Box/Pixelation Kernel Size)
    /// </summary>
    public int AnonymizationPixelationBlockSize { get; set; } = 12;

    /// <summary>
    /// نرخ نمونه‌برداری فریم برای آشکارسازی چهره (فریم بر ثانیه)
    /// </summary>
    public double FaceSamplingFps { get; set; } = 2.0;

    /// <summary>
    /// ظرفیت صف محدود استنتاج چهره
    /// </summary>
    public int FaceQueueCapacity { get; set; } = 10;
}
