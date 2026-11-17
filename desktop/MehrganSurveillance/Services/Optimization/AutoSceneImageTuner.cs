using System;
using System.Collections.Concurrent;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// وضعیت تشخیص‌داده‌شده نور و شرایط محیطی صحنه برای تنظیم خودکار تصویر پلاک‌خوان
    /// </summary>
    public enum AutoSceneCondition
    {
        DaylightBalanced,
        HighContrastSunlightWdr,
        StrongGlareOrBacklight,
        NightHeadlightHlc,
        LowLightShadow,
        FastMovingBlur
    }

    /// <summary>
    /// خروجی پیشنهاد و تنظیمات خودکار تولیدشده بر اساس فریم زنده دوربین (کاملاً محلی و بدون تغییر فریم اصلی)
    /// </summary>
    public sealed class AutoImageTuningRecommendation
    {
        public string CameraId { get; init; } = string.Empty;
        public int ChannelIndex { get; init; }
        public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
        public AutoSceneCondition Condition { get; init; } = AutoSceneCondition.DaylightBalanced;
        public string SceneLabelFa { get; init; } = "نور متعادل روز";
        public string ShortBadgeFa { get; init; } = "✨ خودکار";
        public string SummaryFa { get; init; } = string.Empty;

        public ImageOptimizationPreset RecommendedPreset { get; init; } = ImageOptimizationPreset.GeneralAnpr;
        public CameraOptimizationProfileType HardwareProfileType { get; init; } = CameraOptimizationProfileType.DaytimeLicensePlate;
        public CameraImageOptimizationProfile OptimizedProfile { get; init; } = new();
        public ImageQualityMetrics Metrics { get; init; } = new();

        // پارامترهای اعمال مستقیم بلادرنگ بر روی LibVLC VideoAdjust
        public float VlcContrast { get; init; } = 1.0f;
        public float VlcBrightness { get; init; } = 1.0f;
        public float VlcGamma { get; init; } = 1.0f;
        public float VlcSaturation { get; init; } = 1.0f;

        /// <summary>
        /// آیا لازم است پیش از کادیابی پلاک، بافر کاری موقت متعادل‌سازی (WDR/Gamma/Contrast) شود؟
        /// </summary>
        public bool RequiresWorkingBufferEnhancement { get; init; }
    }

    /// <summary>
    /// موتور تحلیلگر خودکار شرایط نوری صحنه (Scene-Adaptive Auto Image Tuner)
    /// تنظیمات کنتراست، گاما، WDR نرم‌افزاری، روشنایی و CLAHE را به‌صورت خودکار متناسب با نور لحظه‌ای دوربین تنظیم می‌کند.
    /// فریم اصلی دوربین هرگز دستکاری نمی‌شود.
    /// </summary>
    public sealed class AutoSceneImageTuner
    {
        private readonly ConcurrentDictionary<string, DateTime> _lastEvaluatedUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AutoImageTuningRecommendation> _latestRecommendations = new(StringComparer.OrdinalIgnoreCase);

        public double EvaluationIntervalSeconds { get; set; } = 2.0;

        public AutoImageTuningRecommendation? GetLatestRecommendation(string cameraId)
        {
            if (string.IsNullOrWhiteSpace(cameraId)) return null;
            return _latestRecommendations.TryGetValue(cameraId, out var rec) ? rec : null;
        }

        /// <summary>
        /// ارزیابی دوره‌ای فریم زنده بدون دستکاری بافر ورودی
        /// </summary>
        public (bool Changed, AutoImageTuningRecommendation? Recommendation) EvaluateFrameIfDue(
            AcquiredFrame frame,
            bool forceEvaluate = false)
        {
            if (frame == null || frame.IsDisposed || frame.Width < 32 || frame.Height < 24)
            {
                return (false, null);
            }

            string camKey = !string.IsNullOrWhiteSpace(frame.CameraId) ? frame.CameraId : $"ch_{frame.ChannelIndex}";
            DateTime now = DateTime.UtcNow;

            if (!forceEvaluate &&
                _lastEvaluatedUtc.TryGetValue(camKey, out var lastEval) &&
                (now - lastEval).TotalSeconds < EvaluationIntervalSeconds)
            {
                _latestRecommendations.TryGetValue(camKey, out var existing);
                return (false, existing);
            }

            _lastEvaluatedUtc[camKey] = now;

            var metrics = ImageProcessingFilterSet.CalculateQualityMetrics(
                frame.PixelData.Span,
                frame.Width,
                frame.Height,
                frame.Stride,
                4);

            var rec = BuildRecommendation(camKey, frame.ChannelIndex, metrics, now);

            bool changed = true;
            if (_latestRecommendations.TryGetValue(camKey, out var prev))
            {
                changed = prev.Condition != rec.Condition ||
                          Math.Abs(prev.VlcGamma - rec.VlcGamma) > 0.04f ||
                          Math.Abs(prev.VlcContrast - rec.VlcContrast) > 0.04f ||
                          Math.Abs(prev.VlcBrightness - rec.VlcBrightness) > 0.04f;
            }

            _latestRecommendations[camKey] = rec;
            return (changed, rec);
        }

        public AutoImageTuningRecommendation BuildRecommendation(
            string cameraId,
            int channelIndex,
            ImageQualityMetrics m,
            DateTime timestampUtc)
        {
            AutoSceneCondition condition;
            string labelFa;
            string shortBadge;
            string summaryFa;
            ImageOptimizationPreset preset;
            CameraOptimizationProfileType hwType;

            float vlcContrast, vlcBrightness, vlcGamma, vlcSaturation;
            bool needBufferEnhancement;

            var profile = new CameraImageOptimizationProfile
            {
                CameraId = cameraId,
                CameraName = $"دوربین {channelIndex + 1}",
                ClaheGridSize = 8,
                PerspectiveCorrectionEnabled = true,
                UpdatedAtUtc = timestampUtc
            };

            if (m.AverageLuminance < 68f && (m.OverexposedPercentage >= 3.5f || m.ContrastStdDev >= 42f))
            {
                condition = AutoSceneCondition.NightHeadlightHlc;
                labelFa = "شب و نور مستقیم چراغ خودرو (HLC)";
                shortBadge = "✨ HLC شب";
                preset = ImageOptimizationPreset.Night;
                hwType = CameraOptimizationProfileType.NighttimeLicensePlate;

                profile.Preset = ImageOptimizationPreset.Night;
                profile.DenoiseStrength = 30;
                profile.SharpenStrength = 58;
                profile.ClaheClipLimit = 3.2f;
                profile.Gamma = 0.80f;
                profile.Contrast = 1.42f;
                profile.Brightness = -8;
                profile.AdaptiveThresholdEnabled = true;

                vlcContrast = 1.32f;
                vlcBrightness = 0.95f;
                vlcGamma = 0.85f;
                vlcSaturation = 0.10f;
                needBufferEnhancement = true;
                summaryFa = "تشخیص صحنه شب با چراغ خیره‌کننده: کاهش گاما (0.80)، افزایش کنتراست (1.32) و فعال‌سازی فیلتر مهار نور HLC.";
            }
            else if (m.AverageLuminance < 76f)
            {
                condition = AutoSceneCondition.LowLightShadow;
                labelFa = "محیط کم‌نور / سایه سنگین";
                shortBadge = "✨ تقویت نور";
                preset = ImageOptimizationPreset.LowLight;
                hwType = CameraOptimizationProfileType.LowLightEnvironment;

                profile.Preset = ImageOptimizationPreset.LowLight;
                profile.DenoiseStrength = 40;
                profile.SharpenStrength = 45;
                profile.ClaheClipLimit = 3.4f;
                profile.Gamma = 1.28f;
                profile.Contrast = 1.26f;
                profile.Brightness = 12;
                profile.AdaptiveThresholdEnabled = false;

                vlcContrast = 1.18f;
                vlcBrightness = 1.10f;
                vlcGamma = 1.18f;
                vlcSaturation = 0.95f;
                needBufferEnhancement = true;
                summaryFa = "تشخیص نور ضعیف محیط: افزایش گاما (1.28)، ارتقای روشنایی و CLAHE برای بیرون کشیدن ارقام پلاک از تاریکی.";
            }
            else if (m.OverexposedPercentage >= 16f || m.AverageLuminance >= 182f)
            {
                condition = AutoSceneCondition.StrongGlareOrBacklight;
                labelFa = "تابش شدید آفتاب / سوختگی تصویر";
                shortBadge = "✨ مهار آفتاب";
                preset = ImageOptimizationPreset.Daylight;
                hwType = CameraOptimizationProfileType.DaytimeLicensePlate;

                profile.Preset = ImageOptimizationPreset.Daylight;
                profile.DenoiseStrength = 10;
                profile.SharpenStrength = 60;
                profile.ClaheClipLimit = 3.3f;
                profile.Gamma = 0.78f;
                profile.Contrast = 1.34f;
                profile.Brightness = -12;
                profile.AdaptiveThresholdEnabled = false;

                vlcContrast = 1.24f;
                vlcBrightness = 0.90f;
                vlcGamma = 0.84f;
                vlcSaturation = 0.92f;
                needBufferEnhancement = true;
                summaryFa = "تشخیص اشباع نوری آفتاب: کاهش گاما (0.78) و روشنایی (-12) جهت جلوگیری از سفید شدن پلاک‌های روشن.";
            }
            else if ((m.UnderexposedPercentage >= 8f && (m.OverexposedPercentage >= 3.0f || m.AverageLuminance >= 92f)) ||
                     (m.ContrastStdDev >= 46f && m.AverageLuminance >= 85f))
            {
                // دقیقاً شرایط تصویر ارسالی کاربر: ترکیب سایه عمیق و آفتاب شدید روی آسفالت/خودروهای سفید
                condition = AutoSceneCondition.HighContrastSunlightWdr;
                labelFa = "آفتاب و سایه شدید (WDR خودکار)";
                shortBadge = "✨ WDR آفتاب";
                preset = ImageOptimizationPreset.Daylight;
                hwType = CameraOptimizationProfileType.DaytimeLicensePlate;

                profile.Preset = ImageOptimizationPreset.Daylight;
                profile.DenoiseStrength = 12;
                profile.SharpenStrength = 55;
                profile.ClaheClipLimit = 3.1f;
                profile.Gamma = 0.88f;
                profile.Contrast = 1.28f;
                profile.Brightness = -4;
                profile.AdaptiveThresholdEnabled = false;

                vlcContrast = 1.18f;
                vlcBrightness = 0.96f;
                vlcGamma = 0.90f;
                vlcSaturation = 0.95f;
                needBufferEnhancement = true;
                summaryFa = "تشخیص تضاد شدید آفتاب و سایه (WDR): اعمال متوازن‌سازی CLAHE (3.1)، گامای 0.88 و کنتراست 1.28 روی تصویر زنده و کراپ پلاک.";
            }
            else if (m.MotionBlurScore >= 52f)
            {
                condition = AutoSceneCondition.FastMovingBlur;
                labelFa = "حرکت سریع خودرو / کاهش تاری";
                shortBadge = "✨ ضدتاری";
                preset = ImageOptimizationPreset.FastMovingVehicles;
                hwType = CameraOptimizationProfileType.HighSpeedVehicle;

                profile.Preset = ImageOptimizationPreset.FastMovingVehicles;
                profile.DenoiseStrength = 12;
                profile.SharpenStrength = 70;
                profile.ClaheClipLimit = 2.6f;
                profile.Gamma = 0.96f;
                profile.Contrast = 1.36f;
                profile.Brightness = 0;
                profile.AdaptiveThresholdEnabled = false;

                vlcContrast = 1.22f;
                vlcBrightness = 1.00f;
                vlcGamma = 0.95f;
                vlcSaturation = 0.95f;
                needBufferEnhancement = true;
                summaryFa = "تشخیص تاری حرکتی: افزایش شارپنس لبه‌ها و کنتراست کاراکترها به همراه پیشنهاد شاتر سریع (1/1000s).";
            }
            else
            {
                condition = AutoSceneCondition.DaylightBalanced;
                labelFa = "نور متعادل روز";
                shortBadge = "✨ روز متعادل";
                preset = ImageOptimizationPreset.Daylight;
                hwType = CameraOptimizationProfileType.DaytimeLicensePlate;

                profile.Preset = ImageOptimizationPreset.Daylight;
                profile.DenoiseStrength = 10;
                profile.SharpenStrength = 42;
                profile.ClaheClipLimit = 2.2f;
                profile.Gamma = 0.98f;
                profile.Contrast = 1.18f;
                profile.Brightness = 0;
                profile.AdaptiveThresholdEnabled = false;

                vlcContrast = 1.08f;
                vlcBrightness = 1.00f;
                vlcGamma = 0.98f;
                vlcSaturation = 1.00f;
                needBufferEnhancement = false;
                summaryFa = "شرایط نوری پایدار روز: تنظیمات بهینه شفافیت لبه‌ها و کنتراست ملایم فعال است.";
            }

            return new AutoImageTuningRecommendation
            {
                CameraId = cameraId,
                ChannelIndex = channelIndex,
                EvaluatedAtUtc = timestampUtc,
                Condition = condition,
                SceneLabelFa = labelFa,
                ShortBadgeFa = shortBadge,
                SummaryFa = summaryFa,
                RecommendedPreset = preset,
                HardwareProfileType = hwType,
                OptimizedProfile = profile,
                Metrics = m,
                VlcContrast = vlcContrast,
                VlcBrightness = vlcBrightness,
                VlcGamma = vlcGamma,
                VlcSaturation = vlcSaturation,
                RequiresWorkingBufferEnhancement = needBufferEnhancement
            };
        }

        /// <summary>
        /// ساخت نسخه ایزوله و متعادل‌شده موقت برای تشخیص پلاک و پیش‌نمایش بدون دستکاری بافر اصلی دوربین
        /// </summary>
        public static byte[] CreateIsolatedBalancedBuffer(
            ReadOnlySpan<byte> src,
            int width,
            int height,
            int stride,
            AutoImageTuningRecommendation rec,
            ref byte[]? scratchBuffer)
        {
            int requiredBytes = stride * height;
            if (scratchBuffer == null || scratchBuffer.Length < requiredBytes)
            {
                scratchBuffer = new byte[requiredBytes];
            }

            var dst = scratchBuffer.AsSpan(0, requiredBytes);
            var prof = rec.OptimizedProfile;

            // ساخت جدول LUT ترکیبی شامل: تصحیح گاما + کنتراست + روشنایی + فشرده‌سازی ملایم هایلایت (WDR نرم‌افزاری)
            Span<byte> lut = stackalloc byte[256];
            float invGamma = 1.0f / Math.Max(0.25f, prof.Gamma);
            float contrast = Math.Clamp(prof.Contrast, 0.7f, 1.8f);
            float brightness = prof.Brightness;
            bool applyWdrHighlightSoftKnee = rec.Condition == AutoSceneCondition.HighContrastSunlightWdr ||
                                             rec.Condition == AutoSceneCondition.StrongGlareOrBacklight;

            for (int i = 0; i < 256; i++)
            {
                float norm = i / 255.0f;

                if (applyWdrHighlightSoftKnee)
                {
                    // بالا بردن ملایم سایه‌ها (< 0.25) و مهار اشباع سفید شدید (> 0.78)
                    if (norm < 0.25f)
                    {
                        norm = norm * 1.18f;
                    }
                    else if (norm > 0.78f)
                    {
                        norm = 0.78f + (norm - 0.78f) * 0.68f;
                    }
                }

                float g = (float)Math.Pow(Math.Clamp(norm, 0f, 1f), invGamma) * 255.0f;
                float c = ((g - 128.0f) * contrast) + 128.0f + brightness;
                lut[i] = (byte)Math.Clamp((int)c, 0, 255);
            }

            for (int y = 0; y < height; y++)
            {
                int rowOff = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOff + (x * 4);
                    if (idx + 3 < src.Length && idx + 3 < dst.Length)
                    {
                        dst[idx] = lut[src[idx]];
                        dst[idx + 1] = lut[src[idx + 1]];
                        dst[idx + 2] = lut[src[idx + 2]];
                        dst[idx + 3] = src[idx + 3];
                    }
                }
            }

            return scratchBuffer;
        }
    }
}
