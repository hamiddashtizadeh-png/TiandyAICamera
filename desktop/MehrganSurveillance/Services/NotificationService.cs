using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// سرویس جامع ارسال هشدارهای هوشمند (واتس‌اپ، تلگرام و پیامک SMS)
/// شامل: پیام متنی فارسی + تاریخ شمسی (Jalali) + ساعت دقیق + شماره و نام دوربین + ارسال تصویر واقعی خودرو/پلاک یا پرتره چهره
/// </summary>
public sealed class NotificationService : IAsyncDisposable, IDisposable
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;
    private readonly Channel<NotificationEventPayload> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<NotificationDispatchRecord> _history = new();
    private readonly object _configLock = new();

    private NotificationConfig _config = new();
    private Task? _workerTask;
    private bool _isDisposed;

    public event Action<string>? LogMessage;
    public event Action<NotificationDispatchRecord>? NotificationDispatched;

    public NotificationConfig Config
    {
        get { lock (_configLock) return _config; }
    }

    public NotificationService(string? customConfigPath = null)
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MehrganSurveillance");
        Directory.CreateDirectory(appData);
        _configPath = customConfigPath ?? Path.Combine(appData, "notification_config.json");

        LoadConfig();

        _queue = Channel.CreateBounded<NotificationEventPayload>(new BoundedChannelOptions(120)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public void LoadConfig()
    {
        lock (_configLock)
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<NotificationConfig>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _config = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"خطا در بارگذاری تنظیمات اعلان: {ex.Message}");
            }

            _config = new NotificationConfig();
        }
    }

    public void SaveConfig(NotificationConfig updated)
    {
        if (updated == null) throw new ArgumentNullException(nameof(updated));

        lock (_configLock)
        {
            _config = updated;
            string json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(_configPath, json, Encoding.UTF8);
        }

        Log("تنظیمات سیستم ارسال اعلان (واتس‌اپ / تلگرام / SMS) ذخیره و فعال‌سازی شد.");
    }

    public List<NotificationDispatchRecord> GetRecentHistory()
    {
        return _history.Reverse().ToList();
    }

    /// <summary>
    /// افزودن رویداد پلاک‌خوان تاییدشده به صف ارسال پیام‌رسان و پیامک
    /// </summary>
    public bool EnqueueAnprNotification(AnprResult result, string cameraName, byte[]? vehicleOrPlateJpeg)
    {
        if (result == null || !result.Plate.IsValid) return false;

        var cfg = Config;
        if (!cfg.IsEnabled || !cfg.NotifyOnAnpr) return false;
        if (!HasAnyActiveChannel(cfg)) return false;

        string dedupKey = $"ANPR:{result.CameraId}:{result.Plate.CompactText}";
        if (IsInCooldown(dedupKey, cfg.PerCameraCooldownSeconds)) return false;

        var payload = new NotificationEventPayload
        {
            EventKind = NotificationEventKind.LicensePlateRecognized,
            CameraId = result.CameraId,
            CameraName = string.IsNullOrWhiteSpace(cameraName) ? $"دوربین {result.ChannelIndex + 1}" : cameraName,
            ChannelNumber = result.ChannelIndex + 1,
            TimestampUtc = result.TimestampUtc,
            Confidence = result.OcrConfidence,
            PlateText = result.Plate.NormalizedText,
            PlateCompact = result.Plate.CompactText,
            OptimizationMethod = result.OptimizationMethod,
            JpegImageBytes = cfg.SendPhotoInMessaging ? vehicleOrPlateJpeg : null,
            ImageFileName = $"plate_{result.Plate.CompactText}_{DateTime.Now:HHmmss}.jpg"
        };

        return _queue.Writer.TryWrite(payload);
    }

    /// <summary>
    /// افزودن رویداد بهترین تصویر ثبت‌شده از حضور شخص/چهره به صف ارسال پیام‌رسان و پیامک
    /// </summary>
    public bool EnqueueFaceNotification(
        string cameraId,
        int channelIndex,
        string cameraName,
        DateTime timestampUtc,
        float confidence,
        byte[]? bestFaceJpeg)
    {
        var cfg = Config;
        if (!cfg.IsEnabled || !cfg.NotifyOnFaceOrHuman) return false;
        if (!HasAnyActiveChannel(cfg)) return false;

        string dedupKey = $"FACE:{cameraId}";
        if (IsInCooldown(dedupKey, cfg.PerCameraCooldownSeconds)) return false;

        var payload = new NotificationEventPayload
        {
            EventKind = NotificationEventKind.FaceOrHumanDetected,
            CameraId = cameraId,
            CameraName = string.IsNullOrWhiteSpace(cameraName) ? $"دوربین {channelIndex + 1}" : cameraName,
            ChannelNumber = channelIndex + 1,
            TimestampUtc = timestampUtc,
            Confidence = confidence,
            JpegImageBytes = cfg.SendPhotoInMessaging ? bestFaceJpeg : null,
            ImageFileName = $"face_ch{channelIndex + 1}_{DateTime.Now:HHmmss}.jpg"
        };

        return _queue.Writer.TryWrite(payload);
    }

    /// <summary>
    /// ارسال تست زنده به کانال‌های پیکربندی‌شده همراه با تصویر آزمایشی و متادیتای کامل فارسی
    /// </summary>
    public async Task<(bool OverallSuccess, string SummaryReport)> SendTestNotificationAsync(NotificationConfig? overrideConfig = null, CancellationToken ct = default)
    {
        var cfg = overrideConfig ?? Config;
        if (!HasAnyActiveChannel(cfg))
        {
            return (false, "هیچ کانال فعالی (واتس‌اپ، تلگرام یا SMS) انتخاب یا پیکربندی نشده است.");
        }

        byte[]? testJpeg = CreateTestBannerJpeg();
        var testPayload = new NotificationEventPayload
        {
            EventKind = NotificationEventKind.LicensePlateRecognized,
            CameraId = "test-cam-01",
            CameraName = "دوربین آزمایشی ورودی",
            ChannelNumber = 1,
            TimestampUtc = DateTime.UtcNow,
            Confidence = 0.96f,
            PlateText = "۱۲ ب ۳۴۵ ایران ۶۷",
            PlateCompact = "12ب345-67",
            OptimizationMethod = "CLAHE + Sharpen",
            JpegImageBytes = cfg.SendPhotoInMessaging ? testJpeg : null,
            ImageFileName = "mehrgan_test_alert.jpg"
        };

        var results = await DispatchPayloadInternalAsync(testPayload, cfg, ct);
        if (results.Count == 0)
        {
            return (false, "گیرنده یا توکن معتبری برای ارسال تست وارد نشده است.");
        }

        bool anySuccess = results.Any(r => r.Success);
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"{(r.Success ? "✅" : "❌")} [{r.ChannelProvider}] {r.Recipient}: {r.StatusMessage}");
        }

        return (anySuccess, sb.ToString().Trim());
    }

    private bool IsInCooldown(string key, int cooldownSec)
    {
        if (cooldownSec <= 0) return false;
        DateTime now = DateTime.UtcNow;
        if (_cooldowns.TryGetValue(key, out var last) && (now - last).TotalSeconds < cooldownSec)
        {
            return true;
        }
        _cooldowns[key] = now;
        return false;
    }

    private static bool HasAnyActiveChannel(NotificationConfig cfg)
    {
        return cfg.WhatsAppEnabled || cfg.TelegramEnabled || cfg.SmsEnabled;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                while (_queue.Reader.TryRead(out var payload))
                {
                    try
                    {
                        var cfg = Config;
                        if (!cfg.IsEnabled) continue;
                        await DispatchPayloadInternalAsync(payload, cfg, ct);
                    }
                    catch (Exception ex)
                    {
                        Log($"خطا در ارسال اعلان: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<List<NotificationDispatchRecord>> DispatchPayloadInternalAsync(
        NotificationEventPayload payload,
        NotificationConfig cfg,
        CancellationToken ct)
    {
        var dispatched = new List<NotificationDispatchRecord>();
        var (jalaliDate, timeStr) = FormatJalaliDateTime(payload.TimestampUtc.ToLocalTime());
        string fullMessage = BuildRichPersianMessage(payload, jalaliDate, timeStr);
        string smsText = BuildCompactSmsMessage(payload, jalaliDate, timeStr);
        string eventSummary = payload.EventKind == NotificationEventKind.LicensePlateRecognized
            ? $"پلاک {payload.PlateText} (کانال {payload.ChannelNumber})"
            : $"تشخیص چهره/انسان (کانال {payload.ChannelNumber})";

        // ۱. ارسال از طریق واتس‌اپ (با تصویر در صورت وجود)
        if (cfg.WhatsAppEnabled)
        {
            var recipients = ParseRecipients(cfg.WhatsAppRecipients);
            foreach (var recipient in recipients)
            {
                var (ok, detail) = await SendWhatsAppAsync(cfg, recipient, fullMessage, payload, jalaliDate, timeStr, ct);
                var rec = RecordResult($"{cfg.WhatsAppProvider} (WhatsApp)", eventSummary, recipient, ok, detail, jalaliDate, timeStr);
                dispatched.Add(rec);
            }
        }

        // ۲. ارسال از طریق تلگرام (با تصویر در صورت وجود)
        if (cfg.TelegramEnabled)
        {
            var chatIds = ParseRecipients(cfg.TelegramChatIds);
            foreach (var chatId in chatIds)
            {
                var (ok, detail) = await SendTelegramAsync(cfg, chatId, fullMessage, payload.JpegImageBytes, payload.ImageFileName, ct);
                var rec = RecordResult("Telegram Bot", eventSummary, chatId, ok, detail, jalaliDate, timeStr);
                dispatched.Add(rec);
            }
        }

        // ۳. ارسال از طریق پیامک (SMS)
        if (cfg.SmsEnabled)
        {
            var smsTargets = ParseRecipients(cfg.SmsRecipients);
            foreach (var phone in smsTargets)
            {
                var (ok, detail) = await SendSmsAsync(cfg, phone, smsText, payload, jalaliDate, timeStr, ct);
                var rec = RecordResult($"{cfg.SmsProvider} (SMS)", eventSummary, phone, ok, detail, jalaliDate, timeStr);
                dispatched.Add(rec);
            }
        }

        return dispatched;
    }

    private NotificationDispatchRecord RecordResult(
        string provider,
        string summary,
        string recipient,
        bool success,
        string detail,
        string jalaliDate,
        string timeStr)
    {
        var rec = new NotificationDispatchRecord
        {
            TimestampLocal = DateTime.Now,
            JalaliTimestamp = $"{jalaliDate} {timeStr}",
            ChannelProvider = provider,
            EventSummary = summary,
            Recipient = recipient,
            Success = success,
            StatusMessage = detail
        };

        _history.Enqueue(rec);
        while (_history.Count > 100) _history.TryDequeue(out _);

        NotificationDispatched?.Invoke(rec);
        Log($"[{provider}] به {recipient}: {(success ? "ارسال موفق" : "خطا")} - {detail}");
        return rec;
    }

    public static (string JalaliDate, string TimeStr) FormatJalaliDateTime(DateTime localTime)
    {
        var pc = new PersianCalendar();
        string date = $"{pc.GetYear(localTime):0000}/{pc.GetMonth(localTime):00}/{pc.GetDayOfMonth(localTime):00}";
        string time = localTime.ToString("HH:mm:ss");
        return (IranianPlateNormalizer.ToPersianDigits(date), IranianPlateNormalizer.ToPersianDigits(time));
    }

    private static string BuildRichPersianMessage(NotificationEventPayload p, string jalaliDate, string timeStr)
    {
        string confPct = IranianPlateNormalizer.ToPersianDigits($"{Math.Round(p.Confidence * 100)}%");
        string chNum = IranianPlateNormalizer.ToPersianDigits(p.ChannelNumber.ToString());

        if (p.EventKind == NotificationEventKind.LicensePlateRecognized)
        {
            return
                $"🚗 *هشدار پلاک‌خوان هوشمند مهرگان*\n" +
                $"🔢 شماره پلاک: *{p.PlateText}*\n" +
                $"📷 شماره دوربین: کانال {chNum} ({p.CameraName})\n" +
                $"📅 تاریخ: {jalaliDate}\n" +
                $"🕒 ساعت: {timeStr}\n" +
                $"🎯 دقت تشخیص: {confPct}";
        }

        return
            $"👤 *هشدار تشخیص انسان و چهره (بهترین شات)*\n" +
            $"📷 شماره دوربین: کانال {chNum} ({p.CameraName})\n" +
            $"📅 تاریخ: {jalaliDate}\n" +
            $"🕒 ساعت: {timeStr}\n" +
            $"🎯 ضریب اطمینان: {confPct}\n" +
            $"🖼️ بهترین تصویر چهره ثبت‌شده از ورود تا خروج فرد پیوست شد.";
    }

    private static string BuildCompactSmsMessage(NotificationEventPayload p, string jalaliDate, string timeStr)
    {
        string chNum = IranianPlateNormalizer.ToPersianDigits(p.ChannelNumber.ToString());
        if (p.EventKind == NotificationEventKind.LicensePlateRecognized)
        {
            return $"هشدار پلاک‌خوان مهرگان\nپلاک: {p.PlateText}\nدوربین: کانال {chNum} ({p.CameraName})\nتاریخ: {jalaliDate} ساعت: {timeStr}";
        }

        return $"هشدار تشخیص انسان/چهره مهرگان\nدوربین: کانال {chNum} ({p.CameraName})\nتاریخ: {jalaliDate} ساعت: {timeStr}";
    }

    private async Task<(bool Ok, string Detail)> SendWhatsAppAsync(
        NotificationConfig cfg,
        string recipient,
        string caption,
        NotificationEventPayload payload,
        string jalaliDate,
        string timeStr,
        CancellationToken ct)
    {
        string token = CredentialProtectionService.DecryptSecret(cfg.WhatsAppEncryptedToken);
        string normalizedPhone = NormalizeWhatsAppNumber(recipient);

        try
        {
            switch (cfg.WhatsAppProvider)
            {
                case WhatsAppProviderType.Ultramsg:
                    if (string.IsNullOrWhiteSpace(cfg.WhatsAppInstanceId) || string.IsNullOrWhiteSpace(token))
                        return (false, "Instance ID یا Token وب‌سرویس Ultramsg وارد نشده است.");

                    if (payload.JpegImageBytes is { Length: > 0 })
                    {
                        string imgUrl = $"https://api.ultramsg.com/{cfg.WhatsAppInstanceId.Trim()}/messages/image";
                        string base64 = Convert.ToBase64String(payload.JpegImageBytes);
                        using var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("token", token),
                            new KeyValuePair<string, string>("to", normalizedPhone),
                            new KeyValuePair<string, string>("image", $"data:image/jpeg;base64,{base64}"),
                            new KeyValuePair<string, string>("caption", caption)
                        });
                        using var resp = await SharedHttpClient.PostAsync(imgUrl, content, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "تصویر و پیام ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }
                    else
                    {
                        string chatUrl = $"https://api.ultramsg.com/{cfg.WhatsAppInstanceId.Trim()}/messages/chat";
                        using var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("token", token),
                            new KeyValuePair<string, string>("to", normalizedPhone),
                            new KeyValuePair<string, string>("body", caption)
                        });
                        using var resp = await SharedHttpClient.PostAsync(chatUrl, content, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "پیام متنی ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case WhatsAppProviderType.GreenApi:
                    if (string.IsNullOrWhiteSpace(cfg.WhatsAppInstanceId) || string.IsNullOrWhiteSpace(token))
                        return (false, "idInstance یا apiTokenInstance وب‌سرویس GreenAPI خالی است.");

                    string chatId = normalizedPhone.Contains("@") ? normalizedPhone : $"{normalizedPhone.TrimStart('+')}@c.us";
                    if (payload.JpegImageBytes is { Length: > 0 })
                    {
                        string url = $"https://api.green-api.com/waInstance{cfg.WhatsAppInstanceId.Trim()}/sendFileByUpload/{token}";
                        using var form = new MultipartFormDataContent();
                        form.Add(new StringContent(chatId), "chatId");
                        form.Add(new StringContent(caption), "caption");
                        var fileContent = new ByteArrayContent(payload.JpegImageBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                        form.Add(fileContent, "file", payload.ImageFileName);

                        using var resp = await SharedHttpClient.PostAsync(url, form, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "تصویر و کپشن از طریق GreenAPI ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }
                    else
                    {
                        string url = $"https://api.green-api.com/waInstance{cfg.WhatsAppInstanceId.Trim()}/sendMessage/{token}";
                        string jsonBody = JsonSerializer.Serialize(new { chatId, message = caption });
                        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        using var resp = await SharedHttpClient.PostAsync(url, content, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "پیام متنی ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case WhatsAppProviderType.MetaCloudApi:
                    if (string.IsNullOrWhiteSpace(cfg.WhatsAppMetaPhoneNumberId) || string.IsNullOrWhiteSpace(token))
                        return (false, "PhoneNumberId یا AccessToken متا وارد نشده است.");

                    string toNumber = normalizedPhone.TrimStart('+');
                    string? mediaId = null;

                    if (payload.JpegImageBytes is { Length: > 0 })
                    {
                        string mediaUrl = $"https://graph.facebook.com/v20.0/{cfg.WhatsAppMetaPhoneNumberId.Trim()}/media";
                        using var uploadReq = new HttpRequestMessage(HttpMethod.Post, mediaUrl);
                        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var mForm = new MultipartFormDataContent();
                        mForm.Add(new StringContent("whatsapp"), "messaging_product");
                        var imgPart = new ByteArrayContent(payload.JpegImageBytes);
                        imgPart.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                        mForm.Add(imgPart, "file", payload.ImageFileName);
                        uploadReq.Content = mForm;

                        using var uploadResp = await SharedHttpClient.SendAsync(uploadReq, ct);
                        string uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);
                        if (uploadResp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(uploadBody);
                            if (doc.RootElement.TryGetProperty("id", out var idEl))
                                mediaId = idEl.GetString();
                        }
                    }

                    string msgUrl = $"https://graph.facebook.com/v20.0/{cfg.WhatsAppMetaPhoneNumberId.Trim()}/messages";
                    object msgObj = !string.IsNullOrEmpty(mediaId)
                        ? new
                        {
                            messaging_product = "whatsapp",
                            to = toNumber,
                            type = "image",
                            image = new { id = mediaId, caption }
                        }
                        : new
                        {
                            messaging_product = "whatsapp",
                            to = toNumber,
                            type = "text",
                            text = new { body = caption }
                        };

                    using (var sendReq = new HttpRequestMessage(HttpMethod.Post, msgUrl))
                    {
                        sendReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        sendReq.Content = new StringContent(JsonSerializer.Serialize(msgObj), Encoding.UTF8, "application/json");
                        using var resp = await SharedHttpClient.SendAsync(sendReq, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, mediaId != null ? "تصویر و پیام از Meta API ارسال شد" : "پیام متنی ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case WhatsAppProviderType.CustomWebhook:
                    if (string.IsNullOrWhiteSpace(cfg.WhatsAppCustomWebhookUrl))
                        return (false, "آدرس Webhook اختصاصی واتس‌اپ وارد نشده است.");

                    var webhookPayload = new
                    {
                        eventType = payload.EventKind.ToString(),
                        recipient = normalizedPhone,
                        message = caption,
                        cameraNumber = payload.ChannelNumber,
                        cameraName = payload.CameraName,
                        plateText = payload.PlateText,
                        jalaliDate,
                        time = timeStr,
                        confidence = payload.Confidence,
                        mimeType = "image/jpeg",
                        imageBase64 = payload.JpegImageBytes != null ? Convert.ToBase64String(payload.JpegImageBytes) : null
                    };
                    using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.WhatsAppCustomWebhookUrl.Trim()))
                    {
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        }
                        req.Content = new StringContent(JsonSerializer.Serialize(webhookPayload), Encoding.UTF8, "application/json");
                        using var resp = await SharedHttpClient.SendAsync(req, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "وب‌هوک با موفقیت ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (false, "پرووایدر پشتیبانی نشد.");
    }

    private async Task<(bool Ok, string Detail)> SendTelegramAsync(
        NotificationConfig cfg,
        string chatId,
        string caption,
        byte[]? jpegBytes,
        string fileName,
        CancellationToken ct)
    {
        string botToken = CredentialProtectionService.DecryptSecret(cfg.TelegramBotEncryptedToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return (false, "توکن ربات تلگرام وارد نشده است.");

        try
        {
            if (jpegBytes is { Length: > 0 })
            {
                string url = $"https://api.telegram.org/bot{botToken.Trim()}/sendPhoto";
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId.Trim()), "chat_id");
                form.Add(new StringContent(caption), "caption");
                var fileContent = new ByteArrayContent(jpegBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(fileContent, "photo", fileName);

                using var resp = await SharedHttpClient.PostAsync(url, form, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                return resp.IsSuccessStatusCode ? (true, "تصویر و مشخصات به تلگرام ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
            }
            else
            {
                string url = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
                string json = JsonSerializer.Serialize(new { chat_id = chatId.Trim(), text = caption });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await SharedHttpClient.PostAsync(url, content, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                return resp.IsSuccessStatusCode ? (true, "پیام متنی به تلگرام ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Ok, string Detail)> SendSmsAsync(
        NotificationConfig cfg,
        string phoneNumber,
        string text,
        NotificationEventPayload payload,
        string jalaliDate,
        string timeStr,
        CancellationToken ct)
    {
        string apiKey = CredentialProtectionService.DecryptSecret(cfg.SmsEncryptedApiKey);
        string password = CredentialProtectionService.DecryptSecret(cfg.SmsEncryptedPassword);
        string receptor = NormalizeIranianLocalMobile(phoneNumber);

        try
        {
            switch (cfg.SmsProvider)
            {
                case SmsProviderType.Kavenegar:
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return (false, "API Key کاوه‌نگار وارد نشده است.");

                    string kavUrl = $"https://api.kavenegar.com/v1/{apiKey.Trim()}/sms/send.json";
                    var kavPairs = new List<KeyValuePair<string, string>>
                    {
                        new("receptor", receptor),
                        new("message", text)
                    };
                    if (!string.IsNullOrWhiteSpace(cfg.SmsSenderLine))
                        kavPairs.Add(new("sender", cfg.SmsSenderLine.Trim()));

                    using (var content = new FormUrlEncodedContent(kavPairs))
                    using (var resp = await SharedHttpClient.PostAsync(kavUrl, content, ct))
                    {
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "پیامک کاوه‌نگار ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case SmsProviderType.SmsIr:
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return (false, "API Key سامانه SMS.ir وارد نشده است.");

                    using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.sms.ir/v1/send/bulk"))
                    {
                        req.Headers.Add("X-API-KEY", apiKey.Trim());
                        var smsIrBody = new
                        {
                            lineNumber = long.TryParse(cfg.SmsSenderLine.Trim(), out long ln) ? ln : 0,
                            messageText = text,
                            mobiles = new[] { receptor }
                        };
                        req.Content = new StringContent(JsonSerializer.Serialize(smsIrBody), Encoding.UTF8, "application/json");
                        using var resp = await SharedHttpClient.SendAsync(req, ct);
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "پیامک SMS.ir ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case SmsProviderType.Melipayamak:
                    if (string.IsNullOrWhiteSpace(cfg.SmsUsername) || string.IsNullOrWhiteSpace(password))
                        return (false, "نام کاربری یا رمز عبور ملی‌پیامک وارد نشده است.");

                    var meliPayload = new
                    {
                        username = cfg.SmsUsername.Trim(),
                        password,
                        to = receptor,
                        from = cfg.SmsSenderLine.Trim(),
                        text
                    };
                    using (var content = new StringContent(JsonSerializer.Serialize(meliPayload), Encoding.UTF8, "application/json"))
                    using (var resp = await SharedHttpClient.PostAsync("https://rest.payamak-panel.com/api/SendSMS/SendSMS", content, ct))
                    {
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "پیامک ملی‌پیامک ارسال شد") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }

                case SmsProviderType.CustomWebhook:
                    if (string.IsNullOrWhiteSpace(cfg.SmsCustomUrlTemplate))
                        return (false, "آدرس URL سرویس پیامک سفارشی وارد نشده است.");

                    string finalUrl = cfg.SmsCustomUrlTemplate.Trim()
                        .Replace("{to}", Uri.EscapeDataString(receptor))
                        .Replace("{text}", Uri.EscapeDataString(text))
                        .Replace("{plate}", Uri.EscapeDataString(payload.PlateText ?? ""))
                        .Replace("{camera}", Uri.EscapeDataString(payload.CameraName))
                        .Replace("{channel}", Uri.EscapeDataString(payload.ChannelNumber.ToString()))
                        .Replace("{date}", Uri.EscapeDataString(jalaliDate))
                        .Replace("{time}", Uri.EscapeDataString(timeStr));

                    using (var resp = await SharedHttpClient.GetAsync(finalUrl, ct))
                    {
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        return resp.IsSuccessStatusCode ? (true, "درخواست پیامک سفارشی موفق بود") : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 120)}");
                    }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (false, "پرووایدر پیامک پشتیبانی نشد.");
    }

    private static List<string> ParseRecipients(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';', '\n', '\r', '،' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => IranianPlateNormalizer.ToLatinDigits(s.Trim()))
                  .Where(s => !string.IsNullOrWhiteSpace(s))
                  .Distinct()
                  .ToList();
    }

    private static string NormalizeWhatsAppNumber(string raw)
    {
        string clean = IranianPlateNormalizer.ToLatinDigits(raw).Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("00")) clean = "+" + clean[2..];
        else if (clean.StartsWith("09") && clean.Length == 11) clean = "+98" + clean[1..];
        else if (clean.StartsWith("989") && clean.Length == 12) clean = "+" + clean;
        return clean;
    }

    private static string NormalizeIranianLocalMobile(string raw)
    {
        string clean = IranianPlateNormalizer.ToLatinDigits(raw).Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("+98")) clean = "0" + clean[3..];
        else if (clean.StartsWith("0098")) clean = "0" + clean[4..];
        else if (clean.StartsWith("98") && clean.Length == 12) clean = "0" + clean[2..];
        return clean;
    }

    private static string Truncate(string s, int maxLen) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= maxLen ? s : s[..maxLen] + "...");

    private static byte[]? CreateTestBannerJpeg()
    {
        try
        {
            int w = 320, h = 120, stride = w * 4;
            byte[] pixels = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * stride + x * 4;
                    bool border = x < 4 || x >= w - 4 || y < 4 || y >= h - 4;
                    bool blueStrip = x >= 8 && x <= 44 && y >= 18 && y <= h - 18;
                    bool card = x > 44 && x < w - 12 && y >= 18 && y <= h - 18;

                    if (border) { pixels[idx] = 232; pixels[idx + 1] = 115; pixels[idx + 2] = 26; }
                    else if (blueStrip) { pixels[idx] = 216; pixels[idx + 1] = 78; pixels[idx + 2] = 29; }
                    else if (card) { pixels[idx] = 245; pixels[idx + 1] = 248; pixels[idx + 2] = 250; }
                    else { pixels[idx] = 38; pixels[idx + 1] = 27; pixels[idx + 2] = 22; }
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void Log(string message) => LogMessage?.Invoke($"[Notification] {message}");

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        if (_workerTask != null)
        {
            await Task.WhenAny(_workerTask, Task.Delay(800));
        }
        _cts.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
