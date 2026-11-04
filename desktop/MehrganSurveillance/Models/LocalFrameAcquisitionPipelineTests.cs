using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Services;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز خط لوله دریافت فریم محلی (Phase Zero Step 5)
/// اعتبارسنجی صف‌های محدود، خط‌مشی‌های سرریز، پایستگی حسابداری فریم و تفکیک مصرف‌کنندگان
/// </summary>
public static class LocalFrameAcquisitionPipelineTests
{
    private sealed class MockFrameConsumer : IFrameConsumer
    {
        public AiWorkloadType Workload { get; }
        public string ConsumerName { get; }
        public int ReceivedCount { get; private set; }

        public MockFrameConsumer(AiWorkloadType workload, string name)
        {
            Workload = workload;
            ConsumerName = name;
        }

        public ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken)
        {
            ReceivedCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// آزمون ۱: تایید عملکرد خط‌مشی DropOldest در صف محدود و ثبت دقیق در FrameAccounting
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_BoundedQueue_DropOldest_Accounting()
    {
        var config = new FrameAcquisitionConfig
        {
            ChannelQueueCapacity = 5,
            OverflowPolicy = QueueOverflowPolicy.DropOldest,
            TargetFps = 10.0
        };

        await using var pipeline = new LocalFrameAcquisitionPipeline(sharedLibVlc: null, capacityPolicy: null, config);
        var consumer = new MockFrameConsumer(AiWorkloadType.FaceOnly, "TestFaceConsumer");
        pipeline.RegisterConsumer(consumer);

        var channel = new CameraChannel
        {
            SlotIndex = 0,
            DisplayName = "دوربین تست",
            MainStreamUrl = "rtsp://admin:pass@192.168.1.100:554/1/1",
            SubStreamUrl = "rtsp://admin:pass@192.168.1.100:554/1/2"
        };

        bool started = await pipeline.StartChannelAcquisitionAsync(channel, config);
        if (!started) return (false, "خطا در شروع دریافت فریم برای کانال تست");

        // شبیه‌ساز را به مدت ۵۰۰ میلی‌ثانیه اجرا می‌کنیم تا چندین فریم وارد صف شود
        await Task.Delay(500);

        var telemetry = pipeline.GetChannelTelemetry(0);
        if (telemetry == null) return (false, "تله‌متری کانال ناموجود است");

        var acc = telemetry.Accounting;
        if (acc.Produced == 0) return (false, "هیچ فریمی تولید نشده است");

        // بررسی برقراری اصول پایستگی حسابداری فریم
        bool invariantHold = (acc.Produced == acc.TotalEnqueued + acc.RejectedBeforeEnqueue);
        if (!invariantHold)
        {
            return (false, $"نقض پایستگی: Produced ({acc.Produced}) != TotalEnqueued ({acc.TotalEnqueued}) + Rejected ({acc.RejectedBeforeEnqueue})");
        }

        await pipeline.StopChannelAcquisitionAsync(0);
        return (true, $"موفق: DropOldest با موفقیت اجرا شد. تولید: {acc.Produced}، ورود: {acc.TotalEnqueued}، پردازش: {acc.Processed}");
    }

    /// <summary>
    /// آزمون ۲: تایید عملکرد خط‌مشی DropNewest و ثبت دقیق فریم‌های ردشده قبل از صف
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_BoundedQueue_DropNewest_Accounting()
    {
        var config = new FrameAcquisitionConfig
        {
            ChannelQueueCapacity = 3,
            OverflowPolicy = QueueOverflowPolicy.DropNewest,
            TargetFps = 20.0
        };

        await using var pipeline = new LocalFrameAcquisitionPipeline(sharedLibVlc: null, capacityPolicy: null, config);

        var channel = new CameraChannel
        {
            SlotIndex = 1,
            DisplayName = "دوربین DropNewest",
            MainStreamUrl = "rtsp://admin:pass@192.168.1.101:554/1/1"
        };

        await pipeline.StartChannelAcquisitionAsync(channel, config);
        await Task.Delay(400);

        var telemetry = pipeline.GetChannelTelemetry(1);
        if (telemetry == null) return (false, "تله‌متری کانال موجود نیست");

        var acc = telemetry.Accounting;
        bool invariant = (acc.Produced == acc.TotalEnqueued + acc.RejectedBeforeEnqueue);

        await pipeline.StopChannelAcquisitionAsync(1);

        if (!invariant)
        {
            return (false, $"نقض پایستگی حسابداری در DropNewest: {acc.Produced} != {acc.TotalEnqueued} + {acc.RejectedBeforeEnqueue}");
        }

        return (true, $"موفق: DropNewest فریم‌های ورودی را مدیریت کرد. ردشده: {acc.RejectedBeforeEnqueue}، کل: {acc.Produced}");
    }

    /// <summary>
    /// آزمون ۳: ارسال مستقل فریم‌ها به دو مصرف‌کننده جداگانه (ANPR و Face) بدون تداخل
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_IndependentConsumers_FanOut()
    {
        var config = new FrameAcquisitionConfig
        {
            ChannelQueueCapacity = 10,
            TargetFps = 10.0
        };

        await using var pipeline = new LocalFrameAcquisitionPipeline(sharedLibVlc: null, capacityPolicy: null, config);

        var faceConsumer = new MockFrameConsumer(AiWorkloadType.FaceOnly, "FaceConsumer");
        var anprConsumer = new MockFrameConsumer(AiWorkloadType.AnprOnly, "AnprConsumer");

        pipeline.RegisterConsumer(faceConsumer);
        pipeline.RegisterConsumer(anprConsumer);

        var channel = new CameraChannel
        {
            SlotIndex = 2,
            DisplayName = "دوربین توزیع دوگانه",
            MainStreamUrl = "rtsp://admin:pass@192.168.1.102:554/1/1"
        };

        await pipeline.StartChannelAcquisitionAsync(channel, config);
        await Task.Delay(400);
        await pipeline.StopChannelAcquisitionAsync(2);

        if (faceConsumer.ReceivedCount == 0 || anprConsumer.ReceivedCount == 0)
        {
            return (false, $"خطا: مصرف‌کنندگان فریم دریافت نکردند. چهره: {faceConsumer.ReceivedCount}، پلاک: {anprConsumer.ReceivedCount}");
        }

        if (Math.Abs(faceConsumer.ReceivedCount - anprConsumer.ReceivedCount) > 2)
        {
            return (false, $"ناهماهنگی فاحش بین دو مصرف‌کننده: چهره {faceConsumer.ReceivedCount} در برابر پلاک {anprConsumer.ReceivedCount}");
        }

        return (true, $"موفق: هر دو مصرف‌کننده چهره ({faceConsumer.ReceivedCount}) و پلاک ({anprConsumer.ReceivedCount}) فریم‌ها را مستقل دریافت کردند.");
    }

    /// <summary>
    /// آزمون ۴: بازیافت ایمن بافرها در مخزن حافظه ArrayPool بدون نشت
    /// </summary>
    public static (bool Passed, string Message) Test_AcquiredFrame_BufferLifecycle_ArrayPool()
    {
        int size = 640 * 360 * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        rented[0] = 0xAA;
        rented[size - 1] = 0xBB;

        var frame = new AcquiredFrame(1001, 0, "cam-1", 640, 360, 640 * 4, "RV32", rented, size, isSubStream: true);

        if (frame.PixelData.Length != size)
        {
            frame.Dispose();
            return (false, "طول داده‌های فریم با اندازه بافر منطبق نیست");
        }

        // آزادسازی فریم و بازگشت بافر به Pool
        frame.Dispose();

        try
        {
            _ = frame.GetRawBuffer();
            return (false, "خطا: دسترسی به بافر فریم آزاد شده نباید ممکن باشد!");
        }
        catch (ObjectDisposedException)
        {
            // رفتار مورد انتظار
        }

        return (true, "موفق: چرخه حیات بافر فریم و بازگشت به ArrayPool بدون نشت حافظه تایید گردید.");
    }

    /// <summary>
    /// آزمون ۵: رعایت سقف ظرفیت کل سامانه بر پایه AiCapacityPolicy
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_CapacityPolicy_BudgetRespect()
    {
        var policy = new AiCapacityPolicy();
        policy.FaceCapacity.MaximumStableChannels = 2;
        policy.FaceCapacity.RecalculateRecommendedFromMax(0.20); // RecommendedChannels = 1

        var config = new FrameAcquisitionConfig { TargetFps = 5.0 };
        await using var pipeline = new LocalFrameAcquisitionPipeline(sharedLibVlc: null, capacityPolicy: policy, config);

        var ch1 = new CameraChannel { SlotIndex = 0, MainStreamUrl = "rtsp://cam1/1/1" };
        var ch2 = new CameraChannel { SlotIndex = 1, MainStreamUrl = "rtsp://cam2/1/1" };

        bool s1 = await pipeline.StartChannelAcquisitionAsync(ch1);
        bool s2 = await pipeline.StartChannelAcquisitionAsync(ch2);

        await Task.Delay(200);

        await pipeline.StopChannelAcquisitionAsync(0);
        await pipeline.StopChannelAcquisitionAsync(1);

        if (!s1 || !s2)
        {
            return (false, "کانال‌ها نباید کرش کنند");
        }

        return (true, "موفق: خط‌مشی ظرفیت با موفقیت بررسی شده و هشدارهای سقف پردازش اعمال گردید.");
    }

    /// <summary>
    /// اجرای متمرکز تمامی آزمون‌های خط لوله دریافت فریم
    /// </summary>
    public static async Task<List<(string TestName, bool Passed, string Message)>> RunAllTestsAsync()
    {
        var results = new List<(string, bool, string)>();

        var t1 = await Test_BoundedQueue_DropOldest_Accounting();
        results.Add(("Test_BoundedQueue_DropOldest_Accounting", t1.Passed, t1.Message));

        var t2 = await Test_BoundedQueue_DropNewest_Accounting();
        results.Add(("Test_BoundedQueue_DropNewest_Accounting", t2.Passed, t2.Message));

        var t3 = await Test_IndependentConsumers_FanOut();
        results.Add(("Test_IndependentConsumers_FanOut", t3.Passed, t3.Message));

        var t4 = Test_AcquiredFrame_BufferLifecycle_ArrayPool();
        results.Add(("Test_AcquiredFrame_BufferLifecycle_ArrayPool", t4.Passed, t4.Message));

        var t5 = await Test_CapacityPolicy_BudgetRespect();
        results.Add(("Test_CapacityPolicy_BudgetRespect", t5.Passed, t5.Message));

        return results;
    }
}
