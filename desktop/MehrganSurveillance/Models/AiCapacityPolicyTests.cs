using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز اعتبارسنجی شرایط صلاحیت کالیبراسیون و بررسی رفتار متقابل
/// PassedDiagnosticCriteria و PassedAllCriteria
/// </summary>
public static class AiCapacityPolicyTests
{
    /// <summary>
    /// تولید یک نمونه پایه معتبر از شواهد بنچمارک که تمامی شروط غیرمرتبط با دو فلگ مورد آزمون را برآورده می‌کند
    /// </summary>
    public static BenchmarkEvidence CreateBaseCalibratableEvidence(
        string fingerprintHash,
        bool passedDiagnostic = true,
        bool passedAll = true)
    {
        return new BenchmarkEvidence
        {
            RunId = Guid.NewGuid(),
            ExecutedAtUtc = DateTime.UtcNow,
            DurationTier = BenchmarkDurationTier.Calibration5to10m,
            ActualDuration = TimeSpan.FromMinutes(5.0),
            SourceType = CalibrationStreamSourceType.LocalVideoFile,
            InputProvenance = BenchmarkInputProvenance.OfflineRecordedVideo,
            ModelFidelity = BenchmarkModelFidelity.FullProductionModel,
            FingerprintHash = fingerprintHash,
            Workload = AiWorkloadType.AnprOnly,
            TestedChannelCount = 4,

            AcquisitionLatencyP50Ms = 8.0,
            AcquisitionLatencyP95Ms = 15.0,
            PreprocessingLatencyP50Ms = 4.0,
            PreprocessingLatencyP95Ms = 7.0,
            InferenceLatencyP50Ms = 25.0,
            InferenceLatencyP95Ms = 38.0,
            PostprocessingLatencyP50Ms = 3.0,
            PostprocessingLatencyP95Ms = 5.0,
            EndToEndLatencyP50Ms = 40.0,
            EndToEndLatencyP95Ms = 65.0, // زیر بودجه DefaultP95LatencyBudgetMs (120ms)

            AchievedProcessingFps = 25.0,
            QueueWaitTimeP95Ms = 5.0,
            DroppedFrameRatio = 0.001,

            UiDispatcherLagP95Ms = 8.0,
            InvariantsVerified = true,
            ConcurrencyMode = ExecutionConcurrencyMode.TrueConcurrent,
            ConcurrencyOverlapRatio = 0.85,

            MemoryMetrics = new MemoryStabilityMetrics
            {
                ManagedHeapBytes = 50 * 1024 * 1024,
                ProcessPrivateBytes = 250 * 1024 * 1024,
                ProcessWorkingSetBytes = 300 * 1024 * 1024,
                MemoryGrowthRateMbPerHour = 12.0,
                IsMemorySlopeAcceptable = true,
                MeasuredDuration = TimeSpan.FromMinutes(5.0)
            },

            PassedDiagnosticCriteria = passedDiagnostic,
            PassedAllCriteria = passedAll,
            ValidationMessages = new List<string>()
        };
    }

    /// <summary>
    /// مورد ۱: Diagnostic=false, All=true -> باید قطعاً رد شود (PassedAllCriteria هرگز نباید نقض تشخیصی را دور بزند)
    /// </summary>
    public static (bool Passed, string Message) Test_DiagnosticFalse_AllTrue_MustBeRejected()
    {
        var fingerprint = new CalibrationFingerprint();
        var evidence = CreateBaseCalibratableEvidence(fingerprint.DeterministicHash, passedDiagnostic: false, passedAll: true);
        var policy = new AiCapacityPolicy();

        bool isEligible = evidence.IsEligibleForCalibration(out string reason);
        if (isEligible)
        {
            return (false, "خطا: آزمون در حالت Diagnostic=false و All=true نباید تایید می‌شد!");
        }

        bool applied = policy.TryApplyBenchmarkEvidence(evidence, fingerprint, out string policyMsg);
        if (applied)
        {
            return (false, "خطا: شواهد با Diagnostic=false نباید در خط‌مشی اعمال و کالیبره می‌شد!");
        }

        if (!reason.Contains("تشخیصی"))
        {
            return (false, $"خطا: پیام رد شواهد باید به نقض معیارهای تشخیصی اشاره کند. پیام فعلی: {reason}");
        }

        return (true, "موفق: شواهد با Diagnostic=false و All=true با موفقیت و بر اساس گارد مستقل تشخیصی رد شد.");
    }

    /// <summary>
    /// مورد ۲: Diagnostic=false, All=false -> باید قطعاً رد شود
    /// </summary>
    public static (bool Passed, string Message) Test_DiagnosticFalse_AllFalse_MustBeRejected()
    {
        var fingerprint = new CalibrationFingerprint();
        var evidence = CreateBaseCalibratableEvidence(fingerprint.DeterministicHash, passedDiagnostic: false, passedAll: false);
        var policy = new AiCapacityPolicy();

        bool isEligible = evidence.IsEligibleForCalibration(out string reason);
        if (isEligible)
        {
            return (false, "خطا: آزمون در حالت Diagnostic=false و All=false نباید تایید می‌شد!");
        }

        bool applied = policy.TryApplyBenchmarkEvidence(evidence, fingerprint, out string _);
        if (applied)
        {
            return (false, "خطا: شواهد در خط‌مشی نباید اعمال می‌شد!");
        }

        return (true, "موفق: شواهد با Diagnostic=false و All=false با موفقیت رد شد.");
    }

    /// <summary>
    /// مورد ۳: Diagnostic=true, All=false -> باید طبق معنای مستقل و تاییدشده All رد شود
    /// </summary>
    public static (bool Passed, string Message) Test_DiagnosticTrue_AllFalse_FollowsIntendedSemantics_MustBeRejected()
    {
        var fingerprint = new CalibrationFingerprint();
        var evidence = CreateBaseCalibratableEvidence(fingerprint.DeterministicHash, passedDiagnostic: true, passedAll: false);
        var policy = new AiCapacityPolicy();

        bool isEligible = evidence.IsEligibleForCalibration(out string reason);
        if (isEligible)
        {
            return (false, "خطا: آزمون در حالت Diagnostic=true و All=false نباید تایید می‌شد؛ شرایط جامع عملیاتی احراز نشده است!");
        }

        bool applied = policy.TryApplyBenchmarkEvidence(evidence, fingerprint, out string _);
        if (applied)
        {
            return (false, "خطا: شواهد با All=false نباید در خط‌مشی به وضعیت Calibrated منتقل می‌شد!");
        }

        if (!reason.Contains("جامع") && !reason.Contains("عملیاتی"))
        {
            return (false, $"خطا: پیام رد شواهد باید به عدم احراز شرایط جامع عملیاتی اشاره کند. پیام فعلی: {reason}");
        }

        return (true, "موفق: شواهد با Diagnostic=true و All=false بر اساس معنای مستقل PassedAllCriteria رد شد.");
    }

    /// <summary>
    /// مورد ۴: Diagnostic=true, All=true -> در صورت برقراری سایر شروط باید پذیرفته شود
    /// </summary>
    public static (bool Passed, string Message) Test_DiagnosticTrue_AllTrue_Accepted_WhenOtherChecksPass()
    {
        var fingerprint = new CalibrationFingerprint();
        var evidence = CreateBaseCalibratableEvidence(fingerprint.DeterministicHash, passedDiagnostic: true, passedAll: true);
        var policy = new AiCapacityPolicy();

        bool isEligible = evidence.IsEligibleForCalibration(out string reason);
        if (!isEligible)
        {
            return (false, $"خطا: آزمون با Diagnostic=true و All=true و تمامی شروط معتبر رد شد: {reason}");
        }

        bool applied = policy.TryApplyBenchmarkEvidence(evidence, fingerprint, out string policyMsg);
        if (!applied)
        {
            return (false, $"خطا: اعمال شواهد در خط‌مشی ناموفق بود: {policyMsg}");
        }

        if (policy.AnprCapacity.State != CapacityCalibrationState.Calibrated)
        {
            return (false, $"خطا: وضعیت ظرفیت پس از اعمال شواهد به Calibrated تغییر نیافت! وضعیت فعلی: {policy.AnprCapacity.State}");
        }

        if (policy.AnprCapacity.MaximumStableChannels != 4)
        {
            return (false, $"خطا: حداکثر ظرفیت پایدار کالیبره شده با تعداد کانال‌های آزموده‌شده منطبق نیست!");
        }

        return (true, "موفق: شواهد با Diagnostic=true و All=true با موفقیت پذیرفته شده و وضعیت ظرفیت را به Calibrated ارتقا داد.");
    }

    /// <summary>
    /// آزمون تکمیلی: بررسی پایستگی سایر گاردهای صلاحیت حتی در صورت true بودن هر دو فلگ
    /// </summary>
    public static (bool Passed, string Message) Test_DiagnosticTrue_AllTrue_StillRejected_WhenOtherGuardsFail()
    {
        var fingerprint = new CalibrationFingerprint();

        // الف) اصالت مدل سنتتیک تشخیصی
        var syntheticModelEvidence = new BenchmarkEvidence
        {
            DurationTier = BenchmarkDurationTier.Calibration5to10m,
            ActualDuration = TimeSpan.FromMinutes(5.0),
            ModelFidelity = BenchmarkModelFidelity.SyntheticDiagnostic,
            InputProvenance = BenchmarkInputProvenance.OfflineRecordedVideo,
            FingerprintHash = fingerprint.DeterministicHash,
            InvariantsVerified = true,
            MemoryMetrics = new MemoryStabilityMetrics { IsMemorySlopeAcceptable = true },
            PassedDiagnosticCriteria = true,
            PassedAllCriteria = true
        };
        if (syntheticModelEvidence.IsEligibleForCalibration(out _))
        {
            return (false, "خطا: مدل سنتتیک نباید واجد شرایط کالیبراسیون باشد!");
        }

        // ب) ورودی بافر حافظه سنتتیک
        var syntheticInputEvidence = new BenchmarkEvidence
        {
            DurationTier = BenchmarkDurationTier.Calibration5to10m,
            ActualDuration = TimeSpan.FromMinutes(5.0),
            ModelFidelity = BenchmarkModelFidelity.FullProductionModel,
            InputProvenance = BenchmarkInputProvenance.SyntheticMemoryBuffer,
            FingerprintHash = fingerprint.DeterministicHash,
            InvariantsVerified = true,
            MemoryMetrics = new MemoryStabilityMetrics { IsMemorySlopeAcceptable = true },
            PassedDiagnosticCriteria = true,
            PassedAllCriteria = true
        };
        if (syntheticInputEvidence.IsEligibleForCalibration(out _))
        {
            return (false, "خطا: ورودی حافظه سنتتیک نباید واجد شرایط کالیبراسیون باشد!");
        }

        // ج) نقض پایستگی فریم
        var brokenInvariantsEvidence = new BenchmarkEvidence
        {
            DurationTier = BenchmarkDurationTier.Calibration5to10m,
            ActualDuration = TimeSpan.FromMinutes(5.0),
            ModelFidelity = BenchmarkModelFidelity.FullProductionModel,
            InputProvenance = BenchmarkInputProvenance.OfflineRecordedVideo,
            FingerprintHash = fingerprint.DeterministicHash,
            InvariantsVerified = false,
            MemoryMetrics = new MemoryStabilityMetrics { IsMemorySlopeAcceptable = true },
            PassedDiagnosticCriteria = true,
            PassedAllCriteria = true
        };
        if (brokenInvariantsEvidence.IsEligibleForCalibration(out _))
        {
            return (false, "خطا: نقض پایستگی حسابداری فریم نباید واجد شرایط کالیبراسیون باشد!");
        }

        return (true, "موفق: سایر گاردهای صلاحیت (ModelFidelity, InputProvenance, Invariants) حتی با وجود true بودن هر دو فلگ پابرجا هستند.");
    }

    /// <summary>
    /// اجرای یکپارچه تمامی آزمون‌ها
    /// </summary>
    public static List<(string TestName, bool Passed, string Message)> RunAllTests()
    {
        var results = new List<(string, bool, string)>
        {
            ("Test_DiagnosticFalse_AllTrue_MustBeRejected", Test_DiagnosticFalse_AllTrue_MustBeRejected().Passed, Test_DiagnosticFalse_AllTrue_MustBeRejected().Message),
            ("Test_DiagnosticFalse_AllFalse_MustBeRejected", Test_DiagnosticFalse_AllFalse_MustBeRejected().Passed, Test_DiagnosticFalse_AllFalse_MustBeRejected().Message),
            ("Test_DiagnosticTrue_AllFalse_FollowsIntendedSemantics_MustBeRejected", Test_DiagnosticTrue_AllFalse_FollowsIntendedSemantics_MustBeRejected().Passed, Test_DiagnosticTrue_AllFalse_FollowsIntendedSemantics_MustBeRejected().Message),
            ("Test_DiagnosticTrue_AllTrue_Accepted_WhenOtherChecksPass", Test_DiagnosticTrue_AllTrue_Accepted_WhenOtherChecksPass().Passed, Test_DiagnosticTrue_AllTrue_Accepted_WhenOtherChecksPass().Message),
            ("Test_DiagnosticTrue_AllTrue_StillRejected_WhenOtherGuardsFail", Test_DiagnosticTrue_AllTrue_StillRejected_WhenOtherGuardsFail().Passed, Test_DiagnosticTrue_AllTrue_StillRejected_WhenOtherGuardsFail().Message)
        };

        return results;
    }
}
