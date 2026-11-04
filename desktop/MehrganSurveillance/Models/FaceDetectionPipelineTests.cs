using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Services;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز خط لوله آشکارسازی چهره (Face Detection Pipeline Tests)
/// اعتبارسنجی انطباق با نیازمندی‌های امنیتی: غیرفعال به صورت پیش‌فرض، صرفاً کادر محاطی غیرهویتی،
/// الگوریتم NMS، محوسازی چهره، و تفکیک ظرفیت چهره از پلاک‌خوان
/// </summary>
public static class FaceDetectionPipelineTests
{
    /// <summary>
    /// آزمون ۱: خط لوله به صورت پیش‌فرض غیرفعال است (Disabled by default) و تا زمان تایید صریح مدیر فریم‌ها را پردازش نمی‌کند
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
    /// عدم وجود هرگونه بردار ویژگی (Embedding)، نمایه بیومتریک یا داده هویتی
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

        var result = new FaceDetectionResult
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

        // بررسی اینکه مدل FaceDetectionResult هیچ فیلد یا ارجاعی به شناسه هویتی، امبدینگ یا دیتابیس ندارد
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
        // دو کادر بسیار نزدیک و همپوشان برای یک چهره واحد (IoU بالا)
        var box1 = new FaceBoundingBox { NormalizedX = 0.10f, NormalizedY = 0.10f, NormalizedWidth = 0.20f, NormalizedHeight = 0.20f, Confidence = 0.90f };
        var box2 = new FaceBoundingBox { NormalizedX = 0.11f, NormalizedY = 0.11f, NormalizedWidth = 0.19f, NormalizedHeight = 0.19f, Confidence = 0.75f };

        // یک کادر چهره کاملاً مجزا در سمت دیگر تصویر (بدون همپوشانی)
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
    /// آزمون ۴: اعتبارسنجی محوسازی خودکار کادر چهره (Anonymization Blur / Pixelation)
    /// </summary>
    public static (bool Passed, string Message) Test_FaceAnonymizationBlur_MutatesPixels()
    {
        int width = 100;
        int height = 100;
        int stride = width * 4;
        int size = stride * height;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

        // پر کردن فریم با الگوی گرادیانی قابل پیش‌بینی
        for (int i = 0; i < size; i++)
        {
            buffer[i] = (byte)(i % 256);
        }

        var frame = new AcquiredFrame(1, 0, "cam-blur", width, height, stride, "RV32", buffer, size);

        // تعریف کادر چهره در مرکز تصویر (از پیکسل ۲۰ تا ۵۰)
        var face = new FaceBoundingBox
        {
            NormalizedX = 0.20f,
            NormalizedY = 0.20f,
            NormalizedWidth = 0.30f,
            NormalizedHeight = 0.30f,
            Confidence = 0.95f
        };

        // ذخیره مقادیر پیکسل‌های داخل کادر قبل از محوسازی
        var (px, py, pw, ph) = face.ToPixelCoordinates(width, height);
        byte[] beforeCenterSample = new byte[pw * 4];
        Array.Copy(buffer, (py + ph / 2) * stride + px * 4, beforeCenterSample, 0, beforeCenterSample.Length);

        // اعمال محوسازی پیکسلی
        FaceDetectionPipeline.ApplyFaceAnonymizationBlur(frame, new List<FaceBoundingBox> { face }, blockSize: 8);

        // مقایسه پیکسل‌های پس از محوسازی
        byte[] afterCenterSample = new byte[pw * 4];
        Array.Copy(buffer, (py + ph / 2) * stride + px * 4, afterCenterSample, 0, afterCenterSample.Length);

        bool pixelsChanged = false;
        for (int i = 0; i < beforeCenterSample.Length; i++)
        {
            if (beforeCenterSample[i] != afterCenterSample[i])
            {
                pixelsChanged = true;
                break;
            }
        }

        frame.Dispose();

        if (!pixelsChanged)
        {
            return (false, "خطا: پیکسل‌های داخل کادر چهره محوسازی یا تغییر نیافته‌اند!");
        }

        return (true, "موفق: الگوریتم محوسازی (Pixelation Anonymization) پیکسل‌های محدوده چهره را جهت حفظ حریم خصوصی تغییر داد.");
    }

    /// <summary>
    /// آزمون ۵: تفکیک مستقل ظرفیت چهره از ظرفیت پلاک‌خوان و بار ترکیبی در AiCapacityPolicy
    /// </summary>
    public static (bool Passed, string Message) Test_FaceCapacity_IndependentFromAnpr()
    {
        var policy = new AiCapacityPolicy();
        policy.SetInitialHardwareEstimates(isGpuAvailable: true, logicalCores: 8, totalRamGb: 16.0);

        int initialFaceMax = policy.FaceCapacity.MaximumStableChannels;
        int initialAnprMax = policy.AnprCapacity.MaximumStableChannels;
        int initialCombinedMax = policy.CombinedCapacity.MaximumStableChannels;

        // کالیبراسیون آزمایشی صرفاً برای بار چهره
        policy.FaceCapacity.MaximumStableChannels = 6;
        policy.FaceCapacity.State = CapacityCalibrationState.Calibrated;

        // ظرفیت پلاک‌خوان و ترکیبی نباید به صورت ساده دستخوش تغییر شوند
        if (policy.AnprCapacity.MaximumStableChannels != initialAnprMax)
        {
            return (false, "تغییر در ظرفیت چهره باعث نشت ناخواسته به ظرفیت پلاک‌خوان گردید!");
        }

        if (policy.CombinedCapacity.MaximumStableChannels == policy.FaceCapacity.MaximumStableChannels + policy.AnprCapacity.MaximumStableChannels)
        {
            return (false, "خطای معماری: ظرفیت ترکیبی هرگز نباید حاصل‌جمع جبری دو بار مستقل باشد!");
        }

        return (true, $"موفق: استقلال ظرفیت چهره ({policy.FaceCapacity.MaximumStableChannels}) از پلاک ({policy.AnprCapacity.MaximumStableChannels}) و ترکیبی ({policy.CombinedCapacity.MaximumStableChannels}) تضمین شده است.");
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

        var t4 = Test_FaceAnonymizationBlur_MutatesPixels();
        results.Add(("Test_FaceAnonymizationBlur_MutatesPixels", t4.Passed, t4.Message));

        var t5 = Test_FaceCapacity_IndependentFromAnpr();
        results.Add(("Test_FaceCapacity_IndependentFromAnpr", t5.Passed, t5.Message));

        return results;
    }
}
