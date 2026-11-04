using System;
using System.Buffers;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// فریم ویدیویی دریافت شده از استریم دوربین جهت پردازش در خط لوله هوش مصنوعی
/// پیاده‌سازی IDisposable جهت بازگرداندن بافر به مخزن حافظه مشترک (ArrayPool) و پیشگیری قطعی از نشت حافظه
/// </summary>
public sealed class AcquiredFrame : IDisposable
{
    private byte[]? _buffer;
    private bool _disposed;

    public long FrameId { get; init; }
    public int ChannelIndex { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public string PixelFormat { get; init; } = "RV32"; // معمولاً BGRA / RV32 از دیکودر LibVLC
    public bool IsSubStream { get; init; } = true;

    /// <summary>
    /// بافر داده‌های خام پیکسل. می‌تواند از ArrayPool تخصیص یافته باشد.
    /// </summary>
    public ReadOnlyMemory<byte> PixelData => _buffer != null ? new ReadOnlyMemory<byte>(_buffer, 0, DataLength) : ReadOnlyMemory<byte>.Empty;

    public int DataLength { get; init; }

    public AcquiredFrame(
        long frameId,
        int channelIndex,
        string cameraId,
        int width,
        int height,
        int stride,
        string pixelFormat,
        byte[] buffer,
        int dataLength,
        bool isSubStream = true)
    {
        FrameId = frameId;
        ChannelIndex = channelIndex;
        CameraId = cameraId;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        _buffer = buffer;
        DataLength = dataLength;
        IsSubStream = isSubStream;
    }

    /// <summary>
    /// ایجاد یک کپی مستقل یا بافر مشترک برای مصرف‌کننده
    /// </summary>
    public byte[] GetRawBuffer()
    {
        if (_disposed || _buffer == null)
            throw new ObjectDisposedException(nameof(AcquiredFrame));

        return _buffer;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }
}

/// <summary>
/// پیکربندی خط لوله دریافت محلی فریم‌های ویدیویی برای یک کانال یا کل سامانه
/// </summary>
public class FrameAcquisitionConfig
{
    /// <summary>
    /// نرخ نمونه‌برداری فریم برای هوش مصنوعی (به عنوان مثال ۲ الی ۵ فریم بر ثانیه برای نظارت بهینه)
    /// </summary>
    public double TargetFps { get; set; } = 3.0;

    /// <summary>
    /// اولویت قطعی استفاده از استریم فرعی (Sub-stream /1/2) جهت صرفه‌جویی در منابع شبکه و دیکود
    /// </summary>
    public bool PreferSubStream { get; set; } = true;

    /// <summary>
    /// حداکثر ظرفیت صف محدود هر کانال (جهت جلوگیری از انباشت حافظه در تاخیرهای موقت استنتاج)
    /// </summary>
    public int ChannelQueueCapacity { get; set; } = 15;

    /// <summary>
    /// خط‌مشی رفتار صف در زمان پر شدن: DropOldest بهترین حالت برای نظارت ویدیویی زنده است
    /// </summary>
    public QueueOverflowPolicy OverflowPolicy { get; set; } = QueueOverflowPolicy.DropOldest;

    /// <summary>
    /// سقف کل فریم‌های قابل پردازش در ثانیه در کل سامانه (System-wide AI processing budget)
    /// </summary>
    public double SystemWideMaxProcessingFps { get; set; } = 30.0;

    /// <summary>
    /// زمان بازآزمایی و اتصال مجدد در صورت قطع ارتباط RTSP (بر حسب میلی‌ثانیه)
    /// </summary>
    public int ReconnectInitialDelayMs { get; set; } = 2000;

    /// <summary>
    /// حداکثر تاخیر بازآزمایی با روش Exponential Backoff (میلی‌ثانیه)
    /// </summary>
    public int ReconnectMaxDelayMs { get; set; } = 30000;
}

/// <summary>
/// وضعیت جاری کارگر دریافت فریم برای یک کانال دوربین
/// </summary>
public enum FrameGrabberState
{
    Stopped,
    Connecting,
    Streaming,
    Reconnecting,
    Failed,
    PausedByPolicy // متوقف‌شده به دلیل سقف ظرفیت سامانه
}

/// <summary>
/// تله‌متری و آمار لحظه‌ای دریافت فریم در هر کانال دوربین
/// </summary>
public class ChannelAcquisitionTelemetry
{
    public int ChannelIndex { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public FrameGrabberState State { get; set; } = FrameGrabberState.Stopped;
    public string ActiveRtspUrlMasked { get; set; } = string.Empty;
    public bool IsUsingSubStream { get; set; } = true;
    public double CurrentAcquisitionFps { get; set; }
    public int ReconnectAttempts { get; set; }
    public DateTime? LastFrameReceivedUtc { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public FrameAccountingMetrics Accounting { get; init; } = new();
}
