using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// وضعیت چرخه حیات کالیبراسیون ظرفیت پردازش هوش مصنوعی
/// </summary>
public enum CapacityCalibrationState
{
    /// <summary>
    /// برآورد اولیه بر پایه کاوش سخت‌افزاری و روابط تقریبی پیش از اجرای هرگونه بنچمارک
    /// </summary>
    Estimated,

    /// <summary>
    /// سامانه در حال اجرای آزمون‌های بنچمارک و سنجش بار پردازشی است
    /// </summary>
    Calibrating,

    /// <summary>
    /// ظرفیت با شواهد رسمی بنچمارک معتبر (حداقل ۵ تا ۱۰ دقیقه) و منطبق با اثرانگشت جاری تایید شده است
    /// </summary>
    Calibrated,

    /// <summary>
    /// تغییرات در سخت‌افزار، درایور، مدل‌ها، یا پارامترهای استریم باعث ابطال نتایج قبلی شده و کالیبراسیون مجدد الزامی است
    /// </summary>
    RecalibrationRequired
}

/// <summary>
/// نوع بار کاری هوش مصنوعی تحت آزمون و نظارت
/// </summary>
public enum AiWorkloadType
{
    /// <summary>
    /// صرفاً پلاک‌خوان (تشخیص پلاک و سپس نویسه‌خوانی OCR)
    /// </summary>
    AnprOnly,

    /// <summary>
    /// صرفاً آشکارسازی چهره غیرهویتی (Bounding Box بدون تشخیص هویت یا انطباق پایگاه داده)
    /// </summary>
    FaceOnly,

    /// <summary>
    /// پردازش همزمان پلاک‌خوان و چهره روی کانال‌های فعال
    /// </summary>
    Combined
}

/// <summary>
/// سطح مدت‌زمان اجرای بنچمارک
/// آزمون‌های دود ۳۰ ثانیه‌ای هرگز مبنای تایید ظرفیت عملیاتی قرار نمی‌گیرند
/// </summary>
public enum BenchmarkDurationTier
{
    /// <summary>
    /// آزمون سریع ۳۰ ثانیه‌ای برای سنجش سلامت اجرا (عدم کرش و درستی خط لوله) - ناکافی برای کالیبراسیون قطعی
    /// </summary>
    SmokeTest30s,

    /// <summary>
    /// آزمون ۵ تا ۱۰ دقیقه‌ای برای کالیبراسیون ظرفیت اولیه
    /// </summary>
    Calibration5to10m,

    /// <summary>
    /// آزمون پایداری ۳۰ تا ۶۰ دقیقه‌ای برای اعتبارسنجی حداکثر ظرفیت پایدار و ثبات حافظه
    /// </summary>
    SustainedValidation30to60m,

    /// <summary>
    /// آزمون طولانی‌مدت ۱ تا ۲ ساعته برای ارزیابی پایداری عمیق حافظه و تایید نرخ نشت نهایی
    /// </summary>
    MemoryStability1to2h
}

/// <summary>
/// معیارهای اندازه‌گیری‌شده پایداری حافظه در طول بنچمارک
/// تفکیک صریح حافظه مدیریت‌شده دات‌نت، حافظه کاری/اختصاصی پروسه، و حافظه ویدئویی اختصاصی GPU
/// جایگزین الزام اثبات‌ناپذیر نشت حافظه صفر با شیب پایداری قابل اندازه‌گیری (Slope)
/// </summary>
public class MemoryStabilityMetrics
{
    public long ManagedHeapBytes { get; init; }
    public long ProcessPrivateBytes { get; init; }
    public long ProcessWorkingSetBytes { get; init; }
    public long? GpuDedicatedUsedBytes { get; init; }
    public long? GpuDedicatedPeakBytes { get; init; }

    public TimeSpan MeasuredDuration { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// شیب افزایش حافظه در ساعت (MB per hour)
    /// </summary>
    public double MemoryGrowthRateMbPerHour { get; init; }

    /// <summary>
    /// آیا شیب رشد حافظه در محدوده مجاز خط‌مشی پایداری (مثلاً کمتر از ۵۰ مگابایت در ساعت) است؟
    /// </summary>
    public bool IsMemorySlopeAcceptable { get; init; }

    public double ManagedHeapMb => Math.Round(ManagedHeapBytes / (1024.0 * 1024.0), 2);
    public double ProcessPrivateMb => Math.Round(ProcessPrivateBytes / (1024.0 * 1024.0), 2);
    public double ProcessWorkingSetMb => Math.Round(ProcessWorkingSetBytes / (1024.0 * 1024.0), 2);
    public double? GpuDedicatedUsedMb => GpuDedicatedUsedBytes.HasValue
        ? Math.Round(GpuDedicatedUsedBytes.Value / (1024.0 * 1024.0), 2)
        : null;
}

/// <summary>
/// سند شواهد تجربی حاصل از اجرای یک بنچمارک واقعی
/// شرط لازم برای تغییر وضعیت ظرفیت به Calibrated
/// </summary>
public class BenchmarkEvidence
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public DateTime ExecutedAtUtc { get; init; } = DateTime.UtcNow;
    public BenchmarkDurationTier DurationTier { get; init; }
    public TimeSpan ActualDuration { get; init; }
    public CalibrationStreamSourceType SourceType { get; init; }
    public BenchmarkInputProvenance InputProvenance { get; init; } = BenchmarkInputProvenance.SyntheticMemoryBuffer;
    public BenchmarkModelFidelity ModelFidelity { get; init; } = BenchmarkModelFidelity.SyntheticDiagnostic;
    public string FingerprintHash { get; init; } = string.Empty;
    public AiWorkloadType Workload { get; init; }
    public int TestedChannelCount { get; init; }

    // Latency & Throughput Metrics (Separated per specification)
    public double AcquisitionLatencyP50Ms { get; init; }
    public double AcquisitionLatencyP95Ms { get; init; }
    public double PreprocessingLatencyP50Ms { get; init; }
    public double PreprocessingLatencyP95Ms { get; init; }
    public double InferenceLatencyP50Ms { get; init; }
    public double InferenceLatencyP95Ms { get; init; }
    public double PostprocessingLatencyP50Ms { get; init; }
    public double PostprocessingLatencyP95Ms { get; init; }
    public double EndToEndLatencyP50Ms { get; init; }
    public double EndToEndLatencyP95Ms { get; init; }
    public double AchievedProcessingFps { get; init; }
    public double QueueWaitTimeP95Ms { get; init; }
    public double DroppedFrameRatio { get; init; } // e.g. 0.001 = 0.1%

    // UI Dispatcher Lag (Live View Health Protection)
    public double UiDispatcherLagP95Ms { get; init; }

    // Frame Accounting (7 Dispositions)
    public FrameAccountingMetrics FrameAccounting { get; init; } = new();
    public bool InvariantsVerified { get; init; }

    // Concurrency Analysis (Combined Mode)
    public ExecutionConcurrencyMode ConcurrencyMode { get; init; } = ExecutionConcurrencyMode.Unknown;
    public double ConcurrencyOverlapRatio { get; init; }

    // LibVLC Telemetry Status
    public LibVlcTelemetryStatus LibVlcStatus { get; init; } = LibVlcTelemetryStatus.UnknownNotInstrumented;

    // Memory Stability Metrics
    public MemoryStabilityMetrics MemoryMetrics { get; init; } = new();

    /// <summary>
    /// آیا آزمون کلیه معیارهای تشخیصی (تاخیر، شیب حافظه، پایداری صف، برقراری پایستگی حسابداری فریم) را با موفقیت پاس کرده است؟
    /// </summary>
    public bool PassedDiagnosticCriteria { get; init; }

    /// <summary>
    /// آیا آزمون کلیه معیارها اعم از تشخیصی و شرایط احراز عملیاتی را پاس کرده است؟
    /// </summary>
    public bool PassedAllCriteria { get; init; }

    public List<string> ValidationMessages { get; init; } = new();

    /// <summary>
    /// اعتبارسنجی اینکه آیا این بنچمارک صلاحیت ثبت به عنوان شواهد کالیبراسیون را دارد یا خیر
    /// آزمون‌های دود ۳۰ ثانیه‌ای و بارهای کاری سنتتیک هرگز مستقلاً وضعیت را به Calibrated تغییر نمی‌دهند
    /// </summary>
    public bool IsEligibleForCalibration(out string reason)
    {
        if (DurationTier == BenchmarkDurationTier.SmokeTest30s || ActualDuration < TimeSpan.FromMinutes(4.5))
        {
            reason = "مدت زمان اجرای بنچمارک کمتر از حد مجاز کالیبراسیون (حداقل ۵ دقیقه) است. آزمون دود فقط برای تایید عدم کرش و عیب‌یابی است.";
            return false;
        }

        // بررسی اصالت معماری مدل: مدل‌های سنتتیک تشخیصی نمی‌توانند مستقلاً ظرفیت را کالیبره کنند
        if (ModelFidelity != BenchmarkModelFidelity.FullProductionModel)
        {
            reason = $"سطح اصالت مدل ({ModelFidelity}) تشخیصی/سنتتیک است. این بنچمارک صرفاً شواهد تخمینی و سلامت خط لوله را تامین می‌کند و نمی‌تواند وضعیت را مستقلاً به Calibrated تغییر دهد.";
            return false;
        }

        // بررسی منشأ ورودی: بافر حافظه سنتتیک دیکود شبکه و فایل را احراز نمی‌کند
        if (InputProvenance == BenchmarkInputProvenance.SyntheticMemoryBuffer)
        {
            reason = "منشأ ورودی فریم‌ها بافر حافظه سنتتیک است و خط لوله دیکود واقعی RTSP یا ویدیو را احراز نمی‌کند.";
            return false;
        }

        // شرط الزامی و مستقل ۱: قبولی قطعی در کلیه معیارهای تشخیصی خط لوله (افت فریم، تاخیر، رشد حافظه و پایداری)
        if (!PassedDiagnosticCriteria)
        {
            reason = "این بنچمارک به دلیل نقض معیارهای تشخیصی و پایداری (مانند تاخیر، افت فریم یا رشد حافظه) قبول نشده است.";
            return false;
        }

        // شرط الزامی و مستقل ۲: قبولی قطعی در کلیه شرایط و معیارهای جامع عملیاتی جهت کالیبراسیون
        if (!PassedAllCriteria)
        {
            reason = "این بنچمارک کلیه معیارهای جامع احراز صلاحیت عملیاتی جهت کالیبراسیون را احراز نکرده است.";
            return false;
        }

        if (!InvariantsVerified)
        {
            reason = "اصول پایستگی حسابداری فریم در این بنچمارک نقض شده است.";
            return false;
        }

        if (!MemoryMetrics.IsMemorySlopeAcceptable)
        {
            reason = $"شیب رشد حافظه ({MemoryMetrics.MemoryGrowthRateMbPerHour:F1} MB/h) بیش از حد مجاز خط‌مشی پایداری است.";
            return false;
        }

        if (Workload == AiWorkloadType.Combined && ConcurrencyMode == ExecutionConcurrencyMode.Serialized)
        {
            reason = "در بار کاری ترکیبی، همپوشانی زمانی بین سشن‌های مستقل رخ نداده و اجرا کاملاً سریالی بوده است.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(FingerprintHash))
        {
            reason = "اثرانگشت کالیبراسیون معتبر به این بنچمارک الصاق نشده است.";
            return false;
        }

        reason = "شواهد بنچمارک معتبر و حائز شرایط کالیبراسیون کامل است.";
        return true;
    }
}

/// <summary>
/// برآورد یا رکورد ظرفیت برای یک نوع بار کاری مشخص (پلاک‌خوان، چهره، یا ترکیبی)
/// تفکیک صریح ظرفیت پیشنهادی امن (Recommended) از حداکثر ظرفیت پایدار (Maximum Stable)
/// </summary>
public class WorkloadCapacityEstimate
{
    public AiWorkloadType Workload { get; init; }
    public CapacityCalibrationState State { get; set; } = CapacityCalibrationState.Estimated;

    /// <summary>
    /// حداکثر تعداد دوربین‌هایی که در بنچمارک پایدار ماندند (بدون انفجار صف و بدون سرریز حافظه)
    /// </summary>
    public int MaximumStableChannels { get; set; }

    /// <summary>
    /// ظرفیت پیشنهادی بهره‌برداری با احتساب حاشیه امن خط‌مشی (Safety Margin)
    /// این مقدار همواره کوچکتر یا مساوی MaximumStableChannels است
    /// </summary>
    public int RecommendedChannels { get; set; }

    /// <summary>
    /// درصد حاشیه امن اعمال‌شده (پیش‌فرض ۲۵ درصد فضای تنفس برای سناریوهای ترافیک ناگهانی)
    /// </summary>
    public double AppliedSafetyMarginPercent { get; set; } = 25.0;

    /// <summary>
    /// آخرین شواهد بنچمارک معتبر که این ظرفیت بر اساس آن تایید شده است
    /// </summary>
    public BenchmarkEvidence? CalibratedEvidence { get; set; }

    /// <summary>
    /// بودجه تاخیر P95 هدف بر حسب میلی‌ثانیه (تنظیم‌پذیر بر اساس نیازمندی پروژه)
    /// </summary>
    public double TargetP95LatencyBudgetMs { get; set; } = 250.0;

    /// <summary>
    /// حداکثر زمان انتظار در صف بر حسب میلی‌ثانیه
    /// </summary>
    public double MaxQueueWaitBudgetMs { get; set; } = 150.0;

    /// <summary>
    /// حداکثر نرخ مجاز فریم‌های افتاده (Dropped Frames)
    /// </summary>
    public double MaxDroppedFrameRatioBudget { get; set; } = 0.005; // 0.5%

    public string StatusNotes { get; set; } = string.Empty;

    /// <summary>
    /// اعمال ضریب ایمنی برای محاسبه ظرفیت پیشنهادی از روی حداکثر ظرفیت پایدار
    /// هرگز به عنوان تضمین غیرواقعی تلقی نمی‌شود
    /// </summary>
    public void RecalculateRecommendedFromMax(double safetyMarginFraction = 0.25)
    {
        AppliedSafetyMarginPercent = Math.Clamp(safetyMarginFraction * 100.0, 5.0, 50.0);
        if (MaximumStableChannels <= 0)
        {
            RecommendedChannels = 0;
            return;
        }

        // فرمول: Recommended = floor(Max * (1 - Margin))
        int safeCount = (int)Math.Floor(MaximumStableChannels * (1.0 - (AppliedSafetyMarginPercent / 100.0)));
        RecommendedChannels = Math.Max(1, safeCount);
    }
}

/// <summary>
/// خلاصه گزارش پیشنهادی ظرفیت هوش مصنوعی سامانه
/// </summary>
public class CapacityRecommendationSummary
{
    public CapacityCalibrationState OverallState { get; init; }
    public string FingerprintHash { get; init; } = string.Empty;
    public int RecommendedAnprChannels { get; init; }
    public int MaxStableAnprChannels { get; init; }
    public int RecommendedFaceChannels { get; init; }
    public int MaxStableFaceChannels { get; init; }
    public int RecommendedCombinedChannels { get; init; }
    public int MaxStableCombinedChannels { get; init; }
    public double SafetyMarginPercent { get; init; }
    public string PolicyStatusDescription { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// خط‌مشی جامع ظرفیت هوش مصنوعی سامانه نظارتی مهرگان (AiCapacityPolicy)
/// 
/// اصول طراحی:
/// ۱. تفکیک کامل ظرفیت پلاک‌خوان، چهره، و ظرفیت ترکیبی.
/// ۲. قانون حیاتی: ظرفیت ترکیبی (Combined) هرگز نباید با جمع ساده ظرفیت‌های مستقل محاسبه شود.
/// ۳. تفکیک دقیق ظرفیت پیشنهادی (Recommended) از حداکثر ظرفیت پایدار (Maximum Stable).
/// ۴. پذیرش فقط شواهد بنچمارک معتبر (حداقل ۵ تا ۱۰ دقیقه) برای وضعیت Calibrated.
/// ۵. تفکیک صریح وضعیت‌های Estimated، Calibrating، Calibrated، و RecalibrationRequired.
/// ۶. پشتیبانی از حاشیه امن تنظیم‌پذیر و اجتناب از ارقام هاردکدشده یا ادعای تضمین بدون بنچمارک.
/// </summary>
public class AiCapacityPolicy
{
    public string PolicyVersion { get; init; } = "1.0.0";
    public DateTime LastUpdatedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// حاشیه ایمنی پیش‌فرض خط‌مشی (مثلاً ۰.۲۵ برابر ۲۵ درصد فضای احتیاطی)
    /// </summary>
    public double DefaultSafetyMargin { get; set; } = 0.25;

    /// <summary>
    /// بودجه تاخیر حداکثری P95 به صورت پیش‌فرض (میلی‌ثانیه)
    /// </summary>
    public double DefaultP95LatencyBudgetMs { get; set; } = 250.0;

    /// <summary>
    /// اثرانگشت کالیبراسیون فعال که این خط‌مشی بر اساس آن سنجیده شده است
    /// </summary>
    public CalibrationFingerprint? ActiveFingerprint { get; private set; }

    /// <summary>
    /// وضعیت کلی اعتبار خط‌مشی ظرفیت در کل سامانه
    /// </summary>
    public CapacityCalibrationState OverallState
    {
        get
        {
            if (AnprCapacity.State == CapacityCalibrationState.RecalibrationRequired ||
                FaceCapacity.State == CapacityCalibrationState.RecalibrationRequired ||
                CombinedCapacity.State == CapacityCalibrationState.RecalibrationRequired)
            {
                return CapacityCalibrationState.RecalibrationRequired;
            }

            if (AnprCapacity.State == CapacityCalibrationState.Calibrating ||
                FaceCapacity.State == CapacityCalibrationState.Calibrating ||
                CombinedCapacity.State == CapacityCalibrationState.Calibrating)
            {
                return CapacityCalibrationState.Calibrating;
            }

            if (AnprCapacity.State == CapacityCalibrationState.Calibrated &&
                FaceCapacity.State == CapacityCalibrationState.Calibrated &&
                CombinedCapacity.State == CapacityCalibrationState.Calibrated)
            {
                return CapacityCalibrationState.Calibrated;
            }

            return CapacityCalibrationState.Estimated;
        }
    }

    /// <summary>
    /// پرونده ظرفیت اختصاصی پلاک‌خوان
    /// </summary>
    public WorkloadCapacityEstimate AnprCapacity { get; } = new()
    {
        Workload = AiWorkloadType.AnprOnly,
        State = CapacityCalibrationState.Estimated,
        StatusNotes = "برآورد اولیه پیش‌فرض قبل از اجرای بنچمارک"
    };

    /// <summary>
    /// پرونده ظرفیت اختصاصی چهره غیرهویتی
    /// </summary>
    public WorkloadCapacityEstimate FaceCapacity { get; } = new()
    {
        Workload = AiWorkloadType.FaceOnly,
        State = CapacityCalibrationState.Estimated,
        StatusNotes = "برآورد اولیه پیش‌فرض قبل از اجرای بنچمارک"
    };

    /// <summary>
    /// پرونده ظرفیت پردازش ترکیبی همزمان (ANPR + Face)
    /// توجه: این مقدار مستقل از جمع دو بار دیگر و منحصراً بر پایه آزمون مشترک کالیبره می‌شود
    /// </summary>
    public WorkloadCapacityEstimate CombinedCapacity { get; } = new()
    {
        Workload = AiWorkloadType.Combined,
        State = CapacityCalibrationState.Estimated,
        StatusNotes = "برآورد اولیه ترکیبی (کوچکتر از مجموع بارهای مستقل)"
    };

    /// <summary>
    /// سازنده خط‌مشی با تنظیمات ایمنی اختیاری
    /// </summary>
    public AiCapacityPolicy(double safetyMargin = 0.25, double latencyBudgetMs = 250.0)
    {
        DefaultSafetyMargin = Math.Clamp(safetyMargin, 0.05, 0.50);
        DefaultP95LatencyBudgetMs = Math.Max(50.0, latencyBudgetMs);
    }

    /// <summary>
    /// مقداردهی اولیه یا به‌روزرسانی برآوردهای تقریبی (Heuristic Estimates) بر پایه کاوش سخت‌افزار
    /// قبل از اجرای بنچمارک عملیاتی
    /// </summary>
    public void InitializeProvisionalEstimates(HardwareSnapshot hardware, ExecutionProviderProfile epProfile)
    {
        bool isGpuAvailable = epProfile.DirectMlResult.State == ProviderOperationalState.FullyOperational;
        int logicalCores = Math.Max(1, hardware.Cpu.LogicalCores);
        double vramGb = hardware.PrimaryGpu?.DedicatedVramMb.HasValue == true
            ? hardware.PrimaryGpu.DedicatedVramMb.Value / 1024.0
            : 0.0;

        int estimatedAnpr;
        int estimatedFace;

        if (isGpuAvailable && vramGb >= 4.0)
        {
            // برآورد تقریبی روی GPU سازگار با DirectML و VRAM کافی
            estimatedAnpr = Math.Clamp((int)(vramGb * 1.5), 2, 12);
            estimatedFace = Math.Clamp((int)(vramGb * 1.5), 2, 12);
        }
        else
        {
            // برآورد تقریبی صرفاً بر مبنای CPU
            estimatedAnpr = Math.Clamp(logicalCores / 4, 1, 4);
            estimatedFace = Math.Clamp(logicalCores / 4, 1, 4);
        }

        // برآورد ترکیبی هرگز حاصل‌جمع نیست؛ بلکه کسری از حداقل بارهای کاری است
        // به دلیل اشتراک پهنای‌باند حافظه، دیکود چندگانه و کش VRAM/L3
        int estimatedCombined = Math.Max(1, (int)Math.Floor(Math.Min(estimatedAnpr, estimatedFace) * 0.65));

        AnprCapacity.MaximumStableChannels = estimatedAnpr;
        AnprCapacity.State = CapacityCalibrationState.Estimated;
        AnprCapacity.RecalculateRecommendedFromMax(DefaultSafetyMargin);
        AnprCapacity.StatusNotes = $"برآورد تخمینی اولیه بر پایه سخت‌افزار (GPU={isGpuAvailable}, Cores={logicalCores})";

        FaceCapacity.MaximumStableChannels = estimatedFace;
        FaceCapacity.State = CapacityCalibrationState.Estimated;
        FaceCapacity.RecalculateRecommendedFromMax(DefaultSafetyMargin);
        FaceCapacity.StatusNotes = $"برآورد تخمینی اولیه بر پایه سخت‌افزار (GPU={isGpuAvailable}, Cores={logicalCores})";

        CombinedCapacity.MaximumStableChannels = estimatedCombined;
        CombinedCapacity.State = CapacityCalibrationState.Estimated;
        CombinedCapacity.RecalculateRecommendedFromMax(DefaultSafetyMargin);
        CombinedCapacity.StatusNotes = "برآورد تخمینی بار ترکیبی اولیه (تضعیف غیرخطی به علت اشتراک منابع)";

        LastUpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// اعمال شواهد تجربی یک بنچمارک معتبر و انتقال وضعیت به Calibrated
    /// </summary>
    public bool TryApplyBenchmarkEvidence(
        BenchmarkEvidence evidence,
        CalibrationFingerprint currentFingerprint,
        out string validationMessage)
    {
        if (evidence == null)
        {
            validationMessage = "شواهد بنچمارک نمی‌تواند خالی (null) باشد.";
            return false;
        }

        if (currentFingerprint == null)
        {
            validationMessage = "اثرانگشت جاری سیستم نامشخص است.";
            return false;
        }

        // ۱. بررسی صلاحیت و مدت‌زمان بنچمارک (دود ۳۰ ثانیه‌ای هرگز کالیبره نمی‌کند)
        if (!evidence.IsEligibleForCalibration(out string eligibleReason))
        {
            validationMessage = eligibleReason;
            return false;
        }

        // ۲. انطباق اثرانگشت شواهد با اثرانگشت جاری
        if (!string.Equals(evidence.FingerprintHash, currentFingerprint.DeterministicHash, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "هش اثرانگشت مندرج در شواهد بنچمارک با اثرانگشت جاری سیستم منطبق نیست. کالیبراسیون باطل است.";
            return false;
        }

        // ۳. بررسی رعایت بودجه تاخیر در بنچمارک
        if (evidence.EndToEndLatencyP95Ms > DefaultP95LatencyBudgetMs)
        {
            validationMessage = $"تاخیر سرتاسری P95 اندازه‌گیری‌شده ({evidence.EndToEndLatencyP95Ms:F1}ms) بیش از بودجه مجاز ({DefaultP95LatencyBudgetMs:F1}ms) است.";
            return false;
        }

        WorkloadCapacityEstimate targetEstimate = evidence.Workload switch
        {
            AiWorkloadType.AnprOnly => AnprCapacity,
            AiWorkloadType.FaceOnly => FaceCapacity,
            AiWorkloadType.Combined => CombinedCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(evidence.Workload))
        };

        // ۴. قاعده سخت‌گیرانه برای بار کاری ترکیبی:
        // اگر بار کاری ترکیبی باشد، باید اثبات شود که از بنچمارک ترکیبی واقعی آمده و به صورت ساده حاصل جمع نیست
        if (evidence.Workload == AiWorkloadType.Combined)
        {
            if (AnprCapacity.MaximumStableChannels > 0 && FaceCapacity.MaximumStableChannels > 0)
            {
                if (evidence.TestedChannelCount >= AnprCapacity.MaximumStableChannels + FaceCapacity.MaximumStableChannels)
                {
                    validationMessage = "رد شد: ظرفیت ترکیبی آزموده‌شده مساوی یا بزرگتر از مجموع جبری دو بار مستقل است که با قوانین همپوشانی منابع مغایرت دارد.";
                    return false;
                }
            }
        }

        // ثبت موفق شواهد
        targetEstimate.CalibratedEvidence = evidence;
        targetEstimate.MaximumStableChannels = evidence.TestedChannelCount;
        targetEstimate.State = CapacityCalibrationState.Calibrated;
        targetEstimate.RecalculateRecommendedFromMax(DefaultSafetyMargin);
        targetEstimate.StatusNotes = $"کالیبره شده با بنچمارک {evidence.ActualDuration.TotalMinutes:F1} دقیقه‌ای در {evidence.ExecutedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}";

        ActiveFingerprint = currentFingerprint;
        LastUpdatedUtc = DateTime.UtcNow;

        validationMessage = $"ظرفیت کاری {evidence.Workload} با موفقیت در وضعیت کالیبره قرار گرفت (پیشنهادی: {targetEstimate.RecommendedChannels} کانال، پایدار: {targetEstimate.MaximumStableChannels} کانال).";
        return true;
    }

    /// <summary>
    /// بررسی و تنظیم ظرفیت ترکیبی با تضمین عدم محاسبه از طریق جمع ساده بارهای مستقل
    /// </summary>
    public bool ValidateAndSetCombinedCapacity(
        int maxStableCombined,
        string sourceReason,
        BenchmarkEvidence? evidence = null)
    {
        if (AnprCapacity.MaximumStableChannels > 0 && FaceCapacity.MaximumStableChannels > 0)
        {
            int naiveSum = AnprCapacity.MaximumStableChannels + FaceCapacity.MaximumStableChannels;
            if (maxStableCombined >= naiveSum)
            {
                throw new InvalidOperationException(
                    $"خطای نقض خط‌مشی: ظرفیت ترکیبی ({maxStableCombined}) نمی‌تواند مساوی یا بزرگتر از جمع جبری مستقل پلاک ({AnprCapacity.MaximumStableChannels}) و چهره ({FaceCapacity.MaximumStableChannels}) باشد.");
            }
        }

        CombinedCapacity.MaximumStableChannels = maxStableCombined;
        CombinedCapacity.CalibratedEvidence = evidence;
        CombinedCapacity.State = evidence != null ? CapacityCalibrationState.Calibrated : CapacityCalibrationState.Estimated;
        CombinedCapacity.RecalculateRecommendedFromMax(DefaultSafetyMargin);
        CombinedCapacity.StatusNotes = sourceReason;
        LastUpdatedUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// ابطال وضعیت کالیبراسیون در هنگام بروز تغییرات در اثرانگشت سخت‌افزار یا خط لوله
    /// </summary>
    public void InvalidateCalibration(FingerprintComparisonResult comparisonResult)
    {
        var invalidatingDiffs = comparisonResult.InvalidatingDifferences;
        string reasons = string.Join(" | ", invalidatingDiffs);

        AnprCapacity.State = CapacityCalibrationState.RecalibrationRequired;
        AnprCapacity.StatusNotes = $"ابطال به دلیل تغییر شرایط: {reasons}";

        FaceCapacity.State = CapacityCalibrationState.RecalibrationRequired;
        FaceCapacity.StatusNotes = $"ابطال به دلیل تغییر شرایط: {reasons}";

        CombinedCapacity.State = CapacityCalibrationState.RecalibrationRequired;
        CombinedCapacity.StatusNotes = $"ابطال به دلیل تغییر شرایط: {reasons}";

        LastUpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// تولید خلاصه گزارش وضعیت پیشنهادی ظرفیت جهت نمایش در واسط کاربری یا لاگ سیستم
    /// </summary>
    public CapacityRecommendationSummary GetRecommendationSummary()
    {
        var warnings = new List<string>();

        if (OverallState == CapacityCalibrationState.Estimated)
        {
            warnings.Add("ظرفیت‌های فعلی صرفاً برآورد تخمینی اولیه هستند. لطفاً پیش از بهره‌برداری نهایی، آزمون کالیبراسیون ۵ تا ۱۰ دقیقه‌ای را اجرا فرمایید.");
        }
        else if (OverallState == CapacityCalibrationState.RecalibrationRequired)
        {
            warnings.Add("شرایط محیطی یا پیکربندی استریم با کالیبراسیون پیشین تطابق ندارد. اجرای مجدد بنچمارک الزامی است.");
        }

        if (AnprCapacity.CalibratedEvidence?.SourceType == CalibrationStreamSourceType.LocalVideoFile)
        {
            warnings.Add("توجه: بنچمارک پلاک‌خوان با فایل ویدیویی محلی انجام شده است؛ برای استریم‌های شبکه زنده RTSP با تاخیر شبکه، کالیبراسیون مجدد زنده توصیه می‌شود.");
        }

        string description = OverallState switch
        {
            CapacityCalibrationState.Calibrated => "ظرفیت با شواهد بنچمارک رسمی کالیبره و تایید شده است.",
            CapacityCalibrationState.Estimated => "ظرفیت بر اساس مشخصات تئوریک سخت‌افزار برآورد شده است (تخمینی).",
            CapacityCalibrationState.Calibrating => "بنچمارک سنجش ظرفیت در حال اجرا است.",
            CapacityCalibrationState.RecalibrationRequired => "کالیبراسیون باطل شده و نیاز به سنجش مجدد دارد.",
            _ => "وضعیت نامشخص"
        };

        return new CapacityRecommendationSummary
        {
            OverallState = OverallState,
            FingerprintHash = ActiveFingerprint?.DeterministicHash ?? string.Empty,
            RecommendedAnprChannels = AnprCapacity.RecommendedChannels,
            MaxStableAnprChannels = AnprCapacity.MaximumStableChannels,
            RecommendedFaceChannels = FaceCapacity.RecommendedChannels,
            MaxStableFaceChannels = FaceCapacity.MaximumStableChannels,
            RecommendedCombinedChannels = CombinedCapacity.RecommendedChannels,
            MaxStableCombinedChannels = CombinedCapacity.MaximumStableChannels,
            SafetyMarginPercent = DefaultSafetyMargin * 100.0,
            PolicyStatusDescription = description,
            Warnings = warnings
        };
    }
}
