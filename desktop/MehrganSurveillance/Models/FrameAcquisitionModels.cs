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
    private int _refCount = 1; // Producer initial lease
    private int _primaryDisposed = 0;

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

    /// <summary>
    /// تعداد اجاره‌های فعال جاری روی این فریم
    /// </summary>
    public int ReferenceCount => System.Threading.Volatile.Read(ref _refCount);

    /// <summary>
    /// آیا تمامی اجاره‌های بافر آزاد شده و بافر به ArrayPool بازگشته است؟
    /// </summary>
    public bool IsDisposed => System.Threading.Volatile.Read(ref _refCount) <= 0 || _buffer == null;

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
        _refCount = 1;
    }

    /// <summary>
    /// تلاش برای ثبت یک اجاره/ارجاع جدید توسط مصرف‌کننده غیرهمگام
    /// در صورت فعال بودن بافر، شمارنده ارجاع یک واحد افزایش یافته و true بازمی‌گردد
    /// </summary>
    public bool TryRetain()
    {
        while (true)
        {
            int current = System.Threading.Volatile.Read(ref _refCount);
            if (current <= 0 || _buffer == null) return false;
            if (System.Threading.Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return true;
        }
    }

    /// <summary>
    /// آزادسازی یک اجاره/ارجاع. با رسیدن تعداد ارجاعات به صفر، بافر دقیقاً یک‌بار به ArrayPool بازگردانده می‌شود.
    /// </summary>
    public void Release()
    {
        while (true)
        {
            int current = System.Threading.Volatile.Read(ref _refCount);
            if (current <= 0) return; // قبلاً به طور کامل آزاد شده است
            int next = current - 1;
            if (System.Threading.Interlocked.CompareExchange(ref _refCount, next, current) == current)
            {
                if (next == 0)
                {
                    byte[]? buf = System.Threading.Interlocked.Exchange(ref _buffer, null);
                    if (buf != null)
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// دریافت بافر خام جهت دسترسی خواندن. در صورت آزادسازی تمام اجاره‌ها، ObjectDisposedException پرتاب می‌شود.
    /// </summary>
    public byte[] GetRawBuffer()
    {
        byte[]? buf = _buffer;
        if (IsDisposed || buf == null)
            throw new ObjectDisposedException(nameof(AcquiredFrame));

        return buf;
    }

    /// <summary>
    /// آزادسازی اجاره دارنده اصلی (Producer / Dispatcher Lease)
    /// جهت جلوگیری از اثر منفی فراخوانی‌های مکرر Dispose روی یک ارجاع، از Interlocked.Exchange استفاده می‌شود.
    /// </summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _primaryDisposed, 1) == 0)
        {
            Release();
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
