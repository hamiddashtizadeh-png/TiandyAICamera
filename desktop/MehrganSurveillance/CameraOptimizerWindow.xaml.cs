using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance
{
    public partial class CameraOptimizerWindow : Window
    {
        private readonly CameraSettingsOptimizerService _optimizerService;
        private readonly CameraRegistryService _cameraRegistry;

        private CameraCapabilityProfile? _currentCapabilities;
        private CameraSettingsSnapshot? _currentSnapshot;
        private CameraOptimizationRecommendation? _currentRecommendation;
        private CameraOptimizationProfileType _selectedProfile = CameraOptimizationProfileType.NighttimeLicensePlate;

        public CameraOptimizerWindow()
        {
            InitializeComponent();

            _optimizerService = new CameraSettingsOptimizerService();
            _cameraRegistry = new CameraRegistryService();

            _optimizerService.LogMessage += OnLogMessageReceived;
            _optimizerService.AuditRecordAdded += OnAuditRecordAdded;

            Loaded += CameraOptimizerWindow_Loaded;
        }

        private void CameraOptimizerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRegisteredCameras();
            RefreshAuditHistoryGrid();
        }

        private void LoadRegisteredCameras()
        {
            try
            {
                var cameras = _cameraRegistry.GetAllCameras();
                CameraSelectorComboBox.Items.Clear();

                if (cameras.Count > 0)
                {
                    foreach (var cam in cameras)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = $"{cam.Name} ({cam.IpAddress}:{cam.RtspPort}) - {cam.Brand}",
                            Tag = cam
                        };
                        CameraSelectorComboBox.Items.Add(item);
                    }
                    CameraSelectorComboBox.SelectedIndex = 0;
                }
                else
                {
                    // دوربین پیش‌فرض نمونه
                    var defaultItem = new ComboBoxItem
                    {
                        Content = "دوربین هوشمند پلاک‌خوان تیاندی (192.168.1.100) - Tiandy",
                        Tag = new RegisteredCamera
                        {
                            Id = "tiandy-01",
                            Name = "دوربین ورودی پلاک‌خوان",
                            IpAddress = "192.168.1.100",
                            RtspPort = 554,
                            Username = "admin",
                            Brand = "Tiandy",
                            Model = "TC-NC214"
                        }
                    };
                    CameraSelectorComboBox.Items.Add(defaultItem);
                    CameraSelectorComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"خطا در بارگذاری لیست دوربین‌ها: {ex.Message}", false);
            }
        }

        private void CameraSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraSelectorComboBox.SelectedItem is ComboBoxItem item && item.Tag is RegisteredCamera cam)
            {
                CameraIpTextBox.Text = cam.IpAddress;
                CameraUserTextBox.Text = !string.IsNullOrEmpty(cam.Username) ? cam.Username : "admin";
                ManufacturerTextBlock.Text = !string.IsNullOrWhiteSpace(cam.Brand) ? cam.Brand : "Security Dev";

                // استعلام خودکار سریع
                _ = RunDetectionAndOptimizationAsync(autoScanPortsIfClosed: true);
            }
        }

        private async void ScanPortsButton_Click(object sender, RoutedEventArgs e)
        {
            string ip = CameraIpTextBox.Text.Trim();
            SetStatus($"در حال بررسی و اسکن سوکت‌های شبکه {ip}...", true);
            ScanPortsButton.IsEnabled = false;

            try
            {
                var details = await OnvifImagingProvider.ScanPortsDetailAsync(ip, 250);
                var openPorts = details.Where(x => x.IsOpen).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"گزارش اسکن پورت‌های دوربین ({ip}):");
                sb.AppendLine("----------------------------------------");

                foreach (var d in details)
                {
                    sb.AppendLine($"{(d.IsOpen ? "✔ باز" : "✖ بسته")} | پورت {d.Port}: {d.ServiceName}");
                }

                int selectedPort = 80;
                if (details.Any(d => d.Port == 8899 && d.IsOpen))
                {
                    selectedPort = 8899;
                    sb.AppendLine("\nنتیجه: پورت ۸۸۹۹ (ONVIF اختصاصی Security Dev) باز است و به عنوان پورت کنترل انتخاب شد.");
                }
                else if (details.Any(d => d.Port == 80 && d.IsOpen))
                {
                    selectedPort = 80;
                    sb.AppendLine("\nنتیجه: پورت ۸۰ (Web HTTP) باز است و انتخاب شد.");
                }
                else if (details.Any(d => d.Port == 34567 && d.IsOpen))
                {
                    selectedPort = 34567;
                    sb.AppendLine("\nنتیجه: پورت ۳۴۵۶۷ (پروتکل اختصاصی XM) باز است و انتخاب شد.");
                }
                else if (openPorts.Any())
                {
                    selectedPort = openPorts.First().Port;
                    sb.AppendLine($"\nنتیجه: پورت باز {selectedPort} انتخاب شد.");
                }
                else
                {
                    sb.AppendLine("\nهشدار: هیچ‌یک از پورت‌های وب و ONVIF باز پاسخ ندادند؛ ارتباط فقط از طریق استریم تصویر انجام می‌شود.");
                }

                CameraPortTextBox.Text = selectedPort.ToString();
                SetStatus($"اسکن کامل شد. پورت فعال: {selectedPort}", true);

                MessageBox.Show(sb.ToString(), "نتیجه اسکن پورت‌های دوربین", MessageBoxButton.OK, MessageBoxImage.Information);

                await RunDetectionAndOptimizationAsync(autoScanPortsIfClosed: false);
            }
            finally
            {
                ScanPortsButton.IsEnabled = true;
            }
        }

        private void QuickPort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string portStr)
            {
                CameraPortTextBox.Text = portStr;
                _ = RunDetectionAndOptimizationAsync(autoScanPortsIfClosed: false);
            }
        }

        private async void DetectCapabilitiesButton_Click(object sender, RoutedEventArgs e)
        {
            await RunDetectionAndOptimizationAsync(autoScanPortsIfClosed: true);
        }

        private async Task RunDetectionAndOptimizationAsync(bool autoScanPortsIfClosed = false)
        {
            string ip = CameraIpTextBox.Text.Trim();
            if (!int.TryParse(CameraPortTextBox.Text.Trim(), out int port) || port <= 0) port = 80;
            string user = CameraUserTextBox.Text.Trim();
            string pass = CameraPasswordBox.Password;
            string brand = ManufacturerTextBlock.Text;

            SetStatus($"در حال استعلام وضعیت دوربین {ip} روی پورت {port}...", true);
            DetectCapabilitiesButton.IsEnabled = false;
            ConnectionStateTextBlock.Text = "وضعیت: در حال بررسی...";
            ConnectionStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            try
            {
                // تست سوکت ۲۵۰ میلی‌ثانیه‌ای
                bool portOpen = await OnvifImagingProvider.IsPortOpenAsync(ip, port, 250);

                if (!portOpen && autoScanPortsIfClosed)
                {
                    // اسکن موازی و سریع پورت‌های متداول بدون فریز (حداکثر ۲۵۰ میلی‌ثانیه)
                    int[] commonPorts = new[] { 8899, 80, 3000, 554, 34567, 8000, 5000, 8080 };
                    int? foundPort = await OnvifImagingProvider.FindOpenPortAsync(ip, port, commonPorts, 250);
                    if (foundPort.HasValue && foundPort.Value != port)
                    {
                        port = foundPort.Value;
                        CameraPortTextBox.Text = port.ToString();
                        portOpen = true;
                    }
                }

                _currentCapabilities = await _optimizerService.DetectCapabilitiesAsync(
                    $"cam_{ip}", ip, port, user, pass, brand);

                UpdateCapabilitiesUI(_currentCapabilities);
                await GenerateRecommendationCoreAsync($"cam_{ip}", ip, port, _currentCapabilities.Manufacturer, _currentCapabilities.Model);

                SetStatus(_currentCapabilities.VerificationSummary, true);
                ConnectionStateTextBlock.Text = portOpen ? $"وضعیت: متصل به پورت {port}" : "وضعیت: بهینه‌ساز فعال";
                ConnectionStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            catch (Exception ex)
            {
                SetStatus($"خطا در ارتباط: {ex.Message}", false);
                ConnectionStateTextBlock.Text = "وضعیت: خطا";
                ConnectionStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            finally
            {
                DetectCapabilitiesButton.IsEnabled = true;
            }
        }

        private void UpdateCapabilitiesUI(CameraCapabilityProfile caps)
        {
            ManufacturerTextBlock.Text = caps.Manufacturer;
            ProviderNameTextBlock.Text = caps.ProviderName;
            VerificationStatusTextBlock.Text = caps.IsVerified ? "تأییدشده و فعال" : "پایه / بدون پاسخ";
            VerificationStatusTextBlock.Foreground = new SolidColorBrush(caps.IsVerified ? Color.FromRgb(16, 185, 129) : Color.FromRgb(245, 158, 11));

            // بروزرسانی وضعیت تگ‌ها
            SetTagVisual(TagShutter, caps.CanControlShutter, "کنترل شاتر (Exposure)");
            SetTagVisual(TagGain, caps.CanControlGain, "گین آنالوگ (Gain)");
            SetTagVisual(TagWdr, caps.CanControlWdr, "دامنه دینامیکی (WDR)");
            SetTagVisual(TagHlc, caps.CanControlHlc, "مهار نور چراغ (HLC)");
            SetTagVisual(TagDayNight, caps.CanControlDayNightMode, "سوئیچ روز/شب (ICR)");
            SetTagVisual(TagIr, caps.CanControlIr, "مادون قرمز (Smart IR)");
        }

        private void SetTagVisual(TextBlock tagBlock, bool isSupported, string label)
        {
            if (isSupported)
            {
                tagBlock.Text = $"✔ {label}";
                tagBlock.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                if (tagBlock.Parent is Border b)
                {
                    b.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                    b.Opacity = 1.0;
                }
            }
            else
            {
                tagBlock.Text = $"✖ {label} (پشتیبانی‌نشده)";
                tagBlock.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                if (tagBlock.Parent is Border b)
                {
                    b.Background = new SolidColorBrush(Color.FromRgb(20, 26, 36));
                    b.Opacity = 0.6;
                }
            }
        }

        private void ProfileRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (ProfileNightPlateRadio.IsChecked == true)
                _selectedProfile = CameraOptimizationProfileType.NighttimeLicensePlate;
            else if (ProfileDayPlateRadio.IsChecked == true)
                _selectedProfile = CameraOptimizationProfileType.DaytimeLicensePlate;
            else if (ProfileHighSpeedRadio.IsChecked == true)
                _selectedProfile = CameraOptimizationProfileType.HighSpeedVehicle;
            else if (ProfileLowLightRadio.IsChecked == true)
                _selectedProfile = CameraOptimizationProfileType.LowLightEnvironment;
            else if (ProfileGeneralRadio.IsChecked == true)
                _selectedProfile = CameraOptimizationProfileType.GeneralSurveillance;

            if (_currentCapabilities != null)
            {
                _ = GenerateRecommendationButton_Click_Async();
            }
        }

        private async void GenerateRecommendationButton_Click(object sender, RoutedEventArgs e)
        {
            await GenerateRecommendationButton_Click_Async();
        }

        private async Task GenerateRecommendationButton_Click_Async()
        {
            string ip = CameraIpTextBox.Text.Trim();
            if (!int.TryParse(CameraPortTextBox.Text.Trim(), out int port)) port = 80;

            SetStatus("در حال تحلیل وضعیت فریم‌ها و تولید پیشنهاد بهینه‌سازی هوشمند...", true);
            await GenerateRecommendationCoreAsync($"cam_{ip}", ip, port, ManufacturerTextBlock.Text);
            SetStatus("پیشنهاد جدید با موفقیت تولید و جدول مقایسه به‌روز شد.", true);
        }

        private async Task GenerateRecommendationCoreAsync(string camId, string ip, int port, string brand = "", string model = "")
        {
            string user = CameraUserTextBox.Text.Trim();
            string pass = CameraPasswordBox.Password;

            _currentRecommendation = await _optimizerService.GenerateRecommendationAsync(
                camId, ip, port, user, pass, _selectedProfile, brand, model);

            _currentSnapshot = _currentRecommendation.CurrentSettings;
            var proposed = _currentRecommendation.ProposedSettings;

            // به‌روزرسانی جدول Side-by-Side مقایسه
            CurShutterText.Text = _currentSnapshot.FormatShutterSpeed();
            PropShutterText.Text = proposed.FormatShutterSpeed();

            CurGainText.Text = $"{_currentSnapshot.Gain:F0}";
            PropGainText.Text = $"{proposed.Gain:F0}";

            CurHlcText.Text = _currentSnapshot.HlcEnabled ? $"فعال ({_currentSnapshot.HlcLevel:F0}%)" : "غیرفعال (Off)";
            CurHlcText.Foreground = new SolidColorBrush(_currentSnapshot.HlcEnabled ? Color.FromRgb(16, 185, 129) : Color.FromRgb(239, 68, 68));

            PropHlcText.Text = proposed.HlcEnabled ? $"فعال ({proposed.HlcLevel:F0}%) - پلاک‌خوانی" : "غیرفعال";
            PropHlcText.Foreground = new SolidColorBrush(proposed.HlcEnabled ? Color.FromRgb(16, 185, 129) : Color.FromRgb(203, 213, 225));

            CurWdrText.Text = _currentSnapshot.WdrEnabled ? $"فعال ({_currentSnapshot.WdrLevel:F0}%)" : "غیرفعال";
            PropWdrText.Text = proposed.WdrEnabled ? $"فعال ({proposed.WdrLevel:F0}%)" : "غیرفعال";

            CurIrText.Text = $"{_currentSnapshot.IrMode} / {_currentSnapshot.DayNightMode}";
            PropIrText.Text = $"{proposed.IrMode} ({proposed.IrIntensity:F0}%) / {proposed.DayNightMode}";

            EngineConfidenceTextBlock.Text = $"(میزان اطمینان موتور: {Math.Round(_currentRecommendation.EngineConfidence * 100)}٪)";

            // به‌روزرسانی پنل دلایل فنی فارسی
            ExecutiveSummaryTextBlock.Text = _currentRecommendation.ExecutiveSummary;

            if (_currentRecommendation.ParameterReasons.TryGetValue("Shutter", out var shutReason))
            {
                ShutterReasonTextBlock.Text = $"• سرعت شاتر: {shutReason}";
                ShutterReasonTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                ShutterReasonTextBlock.Visibility = Visibility.Collapsed;
            }

            if (_currentRecommendation.ParameterReasons.TryGetValue("HLC", out var hlcReason))
            {
                HlcReasonTextBlock.Text = $"• مهار نور شدید (HLC): {hlcReason}";
                HlcReasonTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                HlcReasonTextBlock.Visibility = Visibility.Collapsed;
            }

            if (_currentRecommendation.ParameterReasons.TryGetValue("Gain", out var gainReason))
            {
                GainReasonTextBlock.Text = $"• گین سنسور: {gainReason}";
                GainReasonTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                GainReasonTextBlock.Visibility = Visibility.Collapsed;
            }
        }

        private void AdminApprovalCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            ApplySettingsButton.IsEnabled = (AdminApprovalCheckBox.IsChecked == true);
        }

        private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRecommendation == null)
            {
                MessageBox.Show("هیچ پیشنهادی برای اعمال موجود نیست.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (AdminApprovalCheckBox.IsChecked != true)
            {
                MessageBox.Show("اعمال تنظیمات مستلزم تأیید صریح مدیر سیستم می‌باشد.", "تأیید امنیتی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = CameraIpTextBox.Text.Trim();
            if (!int.TryParse(CameraPortTextBox.Text.Trim(), out int port)) port = 80;
            string user = CameraUserTextBox.Text.Trim();
            string pass = CameraPasswordBox.Password;

            ApplySettingsButton.IsEnabled = false;
            RollbackButton.IsEnabled = false;
            SetStatus("در حال ایجاد Snapshot، اعمال تنظیمات بر روی دوربین و اجرای اعتبارسنجی بازخوانی...", true);

            try
            {
                var (success, msg, record) = await _optimizerService.ApplySettingsWithSafetyAsync(
                    $"cam_{ip}", ip, port, user, pass,
                    _currentRecommendation.ProposedSettings,
                    _selectedProfile.ToString(),
                    adminConfirmed: true,
                    adminUsername: "مدیر ارشد پایش مهرگان",
                    manufacturer: ManufacturerTextBlock.Text);

                if (success)
                {
                    SetStatus($"پروفایل بهینه‌سازی «{_selectedProfile}» با موفقیت اعمال گردید.", true);
                    MessageBox.Show(
                        $"تنظیمات بهینه‌ساز با موفقیت بر روی دوربین و پردازشگر ویدیویی اعمال شد:\n\n" +
                        $"• سرعت شاتر (Exposure): {_currentRecommendation.ProposedSettings.FormatShutterSpeed()}\n" +
                        $"• مهار نور شدید (HLC): {(_currentRecommendation.ProposedSettings.HlcEnabled ? $"فعال ({_currentRecommendation.ProposedSettings.HlcLevel:F0}%)" : "غیرفعال")}\n" +
                        $"• گین سنسور (Gain): {_currentRecommendation.ProposedSettings.Gain:F0}\n" +
                        $"• وضعیت فیلتر شب/روز: {_currentRecommendation.ProposedSettings.DayNightMode}\n\n" +
                        $"فرامین سخت‌افزاری به دوربین ارسال شد و فیلترهای بصری بهینه‌سازی بلافاصله روی تصویر زنده و هسته پلاک‌خوان فعال گردید.\n" +
                        $"اسنپ‌شات وضعیت قبلی جهت امکان بازگردانی (Rollback) در سیستم ذخیره شد.",
                        "تأیید اعمال بهینه‌سازی تصویر",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // بروزرسانی مقادیر جاری در جدول مقایسه
                    _currentSnapshot = record.AfterSnapshot;
                    CurShutterText.Text = _currentSnapshot.FormatShutterSpeed();
                    CurGainText.Text = $"{_currentSnapshot.Gain:F0}";
                    CurHlcText.Text = _currentSnapshot.HlcEnabled ? $"فعال ({_currentSnapshot.HlcLevel:F0}%)" : "غیرفعال (Off)";
                    CurHlcText.Foreground = new SolidColorBrush(_currentSnapshot.HlcEnabled ? Color.FromRgb(16, 185, 129) : Color.FromRgb(239, 68, 68));
                    CurWdrText.Text = _currentSnapshot.WdrEnabled ? $"فعال ({_currentSnapshot.WdrLevel:F0}%)" : "غیرفعال";
                    CurIrText.Text = $"{_currentSnapshot.IrMode} / {_currentSnapshot.DayNightMode}";
                }
                else
                {
                    SetStatus($"خطا: {msg}", false);
                    MessageBox.Show($"خطا در اعمال تنظیمات روی دوربین:\n{msg}\nتنظیمات قبلی دوربین بدون تغییر حفظ گردید.", "خطای اعمال", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                RefreshAuditHistoryGrid();
            }
            catch (Exception ex)
            {
                SetStatus($"استثنا در فرآیند اعمال: {ex.Message}", false);
            }
            finally
            {
                ApplySettingsButton.IsEnabled = (AdminApprovalCheckBox.IsChecked == true);
                RollbackButton.IsEnabled = true;
            }
        }

        private async void RollbackButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "آیا از بازگردانی (Rollback) تنظیمات این دوربین به اسنپ‌شات پایدار قبلی اطمینان دارید؟",
                "تأیید بازگردانی تنظیمات",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            string ip = CameraIpTextBox.Text.Trim();
            if (!int.TryParse(CameraPortTextBox.Text.Trim(), out int port)) port = 80;
            string user = CameraUserTextBox.Text.Trim();
            string pass = CameraPasswordBox.Password;

            SetStatus("در حال بازگردانی تنظیمات دوربین به اسنپ‌شات اولیه (Rollback)...", true);
            RollbackButton.IsEnabled = false;

            try
            {
                var (success, msg) = await _optimizerService.RollbackToPreviousSettingsAsync(
                    $"cam_{ip}", ip, port, user, pass,
                    adminUsername: "مدیر ارشد پایش مهرگان",
                    manufacturer: ManufacturerTextBlock.Text);

                if (success)
                {
                    SetStatus("عملیات بازگردانی (Rollback) با موفقیت انجام شد.", true);
                    MessageBox.Show("تنظیمات دوربین با موفقیت به اسنپ‌شات پایدار قبلی بازگردانی شد.", "بازگردانی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
                    await GenerateRecommendationCoreAsync($"cam_{ip}", ip, port, ManufacturerTextBlock.Text);
                }
                else
                {
                    SetStatus($"خطا در بازگردانی: {msg}", false);
                    MessageBox.Show($"خطا در بازگردانی تنظیمات:\n{msg}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                RefreshAuditHistoryGrid();
            }
            catch (Exception ex)
            {
                SetStatus($"استثنا در بازگردانی: {ex.Message}", false);
            }
            finally
            {
                RollbackButton.IsEnabled = true;
            }
        }

        private void RefreshAuditLogButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshAuditHistoryGrid();
        }

        private void RefreshAuditHistoryGrid()
        {
            try
            {
                var history = _optimizerService.GetAuditHistory();
                AuditLogDataGrid.ItemsSource = null;
                AuditLogDataGrid.ItemsSource = history;
            }
            catch
            {
                // نادیده گرفتن خطا
            }
        }

        private void OnAuditRecordAdded(OptimizationAuditRecord rec)
        {
            Dispatcher.Invoke(RefreshAuditHistoryGrid);
        }

        private void OnLogMessageReceived(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                StatusBarMessageTextBlock.Text = msg;
            });
        }

        private void SetStatus(string message, bool isSuccess)
        {
            StatusBarMessageTextBlock.Text = message;
            StatusBarMessageTextBlock.Foreground = new SolidColorBrush(isSuccess ? Color.FromRgb(148, 163, 184) : Color.FromRgb(239, 68, 68));
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
