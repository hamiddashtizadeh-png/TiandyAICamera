using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// وضعیت تأیید یا در دسترس بودن شاخص‌های سخت‌افزاری
    /// جهت تفکیک صریح مقادیر نامشخص (Unknown) از مقادیر صفر واقعی (Actual Zero)
    /// </summary>
    public enum HardwareMetricStatus
    {
        Unknown,     // مقدار به دلیل محدودیت مجوز یا عدم پاسخ سیستم‌عامل در دسترس نیست
        Estimated,   // مقدار از طریق الگوریتم‌های اکتشافی یا تخمینی برآورد شده است
        Verified     // مقدار مستقیماً از طریق فراخوانی سیستمی Win32 یا رجیستری ویندوز تأیید شده است
    }

    /// <summary>
    /// مشخصات سیستم‌عامل و ساختار نگارش ویندوز
    /// </summary>
    public class OsProfile
    {
        public string ProductName { get; set; } = "Windows";
        public string DisplayVersion { get; set; } = string.Empty; // e.g. "23H2"
        public string BuildNumber { get; set; } = string.Empty;     // e.g. "22631"
        public int? UpdateBuildRevision { get; set; }             // UBR e.g. 4387
        public bool Is64BitOperatingSystem { get; set; } = true;
        public bool Is64BitProcess { get; set; } = true;
        public string FullOsDescription { get; set; } = string.Empty;
        public HardwareMetricStatus Status { get; set; } = HardwareMetricStatus.Unknown;

        public string GetFormattedVersion()
        {
            if (!string.IsNullOrWhiteSpace(DisplayVersion) && !string.IsNullOrWhiteSpace(BuildNumber))
            {
                return UpdateBuildRevision.HasValue
                    ? $"{ProductName} ({DisplayVersion} - Build {BuildNumber}.{UpdateBuildRevision.Value})"
                    : $"{ProductName} ({DisplayVersion} - Build {BuildNumber})";
            }
            return FullOsDescription;
        }
    }

    /// <summary>
    /// مشخصات پردازنده مرکزی (CPU)
    /// </summary>
    public class CpuProfile
    {
        public string ModelName { get; set; } = "پردازنده نامشخص";
        public int LogicalCores { get; set; }
        public int? PhysicalCores { get; set; } // اگر قابل استخراج نباشد null است (نه صفر)
        public string Architecture { get; set; } = "x64";
        public bool? HasAvx2Support { get; set; } // null به معنای عدم امکان سنجش دستورالعمل
        public bool? HasFmaSupport { get; set; }
        public HardwareMetricStatus Status { get; set; } = HardwareMetricStatus.Unknown;

        public string GetSummary()
        {
            string avx = HasAvx2Support switch
            {
                true => "AVX2 فعال",
                false => "فاقد AVX2",
                null => "وضعیت AVX2 نامشخص"
            };

            string cores = PhysicalCores.HasValue
                ? $"{PhysicalCores.Value} هسته فیزیکی / {LogicalCores} رشته (Threads)"
                : $"{LogicalCores} هسته منطقی (Threads)";

            return $"{ModelName} | {cores} | {avx}";
        }
    }

    /// <summary>
    /// مشخصات حافظه موقت سیستم (RAM)
    /// </summary>
    public class MemoryProfile
    {
        public long? TotalPhysicalBytes { get; set; }     // null در صورت عدم دستیابی
        public long? AvailablePhysicalBytes { get; set; } // null در صورت عدم دستیابی
        public long? TotalVirtualBytes { get; set; }
        public long? AvailableVirtualBytes { get; set; }
        public uint? MemoryLoadPercentage { get; set; }
        public HardwareMetricStatus Status { get; set; } = HardwareMetricStatus.Unknown;

        public double? TotalPhysicalGb => TotalPhysicalBytes.HasValue
            ? Math.Round(TotalPhysicalBytes.Value / (1024.0 * 1024.0 * 1024.0), 2)
            : null;

        public double? AvailablePhysicalGb => AvailablePhysicalBytes.HasValue
            ? Math.Round(AvailablePhysicalBytes.Value / (1024.0 * 1024.0 * 1024.0), 2)
            : null;

        public string GetSummary()
        {
            if (TotalPhysicalGb.HasValue && AvailablePhysicalGb.HasValue)
            {
                return $"{TotalPhysicalGb.Value:0.0} GB کل ({AvailablePhysicalGb.Value:0.0} GB آزاد)";
            }
            if (TotalPhysicalGb.HasValue)
            {
                return $"{TotalPhysicalGb.Value:0.0} GB کل";
            }
            return "حافظه رم نامشخص";
        }
    }

    /// <summary>
    /// مشخصات کارت گرافیک یا آداپتور تصویری (GPU Adapter)
    /// </summary>
    public class GpuAdapterProfile
    {
        public int AdapterIndex { get; set; }
        public string Name { get; set; } = "آداپتور ویدیویی نامشخص";
        public string Vendor { get; set; } = "Unknown"; // NVIDIA, AMD, Intel, Microsoft, Unknown
        public long? DedicatedVideoMemoryBytes { get; set; } // null در صورت عدم پشتیبانی یا نامشخص بودن
        public long? DedicatedSystemMemoryBytes { get; set; }
        public long? SharedSystemMemoryBytes { get; set; }
        public string? DriverVersion { get; set; }           // null اگر نسخه درایور خوانده نشود
        public string? DeviceId { get; set; }                // PCI\VEN_xxxx&DEV_xxxx
        public bool IsSoftwareRenderer { get; set; }
        public HardwareMetricStatus Status { get; set; } = HardwareMetricStatus.Unknown;

        public double? DedicatedVramMb => DedicatedVideoMemoryBytes.HasValue
            ? Math.Round(DedicatedVideoMemoryBytes.Value / (1024.0 * 1024.0), 1)
            : null;

        public string GetSummary()
        {
            string vramText = DedicatedVramMb.HasValue
                ? (DedicatedVramMb.Value > 0 ? $"{DedicatedVramMb.Value:0} MB VRAM" : "حافظه اشتراکی / Shared")
                : "VRAM نامشخص";

            string driver = !string.IsNullOrWhiteSpace(DriverVersion) ? $"دراRow: {DriverVersion}" : "نسخه درایور نامشخص";
            string type = IsSoftwareRenderer ? " [رندر نرم‌افزاری]" : string.Empty;

            return $"{Name} ({Vendor}) | {vramText} | {driver}{type}";
        }
    }

    /// <summary>
    /// تصویر کامل و لحظه‌ای از کاوش سخت‌افزاری سیستم جاری (Hardware Snapshot)
    /// بدون تغییر در تنظیمات، صرفاً با بازرسی خواندنی (Read-Only)
    /// </summary>
    public class HardwareSnapshot
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public OsProfile Os { get; set; } = new();
        public CpuProfile Cpu { get; set; } = new();
        public MemoryProfile Memory { get; set; } = new();
        public List<GpuAdapterProfile> GpuAdapters { get; set; } = new();
        public GpuAdapterProfile? PrimaryGpu { get; set; }
        public List<string> InspectionWarnings { get; set; } = new();
        public bool IsSuccess { get; set; } = true;

        public string GeneratePersianDiagnosticReport()
        {
            var lines = new List<string>
            {
                "=== گزارش کاوش وضعیت سخت‌افزار سامانه نظارتی مهرگان ===",
                $"زمان کاوش: {TimestampUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
                $"سیستم‌عامل: {Os.GetFormattedVersion()}",
                $"پردازنده مرکزی (CPU): {Cpu.GetSummary()}",
                $"حافظه رم (RAM): {Memory.GetSummary()}"
            };

            if (GpuAdapters.Count == 0)
            {
                lines.Add("کارت گرافیک (GPU): هیچ آداپتور فیزیکی یا مجازی کشف نشد یا دسترسی ناموفق بود.");
            }
            else
            {
                lines.Add($"کارت گرافیک (GPU) - تعداد کشف‌شده: {GpuAdapters.Count}");
                for (int i = 0; i < GpuAdapters.Count; i++)
                {
                    var gpu = GpuAdapters[i];
                    string isPrimary = (gpu == PrimaryGpu) ? " (اصلی)" : string.Empty;
                    lines.Add($"  [{i + 1}] {gpu.GetSummary()}{isPrimary}");
                }
            }

            if (InspectionWarnings.Count > 0)
            {
                lines.Add("هشدارهای کاوش:");
                foreach (var warn in InspectionWarnings)
                {
                    lines.Add($"  ⚠️ {warn}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
