using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    public enum PhotoEnhancementTarget
    {
        LicensePlate,
        FacePortrait,
        GeneralScene
    }

    public sealed class PhotoEnhancementOptions
    {
        public PhotoEnhancementTarget Target { get; set; } = PhotoEnhancementTarget.LicensePlate;
        public int UpscaleFactor { get; set; } = 2; // 1, 2, or 4
        public int DeblurIterations { get; set; } = 5; // 0..12
        public float SharpnessStrength { get; set; } = 0.70f; // 0..1.8
        public float DenoiseStrength { get; set; } = 0.35f; // 0..1.0
        public bool ApplyLightingRecovery { get; set; } = true;
        public bool RemoveScreenMoire { get; set; } = true;
        public float MoireRemovalStrength { get; set; } = 0.65f; // 0..1.0 (0 = auto)
        public bool PreferOnnxSuperResIfAvailable { get; set; } = true;
    }

    public sealed class PhotoEnhancementResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public PhotoEnhancementTarget Target { get; init; }

        public bool UsedOnnxModel { get; init; }
        public string OnnxModelName { get; init; } = string.Empty;
        public string ExecutionProvider { get; init; } = "موتور محلی YCbCr TV-Deconvolution";
        public string MethodSummaryFa { get; init; } = string.Empty;

        public bool DetectedScreenMoire { get; init; }
        public float ScreenMoireScore { get; init; }
        public int DetectedGridPeriodPx { get; init; }

        public int OriginalWidth { get; init; }
        public int OriginalHeight { get; init; }
        public int EnhancedWidth { get; init; }
        public int EnhancedHeight { get; init; }

        public byte[] OriginalPngBytes { get; init; } = Array.Empty<byte>();
        public byte[] EnhancedPngBytes { get; init; } = Array.Empty<byte>();
        public byte[] EnhancedBgraBuffer { get; init; } = Array.Empty<byte>();
        public int EnhancedStride { get; init; }

        public ImageQualityMetrics MetricsBefore { get; init; } = new();
        public ImageQualityMetrics MetricsAfter { get; init; } = new();
        public double ProcessingDurationMs { get; init; }

        // نتیجه OCR واقعی (تنها در صورت بارگذاری مدل iranian-plate-ocr.onnx پر می‌شود)
        public string? PlateOcrBefore { get; init; }
        public float PlateOcrConfBefore { get; init; }
        public string? PlateOcrAfter { get; init; }
        public float PlateOcrConfAfter { get; init; }
        public bool HasValidPlateAfter { get; init; }
    }

    /// <summary>
    /// موتور تخصصی شفاف‌سازی، حذف شبکه مانیتور (Demoiré)، رفع تاری TV-Richardson-Lucy و فوق‌وضوح (Super-Resolution)
    /// تمامی محاسبات روشنایی و رفع تاری در فضای YCbCr انجام می‌شود تا هیچگونه سوختگی رنگ یا هاله نئونی رخ ندهد.
    /// کاملاً محلی، چندنخی (Multi-threaded) و ۱۰۰٪ آفلاین روی ویندوز.
    /// </summary>
    public sealed class AiPhotoEnhancementService : IDisposable
    {
        private static readonly string[] FaceModelCandidates =
        {
            "gfpgan-face.onnx",
            "codeformer.onnx",
            "face-restore.onnx",
            "realesrgan-x2.onnx",
            "realesrgan-x4.onnx",
            "swinir-x2.onnx",
            "superres.onnx"
        };

        private static readonly string[] PlateModelCandidates =
        {
            "plate-sr-x2.onnx",
            "realesrgan-x2.onnx",
            "realesrgan-x4.onnx",
            "bsrgan-x2.onnx",
            "swinir-x2.onnx",
            "superres.onnx"
        };

        private readonly ConcurrentDictionary<string, InferenceSession> _onnxCache = new(StringComparer.OrdinalIgnoreCase);
        private string _activeEp = "CPU";

        public (bool Available, string ModelName, string ModelPath, string ExecutionProvider) ProbeAvailableModel(PhotoEnhancementTarget target)
        {
            string[] candidates = target == PhotoEnhancementTarget.FacePortrait
                ? FaceModelCandidates
                : PlateModelCandidates;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cwd = Directory.GetCurrentDirectory();

            string[] searchDirs =
            {
                Path.Combine(baseDir, "Models", "Weights"),
                Path.Combine(baseDir, "Models", "ANPR"),
                Path.Combine(cwd, "Models", "Weights"),
                Path.Combine(cwd, "Models", "ANPR"),
                Path.Combine(cwd, "desktop", "MehrganSurveillance", "Models", "Weights"),
                Path.Combine(baseDir, "..", "..", "..", "Models", "Weights")
            };

            string ep = DetectExecutionProvider();
            foreach (string dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;

                // ۱. اولویت با نام‌های شناخته‌شده
                foreach (string file in candidates)
                {
                    string full = Path.Combine(dir, file);
                    if (File.Exists(full))
                    {
                        return (true, file, Path.GetFullPath(full), ep);
                    }
                }

                // ۲. پشتیبانی از هر مدل سفارشی واردشده توسط کاربر (به‌جز مدل‌های آشکارساز چهره/پلاک)
                foreach (string onnxFile in Directory.EnumerateFiles(dir, "*.onnx"))
                {
                    string fname = Path.GetFileName(onnxFile);
                    if (fname.StartsWith("version-RFB", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("yolo", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("plate-det", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("plate-ocr", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return (true, fname, Path.GetFullPath(onnxFile), ep);
                }
            }

            return (false, string.Empty, string.Empty, ep);
        }

        public (bool Success, string InstalledPath, string Message) ImportOnnxModel(string sourceFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                {
                    return (false, string.Empty, "فایل مدل انتخاب‌شده وجود ندارد.");
                }

                string ext = Path.GetExtension(sourceFilePath);
                if (!string.Equals(ext, ".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, string.Empty, "فقط فایل‌های استاندارد با پسوند .onnx پشتیبانی می‌شوند.");
                }

                string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "Weights");
                Directory.CreateDirectory(targetDir);

                string fileName = Path.GetFileName(sourceFilePath);
                string dstPath = Path.Combine(targetDir, fileName);

                // بستن سشن قبلی در صورت وجود هم‌نام
                if (_onnxCache.TryRemove(dstPath, out var oldSession))
                {
                    try { oldSession.Dispose(); } catch { }
                }

                File.Copy(sourceFilePath, dstPath, overwrite: true);

                // اعتبارسنجی سریع باز شدن مدل در ONNX Runtime
                using var testOpt = new SessionOptions();
                using var testSession = new InferenceSession(dstPath, testOpt);
                if (testSession.InputMetadata.Count == 0 || testSession.OutputMetadata.Count == 0)
                {
                    File.Delete(dstPath);
                    return (false, string.Empty, "ساختار ورودی/خروجی این فایل ONNX برای پردازش تصویر معتبر نیست.");
                }

                return (true, dstPath, $"مدل عصبی «{fileName}» با موفقیت نصب و فعال شد.");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"خطا در بارگذاری یا اعتبارسنجی مدل ONNX: {ex.Message}");
            }
        }

        private string DetectExecutionProvider()
        {
            try
            {
                using var opt = new SessionOptions();
                opt.AppendExecutionProvider_DML(0);
                _activeEp = "DirectML (GPU)";
            }
            catch
            {
                _activeEp = "CPU";
            }
            return _activeEp;
        }

        public static (byte[] BgraBytes, int Width, int Height, int Stride) DecodeImageFileToBgra32(string filePath)
        {
            byte[] raw = File.ReadAllBytes(filePath);
            return DecodeImageBytesToBgra32(raw);
        }

        public static (byte[] BgraBytes, int Width, int Height, int Stride) DecodeImageBytesToBgra32(byte[] encodedBytes)
        {
            using var ms = new MemoryStream(encodedBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];

            BitmapSource bgraSource = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            int w = bgraSource.PixelWidth;
            int h = bgraSource.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            bgraSource.CopyPixels(pixels, stride, 0);
            return (pixels, w, h, stride);
        }

        public static (byte[] BgraBytes, int Width, int Height, int Stride) CropBgra32(
            byte[] srcBgra, int srcW, int srcH, int srcStride,
            int cropX, int cropY, int cropW, int cropH)
        {
            int x = Math.Clamp(cropX, 0, Math.Max(0, srcW - 8));
            int y = Math.Clamp(cropY, 0, Math.Max(0, srcH - 8));
            int w = Math.Clamp(cropW, 8, srcW - x);
            int h = Math.Clamp(cropH, 8, srcH - y);
            int dstStride = w * 4;
            byte[] dst = new byte[dstStride * h];

            for (int row = 0; row < h; row++)
            {
                int srcOff = ((y + row) * srcStride) + (x * 4);
                int dstOff = row * dstStride;
                Buffer.BlockCopy(srcBgra, srcOff, dst, dstOff, dstStride);
            }

            return (dst, w, h, dstStride);
        }

        public static byte[] EncodeBgra32ToPng(byte[] bgra, int width, int height, int stride)
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        public static (bool HasScreenGrid, float Score, int PeriodPx) DetectScreenMoireGrid(
            byte[] bgra, int w, int h, int stride)
        {
            if (bgra == null || w < 32 || h < 32) return (false, 0f, 0);

            // بررسی ناحیه مرکزی تصویر جهت تشخیص الگوی دوره‌ای پیکسل‌های مانیتور (RGB Subpixel / Moiré)
            int x0 = w / 6;
            int y0 = h / 6;
            int x1 = w - x0;
            int y1 = h - y0;
            int stepY = Math.Max(1, (y1 - y0) / 80);

            double chromaHfSum = 0;
            double lumaHfSum = 0;
            double lumaSignFlipSum = 0;
            int samples = 0;

            // آرایه همبستگی افقی برای تخمین دوره تکراری پیکسل مانیتور (۲ تا ۱۰ پیکسل)
            const int maxLag = 10;
            double[] lagCorr = new double[maxLag + 1];
            double[] lagCount = new double[maxLag + 1];

            for (int y = y0; y < y1 - 2; y += stepY)
            {
                int rOff = y * stride;
                int rowLen = x1 - x0;
                if (rowLen < 24) continue;

                float[] rowHp = new float[rowLen];
                for (int i = 1; i < rowLen - 1; i++)
                {
                    int x = x0 + i;
                    int c = rOff + (x * 4);
                    int l = c - 4;
                    int r = c + 4;

                    float lumC = 0.299f * bgra[c + 2] + 0.587f * bgra[c + 1] + 0.114f * bgra[c];
                    float lumL = 0.299f * bgra[l + 2] + 0.587f * bgra[l + 1] + 0.114f * bgra[l];
                    float lumR = 0.299f * bgra[r + 2] + 0.587f * bgra[r + 1] + 0.114f * bgra[r];

                    float hp = lumC - 0.5f * (lumL + lumR);
                    rowHp[i] = hp;
                    lumaHfSum += Math.Abs(hp);

                    // نوسان رنگین‌کمانی زیرپیکسل‌های RGB مانیتور
                    float rgC = bgra[c + 2] - bgra[c + 1];
                    float rgL = bgra[l + 2] - bgra[l + 1];
                    float gbC = bgra[c + 1] - bgra[c];
                    float gbL = bgra[l + 1] - bgra[l];
                    chromaHfSum += (Math.Abs(rgC - rgL) + Math.Abs(gbC - gbL)) * 0.5;

                    if (i > 2 && rowHp[i] * rowHp[i - 1] < -16f)
                    {
                        lumaSignFlipSum += 1.0;
                    }
                    samples++;
                }

                // خودهمبستگی فرکانس بالا برای پیدا کردن فاصله تکرار شبکه پیکسلی
                for (int lag = 2; lag <= maxLag; lag++)
                {
                    double acc = 0;
                    int cnt = 0;
                    for (int i = 2; i < rowLen - lag - 2; i += 2)
                    {
                        acc += rowHp[i] * rowHp[i + lag];
                        cnt++;
                    }
                    if (cnt > 0)
                    {
                        lagCorr[lag] += acc;
                        lagCount[lag] += cnt;
                    }
                }
            }

            if (samples < 100) return (false, 0f, 0);

            double avgChromaHf = chromaHfSum / samples;
            double avgLumaHf = lumaHfSum / samples;
            double signFlipRate = lumaSignFlipSum / samples;

            // پیدا کردن قوی‌ترین پیک تناوبی بین ۲ تا ۱۰ پیکسل
            double bestPeak = 0;
            int bestLag = 4;
            for (int lag = 3; lag <= maxLag; lag++)
            {
                double val = lagCount[lag] > 0 ? lagCorr[lag] / lagCount[lag] : 0;
                if (val > bestPeak)
                {
                    bestPeak = val;
                    bestLag = lag;
                }
            }

            // ترکیب امتیاز نوسان RGB مانیتور + تناوب پیکسلی
            float score = (float)Math.Clamp(
                (avgChromaHf * 2.4) +
                (Math.Sqrt(Math.Max(0, bestPeak)) * 2.8) +
                (signFlipRate * 45.0) +
                (avgLumaHf > 9.0 ? (avgLumaHf - 9.0) * 1.4 : 0.0),
                0.0, 100.0);

            bool hasGrid = score >= 34f || (avgChromaHf >= 11.5 && avgLumaHf >= 8.0);
            return (hasGrid, score, hasGrid ? bestLag : 0);
        }

        public Task<PhotoEnhancementResult> EnhanceAsync(
            byte[] sourceBgra,
            int width,
            int height,
            int stride,
            PhotoEnhancementOptions options,
            AnprPipeline? anprPipeline = null,
            CancellationToken ct = default)
        {
            return Task.Run(() => EnhanceCore(sourceBgra, width, height, stride, options, anprPipeline, ct), ct);
        }

        private PhotoEnhancementResult EnhanceCore(
            byte[] sourceBgra,
            int width,
            int height,
            int stride,
            PhotoEnhancementOptions options,
            AnprPipeline? anprPipeline,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (sourceBgra == null || sourceBgra.Length == 0 || width < 8 || height < 8)
            {
                return new PhotoEnhancementResult
                {
                    Success = false,
                    ErrorMessage = "تصویر ورودی معتبر نیست یا ابعاد آن بسیار کوچک است.",
                    Target = options.Target
                };
            }

            // تضمین بافر فشرده ۴ بایت بر پیکسل در کپی کاملاً مستقل (محافظت از منبع اصلی)
            int cleanStride = width * 4;
            byte[] cleanOriginal = new byte[cleanStride * height];
            for (int y = 0; y < height; y++)
            {
                int srcOff = y * stride;
                int dstOff = y * cleanStride;
                int copyLen = Math.Min(cleanStride, Math.Max(0, sourceBgra.Length - srcOff));
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(sourceBgra, srcOff, cleanOriginal, dstOff, copyLen);
                }
            }

            // کنترل هوشمند مقیاس بزرگ‌نمایی تا تصاویر خیلی بزرگ رم را اشباع نکنند
            int scale = Math.Clamp(options.UpscaleFactor, 1, 4);
            int maxDim = Math.Max(width, height);
            if (scale == 4 && maxDim > 720) scale = 2;
            if (scale == 2 && maxDim > 1600) scale = 1;

            var metricsBefore = ImageProcessingFilterSet.CalculateQualityMetrics(
                cleanOriginal.AsSpan(), width, height, cleanStride, 4);
            var moireInfo = DetectScreenMoireGrid(cleanOriginal, width, height, cleanStride);
            byte[] origPng = EncodeBgra32ToPng(cleanOriginal, width, height, cleanStride);

            string? ocrBefore = null;
            float ocrConfBefore = 0f;
            if (options.Target == PhotoEnhancementTarget.LicensePlate &&
                anprPipeline != null &&
                anprPipeline.ModelStatus.OcrLoaded)
            {
                var (rawTxt, cBefore, pBefore) = anprPipeline.EvaluateOcrOnBuffer(cleanOriginal, width, height, cleanStride);
                ocrBefore = pBefore.IsValid ? pBefore.NormalizedText : (string.IsNullOrWhiteSpace(rawTxt) ? "ناخوانا" : rawTxt);
                ocrConfBefore = cBefore;
            }

            // تبدیل به فضای تفکیک‌شده YCbCr (جلوگیری مطلق از سوختگی رنگ و نقاط سبز/بنفش نئونی)
            int curW = width;
            int curH = height;
            int curStride = cleanStride;
            byte[] workingBgra = (byte[])cleanOriginal.Clone();

            bool appliedMoireRemoval = false;
            float effectiveMoireStrength = 0f;

            // گام ۱: حذف هوشمند شبکه پیکسلی مانیتور و موج Moiré (بسیار حیاتی برای عکس‌های گرفته‌شده از صفحه مانیتور دوربین)
            if (options.RemoveScreenMoire)
            {
                ct.ThrowIfCancellationRequested();
                effectiveMoireStrength = options.MoireRemovalStrength > 0.05f
                    ? (moireInfo.HasScreenGrid ? Math.Max(options.MoireRemovalStrength, 0.65f) : options.MoireRemovalStrength * 0.55f)
                    : (moireInfo.HasScreenGrid ? Math.Clamp(moireInfo.Score / 95f, 0.55f, 0.92f) : 0f);

                if (moireInfo.HasScreenGrid || options.MoireRemovalStrength >= 0.35f)
                {
                    int gridRadius = moireInfo.PeriodPx > 0
                        ? Math.Clamp((moireInfo.PeriodPx + 1) / 2, 2, 5)
                        : 3;

                    workingBgra = ApplyYCbCrDemoireFilter(workingBgra, curW, curH, curStride, gridRadius, effectiveMoireStrength);
                    appliedMoireRemoval = true;
                }
            }

            // گام ۲: کاهش نویز دوطرفه حافظ لبه در فضای YCbCr (حفظ لبه‌های پلاک و چشم‌ها، پاکسازی نویز و بلوک JPEG)
            if (options.DenoiseStrength > 0.04f)
            {
                ct.ThrowIfCancellationRequested();
                workingBgra = ApplyParallelYCbCrBilateral(workingBgra, curW, curH, curStride, options.DenoiseStrength);
            }

            // گام ۳: رفع تاری منظم‌شده با Total Variation (TV-Richardson-Lucy) صرفاً روی کانال روشنایی Y
            // اگر تصویر دارای شبکه مانیتور شدید بود، تعداد تکرار هوشمندانه کنترل می‌شود تا الگوی پیکسلی مجدداً تقویت نشود
            int requestedIters = Math.Clamp(options.DeblurIterations, 0, 12);
            int effectiveIters = appliedMoireRemoval && moireInfo.HasScreenGrid
                ? Math.Min(requestedIters, 4)
                : requestedIters;

            if (effectiveIters > 0)
            {
                ct.ThrowIfCancellationRequested();
                bool directionalPlate = options.Target == PhotoEnhancementTarget.LicensePlate &&
                                        !moireInfo.HasScreenGrid &&
                                        metricsBefore.MotionBlurScore > 28f;
                float tvWeight = options.Target == PhotoEnhancementTarget.FacePortrait
                    ? 0.09f
                    : (appliedMoireRemoval ? 0.085f : 0.05f);

                workingBgra = ApplyTvRichardsonLucyYCbCr(
                    workingBgra,
                    curW,
                    curH,
                    curStride,
                    iterations: effectiveIters,
                    directionalHorizontalPsf: directionalPlate,
                    tvRegularization: tvWeight);
            }

            // گام ۴: بزرگ‌نمایی فوق‌وضوح (مدل ONNX کاشی‌بندی‌شده در صورت وجود، وگرنه Catmull-Rom چندنخی)
            bool usedOnnx = false;
            string onnxName = string.Empty;
            string epUsed = "موتور محلی YCbCr TV-Deconvolution + Catmull-Rom";

            if (options.PreferOnnxSuperResIfAvailable)
            {
                var probe = ProbeAvailableModel(options.Target);
                if (probe.Available)
                {
                    ct.ThrowIfCancellationRequested();
                    var onnxOut = TryRunOnnxSuperResolutionTiled(probe.ModelPath, workingBgra, curW, curH, curStride);
                    if (onnxOut.HasValue)
                    {
                        workingBgra = onnxOut.Value.Buffer;
                        curW = onnxOut.Value.Width;
                        curH = onnxOut.Value.Height;
                        curStride = curW * 4;
                        usedOnnx = true;
                        onnxName = probe.ModelName;
                        epUsed = $"ONNX ({probe.ExecutionProvider})";
                    }
                }
            }

            if (!usedOnnx && scale > 1)
            {
                ct.ThrowIfCancellationRequested();
                int targetW = curW * scale;
                int targetH = curH * scale;
                workingBgra = UpscaleCatmullRomParallel(workingBgra, curW, curH, curStride, targetW, targetH);
                curW = targetW;
                curH = targetH;
                curStride = curW * 4;
            }

            // گام ۵: بازیابی نرم و ایمن نور و کنتراست (Y-Channel Soft CLAHE بدون سوختگی سفید و اشباع رنگ)
            if (options.ApplyLightingRecovery)
            {
                ct.ThrowIfCancellationRequested();
                float clip = options.Target == PhotoEnhancementTarget.FacePortrait
                    ? 1.55f
                    : (options.Target == PhotoEnhancementTarget.LicensePlate ? 2.15f : 1.75f);
                float blend = options.Target == PhotoEnhancementTarget.FacePortrait
                    ? 0.35f
                    : (options.Target == PhotoEnhancementTarget.LicensePlate ? 0.50f : 0.42f);
                workingBgra = ApplySoftYCbCrClahe(workingBgra, curW, curH, curStride, clipLimit: clip, blendStrength: blend);
            }

            // گام ۶: بازسازی و شفاف‌سازی بدون هاله (Multi-Scale Gaussian Unsharp Mask روی روشنایی Y با سقف Overshoot)
            if (options.SharpnessStrength > 0.04f)
            {
                ct.ThrowIfCancellationRequested();
                float effectiveSharp = appliedMoireRemoval && moireInfo.HasScreenGrid
                    ? Math.Min(options.SharpnessStrength, 0.85f)
                    : options.SharpnessStrength;
                float coringThreshold = options.Target == PhotoEnhancementTarget.FacePortrait
                    ? 7.5f
                    : (appliedMoireRemoval ? 6.5f : 3.5f);
                int blurRadius = scale >= 2 ? 2 : 1;

                workingBgra = ApplyHaloFreeYCbCrUnsharp(
                    workingBgra, curW, curH, curStride,
                    strength: effectiveSharp,
                    blurRadius: blurRadius,
                    coringThreshold: coringThreshold,
                    maxOvershoot: options.Target == PhotoEnhancementTarget.FacePortrait ? 20f : 30f);
            }

            var metricsAfter = ImageProcessingFilterSet.CalculateQualityMetrics(
                workingBgra.AsSpan(), curW, curH, curStride, 4);
            byte[] enhancedPng = EncodeBgra32ToPng(workingBgra, curW, curH, curStride);

            string? ocrAfter = null;
            float ocrConfAfter = 0f;
            bool validPlate = false;
            if (options.Target == PhotoEnhancementTarget.LicensePlate &&
                anprPipeline != null &&
                anprPipeline.ModelStatus.OcrLoaded)
            {
                var (rawTxt, cAfter, pAfter) = anprPipeline.EvaluateOcrOnBuffer(workingBgra, curW, curH, curStride);
                ocrAfter = pAfter.IsValid ? pAfter.NormalizedText : (string.IsNullOrWhiteSpace(rawTxt) ? "ناخوانا" : rawTxt);
                ocrConfAfter = cAfter;
                validPlate = pAfter.IsValid;
            }

            sw.Stop();

            string targetFa = options.Target switch
            {
                PhotoEnhancementTarget.LicensePlate => "پلاک خودرو",
                PhotoEnhancementTarget.FacePortrait => "چهره انسان",
                _ => "تصویر عمومی"
            };

            string moireTag = appliedMoireRemoval ? "حذف شبکه مانیتور (Demoiré) + " : string.Empty;
            string methodSummary = usedOnnx
                ? $"{moireTag}شبکه عصبی {onnxName} + TV-Deconvolution ({effectiveIters} تکرار) [{targetFa}]"
                : $"{moireTag}TV-Richardson-Lucy ({effectiveIters} تکرار) + اسپلاین {scale}x + CLAHE نرم YCbCr [{targetFa}]";

            return new PhotoEnhancementResult
            {
                Success = true,
                Target = options.Target,
                UsedOnnxModel = usedOnnx,
                OnnxModelName = onnxName,
                ExecutionProvider = epUsed,
                MethodSummaryFa = methodSummary,
                DetectedScreenMoire = moireInfo.HasScreenGrid,
                ScreenMoireScore = moireInfo.Score,
                DetectedGridPeriodPx = moireInfo.PeriodPx,
                OriginalWidth = width,
                OriginalHeight = height,
                EnhancedWidth = curW,
                EnhancedHeight = curH,
                OriginalPngBytes = origPng,
                EnhancedPngBytes = enhancedPng,
                EnhancedBgraBuffer = workingBgra,
                EnhancedStride = curStride,
                MetricsBefore = metricsBefore,
                MetricsAfter = metricsAfter,
                ProcessingDurationMs = sw.Elapsed.TotalMilliseconds,
                PlateOcrBefore = ocrBefore,
                PlateOcrConfBefore = ocrConfBefore,
                PlateOcrAfter = ocrAfter,
                PlateOcrConfAfter = ocrConfAfter,
                HasValidPlateAfter = validPlate
            };
        }

        #region فیلترهای پیشرفته YCbCr (حذف شبکه مانیتور، TV-RL، CLAHE نرم، Unsharp بدون هاله)

        private static void SplitYCbCr(byte[] bgra, int w, int h, int stride, out float[] yCh, out float[] cbCh, out float[] crCh)
        {
            int count = w * h;
            yCh = new float[count];
            cbCh = new float[count];
            crCh = new float[count];

            var yLocal = yCh;
            var cbLocal = cbCh;
            var crLocal = crCh;

            Parallel.For(0, h, y =>
            {
                int sRow = y * stride;
                int dRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = sRow + (x * 4);
                    float b = bgra[idx];
                    float g = bgra[idx + 1];
                    float r = bgra[idx + 2];

                    int p = dRow + x;
                    yLocal[p] = 0.299f * r + 0.587f * g + 0.114f * b;
                    cbLocal[p] = -0.168736f * r - 0.331264f * g + 0.5f * b + 128.0f;
                    crLocal[p] = 0.5f * r - 0.418688f * g - 0.081312f * b + 128.0f;
                }
            });
        }

        private static byte[] MergeYCbCrToBgra(float[] yCh, float[] cbCh, float[] crCh, int w, int h)
        {
            int stride = w * 4;
            byte[] dst = new byte[stride * h];

            Parallel.For(0, h, y =>
            {
                int dRow = y * stride;
                int sRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int p = sRow + x;
                    float yv = yCh[p];
                    float cb = cbCh[p] - 128.0f;
                    float cr = crCh[p] - 128.0f;

                    int r = (int)(yv + 1.402f * cr + 0.5f);
                    int g = (int)(yv - 0.344136f * cb - 0.714136f * cr + 0.5f);
                    int b = (int)(yv + 1.772f * cb + 0.5f);

                    int idx = dRow + (x * 4);
                    dst[idx] = (byte)Math.Clamp(b, 0, 255);
                    dst[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                    dst[idx + 2] = (byte)Math.Clamp(r, 0, 255);
                    dst[idx + 3] = 255;
                }
            });

            return dst;
        }

        /// <summary>
        /// حذف هوشمند شبکه پیکسلی مانیتور و موج رنگی Moiré (مخصوص عکس‌های موبایل از صفحه دوربین مداربسته)
        /// با پاکسازی قوی رنگین‌کمان RGB روی Cb/Cr و فیلتر ساختاری حافظ لبه‌های کلان روی Y
        /// </summary>
        private static byte[] ApplyYCbCrDemoireFilter(byte[] src, int w, int h, int stride, int gridRadius, float strength)
        {
            SplitYCbCr(src, w, h, stride, out float[] yCh, out float[] cbCh, out float[] crCh);

            // ۱. حذف کامل زیرپیکسل‌های رنگین‌کمانی مانیتور (سبز/سرخابی/آبی) با فیلتر گاوسی دوگذره روی Cb و Cr
            int chromaRadius = Math.Clamp(gridRadius + 2, 3, 7);
            float[] cbSmooth = SeparableBoxBlur(cbCh, w, h, chromaRadius);
            cbSmooth = SeparableBoxBlur(cbSmooth, w, h, Math.Max(2, chromaRadius - 1));
            float[] crSmooth = SeparableBoxBlur(crCh, w, h, chromaRadius);
            crSmooth = SeparableBoxBlur(crSmooth, w, h, Math.Max(2, chromaRadius - 1));

            // ۲. فیلتر میانگین‌گیر دقیقاً به اندازه دوره شبکه پیکسلی روی کانال Y جهت محو کامل دانه‌های شبکه‌ای
            int lumaR = Math.Clamp(gridRadius, 2, 5);
            float[] yGridSmooth = SeparableBoxBlur(yCh, w, h, lumaR);
            yGridSmooth = SeparableBoxBlur(yGridSmooth, w, h, Math.Max(1, lumaR - 1));

            // ۳. ترکیب هوشمند: نواحی که تفاوت آن‌ها صرفاً موج فرکانس بالای شبکه است صاف می‌شوند ولی لبه‌های واقعی حفظ می‌شوند
            int count = w * h;
            float[] yOut = new float[count];
            float[] cbOut = new float[count];
            float[] crOut = new float[count];

            float lumaBlendBase = Math.Clamp(strength * 0.92f, 0.25f, 0.95f);
            float chromaBlend = Math.Clamp(0.55f + strength * 0.42f, 0.55f, 0.97f);

            Parallel.For(0, h, y =>
            {
                int row = y * w;
                int yUp = Math.Max(0, y - lumaR) * w;
                int yDn = Math.Min(h - 1, y + lumaR) * w;

                for (int x = 0; x < w; x++)
                {
                    int p = row + x;
                    int xLf = Math.Max(0, x - lumaR);
                    int xRt = Math.Min(w - 1, x + lumaR);

                    // سنجش وجود لبه کلان واقعی (بر اساس تصویر صاف‌شده تا به نقاط ریز شبکه حساس نباشد)
                    float macroGx = Math.Abs(yGridSmooth[row + xRt] - yGridSmooth[row + xLf]);
                    float macroGy = Math.Abs(yGridSmooth[yDn + x] - yGridSmooth[yUp + x]);
                    float macroEdge = Math.Max(macroGx, macroGy);

                    // روی لبه‌های بزرگ کمی بیشتر ساختار اصلی حفظ می‌شود ولی روی بافت‌های شبکه‌ای کاملاً صاف می‌گردد
                    float edgePreserve = Math.Clamp((macroEdge - 28f) / 70f, 0f, 0.35f);
                    float blendY = Math.Clamp(lumaBlendBase * (1.0f - edgePreserve), 0.15f, 0.95f);

                    yOut[p] = yCh[p] * (1.0f - blendY) + yGridSmooth[p] * blendY;
                    cbOut[p] = cbCh[p] * (1.0f - chromaBlend) + cbSmooth[p] * chromaBlend;
                    crOut[p] = crCh[p] * (1.0f - chromaBlend) + crSmooth[p] * chromaBlend;
                }
            });

            return MergeYCbCrToBgra(yOut, cbOut, crOut, w, h);
        }

        private static float[] SeparableBoxBlur(float[] src, int w, int h, int radius)
        {
            if (radius <= 0) return (float[])src.Clone();

            float[] tmp = new float[w * h];
            float[] dst = new float[w * h];
            float invWin = 1.0f / (2 * radius + 1);

            // گذر افقی با Sliding Sum (زمان O(1) به ازای هر پیکسل)
            Parallel.For(0, h, y =>
            {
                int row = y * w;
                float sum = src[row] * (radius + 1);
                for (int k = 1; k <= radius; k++)
                {
                    sum += src[row + Math.Min(w - 1, k)];
                }

                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = sum * invWin;
                    int removeX = Math.Clamp(x - radius, 0, w - 1);
                    int addX = Math.Clamp(x + radius + 1, 0, w - 1);
                    sum += src[row + addX] - src[row + removeX];
                }
            });

            // گذر عمودی با Sliding Sum
            Parallel.For(0, w, x =>
            {
                float sum = tmp[x] * (radius + 1);
                for (int k = 1; k <= radius; k++)
                {
                    sum += tmp[Math.Min(h - 1, k) * w + x];
                }

                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum * invWin;
                    int removeY = Math.Clamp(y - radius, 0, h - 1);
                    int addY = Math.Clamp(y + radius + 1, 0, h - 1);
                    sum += tmp[addY * w + x] - tmp[removeY * w + x];
                }
            });

            return dst;
        }

        /// <summary>
        /// فیلتر دوطرفه چندنخی در فضای YCbCr (حفظ لبه‌های روشنایی + پاکسازی نویز رنگی)
        /// </summary>
        private static byte[] ApplyParallelYCbCrBilateral(byte[] src, int w, int h, int stride, float strength)
        {
            SplitYCbCr(src, w, h, stride, out float[] yCh, out float[] cbCh, out float[] crCh);

            float[] yOut = (float[])yCh.Clone();
            float[] cbOut = SeparableBoxBlur(cbCh, w, h, strength > 0.5f ? 2 : 1);
            float[] crOut = SeparableBoxBlur(crCh, w, h, strength > 0.5f ? 2 : 1);

            float sigmaRange = Math.Clamp(10f + strength * 26f, 10f, 38f);
            float inv2SigmaSq = 1.0f / (2.0f * sigmaRange * sigmaRange);
            float blend = Math.Clamp(strength * 0.80f, 0f, 0.85f);

            Parallel.For(1, h - 1, y =>
            {
                int rCur = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    float cVal = yCh[rCur + x];
                    float sumW = 0f;
                    float sumY = 0f;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nRow = (y + dy) * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            float nVal = yCh[nRow + x + dx];
                            float diff = nVal - cVal;
                            float spatialW = (dx == 0 && dy == 0) ? 1.0f : ((dx == 0 || dy == 0) ? 0.75f : 0.55f);
                            float rangeW = MathF.Exp(-(diff * diff) * inv2SigmaSq);
                            float weight = spatialW * rangeW;

                            sumW += weight;
                            sumY += nVal * weight;
                        }
                    }

                    if (sumW > 0.001f)
                    {
                        float filtered = sumY / sumW;
                        yOut[rCur + x] = cVal * (1.0f - blend) + filtered * blend;
                    }
                }
            });

            // ترکیب ملایم رنگ صاف‌شده
            float cBlend = Math.Clamp(strength * 0.75f, 0.1f, 0.85f);
            Parallel.For(0, w * h, i =>
            {
                cbOut[i] = cbCh[i] * (1.0f - cBlend) + cbOut[i] * cBlend;
                crOut[i] = crCh[i] * (1.0f - cBlend) + crOut[i] * cBlend;
            });

            return MergeYCbCrToBgra(yOut, cbOut, crOut, w, h);
        }

        /// <summary>
        /// دکانولوشن ریچاردسون-لوسی منظم‌شده با Total Variation (TV-RL) صرفاً در کانال روشنایی Y
        /// از انفجار نویز، ایجاد نقاط سفید و برهم خوردن رنگ‌ها به‌طور کامل جلوگیری می‌کند
        /// </summary>
        private static byte[] ApplyTvRichardsonLucyYCbCr(
            byte[] src, int w, int h, int stride, int iterations,
            bool directionalHorizontalPsf, float tvRegularization)
        {
            if (w < 12 || h < 12 || iterations <= 0) return (byte[])src.Clone();

            SplitYCbCr(src, w, h, stride, out float[] yCh, out float[] cbCh, out float[] crCh);

            int count = w * h;
            float[] observed = new float[count];
            float[] estimate = new float[count];
            float[] blurredEst = new float[count];
            float[] ratio = new float[count];
            float[] correction = new float[count];

            for (int i = 0; i < count; i++)
            {
                float v = Math.Max(1.0f, yCh[i]);
                observed[i] = v;
                estimate[i] = v;
            }

            int kw = directionalHorizontalPsf ? 5 : 3;
            int kh = 3;
            int halfW = kw / 2;
            int halfH = kh / 2;

            float[] psf = directionalHorizontalPsf
                ? new float[]
                {
                    0.02f, 0.06f, 0.08f, 0.06f, 0.02f,
                    0.09f, 0.14f, 0.22f, 0.14f, 0.09f,
                    0.02f, 0.06f, 0.08f, 0.06f, 0.02f
                }
                : new float[]
                {
                    0.0625f, 0.125f, 0.0625f,
                    0.125f,  0.25f,  0.125f,
                    0.0625f, 0.125f, 0.0625f
                };

            float psfSum = psf.Sum();
            for (int i = 0; i < psf.Length; i++) psf[i] /= psfSum;

            for (int iter = 0; iter < iterations; iter++)
            {
                Convolve2DParallel(estimate, blurredEst, w, h, psf, kw, kh, halfW, halfH);

                Parallel.For(0, count, i =>
                {
                    float denom = Math.Max(3.0f, blurredEst[i]);
                    ratio[i] = Math.Clamp(observed[i] / denom, 0.72f, 1.35f);
                });

                Convolve2DParallel(ratio, correction, w, h, psf, kw, kh, halfW, halfH);

                // اعمال ترم منظم‌ساز Total Variation (جلوگیری از Ringing و بزرگ شدن نویز)
                float[] nextEst = new float[count];
                Parallel.For(0, h, y =>
                {
                    int rCur = y * w;
                    int rUp = Math.Max(0, y - 1) * w;
                    int rDn = Math.Min(h - 1, y + 1) * w;

                    for (int x = 0; x < w; x++)
                    {
                        int p = rCur + x;
                        int xLf = Math.Max(0, x - 1);
                        int xRt = Math.Min(w - 1, x + 1);

                        float uC = estimate[p];
                        float uL = estimate[rCur + xLf];
                        float uR = estimate[rCur + xRt];
                        float uU = estimate[rUp + x];
                        float uD = estimate[rDn + x];

                        // تقریب پایدار دیورژانس گرادیان نرمال‌شده
                        float eps = 2.0f;
                        float dR = (uR - uC) / MathF.Sqrt((uR - uC) * (uR - uC) + eps);
                        float dL = (uC - uL) / MathF.Sqrt((uC - uL) * (uC - uL) + eps);
                        float dD = (uD - uC) / MathF.Sqrt((uD - uC) * (uD - uC) + eps);
                        float dU = (uC - uU) / MathF.Sqrt((uC - uU) * (uC - uU) + eps);
                        float div = (dR - dL) + (dD - dU);

                        float tvDenom = Math.Clamp(1.0f - (tvRegularization * div), 0.72f, 1.38f);
                        float rawUpdate = correction[p] / tvDenom;
                        float damped = 1.0f + 0.65f * (rawUpdate - 1.0f);
                        nextEst[p] = Math.Clamp(uC * damped, 0f, 255f);
                    }
                });

                estimate = nextEst;
            }

            // محدودسازی اختلاف حداکثری نسبت به روشنایی اصلی جهت جلوگیری از هاله‌های غیرطبیعی
            Parallel.For(0, count, i =>
            {
                float diff = Math.Clamp(estimate[i] - yCh[i], -42f, 42f);
                yCh[i] = Math.Clamp(yCh[i] + diff, 0f, 255f);
            });

            return MergeYCbCrToBgra(yCh, cbCh, crCh, w, h);
        }

        private static void Convolve2DParallel(
            float[] src, float[] dst, int w, int h, float[] kernel, int kw, int kh, int halfW, int halfH)
        {
            Parallel.For(0, h, y =>
            {
                int dstRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    int kIdx = 0;
                    for (int ky = -halfH; ky <= halfH; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, h - 1);
                        int srcRow = sy * w;
                        for (int kx = -halfW; kx <= halfW; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, w - 1);
                            acc += src[srcRow + sx] * kernel[kIdx++];
                        }
                    }
                    dst[dstRow + x] = acc;
                }
            });
        }

        /// <summary>
        /// بزرگ‌نمایی فوق‌کیفیت چندنخی اسپلاین سه‌مکعبی Catmull-Rom
        /// </summary>
        private static byte[] UpscaleCatmullRomParallel(byte[] src, int srcW, int srcH, int srcStride, int dstW, int dstH)
        {
            int dstStride = dstW * 4;
            byte[] dst = new byte[dstStride * dstH];

            float scaleX = (float)(srcW - 1) / Math.Max(1, dstW - 1);
            float scaleY = (float)(srcH - 1) / Math.Max(1, dstH - 1);

            Parallel.For(0, dstH, y =>
            {
                float fy = y * scaleY;
                int yInt = (int)Math.Floor(fy);
                float ty = fy - yInt;
                int dstRow = y * dstStride;

                float wy0 = CubicCatmullWeight(ty + 1.0f);
                float wy1 = CubicCatmullWeight(ty);
                float wy2 = CubicCatmullWeight(ty - 1.0f);
                float wy3 = CubicCatmullWeight(ty - 2.0f);

                int sy0 = Math.Clamp(yInt - 1, 0, srcH - 1) * srcStride;
                int sy1 = Math.Clamp(yInt, 0, srcH - 1) * srcStride;
                int sy2 = Math.Clamp(yInt + 1, 0, srcH - 1) * srcStride;
                int sy3 = Math.Clamp(yInt + 2, 0, srcH - 1) * srcStride;

                for (int x = 0; x < dstW; x++)
                {
                    float fx = x * scaleX;
                    int xInt = (int)Math.Floor(fx);
                    float tx = fx - xInt;

                    float wx0 = CubicCatmullWeight(tx + 1.0f);
                    float wx1 = CubicCatmullWeight(tx);
                    float wx2 = CubicCatmullWeight(tx - 1.0f);
                    float wx3 = CubicCatmullWeight(tx - 2.0f);

                    int sx0 = Math.Clamp(xInt - 1, 0, srcW - 1) * 4;
                    int sx1 = Math.Clamp(xInt, 0, srcW - 1) * 4;
                    int sx2 = Math.Clamp(xInt + 1, 0, srcW - 1) * 4;
                    int sx3 = Math.Clamp(xInt + 2, 0, srcW - 1) * 4;

                    int dstIdx = dstRow + (x * 4);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float r0 = wx0 * src[sy0 + sx0 + ch] + wx1 * src[sy0 + sx1 + ch] + wx2 * src[sy0 + sx2 + ch] + wx3 * src[sy0 + sx3 + ch];
                        float r1 = wx0 * src[sy1 + sx0 + ch] + wx1 * src[sy1 + sx1 + ch] + wx2 * src[sy1 + sx2 + ch] + wx3 * src[sy1 + sx3 + ch];
                        float r2 = wx0 * src[sy2 + sx0 + ch] + wx1 * src[sy2 + sx1 + ch] + wx2 * src[sy2 + sx2 + ch] + wx3 * src[sy2 + sx3 + ch];
                        float r3 = wx0 * src[sy3 + sx0 + ch] + wx1 * src[sy3 + sx1 + ch] + wx2 * src[sy3 + sx2 + ch] + wx3 * src[sy3 + sx3 + ch];

                        float val = wy0 * r0 + wy1 * r1 + wy2 * r2 + wy3 * r3;
                        dst[dstIdx + ch] = (byte)Math.Clamp((int)(val + 0.5f), 0, 255);
                    }
                    dst[dstIdx + 3] = 255;
                }
            });

            return dst;
        }

        private static float CubicCatmullWeight(float x)
        {
            x = Math.Abs(x);
            if (x <= 1.0f)
                return ((1.5f * x - 2.5f) * x * x) + 1.0f;
            if (x < 2.0f)
                return ((-0.5f * x + 2.5f) * x - 4.0f) * x + 2.0f;
            return 0f;
        }

        /// <summary>
        /// CLAHE نرم روی کانال روشنایی Y با ترکیب تدریجی (بدون سوختگی نقاط روشن و بدون تغییر رنگ)
        /// </summary>
        private static byte[] ApplySoftYCbCrClahe(byte[] src, int w, int h, int stride, float clipLimit, float blendStrength)
        {
            if (w < 24 || h < 24) return src;

            SplitYCbCr(src, w, h, stride, out float[] yCh, out float[] cbCh, out float[] crCh);

            int tilesX = Math.Clamp(w / 96, 2, 8);
            int tilesY = Math.Clamp(h / 96, 2, 8);
            int tileW = w / tilesX;
            int tileH = h / tilesY;
            if (tileW < 4 || tileH < 4) return src;

            byte[,,] cdfTable = new byte[tilesY, tilesX, 256];
            int[] hist = new int[256];

            for (int ty = 0; ty < tilesY; ty++)
            {
                int y0 = ty * tileH;
                int y1 = (ty == tilesY - 1) ? h : y0 + tileH;

                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * tileW;
                    int x1 = (tx == tilesX - 1) ? w : x0 + tileW;

                    Array.Clear(hist, 0, 256);
                    int actualPixels = 0;

                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            int bin = Math.Clamp((int)yCh[row + x], 0, 255);
                            hist[bin]++;
                            actualPixels++;
                        }
                    }

                    int clipVal = Math.Max(1, (int)((clipLimit * actualPixels) / 256f));
                    int excess = 0;
                    for (int i = 0; i < 256; i++)
                    {
                        if (hist[i] > clipVal)
                        {
                            excess += hist[i] - clipVal;
                            hist[i] = clipVal;
                        }
                    }

                    int redist = excess / 256;
                    for (int i = 0; i < 256; i++) hist[i] += redist;

                    long cdf = 0;
                    for (int i = 0; i < 256; i++)
                    {
                        cdf += hist[i];
                        cdfTable[ty, tx, i] = (byte)Math.Clamp((int)((cdf * 255) / Math.Max(1, actualPixels)), 0, 255);
                    }
                }
            }

            float[] yOut = new float[w * h];
            Parallel.For(0, h, y =>
            {
                float fy = (float)y / tileH - 0.5f;
                int ty1 = Math.Clamp((int)Math.Floor(fy), 0, tilesY - 1);
                int ty2 = Math.Clamp(ty1 + 1, 0, tilesY - 1);
                float dy = Math.Clamp(fy - Math.Floor(fy), 0f, 1f);
                int row = y * w;

                for (int x = 0; x < w; x++)
                {
                    float fx = (float)x / tileW - 0.5f;
                    int tx1 = Math.Clamp((int)Math.Floor(fx), 0, tilesX - 1);
                    int tx2 = Math.Clamp(tx1 + 1, 0, tilesX - 1);
                    float dx = Math.Clamp(fx - Math.Floor(fx), 0f, 1f);

                    float origY = yCh[row + x];
                    int lumBin = Math.Clamp((int)origY, 0, 255);

                    float v11 = cdfTable[ty1, tx1, lumBin];
                    float v12 = cdfTable[ty1, tx2, lumBin];
                    float v21 = cdfTable[ty2, tx1, lumBin];
                    float v22 = cdfTable[ty2, tx2, lumBin];

                    float top = v11 + dx * (v12 - v11);
                    float bottom = v21 + dx * (v22 - v21);
                    float eqY = top + dy * (bottom - top);

                    // محافظت از نقاط خیلی روشن (هایلایت‌ها) جهت جلوگیری از سوختگی نور سفید
                    float highlightGuard = origY > 210f ? Math.Clamp((255f - origY) / 45f, 0.15f, 1.0f) : 1.0f;
                    float effBlend = blendStrength * highlightGuard;

                    yOut[row + x] = Math.Clamp(origY * (1.0f - effBlend) + eqY * effBlend, 0f, 255f);
                }
            });

            return MergeYCbCrToBgra(yOut, cbCh, crCh, w, h);
        }

        /// <summary>
        /// تیزسازی بدون هاله (Multi-Scale Gaussian Unsharp Mask) صرفاً روی روشنایی Y با سقف کنترل Overshoot
        /// </summary>
        private static byte[] ApplyHaloFreeYCbCrUnsharp(
            byte[] src, int w, int h, int stride,
            float strength, int blurRadius, float coringThreshold, float maxOvershoot)
        {
            SplitYCbCr(src, w, h, stride, out float[] yCh, out float[] cbCh, out float[] crCh);

            // دو گذر Box Blur تقریب بسیار دقیق فیلتر گاوسی است
            float[] blurred = SeparableBoxBlur(yCh, w, h, blurRadius);
            blurred = SeparableBoxBlur(blurred, w, h, blurRadius);

            float k = Math.Clamp(strength, 0f, 1.8f);
            float[] yOut = new float[w * h];

            Parallel.For(0, w * h, i =>
            {
                float detail = yCh[i] - blurred[i];
                float absD = Math.Abs(detail);

                if (absD <= coringThreshold)
                {
                    yOut[i] = yCh[i];
                }
                else
                {
                    // شیب نرم بالای آستانه جهت حذف نویز ریز و تقویت لبه‌های واقعی
                    float excess = (absD - coringThreshold) * Math.Sign(detail);
                    float boost = Math.Clamp(excess * k, -maxOvershoot, maxOvershoot);
                    yOut[i] = Math.Clamp(yCh[i] + boost, 0f, 255f);
                }
            });

            return MergeYCbCrToBgra(yOut, cbCh, crCh, w, h);
        }

        #endregion

        #region استنتاج کاشی‌بندی‌شده مدل‌های شبکه عصبی ONNX (پشتیبانی از هر ابعاد تصویر)

        private (byte[] Buffer, int Width, int Height)? TryRunOnnxSuperResolutionTiled(
            string modelPath, byte[] srcBgra, int width, int height, int stride)
        {
            try
            {
                var session = _onnxCache.GetOrAdd(modelPath, path =>
                {
                    var opt = new SessionOptions
                    {
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        EnableMemoryPattern = false
                    };
                    try { opt.AppendExecutionProvider_DML(0); } catch { }
                    return new InferenceSession(path, opt);
                });

                var inputMeta = session.InputMetadata.First();
                var dims = inputMeta.Value.Dimensions;
                bool fixedInputSize = dims.Length >= 4 && dims[2] > 0 && dims[3] > 0;

                if (fixedInputSize || (width <= 512 && height <= 512))
                {
                    return RunSingleOnnxPass(session, inputMeta.Key, dims, srcBgra, width, height, stride);
                }

                // برای تصاویر بزرگ با مدل‌های پویا (Dynamic H/W)، پردازش کاشی‌بندی‌شده ۲۵۶×۲۵۶ بدون اشباع حافظه GPU
                const int tileSize = 256;
                const int overlap = 16;

                // تست سریع یک کاشی جهت کشف ضریب بزرگ‌نمایی مدل (1x / 2x / 4x)
                int probeW = Math.Min(tileSize, width);
                int probeH = Math.Min(tileSize, height);
                var probeCrop = CropBgra32(srcBgra, width, height, stride, 0, 0, probeW, probeH);
                var probeOut = RunSingleOnnxPass(session, inputMeta.Key, dims, probeCrop.BgraBytes, probeW, probeH, probeCrop.Stride);
                if (!probeOut.HasValue) return null;

                int scaleFactor = Math.Max(1, (int)Math.Round((double)probeOut.Value.Width / probeW));
                int outFullW = width * scaleFactor;
                int outFullH = height * scaleFactor;
                if (outFullW > 4096 || outFullH > 4096) return null;

                int outFullStride = outFullW * 4;
                byte[] fullOut = new byte[outFullStride * outFullH];

                for (int ty = 0; ty < height; ty += tileSize)
                {
                    for (int tx = 0; tx < width; tx += tileSize)
                    {
                        int x0 = Math.Max(0, tx - overlap);
                        int y0 = Math.Max(0, ty - overlap);
                        int x1 = Math.Min(width, tx + tileSize + overlap);
                        int y1 = Math.Min(height, ty + tileSize + overlap);
                        int tw = x1 - x0;
                        int th = y1 - y0;

                        var tileCrop = CropBgra32(srcBgra, width, height, stride, x0, y0, tw, th);
                        var tileOut = RunSingleOnnxPass(session, inputMeta.Key, dims, tileCrop.BgraBytes, tw, th, tileCrop.Stride);
                        if (!tileOut.HasValue) return null;

                        int validStartX = (tx - x0) * scaleFactor;
                        int validStartY = (ty - y0) * scaleFactor;
                        int validW = Math.Min(tileSize, width - tx) * scaleFactor;
                        int validH = Math.Min(tileSize, height - ty) * scaleFactor;

                        int tileOutStride = tileOut.Value.Width * 4;
                        for (int r = 0; r < validH; r++)
                        {
                            int srcRow = (validStartY + r) * tileOutStride + (validStartX * 4);
                            int dstRow = ((ty * scaleFactor) + r) * outFullStride + (tx * scaleFactor * 4);
                            int copyBytes = Math.Min(validW * 4, Math.Min(tileOut.Value.Buffer.Length - srcRow, fullOut.Length - dstRow));
                            if (copyBytes > 0)
                            {
                                Buffer.BlockCopy(tileOut.Value.Buffer, srcRow, fullOut, dstRow, copyBytes);
                            }
                        }
                    }
                }

                return (fullOut, outFullW, outFullH);
            }
            catch
            {
                return null;
            }
        }

        private static (byte[] Buffer, int Width, int Height)? RunSingleOnnxPass(
            InferenceSession session, string inputName, int[] dims,
            byte[] srcBgra, int width, int height, int stride)
        {
            int inH = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : height;
            int inW = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : width;

            var tensor = new DenseTensor<float>(new[] { 1, 3, inH, inW });
            float sxRatio = (float)width / inW;
            float syRatio = (float)height / inH;

            for (int y = 0; y < inH; y++)
            {
                int sy = Math.Min(height - 1, (int)(y * syRatio));
                int rOff = sy * stride;
                for (int x = 0; x < inW; x++)
                {
                    int sx = Math.Min(width - 1, (int)(x * sxRatio));
                    int idx = rOff + (sx * 4);
                    tensor[0, 0, y, x] = srcBgra[idx + 2] / 255.0f;
                    tensor[0, 1, y, x] = srcBgra[idx + 1] / 255.0f;
                    tensor[0, 2, y, x] = srcBgra[idx] / 255.0f;
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, tensor)
            };

            using var outputs = session.Run(inputs);
            var outTensor = outputs.FirstOrDefault()?.AsTensor<float>();
            if (outTensor == null || outTensor.Dimensions.Length < 4) return null;

            int outC = outTensor.Dimensions[1];
            int outH = outTensor.Dimensions[2];
            int outW = outTensor.Dimensions[3];
            if (outC < 3 || outH < 8 || outW < 8 || outW > 4096 || outH > 4096) return null;

            byte[] result = new byte[outW * outH * 4];
            for (int y = 0; y < outH; y++)
            {
                int rOff = y * outW * 4;
                for (int x = 0; x < outW; x++)
                {
                    int idx = rOff + (x * 4);
                    float r = outTensor[0, 0, y, x];
                    float g = outTensor[0, 1, y, x];
                    float b = outTensor[0, 2, y, x];

                    if (r <= 1.5f && g <= 1.5f && b <= 1.5f && r >= -1.5f)
                    {
                        if (r < 0f || g < 0f || b < 0f)
                        {
                            // مدل‌های [-1..1] مانند GFPGAN / CodeFormer
                            r = (r + 1f) * 127.5f;
                            g = (g + 1f) * 127.5f;
                            b = (b + 1f) * 127.5f;
                        }
                        else
                        {
                            r *= 255f;
                            g *= 255f;
                            b *= 255f;
                        }
                    }

                    result[idx] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
                    result[idx + 1] = (byte)Math.Clamp((int)(g + 0.5f), 0, 255);
                    result[idx + 2] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
                    result[idx + 3] = 255;
                }
            }

            return (result, outW, outH);
        }

        #endregion

        public void Dispose()
        {
            foreach (var session in _onnxCache.Values)
            {
                try { session.Dispose(); } catch { }
            }
            _onnxCache.Clear();
        }
    }
}
