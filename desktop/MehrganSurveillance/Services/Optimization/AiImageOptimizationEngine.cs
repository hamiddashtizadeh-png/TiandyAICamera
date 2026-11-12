using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// موتور هوشمند پردازش و بهینه‌سازی تصویر (AI Image Optimization Engine)
    /// پردازش محلی، بافر کاملاً مستقل، حفاظت ۱۰۰٪ از فریم اصلی، ارزیابی چندنسخه‌ای و اتصال به OCR
    /// بدون ارسال هیچ فرمانی به دوربین
    /// </summary>
    public class AiImageOptimizationEngine
    {
        private readonly CameraImageOptimizationProfileRegistry _profileRegistry;
        private readonly SemaphoreSlim _concurrencyLimiter = new(4, 4); // محدودیت تعداد پردازش‌های هم‌زمان جهت حفظ منابع
        private long _totalOptimizationsPerformed;
        private long _totalOcrImprovements;

        public CameraImageOptimizationProfileRegistry ProfileRegistry => _profileRegistry;
        public long TotalOptimizationsPerformed => Interlocked.Read(ref _totalOptimizationsPerformed);
        public long TotalOcrImprovements => Interlocked.Read(ref _totalOcrImprovements);

        public event Action<string>? LogMessage;
        public event Action<ImageOptimizationResult>? OptimizationCompleted;

        public AiImageOptimizationEngine(CameraImageOptimizationProfileRegistry? registry = null)
        {
            _profileRegistry = registry ?? new CameraImageOptimizationProfileRegistry();
        }

        /// <summary>
        /// بهینه‌سازی هوشمند پلاک با بررسی چندگانه و رقابت نامزدهای پردازشی
        /// تضمین قطعی: بافر ورودی دست‌نخورده باقی می‌ماند و تمام پردازش‌ها روی بافر مستقل انجام می‌شوند.
        /// </summary>
        public async Task<ImageOptimizationResult> OptimizePlateCropAsync(
            string cameraId,
            byte[] sourceBuffer,
            int width,
            int height,
            int stride,
            int bpp,
            Func<byte[], int, int, int, (string RawText, float Confidence, IranianPlate Plate)> ocrEvaluator,
            CameraImageOptimizationProfile? customProfile = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            Interlocked.Increment(ref _totalOptimizationsPerformed);

            var result = new ImageOptimizationResult
            {
                CameraId = cameraId,
                ProcessedAtUtc = DateTime.UtcNow
            };

            if (sourceBuffer == null || sourceBuffer.Length == 0 || width <= 10 || height <= 8)
            {
                sw.Stop();
                result.ProcessingDurationMs = sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // قانون حیاتی ۱: Frame اصلی هرگز تغییر نکند.
            // ایجاد یک کپی کاملاً ایزوله و مستقل از بافر اولیه
            byte[] isolatedRawBytes = new byte[sourceBuffer.Length];
            Buffer.BlockCopy(sourceBuffer, 0, isolatedRawBytes, 0, sourceBuffer.Length);

            // استخراج متریک‌های کیفیت تصویر اولیه (Before)
            var metricsBefore = ImageProcessingFilterSet.CalculateQualityMetrics(
                isolatedRawBytes.AsSpan(), width, height, stride, bpp);
            result.MetricsBefore = metricsBefore;
            result.OriginalCropJpeg = ConvertBufferToJpeg(isolatedRawBytes, width, height, stride, bpp);

            // ارزیابی OCR روی نسخه خام اولیه (Baseline Before)
            var (rawOcr, rawConf, rawPlate) = ocrEvaluator(isolatedRawBytes, width, height, stride);
            result.OcrTextBefore = rawPlate.IsValid ? rawPlate.NormalizedText : (string.IsNullOrWhiteSpace(rawOcr) ? "ناخوانا" : rawOcr);
            result.OcrConfidenceBefore = rawConf;

            var profile = customProfile ?? _profileRegistry.GetProfileForCamera(cameraId);

            // ایجاد لیست نامزدهای هوشمند پردازش (محدود به ۲ تا ۴ نسخه جهت جلوگیری از سربار)
            var candidates = new List<ImageOptimizationCandidate>();

            // نامزد ۰: تصویر خام اولیه بدون تغییر (Baseline)
            var candBaseline = new ImageOptimizationCandidate
            {
                FilterName = "Raw Baseline",
                Description = "تصویر خام برش اولیه بدون هیچ فیلتر",
                PixelData = isolatedRawBytes,
                Width = width,
                Height = height,
                Stride = stride,
                Metrics = metricsBefore,
                JpegBytes = result.OriginalCropJpeg,
                OcrRawText = rawOcr,
                OcrConfidence = rawConf,
                Plate = rawPlate,
                CompositeScore = CalculateCompositeScore(rawPlate, rawConf, metricsBefore)
            };
            candidates.Add(candBaseline);

            // نامزد ۱: فیلتر متوازن پروفایل دوربین (Denoise + Sharpen + CLAHE + Gamma + Contrast)
            byte[] cand1Buffer = ApplyFullProfile(isolatedRawBytes, width, height, stride, bpp, profile);
            var cand1Metrics = ImageProcessingFilterSet.CalculateQualityMetrics(cand1Buffer.AsSpan(), width, height, stride, bpp);
            var (cand1Ocr, cand1Conf, cand1Plate) = ocrEvaluator(cand1Buffer, width, height, stride);

            var cand1 = new ImageOptimizationCandidate
            {
                FilterName = $"Standard ({profile.Preset})",
                Description = "ترکیب بهینه‌ساز پروفایل دوربین (کنتراست موضعی، شارپنس و تصحیح نور)",
                PixelData = cand1Buffer,
                Width = width,
                Height = height,
                Stride = stride,
                Metrics = cand1Metrics,
                JpegBytes = ConvertBufferToJpeg(cand1Buffer, width, height, stride, bpp),
                OcrRawText = cand1Ocr,
                OcrConfidence = cand1Conf,
                Plate = cand1Plate,
                CompositeScore = CalculateCompositeScore(cand1Plate, cand1Conf, cand1Metrics)
            };
            candidates.Add(cand1);

            // نامزد ۲: فیلتر تخصصی مهار تابش نور چراغ (Headlight Glare Suppression + Deep CLAHE)
            // در صورتی که پیکسل‌های سوخته وجود داشته باشد یا در حالت شب
            if (metricsBefore.OverexposedPercentage > 6f || profile.Preset == ImageOptimizationPreset.Night)
            {
                byte[] cand2Buffer = ApplyHeadlightSuppression(isolatedRawBytes, width, height, stride, bpp);
                var cand2Metrics = ImageProcessingFilterSet.CalculateQualityMetrics(cand2Buffer.AsSpan(), width, height, stride, bpp);
                var (cand2Ocr, cand2Conf, cand2Plate) = ocrEvaluator(cand2Buffer, width, height, stride);

                var cand2 = new ImageOptimizationCandidate
                {
                    FilterName = "Glare Suppression + CLAHE",
                    Description = "مهار پرتوهای زنون/ال‌ای‌دی چراغ خودرو و تعدیل موضعی هیستوگرام",
                    PixelData = cand2Buffer,
                    Width = width,
                    Height = height,
                    Stride = stride,
                    Metrics = cand2Metrics,
                    JpegBytes = ConvertBufferToJpeg(cand2Buffer, width, height, stride, bpp),
                    OcrRawText = cand2Ocr,
                    OcrConfidence = cand2Conf,
                    Plate = cand2Plate,
                    CompositeScore = CalculateCompositeScore(cand2Plate, cand2Conf, cand2Metrics)
                };
                candidates.Add(cand2);
            }

            // نامزد ۳: فیلتر خنثی‌سازی تاری کشیدگی حرکت (Motion De-blur & High-Pass Edge)
            // در صورتی که تاری حرکتی سنجیده شود یا پریست خودروی پرسرعت فعال باشد
            if (metricsBefore.MotionBlurScore > 30f || profile.Preset == ImageOptimizationPreset.FastMovingVehicles)
            {
                byte[] cand3Buffer = ApplyMotionDeblur(isolatedRawBytes, width, height, stride, bpp);
                var cand3Metrics = ImageProcessingFilterSet.CalculateQualityMetrics(cand3Buffer.AsSpan(), width, height, stride, bpp);
                var (cand3Ocr, cand3Conf, cand3Plate) = ocrEvaluator(cand3Buffer, width, height, stride);

                var cand3 = new ImageOptimizationCandidate
                {
                    FilterName = "Motion De-blur & Sharpen",
                    Description = "خنثی‌سازی تاری خطی حرکت خودرو و ارتقای تیز بودن لبه ارقام",
                    PixelData = cand3Buffer,
                    Width = width,
                    Height = height,
                    Stride = stride,
                    Metrics = cand3Metrics,
                    JpegBytes = ConvertBufferToJpeg(cand3Buffer, width, height, stride, bpp),
                    OcrRawText = cand3Ocr,
                    OcrConfidence = cand3Conf,
                    Plate = cand3Plate,
                    CompositeScore = CalculateCompositeScore(cand3Plate, cand3Conf, cand3Metrics)
                };
                candidates.Add(cand3);
            }

            // نامزد ۴: تفکیک تطبیقی باینری (Adaptive Binarization) در صورت نیاز
            if (profile.AdaptiveThresholdEnabled && candidates.Count < 4)
            {
                byte[] cand4Buffer = ApplyAdaptiveThreshold(isolatedRawBytes, width, height, stride, bpp);
                var cand4Metrics = ImageProcessingFilterSet.CalculateQualityMetrics(cand4Buffer.AsSpan(), width, height, stride, bpp);
                var (cand4Ocr, cand4Conf, cand4Plate) = ocrEvaluator(cand4Buffer, width, height, stride);

                var cand4 = new ImageOptimizationCandidate
                {
                    FilterName = "Adaptive Threshold",
                    Description = "جداسازی کامل کاراکترها از زمینه بازتابنده پلاک با آستانه‌گذاری محلی",
                    PixelData = cand4Buffer,
                    Width = width,
                    Height = height,
                    Stride = stride,
                    Metrics = cand4Metrics,
                    JpegBytes = ConvertBufferToJpeg(cand4Buffer, width, height, stride, bpp),
                    OcrRawText = cand4Ocr,
                    OcrConfidence = cand4Conf,
                    Plate = cand4Plate,
                    CompositeScore = CalculateCompositeScore(cand4Plate, cand4Conf, cand4Metrics)
                };
                candidates.Add(cand4);
            }

            // انتخاب بهترین نامزد بر اساس بیشترین امتیاز ترکیبی (اعتبار پلاک ایران + ضریب اطمینان OCR)
            var bestCandidate = candidates
                .OrderByDescending(c => c.CompositeScore)
                .ThenByDescending(c => c.OcrConfidence)
                .First();

            bestCandidate.IsSelectedAsBest = true;

            result.EvaluatedCandidates = candidates;
            result.BestCandidate = bestCandidate;
            result.WinningMethod = bestCandidate.FilterName;

            // محاسبه درصد بهبود
            if (rawConf > 0.01f)
            {
                result.ConfidenceImprovementPercentage = Math.Max(0f, (bestCandidate.OcrConfidence - rawConf) * 100f);
            }
            else if (bestCandidate.OcrConfidence > 0.1f)
            {
                result.ConfidenceImprovementPercentage = bestCandidate.OcrConfidence * 100f;
            }

            if (bestCandidate != candBaseline && (bestCandidate.Plate.IsValid || bestCandidate.OcrConfidence > rawConf + 0.04f))
            {
                Interlocked.Increment(ref _totalOcrImprovements);
            }

            sw.Stop();
            result.ProcessingDurationMs = sw.Elapsed.TotalMilliseconds;

            OptimizationCompleted?.Invoke(result);
            return result;
        }

        private static float CalculateCompositeScore(IranianPlate plate, float confidence, ImageQualityMetrics metrics)
        {
            float score = confidence * 50f;

            // قانون حیاتی ۴: هیچ نتیجه‌ای صرفاً به دلیل Confidence بالاتر نباید قطعی اعلام شود،
            // مگر اینکه ساختار پلاک و اعتبارسنجی نیز آن را تأیید کنند.
            if (plate.IsValid)
            {
                score += 50f; // امتیاز قطعی به پلاک با ساختار کاملاً معتبر ملی ایران
            }
            else if (plate.ValidationStatus == AnprValidationStatus.Partial)
            {
                score += 20f;
            }

            // امتیاز مکمل کیفیت لبه‌ها و عدم سوختگی
            score += Math.Min(15f, metrics.LaplacianSharpness * 0.05f);
            if (metrics.OverexposedPercentage > 25f) score -= 15f;

            return score;
        }

        /// <summary>
        /// اعمال زنجیره کامل فیلترهای پروفایل
        /// </summary>
        private static byte[] ApplyFullProfile(
            byte[] src, int w, int h, int stride, int bpp, CameraImageOptimizationProfile profile)
        {
            byte[] buf1 = new byte[src.Length];
            byte[] buf2 = new byte[src.Length];

            // گام ۱: کاهش نویز
            if (profile.DenoiseStrength > 10)
            {
                ImageProcessingFilterSet.DenoiseMedian3x3(src.AsSpan(), buf1.AsSpan(), w, h, stride, bpp);
            }
            else
            {
                Buffer.BlockCopy(src, 0, buf1, 0, src.Length);
            }

            // گام ۲: تعدیل هیستوگرام CLAHE
            ImageProcessingFilterSet.ApplyClahe(
                buf1.AsSpan(), buf2.AsSpan(), w, h, stride, bpp, profile.ClaheClipLimit, profile.ClaheGridSize);

            // گام ۳: تصحیح گاما و کنتراست
            ImageProcessingFilterSet.ApplyGamma(
                buf2.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, profile.Gamma);

            ImageProcessingFilterSet.AdjustContrastBrightness(
                buf1.AsSpan(), buf2.AsSpan(), w, h, stride, bpp, profile.Contrast, profile.Brightness);

            // گام ۴: افزایش شارپنس لبه‌ها
            if (profile.SharpenStrength > 5)
            {
                float sharpK = profile.SharpenStrength / 50.0f;
                ImageProcessingFilterSet.Sharpen(
                    buf2.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, sharpK);
                return buf1;
            }

            return buf2;
        }

        /// <summary>
        /// اعمال فیلتر تخصصی مهار نور چراغ جلو
        /// </summary>
        private static byte[] ApplyHeadlightSuppression(byte[] src, int w, int h, int stride, int bpp)
        {
            byte[] buf1 = new byte[src.Length];
            byte[] buf2 = new byte[src.Length];

            // کاهش گاما جهت تضعیف مناطق سفید و سوخته چراغ
            ImageProcessingFilterSet.ApplyGamma(src.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, 0.75f);

            // اعمال CLAHE قوی با ClipLimit بالاتر
            ImageProcessingFilterSet.ApplyClahe(buf1.AsSpan(), buf2.AsSpan(), w, h, stride, bpp, 3.5f, 8);

            // کنتراست تقویتی
            ImageProcessingFilterSet.AdjustContrastBrightness(buf2.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, 1.4f, -8);

            // شارپنس لبه حروف
            ImageProcessingFilterSet.Sharpen(buf1.AsSpan(), buf2.AsSpan(), w, h, stride, bpp, 0.9f);

            return buf2;
        }

        /// <summary>
        /// اعمال فیلتر خنثی‌سازی تاری ناشی از کشیدگی حرکت خودرو
        /// </summary>
        private static byte[] ApplyMotionDeblur(byte[] src, int w, int h, int stride, int bpp)
        {
            byte[] buf1 = new byte[src.Length];
            byte[] buf2 = new byte[src.Length];

            // فیلتر De-blur در راستای حرکت
            ImageProcessingFilterSet.ReduceMotionBlur(src.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, 0.75f);

            // شارپ‌سازی تقویتی کاراکترهای تار
            ImageProcessingFilterSet.Sharpen(buf1.AsSpan(), buf2.AsSpan(), w, h, stride, bpp, 1.25f);

            // ارتقای کنتراست
            ImageProcessingFilterSet.AdjustContrastBrightness(buf2.AsSpan(), buf1.AsSpan(), w, h, stride, bpp, 1.30f, 0);

            return buf1;
        }

        /// <summary>
        /// اعمال باینریزاسیون تطبیقی محلی
        /// </summary>
        private static byte[] ApplyAdaptiveThreshold(byte[] src, int w, int h, int stride, int bpp)
        {
            byte[] dst = new byte[src.Length];
            ImageProcessingFilterSet.AdaptiveThreshold(src.AsSpan(), dst.AsSpan(), w, h, stride, bpp);
            return dst;
        }

        /// <summary>
        /// تبدیل بافر BGRA به فایل JPEG فشرده در حافظه
        /// </summary>
        public static byte[] ConvertBufferToJpeg(byte[] pixelData, int width, int height, int stride, int bpp = 4, int quality = 90)
        {
            try
            {
                var format = (bpp == 4) ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
                var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixelData, stride);

                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }
}
