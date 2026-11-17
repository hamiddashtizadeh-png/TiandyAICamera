using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance;

public partial class NotificationSettingsWindow : Window
{
    private readonly NotificationService _notificationService;

    public NotificationSettingsWindow(NotificationService? notificationService = null)
    {
        InitializeComponent();
        _notificationService = notificationService ?? new NotificationService();

        Loaded += NotificationSettingsWindow_Loaded;
        Closed += NotificationSettingsWindow_Closed;
    }

    private void NotificationSettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConfigToUi(_notificationService.Config);
        RefreshHistory();
        UpdateMessagePreview();
        _notificationService.NotificationDispatched += OnNotificationDispatched;
    }

    private void NotificationSettingsWindow_Closed(object? sender, EventArgs e)
    {
        _notificationService.NotificationDispatched -= OnNotificationDispatched;
    }

    private void OnNotificationDispatched(NotificationDispatchRecord record)
    {
        Dispatcher.InvokeAsync(RefreshHistory);
    }

    private void LoadConfigToUi(NotificationConfig cfg)
    {
        MasterNotificationEnableCheck.IsChecked = cfg.IsEnabled;
        NotifyAnprCheck.IsChecked = cfg.NotifyOnAnpr;
        NotifyFaceCheck.IsChecked = cfg.NotifyOnFaceOrHuman;
        AttachPhotoCheck.IsChecked = cfg.SendPhotoInMessaging;
        CooldownSecondsBox.Text = cfg.PerCameraCooldownSeconds.ToString();

        WhatsAppEnableCheck.IsChecked = cfg.WhatsAppEnabled;
        WhatsAppProviderCombo.SelectedIndex = (int)cfg.WhatsAppProvider;
        WhatsAppRecipientsBox.Text = cfg.WhatsAppRecipients;
        WhatsAppInstanceBox.Text = cfg.WhatsAppProvider == WhatsAppProviderType.MetaCloudApi
            ? cfg.WhatsAppMetaPhoneNumberId
            : cfg.WhatsAppInstanceId;
        WhatsAppWebhookUrlBox.Text = cfg.WhatsAppCustomWebhookUrl;
        WhatsAppTokenBox.Password = CredentialProtectionService.DecryptSecret(cfg.WhatsAppEncryptedToken);

        TelegramEnableCheck.IsChecked = cfg.TelegramEnabled;
        TelegramTokenBox.Password = CredentialProtectionService.DecryptSecret(cfg.TelegramBotEncryptedToken);
        TelegramChatIdsBox.Text = cfg.TelegramChatIds;

        SmsEnableCheck.IsChecked = cfg.SmsEnabled;
        SmsProviderCombo.SelectedIndex = (int)cfg.SmsProvider;
        SmsRecipientsBox.Text = cfg.SmsRecipients;
        SmsSenderLineBox.Text = cfg.SmsSenderLine;
        SmsApiKeyBox.Password = CredentialProtectionService.DecryptSecret(cfg.SmsEncryptedApiKey);
        SmsUsernameBox.Text = cfg.SmsUsername;
        SmsPasswordBox.Password = CredentialProtectionService.DecryptSecret(cfg.SmsEncryptedPassword);
        SmsCustomUrlBox.Text = cfg.SmsCustomUrlTemplate;

        UpdateProviderVisibility();
    }

    private NotificationConfig BuildConfigFromUi()
    {
        var waProvider = WhatsAppProviderCombo.SelectedIndex >= 0
            ? (WhatsAppProviderType)WhatsAppProviderCombo.SelectedIndex
            : WhatsAppProviderType.Ultramsg;

        var smsProvider = SmsProviderCombo.SelectedIndex >= 0
            ? (SmsProviderType)SmsProviderCombo.SelectedIndex
            : SmsProviderType.Kavenegar;

        int cooldown = int.TryParse(CooldownSecondsBox.Text.Trim(), out int cd) ? Math.Clamp(cd, 0, 3600) : 8;

        return new NotificationConfig
        {
            IsEnabled = MasterNotificationEnableCheck.IsChecked == true,
            NotifyOnAnpr = NotifyAnprCheck.IsChecked == true,
            NotifyOnFaceOrHuman = NotifyFaceCheck.IsChecked == true,
            SendPhotoInMessaging = AttachPhotoCheck.IsChecked == true,
            PerCameraCooldownSeconds = cooldown,

            WhatsAppEnabled = WhatsAppEnableCheck.IsChecked == true,
            WhatsAppProvider = waProvider,
            WhatsAppRecipients = WhatsAppRecipientsBox.Text.Trim(),
            WhatsAppInstanceId = waProvider == WhatsAppProviderType.MetaCloudApi ? string.Empty : WhatsAppInstanceBox.Text.Trim(),
            WhatsAppMetaPhoneNumberId = waProvider == WhatsAppProviderType.MetaCloudApi ? WhatsAppInstanceBox.Text.Trim() : string.Empty,
            WhatsAppCustomWebhookUrl = WhatsAppWebhookUrlBox.Text.Trim(),
            WhatsAppEncryptedToken = CredentialProtectionService.EncryptSecret(WhatsAppTokenBox.Password),

            TelegramEnabled = TelegramEnableCheck.IsChecked == true,
            TelegramBotEncryptedToken = CredentialProtectionService.EncryptSecret(TelegramTokenBox.Password),
            TelegramChatIds = TelegramChatIdsBox.Text.Trim(),

            SmsEnabled = SmsEnableCheck.IsChecked == true,
            SmsProvider = smsProvider,
            SmsRecipients = SmsRecipientsBox.Text.Trim(),
            SmsSenderLine = SmsSenderLineBox.Text.Trim(),
            SmsEncryptedApiKey = CredentialProtectionService.EncryptSecret(SmsApiKeyBox.Password),
            SmsUsername = SmsUsernameBox.Text.Trim(),
            SmsEncryptedPassword = CredentialProtectionService.EncryptSecret(SmsPasswordBox.Password),
            SmsCustomUrlTemplate = SmsCustomUrlBox.Text.Trim()
        };
    }

    private void UpdateProviderVisibility()
    {
        if (!IsLoaded) return;

        var waProvider = WhatsAppProviderCombo.SelectedIndex >= 0
            ? (WhatsAppProviderType)WhatsAppProviderCombo.SelectedIndex
            : WhatsAppProviderType.Ultramsg;

        if (waProvider == WhatsAppProviderType.CustomWebhook)
        {
            WhatsAppInstanceRow.Visibility = Visibility.Collapsed;
            WhatsAppWebhookRow.Visibility = Visibility.Visible;
        }
        else
        {
            WhatsAppInstanceRow.Visibility = Visibility.Visible;
            WhatsAppWebhookRow.Visibility = Visibility.Collapsed;
            WhatsAppInstanceLabel.Text = waProvider == WhatsAppProviderType.MetaCloudApi
                ? "Phone Number ID:"
                : "Instance ID:";
        }

        var smsProvider = SmsProviderCombo.SelectedIndex >= 0
            ? (SmsProviderType)SmsProviderCombo.SelectedIndex
            : SmsProviderType.Kavenegar;

        SmsSenderRow.Visibility = smsProvider == SmsProviderType.CustomWebhook ? Visibility.Collapsed : Visibility.Visible;
        SmsApiKeyRow.Visibility = (smsProvider == SmsProviderType.Kavenegar || smsProvider == SmsProviderType.SmsIr)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SmsUserPassRow.Visibility = smsProvider == SmsProviderType.Melipayamak ? Visibility.Visible : Visibility.Collapsed;
        SmsCustomUrlRow.Visibility = smsProvider == SmsProviderType.CustomWebhook ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMessagePreview()
    {
        var (jalaliDate, timeStr) = NotificationService.FormatJalaliDateTime(DateTime.Now);
        MessagePreviewText.Text =
            "نمونه پیام خروجی برای پلاک‌خوان:\n" +
            "🚗 هشدار پلاک‌خوان هوشمند مهرگان\n" +
            "🔢 شماره پلاک: ۱۲ ب ۳۴۵ ایران ۶۷\n" +
            "📷 شماره دوربین: کانال ۱ (دوربین ورودی)\n" +
            $"📅 تاریخ: {jalaliDate}   🕒 ساعت: {timeStr}\n" +
            "🎯 دقت تشخیص: ۹۴٪   (همراه با تصویر خودرو/پلاک در واتس‌اپ و تلگرام)\n" +
            "────────────────────\n" +
            "نمونه پیام خروجی برای تشخیص چهره/انسان (Best Shot):\n" +
            "👤 هشدار تشخیص انسان و چهره (بهترین شات)\n" +
            "📷 شماره دوربین: کانال ۱ (دوربین ورودی)\n" +
            $"📅 تاریخ: {jalaliDate}   🕒 ساعت: {timeStr}\n" +
            "🖼️ بهترین تصویر انتخاب‌شده از لحظه ورود تا خروج فرد پیوست می‌شود.";
    }

    private void RefreshHistory()
    {
        HistoryGrid.ItemsSource = null;
        HistoryGrid.ItemsSource = _notificationService.GetRecentHistory();
    }

    private void WhatsAppProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderVisibility();
    }

    private void SmsProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderVisibility();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = BuildConfigFromUi();
            _notificationService.SaveConfig(cfg);
            StatusFeedbackText.Text = "✅ تنظیمات اعلان با موفقیت ذخیره و رمزنگاری گردید.";
            StatusFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        catch (Exception ex)
        {
            StatusFeedbackText.Text = $"❌ خطا در ذخیره تنظیمات: {ex.Message}";
            StatusFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
    }

    private async void SendTestButton_Click(object sender, RoutedEventArgs e)
    {
        SendTestButton.IsEnabled = false;
        StatusFeedbackText.Text = "در حال ارسال پیام و تصویر آزمایشی...";
        StatusFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        try
        {
            var cfg = BuildConfigFromUi();
            cfg.IsEnabled = true;
            var (ok, report) = await _notificationService.SendTestNotificationAsync(cfg);

            RefreshHistory();
            StatusFeedbackText.Text = ok
                ? "✅ تست ارسال اعلان با موفقیت انجام شد."
                : "⚠️ برخی یا همه کانال‌ها در تست با خطا مواجه شدند.";
            StatusFeedbackText.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                : new SolidColorBrush(Color.FromRgb(251, 191, 36));

            MessageBox.Show(report, ok ? "نتیجه تست ارسال" : "گزارش تست ارسال", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusFeedbackText.Text = $"❌ خطا در ارسال تست: {ex.Message}";
            StatusFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
        finally
        {
            SendTestButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
