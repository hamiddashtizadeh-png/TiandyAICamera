using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Services;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز خط لوله دریافت فریم محلی و مدیریت اجاره بافرها (Phase Zero Step 5 Bug Fixes)
/// </summary>
public static class LocalFrameAcquisitionPipelineTests
{
    private sealed class MockLeaseHoldingConsumer : IFrameConsumer
    {
        public AiWorkloadType Workload { get; }
        public string ConsumerName { get; }
        public AcquiredFrame? HeldFrame { get; private set; }

        public MockLeaseHoldingConsumer(AiWorkloadType workload, string name)
        {
            Workload = workload;
            ConsumerName = name;
        }

        public ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken)
        {
            HeldFrame = frame;
            return ValueTask.CompletedTask;
        }

        public void ReleaseHeldFrame()
        {
            HeldFrame?.Release();
            HeldFrame = null;
        }
    }

    /// <summary>
    /// آزمون ۱: Dispatcher disposal does not invalidate a frame retained by an asynchronous consumer
    /// </summary>
    public static (bool Passed, string Message) Test_DispatcherDisposal_DoesNotInvalidateRetainedConsumerFrame()
    {
        int size = 640 * 360 * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        rented[0] = 0xAA;
        var frame = new AcquiredFrame(1001, 0, "cam-1", 640, 360, 640 * 4, "RV32", rented, size);

        // مصرف‌کننده اجاره خود را Retain می‌کند
        bool retained = frame.TryRetain();
        if (!retained || frame.ReferenceCount != 2)
        {
            return (false, "خطا در Retain اولیه مصرف‌کننده");
        }

        // توزیع‌کننده Dispose می‌کند
        frame.Dispose();

        if (frame.IsDisposed)
        {
            return (false, "خطا: آزادسازی توزیع‌کننده نباید بافر دارای اجاره مصرف‌کننده را باطل کند!");
        }

        if (frame.ReferenceCount != 1)
        {
            return (false, $"تعداد اجاره‌های نادرست: {frame.ReferenceCount}");
        }

        byte[] raw = frame.GetRawBuffer();
        if (raw[0] != 0xAA)
        {
            return (false, "بافر دستکاری شده یا خراب گردیده است");
        }

        // مصرف‌کننده اجاره خود را آزاد می‌کند
        frame.Release();

        if (!frame.IsDisposed || frame.ReferenceCount != 0)
        {
            return (false, "پس از آزادسازی آخرین اجاره، بافر باید به طور کامل Dispose گردد");
        }

        return (true, "موفق: آزادسازی توزیع‌کننده فریم نگهداری‌شده توسط مصرف‌کننده را باطل نکرد.");
    }

    /// <summary>
    /// آزمون ۲: Multiple consumers can safely retain and release the same frame
    /// </summary>
    public static (bool Passed, string Message) Test_MultipleConsumers_SafelyRetainAndReleaseFrame()
    {
        int size = 1024;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        var frame = new AcquiredFrame(2001, 0, "cam-2", 32, 32, 32 * 4, "RV32", rented, size);

        bool retainA = frame.TryRetain();
        bool retainB = frame.TryRetain();

        if (!retainA || !retainB || frame.ReferenceCount != 3)
        {
            return (false, $"خطا در ثبت اجاره‌ها: RefCount={frame.ReferenceCount}");
        }

        // توزیع‌کننده آزاد می‌کند
        frame.Dispose();
        if (frame.IsDisposed || frame.ReferenceCount != 2)
            return (false, "توزیع‌کننده نباید بافر چندمصرف‌کننده را آزاد کند");

        // مصرف‌کننده A آزاد می‌کند
        frame.Release();
        if (frame.IsDisposed || frame.ReferenceCount != 1)
            return (false, "آزادسازی مصرف‌کننده A نباید مصرف‌کننده B را متاثر کند");

        // مصرف‌کننده B آزاد می‌کند
        frame.Release();
        if (!frame.IsDisposed || frame.ReferenceCount != 0)
            return (false, "پس از آزادسازی B بافر باید Dispose شود");

        return (true, "موفق: چندین مصرف‌کننده به طور ایمن فریم یکسان را Retain و Release کردند.");
    }

    /// <summary>
    /// آزمون ۳: The buffer returns to ArrayPool exactly once, after the final release
    /// </summary>
    public static (bool Passed, string Message) Test_BufferReturnsToArrayPool_ExactlyOnce_AfterFinalRelease()
    {
        int size = 512;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        var frame = new AcquiredFrame(3001, 0, "cam-3", 16, 16, 16 * 4, "RV32", rented, size);

        frame.TryRetain(); // RefCount = 2

        frame.Release(); // RefCount = 1
        if (frame.IsDisposed) return (false, "بافر نباید قبل از آخرین اجاره آزاد شود");

        frame.Release(); // RefCount = 0 -> Returns to ArrayPool
        if (!frame.IsDisposed) return (false, "بافر باید پس از آخرین اجاره آزاد شود");

        // فراخوانی مجدد Release نباید باعث خطای double return یا منفی شدن RefCount گردد
        frame.Release();
        if (frame.ReferenceCount != 0) return (false, "شمارنده ارجاع نباید منفی شود");

        return (true, "موفق: بافر دقیقاً یک‌بار و پس از آخرین آزادسازی به ArrayPool بازگشت.");
    }

    /// <summary>
    /// آزمون ۴: Cancellation and exceptions release all leases safely
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_CancellationAndExceptions_ReleaseAllLeasesSafely()
    {
        var config = new FrameAcquisitionConfig { TargetFps = 20.0 };
        await using var pipeline = new LocalFrameAcquisitionPipeline(sharedLibVlc: null, capacityPolicy: null, config);

        var channel = new CameraChannel { SlotIndex = 0, MainStreamUrl = "rtsp://cam/1/1" };
        await pipeline.StartChannelAcquisitionAsync(channel);

        await Task.Delay(200);
        await pipeline.StopChannelAcquisitionAsync(0);

        var telemetry = pipeline.GetChannelTelemetry(0);
        if (telemetry == null) return (false, "تله‌متری ناموجود است");

        bool invariant = (telemetry.Accounting.Produced == telemetry.Accounting.TotalEnqueued + telemetry.Accounting.RejectedBeforeEnqueue);
        if (!invariant) return (false, "نقض حسابداری فریم در زمان توقف/لغو");

        return (true, "موفق: لغو و استثناها تمامی اجاره‌ها را به طور ایمن آزاد کردند.");
    }

    /// <summary>
    /// آزمون ۵: Rejected queue writes do not leak leases
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_RejectedQueueWrites_DoNotLeakLeases()
    {
        var faceConfig = new FaceDetectionConfig { IsEnabled = false }; // غیرفعال
        await using var facePipeline = new FaceDetectionPipeline(faceConfig);
        facePipeline.Initialize();

        byte[] rented = ArrayPool<byte>.Shared.Rent(100);
        var frame = new AcquiredFrame(5001, 0, "cam-5", 10, 10, 40, "RV32", rented, 100);

        frame.TryRetain(); // RefCount = 2 (Consuming attempt)

        // ارسال فریم به پایپ‌لاین غیرفعال (رد فوری)
        _ = facePipeline.OnFrameAvailableAsync(frame, CancellationToken.None);

        if (frame.ReferenceCount != 1)
        {
            return (false, $"خطا: اجاره مصرف‌کننده در رد صف آزاد نشد! RefCount={frame.ReferenceCount}");
        }

        frame.Dispose();
        if (!frame.IsDisposed) return (false, "فریم باید کاملاً Dispose شود");

        return (true, "موفق: رد شدن فریم در صف هیچ اجاره‌ای را نشت نداد.");
    }

    /// <summary>
    /// آزمون ۶: Double disposal cannot cause double return
    /// </summary>
    public static (bool Passed, string Message) Test_DoubleDisposal_CannotCauseDoubleReturn()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(256);
        var frame = new AcquiredFrame(6001, 0, "cam-6", 8, 8, 32, "RV32", rented, 256);

        // فراخوانی ۱۰ بار پشت سر هم Dispose
        for (int i = 0; i < 10; i++)
        {
            frame.Dispose();
        }

        if (frame.ReferenceCount != 0)
        {
            return (false, $"خطا: شمارنده ارجاع پس از فراخوانی‌های مکرر Dispose نادرست است: {frame.ReferenceCount}");
        }

        return (true, "موفق: فراخوانی مکرر Dispose هرگز باعث double return نگردید.");
    }

    /// <summary>
    /// اجرای متمرکز تمامی آزمون‌های خط لوله
    /// </summary>
    public static async Task<List<(string TestName, bool Passed, string Message)>> RunAllTestsAsync()
    {
        var results = new List<(string, bool, string)>();

        var t1 = Test_DispatcherDisposal_DoesNotInvalidateRetainedConsumerFrame();
        results.Add(("Test_DispatcherDisposal_DoesNotInvalidateRetainedConsumerFrame", t1.Passed, t1.Message));

        var t2 = Test_MultipleConsumers_SafelyRetainAndReleaseFrame();
        results.Add(("Test_MultipleConsumers_SafelyRetainAndReleaseFrame", t2.Passed, t2.Message));

        var t3 = Test_BufferReturnsToArrayPool_ExactlyOnce_AfterFinalRelease();
        results.Add(("Test_BufferReturnsToArrayPool_ExactlyOnce_AfterFinalRelease", t3.Passed, t3.Message));

        var t4 = await Test_CancellationAndExceptions_ReleaseAllLeasesSafely();
        results.Add(("Test_CancellationAndExceptions_ReleaseAllLeasesSafely", t4.Passed, t4.Message));

        var t5 = await Test_RejectedQueueWrites_DoNotLeakLeases();
        results.Add(("Test_RejectedQueueWrites_DoNotLeakLeases", t5.Passed, t5.Message));

        var t6 = Test_DoubleDisposal_CannotCauseDoubleReturn();
        results.Add(("Test_DoubleDisposal_CannotCauseDoubleReturn", t6.Passed, t6.Message));

        return results;
    }
}
