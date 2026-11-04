using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services
{
    /// <summary>
    /// کاوشگر امن، کاملاً خواندنی (Read-Only) و سطح پایین سخت‌افزار سیستم.
    /// وظیفه: استخراج مشخصات دقیق CPU، RAM، GPU و نگارش ویندوز بدون دستکاری یا تغییر در تنظیمات،
    /// و تفکیک قطعی مقادیر اثبات‌شده (Verified) از مقادیر نامشخص (Unknown/null).
    /// </summary>
    public class HardwareCapabilityProbe
    {
        #region Native Win32 Memory Structures

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        #endregion

        /// <summary>
        /// اجرای کاوش کامل و غیرهمگام سخت‌افزار با قابلیت لغو
        /// </summary>
        public async Task<HardwareSnapshot> ProbeHardwareAsync(CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var snapshot = new HardwareSnapshot();

                try
                {
                    ct.ThrowIfCancellationRequested();

                    // ۱. کاوش مشخصات سیستم‌عامل
                    snapshot.Os = ProbeOperatingSystem(snapshot.InspectionWarnings);

                    ct.ThrowIfCancellationRequested();

                    // ۲. کاوش مشخصات پردازنده مرکزی (CPU)
                    snapshot.Cpu = ProbeProcessor(snapshot.InspectionWarnings);

                    ct.ThrowIfCancellationRequested();

                    // ۳. کاوش دقیق حافظه رم (RAM)
                    snapshot.Memory = ProbeSystemMemory(snapshot.InspectionWarnings);

                    ct.ThrowIfCancellationRequested();

                    // ۴. کاوش آداپتورهای گرافیکی و حافظه VRAM (GPU)
                    snapshot.GpuAdapters = ProbeGpuAdapters(snapshot.InspectionWarnings);

                    // تعیین پردازنده گرافیکی اصلی (اولویت با کارت‌های مجزای NVIDIA یا AMD نسبت به گرافیک مجتمع یا نرم‌افزاری)
                    snapshot.PrimaryGpu = SelectPrimaryGpu(snapshot.GpuAdapters);
                }
                catch (OperationCanceledException)
                {
                    snapshot.IsSuccess = false;
                    snapshot.InspectionWarnings.Add("عملیات کاوش سخت‌افزار توسط کاربر یا سیستم لغو شد.");
                }
                catch (Exception ex)
                {
                    snapshot.IsSuccess = false;
                    snapshot.InspectionWarnings.Add($"خطای پیش‌بینی‌نشده در کاوش سخت‌افزار: {ex.Message}");
                }

                return snapshot;
            }, ct);
        }

        #region OS Probing

        private OsProfile ProbeOperatingSystem(List<string> warnings)
        {
            var os = new OsProfile
            {
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                Is64BitProcess = Environment.Is64BitProcess,
                FullOsDescription = RuntimeInformation.OSDescription,
                Status = HardwareMetricStatus.Verified
            };

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    if (key != null)
                    {
                        os.ProductName = key.GetValue("ProductName")?.ToString() ?? os.ProductName;
                        os.DisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                        os.BuildNumber = key.GetValue("CurrentBuildNumber")?.ToString() 
                                         ?? key.GetValue("CurrentBuild")?.ToString() 
                                         ?? string.Empty;

                        if (int.TryParse(key.GetValue("UBR")?.ToString(), out int ubr))
                        {
                            os.UpdateBuildRevision = ubr;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"دسترسی به رجیستری نسخه ویندوز با خطا مواجه شد: {ex.Message}");
                    os.Status = HardwareMetricStatus.Estimated;
                }
            }

            return os;
        }

        #endregion

        #region CPU Probing

        private CpuProfile ProbeProcessor(List<string> warnings)
        {
            var cpu = new CpuProfile
            {
                LogicalCores = Environment.ProcessorCount,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Status = HardwareMetricStatus.Verified
            };

            // بررسی دستورالعمل‌های برداری x86/x64 بدون اجرای کدهای ناامن
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
                RuntimeInformation.ProcessArchitecture == Architecture.X86)
            {
                try
                {
                    cpu.HasAvx2Support = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
                    cpu.HasFmaSupport = System.Runtime.Intrinsics.X86.Fma.IsSupported;
                }
                catch (Exception)
                {
                    cpu.HasAvx2Support = null;
                    cpu.HasFmaSupport = null;
                }
            }

            // استخراج نام مدل تجاری پردازنده از رجیستری بدون سربار سنگین WMI
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        string? name = key.GetValue("ProcessorNameString")?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            cpu.ModelName = CleanUpCpuString(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"عدم امکان خواندن نام پردازنده از رجیستری: {ex.Message}");
                    cpu.ModelName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "پردازنده استاندارد x64";
                    cpu.Status = HardwareMetricStatus.Estimated;
                }
            }

            return cpu;
        }

        private static string CleanUpCpuString(string raw)
        {
            // حذف فضاهای خالی متوالی
            return string.Join(" ", raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        #endregion

        #region Memory Probing

        private MemoryProfile ProbeSystemMemory(List<string> warnings)
        {
            var mem = new MemoryProfile();

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var status = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(status))
                    {
                        mem.TotalPhysicalBytes = (long)status.ullTotalPhys;
                        mem.AvailablePhysicalBytes = (long)status.ullAvailPhys;
                        mem.TotalVirtualBytes = (long)status.ullTotalVirtual;
                        mem.AvailableVirtualBytes = (long)status.ullAvailVirtual;
                        mem.MemoryLoadPercentage = status.dwMemoryLoad;
                        mem.Status = HardwareMetricStatus.Verified;
                        return mem;
                    }
                    else
                    {
                        warnings.Add($"فراخوانی Win32 GlobalMemoryStatusEx با کد خطای {Marshal.GetLastWin32Error()} ناموفق بود.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"خطا در P/Invoke حافظه: {ex.Message}");
                }
            }

            // Fallback در صورت بروز خطا در دسترسی Win32
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                if (gcInfo.TotalAvailableMemoryBytes > 0)
                {
                    mem.TotalPhysicalBytes = gcInfo.TotalAvailableMemoryBytes;
                    mem.Status = HardwareMetricStatus.Estimated;
                }
            }
            catch (Exception)
            {
                // حفظ مقادیر به صورت null جهت تفکیک از عدد صفر واقعی
            }

            return mem;
        }

        #endregion

        #region GPU Probing

        private List<GpuAdapterProfile> ProbeGpuAdapters(List<string> warnings)
        {
            var adapters = new List<GpuAdapterProfile>();

            if (!OperatingSystem.IsWindows())
            {
                return adapters;
            }

            try
            {
                // خواندن آداپتورهای نمایش ثبت‌شده در کلاس تجهیزات ویندوز
                // Class GUID استاندارد آداپتورهای گرافیکی: {4d36e968-e325-11ce-bfc1-08002be10318}
                const string displayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using var classKey = Registry.LocalMachine.OpenSubKey(displayClassPath);

                if (classKey != null)
                {
                    string[] subKeys = classKey.GetSubKeyNames();
                    int adapterIndex = 0;

                    foreach (var sub in subKeys)
                    {
                        // ساب‌کی‌های تجهیزات معمولاً به صورت 0000، 0001 و ... نام‌گذاری شده‌اند
                        if (!int.TryParse(sub, out _))
                            continue;

                        using var adapterKey = classKey.OpenSubKey(sub);
                        if (adapterKey == null)
                            continue;

                        string? driverDesc = adapterKey.GetValue("DriverDesc")?.ToString();
                        if (string.IsNullOrWhiteSpace(driverDesc))
                            continue;

                        var adapter = new GpuAdapterProfile
                        {
                            AdapterIndex = adapterIndex++,
                            Name = driverDesc.Trim(),
                            DriverVersion = adapterKey.GetValue("DriverVersion")?.ToString(),
                            DeviceId = adapterKey.GetValue("MatchingDeviceId")?.ToString() 
                                       ?? adapterKey.GetValue("MatchingDeviceID")?.ToString(),
                            Status = HardwareMetricStatus.Verified
                        };

                        // شناسایی سازنده چیپست گرافیکی
                        string? provider = adapterKey.GetValue("ProviderName")?.ToString() ?? string.Empty;
                        adapter.Vendor = DetermineVendor(adapter.Name, provider);

                        // تشخیص رندرهای مجازی/نرم‌افزاری
                        if (adapter.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                            adapter.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                            adapter.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                            adapter.Vendor == "Microsoft")
                        {
                            adapter.IsSoftwareRenderer = true;
                        }

                        // استخراج حافظه ویدیویی VRAM در صورت ثبت در رجیستری سخت‌افزار
                        // رجیستری ویندوز مقادیر ۶۴ بیتی qwMemorySize یا ۳۲ بیتی MemorySize را نگهداری می‌کند
                        object? memObj = adapterKey.GetValue("HardwareInformation.qwMemorySize") 
                                         ?? adapterKey.GetValue("HardwareInformation.MemorySize");

                        if (memObj != null)
                        {
                            if (memObj is long lVal && lVal > 0)
                            {
                                adapter.DedicatedVideoMemoryBytes = lVal;
                            }
                            else if (memObj is int iVal && iVal > 0)
                            {
                                adapter.DedicatedVideoMemoryBytes = (long)(uint)iVal;
                            }
                            else if (memObj is byte[] bVal && bVal.Length >= 4)
                            {
                                adapter.DedicatedVideoMemoryBytes = bVal.Length >= 8 
                                    ? BitConverter.ToInt64(bVal, 0) 
                                    : BitConverter.ToUInt32(bVal, 0);
                            }
                        }

                        adapters.Add(adapter);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"خطا در کاوش آداپتورهای گرافیکی از طریق رجیستری: {ex.Message}");
            }

            return adapters;
        }

        private static string DetermineVendor(string adapterName, string provider)
        {
            string combined = $"{adapterName} {provider}".ToUpperInvariant();
            if (combined.Contains("NVIDIA") || combined.Contains("GEFORCE") || combined.Contains("QUADRO") || combined.Contains("RTX"))
                return "NVIDIA";
            if (combined.Contains("AMD") || combined.Contains("RADEON") || combined.Contains("ADVANCED MICRO DEVICES"))
                return "AMD";
            if (combined.Contains("INTEL") || combined.Contains("IRIS") || combined.Contains("ARC") || combined.Contains("HD GRAPHICS"))
                return "Intel";
            if (combined.Contains("MICROSOFT"))
                return "Microsoft";

            return "Unknown";
        }

        private static GpuAdapterProfile? SelectPrimaryGpu(List<GpuAdapterProfile> adapters)
        {
            if (adapters.Count == 0)
                return null;

            // اولویت ۱: کارت‌های مجزای NVIDIA یا AMD با حافظه VRAM بالاتر
            var discrete = adapters
                .Where(a => !a.IsSoftwareRenderer && (a.Vendor == "NVIDIA" || a.Vendor == "AMD"))
                .OrderByDescending(a => a.DedicatedVideoMemoryBytes ?? 0)
                .FirstOrDefault();

            if (discrete != null)
                return discrete;

            // اولویت ۲: گرافیک‌های پیشرفته Intel (Arc / Iris Xe)
            var intel = adapters
                .Where(a => !a.IsSoftwareRenderer && a.Vendor == "Intel")
                .OrderByDescending(a => a.DedicatedVideoMemoryBytes ?? 0)
                .FirstOrDefault();

            if (intel != null)
                return intel;

            // اولویت ۳: اولین آداپتور غیرنرم‌افزاری
            var anyHardware = adapters.FirstOrDefault(a => !a.IsSoftwareRenderer);
            return anyHardware ?? adapters[0];
        }

        #endregion
    }
}
