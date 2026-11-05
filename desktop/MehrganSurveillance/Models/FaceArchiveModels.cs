using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// مدل پایگاه‌داده رویداد والد چهره (Face Event)
/// </summary>
public record FaceEventRecord
{
    public Guid EventId { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public int ChannelIndex { get; init; }
    public DateTime TimestampUtc { get; init; }
    public int FaceCount { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public List<FaceCropRecord> Crops { get; init; } = new();
}

/// <summary>
/// مدل پایگاه‌داده برش اختصاصی چهره (Face Crop)
/// </summary>
public record FaceCropRecord
{
    public Guid CropId { get; init; }
    public Guid EventId { get; init; }
    public ushort CropIndex { get; init; }
    public float Confidence { get; init; }
    public float NormX { get; init; }
    public float NormY { get; init; }
    public float NormW { get; init; }
    public float NormH { get; init; }
    public string EncryptedFilePath { get; init; } = string.Empty;
    public bool IsPendingDeletion { get; init; }
}

/// <summary>
/// فراداده استاندارد و رمزگذاری‌شده موجود در سربرگ باینری فریم چهره
/// </summary>
public record FaceCropMetadata(
    Guid EventId,
    ushort CropIndex,
    long TimestampUtcTicks,
    string CameraId,
    float NormX,
    float NormY,
    float NormW,
    float NormH,
    float Confidence);

/// <summary>
/// وضعیت ذخیره‌سازی آرشیو محلی
/// </summary>
public enum ArchiveStorageState
{
    Healthy,
    LowDiskSpaceWarning,
    StorageFullPaused,
    AccessDeniedOrSecurityFailed
}

/// <summary>
/// فیلترهای جستجو در آرشیو چهره
/// </summary>
public class FaceArchiveFilter
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? CameraId { get; set; }
    public float MinConfidence { get; set; } = 0.0f;
    public int PageSize { get; set; } = 50;
    public int PageIndex { get; set; } = 0;
}

/// <summary>
/// تنظیمات جامع ماژول آرشیو چهره
/// </summary>
public class FaceArchiveConfig
{
    /// <summary>
    /// آرشیو به صورت پیش‌فرض فعال است (Enabled for automated recording)
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// مسیر دایرکتوری اصلی ذخیره محلی تصاویر
    /// </summary>
    public string ArchiveRootPath { get; set; } = "Archive/FaceEvents";

    /// <summary>
    /// حداقل فضای آزاد دیسک برای ادامه ذخیره‌سازی (بر حسب بایت - پیش‌فرض ۵ گیگابایت)
    /// </summary>
    public long MinFreeDiskSpaceBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// ظرفیت صف محدود ذخیره‌سازی
    /// </summary>
    public int ArchiveQueueCapacity { get; set; } = 100;

    /// <summary>
    /// حداقل فاصله مجاز بین ثبت دو رویداد برای یک دوربین مشخص (بر حسب ثانیه)
    /// </summary>
    public double PerCameraRateLimitSeconds { get; set; } = 2.0;

    /// <summary>
    /// کیفیت فشرده‌سازی تصویر برش‌خورده JPEG (بین ۱ تا ۱۰۰)
    /// </summary>
    public int JpegQuality { get; set; } = 85;
}

/// <summary>
/// بسته داده کارگر صف ذخیره‌سازی غیرهمگام
/// </summary>
public sealed class FaceArchiveWorkItem : IDisposable
{
    public FaceDetectionResult Result { get; }
    public AcquiredFrame Frame { get; }

    public FaceArchiveWorkItem(FaceDetectionResult result, AcquiredFrame frame)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public void Dispose()
    {
        Frame.Release();
    }
}
