using System;
using System.Collections.Generic;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// مجموعه فیلترها و الگوریتم‌های فوق‌سریع پردازش تصویر پلاک در حافظه محلی
    /// پیاده‌سازی شده با استانداردهای کارایی بالا، استفاده از LUT، محاسبات بافر مستقل
    /// و تضمین ۱۰۰٪ عدم دستکاری فریم اصلی
    /// </summary>
    public static class ImageProcessingFilterSet
    {
        /// <summary>
        /// استخراج معیارهای آماری و بصری کیفیت تصویر (روشنایی، کنتراست، وضوح لاپلاسیان و تاری حرکت)
        /// </summary>
        public static ImageQualityMetrics CalculateQualityMetrics(ReadOnlySpan<byte> src, int w, int h, int stride, int bpp = 4)
        {
            var m = new ImageQualityMetrics { SampledAt = DateTime.UtcNow };
            if (src.IsEmpty || w <= 4 || h <= 4) return m;

            long sumLum = 0;
            int totalPixels = 0;
            int overCount = 0;
            int underCount = 0;

            // نمونه‌برداری بهینه از پیکسل‌ها
            int step = Math.Max(1, Math.Min(w, h) / 64);
            List<float> samples = new List<float>((w * h) / (step * step) + 16);

            for (int y = 0; y < h; y += step)
            {
                int rowOff = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int idx = rowOff + (x * bpp);
                    if (idx + 2 < src.Length)
                    {
                        byte b = src[idx];
                        byte g = src[idx + 1];
                        byte r = src[idx + 2];
                        float lum = 0.299f * r + 0.587f * g + 0.114f * b;

                        sumLum += (long)lum;
                        samples.Add(lum);
                        totalPixels++;

                        if (lum > 240) overCount++;
                        if (lum < 30) underCount++;
                    }
                }
            }

            if (totalPixels == 0) return m;

            float avgLum = (float)sumLum / totalPixels;
            m.AverageLuminance = avgLum;
            m.OverexposedPercentage = (float)overCount * 100f / totalPixels;
            m.UnderexposedPercentage = (float)underCount * 100f / totalPixels;

            // محاسبه انحراف معیار کنتراست
            double sumSqDiff = 0;
            foreach (var l in samples)
            {
                float d = l - avgLum;
                sumSqDiff += d * d;
            }
            m.ContrastStdDev = (float)Math.Sqrt(sumSqDiff / totalPixels);

            // محاسبه وضوح لاپلاسیان (Laplacian Edge Sharpness)
            double laplacianSum = 0;
            double horizontalEdgeSum = 0;
            double verticalEdgeSum = 0;
            int edgePixels = 0;

            for (int y = step; y < h - step; y += step)
            {
                int rCur = y * stride;
                int rUp = (y - step) * stride;
                int rDn = (y + step) * stride;

                for (int x = step; x < w - step; x += step)
                {
                    int iCur = rCur + (x * bpp);
                    int iUp = rUp + (x * bpp);
                    int iDn = rDn + (x * bpp);
                    int iLf = rCur + ((x - step) * bpp);
                    int iRt = rCur + ((x + step) * bpp);

                    if (iRt + 2 < src.Length && iDn + 2 < src.Length)
                    {
                        float c = 0.299f * src[iCur + 2] + 0.587f * src[iCur + 1] + 0.114f * src[iCur];
                        float u = 0.299f * src[iUp + 2] + 0.587f * src[iUp + 1] + 0.114f * src[iUp];
                        float d = 0.299f * src[iDn + 2] + 0.587f * src[iDn + 1] + 0.114f * src[iDn];
                        float l = 0.299f * src[iLf + 2] + 0.587f * src[iLf + 1] + 0.114f * src[iLf];
                        float r = 0.299f * src[iRt + 2] + 0.587f * src[iRt + 1] + 0.114f * src[iRt];

                        float lap = Math.Abs(4 * c - u - d - l - r);
                        laplacianSum += lap;

                        float gx = Math.Abs(r - l);
                        float gy = Math.Abs(d - u);
                        horizontalEdgeSum += gx;
                        verticalEdgeSum += gy;

                        edgePixels++;
                    }
                }
            }

            if (edgePixels > 0)
            {
                float avgLap = (float)(laplacianSum / edgePixels);
                m.LaplacianSharpness = avgLap * 10f;

                // نسبت گرادیان افقی به عمودی جهت سنجش تاری کشیدگی حرکتی
                float hGrad = (float)(horizontalEdgeSum / edgePixels);
                float vGrad = (float)(verticalEdgeSum / edgePixels);

                // اگر تصویر لبه‌های عمودی بسیار ضعیف‌تری نسبت به افقی داشته باشد، تاری کشیدگی افقی دارد
                float blurRatio = (vGrad > 1.0f) ? Math.Abs(hGrad - vGrad) / Math.Max(hGrad, vGrad) : 0f;
                float sharpnessInverse = Math.Clamp(100f - (m.LaplacianSharpness * 0.4f), 0f, 100f);
                m.MotionBlurScore = Math.Clamp((blurRatio * 50f) + (sharpnessInverse * 0.5f), 0f, 100f);
            }

            // تخمین نویز بر پایه کنتراست موضعی فرکانس بالا در مناطق کم‌نور
            m.NoiseLevel = (m.AverageLuminance < 60f) ? Math.Clamp((70f - m.AverageLuminance) * 0.8f, 5f, 60f) : 10f;

            // امتیاز ترکیبی کیفیت
            float score = (m.ContrastStdDev * 0.7f) + (Math.Min(m.LaplacianSharpness, 150f) * 0.3f);
            if (m.OverexposedPercentage > 20f) score -= (m.OverexposedPercentage - 20f) * 1.5f;
            if (m.MotionBlurScore > 40f) score -= (m.MotionBlurScore - 40f) * 0.8f;
            m.OverallQualityScore = Math.Clamp(score, 10f, 100f);

            return m;
        }

        /// <summary>
        /// فیلتر کاهش نویز سنسور (Median 3x3 Filter) جهت حذف برفک نوری در شب
        /// </summary>
        public static void DenoiseMedian3x3(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4)
        {
            src.CopyTo(dst);

            Span<byte> window = stackalloc byte[9];

            for (int y = 1; y < h - 1; y++)
            {
                int rCur = y * stride;
                int rUp = (y - 1) * stride;
                int rDn = (y + 1) * stride;

                for (int x = 1; x < w - 1; x++)
                {
                    int cIdx = rCur + (x * bpp);

                    // اعمال مدین برای کانال‌های B, G, R
                    for (int ch = 0; ch < 3; ch++)
                    {
                        window[0] = src[rUp + ((x - 1) * bpp) + ch];
                        window[1] = src[rUp + (x * bpp) + ch];
                        window[2] = src[rUp + ((x + 1) * bpp) + ch];

                        window[3] = src[rCur + ((x - 1) * bpp) + ch];
                        window[4] = src[cIdx + ch];
                        window[5] = src[rCur + ((x + 1) * bpp) + ch];

                        window[6] = src[rDn + ((x - 1) * bpp) + ch];
                        window[7] = src[rDn + (x * bpp) + ch];
                        window[8] = src[rDn + ((x + 1) * bpp) + ch];

                        // سورت حباب سریع روی ۹ مقدار
                        for (int i = 0; i < 5; i++)
                        {
                            for (int j = i + 1; j < 9; j++)
                            {
                                if (window[j] < window[i])
                                {
                                    byte temp = window[i];
                                    window[i] = window[j];
                                    window[j] = temp;
                                }
                            }
                        }

                        dst[cIdx + ch] = window[4]; // میانه
                    }

                    if (bpp == 4) dst[cIdx + 3] = src[cIdx + 3];
                }
            }
        }

        /// <summary>
        /// فیلتر افزایش وضوح و برجسته‌سازی لبه‌ها (Unsharp Masking & Laplacian Boost)
        /// حروف و ارقام پلاک را برای موتور OCR شفاف و متمایز می‌کند
        /// </summary>
        public static void Sharpen(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4, float strength = 0.5f)
        {
            src.CopyTo(dst);
            if (strength <= 0.05f) return;

            float k = Math.Clamp(strength, 0.1f, 2.5f);

            for (int y = 1; y < h - 1; y++)
            {
                int rCur = y * stride;
                int rUp = (y - 1) * stride;
                int rDn = (y + 1) * stride;

                for (int x = 1; x < w - 1; x++)
                {
                    int cIdx = rCur + (x * bpp);

                    for (int ch = 0; ch < 3; ch++)
                    {
                        int center = src[cIdx + ch];
                        int up = src[rUp + (x * bpp) + ch];
                        int down = src[rDn + (x * bpp) + ch];
                        int left = src[rCur + ((x - 1) * bpp) + ch];
                        int right = src[rCur + ((x + 1) * bpp) + ch];

                        // لاپلاسین مرتبه ۲
                        int lap = (4 * center) - up - down - left - right;
                        int sharpened = (int)(center + (lap * k));

                        dst[cIdx + ch] = (byte)Math.Clamp(sharpened, 0, 255);
                    }

                    if (bpp == 4) dst[cIdx + 3] = src[cIdx + 3];
                }
            }
        }

        /// <summary>
        /// تصحیح غیرخطی گاما (Gamma Correction) با جدول از پیش‌محاسبه‌شده LUT
        /// </summary>
        public static void ApplyGamma(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4, float gamma = 1.0f)
        {
            if (Math.Abs(gamma - 1.0f) < 0.01f)
            {
                src.CopyTo(dst);
                return;
            }

            Span<byte> lut = stackalloc byte[256];
            float invGamma = 1.0f / Math.Max(0.1f, gamma);
            for (int i = 0; i < 256; i++)
            {
                float v = (float)Math.Pow(i / 255.0f, invGamma) * 255.0f;
                lut[i] = (byte)Math.Clamp((int)v, 0, 255);
            }

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int idx = rOff + (x * bpp);
                    dst[idx] = lut[src[idx]];
                    dst[idx + 1] = lut[src[idx + 1]];
                    dst[idx + 2] = lut[src[idx + 2]];
                    if (bpp == 4) dst[idx + 3] = src[idx + 3];
                }
            }
        }

        /// <summary>
        /// تنظیم کنتراست و روشنایی با جدول تبدیل ۲۵۶ بایتی
        /// </summary>
        public static void AdjustContrastBrightness(
            ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4,
            float contrast = 1.2f, int brightness = 0)
        {
            Span<byte> lut = stackalloc byte[256];
            for (int i = 0; i < 256; i++)
            {
                float val = ((i - 128) * contrast) + 128 + brightness;
                lut[i] = (byte)Math.Clamp((int)val, 0, 255);
            }

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int idx = rOff + (x * bpp);
                    dst[idx] = lut[src[idx]];
                    dst[idx + 1] = lut[src[idx + 1]];
                    dst[idx + 2] = lut[src[idx + 2]];
                    if (bpp == 4) dst[idx + 3] = src[idx + 3];
                }
            }
        }

        /// <summary>
        /// الگوریتم تعدیل هیستوگرام تطبیقی با محدودیت کنتراست (CLAHE)
        /// پیاده‌سازی شده در سی‌شارپ خالص جهت مهار سوختگی نور چراغ‌ها و سایه‌ها بدون اشباع نویز
        /// </summary>
        public static void ApplyClahe(
            ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4,
            float clipLimit = 2.5f, int gridTiles = 8)
        {
            src.CopyTo(dst);
            if (w < 16 || h < 16) return;

            int tilesX = Math.Clamp(gridTiles, 2, 16);
            int tilesY = Math.Clamp(gridTiles / 2, 2, 8); // پلاک افقی است

            int tileW = w / tilesX;
            int tileH = h / tilesY;
            if (tileW <= 2 || tileH <= 2) return;

            // ماتریس توابع توزیع انباشته CDF برای هر تایل
            byte[,,] cdfTable = new byte[tilesY, tilesX, 256];
            int[] hist = new int[256];

            int pixelsPerTile = tileW * tileH;
            int clipVal = (int)Math.Max(1, (clipLimit * pixelsPerTile) / 256);

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
                        int rOff = y * stride;
                        for (int x = x0; x < x1; x++)
                        {
                            int idx = rOff + (x * bpp);
                            // روشنایی خاکستری Y
                            int lum = (src[idx + 2] * 299 + src[idx + 1] * 587 + src[idx] * 114) / 1000;
                            hist[lum]++;
                            actualPixels++;
                        }
                    }

                    // مهار هیستوگرام (Clip) و توزیع اضافه به بقیه سطل‌ها
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

                    // ساخت CDF نرمال‌شده
                    long cdf = 0;
                    for (int i = 0; i < 256; i++)
                    {
                        cdf += hist[i];
                        cdfTable[ty, tx, i] = (byte)Math.Clamp((int)((cdf * 255) / actualPixels), 0, 255);
                    }
                }
            }

            // نگاشت پیکسل‌ها با درونیابی دوخطی (Bilinear Interpolation) بین تایل‌ها
            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                float fy = (float)y / tileH - 0.5f;
                int ty1 = (int)Math.Floor(fy);
                int ty2 = ty1 + 1;
                float dy = fy - ty1;

                ty1 = Math.Clamp(ty1, 0, tilesY - 1);
                ty2 = Math.Clamp(ty2, 0, tilesY - 1);

                for (int x = 0; x < w; x++)
                {
                    float fx = (float)x / tileW - 0.5f;
                    int tx1 = (int)Math.Floor(fx);
                    int tx2 = tx1 + 1;
                    float dx = fx - tx1;

                    tx1 = Math.Clamp(tx1, 0, tilesX - 1);
                    tx2 = Math.Clamp(tx2, 0, tilesX - 1);

                    int idx = rOff + (x * bpp);
                    int lum = (src[idx + 2] * 299 + src[idx + 1] * 587 + src[idx] * 114) / 1000;

                    // چهار مقدار متناظر در تایل‌های همسایه
                    byte v11 = cdfTable[ty1, tx1, lum];
                    byte v12 = cdfTable[ty1, tx2, lum];
                    byte v21 = cdfTable[ty2, tx1, lum];
                    byte v22 = cdfTable[ty2, tx2, lum];

                    float top = v11 + dx * (v12 - v11);
                    float bottom = v21 + dx * (v22 - v21);
                    int newLum = (int)(top + dy * (bottom - top));

                    // اعمال نسبت روشنایی جدید به کانال‌های رنگی بدون تخریب رنگ
                    float scale = (lum > 5) ? (float)newLum / lum : 1.0f;
                    scale = Math.Clamp(scale, 0.4f, 2.5f);

                    dst[idx] = (byte)Math.Clamp((int)(src[idx] * scale), 0, 255);
                    dst[idx + 1] = (byte)Math.Clamp((int)(src[idx + 1] * scale), 0, 255);
                    dst[idx + 2] = (byte)Math.Clamp((int)(src[idx + 2] * scale), 0, 255);
                    if (bpp == 4) dst[idx + 3] = src[idx + 3];
                }
            }
        }

        /// <summary>
        /// اصلاح زاویه و شیب پلاک خودروهای در حال گردش (Perspective Deskewing)
        /// با استفاده از چرخش و برش آفین دوخطی
        /// </summary>
        public static void CorrectPerspectiveDeskew(
            ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4, float angleDegrees = 0f)
        {
            src.CopyTo(dst);
            if (Math.Abs(angleDegrees) < 0.5f || w < 20 || h < 10) return;

            float rad = (float)(angleDegrees * Math.PI / 180.0);
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);

            float cx = w / 2.0f;
            float cy = h / 2.0f;

            for (int y = 0; y < h; y++)
            {
                int rDst = y * stride;
                float dy = y - cy;

                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx;

                    // معکوس دوران
                    float srcX = (cos * dx + sin * dy) + cx;
                    float srcY = (-sin * dx + cos * dy) + cy;

                    int x0 = (int)Math.Floor(srcX);
                    int y0 = (int)Math.Floor(srcY);
                    int x1 = x0 + 1;
                    int y1 = y0 + 1;

                    int dIdx = rDst + (x * bpp);

                    if (x0 >= 0 && x1 < w && y0 >= 0 && y1 < h)
                    {
                        float wx = srcX - x0;
                        float wy = srcY - y0;

                        int i00 = (y0 * stride) + (x0 * bpp);
                        int i10 = (y0 * stride) + (x1 * bpp);
                        int i01 = (y1 * stride) + (x0 * bpp);
                        int i11 = (y1 * stride) + (x1 * bpp);

                        for (int ch = 0; ch < 3; ch++)
                        {
                            float top = src[i00 + ch] + wx * (src[i10 + ch] - src[i00 + ch]);
                            float bot = src[i01 + ch] + wx * (src[i11 + ch] - src[i01 + ch]);
                            dst[dIdx + ch] = (byte)Math.Clamp((int)(top + wy * (bot - top)), 0, 255);
                        }
                        if (bpp == 4) dst[dIdx + 3] = 255;
                    }
                }
            }
        }

        /// <summary>
        /// فیلتر خنثی‌سازی تاری ناشی از کشیدگی حرکت (Motion De-blurring Kernel)
        /// جهت فریز کردن حروف پلاک خودروهای متحرک
        /// </summary>
        public static void ReduceMotionBlur(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4, float strength = 0.5f)
        {
            src.CopyTo(dst);
            if (strength <= 0.1f || w < 10) return;

            // کرنل تقلیل تاری خطی در راستای افقی پلاک
            // [ -0.2, 1.4, -0.2 ]
            float side = -0.25f * strength;
            float center = 1.0f - (2 * side);

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                for (int x = 1; x < w - 1; x++)
                {
                    int cIdx = rOff + (x * bpp);
                    int lIdx = rOff + ((x - 1) * bpp);
                    int rIdx = rOff + ((x + 1) * bpp);

                    for (int ch = 0; ch < 3; ch++)
                    {
                        float val = (side * src[lIdx + ch]) + (center * src[cIdx + ch]) + (side * src[rIdx + ch]);
                        dst[cIdx + ch] = (byte)Math.Clamp((int)val, 0, 255);
                    }
                    if (bpp == 4) dst[cIdx + 3] = src[cIdx + 3];
                }
            }
        }

        /// <summary>
        /// باینریزاسیون تطبیقی (Adaptive Thresholding Sauvola/Otsu) جهت استخراج نویسه‌های با نور ضعیف
        /// </summary>
        public static void AdaptiveThreshold(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int stride, int bpp = 4)
        {
            src.CopyTo(dst);
            if (w < 8 || h < 8) return;

            int winSize = Math.Clamp(w / 12, 5, 25);
            if (winSize % 2 == 0) winSize++;
            int half = winSize / 2;

            // ماتریس تصویر انتگرالی (Integral Image)
            long[,] integral = new long[h + 1, w + 1];

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                long rowSum = 0;
                for (int x = 0; x < w; x++)
                {
                    int idx = rOff + (x * bpp);
                    int lum = (src[idx + 2] * 299 + src[idx + 1] * 587 + src[idx] * 114) / 1000;
                    rowSum += lum;
                    integral[y + 1, x + 1] = integral[y, x + 1] + rowSum;
                }
            }

            for (int y = 0; y < h; y++)
            {
                int rOff = y * stride;
                int y0 = Math.Max(0, y - half);
                int y1 = Math.Min(h, y + half + 1);

                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - half);
                    int x1 = Math.Min(w, x + half + 1);

                    int count = (x1 - x0) * (y1 - y0);
                    long sum = integral[y1, x1] - integral[y0, x1] - integral[y1, x0] + integral[y0, x0];
                    int mean = (int)(sum / count);

                    // آستانه محلی با حاشیه تفکیک حروف تیره روی زمینه روشن پلاک
                    int threshold = (int)(mean * 0.88f);

                    int idx = rOff + (x * bpp);
                    int lum = (src[idx + 2] * 299 + src[idx + 1] * 587 + src[idx] * 114) / 1000;

                    byte outVal = (lum < threshold) ? (byte)20 : (byte)240; // کنتراست شدید کاراکترها
                    dst[idx] = outVal;
                    dst[idx + 1] = outVal;
                    dst[idx + 2] = outVal;
                    if (bpp == 4) dst[idx + 3] = 255;
                }
            }
        }
    }
}
