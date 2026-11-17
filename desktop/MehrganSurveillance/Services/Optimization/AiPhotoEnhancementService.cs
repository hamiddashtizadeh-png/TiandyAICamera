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
        public int DeblurIterations { get; set; } = 7; // 1..14
        public float SharpnessStrength { get; set; } = 0.85f; // 0..2.0
        public float DenoiseStrength { get; set; } = 0.35f; // 0..1.0
        public bool ApplyLightingRecovery { get; set; } = true;
        public bool PreferOnnxSuperResIfAvailable { get; set; } = true;
    }

    public sealed class PhotoEnhancementResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public PhotoEnhancementTarget Target { get; init; }

        public bool UsedOnnxModel { get; init; }
        public string OnnxModelName { get; init; } = string.Empty;
        public string ExecutionProvider { get; init; } = "DSP + Deconvolution";
        public string MethodSummaryFa { get; init; } = string.Empty;

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
    /// موتور تخصصی شفاف‌سازی، رفع تاری (Deblurring) و فوق‌وضوح (Super-Resolution) برای عکس‌های پلاک خودرو و چهره انسان
    /// ترکیبی از:
    /// ۱. مدل‌های ONNX Super-Resolution / Face Restoration روی DirectML یا CPU (در صورت وجود فایل وزن در Models/Weights)
    /// ۲. دکانولوشن تکراری ریچاردسون-لوسی (Richardson-Lucy) + فیلتر دوطرفه حفظ لبه (Bilateral Denoise)
    /// ۳. بزرگ‌نمایی اسپلاین Catmull-Rom با تیزسازی تطبیقی گرادیان و تعدیل نور CLAHE
    /// کاملاً محلی، آفلاین و بدون ارسال تصویر به اینترنت
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
            "superres.onnx"
        };

        private static readonly string[] PlateModelCandidates =
        {
            "plate-sr-x2.onnx",
            "realesrgan-x2.onnx",
            "realesrgan-x4.onnx",
            "bsrgan-x2.onnx",
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
                foreach (string file in candidates)
                {
                    string full = Path.Combine(dir, file);
                    if (File.Exists(full))
                    {
                        return (true, file, Path.GetFullPath(full), ep);
                    }
                }
            }

            return (false, string.Empty, string.Empty, ep);
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

        public static byte[] EncodeBgra32ToPng(byte[] bgra, int width, int height, int stride)
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
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

            // اگر تصویر خیلی بزرگ باشد، قبل از بزرگ‌نمایی 4x مقیاس کنترل می‌شود تا حافظه اشباع نشود
            int scale = Math.Clamp(options.UpscaleFactor, 1, 4);
            if (scale == 4 && Math.Max(width, height) > 1000) scale = 2;
            if (scale == 2 && Math.Max(width, height) > 1920) scale = 1;

            var metricsBefore = ImageProcessingFilterSet.CalculateQualityMetrics(
                cleanOriginal.AsSpan(), width, height, cleanStride, 4);
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

            // گام ۱: تلاش برای استفاده از مدل شبکه عصبی ONNX Super-Resolution (در صورت وجود در پوشه Weights)
            bool usedOnnx = false;
            string onnxName = string.Empty;
            string epUsed = "موتور هوشمند دکانولوشن محلی + Catmull-Rom SR";

            byte[] workingBgra = (byte[])cleanOriginal.Clone();
            int curW = width;
            int curH = height;
            int curStride = cleanStride;

            // گام ۲: کاهش نویز حافظ لبه (Bilateral Filter) قبل از رفع تاری تا دکانولوشن نویز را بزرگ نکند
            if (options.DenoiseStrength > 0.05f)
            {
                ct.ThrowIfCancellationRequested();
                workingBgra = ApplyEdgePreservingBilateral(workingBgra, curW, curH, curStride, options.DenoiseStrength);
            }

            // گام ۳: رفع تاری هوشمند (Richardson-Lucy Deconvolution با PSF جهت‌دار برای پلاک / متقارن برای چهره)
            int iters = Math.Clamp(options.DeblurIterations, 1, 14);
            if (iters > 0)
            {
                ct.ThrowIfCancellationRequested();
                bool directionalPlate = options.Target == PhotoEnhancementTarget.LicensePlate &&
                                        (metricsBefore.MotionBlurScore > 25f || iters >= 5);
                workingBgra = ApplyRichardsonLucyDeblur(
                    workingBgra,
                    curW,
                    curH,
                    curStride,
                    iterations: iters,
                    directionalHorizontalPsf: directionalPlate,
                    faceProtectSkin: options.Target == PhotoEnhancementTarget.FacePortrait);
            }

            // گام ۴: بزرگ‌نمایی فوق‌وضوح (ONNX Super-Res در صورت وجود مدل، یا Catmull-Rom 4x4 Bicubic)
            if (options.PreferOnnxSuperResIfAvailable && curW <= 640 && curH <= 640)
            {
                var probe = ProbeAvailableModel(options.Target);
                if (probe.Available)
                {
                    var onnxOut = TryRunOnnxSuperResolution(probe.ModelPath, workingBgra, curW, curH, curStride);
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
                workingBgra = UpscaleCatmullRom(workingBgra, curW, curH, curStride, targetW, targetH);
                curW = targetW;
                curH = targetH;
                curStride = curW * 4;
            }

            // گام ۵: تعدیل کنتراست موضعی و بازیابی نور (CLAHE + Soft WDR)
            if (options.ApplyLightingRecovery)
            {
                ct.ThrowIfCancellationRequested();
                byte[] lightingBuf = new byte[workingBgra.Length];
                float clip = options.Target == PhotoEnhancementTarget.FacePortrait ? 1.85f : 2.8f;
                int tiles = options.Target == PhotoEnhancementTarget.FacePortrait ? 6 : 8;
                ImageProcessingFilterSet.ApplyClahe(workingBgra.AsSpan(), lightingBuf.AsSpan(), curW, curH, curStride, 4, clip, tiles);

                // اصلاح گاما و کنتراست ملایم متناسب با نور تصویر
                if (metricsBefore.OverexposedPercentage > 12f)
                {
                    byte[] gammaBuf = new byte[lightingBuf.Length];
                    ImageProcessingFilterSet.ApplyGamma(lightingBuf.AsSpan(), gammaBuf.AsSpan(), curW, curH, curStride, 4, 0.86f);
                    lightingBuf = gammaBuf;
                }
                else if (metricsBefore.AverageLuminance < 85f)
                {
                    byte[] gammaBuf = new byte[lightingBuf.Length];
                    ImageProcessingFilterSet.ApplyGamma(lightingBuf.AsSpan(), gammaBuf.AsSpan(), curW, curH, curStride, 4, 1.18f);
                    lightingBuf = gammaBuf;
                }

                workingBgra = lightingBuf;
            }

            // گام ۶: بازسازی و تیزسازی گرادیان لبه‌ها (Edge-Guided Unsharp Masking) بدون ایجاد دانه روی پوست صورت
            if (options.SharpnessStrength > 0.05f)
            {
                ct.ThrowIfCancellationRequested();
                workingBgra = ApplyGuidedEdgeSharpen(
                    workingBgra,
                    curW,
                    curH,
                    curStride,
                    options.SharpnessStrength,
                    edgeThreshold: options.Target == PhotoEnhancementTarget.FacePortrait ? 10f : 5f);
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

            string methodSummary = usedOnnx
                ? $"شبکه عصبی {onnxName} + دکانولوشن ({iters} تکرار) برای {targetFa}"
                : $"دکانولوشن ریچاردسون-لوسی ({iters} تکرار) + اسپلاین {scale}x Catmull-Rom + CLAHE ({targetFa})";

            return new PhotoEnhancementResult
            {
                Success = true,
                Target = options.Target,
                UsedOnnxModel = usedOnnx,
                OnnxModelName = onnxName,
                ExecutionProvider = epUsed,
                MethodSummaryFa = methodSummary,
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

        private (byte[] Buffer, int Width, int Height)? TryRunOnnxSuperResolution(
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
                        tensor[0, 0, y, x] = srcBgra[idx + 2] / 255.0f; // R
                        tensor[0, 1, y, x] = srcBgra[idx + 1] / 255.0f; // G
                        tensor[0, 2, y, x] = srcBgra[idx] / 255.0f;     // B
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputMeta.Key, tensor)
                };

                using var outputs = session.Run(inputs);
                var outTensor = outputs.FirstOrDefault()?.AsTensor<float>();
                if (outTensor == null || outTensor.Dimensions.Length < 4) return null;

                int outC = outTensor.Dimensions[1];
                int outH = outTensor.Dimensions[2];
                int outW = outTensor.Dimensions[3];
                if (outC < 3 || outH < height || outW < width || outW > 4096 || outH > 4096) return null;

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

                        // نرمال‌سازی خودکار بسته به خروجی [0..1] یا [0..255]
                        if (r <= 1.5f && g <= 1.5f && b <= 1.5f)
                        {
                            r *= 255f;
                            g *= 255f;
                            b *= 255f;
                        }

                        result[idx] = (byte)Math.Clamp((int)b, 0, 255);
                        result[idx + 1] = (byte)Math.Clamp((int)g, 0, 255);
                        result[idx + 2] = (byte)Math.Clamp((int)r, 0, 255);
                        result[idx + 3] = 255;
                    }
                }

                return (result, outW, outH);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// فیلتر کاهش نویز دوطرفه حافظ لبه (Edge-Preserving Bilateral 3x3 Filter)
        /// </summary>
        private static byte[] ApplyEdgePreservingBilateral(byte[] src, int w, int h, int stride, float strength)
        {
            byte[] dst = (byte[])src.Clone();
            float sigmaRange = Math.Clamp(12f + strength * 28f, 10f, 45f);
            float inv2SigmaSq = 1.0f / (2.0f * sigmaRange * sigmaRange);

            for (int y = 1; y < h - 1; y++)
            {
                int rCur = y * stride;
                for (int x = 1; x < w - 1; x++)
                {
                    int cIdx = rCur + (x * 4);
                    float cB = src[cIdx];
                    float cG = src[cIdx + 1];
                    float cR = src[cIdx + 2];
                    float cLum = 0.299f * cR + 0.587f * cG + 0.114f * cB;

                    float sumW = 0f, sumB = 0f, sumG = 0f, sumR = 0f;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nRow = (y + dy) * stride;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nIdx = nRow + ((x + dx) * 4);
                            float nb = src[nIdx];
                            float ng = src[nIdx + 1];
                            float nr = src[nIdx + 2];
                            float nLum = 0.299f * nr + 0.587f * ng + 0.114f * nb;

                            float lumDiff = nLum - cLum;
                            float spatialW = (dx == 0 && dy == 0) ? 1.0f : ((dx == 0 || dy == 0) ? 0.72f : 0.52f);
                            float rangeW = MathF.Exp(-(lumDiff * lumDiff) * inv2SigmaSq);
                            float weight = spatialW * rangeW;

                            sumW += weight;
                            sumB += nb * weight;
                            sumG += ng * weight;
                            sumR += nr * weight;
                        }
                    }

                    if (sumW > 0.001f)
                    {
                        float blend = Math.Clamp(strength * 0.85f, 0f, 0.9f);
                        float outB = sumB / sumW;
                        float outG = sumG / sumW;
                        float outR = sumR / sumW;

                        dst[cIdx] = (byte)Math.Clamp((int)(cB * (1 - blend) + outB * blend), 0, 255);
                        dst[cIdx + 1] = (byte)Math.Clamp((int)(cG * (1 - blend) + outG * blend), 0, 255);
                        dst[cIdx + 2] = (byte)Math.Clamp((int)(cR * (1 - blend) + outR * blend), 0, 255);
                        dst[cIdx + 3] = src[cIdx + 3];
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// الگوریتم دکانولوشن ریچاردسون-لوسی (Richardson-Lucy Deconvolution) در فضای روشنایی
        /// با کنترل میرایی هاله و حفظ صددرصدی طیف رنگ پوست چهره و بدنه پلاک
        /// </summary>
        private static byte[] ApplyRichardsonLucyDeblur(
            byte[] src, int w, int h, int stride, int iterations,
            bool directionalHorizontalPsf, bool faceProtectSkin)
        {
            if (w < 12 || h < 12 || iterations <= 0) return (byte[])src.Clone();

            int count = w * h;
            float[] observed = new float[count];
            float[] estimate = new float[count];
            float[] blurredEst = new float[count];
            float[] ratio = new float[count];
            float[] correction = new float[count];

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                int pRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = rOff + (x * 4);
                    float lum = 0.299f * src[idx + 2] + 0.587f * src[idx + 1] + 0.114f * src[idx];
                    float val = Math.Max(1.0f, lum);
                    observed[pRow + x] = val;
                    estimate[pRow + x] = val;
                }
            }

            // هسته PSF: افقی بیضوی برای پلاک‌های در حال حرکت، یا گاوسی متقارن ۳×۳ برای چهره/فوکوس
            int kw = directionalHorizontalPsf ? 5 : 3;
            int kh = 3;
            int halfW = kw / 2;
            int halfH = kh / 2;

            float[] psf = directionalHorizontalPsf
                ? new float[]
                {
                    0.03f, 0.06f, 0.08f, 0.06f, 0.03f,
                    0.09f, 0.13f, 0.20f, 0.13f, 0.09f,
                    0.03f, 0.06f, 0.08f, 0.06f, 0.03f
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
                // Convolve estimate with PSF
                Convolve2D(estimate, blurredEst, w, h, psf, kw, kh, halfW, halfH);

                // Compute damped ratio
                for (int i = 0; i < count; i++)
                {
                    float denom = Math.Max(2.0f, blurredEst[i]);
                    float r = observed[i] / denom;
                    // میراسازی برای جلوگیری از انفجار نویز و هاله سفید
                    ratio[i] = Math.Clamp(r, 0.65f, 1.48f);
                }

                // Correlate with flipped PSF (متقارن است پس همان کانولوشن)
                Convolve2D(ratio, correction, w, h, psf, kw, kh, halfW, halfH);

                float stepRelax = faceProtectSkin ? 0.68f : 0.85f;
                for (int i = 0; i < count; i++)
                {
                    float updateFactor = 1.0f + stepRelax * (correction[i] - 1.0f);
                    estimate[i] = Math.Clamp(estimate[i] * updateFactor, 1.0f, 255.0f);
                }
            }

            byte[] dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                int pRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = rOff + (x * 4);
                    float oldLum = observed[pRow + x];
                    float newLum = estimate[pRow + x];
                    float scale = (oldLum > 4.0f) ? Math.Clamp(newLum / oldLum, 0.55f, 1.75f) : 1.0f;

                    dst[idx] = (byte)Math.Clamp((int)(src[idx] * scale), 0, 255);
                    dst[idx + 1] = (byte)Math.Clamp((int)(src[idx + 1] * scale), 0, 255);
                    dst[idx + 2] = (byte)Math.Clamp((int)(src[idx + 2] * scale), 0, 255);
                    dst[idx + 3] = src[idx + 3];
                }
            }

            return dst;
        }

        private static void Convolve2D(
            float[] src, float[] dst, int w, int h, float[] kernel, int kw, int kh, int halfW, int halfH)
        {
            for (int y = 0; y < h; y++)
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
            }
        }

        /// <summary>
        /// بزرگ‌نمایی فوق‌کیفیت اسپلاین سه‌مکعبی Catmull-Rom (رفع حالت پیکسلی در برش‌های کوچک پلاک و چهره)
        /// </summary>
        private static byte[] UpscaleCatmullRom(byte[] src, int srcW, int srcH, int srcStride, int dstW, int dstH)
        {
            int dstStride = dstW * 4;
            byte[] dst = new byte[dstStride * dstH];

            float scaleX = (float)(srcW - 1) / Math.Max(1, dstW - 1);
            float scaleY = (float)(srcH - 1) / Math.Max(1, dstH - 1);

            for (int y = 0; y < dstH; y++)
            {
                float fy = y * scaleY;
                int yInt = (int)Math.Floor(fy);
                float ty = fy - yInt;
                int dstRow = y * dstStride;

                float wy0 = CubicCatmullWeight(ty + 1.0f);
                float wy1 = CubicCatmullWeight(ty);
                float wy2 = CubicCatmullWeight(ty - 1.0f);
                float wy3 = CubicCatmullWeight(ty - 2.0f);

                int sy0 = Math.Clamp(yInt - 1, 0, srcH - 1);
                int sy1 = Math.Clamp(yInt, 0, srcH - 1);
                int sy2 = Math.Clamp(yInt + 1, 0, srcH - 1);
                int sy3 = Math.Clamp(yInt + 2, 0, srcH - 1);

                for (int x = 0; x < dstW; x++)
                {
                    float fx = x * scaleX;
                    int xInt = (int)Math.Floor(fx);
                    float tx = fx - xInt;

                    float wx0 = CubicCatmullWeight(tx + 1.0f);
                    float wx1 = CubicCatmullWeight(tx);
                    float wx2 = CubicCatmullWeight(tx - 1.0f);
                    float wx3 = CubicCatmullWeight(tx - 2.0f);

                    int sx0 = Math.Clamp(xInt - 1, 0, srcW - 1);
                    int sx1 = Math.Clamp(xInt, 0, srcW - 1);
                    int sx2 = Math.Clamp(xInt + 1, 0, srcW - 1);
                    int sx3 = Math.Clamp(xInt + 2, 0, srcW - 1);

                    int dstIdx = dstRow + (x * 4);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float r0 =
                            wx0 * src[sy0 * srcStride + sx0 * 4 + ch] +
                            wx1 * src[sy0 * srcStride + sx1 * 4 + ch] +
                            wx2 * src[sy0 * srcStride + sx2 * 4 + ch] +
                            wx3 * src[sy0 * srcStride + sx3 * 4 + ch];
                        float r1 =
                            wx0 * src[sy1 * srcStride + sx0 * 4 + ch] +
                            wx1 * src[sy1 * srcStride + sx1 * 4 + ch] +
                            wx2 * src[sy1 * srcStride + sx2 * 4 + ch] +
                            wx3 * src[sy1 * srcStride + sx3 * 4 + ch];
                        float r2 =
                            wx0 * src[sy2 * srcStride + sx0 * 4 + ch] +
                            wx1 * src[sy2 * srcStride + sx1 * 4 + ch] +
                            wx2 * src[sy2 * srcStride + sx2 * 4 + ch] +
                            wx3 * src[sy2 * srcStride + sx3 * 4 + ch];
                        float r3 =
                            wx0 * src[sy3 * srcStride + sx0 * 4 + ch] +
                            wx1 * src[sy3 * srcStride + sx1 * 4 + ch] +
                            wx2 * src[sy3 * srcStride + sx2 * 4 + ch] +
                            wx3 * src[sy3 * srcStride + sx3 * 4 + ch];

                        float val = wy0 * r0 + wy1 * r1 + wy2 * r2 + wy3 * r3;
                        dst[dstIdx + ch] = (byte)Math.Clamp((int)val, 0, 255);
                    }
                    dst[dstIdx + 3] = 255;
                }
            }

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
        /// تیزسازی هدایت‌شده با آستانه گرادیان (تقویت چشم/ابرو/ارقام بدون ایجاد دانه روی نواحی صاف)
        /// </summary>
        private static byte[] ApplyGuidedEdgeSharpen(
            byte[] src, int w, int h, int stride, float strength, float edgeThreshold)
        {
            byte[] dst = (byte[])src.Clone();
            float k = Math.Clamp(strength, 0f, 2.0f);

            for (int y = 1; y < h - 1; y++)
            {
                int rCur = y * stride;
                int rUp = (y - 1) * stride;
                int rDn = (y + 1) * stride;

                for (int x = 1; x < w - 1; x++)
                {
                    int cIdx = rCur + (x * 4);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int center = src[cIdx + ch];
                        int blur = (src[rUp + x * 4 + ch] + src[rDn + x * 4 + ch] +
                                    src[rCur + (x - 1) * 4 + ch] + src[rCur + (x + 1) * 4 + ch]) >> 2;
                        int detail = center - blur;
                        if (Math.Abs(detail) >= edgeThreshold)
                        {
                            int outVal = center + (int)(detail * k);
                            dst[cIdx + ch] = (byte)Math.Clamp(outVal, 0, 255);
                        }
                    }
                    dst[cIdx + 3] = src[cIdx + 3];
                }
            }

            return dst;
        }

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
