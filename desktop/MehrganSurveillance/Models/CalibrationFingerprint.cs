using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MehrganSurveillance.Models;

/// <summary>
/// منبع استریم ویدیویی مورد استفاده در کالیبراسیون و بنچمارک
/// تفکیک صریح فایل ویدیویی محلی از استریم زنده شبکه RTSP
/// </summary>
public enum CalibrationStreamSourceType
{
    LocalVideoFile,
    LiveRtspStream
}

/// <summary>
/// شدت یا درجه اهمیت تفاوت بین دو اثرانگشت کالیبراسیون
/// </summary>
public enum InvalidationSeverity
{
    None,
    Informational,       // تفاوت جزئی بدون نیاز به ابطال کالیبراسیون (مانند وصله جزئی ویندوز)
    InvalidatingChange   // تفاوت ساختاری که نتایج کالیبراسیون قبلی را کاملاً باطل می‌کند
}

/// <summary>
/// جزئیات یک تغییر یا تفاوت کشف‌شده بین دو اثرانگشت کالیبراسیون
/// </summary>
public class FingerprintDifference
{
    public string Category { get; init; } = string.Empty;
    public string ParameterName { get; init; } = string.Empty;
    public string? BaselineValue { get; init; }
    public string? CurrentValue { get; init; }
    public InvalidationSeverity Severity { get; init; } = InvalidationSeverity.InvalidatingChange;
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// نتیجه مقایسه جامع دو اثرانگشت کالیبراسیون
/// </summary>
public class FingerprintComparisonResult
{
    public bool IsIdentical => Differences.Count == 0;
    public bool IsCalibrationValid => !Differences.Any(d => d.Severity == InvalidationSeverity.InvalidatingChange);
    public List<FingerprintDifference> Differences { get; init; } = new();

    public IEnumerable<FingerprintDifference> InvalidatingDifferences =>
        Differences.Where(d => d.Severity == InvalidationSeverity.InvalidatingChange);

    public IEnumerable<FingerprintDifference> InformationalDifferences =>
        Differences.Where(d => d.Severity == InvalidationSeverity.Informational);
}

/// <summary>
/// مشخصات هویتی و توانایی‌های سخت‌افزار در اثرانگشت
/// </summary>
public class HardwareFingerprintComponent
{
    // OS Profile
    public string OsProductName { get; init; } = "Windows";
    public string OsDisplayVersion { get; init; } = string.Empty; // e.g. "23H2"
    public string OsBuildNumber { get; init; } = string.Empty;     // e.g. "22631"
    public int? OsUbr { get; init; }                              // Update Build Revision
    public bool Is64BitOperatingSystem { get; init; } = true;
    public HardwareMetricStatus OsStatus { get; init; } = HardwareMetricStatus.Unknown;

    // CPU Profile
    public string CpuModelName { get; init; } = string.Empty;
    public int CpuLogicalCores { get; init; }
    public int? CpuPhysicalCores { get; init; }
    public string CpuArchitecture { get; init; } = "x64";
    public bool? HasAvx2Support { get; init; }
    public bool? HasFmaSupport { get; init; }
    public HardwareMetricStatus CpuStatus { get; init; } = HardwareMetricStatus.Unknown;

    // GPU Profile (Primary Adapter)
    public string GpuName { get; init; } = "نامشخص";
    public string GpuVendor { get; init; } = "Unknown";
    public string? GpuDeviceId { get; init; }
    public string? GpuDriverVersion { get; init; }
    public long? DedicatedVideoMemoryBytes { get; init; }
    public long? SharedSystemMemoryBytes { get; init; }
    public bool IsSoftwareRenderer { get; init; }
    public HardwareMetricStatus GpuStatus { get; init; } = HardwareMetricStatus.Unknown;

    // RAM Profile
    public long? TotalPhysicalMemoryBytes { get; init; }
    public HardwareMetricStatus MemoryStatus { get; init; } = HardwareMetricStatus.Unknown;
}

/// <summary>
/// مشخصات محیط اجرای موتور استنتاج هوش مصنوعی
/// </summary>
public class RuntimeFingerprintComponent
{
    public string DotNetVersion { get; init; } = "10.0";
    public string OnnxRuntimeVersion { get; init; } = "1.20.1";
    public string SelectedExecutionProvider { get; init; } = "DirectML"; // DirectML or CPU
    public int DirectMlDeviceId { get; init; } = 0;
    public string ExecutionMode { get; init; } = "ORT_SEQUENTIAL";
    public bool EnableMemoryPattern { get; init; } = false; // Must be false for DirectML stability
    public bool PerSessionLockingEnforced { get; init; } = true;
    public HardwareMetricStatus Status { get; init; } = HardwareMetricStatus.Verified;
}

/// <summary>
/// مشخصات دیکودر ویدیویی و کتابخانه‌های نیتیو رسانه
/// </summary>
public class DecoderFingerprintComponent
{
    public string LibVlcVersion { get; init; } = "3.0.21";
    public string LibVlcSharpVersion { get; init; } = "3.9.0";
    public string HardwareDecodingMode { get; init; } = "d3d11va"; // d3d11va, dxva2, none, any
    public string PreferredPixelFormat { get; init; } = "RV32";
    public HardwareMetricStatus Status { get; init; } = HardwareMetricStatus.Verified;
}

/// <summary>
/// مشخصات هش و ابعاد مدل‌های هوش مصنوعی ثبت‌شده در سامانه
/// </summary>
public class ModelHashEntry
{
    public string ModelKey { get; init; } = string.Empty; // e.g. "AnprPlateDetector", "AnprOcr", "FaceDetector"
    public string FileName { get; init; } = string.Empty;
    public string Sha256Hash { get; init; } = string.Empty;
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public string Precision { get; init; } = "FP32";
    public HardwareMetricStatus Status { get; init; } = HardwareMetricStatus.Verified;
}

/// <summary>
/// مشخصات استریم ورودی دوربین‌ها در زمان کالیبراسیون
/// </summary>
public class StreamFingerprintComponent
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public string Encoding { get; init; } = "H.264";
    public int NominalFps { get; init; } = 25;
    public CalibrationStreamSourceType SourceType { get; init; } = CalibrationStreamSourceType.LiveRtspStream;
    public HardwareMetricStatus Status { get; init; } = HardwareMetricStatus.Verified;

    public string ResolutionLabel => $"{Width}x{Height}";
}

/// <summary>
/// مشخصات نمونه‌برداری تطبیقی و پیش‌پردازش فریم‌ها
/// </summary>
public class SamplingFingerprintComponent
{
    public double BaselineSamplingFps { get; init; } = 2.0;
    public double MotionTriggeredBoostFps { get; init; } = 6.0;
    public bool AdaptiveSamplingEnabled { get; init; } = true;
    public string PreprocessingColorFormat { get; init; } = "RGB_PLANAR_NCHW";
    public string NormalizationRange { get; init; } = "[0.0, 1.0]";
    public HardwareMetricStatus Status { get; init; } = HardwareMetricStatus.Verified;
}

/// <summary>
/// شناسنامه قطعی و یکتای شرایط کالیبراسیون (CalibrationFingerprint)
/// ثبت‌کننده وضعیت سخت‌افزار، درایور، ران‌تایم ONNX، تنظیمات Execution Provider،
/// هش مدل‌ها، نسخه دیکودر، پروفایل استریم و پارامترهای سمپلینگ.
/// این اثرانگشت امکان ردیابی و ابطال دقیق نتایج بنچمارک در صورت تغییر شرایط را فراهم می‌سازد.
/// </summary>
public class CalibrationFingerprint
{
    public string SchemaVersion { get; init; } = "1.0.0";
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public HardwareFingerprintComponent Hardware { get; init; } = new();
    public RuntimeFingerprintComponent Runtime { get; init; } = new();
    public DecoderFingerprintComponent Decoder { get; init; } = new();
    public List<ModelHashEntry> Models { get; init; } = new();
    public StreamFingerprintComponent StreamProfile { get; init; } = new();
    public SamplingFingerprintComponent SamplingConfig { get; init; } = new();

    private string? _cachedDeterministicHash;

    /// <summary>
    /// مقدار هش قطعی (Deterministic SHA-256) محاسبه‌شده از داده‌های کلیدی اثرانگشت
    /// </summary>
    [JsonPropertyName("deterministicHash")]
    public string DeterministicHash => _cachedDeterministicHash ??= ComputeDeterministicHash();

    /// <summary>
    /// محاسبه هش SHA-256 قطعی بر اساس محتوای ساختاریافته و استاندارد اثرانگشت
    /// </summary>
    public string ComputeDeterministicHash()
    {
        var canonicalBuilder = new StringBuilder();

        // 1. Hardware
        canonicalBuilder.Append($"OS:{Hardware.OsProductName}|{Hardware.OsDisplayVersion}|{Hardware.OsBuildNumber};");
        canonicalBuilder.Append($"CPU:{Hardware.CpuModelName}|{Hardware.CpuLogicalCores}|{Hardware.CpuPhysicalCores}|{Hardware.HasAvx2Support}|{Hardware.HasFmaSupport};");
        canonicalBuilder.Append($"GPU:{Hardware.GpuName}|{Hardware.GpuVendor}|{Hardware.GpuDeviceId}|{Hardware.GpuDriverVersion}|{Hardware.DedicatedVideoMemoryBytes}|{Hardware.IsSoftwareRenderer};");
        canonicalBuilder.Append($"RAM:{Hardware.TotalPhysicalMemoryBytes};");

        // 2. Runtime
        canonicalBuilder.Append($"RT:{Runtime.DotNetVersion}|{Runtime.OnnxRuntimeVersion}|{Runtime.SelectedExecutionProvider}|{Runtime.DirectMlDeviceId}|{Runtime.ExecutionMode}|{Runtime.EnableMemoryPattern}|{Runtime.PerSessionLockingEnforced};");

        // 3. Decoder
        canonicalBuilder.Append($"DEC:{Decoder.LibVlcVersion}|{Decoder.LibVlcSharpVersion}|{Decoder.HardwareDecodingMode}|{Decoder.PreferredPixelFormat};");

        // 4. Models (Sorted by ModelKey for determinism)
        foreach (var m in Models.OrderBy(m => m.ModelKey, StringComparer.Ordinal))
        {
            canonicalBuilder.Append($"MOD:{m.ModelKey}:{m.Sha256Hash}:{m.InputWidth}x{m.InputHeight}:{m.Precision};");
        }

        // 5. Stream
        canonicalBuilder.Append($"STR:{StreamProfile.Width}x{StreamProfile.Height}|{StreamProfile.Encoding}|{StreamProfile.NominalFps}|{StreamProfile.SourceType};");

        // 6. Sampling
        canonicalBuilder.Append($"SMP:{SamplingConfig.BaselineSamplingFps:F2}|{SamplingConfig.MotionTriggeredBoostFps:F2}|{SamplingConfig.AdaptiveSamplingEnabled}|{SamplingConfig.PreprocessingColorFormat}|{SamplingConfig.NormalizationRange};");

        byte[] bytes = Encoding.UTF8.GetBytes(canonicalBuilder.ToString());
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// مقایسه اثرانگشت جاری با اثرانگشت قبلی و تشخیص هرگونه تغییر ابطال‌کننده کالیبراسیون
    /// </summary>
    public FingerprintComparisonResult CompareTo(CalibrationFingerprint other)
    {
        var diffs = new List<FingerprintDifference>();

        if (other == null)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Baseline",
                ParameterName = "TargetFingerprint",
                BaselineValue = "null",
                CurrentValue = DeterministicHash,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "اثرانگشت مبنا وجود ندارد یا خالی است."
            });
            return new FingerprintComparisonResult { Differences = diffs };
        }

        // 1. مقایسه سیستم‌عامل
        if (!string.Equals(Hardware.OsBuildNumber, other.Hardware.OsBuildNumber, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Hardware.OS",
                ParameterName = "OsBuildNumber",
                BaselineValue = other.Hardware.OsBuildNumber,
                CurrentValue = Hardware.OsBuildNumber,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "بیلد سیستم‌عامل ویندوز تغییر کرده است."
            });
        }
        else if (Hardware.OsUbr != other.Hardware.OsUbr)
        {
            // تغییر در وصله امنیتی فرعی (UBR) به عنوان تفاوت اطلاعاتی ثبت می‌شود
            diffs.Add(new FingerprintDifference
            {
                Category = "Hardware.OS",
                ParameterName = "OsUbr",
                BaselineValue = other.Hardware.OsUbr?.ToString(),
                CurrentValue = Hardware.OsUbr?.ToString(),
                Severity = InvalidationSeverity.Informational,
                Description = "وصله فرعی ویندوز (UBR) تغییر یافته اما هسته بیلد ثابت است."
            });
        }

        // 2. مقایسه پردازنده (CPU)
        if (!string.Equals(Hardware.CpuModelName, other.Hardware.CpuModelName, StringComparison.OrdinalIgnoreCase) ||
            Hardware.CpuLogicalCores != other.Hardware.CpuLogicalCores)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Hardware.CPU",
                ParameterName = "CpuModelOrCores",
                BaselineValue = $"{other.Hardware.CpuModelName} ({other.Hardware.CpuLogicalCores}T)",
                CurrentValue = $"{Hardware.CpuModelName} ({Hardware.CpuLogicalCores}T)",
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "مدل یا تعداد هسته‌های پردازنده مرکزی تغییر کرده است."
            });
        }

        // 3. مقایسه کارت گرافیک (GPU) و درایور
        if (!string.Equals(Hardware.GpuName, other.Hardware.GpuName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Hardware.GpuDeviceId, other.Hardware.GpuDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Hardware.GPU",
                ParameterName = "GpuAdapter",
                BaselineValue = $"{other.Hardware.GpuName} [{other.Hardware.GpuDeviceId}]",
                CurrentValue = $"{Hardware.GpuName} [{Hardware.GpuDeviceId}]",
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "کارت گرافیک یا آداپتور اصلی تغییر کرده است."
            });
        }

        if (!string.Equals(Hardware.GpuDriverVersion, other.Hardware.GpuDriverVersion, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Hardware.GPU",
                ParameterName = "GpuDriverVersion",
                BaselineValue = other.Hardware.GpuDriverVersion,
                CurrentValue = Hardware.GpuDriverVersion,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "نسخه درایور کارت گرافیک تغییر کرده است (رفتار شتاب‌دهنده DirectML ممکن است تغییر کند)."
            });
        }

        // 4. مقایسه حافظه رم کل (تغییر ظرفیت ماژول‌های فیزیکی)
        if (Hardware.TotalPhysicalMemoryBytes.HasValue && other.Hardware.TotalPhysicalMemoryBytes.HasValue)
        {
            long diffBytes = Math.Abs(Hardware.TotalPhysicalMemoryBytes.Value - other.Hardware.TotalPhysicalMemoryBytes.Value);
            // تفاوت بیش از 512 مگابایت به معنای تغییر اسلات فیزیکی رم است
            if (diffBytes > 512L * 1024 * 1024)
            {
                diffs.Add(new FingerprintDifference
                {
                    Category = "Hardware.RAM",
                    ParameterName = "TotalPhysicalMemoryBytes",
                    BaselineValue = $"{other.Hardware.TotalPhysicalMemoryBytes.Value / (1024 * 1024)} MB",
                    CurrentValue = $"{Hardware.TotalPhysicalMemoryBytes.Value / (1024 * 1024)} MB",
                    Severity = InvalidationSeverity.InvalidatingChange,
                    Description = "ظرفیت حافظه رم فیزیکی سیستم تغییر کرده است."
                });
            }
        }

        // 5. مقایسه ران‌تایم و تنظیمات Execution Provider
        if (!string.Equals(Runtime.OnnxRuntimeVersion, other.Runtime.OnnxRuntimeVersion, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Runtime.ONNX",
                ParameterName = "OnnxRuntimeVersion",
                BaselineValue = other.Runtime.OnnxRuntimeVersion,
                CurrentValue = Runtime.OnnxRuntimeVersion,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "نسخه کتابخانه ONNX Runtime تغییر کرده است."
            });
        }

        if (!string.Equals(Runtime.SelectedExecutionProvider, other.Runtime.SelectedExecutionProvider, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Runtime.EP",
                ParameterName = "SelectedExecutionProvider",
                BaselineValue = other.Runtime.SelectedExecutionProvider,
                CurrentValue = Runtime.SelectedExecutionProvider,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "پرووایدر اجرایی فعال تغییر کرده است (DirectML در برابر CPU)."
            });
        }

        if (Runtime.EnableMemoryPattern != other.Runtime.EnableMemoryPattern ||
            Runtime.PerSessionLockingEnforced != other.Runtime.PerSessionLockingEnforced)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Runtime.Config",
                ParameterName = "SessionGateOrMemoryPattern",
                BaselineValue = $"Pattern={other.Runtime.EnableMemoryPattern},Lock={other.Runtime.PerSessionLockingEnforced}",
                CurrentValue = $"Pattern={Runtime.EnableMemoryPattern},Lock={Runtime.PerSessionLockingEnforced}",
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "تنظیمات همزمانی سشن یا الگوی حافظه تغییر یافته است."
            });
        }

        // 6. مقایسه دیکودر و شتاب‌دهنده دیکود
        if (!string.Equals(Decoder.LibVlcVersion, other.Decoder.LibVlcVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Decoder.HardwareDecodingMode, other.Decoder.HardwareDecodingMode, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Decoder",
                ParameterName = "LibVlcOrAcceleration",
                BaselineValue = $"{other.Decoder.LibVlcVersion} ({other.Decoder.HardwareDecodingMode})",
                CurrentValue = $"{Decoder.LibVlcVersion} ({Decoder.HardwareDecodingMode})",
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "نسخه LibVLC یا حالت شتاب‌سخت‌افزاری دیکودر تغییر کرده است."
            });
        }

        // 7. مقایسه هش مدل‌های هوش مصنوعی
        var currentModelMap = Models.ToDictionary(m => m.ModelKey, StringComparer.Ordinal);
        var otherModelMap = other.Models.ToDictionary(m => m.ModelKey, StringComparer.Ordinal);

        foreach (var key in currentModelMap.Keys.Union(otherModelMap.Keys))
        {
            currentModelMap.TryGetValue(key, out var currentModel);
            otherModelMap.TryGetValue(key, out var otherModel);

            if (currentModel == null || otherModel == null)
            {
                diffs.Add(new FingerprintDifference
                {
                    Category = "Models",
                    ParameterName = key,
                    BaselineValue = otherModel?.Sha256Hash ?? "ناموجود",
                    CurrentValue = currentModel?.Sha256Hash ?? "ناموجود",
                    Severity = InvalidationSeverity.InvalidatingChange,
                    Description = $"مدل هوش مصنوعی {key} افزوده یا حذف شده است."
                });
            }
            else if (!string.Equals(currentModel.Sha256Hash, otherModel.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add(new FingerprintDifference
                {
                    Category = "Models",
                    ParameterName = key,
                    BaselineValue = otherModel.Sha256Hash,
                    CurrentValue = currentModel.Sha256Hash,
                    Severity = InvalidationSeverity.InvalidatingChange,
                    Description = $"هش باینری مدل {key} تغییر کرده است (وزن‌ها یا ساختار گراف بهینه‌سازی شده)."
                });
            }
        }

        // 8. مقایسه نوع منبع استریم (Local File vs Live RTSP) و ابعاد تصویر
        if (StreamProfile.SourceType != other.StreamProfile.SourceType)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Stream.Source",
                ParameterName = "SourceType",
                BaselineValue = other.StreamProfile.SourceType.ToString(),
                CurrentValue = StreamProfile.SourceType.ToString(),
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "نوع منبع استریم تفاوت دارد (ظرفیت بنچمارک فایل محلی هرگز نمی‌تواند مستقیماً به استریم شبکه RTSP اعمال شود)."
            });
        }

        if (StreamProfile.Width != other.StreamProfile.Width || StreamProfile.Height != other.StreamProfile.Height)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Stream.Resolution",
                ParameterName = "Resolution",
                BaselineValue = other.StreamProfile.ResolutionLabel,
                CurrentValue = StreamProfile.ResolutionLabel,
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "رزولوشن استریم ویدیویی ورودی دوربین تغییر کرده است."
            });
        }

        // 9. مقایسه پارامترهای سمپلینگ و پیش‌پردازش
        if (Math.Abs(SamplingConfig.BaselineSamplingFps - other.SamplingConfig.BaselineSamplingFps) > 0.01 ||
            Math.Abs(SamplingConfig.MotionTriggeredBoostFps - other.SamplingConfig.MotionTriggeredBoostFps) > 0.01 ||
            SamplingConfig.AdaptiveSamplingEnabled != other.SamplingConfig.AdaptiveSamplingEnabled)
        {
            diffs.Add(new FingerprintDifference
            {
                Category = "Sampling",
                ParameterName = "SamplingFps",
                BaselineValue = $"{other.SamplingConfig.BaselineSamplingFps} / {other.SamplingConfig.MotionTriggeredBoostFps} FPS",
                CurrentValue = $"{SamplingConfig.BaselineSamplingFps} / {SamplingConfig.MotionTriggeredBoostFps} FPS",
                Severity = InvalidationSeverity.InvalidatingChange,
                Description = "نرخ نمونه‌برداری پایه یا جهش یافته هوش مصنوعی تغییر یافته است."
            });
        }

        return new FingerprintComparisonResult { Differences = diffs };
    }

    /// <summary>
    /// سازنده سریع اثرانگشت از روی نمونه سخت‌افزاری و وضعیت ران‌تایم موجود
    /// </summary>
    public static CalibrationFingerprint CreateFromSnapshot(
        HardwareSnapshot hardware,
        ExecutionProviderProfile epProfile,
        CalibrationStreamSourceType sourceType = CalibrationStreamSourceType.LiveRtspStream,
        int streamWidth = 1920,
        int streamHeight = 1080)
    {
        var primaryGpu = hardware.PrimaryGpu;

        var hwComponent = new HardwareFingerprintComponent
        {
            OsProductName = hardware.Os.ProductName,
            OsDisplayVersion = hardware.Os.DisplayVersion,
            OsBuildNumber = hardware.Os.BuildNumber,
            OsUbr = hardware.Os.UpdateBuildRevision,
            Is64BitOperatingSystem = hardware.Os.Is64BitOperatingSystem,
            OsStatus = hardware.Os.Status,

            CpuModelName = hardware.Cpu.ModelName,
            CpuLogicalCores = hardware.Cpu.LogicalCores,
            CpuPhysicalCores = hardware.Cpu.PhysicalCores,
            CpuArchitecture = hardware.Cpu.Architecture,
            HasAvx2Support = hardware.Cpu.HasAvx2Support,
            HasFmaSupport = hardware.Cpu.HasFmaSupport,
            CpuStatus = hardware.Cpu.Status,

            GpuName = primaryGpu?.Name ?? "نامشخص",
            GpuVendor = primaryGpu?.Vendor ?? "Unknown",
            GpuDeviceId = primaryGpu?.DeviceId,
            GpuDriverVersion = primaryGpu?.DriverVersion,
            DedicatedVideoMemoryBytes = primaryGpu?.DedicatedVideoMemoryBytes,
            SharedSystemMemoryBytes = primaryGpu?.SharedSystemMemoryBytes,
            IsSoftwareRenderer = primaryGpu?.IsSoftwareRenderer ?? false,
            GpuStatus = primaryGpu?.Status ?? HardwareMetricStatus.Unknown,

            TotalPhysicalMemoryBytes = hardware.Memory.TotalPhysicalBytes,
            MemoryStatus = hardware.Memory.Status
        };

        var rtComponent = new RuntimeFingerprintComponent
        {
            DotNetVersion = Environment.Version.ToString(),
            OnnxRuntimeVersion = string.IsNullOrWhiteSpace(epProfile.OnnxRuntimeVersion) ? "1.20.1" : epProfile.OnnxRuntimeVersion,
            SelectedExecutionProvider = epProfile.RecommendedInitialProvider == "DML" ? "DirectML" : "CPU",
            DirectMlDeviceId = 0,
            ExecutionMode = "ORT_SEQUENTIAL",
            EnableMemoryPattern = false,
            PerSessionLockingEnforced = true,
            Status = HardwareMetricStatus.Verified
        };

        var decComponent = new DecoderFingerprintComponent
        {
            LibVlcVersion = "3.0.21",
            LibVlcSharpVersion = "3.9.0",
            HardwareDecodingMode = "d3d11va",
            PreferredPixelFormat = "RV32",
            Status = HardwareMetricStatus.Verified
        };

        var streamComponent = new StreamFingerprintComponent
        {
            Width = streamWidth,
            Height = streamHeight,
            Encoding = "H.264",
            NominalFps = 25,
            SourceType = sourceType,
            Status = HardwareMetricStatus.Verified
        };

        var samplingComponent = new SamplingFingerprintComponent
        {
            BaselineSamplingFps = 2.0,
            MotionTriggeredBoostFps = 6.0,
            AdaptiveSamplingEnabled = true,
            PreprocessingColorFormat = "RGB_PLANAR_NCHW",
            NormalizationRange = "[0.0, 1.0]",
            Status = HardwareMetricStatus.Verified
        };

        return new CalibrationFingerprint
        {
            Hardware = hwComponent,
            Runtime = rtComponent,
            Decoder = decComponent,
            StreamProfile = streamComponent,
            SamplingConfig = samplingComponent,
            Models = new List<ModelHashEntry>()
        };
    }
}
