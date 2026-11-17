using System;
using System.Collections.Generic;

namespace MehrganSurveillance.Models;

public enum WhatsAppProviderType
{
    Ultramsg = 0,
    GreenApi = 1,
    MetaCloudApi = 2,
    CustomWebhook = 3
}

public enum SmsProviderType
{
    Kavenegar = 0,
    SmsIr = 1,
    Melipayamak = 2,
    CustomWebhook = 3
}

public enum NotificationEventKind
{
    LicensePlateRecognized = 0,
    FaceOrHumanDetected = 1
}

/// <summary>
/// پیکربندی سیستم ارسال هشدارهای هوشمند از طریق واتس‌اپ، پیام‌رسان‌ها و پیامک (SMS)
/// توکن‌ها و کلیدهای API به صورت امن با Windows DPAPI رمزنگاری و روی دیسک نگهداری می‌شوند.
/// </summary>
public sealed class NotificationConfig
{
    public bool IsEnabled { get; set; } = false;
    public bool NotifyOnAnpr { get; set; } = true;
    public bool NotifyOnFaceOrHuman { get; set; } = true;
    public bool SendPhotoInMessaging { get; set; } = true;
    public int PerCameraCooldownSeconds { get; set; } = 8;

    // تنظیمات واتس‌اپ
    public bool WhatsAppEnabled { get; set; } = false;
    public WhatsAppProviderType WhatsAppProvider { get; set; } = WhatsAppProviderType.Ultramsg;
    public string WhatsAppRecipients { get; set; } = string.Empty; // با کاما جدا شوند: 0912..., 0935...
    public string WhatsAppInstanceId { get; set; } = string.Empty;
    public string WhatsAppEncryptedToken { get; set; } = string.Empty;
    public string WhatsAppMetaPhoneNumberId { get; set; } = string.Empty;
    public string WhatsAppCustomWebhookUrl { get; set; } = string.Empty;

    // تنظیمات تلگرام / پیام‌رسان
    public bool TelegramEnabled { get; set; } = false;
    public string TelegramBotEncryptedToken { get; set; } = string.Empty;
    public string TelegramChatIds { get; set; } = string.Empty; // با کاما جدا شوند

    // تنظیمات پیامک (SMS)
    public bool SmsEnabled { get; set; } = false;
    public SmsProviderType SmsProvider { get; set; } = SmsProviderType.Kavenegar;
    public string SmsRecipients { get; set; } = string.Empty;
    public string SmsSenderLine { get; set; } = string.Empty;
    public string SmsEncryptedApiKey { get; set; } = string.Empty;
    public string SmsUsername { get; set; } = string.Empty;
    public string SmsEncryptedPassword { get; set; } = string.Empty;
    public string SmsCustomUrlTemplate { get; set; } = string.Empty;
}

/// <summary>
/// اطلاعات رویداد ارسالی به صف اعلان
/// </summary>
public sealed class NotificationEventPayload
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public NotificationEventKind EventKind { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public string CameraName { get; init; } = string.Empty;
    public int ChannelNumber { get; init; } = 1; // ۱-مبنا جهت نمایش به کاربر (دوربین / کانال ۱)
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public float Confidence { get; init; }
    public string? PlateText { get; init; }
    public string? PlateCompact { get; init; }
    public string? OptimizationMethod { get; init; }
    public byte[]? JpegImageBytes { get; init; }
    public string ImageFileName { get; init; } = "event.jpg";
}

/// <summary>
/// سابقه ارسال اعلان برای نمایش در پنجره تنظیمات و عیب‌یابی
/// </summary>
public sealed class NotificationDispatchRecord
{
    public DateTime TimestampLocal { get; init; } = DateTime.Now;
    public string JalaliTimestamp { get; init; } = string.Empty;
    public string ChannelProvider { get; init; } = string.Empty;
    public string EventSummary { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}
