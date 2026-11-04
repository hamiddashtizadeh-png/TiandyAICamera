using System;

namespace MehrganSurveillance.Models;

/// <summary>
/// بسته اطلاعاتی گزارش لحظه‌ای پیشرفت اجرای بنچمارک جهت نمایش در واسط کاربری و پایشگرها
/// </summary>
public class BenchmarkExecutionProgress
{
    public BenchmarkExecutionPhase Phase { get; init; } = BenchmarkExecutionPhase.NotStarted;
    public TimeSpan Elapsed { get; init; } = TimeSpan.Zero;
    public TimeSpan EstimatedRemaining { get; init; } = TimeSpan.Zero;
    public double ProgressPercentage { get; init; } = 0.0;

    // Throughput
    public double InstantaneousFps { get; init; }
    public double AverageProcessingFps { get; init; }

    // Statistical Latency Estimates (Reservoir Sampling P50/P95)
    public double CurrentInferenceLatencyP50Ms { get; init; }
    public double CurrentInferenceLatencyP95Ms { get; init; }
    public double CurrentEndToEndLatencyP50Ms { get; init; }
    public double CurrentEndToEndLatencyP95Ms { get; init; }
    public double CurrentQueueWaitP95Ms { get; init; }

    // Frame Accounting Snapshot
    public FrameAccountingMetrics FrameAccounting { get; init; } = new();

    // Memory Stability Metrics Snapshot
    public MemoryStabilityMetrics MemoryMetrics { get; init; } = new();

    // UI Dispatcher Lag (Safety for Live View)
    public double CurrentUiDispatcherLagMs { get; init; }

    // Concurrency Analysis (Combined Mode)
    public ExecutionConcurrencyMode ConcurrencyMode { get; init; } = ExecutionConcurrencyMode.Unknown;
    public double ConcurrencyOverlapRatio { get; init; } = 0.0;

    public string StatusMessage { get; init; } = string.Empty;
}
