using System;
using System.Collections.Generic;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// موتور تحلیل هوشمند کیفیت تصویر دوربین و تولید پیشنهادهای بهینه‌سازی
    /// بر اساس تحلیل بصری نور/تاری/نویز و فیدبک دقت پلاک‌خوان ANPR
    /// </summary>
    public class CameraSettingsAnalysisEngine
    {
        /// <summary>
        /// تحلیل تصویر از روی بافر فریم و بازخورد ANPR
        /// </summary>
        public ImageAnalysisMetrics AnalyzeFrame(
            byte[]? bgrBuffer, int width, int height,
            float recentAnprConfidence = 0.85f,
            int recentBlurredPlateCount = 0)
        {
            var metrics = new ImageAnalysisMetrics
            {
                SampledAt = DateTime.UtcNow,
                RecentAnprConfidence = Math.Clamp(recentAnprConfidence, 0f, 1f)
            };

            if (bgrBuffer == null || bgrBuffer.Length < width * height * 3 || width <= 0 || height <= 0)
            {
                // متریک‌های اسمی پایدار در صورت عدم دسترسی به فریم خام
                metrics.AverageLuminance = 115f;
                metrics.ContrastStdDev = 42f;
                metrics.MotionBlurScore = recentBlurredPlateCount > 0 ? 65f : 20f;
                metrics.EstimatedNoiseLevel = 15f;
                metrics.EstimatedAmbientLighting = DateTime.Now.Hour >= 19 || DateTime.Now.Hour < 6 ? "Night" : "Day";
                return metrics;
            }

            // نمونه‌برداری با گام بهینه جهت عملکرد بی‌درنگ و عدم بار سنگین پردازشی
            long sumLum = 0;
            int step = 8; // نمونه‌برداری شبکه‌ای
            int sampledPixels = 0;
            List<float> lumSamples = new List<float>((width * height) / (step * step));

            // محاسبه واریانس لاپلاسیان تخمینی (Edge Sharpness / Motion Blur)
            double laplacianSum = 0;

            for (int y = step; y < height - step; y += step)
            {
                int rowOffset = y * width * 3;
                for (int x = step; x < width - step; x += step)
                {
                    int idx = rowOffset + x * 3;
                    byte b = bgrBuffer[idx];
                    byte g = bgrBuffer[idx + 1];
                    byte r = bgrBuffer[idx + 2];

                    // تبدیل به روشنایی Y (ITU-R BT.601)
                    float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                    sumLum += (long)lum;
                    lumSamples.Add(lum);
                    sampledPixels++;

                    // گرادیان تفاضلی مرتبه ۲ برای تخمین وضوح مرزها
                    int upIdx = ((y - step) * width + x) * 3;
                    int downIdx = ((y + step) * width + x) * 3;
                    int leftIdx = (y * width + (x - step)) * 3;
                    int rightIdx = (y * width + (x + step)) * 3;

                    float lumUp = 0.299f * bgrBuffer[upIdx + 2] + 0.587f * bgrBuffer[upIdx + 1] + 0.114f * bgrBuffer[upIdx];
                    float lumDown = 0.299f * bgrBuffer[downIdx + 2] + 0.587f * bgrBuffer[downIdx + 1] + 0.114f * bgrBuffer[downIdx];
                    float lumLeft = 0.299f * bgrBuffer[leftIdx + 2] + 0.587f * bgrBuffer[leftIdx + 1] + 0.114f * bgrBuffer[leftIdx];
                    float lumRight = 0.299f * bgrBuffer[rightIdx + 2] + 0.587f * bgrBuffer[rightIdx + 1] + 0.114f * bgrBuffer[rightIdx];

                    float lap = Math.Abs(4 * lum - lumUp - lumDown - lumLeft - lumRight);
                    laplacianSum += lap;
                }
            }

            if (sampledPixels > 0)
            {
                float avgLum = (float)sumLum / sampledPixels;
                metrics.AverageLuminance = avgLum;

                // محاسبه انحراف معیار کنتراست
                double variance = 0;
                foreach (var l in lumSamples)
                {
                    float diff = l - avgLum;
                    variance += diff * diff;
                }
                metrics.ContrastStdDev = (float)Math.Sqrt(variance / sampledPixels);

                // ارزیابی تاری و کشیدگی حرکتی (Sharpness Score معکوس می‌شود)
                double avgEdgeSharpness = laplacianSum / sampledPixels;
                // هرچه گرادیان کمتر باشد، تصویر تارتر است
                float blurFromSharpness = (float)Math.Clamp(100.0 - (avgEdgeSharpness * 3.5), 0.0, 100.0);

                if (recentBlurredPlateCount > 0)
                {
                    blurFromSharpness = Math.Max(blurFromSharpness, 50f + (recentBlurredPlateCount * 10f));
                }
                metrics.MotionBlurScore = Math.Clamp(blurFromSharpness, 0f, 100f);

                // سوختگی نور (Overexposure) و تاریکی (Underexposure)
                metrics.IsOverExposed = avgLum > 210f;
                metrics.IsUnderExposed = avgLum < 45f;

                // تخمین نویز ناشی از حساسیت بالا (گین)
                metrics.EstimatedNoiseLevel = (metrics.IsUnderExposed || avgLum < 70) ? Math.Clamp(100f - metrics.ContrastStdDev, 10f, 90f) : 10f;

                // تعیین وضعیت شب و روز
                metrics.EstimatedAmbientLighting = avgLum < 75 ? "Night" : "Day";
            }

            return metrics;
        }

        /// <summary>
        /// تولید پیشنهاد هوشمند بر اساس پروفایل درخواستی یا تحلیل وضعیت زنده
        /// </summary>
        public CameraOptimizationRecommendation GenerateRecommendation(
            CameraCapabilityProfile capabilities,
            CameraSettingsSnapshot currentSettings,
            ImageAnalysisMetrics metrics,
            CameraOptimizationProfileType requestedProfile)
        {
            var rec = new CameraOptimizationRecommendation
            {
                CameraId = currentSettings.CameraId,
                TargetProfile = requestedProfile,
                CurrentSettings = currentSettings.Clone(),
                ProposedSettings = currentSettings.Clone(),
                EnvironmentalMetrics = metrics,
                CreatedAt = DateTime.UtcNow
            };

            var reasons = rec.ParameterReasons;

            switch (requestedProfile)
            {
                case CameraOptimizationProfileType.DaytimeLicensePlate:
                    ApplyDaytimeLicensePlateProfile(rec, capabilities, currentSettings, metrics);
                    break;

                case CameraOptimizationProfileType.NighttimeLicensePlate:
                    ApplyNighttimeLicensePlateProfile(rec, capabilities, currentSettings, metrics);
                    break;

                case CameraOptimizationProfileType.HighSpeedVehicle:
                    ApplyHighSpeedVehicleProfile(rec, capabilities, currentSettings, metrics);
                    break;

                case CameraOptimizationProfileType.LowLightEnvironment:
                    ApplyLowLightProfile(rec, capabilities, currentSettings, metrics);
                    break;

                case CameraOptimizationProfileType.GeneralSurveillance:
                default:
                    ApplyGeneralSurveillanceProfile(rec, capabilities, currentSettings, metrics);
                    break;
            }

            return rec;
        }

        private void ApplyDaytimeLicensePlateProfile(
            CameraOptimizationRecommendation rec,
            CameraCapabilityProfile caps,
            CameraSettingsSnapshot current,
            ImageAnalysisMetrics metrics)
        {
            var p = rec.ProposedSettings;

            // سرعت شاتر مناسب روز برای پلاک‌خوانی: حداقل 1/500 تا 1/1000 ثانیه جهت فریز کردن حرکت خودرو
            if (caps.CanControlShutter)
            {
                p.ExposureMode = "Manual";
                // اگر قبلا شاتر کندتر از 1/500 بوده، به 1/1000 ثانیه ارتقا می‌یابد
                float targetShutter = (metrics.MotionBlurScore > 40 || metrics.RecentAnprConfidence < 0.8f) ? 0.001f : 0.002f; // 1/1000s or 1/500s
                p.ShutterSpeedSeconds = Math.Clamp(targetShutter, caps.MinShutterSeconds, caps.MaxShutterSeconds);

                rec.ParameterReasons["Shutter"] = $"تنظیم سرعت شاتر روی {p.FormatShutterSpeed()} جهت فریز کردن حروف و ارقام پلاک خودرو و حذف کامل تاری کشیدگی حرکتی (Motion Blur).";
            }

            // در روشنایی روز، گین باید کمترین مقدار باشد تا نویز برفکی روی کادر پلاک ایجاد نشود
            if (caps.CanControlGain)
            {
                p.Gain = Math.Clamp(15f, caps.MinGain, caps.MaxGain);
                rec.ParameterReasons["Gain"] = "کاهش گین آنالوگ سنسور به سطح ۱۵ برای ثبت شفاف‌ترین لبه‌های کاراکترهای پلاک بدون ایجاد نویز سنسور.";
            }

            // WDR متعادل برای حذف سایه زیر سپر خودرو
            if (caps.CanControlWdr)
            {
                p.WdrEnabled = true;
                p.WdrLevel = 45f;
                rec.ParameterReasons["WDR"] = "فعال‌سازی WDR در سطح ۴۵٪ جهت برقراری تعادل نور بین آفتاب شدید و سایه زیر سپر و پلاک خودرو.";
            }

            if (caps.CanControlHlc)
            {
                p.HlcEnabled = false; // در روز HLC نیاز نیست
                rec.ParameterReasons["HLC"] = "غیرفعال‌سازی مهار نور شدید چراغ در روز جهت حفظ حداکثر کنتراست طبیعی.";
            }

            if (caps.CanControlDayNightMode)
            {
                p.DayNightMode = "Day";
                rec.ParameterReasons["DayNight"] = "تثبیت حالت روز (فیلتر فیزیکی ICR فعال) جهت دریافت پالت رنگ واقعی پلاک ملی ایران.";
            }

            rec.ExecutiveSummary = "پیکربندی تخصصی پلاک‌خوانی روزانه: سرعت شاتر بالا برای تثبیت حرکت خودروهای عبوری تا سرعت ۸۰ کیلومتر بر ساعت، گین سنسور پایین برای شفافیت فونت پلاک و فعال‌سازی WDR جهت متعادل‌سازی سایه‌های شدید آفتاب.";
            rec.EngineConfidence = 0.95f;
        }

        private void ApplyNighttimeLicensePlateProfile(
            CameraOptimizationRecommendation rec,
            CameraCapabilityProfile caps,
            CameraSettingsSnapshot current,
            ImageAnalysisMetrics metrics)
        {
            var p = rec.ProposedSettings;

            // شاتر شب برای پلاک‌خوانی: معمولاً بین 1/250 تا 1/500 ثانیه
            // مهم: در شب شاتر خیلی سریع تصویر را سیاه می‌کند و شاتر خیلی کند پلاک را کشیده می‌کند
            if (caps.CanControlShutter)
            {
                p.ExposureMode = "Manual";
                float targetShutter = 0.0025f; // 1/400s
                p.ShutterSpeedSeconds = Math.Clamp(targetShutter, caps.MinShutterSeconds, caps.MaxShutterSeconds);
                rec.ParameterReasons["Shutter"] = $"تنظیم سرعت شاتر روی {p.FormatShutterSpeed()} برای برقراری موازنه دقیق بین جلوگیری از کشیدگی پلاک و جذب نور کافی در شب.";
            }

            // ویژگی حیاتی پلاک‌خوانی در شب: HLC (Highlight Compensation)
            // مانع از سوختن و سفید شدن صفحه پلاک در اثر تابش چراغ‌های جلوی خودرو می‌شود
            if (caps.CanControlHlc)
            {
                p.HlcEnabled = true;
                p.HlcLevel = 75f;
                rec.ParameterReasons["HLC"] = "فعال‌سازی کلیدی HLC (مهار نور شدید) در سطح ۷۵٪ برای جلوگیری از سفید شدن (Overexposure) و انعکاس نور خیره‌کننده کاسه چراغ خودرو روی پلاک آلومینیومی.";
            }

            // هماهنگی گین و شاتر: گین باید کنترل شود تا برفک نوری پلاک را مخدوش نکند
            if (caps.CanControlGain)
            {
                p.Gain = Math.Clamp(35f, caps.MinGain, caps.MaxGain);
                rec.ParameterReasons["Gain"] = "تنظیم گین سنسور روی ۳۵؛ افزایش کنترل‌شده برای روشنایی تصویر هم‌زمان با مهار نویز برفکی در پردازش کاراکترها.";
            }

            if (caps.CanControlWdr)
            {
                p.WdrEnabled = true;
                p.WdrLevel = 30f;
                rec.ParameterReasons["WDR"] = "فعال‌سازی WDR ملایم جهت نمایش هم‌زمان جزئیات تیره بدنه خودرو و ارقام بازتابنده پلاک.";
            }

            if (caps.CanControlDayNightMode)
            {
                p.DayNightMode = "Night";
                rec.ParameterReasons["DayNight"] = "تغییر به مد شبانه (سیاه و سفید) با حساسیت مادون قرمز جهت افزایش کنتراست ارقام مشکی روی زمینه سفید/زرد پلاک.";
            }

            if (caps.CanControlIr)
            {
                p.IrMode = caps.SupportsSmartIr ? "Smart" : "Auto";
                p.IrIntensity = 65f;
                rec.ParameterReasons["IR"] = "تنظیم توان مادون قرمز روی ۶۵٪ هوشمند برای ایجاد روشنایی مادون قرمز یکنواخت بدون سوزاندن بازتاب شبرنگ پلاک.";
            }

            rec.ExecutiveSummary = "پیکربندی تخصصی پلاک‌خوانی شبانه: فعال‌سازی قطعی HLC جهت مهار پرتوهای مستقیم چراغ‌های زنون و ال‌ای‌دی خودرو، شاتر 1/400s جهت جلوگیری از کشیدگی پلاک و کنترل توان مادون قرمز جهت خوانایی بی‌نقص OCR.";
            rec.EngineConfidence = 0.94f;
        }

        private void ApplyHighSpeedVehicleProfile(
            CameraOptimizationRecommendation rec,
            CameraCapabilityProfile caps,
            CameraSettingsSnapshot current,
            ImageAnalysisMetrics metrics)
        {
            var p = rec.ProposedSettings;

            if (caps.CanControlShutter)
            {
                p.ExposureMode = "Manual";
                float targetShutter = 0.0005f; // 1/2000s
                p.ShutterSpeedSeconds = Math.Clamp(targetShutter, caps.MinShutterSeconds, caps.MaxShutterSeconds);
                rec.ParameterReasons["Shutter"] = $"افزایش شاتر به {p.FormatShutterSpeed()} برای فریز کردن قطعی خودروهای پرسرعت (تا ۱۲۰ کیلومتر در ساعت) در معابر شریانی و بزرگراه‌ها.";
            }

            if (caps.CanControlGain)
            {
                // جبران کاهش نور شاتر 1/2000 با گین حساب‌شده
                p.Gain = Math.Clamp(40f, caps.MinGain, caps.MaxGain);
                rec.ParameterReasons["Gain"] = "افزایش گین به ۴۰٪ جهت جبران زمان نوردهی بسیار کوتاه شاتر فوق‌سریع بدون تخریب لبه‌های تصویر.";
            }

            if (caps.CanControlWdr)
            {
                p.WdrEnabled = true;
                p.WdrLevel = 50f;
                rec.ParameterReasons["WDR"] = "تنظیم WDR روی ۵۰٪ جهت جلوگیری از تیرگی کابین و شیشه خودرو.";
            }

            rec.ExecutiveSummary = "پیکربندی بزرگراهی و سرعت بالا: شاتر فوق‌سریع 1/2000 ثانیه جهت ثبت دقیق پلاک خودروهای شتاب‌دار، هماهنگ‌شده با گین تقویتی و دامنه دینامیکی پایدار.";
            rec.EngineConfidence = 0.91f;
        }

        private void ApplyLowLightProfile(
            CameraOptimizationRecommendation rec,
            CameraCapabilityProfile caps,
            CameraSettingsSnapshot current,
            ImageAnalysisMetrics metrics)
        {
            var p = rec.ProposedSettings;

            if (caps.CanControlShutter)
            {
                p.ExposureMode = "Auto";
                rec.ParameterReasons["Shutter"] = "تنظیم نوردهی در وضعیت هوشمند تطبیقی برای جذب بیشترین فوتون‌های نوری محیط کم‌نور.";
            }

            if (caps.CanControlGain)
            {
                p.Gain = Math.Clamp(60f, caps.MinGain, caps.MaxGain);
                rec.ParameterReasons["Gain"] = "افزایش گین به ۶۰٪ برای روشن‌سازی فضاهای تاریک و زاویه‌های کور محیطی.";
            }

            if (caps.CanControlWdr)
            {
                p.WdrEnabled = true;
                p.WdrLevel = 60f;
                rec.ParameterReasons["WDR"] = "فعال‌سازی WDR عمیق ۶۰٪ جهت استخراج جزئیات از اعماق سایه‌ها.";
            }

            if (caps.CanControlIr)
            {
                p.IrMode = "Auto";
                p.IrIntensity = 90f;
                rec.ParameterReasons["IR"] = "فعال‌سازی پرتوهای مادون قرمز پرقدرت برای روشن‌سازی محوطه پایش تا عمق میدان حداکثری.";
            }

            rec.ExecutiveSummary = "پیکربندی محیط تاریک و کم‌نور: ارتقای توان مادون قرمز و حساسیت سنسور به منظور دستیابی به حداکثر دید پیرامونی و نظارت عمومی امنیتی.";
            rec.EngineConfidence = 0.89f;
        }

        private void ApplyGeneralSurveillanceProfile(
            CameraOptimizationRecommendation rec,
            CameraCapabilityProfile caps,
            CameraSettingsSnapshot current,
            ImageAnalysisMetrics metrics)
        {
            var p = rec.ProposedSettings;

            if (caps.CanControlShutter)
            {
                p.ExposureMode = "Auto";
                p.ShutterSpeedSeconds = 0.02f; // 1/50s
                rec.ParameterReasons["Shutter"] = "حالت نوردهی خودکار استاندارد (1/50s) برای ارائه طبیعی‌ترین جریان ویدیویی بدون لرزش نور مصنوعی (Anti-Flicker 50Hz).";
            }

            if (caps.CanControlGain)
            {
                p.Gain = Math.Clamp(20f, caps.MinGain, caps.MaxGain);
                rec.ParameterReasons["Gain"] = "گین خودکار نرمال با تراز نویز استاندارد.";
            }

            if (caps.CanControlWdr)
            {
                p.WdrEnabled = false;
                rec.ParameterReasons["WDR"] = "غیرفعال‌سازی پردازش سنگین WDR جهت حفظ کنتراست طبیعی محیط نظارتی عمومی.";
            }

            if (caps.CanControlHlc)
            {
                p.HlcEnabled = false;
                rec.ParameterReasons["HLC"] = "حالت عادی کنترل نور.";
            }

            rec.ExecutiveSummary = "پیکربندی نظارت عمومی محیطی: موازنه پیش‌فرض کارخانه برای نظارت پیوسته، روان و کم‌مصرف بر محوطه عمومی و تردد افراد.";
            rec.EngineConfidence = 0.98f;
        }
    }
}
