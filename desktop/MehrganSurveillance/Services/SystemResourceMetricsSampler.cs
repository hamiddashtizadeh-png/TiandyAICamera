using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// موتور نمونه‌برداری دقیق و محدود از معیارهای تاخیر، منابع سیستمی و سلامت واسط کاربری
/// 
/// اصول طراحی:
/// ۱. استفاده از نمونه‌برداری مخزنی با ظرفیت کراندار K=4000 (Algorithm R) جهت برآورد آماری P50/P95
/// ۲. تفکیک دقیق معیارهای حافظه مدیریت‌شده دات‌نت، بایت‌های اختصاصی پروسه، Working Set و VRAM
/// ۳. نگهداری VRAM به صورت null/Unknown در صورت عدم دسترسی به DXGI و اجتناب قطعی از جایگزینی مقدار صفر
/// ۴. محاسبه شیب رشد حافظه (MB/hour) با رگرسیون خطی
/// ۵. پایش ایمن و غیرانباشته‌شونده تاخیر دیسپچر WPF بدون بلاک کردن ثردهای عملیاتی
/// </summary>
public class SystemResourceMetricsSampler
{
    private const int ReservoirCapacityK = 4000;

    // مخزن‌های نمونه‌برداری Algorithm R برای مراحل مختلف خط لوله
    private readonly ReservoirSampler _acquisitionReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _preprocessingReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _inferenceReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _postprocessingReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _endToEndReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _queueWaitReservoir = new(ReservoirCapacityK);
    private readonly ReservoirSampler _dispatcherLagReservoir = new(ReservoirCapacityK);

    // سابقه نمونه‌های حافظه جهت محاسبه رگرسیون خطی شیب رشد (MB/h)
    private readonly List<(DateTime UtcTime, long PrivateBytes, long ManagedHeapBytes, long WorkingSetBytes)> _memoryHistory = new();
    private readonly object _memoryLock = new();

    // همگام‌ساز پایش دیسپچر واسط کاربری (جلوگیری از انباشت تسک‌های پینگ در صورت کندی UI)
    private readonly SemaphoreSlim _dispatcherProbeLock = new(1, 1);
    private double _lastMeasuredDispatcherLagMs = 0.0;

    private long? _peakGpuVramBytes = null;

    #region Reservoir Recording Methods

    public void RecordAcquisitionLatencyMs(double latencyMs) => _acquisitionReservoir.Add(latencyMs);
    public void RecordPreprocessingLatencyMs(double latencyMs) => _preprocessingReservoir.Add(latencyMs);
    public void RecordInferenceLatencyMs(double latencyMs) => _inferenceReservoir.Add(latencyMs);
    public void RecordPostprocessingLatencyMs(double latencyMs) => _postprocessingReservoir.Add(latencyMs);
    public void RecordEndToEndLatencyMs(double latencyMs) => _endToEndReservoir.Add(latencyMs);
    public void RecordQueueWaitTimeMs(double waitTimeMs) => _queueWaitReservoir.Add(waitTimeMs);

    #endregion

    #region Latency Statistical Estimates (P50 / P95)

    public (double P50, double P95, long Count) GetAcquisitionLatencies() => _acquisitionReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetPreprocessingLatencies() => _preprocessingReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetInferenceLatencies() => _inferenceReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetPostprocessingLatencies() => _postprocessingReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetEndToEndLatencies() => _endToEndReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetQueueWaitLatencies() => _queueWaitReservoir.GetPercentiles();
    public (double P50, double P95, long Count) GetDispatcherLagLatencies() => _dispatcherLagReservoir.GetPercentiles();

    #endregion

    #region Memory Sampling & Linear Regression

    /// <summary>
    /// ثبت یک نمونه جدید از مصرف حافظه پروسه و سامانه
    /// </summary>
    public MemoryStabilityMetrics SampleMemoryMetrics(TimeSpan elapsedDuration, double maxAcceptableSlopeMbPerHour = 50.0)
    {
        long managedHeap = GC.GetTotalMemory(false);
        long privateBytes;
        long workingSet;

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            privateBytes = currentProcess.PrivateMemorySize64;
            workingSet = currentProcess.WorkingSet64;
        }
        catch
        {
            privateBytes = managedHeap;
            workingSet = managedHeap;
        }

        // استخراج حافظه VRAM در ویندوز از طریق DXGI در صورت وجود (در غیر این صورت null)
        long? gpuVramUsed = ProbeDedicatedGpuMemoryUsageBytes();
        if (gpuVramUsed.HasValue)
        {
            if (!_peakGpuVramBytes.HasValue || gpuVramUsed.Value > _peakGpuVramBytes.Value)
            {
                _peakGpuVramBytes = gpuVramUsed.Value;
            }
        }

        var now = DateTime.UtcNow;
        lock (_memoryLock)
        {
            _memoryHistory.Add((now, privateBytes, managedHeap, workingSet));
        }

        double slopeMbPerHour = CalculateMemorySlopeMbPerHour();
        bool isSlopeAcceptable = elapsedDuration < TimeSpan.FromMinutes(2) || slopeMbPerHour <= maxAcceptableSlopeMbPerHour;

        return new MemoryStabilityMetrics
        {
            ManagedHeapBytes = managedHeap,
            ProcessPrivateBytes = privateBytes,
            ProcessWorkingSetBytes = workingSet,
            GpuDedicatedUsedBytes = gpuVramUsed,
            GpuDedicatedPeakBytes = _peakGpuVramBytes,
            MeasuredDuration = elapsedDuration,
            MemoryGrowthRateMbPerHour = slopeMbPerHour,
            IsMemorySlopeAcceptable = isSlopeAcceptable
        };
    }

    /// <summary>
    /// محاسبه شیب افزایش حافظه در ساعت (MB/hour) با استفاده از روش رگرسیون خطی Least Squares
    /// بر مبنای بایت‌های اختصاصی پروسه (Process Private Bytes)
    /// </summary>
    public double CalculateMemorySlopeMbPerHour()
    {
        List<(DateTime UtcTime, long PrivateBytes, long ManagedHeapBytes, long WorkingSetBytes)> history;
        lock (_memoryLock)
        {
            if (_memoryHistory.Count < 2)
            {
                return 0.0;
            }
            history = new List<(DateTime UtcTime, long PrivateBytes, long ManagedHeapBytes, long WorkingSetBytes)>(_memoryHistory);
        }

        DateTime t0 = history[0].UtcTime;
        int n = history.Count;

        double sumT = 0.0;
        double sumY = 0.0;
        double sumT2 = 0.0;
        double sumTY = 0.0;

        foreach (var item in history)
        {
            double seconds = (item.UtcTime - t0).TotalSeconds;
            double mb = item.PrivateBytes / (1024.0 * 1024.0);

            sumT += seconds;
            sumY += mb;
            sumT2 += seconds * seconds;
            sumTY += seconds * mb;
        }

        double denominator = (n * sumT2) - (sumT * sumT);
        if (Math.Abs(denominator) < 1e-9)
        {
            return 0.0;
        }

        // شیب بر حسب مگابایت بر ثانیه
        double slopeMbPerSecond = ((n * sumTY) - (sumT * sumY)) / denominator;

        // تبدیل به مگابایت در ساعت
        double slopeMbPerHour = slopeMbPerSecond * 3600.0;

        // شیب‌های منفی (ناشی از GC) به صورت پایدار ۰ در نظر گرفته می‌شوند
        return Math.Round(Math.Max(0.0, slopeMbPerHour), 2);
    }

    /// <summary>
    /// کاوش میزان مصرف جاری VRAM اختصاصی توسط DXGI در صورت پشتیبانی سیستم‌عامل
    /// در صورت عدم پشتیبانی یا اجرا روی لینوکس، null بازمی‌گرداند (هیچ‌گاه صفر جایگزین نمی‌شود)
    /// </summary>
    private long? ProbeDedicatedGpuMemoryUsageBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null; // عدم ادعای کاذب در محیط‌های غیر ویندوز
        }

        // در ویندوز، استخراج دقیق مصرف لحظه‌ای VRAM به اینترفیس IDXGIAdapter3 وابسته است.
        // در غیاب پیاده‌سازی اینجکشن C++ COM، مقدار به صورت صریح null برگردانده می‌شود.
        return null;
    }

    #endregion

    #region UI Dispatcher Lag Observer

    /// <summary>
    /// پایش غیرانباشته‌شونده تاخیر پاسخگویی Dispatcher واسط کاربری (WPF)
    /// جهت تضمین عدم افت فریم یا فریز در پخش زنده دوربین‌ها حین پردازش سنگین هوش مصنوعی
    /// </summary>
    public async Task<double> SampleUiDispatcherLagAsync(CancellationToken cancellationToken = default)
    {
        // تلاش برای ورود به سِمافور؛ اگر پروب قبلی هنوز تمام نشده، رد می‌شویم تا کار انباشته نشود
        if (!await _dispatcherProbeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _lastMeasuredDispatcherLagMs;
        }

        try
        {
            double measuredLagMs = await MeasureDispatcherRoundTripAsync().ConfigureAwait(false);
            _lastMeasuredDispatcherLagMs = measuredLagMs;
            _dispatcherLagReservoir.Add(measuredLagMs);
            return measuredLagMs;
        }
        finally
        {
            _dispatcherProbeLock.Release();
        }
    }

    private static async Task<double> MeasureDispatcherRoundTripAsync()
    {
        // بررسی وجود اپلیکیشن WPF و Dispatcher آن
        var app = System.Windows.Application.Current;
        var dispatcher = app?.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return 0.0;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // ارسال اکشن سبک به صف دیسپچر با اولویت نرمال
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                tcs.TrySetResult(true);
            }), System.Windows.Threading.DispatcherPriority.Normal);

            // انتظار با سقف تاخیر ۲ ثانیه
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(2000)).ConfigureAwait(false);
            if (completedTask == tcs.Task)
            {
                long endTimestamp = Stopwatch.GetTimestamp();
                double elapsedMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
                return Math.Round(elapsedMs, 2);
            }

            // زمان بیش از ۲ ثانیه طول کشید؛ وضعیت تاخیر شدید UI
            return 2000.0;
        }
        catch
        {
            return 0.0;
        }
    }

    #endregion

    /// <summary>
    /// پاکسازی مخازن نمونه‌برداری (مثلاً پس از پایان فاز Warmup)
    /// </summary>
    public void ResetMeasurementReservoirs()
    {
        _acquisitionReservoir.Reset();
        _preprocessingReservoir.Reset();
        _inferenceReservoir.Reset();
        _postprocessingReservoir.Reset();
        _endToEndReservoir.Reset();
        _queueWaitReservoir.Reset();
        _dispatcherLagReservoir.Reset();

        lock (_memoryLock)
        {
            _memoryHistory.Clear();
        }
    }

    #region Inner Reservoir Sampler (Algorithm R, K=4000)

    /// <summary>
    /// پیاده‌سازی نخ‌ایمن الگوریتم R برای نمونه‌برداری مخزنی با اندازه کراندار K
    /// تولید برآوردهای نااریب و پایدار از چندک‌های P50 و P95 بدون رشد نامحدود حافظه
    /// </summary>
    public class ReservoirSampler
    {
        private readonly int _capacity;
        private readonly double[] _samples;
        private readonly object _lock = new();
        private long _totalCount = 0;
        private readonly Random _random = new();

        public ReservoirSampler(int capacity)
        {
            _capacity = Math.Max(100, capacity);
            _samples = new double[_capacity];
        }

        public void Add(double value)
        {
            lock (_lock)
            {
                _totalCount++;
                if (_totalCount <= _capacity)
                {
                    _samples[_totalCount - 1] = value;
                }
                else
                {
                    // تولید عدد تصادفی بین 0 و _totalCount - 1
                    long j = (long)(_random.NextDouble() * _totalCount);
                    if (j < _capacity)
                    {
                        _samples[j] = value;
                    }
                }
            }
        }

        public (double P50, double P95, long TotalCount) GetPercentiles()
        {
            lock (_lock)
            {
                int currentSampleCount = (int)Math.Min(_totalCount, _capacity);
                if (currentSampleCount == 0)
                {
                    return (0.0, 0.0, 0);
                }

                double[] snapshot = new double[currentSampleCount];
                Array.Copy(_samples, snapshot, currentSampleCount);
                Array.Sort(snapshot);

                double p50 = ComputeQuantile(snapshot, 0.50);
                double p95 = ComputeQuantile(snapshot, 0.95);

                return (Math.Round(p50, 2), Math.Round(p95, 2), _totalCount);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _totalCount = 0;
            }
        }

        private static double ComputeQuantile(double[] sortedArray, double quantile)
        {
            if (sortedArray.Length == 1)
            {
                return sortedArray[0];
            }

            double rank = quantile * (sortedArray.Length - 1);
            int lowIndex = (int)Math.Floor(rank);
            int highIndex = (int)Math.Ceiling(rank);

            if (lowIndex == highIndex)
            {
                return sortedArray[lowIndex];
            }

            double fraction = rank - lowIndex;
            return sortedArray[lowIndex] + fraction * (sortedArray[highIndex] - sortedArray[lowIndex]);
        }
    }

    #endregion
}
