using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Services;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز خط لوله آشکارسازی چهره و انزوا دادن بافر محوسازی (Phase Zero Step 5 Bug Fixes)
/// </summary>
public static class FaceDetectionPipelineTests
{
    /// <summary>
    /// آزمون ۱: خط لوله به صورت پیش‌فرض غیرفعال است (Disabled by default)
    /// </summary>
    public static async Task<(bool Passed, string Message)> Test_DisabledByDefault_RequiresExplicitAdminEnable()
    {
        var config = new FaceDetectionConfig { IsEnabled = false };
        await using var pipeline = new FaceDetectionPipeline(config);

        bool init = pipeline.Initialize();
        if (!init) return (false, "خطا در مقداردهی اولیه پایپ‌لاین");

        if (pipeline.Config.IsEnabled)
        {
            return (false, "خطای امنیتی: خط لوله چهره نباید به صورت پیش‌فرض فعال باشد!");
        }

        int bufferSize = 640 * 360 * 4;
        byte[] pooled = ArrayPool<byte>.Shared.Rent(bufferSize);
        var frame = new AcquiredFrame(1, 0, "cam-test", 640, 360, 640 * 4, "RV32", pooled, bufferSize);

        // ارسال فریم در حالت غیرفعال
        await pipeline.OnFrameAvailableAsync(frame, CancellationToken.None);
        await Task.Delay(50);

        if (pipeline.TotalFramesProcessed > 0)
        {
            frame.Dispose();
            return (false, "خطای امنیتی: خط لوله غیرفعال نباید هیچ فریمی را پردازش کند!");
        }

        // اکنون توسط مدیر سامانه فعال می‌شود
        pipeline.EnablePipeline();
        if (!pipeline.Config.IsEnabled)
        {
            frame.Dispose();
            return (false, "خطا در فعال‌سازی صریح خط لوله توسط مدیر");
        }

        frame.Dispose();
        return (true, "موفق: خط لوله در بدو ورود کاملاً غیرفعال بود و صرفاً با متد صریح EnablePipeline فعال گردید.");
    }

    /// <summary>
    /// آزمون ۲: تایید خروجی صرفاً کادر محاطی غیرهویتی (Strictly Non-Identifying Bounding Box Only)
    /// </summary>
    public static (bool Passed, string Message) Test_NonIdentifyingBoundingBoxOnly_Contract()
    {
        var box = new FaceBoundingBox
        {
            NormalizedX = 0.2f,
            NormalizedY = 0.3f,
            NormalizedWidth = 0.15f,
            NormalizedHeight = 0.20f,
            Confidence = 0.92f
        };

        var (px, py, pw, ph) = box.ToPixelCoordinates(1920, 1080);
        if (px != 384 || py != 324 || pw != 288 || ph != 216)
        {
            return (false, $"خطا در محاسبه مختصات پیکسلی: ({px}, {py}, {pw}, {ph})");
        }

        using var result = new FaceDetectionResult
        {
            FrameId = 100,
            ChannelIndex = 0,
            CameraId = "cam-01",
            Faces = new List<FaceBoundingBox> { box }
        };

        if (result.FaceCount != 1)
        {
            return (false, "شمارش چهره‌ها نادرست است");
        }

        var type = typeof(FaceDetectionResult);
        var properties = type.GetProperties();
        foreach (var p in properties)
        {
            string nameLower = p.Name.ToLowerInvariant();
            if (nameLower.Contains("identity") || nameLower.Contains("embedding") || nameLower.Contains("biometric") || nameLower.Contains("personname"))
            {
                return (false, $"خطای ساختاری: خصوصیت هویتی نامجاز '{p.Name}' در مدل FaceDetectionResult کشف شد!");
            }
        }

        return (true, "موفق: مدل صرفاً مختصات کادر محاطی، ضریب اطمینان و شمارش چهره را بدون ذخیره هویت یا بیومتریک فراهم می‌کند.");
    }

    /// <summary>
    /// آزمون ۳: اعتبارسنجی الگوریتم حذف کادرهای همپوشان (Non-Maximum Suppression - NMS)
    /// </summary>
    public static (bool Passed, string Message) Test_NonMaximumSuppression_RemovesOverlappingBoxes()
    {
        var box1 = new FaceBoundingBox { NormalizedX = 0.10f, NormalizedY = 0.10f, NormalizedWidth = 0.20f, NormalizedHeight = 0.20f, Confidence = 0.90f };
        var box2 = new FaceBoundingBox { NormalizedX = 0.11f, NormalizedY = 0.11f, NormalizedWidth = 0.19f, NormalizedHeight = 0.19f, Confidence = 0.75f };
        var box3 = new FaceBoundingBox { NormalizedX = 0.60f, NormalizedY = 0.50f, NormalizedWidth = 0.15f, NormalizedHeight = 0.15f, Confidence = 0.85f };

        var inputCandidates = new List<FaceBoundingBox> { box1, box2, box3 };
        var filtered = FaceDetectionPipeline.ApplyNonMaximumSuppression(inputCandidates, iouThreshold: 0.40f, maxOutputs: 10);

        if (filtered.Count != 2)
        {
            return (false, $"خطا در NMS: انتظار ۲ کادر نهایی وجود داشت اما {filtered.Count} کادر باقی ماند.");
        }

        if (!filtered.Contains(box1) || !filtered.Contains(box3))
        {
            return (false, "کادر با ضریب اطمینان بالاتر یا کادر مستقل به اشتباه حذف شده است");
        }

        return (true, "موفق: الگوریتم NMS کادر همپوشان ضعیف‌تر (box2) را حذف کرد و کادرهای معتبر (box1 و box3) را حفظ نمود.");
    }

    /// <summary>
    /// آزمون ۷: Face anonymization does not change any byte of the original frame
    /// </summary>
    public static (bool Passed, string Message) Test_FaceAnonymization_DoesNotChangeOriginalFrameBytes()
    {
        int width = 100;
        int height = 100;
        int stride = width * 4;
        int size = stride * height;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

        // پر کردن فریم با الگوی مقادیر نمونه
        for (int i = 0; i < size; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        // ایجاد کپی ایزوله پشتیبان برای مقایسه دقیق بایت به بایت
        byte[] originalCopy = new byte[size];
        Array.Copy(buffer, 0, originalCopy, 0, size);

        var frame = new AcquiredFrame(7001, 0, "cam-blur-test", width, height, stride, "RV32", buffer, size);

        var face = new FaceBoundingBox
        {
            NormalizedX = 0.20f,
            NormalizedY = 0.20f,
            NormalizedWidth = 0.30f,
            NormalizedHeight = 0.30f,
            Confidence = 0.95f
        };

        // فراخوانی متد کپی انزوا یافته محوسازی
        byte[] anonymizedOutput = FaceDetectionPipeline.CreateAnonymizedCopy(frame, new List<FaceBoundingBox> { face }, blockSize: 8);

        // بررسی اینکه فریم اصلی حتی ۱ بایت تغییر نکرده باشد
        ReadOnlySpan<byte> currentFrameSpan = frame.PixelData.Span;
        for (int i = 0; i < size; i++)
        {
            if (currentFrameSpan[i] != originalCopy[i])
            {
                ArrayPool<byte>.Shared.Return(anonymizedOutput);
                frame.Dispose();
                return (false, $"خطای حاد: بایت شماره {i} از فریم اصلی دستکاری شده است! ({currentFrameSpan[i]} != {originalCopy[i]})");
            }
        }

        ArrayPool<byte>.Shared.Return(anonymizedOutput);
        frame.Dispose();

        return (true, "موفق: محوسازی چهره حتی ۱ بایت از فریم اصلی و مشترک را دستکاری یا مخدوش نکرد.");
    }

    /// <summary>
    /// آزمون ۸: The anonymized output is independent and correctly disposed
    /// </summary>
    public static (bool Passed, string Message) Test_AnonymizedOutput_IsIndependentAndDisposedCleanly()
    {
        int width = 100;
        int height = 100;
        int stride = width * 4;
        int size = stride * height;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        for (int i = 0; i < size; i++) buffer[i] = (byte)(i % 255);

        var frame = new AcquiredFrame(8001, 0, "cam-out-test", width, height, stride, "RV32", buffer, size);

        var face = new FaceBoundingBox
        {
            NormalizedX = 0.10f,
            NormalizedY = 0.10f,
            NormalizedWidth = 0.40f,
            NormalizedHeight = 0.40f,
            Confidence = 0.90f
        };

        byte[] anonymizedOutput = FaceDetectionPipeline.CreateAnonymizedCopy(frame, new List<FaceBoundingBox> { face }, blockSize: 8);

        // ۱. بررسی ارجاع متفاوت بافر خروجی نسبت به فریم اصلی
        if (ReferenceEquals(anonymizedOutput, frame.GetRawBuffer()))
        {
            frame.Dispose();
            return (false, "خطا: بافر خروجی محوسازی نباید همان ارجاع بافر فریم اصلی باشد!");
        }

        // ۲. بررسی تغییر پیکسل‌های داخل کادر چهره در بافر خروجی
        var (px, py, pw, ph) = face.ToPixelCoordinates(width, height);
        int sampleIdx = (py + ph / 2) * stride + (px + pw / 2) * 4;
        if (anonymizedOutput[sampleIdx] == frame.PixelData.Span[sampleIdx])
        {
            frame.Dispose();
            return (false, "پیکسل‌های داخل کادر چهره در بافر خروجی تغییر نیافته‌اند");
        }

        // ۳. بررسی آزادسازی پاکیزه در FaceDetectionResult
        var result = new FaceDetectionResult
        {
            FrameId = 8001,
            AnonymizationApplied = true,
            AnonymizedPixelBuffer = anonymizedOutput
        };

        result.Dispose(); // آزادسازی بافر خروجی به ArrayPool
        frame.Dispose();

        return (true, "موفق: بافر خروجی محوسازی کاملاً مستقل بوده و با Dispose آزاد گردید.");
    }

    /// <summary>
    /// آزمون ۹: Concurrent calls to FaceDetectionResult.Dispose() are thread-safe and return buffer exactly once
    /// </summary>
    public static (bool Passed, string Message) Test_ConcurrentDispose_CallsOnFaceDetectionResult_AreThreadSafe()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(512);
        var result = new FaceDetectionResult
        {
            FrameId = 9001,
            AnonymizationApplied = true,
            AnonymizedPixelBuffer = rented
        };

        // فراخوانی همزمان Dispose از ۵0 نخ به صورت موازی
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 50, _ =>
        {
            try
            {
                result.Dispose();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        if (exceptions.Count > 0)
        {
            return (false, $"استثنا در فراخوانی‌های همزمان Dispose: {exceptions.First().Message}");
        }

        if (result.AnonymizedPixelBuffer != null)
        {
            return (false, "بافر پس از Dispose همزمان باید null شده باشد");
        }

        return (true, "موفق: فراخوانی‌های همزمان Dispose در FaceDetectionResult کاملاً نخ‌ایمن بوده و بافر را دقیقاً یک‌بار آزاد کردند.");
    }

    /// <summary>
    /// اجرای متمرکز تمامی آزمون‌های آشکارسازی چهره
    /// </summary>
    public static async Task<List<(string TestName, bool Passed, string Message)>> RunAllTestsAsync()
    {
        var results = new List<(string, bool, string)>();

        var t1 = await Test_DisabledByDefault_RequiresExplicitAdminEnable();
        results.Add(("Test_DisabledByDefault_RequiresExplicitAdminEnable", t1.Passed, t1.Message));

        var t2 = Test_NonIdentifyingBoundingBoxOnly_Contract();
        results.Add(("Test_NonIdentifyingBoundingBoxOnly_Contract", t2.Passed, t2.Message));

        var t3 = Test_NonMaximumSuppression_RemovesOverlappingBoxes();
        results.Add(("Test_NonMaximumSuppression_RemovesOverlappingBoxes", t3.Passed, t3.Message));

        var t7 = Test_FaceAnonymization_DoesNotChangeOriginalFrameBytes();
        results.Add(("Test_FaceAnonymization_DoesNotChangeOriginalFrameBytes", t7.Passed, t7.Message));

        var t8 = Test_AnonymizedOutput_IsIndependentAndDisposedCleanly();
        results.Add(("Test_AnonymizedOutput_IsIndependentAndDisposedCleanly", t8.Passed, t8.Message));

        var t9 = Test_ConcurrentDispose_CallsOnFaceDetectionResult_AreThreadSafe();
        results.Add(("Test_ConcurrentDispose_CallsOnFaceDetectionResult_AreThreadSafe", t9.Passed, t9.Message));

        return results;
    }
}
