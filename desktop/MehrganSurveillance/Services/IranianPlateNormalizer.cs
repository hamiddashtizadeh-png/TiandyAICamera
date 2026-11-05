using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// سرویس استانداردسازی، اعتبارسنجی و تبدیل ارقام و حروف پلاک‌های ملی ایران
/// </summary>
public static class IranianPlateNormalizer
{
    private static readonly Dictionary<char, char> PersianDigitMap = new()
    {
        ['۰'] = '0', ['۱'] = '1', ['۲'] = '2', ['۳'] = '3', ['۴'] = '4',
        ['۵'] = '5', ['۶'] = '6', ['۷'] = '7', ['۸'] = '8', ['۹'] = '9',
        ['٠'] = '0', ['١'] = '1', ['٢'] = '2', ['٣'] = '3', ['٤'] = '4',
        ['٥'] = '5', ['٦'] = '6', ['٧'] = '7', ['٨'] = '8', ['٩'] = '9'
    };

    private static readonly Dictionary<char, char> LatinToPersianDigitMap = new()
    {
        ['0'] = '۰', ['1'] = '۱', ['2'] = '۲', ['3'] = '۳', ['4'] = '۴',
        ['5'] = '۵', ['6'] = '۶', ['7'] = '۷', ['8'] = '۸', ['9'] = '۹'
    };

    private static readonly Dictionary<string, (IranianPlateLetter Letter, IranianPlateType PlateType, string StandardChar)> KnownLetters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["الف"] = (IranianPlateLetter.Alef, IranianPlateType.Government, "الف"),
        ["ا"] = (IranianPlateLetter.Alef, IranianPlateType.Government, "الف"),
        ["ب"] = (IranianPlateLetter.Be, IranianPlateType.Private, "ب"),
        ["پ"] = (IranianPlateLetter.Pe, IranianPlateType.Police, "پ"),
        ["ت"] = (IranianPlateLetter.Te, IranianPlateType.Taxi, "ت"),
        ["ث"] = (IranianPlateLetter.Se, IranianPlateType.Military, "ث"),
        ["ج"] = (IranianPlateLetter.Jim, IranianPlateType.Military, "ج"),
        ["د"] = (IranianPlateLetter.Dal, IranianPlateType.Private, "د"),
        ["س"] = (IranianPlateLetter.Sin, IranianPlateType.Private, "س"),
        ["ص"] = (IranianPlateLetter.Sad, IranianPlateType.Private, "ص"),
        ["ط"] = (IranianPlateLetter.Ta, IranianPlateType.Private, "ط"),
        ["ع"] = (IranianPlateLetter.Ein, IranianPlateType.Public, "ع"),
        ["ق"] = (IranianPlateLetter.Ghaf, IranianPlateType.Private, "ق"),
        ["ل"] = (IranianPlateLetter.Lam, IranianPlateType.Private, "ل"),
        ["م"] = (IranianPlateLetter.Mim, IranianPlateType.Private, "م"),
        ["ن"] = (IranianPlateLetter.Noon, IranianPlateType.Private, "ن"),
        ["و"] = (IranianPlateLetter.Vav, IranianPlateType.Private, "و"),
        ["ه"] = (IranianPlateLetter.He, IranianPlateType.Private, "ه"),
        ["ی"] = (IranianPlateLetter.Ye, IranianPlateType.Private, "ی"),
        ["ي"] = (IranianPlateLetter.Ye, IranianPlateType.Private, "ی"),
        ["ژ"] = (IranianPlateLetter.Malulin, IranianPlateType.Disabled, "ژ"),
        ["معلولین"] = (IranianPlateLetter.Malulin, IranianPlateType.Disabled, "ژ"),
        ["ویلچر"] = (IranianPlateLetter.Malulin, IranianPlateType.Disabled, "ژ"),
        ["ک"] = (IranianPlateLetter.Kaf, IranianPlateType.Public, "ک"),
        ["ك"] = (IranianPlateLetter.Kaf, IranianPlateType.Public, "ک"),
        ["ش"] = (IranianPlateLetter.Shin, IranianPlateType.Military, "ش"),
        ["ز"] = (IranianPlateLetter.Ze, IranianPlateType.Military, "ز")
    };

    // الگوی استاندارد پلاک ملی ایران: ۲ رقم + حرف + ۳ رقم + ۲ رقم کد استان
    private static readonly Regex PlateStandardRegex = new(
        @"^(\d{2})\s*([ابپتثجچحخدذرزژسشصضطظعغفقکگلمنوهی]|معلولین|ویلچر|الف)\s*(\d{3})\s*(?:ایران|IRAN|-)?\s*(\d{2})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// تبدیل کلیه ارقام فارسی و عربی به ارقام انگلیسی لاتین
    /// </summary>
    public static string ToLatinDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (PersianDigitMap.TryGetValue(c, out char latin))
            {
                sb.Append(latin);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// تبدیل کلیه ارقام انگلیسی به ارقام زیبای فارسی
    /// </summary>
    public static string ToPersianDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (LatinToPersianDigitMap.TryGetValue(c, out char fa))
            {
                sb.Append(fa);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// نرمال‌سازی و استخراج ساختار پلاک ایرانی از خروجی خام OCR
    /// </summary>
    public static IranianPlate ParseAndNormalize(string rawOcr, float ocrConfidence = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(rawOcr))
        {
            return IranianPlate.CreateInvalid(rawOcr ?? string.Empty, AnprValidationStatus.Failed);
        }

        // ۱. حذف کاراکترهای زائد و تبدیل ارقام به لاتین جهت پردازش رگولار اکسپرشن
        string cleaned = ToLatinDigits(rawOcr.Trim())
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("،", " ")
            .Replace("/", " ");

        // حذف فاصله‌های متوالی
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        var match = PlateStandardRegex.Match(cleaned);
        if (match.Success)
        {
            string part1 = match.Groups[1].Value;
            string letterRaw = match.Groups[2].Value;
            string part2 = match.Groups[3].Value;
            string iranCode = match.Groups[4].Value;

            var (letter, plateType, standardChar) = ResolveLetter(letterRaw);

            string compact = $"{part1}{standardChar}{part2}-{iranCode}";
            string normalized = $"{ToPersianDigits(part1)} {standardChar} {ToPersianDigits(part2)} ایران {ToPersianDigits(iranCode)}";

            AnprValidationStatus status = ocrConfidence >= 0.75f
                ? AnprValidationStatus.Verified
                : AnprValidationStatus.Uncertain;

            return new IranianPlate
            {
                Part1 = part1,
                Letter = letter,
                LetterText = standardChar,
                Part2 = part2,
                IranCode = iranCode,
                PlateType = plateType,
                RawOcr = rawOcr,
                NormalizedText = normalized,
                CompactText = compact,
                IsValid = true,
                ValidationStatus = status
            };
        }

        // ۲. تلاش برای استخراج حالت ناقص (Partial) بدون جعل پلاک
        return TryParsePartial(rawOcr, cleaned, ocrConfidence);
    }

    private static (IranianPlateLetter Letter, IranianPlateType PlateType, string StandardChar) ResolveLetter(string letterRaw)
    {
        string trimmed = letterRaw.Trim();
        if (KnownLetters.TryGetValue(trimmed, out var info))
        {
            return info;
        }

        return (IranianPlateLetter.Unknown, IranianPlateType.Private, trimmed);
    }

    private static IranianPlate TryParsePartial(string rawOcr, string cleaned, float ocrConfidence)
    {
        // استخراج هر ترتیبی از ارقام و حروف برای ذخیره‌سازی دقیق و بدون دخل و تصرف
        var digitsOnly = Regex.Replace(cleaned, @"[^\d]", "");
        var lettersMatch = Regex.Match(cleaned, @"[ابپتثجچحخدذرزژسشصضطظعغفقکگلمنوهی]");

        string letterChar = lettersMatch.Success ? lettersMatch.Value : string.Empty;
        var (letter, plateType, standardChar) = ResolveLetter(letterChar);

        // اگر دست‌کم ۵ یا ۶ رقم خوانده شده باشد، با برچسب صریح Partial علامت‌گذاری می‌شود
        if (digitsOnly.Length is >= 5 and <= 8)
        {
            string part1 = digitsOnly.Length >= 2 ? digitsOnly[..2] : digitsOnly;
            string part2 = digitsOnly.Length >= 5 ? digitsOnly.Substring(2, Math.Min(3, digitsOnly.Length - 2)) : string.Empty;
            string iran = digitsOnly.Length >= 7 ? digitsOnly.Substring(5, Math.Min(2, digitsOnly.Length - 5)) : string.Empty;

            string compact = $"{part1}{standardChar}{part2}{(string.IsNullOrEmpty(iran) ? "" : $"-{iran}")}";
            string normalized = $"{ToPersianDigits(part1)} {standardChar} {ToPersianDigits(part2)} {(string.IsNullOrEmpty(iran) ? "[ناقص]" : $"ایران {ToPersianDigits(iran)}")}";

            return new IranianPlate
            {
                Part1 = part1,
                Letter = letter,
                LetterText = standardChar,
                Part2 = part2,
                IranCode = iran,
                PlateType = plateType,
                RawOcr = rawOcr,
                NormalizedText = normalized,
                CompactText = compact,
                IsValid = false,
                ValidationStatus = AnprValidationStatus.Partial
            };
        }

        return IranianPlate.CreateInvalid(rawOcr, AnprValidationStatus.Uncertain);
    }

    /// <summary>
    /// بررسی انطباق جستجوی متنی (کامل یا بخشی از پلاک) با پشتیبانی از ارقام فارسی و انگلیسی
    /// </summary>
    public static bool MatchesSearch(IranianPlate plate, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (plate == null) return false;

        string normalizedQuery = ToLatinDigits(query.Trim())
            .Replace(" ", "")
            .Replace("-", "")
            .ToLowerInvariant();

        string compact = plate.CompactText
            .Replace(" ", "")
            .Replace("-", "")
            .ToLowerInvariant();

        string raw = ToLatinDigits(plate.RawOcr ?? "")
            .Replace(" ", "")
            .Replace("-", "")
            .ToLowerInvariant();

        string normalizedFa = ToLatinDigits(plate.NormalizedText ?? "")
            .Replace(" ", "")
            .Replace("-", "")
            .ToLowerInvariant();

        // تطبیق مستقیم زیررشته
        if (compact.Contains(normalizedQuery) ||
            raw.Contains(normalizedQuery) ||
            normalizedFa.Contains(normalizedQuery))
        {
            return true;
        }

        // تطبیق بر روی بخش‌های تفکیک‌شده (مثلاً جستجوی فقط کد استان یا ۳ رقم میانی)
        if (!string.IsNullOrEmpty(plate.Part1) && plate.Part1.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(plate.Part2) && plate.Part2.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(plate.IranCode) && plate.IranCode.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(plate.LetterText) && plate.LetterText.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
