using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// پریست‌های بهینه‌سازی پردازش تصویر مخصوص شرایط محیطی مختلف
    /// </summary>
    public enum ImageOptimizationPreset
    {
        Daylight,            // روشنایی روز - افزایش کنتراست متعادل و شارپنس لبه‌ها
        Night,               // شبانه - مهار نور شدید چراغ خودرو (HLC) و اعمال CLAHE عمیق
        LowLight,            // نور ضعیف و سایه - جبران گاما، روشنایی و حذف نویز سنسور
        FastMovingVehicles,  // خودروهای پرسرعت - فیلتر خنثی‌سازی تاری کشیدگی حرکتی (De-blur) و شارپ‌سازی
        GeneralAnpr,         // عمومی پلاک‌خوان - تنظیمات متوازن کارخانه‌ای
        Custom               // سفارشی - تنظیمات دستی اپراتور
    }

    /// <summary>
    /// معیارهای عددی و آماری کیفیت تصویر پلاک
    /// </summary>
    public class ImageQualityMetrics
    {
        public DateTime SampledAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// میانگین روشنایی پیکسل‌ها (۰ تا ۲۵۵)
        /// </summary>
        public float AverageLuminance { get; set; } = 128f;

        /// <summary>
        /// انحراف معیار کنتراست (۰ تا ۱۰۰)
        /// </summary>
        public float ContrastStdDev { get; set; } = 40f;

        /// <summary>
        /// شاخص وضوح و شارپنس لبه‌ها بر پایه واریانس گرادیان لاپلاسین (Laplacian Sharpness)
        /// هرچه بیشتر باشد لبه‌های کاراکترهای پلاک شفاف‌ترند
        /// </summary>
        public float LaplacianSharpness { get; set; } = 150f;

        /// <summary>
        /// شاخص تخمینی تاری ناشی از حرکت خودرو (۰ تا ۱۰۰)
        /// مقادیر بالای ۵۰ نشان‌دهنده کشیدگی حرکتی در سرعت بالا هستند
        /// </summary>
        public float MotionBlurScore { get; set; } = 15f;

        /// <summary>
        /// تراز تخمینی نویز برفکی سنسور (۰ تا ۱۰۰)
        /// </summary>
        public float NoiseLevel { get; set; } = 10f;

        /// <summary>
        /// درصد پیکسل‌های سوخته ناشی از تابش مستقیم چراغ خودرو (Luminance > 240)
        /// </summary>
        public float OverexposedPercentage { get; set; } = 0f;

        /// <summary>
        /// درصد پیکسل‌های به شدت تاریک (Luminance < 30)
        /// </summary>
        public float UnderexposedPercentage { get; set; } = 0f;

        /// <summary>
        /// امتیاز کلی کیفیت خوانایی پلاک (۰ تا ۱۰۰)
        /// </summary>
        public float OverallQualityScore { get; set; } = 75f;

        /// <summary>
        /// آیا تصویر دچار سوختگی نوری ناشی از چراغ خودرو است؟
        /// </summary>
        public bool IsGlareSevere => OverexposedPercentage > 15f;

        /// <summary>
        /// آیا تصویر دارای کشیدگی حرکتی خودرو است؟
        /// </summary>
        public bool IsMotionBlurred => MotionBlurScore > 45f;
    }

    /// <summary>
    /// پروفایل بهینه‌سازی پردازش تصویر اختصاصی برای هر دوربین
    /// ذخیره‌شده به صورت پایدار و مستقل از تنظیمات فیزیکی دوربین
    /// </summary>
    public class CameraImageOptimizationProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
        public string CameraId { get; set; } = string.Empty;
        public string CameraName { get; set; } = "دوربین پیش‌فرض";
        public ImageOptimizationPreset Preset { get; set; } = ImageOptimizationPreset.Night;

        /// <summary>
        /// شدت فیلتر حذف نویز سنسور (۰ تا ۱۰۰)
        /// </summary>
        public int DenoiseStrength { get; set; } = 25;

        /// <summary>
        /// شدت افزایش وضوح و شارپنس کاراکترها (۰ تا ۱۰۰)
        /// </summary>
        public int SharpenStrength { get; set; } = 45;

        /// <summary>
        /// سقف مهار هیستوگرام موضعی در CLAHE (بین ۱.۰ تا ۵.۰)
        /// </summary>
        public float ClaheClipLimit { get; set; } = 2.8f;

        /// <summary>
        /// ابعاد شبکه کاشی‌های CLAHE (بین ۲ تا ۱۶)
        /// </summary>
        public int ClaheGridSize { get; set; } = 8;

        /// <summary>
        /// ضریب تصحیح گاما (بین ۰.۲ تا ۲.۵) - کمتر از ۱ جهت مهار نور چراغ، بیشتر از ۱ جهت روشن‌سازی تاریکی
        /// </summary>
        public float Gamma { get; set; } = 0.85f;

        /// <summary>
        /// ضریب کنتراست خطی (بین ۰.۵ تا ۲.۵)
        /// </summary>
        public float Contrast { get; set; } = 1.35f;

        /// <summary>
        /// تصحیح روشنایی (-۱۰۰ تا +۱۰۰)
        /// </summary>
        public int Brightness { get; set; } = -5;

        /// <summary>
        /// فعال‌سازی تصحیح پرسپکتیو و تراز کردن زاویه چرخش پلاک خودروهای در حال گردش
        /// </summary>
        public bool PerspectiveCorrectionEnabled { get; set; } = true;

        /// <summary>
        /// فعال‌سازی باینریزاسیون تطبیقی جهت جداسازی کاراکترهای با کنتراست ضعیف
        /// </summary>
        public bool AdaptiveThresholdEnabled { get; set; } = false;

        /// <summary>
        /// فعال‌سازی فیلتر سوپر رزولوشن بازسازی لبه‌های پلاک‌های دوردست
        /// </summary>
        public bool SuperResolutionEnabled { get; set; } = false;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// ایجاد یک کپی مستقل از پروفایل
        /// </summary>
        public CameraImageOptimizationProfile Clone()
        {
            return new CameraImageOptimizationProfile
            {
                ProfileId = this.ProfileId,
                CameraId = this.CameraId,
                CameraName = this.CameraName,
                Preset = this.Preset,
                DenoiseStrength = this.DenoiseStrength,
                SharpenStrength = this.SharpenStrength,
                ClaheClipLimit = this.ClaheClipLimit,
                ClaheGridSize = this.ClaheGridSize,
                Gamma = this.Gamma,
                Contrast = this.Contrast,
                Brightness = this.Brightness,
                PerspectiveCorrectionEnabled = this.PerspectiveCorrectionEnabled,
                AdaptiveThresholdEnabled = this.AdaptiveThresholdEnabled,
                SuperResolutionEnabled = this.SuperResolutionEnabled,
                UpdatedAtUtc = this.UpdatedAtUtc
            };
        }

        /// <summary>
        /// اعمال مقادیر از پیش‌تعیین‌شده بر اساس نوع پریست
        /// </summary>
        public static CameraImageOptimizationProfile CreatePreset(string cameraId, string cameraName, ImageOptimizationPreset preset)
        {
            var p = new CameraImageOptimizationProfile
            {
                CameraId = cameraId,
                CameraName = cameraName,
                Preset = preset,
                UpdatedAtUtc = DateTime.UtcNow
            };

            switch (preset)
            {
                case ImageOptimizationPreset.Daylight:
                    p.DenoiseStrength = 10;
                    p.SharpenStrength = 35;
                    p.ClaheClipLimit = 2.0f;
                    p.ClaheGridSize = 8;
                    p.Gamma = 1.0f;
                    p.Contrast = 1.15f;
                    p.Brightness = 0;
                    p.PerspectiveCorrectionEnabled = true;
                    p.AdaptiveThresholdEnabled = false;
                    break;

                case ImageOptimizationPreset.Night:
                    // مهار نور خیره‌کننده چراغ‌ها و تفکیک ارقام پلاک در تاریکی
                    p.DenoiseStrength = 30;
                    p.SharpenStrength = 55;
                    p.ClaheClipLimit = 3.2f;
                    p.ClaheGridSize = 8;
                    p.Gamma = 0.80f; // تضعیف تابش شدید کاسه چراغ
                    p.Contrast = 1.45f;
                    p.Brightness = -10;
                    p.PerspectiveCorrectionEnabled = true;
                    p.AdaptiveThresholdEnabled = true;
                    break;

                case ImageOptimizationPreset.LowLight:
                    // محیط‌های بسیار تاریک
                    p.DenoiseStrength = 45;
                    p.SharpenStrength = 40;
                    p.ClaheClipLimit = 3.5f;
                    p.ClaheGridSize = 8;
                    p.Gamma = 1.35f; // بالا بردن روشنایی سایه‌ها
                    p.Contrast = 1.30f;
                    p.Brightness = 15;
                    p.PerspectiveCorrectionEnabled = true;
                    p.AdaptiveThresholdEnabled = false;
                    break;

                case ImageOptimizationPreset.FastMovingVehicles:
                    // خودروهای شتاب‌دار و بزرگراهی - تمرکز بر De-blur و شارپ‌سازی تهاجمی
                    p.DenoiseStrength = 15;
                    p.SharpenStrength = 70;
                    p.ClaheClipLimit = 2.5f;
                    p.ClaheGridSize = 8;
                    p.Gamma = 0.95f;
                    p.Contrast = 1.40f;
                    p.Brightness = 0;
                    p.PerspectiveCorrectionEnabled = true;
                    p.AdaptiveThresholdEnabled = false;
                    break;

                case ImageOptimizationPreset.GeneralAnpr:
                default:
                    p.DenoiseStrength = 20;
                    p.SharpenStrength = 40;
                    p.ClaheClipLimit = 2.5f;
                    p.ClaheGridSize = 8;
                    p.Gamma = 1.0f;
                    p.Contrast = 1.25f;
                    p.Brightness = 0;
                    p.PerspectiveCorrectionEnabled = true;
                    p.AdaptiveThresholdEnabled = false;
                    break;
            }

            return p;
        }
    }

    /// <summary>
    /// نامزد پردازش‌شده از تصویر پلاک (یکی از نسخه‌های پردازش‌شده جهت ارزیابی در OCR)
    /// </summary>
    public class ImageOptimizationCandidate
    {
        public string CandidateId { get; set; } = Guid.NewGuid().ToString("N");
        public string FilterName { get; set; } = "Original";
        public string Description { get; set; } = "تصویر برش اولیه بدون پردازش";

        /// <summary>
        /// بافر مستقل پردازش‌شده (BGRA یا BGR)
        /// </summary>
        public byte[] PixelData { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }

        public byte[]? JpegBytes { get; set; }
        public ImageQualityMetrics Metrics { get; set; } = new();

        public string OcrRawText { get; set; } = string.Empty;
        public float OcrConfidence { get; set; } = 0.0f;
        public IranianPlate Plate { get; set; } = IranianPlate.CreateInvalid(string.Empty);

        /// <summary>
        /// امتیاز ترکیبی شایستگی جهت انتخاب به عنوان خروجی نهایی
        /// </summary>
        public float CompositeScore { get; set; } = 0.0f;
        public bool IsSelectedAsBest { get; set; } = false;
    }

    /// <summary>
    /// نتیجه نهایی اجرای موتور بهینه‌سازی تصویر شامل مقایسه قبل و بعد و ارزیابی OCR
    /// </summary>
    public class ImageOptimizationResult
    {
        public Guid ResultId { get; set; } = Guid.NewGuid();
        public string CameraId { get; set; } = string.Empty;
        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// مشخصات و کیفیت تصویر قبل از پردازش
        /// </summary>
        public ImageQualityMetrics MetricsBefore { get; set; } = new();
        public string OcrTextBefore { get; set; } = string.Empty;
        public float OcrConfidenceBefore { get; set; } = 0.0f;
        public byte[]? OriginalCropJpeg { get; set; }

        /// <summary>
        /// بهترین نسخه انتخابی پس از پردازش و رقابت کاندیداها
        /// </summary>
        public ImageOptimizationCandidate BestCandidate { get; set; } = new();

        /// <summary>
        /// تمامی نسخه‌های تولیدشده محدود جهت ارزیابی و ممیزی
        /// </summary>
        public List<ImageOptimizationCandidate> EvaluatedCandidates { get; set; } = new();

        /// <summary>
        /// نام فیلتر یا متد برنده
        /// </summary>
        public string WinningMethod { get; set; } = "Original";

        /// <summary>
        /// درصد بهبود ضریب اطمینان OCR نسبت به تصویر خام (+X%)
        /// </summary>
        public float ConfidenceImprovementPercentage { get; set; } = 0.0f;

        /// <summary>
        /// مدت زمان اجرای کامل پردازش بر حسب میلی‌ثانیه
        /// </summary>
        public double ProcessingDurationMs { get; set; } = 0.0;
    }
}
