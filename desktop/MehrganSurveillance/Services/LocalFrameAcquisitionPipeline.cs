using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using MehrganSurveillance.Models;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MehrganSurveillance.Services;

/// <summary>
/// رابط دریافت‌کننده و مصرف‌کننده فریم در ماژول‌های هوش مصنوعی (مانند چهره یا پلاک)
/// </summary>
public interface IFrameConsumer
{
    AiWorkloadType Workload { get; }
    string ConsumerName { get; }
    ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken);
}

/// <summary>
/// رابط منبع دریافت فریم (استریم RTSP زنده، فایل ویدیویی یا شبیه‌ساز تست)
/// </summary>
public interface IFrameGrabber : IAsyncDisposable
{
    int ChannelIndex { get; }
    string CameraId { get; }
    FrameGrabberState State { get; }
    string ActiveUrlMasked { get; }
    bool IsUsingSubStream { get; }

    event Action<AcquiredFrame>? FrameAcquired;
    event Action<string>? StateChanged;

    Task StartAsync(CameraChannel channel, FrameAcquisitionConfig config, CancellationToken cancellationToken);
    Task StopAsync();
}

/// <summary>
/// خط لوله محلی دریافت و توزیع فریم‌ها برای هوش مصنوعی (Phase Zero Step 5)
/// کاملاً مستقل از پخش زنده UI در LibVLC، با صف‌های محدود، خط‌مشی‌های سرریز، حسابداری دقیق و مصرف‌کنندگان تفکیک‌شده
/// </summary>
public class LocalFrameAcquisitionPipeline : IAsyncDisposable
{
    private readonly LibVLC? _sharedLibVlc;
    private readonly AiCapacityPolicy? _capacityPolicy;
    private readonly FrameAcquisitionConfig _globalConfig;

    private readonly ConcurrentDictionary<int, ChannelContext> _activeChannels = new();
    private readonly ConcurrentDictionary<AiWorkloadType, IFrameConsumer> _consumers = new();

    private readonly CancellationTokenSource _pipelineCts = new();
    private bool _isDisposed;

    public event Action<string>? LogMessage;
    public event Action<int, ChannelAcquisitionTelemetry>? TelemetryUpdated;

    public LocalFrameAcquisitionPipeline(
        LibVLC? sharedLibVlc = null,
        AiCapacityPolicy? capacityPolicy = null,
        FrameAcquisitionConfig? globalConfig = null)
    {
        _sharedLibVlc = sharedLibVlc;
        _capacityPolicy = capacityPolicy;
        _globalConfig = globalConfig ?? new FrameAcquisitionConfig();
    }

    /// <summary>
    /// ثبت یک مصرف‌کننده مستقل (مثلاً آشکارساز چهره یا پلاک‌خوان)
    /// </summary>
    public void RegisterConsumer(IFrameConsumer consumer)
    {
        if (consumer == null) throw new ArgumentNullException(nameof(consumer));
        _consumers[consumer.Workload] = consumer;
        Log($"مصرف‌کننده هوش مصنوعی '{consumer.ConsumerName}' برای بار کاری {consumer.Workload} با موفقیت ثبت گردید.");
    }

    public void UnregisterConsumer(AiWorkloadType workload)
    {
        _consumers.TryRemove(workload, out _);
    }

    /// <summary>
    /// راه‌اندازی دریافت فریم برای یک کانال مشخص دوربین با استفاده از استریم فرعی (Sub-stream /1/2)
    /// </summary>
    public async Task<bool> StartChannelAcquisitionAsync(CameraChannel channel, FrameAcquisitionConfig? channelConfig = null)
    {
        if (channel == null) throw new ArgumentNullException(nameof(channel));
        if (!channel.IsAssigned)
        {
            Log($"[کانال {channel.SlotIndex + 1}] خطا: دوربینی به این کانال تخصیص نیافته است.");
            return false;
        }

        var config = channelConfig ?? _globalConfig;

        // بررسی سقف مجاز ظرفیت سامانه (Respect system-wide AI capacity budget)
        if (_capacityPolicy != null)
        {
            int currentActiveCount = _activeChannels.Count;
            int recommendedLimit = _capacityPolicy.RecommendedChannelsFor(AiWorkloadType.Combined);
            if (recommendedLimit > 0 && currentActiveCount >= recommendedLimit)
            {
                Log($"[هشدار خط‌مشی ظرفیت] تعداد کانال‌های هوش مصنوعی فعال ({currentActiveCount}) به سقف توصیه شده خط‌مشی ({recommendedLimit}) رسیده است. فعال‌سازی با نرخ فریم کنترل‌شده انجام می‌شود.");
            }
        }

        // اگر کانال قبلاً فعال بوده متوقف می‌گردد
        await StopChannelAcquisitionAsync(channel.SlotIndex);

        var context = new ChannelContext(channel.SlotIndex, channel.Id, config);
        if (!_activeChannels.TryAdd(channel.SlotIndex, context))
        {
            return false;
        }

        // ایجاد منبع دریافت فریم (در صورت در دسترس بودن LibVLC از گربر بومی و در غیر این صورت از گربر سنتتیک شبیه‌سازی استفاده می‌شود)
        IFrameGrabber grabber = _sharedLibVlc != null
            ? new LibVlcHeadlessFrameGrabber(channel.SlotIndex, channel.Id, _sharedLibVlc)
            : new SyntheticFrameGrabber(channel.SlotIndex, channel.Id);

        context.Grabber = grabber;
        grabber.FrameAcquired += frame => OnFrameAcquired(context, frame);
        grabber.StateChanged += msg => Log($"[کانال {channel.SlotIndex + 1}] {msg}");

        // شروع حلقه پردازش و ارسال فریم به مصرف‌کنندگان
        context.DispatcherTask = Task.Run(() => ChannelDispatchWorkerAsync(context, _pipelineCts.Token));

        // شروع دریافت فریم از استریم دوربین
        try
        {
            await grabber.StartAsync(channel, config, _pipelineCts.Token);
            Log($"[کانال {channel.SlotIndex + 1}] دریافت فریم‌های هوش مصنوعی آغاز شد (هدف: {config.TargetFps:F1} FPS، استریم فرعی: {config.PreferSubStream}).");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[کانال {channel.SlotIndex + 1}] خطا در راه‌اندازی دریافت فریم: {ex.Message}");
            await StopChannelAcquisitionAsync(channel.SlotIndex);
            return false;
        }
    }

    /// <summary>
    /// متوقف‌سازی ایمن دریافت فریم برای یک کانال مشخص
    /// </summary>
    public async Task StopChannelAcquisitionAsync(int slotIndex)
    {
        if (_activeChannels.TryRemove(slotIndex, out var context))
        {
            try
            {
                context.Cts.Cancel();
                if (context.Grabber != null)
                {
                    await context.Grabber.StopAsync();
                    await context.Grabber.DisposeAsync();
                }

                context.Queue.Writer.TryComplete();
                if (context.DispatcherTask != null)
                {
                    await Task.WhenAny(context.DispatcherTask, Task.Delay(500));
                }

                // تخلیه و آزادسازی فریم‌های باقیمانده در صف جهت بازگشت به ArrayPool
                while (context.Queue.Reader.TryRead(out var leftoverFrame))
                {
                    context.Accounting.OnEvictedFromQueue();
                    leftoverFrame.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"[کانال {slotIndex + 1}] استثنا در توقف دریافت فریم: {ex.Message}");
            }
            finally
            {
                context.Dispose();
                Log($"[کانال {slotIndex + 1}] دریافت فریم متوقف و کلیه منابع آزاد گردید.");
            }
        }
    }

    /// <summary>
    /// دریافت فریم خام از Grabber و مدیریت درج در صف بر اساس خط‌مشی سرریز
    /// </summary>
    private void OnFrameAcquired(ChannelContext context, AcquiredFrame frame)
    {
        context.Accounting.IncrementProduced();

        switch (context.Config.OverflowPolicy)
        {
            case QueueOverflowPolicy.DropOldest:
                // تلاش برای درج بدون مسدودسازی؛ در صورت پر بودن صف، قدیمی‌ترین فریم اخراج می‌شود
                while (!context.Queue.Writer.TryWrite(frame))
                {
                    if (context.Queue.Reader.TryRead(out var oldestFrame))
                    {
                        context.Accounting.OnEvictedFromQueue();
                        oldestFrame.Dispose(); // آزادسازی بافر به ArrayPool
                    }
                    else
                    {
                        break;
                    }
                }
                context.Accounting.OnEnqueued();
                break;

            case QueueOverflowPolicy.DropNewest:
                if (context.Queue.Writer.TryWrite(frame))
                {
                    context.Accounting.OnEnqueued();
                }
                else
                {
                    // صف پر است: فریم جدید بلافاصله دور ریخته می‌شود
                    context.Accounting.IncrementRejectedBeforeEnqueue();
                    frame.Dispose();
                }
                break;

            case QueueOverflowPolicy.BlockProducer:
                // ثبت در صف به صورت غیرمسدودکننده در صورت امکان، در غیر این صورت تلاش با تاخیر کوتاه
                if (context.Queue.Writer.TryWrite(frame))
                {
                    context.Accounting.OnEnqueued();
                }
                else
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            await context.Queue.Writer.WriteAsync(frame, context.Cts.Token);
                            context.Accounting.OnEnqueued();
                        }
                        catch
                        {
                            context.Accounting.IncrementRejectedBeforeEnqueue();
                            frame.Dispose();
                        }
                    });
                }
                break;
        }
    }

    /// <summary>
    /// حلقه غیرهمگام توزیع فریم‌ها به مصرف‌کنندگان مستقل هوش مصنوعی
    /// </summary>
    private async Task ChannelDispatchWorkerAsync(ChannelContext context, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.Cts.Token, cancellationToken);
        var token = linkedCts.Token;

        try
        {
            while (await context.Queue.Reader.WaitToReadAsync(token))
            {
                while (context.Queue.Reader.TryRead(out var frame))
                {
                    context.Accounting.OnDequeued();
                    context.Accounting.OnInFlightStarted();

                    bool success = true;
                    try
                    {
                        // ارسال به کلیه مصرف‌کنندگان ثبت‌شده به صورت مستقل با حفظ اجاره مجزا برای هر مصرف‌کننده
                        var activeConsumers = _consumers.Values.ToList();
                        if (activeConsumers.Count > 0)
                        {
                            foreach (var consumer in activeConsumers)
                            {
                                try
                                {
                                    if (frame.TryRetain())
                                    {
                                        await consumer.OnFrameAvailableAsync(frame, token);
                                    }
                                }
                                catch (Exception cEx)
                                {
                                    Log($"[کانال {context.ChannelIndex + 1}] خطا در مصرف‌کننده '{consumer.ConsumerName}': {cEx.Message}");
                                    success = false;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[کانال {context.ChannelIndex + 1}] استثنا در پردازش فریم: {ex.Message}");
                        success = false;
                    }
                    finally
                    {
                        context.Accounting.OnInFlightCompleted(success);
                        // آزادسازی اجاره توزیع‌کننده (Dispatcher Lease). بافر تنها زمانی به Pool بازمی‌گردد که کلیه مصرف‌کنندگان اجاره خود را آزاد کرده باشند.
                        frame.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // خروج عادی در زمان لغو یا توقف
        }
    }

    public ChannelAcquisitionTelemetry? GetChannelTelemetry(int slotIndex)
    {
        if (_activeChannels.TryGetValue(slotIndex, out var context))
        {
            return new ChannelAcquisitionTelemetry
            {
                ChannelIndex = slotIndex,
                CameraId = context.CameraId,
                State = context.Grabber?.State ?? FrameGrabberState.Stopped,
                ActiveRtspUrlMasked = context.Grabber?.ActiveUrlMasked ?? string.Empty,
                IsUsingSubStream = context.Grabber?.IsUsingSubStream ?? true,
                CurrentAcquisitionFps = context.Config.TargetFps,
                Accounting = context.Accounting.CreateSnapshot()
            };
        }
        return null;
    }

    public List<ChannelAcquisitionTelemetry> GetAllTelemetries()
    {
        return _activeChannels.Keys.Select(k => GetChannelTelemetry(k)).Where(t => t != null).Select(t => t!).ToList();
    }

    private void Log(string message)
    {
        LogMessage?.Invoke($"[AI Frame Pipeline] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _pipelineCts.Cancel();

            var stopTasks = _activeChannels.Keys.Select(k => StopChannelAcquisitionAsync(k)).ToArray();
            await Task.WhenAll(stopTasks);

            _pipelineCts.Dispose();
        }
    }

    #region Inner Channel Context

    private sealed class ChannelContext : IDisposable
    {
        public int ChannelIndex { get; }
        public string CameraId { get; }
        public FrameAcquisitionConfig Config { get; }
        public Channel<AcquiredFrame> Queue { get; }
        public FrameAccountingMetrics Accounting { get; } = new();
        public IFrameGrabber? Grabber { get; set; }
        public Task? DispatcherTask { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        public ChannelContext(int channelIndex, string cameraId, FrameAcquisitionConfig config)
        {
            ChannelIndex = channelIndex;
            CameraId = cameraId;
            Config = config;

            var options = new BoundedChannelOptions(Math.Max(5, config.ChannelQueueCapacity))
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            };
            Queue = Channel.CreateBounded<AcquiredFrame>(options);
        }

        public void Dispose()
        {
            Cts.Dispose();
        }
    }

    #endregion
}

/// <summary>
/// منبع دریافت فریم بدون واسط کاربری (Headless) با LibVLC با استفاده از توابع Callback حافظه
/// دیکود مستقیم بر بستر TCP و استریم سبک فرعی (/1/2) بدون ایجاد پنجره یا تصادم با Live View
/// </summary>
public sealed class LibVlcHeadlessFrameGrabber : IFrameGrabber
{
    private readonly int _channelIndex;
    private readonly string _cameraId;
    private readonly LibVLC _libVlc;
    private VlcMediaPlayer? _mediaPlayer;
    private CancellationTokenSource? _reconnectCts;

    private int _videoWidth = 640;
    private int _videoHeight = 360;
    private int _pixelPitch = 4; // RV32 = 4 bytes per pixel

    private long _frameCounter;
    private DateTime _lastFrameAcquiredTime = DateTime.MinValue;
    private double _targetFps = 3.0;

    public int ChannelIndex => _channelIndex;
    public string CameraId => _cameraId;
    public FrameGrabberState State { get; private set; } = FrameGrabberState.Stopped;
    public string ActiveUrlMasked { get; private set; } = string.Empty;
    public bool IsUsingSubStream { get; private set; } = true;

    public event Action<AcquiredFrame>? FrameAcquired;
    public event Action<string>? StateChanged;

    public LibVlcHeadlessFrameGrabber(int channelIndex, string cameraId, LibVLC libVlc)
    {
        _channelIndex = channelIndex;
        _cameraId = cameraId;
        _libVlc = libVlc;
    }

    public Task StartAsync(CameraChannel channel, FrameAcquisitionConfig config, CancellationToken cancellationToken)
    {
        _targetFps = config.TargetFps > 0 ? config.TargetFps : 3.0;
        IsUsingSubStream = config.PreferSubStream;
        string targetUrl = channel.GetEffectiveStreamUrl(IsUsingSubStream);

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            State = FrameGrabberState.Failed;
            StateChanged?.Invoke("آدرس استریم جهت دریافت فریم خالی است.");
            return Task.CompletedTask;
        }

        ActiveUrlMasked = CredentialProtectionService.MaskRtspUrl(targetUrl);
        State = FrameGrabberState.Connecting;
        StateChanged?.Invoke($"اتصال به استریم جهت استخراج فریم‌های هوش مصنوعی ({ActiveUrlMasked})");

        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _mediaPlayer = new VlcMediaPlayer(_libVlc);

            // تنظیم کال‌بک‌های ویدیویی حافظه برای استخراج فریم‌ها بدون رندرینگ HWND
            _mediaPlayer.SetVideoFormatCallbacks(VideoFormatSetupCallback, null);
            _mediaPlayer.SetVideoCallbacks(LockVideoCallback, null, DisplayVideoCallback);

            _mediaPlayer.Playing += (s, e) =>
            {
                State = FrameGrabberState.Streaming;
                StateChanged?.Invoke("استریم هوش مصنوعی با موفقیت برقرار شد.");
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                State = FrameGrabberState.Reconnecting;
                StateChanged?.Invoke("قطع موقت ارتباط RTSP؛ تلاش خودکار برای برقراری مجدد...");
            };

            using var media = new Media(_libVlc, new Uri(targetUrl));
            _mediaPlayer.Play(media);
        }
        catch (Exception ex)
        {
            State = FrameGrabberState.Failed;
            StateChanged?.Invoke($"خطا در ایجاد پخش‌کننده پس‌زمینه: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private uint VideoFormatSetupCallback(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // درخواست فرمت RV32 (BGRA 32-bit)
        byte[] rv32 = System.Text.Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(rv32, 0, chroma, 4);

        if (width == 0) width = (uint)_videoWidth;
        if (height == 0) height = (uint)_videoHeight;

        _videoWidth = (int)width;
        _videoHeight = (int)height;
        pitches = width * (uint)_pixelPitch;
        lines = height;

        return 1;
    }

    private IntPtr LockVideoCallback(IntPtr opaque, IntPtr planes)
    {
        int byteSize = _videoWidth * _videoHeight * _pixelPitch;
        IntPtr unmanagedBuffer = Marshal.AllocHGlobal(byteSize);
        Marshal.WriteIntPtr(planes, unmanagedBuffer);
        return unmanagedBuffer;
    }

    private void DisplayVideoCallback(IntPtr opaque, IntPtr picture)
    {
        if (picture == IntPtr.Zero) return;

        try
        {
            var now = DateTime.UtcNow;
            double minIntervalSec = 1.0 / _targetFps;

            // کنترل نرخ نمونه‌برداری (Rate-limiting to target FPS) جهت رعایت بودجه پردازش سیستم
            if ((now - _lastFrameAcquiredTime).TotalSeconds >= minIntervalSec)
            {
                _lastFrameAcquiredTime = now;
                int byteSize = _videoWidth * _videoHeight * _pixelPitch;

                // دریافت یک بافر با اندازه مناسب از مخزن مشترک ArrayPool (Zero GC allocation)
                byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(byteSize);
                Marshal.Copy(picture, pooledBuffer, 0, byteSize);

                long frameId = Interlocked.Increment(ref _frameCounter);
                var frame = new AcquiredFrame(
                    frameId,
                    _channelIndex,
                    _cameraId,
                    _videoWidth,
                    _videoHeight,
                    _videoWidth * _pixelPitch,
                    "RV32",
                    pooledBuffer,
                    byteSize,
                    IsUsingSubStream);

                FrameAcquired?.Invoke(frame);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(picture);
        }
    }

    public Task StopAsync()
    {
        State = FrameGrabberState.Stopped;
        _reconnectCts?.Cancel();

        if (_mediaPlayer != null)
        {
            try
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }
                _mediaPlayer.Dispose();
            }
            catch
            {
            }
            finally
            {
                _mediaPlayer = null;
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _reconnectCts?.Dispose();
    }
}

/// <summary>
/// منبع تولید فریم‌های مصنوعی/شبیه‌سازی شده جهت آزمون‌های واحد، بنچمارک و تست بدون دوربین فیزیکی
/// </summary>
public sealed class SyntheticFrameGrabber : IFrameGrabber
{
    private readonly int _channelIndex;
    private readonly string _cameraId;
    private CancellationTokenSource? _cts;
    private long _frameCounter;

    public int ChannelIndex => _channelIndex;
    public string CameraId => _cameraId;
    public FrameGrabberState State { get; private set; } = FrameGrabberState.Stopped;
    public string ActiveUrlMasked { get; private set; } = "synthetic://frame-generator";
    public bool IsUsingSubStream => true;

    public event Action<AcquiredFrame>? FrameAcquired;
    public event Action<string>? StateChanged;

    public SyntheticFrameGrabber(int channelIndex, string cameraId)
    {
        _channelIndex = channelIndex;
        _cameraId = cameraId;
    }

    public Task StartAsync(CameraChannel channel, FrameAcquisitionConfig config, CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        State = FrameGrabberState.Streaming;
        StateChanged?.Invoke("شبیه‌ساز فریم‌های هوش مصنوعی فعال شد.");

        int width = 640;
        int height = 360;
        int stride = width * 4;
        int byteSize = stride * height;
        double intervalMs = 1000.0 / Math.Max(0.5, config.TargetFps);

        Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay((int)intervalMs, token);

                    byte[] pooled = ArrayPool<byte>.Shared.Rent(byteSize);
                    // پر کردن الگوی شبیه‌سازی رنگی ثابت
                    Array.Clear(pooled, 0, Math.Min(pooled.Length, byteSize));

                    long frameId = Interlocked.Increment(ref _frameCounter);
                    var frame = new AcquiredFrame(
                        frameId,
                        _channelIndex,
                        _cameraId,
                        width,
                        height,
                        stride,
                        "RV32",
                        pooled,
                        byteSize,
                        isSubStream: true);

                    FrameAcquired?.Invoke(frame);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        State = FrameGrabberState.Stopped;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
