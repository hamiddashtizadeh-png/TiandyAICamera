using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance;

/// <summary>
/// پنجره اختصاصی پلاک‌خوان هوشمند خودرو (ANPR) با نمایش زنده دوربین، رسم کادر پلاک و آرشیو واقعی بدون پلاک جعلی
/// </summary>
public partial class AnprWindow : Window
{
    private readonly AnprPipeline _pipeline;
    private readonly AnprArchiveService _archiveService;
    private readonly LocalFrameAcquisitionPipeline? _framePipeline;
    private readonly List<CameraChannel> _channels;
    private readonly List<AnprResult> _loadedEvents = new();
    private DispatcherTimer? _overlayExpireTimer;
    private DateTime _lastOverlayUtc = DateTime.MinValue;
    private int _selectedPreviewSlot = -1; // -1 = همه دوربین‌های فعال

    public AnprWindow(
        AnprPipeline? pipeline = null,
        AnprArchiveService? archiveService = null,
        LocalFrameAcquisitionPipeline? framePipeline = null,
        List<CameraChannel>? channels = null)
    {
        InitializeComponent();

        _pipeline = pipeline ?? new AnprPipeline();
        _archiveService = archiveService ?? new AnprArchiveService();
        _framePipeline = framePipeline;
        _channels = channels ?? new List<CameraChannel>();

        Loaded += AnprWindow_Loaded;
        Closed += AnprWindow_Closed;
    }

    private async void AnprWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _archiveService.InitializeAsync();
            _pipeline.RegisterPreviewSubscriber();
            _pipeline.PlateRecognized += OnLivePlateRecognized;
            _pipeline.LiveObservationAvailable += OnLiveObservationAvailable;
            _pipeline.AutoSceneTuningUpdated += OnAutoSceneTuningUpdated;

            AnprEnableToggle.IsChecked = _pipeline.Config.IsEnabled;
            AutoImageTuneToggle.IsChecked = _pipeline.IsAutoImageOptimizationEnabled;
            UpdateEngineBadge();
            UpdateOcrNoticeBanner();

            _overlayExpireTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _overlayExpireTimer.Tick += (s, ev) =>
            {
                if (_lastOverlayUtc != DateTime.MinValue && (DateTime.UtcNow - _lastOverlayUtc).TotalSeconds > 1.8)
                {
                    LiveAnprOverlayCanvas.Children.Clear();
                    _lastOverlayUtc = DateTime.MinValue;
                }
            };
            _overlayExpireTimer.Start();

            PopulateCameraSelectors();
            RefreshAutoTuningStatusBanner();

            // اگر موتور فعال است و دوربین تخصیص‌یافته داریم، فریم‌گیری کانال منتخب را تضمین می‌کنیم
            if (_pipeline.Config.IsEnabled && _framePipeline != null)
            {
                await EnsureLivePreviewAcquisitionStartedAsync();
            }

            await LoadEventsAsync();
            await UpdateStatisticsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در راه‌اندازی ماژول پلاک‌خوان: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AnprWindow_Closed(object? sender, EventArgs e)
    {
        _overlayExpireTimer?.Stop();
        _pipeline.AutoSceneTuningUpdated -= OnAutoSceneTuningUpdated;
        _pipeline.LiveObservationAvailable -= OnLiveObservationAvailable;
        _pipeline.PlateRecognized -= OnLivePlateRecognized;
        _pipeline.UnregisterPreviewSubscriber();
    }

    private void PopulateCameraSelectors()
    {
        CameraFilterCombo.Items.Clear();
        CameraFilterCombo.Items.Add(new ComboBoxItem { Content = "همه دوربین‌ها", Tag = null });

        LiveCameraSelectorCombo.Items.Clear();
        LiveCameraSelectorCombo.Items.Add(new ComboBoxItem { Content = "اولین دوربین فعال (خودکار)", Tag = -1 });

        foreach (var ch in _channels.Where(c => c.IsAssigned))
        {
            string title = $"{ch.DisplayName} (کانال {ch.SlotIndex + 1})";
            CameraFilterCombo.Items.Add(new ComboBoxItem { Content = title, Tag = ch.Id });
            LiveCameraSelectorCombo.Items.Add(new ComboBoxItem { Content = title, Tag = ch.SlotIndex });
        }

        CameraFilterCombo.SelectedIndex = 0;
        LiveCameraSelectorCombo.SelectedIndex = LiveCameraSelectorCombo.Items.Count > 1 ? 1 : 0;
    }

    private async Task EnsureLivePreviewAcquisitionStartedAsync()
    {
        if (_framePipeline == null) return;

        var assigned = _channels.Where(c => c.IsAssigned && c.IsAnprEnabled).ToList();
        if (_selectedPreviewSlot >= 0)
        {
            var target = _channels.FirstOrDefault(c => c.SlotIndex == _selectedPreviewSlot && c.IsAssigned);
            if (target != null)
            {
                target.IsAnprEnabled = true;
                await _framePipeline.StartChannelAcquisitionAsync(target);
                return;
            }
        }

        foreach (var ch in assigned)
        {
            await _framePipeline.StartChannelAcquisitionAsync(ch);
        }
    }

    private void OnLiveObservationAvailable(AnprLiveObservation obs)
    {
        if (obs == null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_selectedPreviewSlot >= 0 && obs.ChannelIndex != _selectedPreviewSlot)
            {
                return;
            }

            if (obs.PreviewFrameJpeg != null && obs.PreviewFrameJpeg.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(obs.PreviewFrameJpeg);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    LiveCameraPreviewImage.Source = bmp;
                    LivePreviewPlaceholderText.Visibility = Visibility.Collapsed;
                }
                catch { }
            }

            DrawLiveAnprBoxes(obs);
            RefreshAutoTuningStatusBanner(obs.CameraId);
        }));
    }

    private void OnAutoSceneTuningUpdated(AutoImageTuningRecommendation rec)
    {
        if (rec == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_selectedPreviewSlot >= 0 && rec.ChannelIndex != _selectedPreviewSlot) return;
            ApplyAutoTuningStatusText(rec);
        }));
    }

    private void RefreshAutoTuningStatusBanner(string? cameraId = null)
    {
        if (!_pipeline.IsAutoImageOptimizationEnabled)
        {
            AutoTuningStatusText.Text = "تنظیم خودکار تصویر غیرفعال است (پخش با مقادیر خام دوربین).";
            AutoTuningStatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            return;
        }

        string targetCam = cameraId ?? string.Empty;
        if (string.IsNullOrEmpty(targetCam))
        {
            var ch = _selectedPreviewSlot >= 0
                ? _channels.FirstOrDefault(c => c.SlotIndex == _selectedPreviewSlot && c.IsAssigned)
                : _channels.FirstOrDefault(c => c.IsAssigned);
            targetCam = ch?.Id ?? string.Empty;
        }

        var latest = !string.IsNullOrEmpty(targetCam) ? _pipeline.AutoTuner.GetLatestRecommendation(targetCam) : null;
        if (latest != null)
        {
            ApplyAutoTuningStatusText(latest);
        }
        else
        {
            AutoTuningStatusText.Text = "✨ بهینه‌ساز خودکار نور صحنه فعال است (در انتظار دریافت اولین فریم...)";
            AutoTuningStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }
    }

    private void ApplyAutoTuningStatusText(AutoImageTuningRecommendation rec)
    {
        var p = rec.OptimizedProfile;
        AutoTuningStatusText.Text =
            $"✨ {rec.SceneLabelFa} | گاما {p.Gamma:F2} • کنتراست {p.Contrast:F2} • CLAHE {p.ClaheClipLimit:F1} (اعمال خودکار فعال)";
        AutoTuningStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    }

    private void DrawLiveAnprBoxes(AnprLiveObservation obs)
    {
        LiveAnprOverlayCanvas.Children.Clear();

        if (obs.PlateBox == null)
        {
            return;
        }

        double cw = LiveAnprOverlayCanvas.ActualWidth;
        double ch = LiveAnprOverlayCanvas.ActualHeight;
        if (cw <= 10 || ch <= 10) return;

        double ox = 0, oy = 0, rw = cw, rh = ch;
        if (LiveCameraPreviewImage.Source is BitmapSource bmp && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            double scale = Math.Min(cw / bmp.PixelWidth, ch / bmp.PixelHeight);
            rw = bmp.PixelWidth * scale;
            rh = bmp.PixelHeight * scale;
            ox = (cw - rw) / 2.0;
            oy = (ch - rh) / 2.0;
        }

        if (obs.VehicleBox != null)
        {
            var vb = obs.VehicleBox;
            var vehRect = new Rectangle
            {
                Width = Math.Max(14, vb.Width * rw),
                Height = Math.Max(14, vb.Height * rh),
                Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent,
                RadiusX = 3,
                RadiusY = 3
            };
            Canvas.SetLeft(vehRect, ox + (vb.X * rw));
            Canvas.SetTop(vehRect, oy + (vb.Y * rh));
            LiveAnprOverlayCanvas.Children.Add(vehRect);
        }

        var pb = obs.PlateBox;
        double px = ox + (pb.X * rw);
        double py = oy + (pb.Y * rh);
        double pw = Math.Max(34, pb.Width * rw);
        double ph = Math.Max(14, pb.Height * rh);

        Color col = obs.IsOcrVerified ? Color.FromRgb(16, 185, 129) : Color.FromRgb(245, 158, 11);
        var plateRect = new Rectangle
        {
            Width = pw,
            Height = ph,
            Stroke = new SolidColorBrush(col),
            StrokeThickness = 2.5,
            Fill = new SolidColorBrush(Color.FromArgb(32, col.R, col.G, col.B)),
            RadiusX = 2,
            RadiusY = 2
        };
        Canvas.SetLeft(plateRect, px);
        Canvas.SetTop(plateRect, py);
        LiveAnprOverlayCanvas.Children.Add(plateRect);

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230,
                obs.IsOcrVerified ? (byte)5 : (byte)180,
                obs.IsOcrVerified ? (byte)150 : (byte)83,
                obs.IsOcrVerified ? (byte)105 : (byte)9)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1)
        };
        badge.Child = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(obs.LabelText) ? "🚗 کادر پلاک" : $"🚗 {obs.LabelText}",
            Foreground = Brushes.White,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(badge, px);
        Canvas.SetTop(badge, Math.Max(0, py - 21));
        LiveAnprOverlayCanvas.Children.Add(badge);

        _lastOverlayUtc = DateTime.UtcNow;
    }

    private void OnLivePlateRecognized(AnprResult result)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(120);
            await LoadEventsAsync(PlateSearchBox.Text);
            await UpdateStatisticsAsync();
        });
    }

    private void UpdateEngineBadge()
    {
        if (_pipeline.ModelStatus.IsReady)
        {
            AiEngineStatusBadge.Text = $"موتور فعال ONNX ({_pipeline.ModelStatus.ExecutionProvider})";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else if (_pipeline.IsInitialized)
        {
            AiEngineStatusBadge.Text = $"کادریاب فعال ({_pipeline.ModelStatus.ExecutionProvider}) — بدون پلاک جعلی";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }
        else
        {
            AiEngineStatusBadge.Text = "در انتظار راه‌اندازی موتور";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }
    }

    private void UpdateOcrNoticeBanner()
    {
        if (_pipeline.ModelStatus.IsReady)
        {
            OcrModelNoticeText.Text = "✅ مدل‌های ONNX تشخیص پلاک و OCR ایرانی فعال هستند؛ پلاک‌های تاییدشده بلافاصله در دیتابیس ذخیره می‌شوند.";
            OcrModelNoticeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else
        {
            OcrModelNoticeText.Text =
                "ℹ️ تصویر دوربین و کادر پلاک زنده نمایش داده می‌شود. به دلیل عدم وجود فایل وزن مدل نویسه‌خوان (Models/ANPR/iranian-plate-ocr.onnx)، هیچ پلاک تصادفی/جعلی در دیتابیس ثبت نمی‌شود.";
            OcrModelNoticeText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }
    }

    private async Task LoadEventsAsync(string? query = null)
    {
        try
        {
            DateTime? from = FromDatePicker.SelectedDate?.ToUniversalTime();
            DateTime? to = ToDatePicker.SelectedDate?.ToUniversalTime().AddDays(1);

            string? cam = (CameraFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;

            AnprValidationStatus? status = null;
            if (StatusFilterCombo.SelectedIndex == 1) status = AnprValidationStatus.Verified;
            else if (StatusFilterCombo.SelectedIndex == 2) status = AnprValidationStatus.Partial;
            else if (StatusFilterCombo.SelectedIndex == 3) status = AnprValidationStatus.Uncertain;

            var events = await _archiveService.SearchAsync(query, cam, from, to, status, limit: 150);

            _loadedEvents.Clear();
            _loadedEvents.AddRange(events);

            AnprEventsGrid.ItemsSource = null;
            AnprEventsGrid.ItemsSource = _loadedEvents;

            if (_loadedEvents.Count > 0)
            {
                AnprEventsGrid.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در بازیابی رویدادها: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task UpdateStatisticsAsync()
    {
        try
        {
            var (total, verified, today, avgConf) = await _archiveService.GetStatisticsAsync();
            TotalEventsText.Text = IranianPlateNormalizer.ToPersianDigits(total.ToString("N0"));
            VerifiedEventsText.Text = IranianPlateNormalizer.ToPersianDigits(verified.ToString("N0"));
            TodayEventsText.Text = IranianPlateNormalizer.ToPersianDigits(today.ToString("N0"));
            AvgConfidenceText.Text = IranianPlateNormalizer.ToPersianDigits($"{avgConf * 100:F0}%");
        }
        catch { }
    }

    private async void AnprEnableToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enable = AnprEnableToggle.IsChecked == true;
        if (enable)
        {
            _pipeline.EnablePipeline();
            await EnsureLivePreviewAcquisitionStartedAsync();
        }
        else
        {
            _pipeline.DisablePipeline();
            LiveAnprOverlayCanvas.Children.Clear();
        }
    }

    private void LiveCameraSelectorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LiveCameraSelectorCombo.SelectedItem is ComboBoxItem item && item.Tag is int slot)
        {
            _selectedPreviewSlot = slot;
            LiveAnprOverlayCanvas.Children.Clear();
        }
    }

    private async void StartLiveCameraPreview_Click(object sender, RoutedEventArgs e)
    {
        AnprEnableToggle.IsChecked = true;
        _pipeline.EnablePipeline();

        if (_framePipeline == null)
        {
            MessageBox.Show("پایپ‌لاین دریافت فریم آماده نیست. ابتدا پنجره اصلی دوربین را باز نگه دارید.", "توجه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_channels.Any(c => c.IsAssigned))
        {
            MessageBox.Show("هنوز دوربینی به اسلات‌های برنامه اختصاص نیافته است. ابتدا از «مدیریت تجهیزات» یک دوربین اضافه یا به اسلات اختصاص دهید.", "دوربین یافت نشد", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LivePreviewPlaceholderText.Text = "در حال دریافت تصویر زنده از دوربین...";
        await EnsureLivePreviewAcquisitionStartedAsync();
    }

    private void AutoImageTuneToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoImageTuneToggle.IsChecked == true;
        _pipeline.IsAutoImageOptimizationEnabled = enabled;
        foreach (var ch in _channels)
        {
            ch.IsAutoImageOptimizationEnabled = enabled;
        }
        RefreshAutoTuningStatusBanner();
    }

    private async void SyncHardwareSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var targetChannel = _selectedPreviewSlot >= 0
            ? _channels.FirstOrDefault(c => c.SlotIndex == _selectedPreviewSlot && c.IsAssigned)
            : _channels.FirstOrDefault(c => c.IsAssigned);

        if (targetChannel == null || string.IsNullOrWhiteSpace(targetChannel.IpAddress))
        {
            MessageBox.Show("ابتدا یک دوربین متصل را از لیست انتخاب کنید.", "توجه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SyncHardwareSettingsButton.IsEnabled = false;
        AutoTuningStatusText.Text = $"⏳ در حال همگام‌سازی خودکار تنظیمات سخت‌افزاری دوربین {targetChannel.IpAddress}...";
        AutoTuningStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));

        try
        {
            var regService = new CameraRegistryService();
            var regCam = regService.GetAllCameras().FirstOrDefault(c => c.IpAddress == targetChannel.IpAddress);
            string user = regCam?.Username ?? "admin";
            string pass = !string.IsNullOrEmpty(regCam?.EncryptedPassword)
                ? CredentialProtectionService.DecryptSecret(regCam.EncryptedPassword)
                : string.Empty;

            int[] candidatePorts = new[] { 3000, 80, 8899, 8080, 8000, 34567 };
            int? controlPort = await OnvifImagingProvider.FindOpenPortAsync(targetChannel.IpAddress, 3000, candidatePorts, 300);
            if (!controlPort.HasValue)
            {
                RefreshAutoTuningStatusBanner(targetChannel.Id);
                MessageBox.Show(
                    $"هیچ پورت کنترل HTTP/ONVIF/Tiandy روی {targetChannel.IpAddress} پاسخ نداد، اما تنظیمات بهینه‌سازی نرم‌افزاری (WDR/گاما/CLAHE) همچنان به‌صورت خودکار روی تصویر و پلاک‌خوان فعال است.",
                    "همگام‌سازی سخت‌افزاری در دسترس نیست",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var latest = _pipeline.AutoTuner.GetLatestRecommendation(targetChannel.Id);
            var hwProfile = latest?.HardwareProfileType ?? CameraOptimizationProfileType.DaytimeLicensePlate;

            var optimizer = new CameraSettingsOptimizerService();
            var hwRec = await optimizer.GenerateRecommendationAsync(
                targetChannel.Id,
                targetChannel.IpAddress,
                controlPort.Value,
                user,
                pass,
                hwProfile,
                targetChannel.Brand);

            var (ok, msg, _) = await optimizer.ApplySettingsWithSafetyAsync(
                targetChannel.Id,
                targetChannel.IpAddress,
                controlPort.Value,
                user,
                pass,
                hwRec.ProposedSettings,
                $"AutoScene-{hwProfile}",
                adminConfirmed: true,
                adminUsername: "مدیر سیستم",
                manufacturer: targetChannel.Brand);

            RefreshAutoTuningStatusBanner(targetChannel.Id);
            MessageBox.Show(
                ok ? $"تنظیمات سخت‌افزاری متناسب با صحنه ({latest?.SceneLabelFa ?? hwProfile.ToString()}) روی دوربین {targetChannel.IpAddress}:{controlPort.Value} اعمال شد.\n{msg}"
                   : $"دوربین تغییر سخت‌افزاری را نپذیرفت ({msg})؛ بهینه‌ساز نرم‌افزاری خودکار همچنان فعال است.",
                ok ? "اعمال موفق" : "نتیجه همگام‌سازی",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در همگام‌سازی سخت‌افزار دوربین: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SyncHardwareSettingsButton.IsEnabled = true;
        }
    }

    private void AutoArchiveToggle_Click(object sender, RoutedEventArgs e)
    {
        // وضعیت ثبت خودکار
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync(PlateSearchBox.Text);
        await UpdateStatisticsAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync(PlateSearchBox.Text);
    }

    private async void PlateSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LoadEventsAsync(PlateSearchBox.Text);
        }
    }

    private void QuickLetter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string letter)
        {
            PlateSearchBox.Text += letter;
            PlateSearchBox.Focus();
            PlateSearchBox.CaretIndex = PlateSearchBox.Text.Length;
        }
    }

    private void AnprEventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnprEventsGrid.SelectedItem is not AnprResult item) return;

        // به‌روزرسانی پلاک گرافیکی
        var plate = item.Plate;
        PlatePart1Text.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.Part1) ? "--" : plate.Part1);
        PlateLetterText.Text = string.IsNullOrEmpty(plate.LetterText) ? "؟" : plate.LetterText;
        PlatePart2Text.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.Part2) ? "---" : plate.Part2);
        PlateIranCodeText.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.IranCode) ? "--" : plate.IranCode);

        // رنگ‌بندی کارت پلاک بر اساس نوع
        ApplyPlateColorTheme(plate.PlateType);

        // مقادیر متادیتا
        RawOcrValue.Text = string.IsNullOrWhiteSpace(plate.RawOcr) ? "ندارد" : plate.RawOcr;
        EventIdValue.Text = item.EventId.ToString("D");
        ModelInfoValue.Text = item.ModelInfo;
        LatencyValue.Text = $"{item.InferenceDurationMs:F1} میلی‌ثانیه";
        EpValue.Text = item.ExecutionProvider;

        // بارگذاری تصویر در صورت وجود فایل محلی
        LoadImagePreview(PlateCropImage, item.EncryptedPlateCropPath);
        LoadImagePreview(VehicleCropImage, item.EncryptedVehicleCropPath);
    }

    private void ApplyPlateColorTheme(IranianPlateType plateType)
    {
        switch (plateType)
        {
            case IranianPlateType.Taxi:
            case IranianPlateType.Public:
                // زرد با فونت مشکی
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                SetPlateTextColors(Color.FromRgb(15, 23, 42));
                break;

            case IranianPlateType.Government:
                // قرمز دولتی با فونت سفید
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                SetPlateTextColors(Colors.White);
                break;

            case IranianPlateType.Police:
                // سبز پلیس با فونت سفید
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                SetPlateTextColors(Colors.White);
                break;

            default:
                // پلاک سفید شخصی استاندارد
                PlateVisualCard.Background = Brushes.White;
                SetPlateTextColors(Color.FromRgb(15, 23, 42));
                break;
        }
    }

    private void SetPlateTextColors(Color color)
    {
        var brush = new SolidColorBrush(color);
        PlatePart1Text.Foreground = brush;
        PlateLetterText.Foreground = brush;
        PlatePart2Text.Foreground = brush;
        PlateIranCodeText.Foreground = brush;
    }

    private void LoadImagePreview(Image imageControl, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            imageControl.Source = null;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            imageControl.Source = bmp;
        }
        catch
        {
            imageControl.Source = null;
        }
    }

    private async void BulkDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AnprEventsGrid.SelectedItems.OfType<AnprResult>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("هیچ رویدادی برای حذف انتخاب نشده است.", "توجه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"آیا از حذف دائم {IranianPlateNormalizer.ToPersianDigits(selected.Count.ToString())} رویداد پلاک‌خوانی از دیتابیس محلی اطمینان دارید؟",
            "تایید حذف",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                int deleted = await _archiveService.DeleteEventsAsync(selected.Select(s => s.EventId));
                MessageBox.Show($"{IranianPlateNormalizer.ToPersianDigits(deleted.ToString())} رویداد با موفقیت حذف گردید.", "حذف موفق", MessageBoxButton.OK, MessageBoxImage.Information);

                await LoadEventsAsync(PlateSearchBox.Text);
                await UpdateStatisticsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در حذف رویدادها: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void EnhanceSelectedPlate_Click(object sender, RoutedEventArgs e)
    {
        OpenEnhancerForSelection(isPlateCrop: true);
    }

    private void EnhanceSelectedVehicle_Click(object sender, RoutedEventArgs e)
    {
        OpenEnhancerForSelection(isPlateCrop: false);
    }

    private void OpenEnhancerForSelection(bool isPlateCrop)
    {
        if (AnprEventsGrid.SelectedItem is not AnprResult item)
        {
            var emptyWin = new PhotoEnhancerWindow(_pipeline) { Owner = this };
            emptyWin.ShowDialog();
            return;
        }

        byte[]? bytes = isPlateCrop ? item.PlateCropJpeg : item.VehicleCropJpeg;
        string? filePath = isPlateCrop ? item.EncryptedPlateCropPath : item.EncryptedVehicleCropPath;

        if ((bytes == null || bytes.Length == 0) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                bytes = File.ReadAllBytes(filePath);
            }
            catch { }
        }

        if (bytes == null || bytes.Length == 0)
        {
            MessageBox.Show("برای این ردیف تصویر ذخیره‌شده‌ای موجود نیست؛ پنجره به‌صورت آزاد باز می‌شود تا هر عکسی را انتخاب کنید.", "تصویر موجود نیست", MessageBoxButton.OK, MessageBoxImage.Information);
            var emptyWin = new PhotoEnhancerWindow(_pipeline) { Owner = this };
            emptyWin.ShowDialog();
            return;
        }

        string title = isPlateCrop
            ? $"برش پلاک {item.Plate.NormalizedText}"
            : $"تصویر خودرو ({item.Plate.NormalizedText})";

        var target = isPlateCrop
            ? PhotoEnhancementTarget.LicensePlate
            : PhotoEnhancementTarget.GeneralScene;

        var win = new PhotoEnhancerWindow(bytes, target, title, _pipeline)
        {
            Owner = this
        };
        win.ShowDialog();
    }
}
