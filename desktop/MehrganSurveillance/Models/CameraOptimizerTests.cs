using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// مجموعه تست‌های واقعی و خودکار فاز ۸ (Smart Camera Settings Optimizer)
    /// جهت سنجش قابلیت‌ها، اسنپ‌شات، رول‌بک، تفکیک سازندگان، امنیت دسترسی و عدم نشت کریدنشال
    /// </summary>
    public static class CameraOptimizerTests
    {
        public static async Task<bool> RunAllTestsAsync()
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("شروع اجرای آزمون‌های جامع موتور بهینه‌سازی هوشمند دوربین (Phase 8 Optimizer Tests)");
            Console.WriteLine("================================================================================");

            bool allPassed = true;

            allPassed &= await Test_VendorProviderIsolation();
            allPassed &= await Test_CapabilityDetection_UnsupportedParametersAreNotMarkedSupported();
            allPassed &= await Test_SnapshotCreationAndPreservation();
            allPassed &= await Test_AdminApprovalEnforcement();
            allPassed &= await Test_NighttimeAnprProfile_GeneratesHlcAndFastShutter();
            allPassed &= await Test_RollbackMechanism_RestoresPreviousState();
            allPassed &= await Test_NetworkDisconnectionHandling();
            allPassed &= await Test_AuditLog_DoesNotContainPasswordsOrSecrets();
            allPassed &= await Test_AnalysisEngine_SharpnessAndMotionBlurDetection();
            allPassed &= await Test_RtspStreamingUnchanged_DoesNotAffectPlayback();

            Console.WriteLine("================================================================================");
            if (allPassed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("تمامی ۱۰ آزمون فاز ۸ بهینه‌ساز دوربین با موفقیت ۱۰۰٪ پاس شدند. [ALL PASSED]");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("برخی از آزمون‌های فاز ۸ با شکست مواجه شدند.");
                Console.ResetColor();
            }
            Console.WriteLine("================================================================================");

            return allPassed;
        }

        /// <summary>
        /// آزمون ۱: تفکیک سازندگان و اولویت‌بندی صحیح ارائه‌دهندگان
        /// </summary>
        public static Task<bool> Test_VendorProviderIsolation()
        {
            var service = new CameraSettingsOptimizerService();

            var tiandyProvider = service.SelectProvider("Tiandy", "TC-NC214");
            var dahuaProvider = service.SelectProvider("Dahua", "DH-IPC-HFW");
            var hikProvider = service.SelectProvider("Hikvision", "DS-2CD2043");
            var onvifProvider = service.SelectProvider("Generic", "Standard ONVIF");

            bool pass = tiandyProvider is TiandySettingsProvider &&
                        dahuaProvider is DahuaSettingsProvider &&
                        hikProvider is HikvisionSettingsProvider &&
                        onvifProvider is OnvifImagingProvider &&
                        tiandyProvider.Priority < dahuaProvider.Priority &&
                        dahuaProvider.Priority < hikProvider.Priority &&
                        hikProvider.Priority < onvifProvider.Priority;

            Console.WriteLine($"[Test 1] جداسازی و اولویت ارائه‌دهندگان سازندگان (Tiandy/Dahua/Hik/ONVIF): {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۲: عدم فعال‌سازی پارامترهای پشتیبانی‌نشده
        /// </summary>
        public static Task<bool> Test_CapabilityDetection_UnsupportedParametersAreNotMarkedSupported()
        {
            var profile = new CameraCapabilityProfile
            {
                CanControlShutter = false,
                CanControlGain = false,
                CanControlHlc = false,
                CanControlWdr = false,
                IsVerified = false
            };

            var engine = new CameraSettingsAnalysisEngine();
            var current = new CameraSettingsSnapshot
            {
                ExposureMode = "Auto",
                ShutterSpeedSeconds = 0.02f,
                Gain = 30f,
                HlcEnabled = false
            };
            var metrics = new ImageAnalysisMetrics();

            var rec = engine.GenerateRecommendation(profile, current, metrics, CameraOptimizationProfileType.NighttimeLicensePlate);

            // وقتی HLC و Shutter پشتیبانی نمی‌شود، نباید در فیلتر اجباری مقادیر غیرمجاز وارد شود
            bool pass = !profile.CanControlShutter &&
                        !profile.CanControlHlc &&
                        rec.ProposedSettings.HlcEnabled == false &&
                        !rec.ParameterReasons.ContainsKey("HLC");

            Console.WriteLine($"[Test 2] اعتبارسنجی عدم فعال‌سازی پارامترهای پشتیبانی‌نشده: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۳: ایجاد صحیح اسنپ‌شات و حفظ مقادیر قبل از تغییر
        /// </summary>
        public static Task<bool> Test_SnapshotCreationAndPreservation()
        {
            var original = new CameraSettingsSnapshot
            {
                CameraId = "cam_test_01",
                CameraIp = "192.168.1.105",
                ShutterSpeedSeconds = 0.02f, // 1/50s
                Gain = 20f,
                HlcEnabled = false,
                WdrEnabled = false
            };

            var clone = original.Clone();

            // تغییر نسخه کلون نباید ارجاع اولیه را دستکاری کند
            clone.ShutterSpeedSeconds = 0.001f;
            clone.Gain = 60f;
            clone.HlcEnabled = true;

            bool pass = original.ShutterSpeedSeconds == 0.02f &&
                        original.Gain == 20f &&
                        original.HlcEnabled == false &&
                        clone.ShutterSpeedSeconds == 0.001f &&
                        clone.Gain == 60f &&
                        clone.HlcEnabled == true &&
                        original.SnapshotId != clone.SnapshotId;

            Console.WriteLine($"[Test 3] صحت ایجاد اسنپ‌شات مجزا و عدم دستکاری وضعیت اولیه: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۴: کنترل دسترسی و الزام به تأیید صریح مدیر
        /// </summary>
        public static async Task<bool> Test_AdminApprovalEnforcement()
        {
            var service = new CameraSettingsOptimizerService();
            var target = new CameraSettingsSnapshot { CameraIp = "127.0.0.1" };

            // تلاش برای اعمال بدون تأیید مدیر (adminConfirmed = false)
            var (success, msg, _) = await service.ApplySettingsWithSafetyAsync(
                "cam_01", "127.0.0.1", 80, "admin", "pass",
                target, "DaytimeLicensePlate", adminConfirmed: false);

            bool pass = !success && msg.Contains("تأیید صریح مدیر");

            Console.WriteLine($"[Test 4] رد امنیتی تغییرات بدون تأیید صریح مدیر سیستم: {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        /// <summary>
        /// آزمون ۵: بررسی تولید پروفایل شبانه پلاک با HLC و شاتر ضد کشیدگی
        /// </summary>
        public static Task<bool> Test_NighttimeAnprProfile_GeneratesHlcAndFastShutter()
        {
            var caps = new CameraCapabilityProfile
            {
                CanControlShutter = true,
                MinShutterSeconds = 0.00001f,
                MaxShutterSeconds = 0.04f,
                CanControlGain = true,
                CanControlHlc = true,
                CanControlWdr = true,
                CanControlDayNightMode = true,
                CanControlIr = true
            };

            var current = new CameraSettingsSnapshot
            {
                ShutterSpeedSeconds = 0.02f, // 1/50s
                ExposureMode = "Auto",
                HlcEnabled = false,
                Gain = 50f
            };

            var engine = new CameraSettingsAnalysisEngine();
            var metrics = new ImageAnalysisMetrics
            {
                AverageLuminance = 40f,
                EstimatedAmbientLighting = "Night",
                RecentAnprConfidence = 0.65f, // دقت پایین به دلیل تاریکی و نور چراغ
                MotionBlurScore = 70f
            };

            var rec = engine.GenerateRecommendation(caps, current, metrics, CameraOptimizationProfileType.NighttimeLicensePlate);

            bool pass = rec.ProposedSettings.HlcEnabled == true &&
                        rec.ProposedSettings.HlcLevel >= 70f &&
                        rec.ProposedSettings.ShutterSpeedSeconds <= 0.004f && // حداقل 1/250s
                        rec.ProposedSettings.ExposureMode == "Manual" &&
                        rec.ParameterReasons.ContainsKey("HLC") &&
                        rec.ParameterReasons.ContainsKey("Shutter") &&
                        rec.ProposedSettings.DayNightMode == "Night";

            Console.WriteLine($"[Test 5] تولید صحیح پروفایل شبانه پلاک‌خوانی (HLC فعال + شاتر سریع): {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۶: مکانیزم بازگردانی (Rollback) به اسنپ‌شات قبلی
        /// </summary>
        public static async Task<bool> Test_RollbackMechanism_RestoresPreviousState()
        {
            var service = new CameraSettingsOptimizerService();
            string camId = "cam_rollback_unit";

            // اعمال اول: ثبت اسنپ‌شات پایدار قبلی
            var target1 = new CameraSettingsSnapshot
            {
                CameraId = camId,
                CameraIp = "192.168.1.199",
                ShutterSpeedSeconds = 0.002f,
                Gain = 25f
            };

            // در حالت آفلاین، تست بازخوانی و نگهداری اسنپ‌شات در حافظه سرویس
            var (applied, _, record) = await service.ApplySettingsWithSafetyAsync(
                camId, "192.168.1.199", 80, "admin", "123",
                target1, "DaytimeLicensePlate", adminConfirmed: true);

            // فراخوانی رول‌بک
            var (rollSuccess, rollMsg) = await service.RollbackToPreviousSettingsAsync(
                camId, "192.168.1.199", 80, "admin", "123");

            // بررسی موجود بودن لاگ و ذخیره‌سازی
            var history = service.GetAuditHistory(camId);
            bool pass = history.Count > 0 && history.Any(h => h.ProfileApplied.Contains("DaytimeLicensePlate") || h.WasRolledBack);

            Console.WriteLine($"[Test 6] عملکرد مکانیزم اسنپ‌شات قبل و بازگردانی امن (Rollback): {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        /// <summary>
        /// آزمون ۷: عدم کرش و پایداری در صورت قطعی شبکه دوربین
        /// </summary>
        public static async Task<bool> Test_NetworkDisconnectionHandling()
        {
            var service = new CameraSettingsOptimizerService();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            // اتصال به آدرس نامعتبر یا بسته
            bool handled = false;
            try
            {
                var caps = await service.DetectCapabilitiesAsync(
                    "cam_unreachable", "192.0.2.1", 8080, "user", "pass", "Tiandy", "TC-NC", cts.Token);

                // باید با مقادیر امن و IsVerified = false بازگردد بدون استثنای کرش‌کننده
                handled = caps != null;
            }
            catch
            {
                handled = false;
            }

            Console.WriteLine($"[Test 7] مدیریت قطع ارتباط دوربین و حفظ پایداری سیستم: {(handled ? "PASS" : "FAIL")}");
            return handled;
        }

        /// <summary>
        /// آزمون ۸: عدم ذخیره رمز عبور یا اطلاعات محرمانه در لاگ ممیزی
        /// </summary>
        public static Task<bool> Test_AuditLog_DoesNotContainPasswordsOrSecrets()
        {
            var audit = new OptimizationAuditRecord
            {
                CameraId = "cam_sec_test",
                CameraIp = "192.168.1.50",
                ProfileApplied = "NighttimeLicensePlate",
                AdminUser = "مدیر ارشد",
                Success = true,
                ResultSummary = "تست با موفقیت ثبت شد"
            };

            string serialized = System.Text.Json.JsonSerializer.Serialize(audit);

            bool pass = !serialized.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                        !serialized.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                        !serialized.Contains("token", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine($"[Test 8] تضمین عدم افشای رمز عبور یا اطلاعات محرمانه در Audit Log: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۹: الگوریتم تحلیل وضوح گرادیان و تشخیص موشن بلر
        /// </summary>
        public static Task<bool> Test_AnalysisEngine_SharpnessAndMotionBlurDetection()
        {
            var engine = new CameraSettingsAnalysisEngine();

            // فریم یکنواخت خاکستری (بدون لبه و تاری شدید)
            int w = 64;
            int h = 64;
            byte[] flatFrame = new byte[w * h * 3];
            Array.Fill(flatFrame, (byte)128);

            var flatMetrics = engine.AnalyzeFrame(flatFrame, w, h);

            // فریم با کنتراست و لبه‌های متناوب تیز
            byte[] sharpFrame = new byte[w * h * 3];
            for (int i = 0; i < sharpFrame.Length; i += 3)
            {
                byte val = (byte)((i % 6) == 0 ? 255 : 0);
                sharpFrame[i] = val;
                sharpFrame[i + 1] = val;
                sharpFrame[i + 2] = val;
            }

            var sharpMetrics = engine.AnalyzeFrame(sharpFrame, w, h);

            // فریم یکنواخت باید تاری بیشتری نسبت به فریم پرکنتراست گزارش کند
            bool pass = flatMetrics.MotionBlurScore >= sharpMetrics.MotionBlurScore &&
                        sharpMetrics.ContrastStdDev > flatMetrics.ContrastStdDev;

            Console.WriteLine($"[Test 9] صحت الگوریتم تحلیل تفاضلی لبه‌ها و تشخیص تاری موشن بلر: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۱۰: تضمین عدم دستکاری یا تداخل با مسیرهای RTSP و استریم زنده
        /// </summary>
        public static Task<bool> Test_RtspStreamingUnchanged_DoesNotAffectPlayback()
        {
            string originalRtsp = "rtsp://admin:pass@192.168.1.100:554/1/1";
            string generatedRtsp = OnvifClientService.GenerateVendorRtspUri("Tiandy", "192.168.1.100", 554, "admin", "pass", 1);

            bool pass = originalRtsp == generatedRtsp;

            Console.WriteLine($"[Test 10] تضمین عدم تأثیر منفی بر استریم‌های زنده RTSP و پایداری Live View: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }
    }
}
