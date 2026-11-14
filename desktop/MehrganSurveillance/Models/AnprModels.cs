using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

/// <summary>
/// حروف استاندارد پلاک‌های وسایل نقلیه در ایران
/// </summary>
public enum IranianPlateLetter
{
    Alef = 1,     // الف - دولتی
    Be = 2,       // ب - شخصی
    Pe = 3,       // پ - پلیس
    Te = 4,       // ت - تاکسی
    Se = 5,       // ث - سپاه
    Jim = 6,      // ج - ارتش / ستاد کل
    Dal = 7,      // د - دیپلمات
    Sin = 8,      // س - شخصی
    Sad = 9,      // ص - شخصی
    Ta = 10,      // ط - شخصی
    Ein = 11,     // ع - عمومی
    Ghaf = 12,    // ق - شخصی
    Lam = 13,     // ل - شخصی
    Mim = 14,     // م - شخصی
    Noon = 15,    // ن - شخصی
    Vav = 16,     // و - شخصی
    He = 17,      // ه - شخصی
    Ye = 18,      // ی - شخصی
    Malulin = 19, // ژ / معلولین و جانبازان
    Kaf = 20,     // ک - کشاورزی
    Shin = 21,    // ش - ارتش
    Ze = 22,      // ز - وزارت دفاع
    Unknown = 99  // ناشناخته / ناخوانا
}

/// <summary>
/// طبقه‌بندی پلاک‌های ایرانی بر اساس رنگ و نوع کاربری
/// </summary>
public enum IranianPlateType
{
    Private,    // شخصی - پس‌زمینه سفید با حروف مشکی
    Taxi,       // تاکسی - پس‌زمینه زرد با حرف ت مشکی
    Public,     // عمومی - پس‌زمینه زرد با حرف ع مشکی
    Government, // دولتی - پس‌زمینه قرمز با حرف الف سفید
    Police,     // پلیس - پس‌زمینه سبز با حرف پ سفید
    Military,   // نظامی - پس‌زمینه خاکی/آبی با حروف مشکی/سفید
    Disabled,   // معلولین و جانبازان - پس‌زمینه سفید با نشان ویلچر آبی
    Historic,   // تاریخی و کلاسیک - پس‌زمینه قهوه‌ای
    Transit,    // گذر موقت - سفید با کادر مشخص
    Unknown     // نامشخص
}

/// <summary>
/// وضعیت اعتبارسنجی پلاک خوانده شده
/// </summary>
public enum AnprValidationStatus
{
    Verified,       // کاملاً منطبق بر ساختار رسمی پلاک ایران و ضریب اطمینان بالا
    Partial,        // پلاک ناقص (مثلاً عدم تشخیص ۲ رقم کد استان یا حرف)
    Uncertain,      // دارای ابهام یا ضریب اطمینان OCR کمتر از آستانه مجاز
    LowConfidence,  // ضریب اطمینان تشخیص کلی پایین
    Failed          // غیرقابل بازشناسی یا خراب
}

/// <summary>
/// ساختار تفکیک‌شده پلاک ملی ایران
/// </summary>
public sealed record IranianPlate
{
    public string Part1 { get; init; } = string.Empty; // ۲ رقم ابتدایی (مثلاً "12")
    public IranianPlateLetter Letter { get; init; } = IranianPlateLetter.Unknown;
    public string LetterText { get; init; } = string.Empty; // حرف فارسی (مثلاً "ب")
    public string Part2 { get; init; } = string.Empty; // ۳ رقم میانی (مثلاً "345")
    public string IranCode { get; init; } = string.Empty; // ۲ رقم کد استان (مثلاً "67")
    public IranianPlateType PlateType { get; init; } = IranianPlateType.Private;
    public string RawOcr { get; init; } = string.Empty; // متن خام استخراج شده از موتور OCR
    public string NormalizedText { get; init; } = string.Empty; // متن استاندارد فارسی (مثلاً "۱۲ ب ۳۴۵ ایران ۶۷")
    public string CompactText { get; init; } = string.Empty; // فرمت فشرده جهت جستجوی سریع (مثلاً "12ب345-67")
    public bool IsValid { get; init; }
    public AnprValidationStatus ValidationStatus { get; init; } = AnprValidationStatus.Uncertain;

    public static IranianPlate CreateInvalid(string rawOcr, AnprValidationStatus status = AnprValidationStatus.Failed)
    {
        return new IranianPlate
        {
            RawOcr = rawOcr,
            IsValid = false,
            ValidationStatus = status,
            NormalizedText = string.IsNullOrWhiteSpace(rawOcr) ? "ناخوانا" : rawOcr,
            CompactText = rawOcr.Trim()
        };
    }
}

/// <summary>
/// مشخصات کادر محاطی شی یا پلاک در تصویر
/// </summary>
public sealed record NormalizedBoundingBox(float X, float Y, float Width, float Height)
{
    public bool IsValid =>
        X >= 0.0f && Y >= 0.0f &&
        Width > 0.0f && Height > 0.0f &&
        X + Width <= 1.0f && Y + Height <= 1.0f;
}

/// <summary>
/// نتیجه خروجی پایپ‌لاین تشخیص و بازشناسی پلاک (ANPR Event Record)
/// </summary>
public sealed record AnprResult
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string CameraId { get; init; } = string.Empty;
    public int ChannelIndex { get; init; } = 1;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public IranianPlate Plate { get; init; } = IranianPlate.CreateInvalid(string.Empty);
    public float PlateDetectionConfidence { get; init; }
    public float OcrConfidence { get; init; }
    public NormalizedBoundingBox? VehicleBox { get; init; }
    public NormalizedBoundingBox? PlateBox { get; init; }
    public byte[]? VehicleCropJpeg { get; init; }
    public byte[]? PlateCropJpeg { get; init; }
    public byte[]? OriginalPlateCropJpeg { get; init; }
    public string OptimizationMethod { get; init; } = "Original Baseline";
    public float OptimizationConfidenceGain { get; init; } = 0.0f;
    public string? EncryptedVehicleCropPath { get; init; }
    public string? EncryptedPlateCropPath { get; init; }
    public double InferenceDurationMs { get; init; }
    public string ModelInfo { get; init; } = "YOLOv8-ANPR-IR-v1.0";
    public string ExecutionProvider { get; init; } = "CPU";
}

/// <summary>
/// تنظیمات خط لوله پلاک‌خوان هوشمند
/// </summary>
public sealed class AnprConfig
{
    public bool IsEnabled { get; set; } = false; // غیرفعال به صورت پیش‌فرض برای امنیت
    public float MinDetectionConfidence { get; set; } = 0.65f;
    public float MinOcrConfidence { get; set; } = 0.70f;
    public double DeduplicationWindowSeconds { get; set; } = 4.0; // پنجره زمانی جلوگیری از ثبت تکراری
    public int MaxQueueCapacity { get; set; } = 25;
    public bool EnablePerspectiveCorrection { get; set; } = true;
    public string ModelDirectory { get; set; } = "Models/ANPR";
    public string DetectorModelPath { get; set; } = "Models/ANPR/yolov8n-license-plate.onnx";
    public string OcrModelPath { get; set; } = "Models/ANPR/iranian-plate-ocr.onnx";
    public string DatabasePath { get; set; } = "archive/anpr_events.db";
    public HashSet<string> ActiveCameraIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int MaxAdaptiveFps { get; set; } = 10; // حداکثر فریم سمپلینگ برای کنترل بار پردازشی
}

/// <summary>
/// وضعیت بارگذاری و سلامت مدل‌های هوش مصنوعی ANPR
/// </summary>
public sealed record AnprModelStatus
{
    public bool DetectorLoaded { get; init; }
    public bool OcrLoaded { get; init; }
    public string DetectorPath { get; init; } = string.Empty;
    public string OcrPath { get; init; } = string.Empty;
    public string ExecutionProvider { get; init; } = "None";
    public string StatusMessage { get; init; } = "آماده‌سازی نشده";
    public bool IsReady => DetectorLoaded && OcrLoaded;
}

/// <summary>
/// پارسر و تطبیق‌دهنده گرامر پلاک ملی ایران
/// </summary>
public static class IranianPlateParser
{
    public static IranianPlate Parse(string rawText, float confidence = 0.90f)
    {
        return Services.IranianPlateNormalizer.ParseAndNormalize(rawText, confidence);
    }
}
