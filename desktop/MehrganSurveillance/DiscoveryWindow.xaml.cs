using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MehrganSurveillance
{
    /// <summary>
    /// پنجره کشف هوشمند، اعتبارسنجی و ثبت دوربین‌های مداربسته (Smart Camera Discovery)
    /// </summary>
    public partial class DiscoveryWindow : Window
    {
        private readonly OnvifDiscoveryService _discoveryService;
        private readonly OnvifClientService _clientService;
        private readonly CameraRegistryService _registryService;

        private readonly ObservableCollection<DiscoveredCamera> _discoveredCameras = new();
        private CancellationTokenSource? _scanCts;

        // پلیر LibVLC بومی جهت تست تصویر زنده قبل از ثبت
        private LibVLC? _libVlc;
        private VlcMediaPlayer? _previewPlayer;

        public DiscoveryWindow()
        {
            InitializeComponent();

            _discoveryService = new OnvifDiscoveryService();
            _clientService = new OnvifClientService();
            _registryService = new CameraRegistryService();

            _discoveryService.LogMessage += LogEvent;
            _discoveryService.ScanProgress += OnScanProgress;
            _clientService.LogMessage += LogEvent;

            CamerasListBox.ItemsSource = _discoveredCameras;

            Loaded += DiscoveryWindow_Loaded;
            Closing += DiscoveryWindow_Closing;
        }

        private async void DiscoveryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // مقداردهی هسته LibVLC برای پیش‌نمایش تست زنده به صورت ناهمگام
                await Task.Run(() =>
                {
                    Core.Initialize();
                    _libVlc = new LibVLC("--rtsp-tcp", "--network-caching=300", "--avcodec-hw=any", "--no-video-title-show");
                });
                LogEvent("هسته بومی LibVLC جهت تست پیش‌نمایش استریم‌ها آماده شد.");
            }
            catch (Exception ex)
            {
                LogEvent($"هشدار در مقداردهی اولیه LibVLC: {ex.Message}");
            }

            // تعیین پیش‌فرض پیشوند زیرشبکه محلی
            string defaultPrefix = OnvifDiscoveryService.GetDefaultSubnetPrefix();
            SubnetPrefixInput.Text = defaultPrefix;

            LogEvent("پنجره کشف هوشمند آماده است. می‌توانید از دکمه کشف خودکار ONVIF یا اسکن کمکی زیرشبکه استفاده فرمایید.");
        }

        #region فرآیند کشف خودکار ONVIF و اسکن زیرشبکه

        private async void StartOnvifDiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            SetDiscoveryUiRunning(true, "در حال ارسال درخواست‌های WS-Discovery Probe و شنود پاسخ مالتی‌کست...");
            _scanCts = new CancellationTokenSource();

            try
            {
                var cameras = await _discoveryService.DiscoverOnvifDevicesAsync(timeoutMilliseconds: 4000, _scanCts.Token);
                foreach (var cam in cameras)
                {
                    AddOrUpdateDiscoveredCamera(cam);
                }

                if (cameras.Count == 0)
                {
                    LogEvent("هیچ دوربین ONVIF فعالی با پاسخ مالتی‌کست دریافت نشد. در صورت فعال نبودن ONVIF در دوربین، لطفاً از دکمه «اسکن کمکی زیرشبکه» یا «افزودن دستی» استفاده فرمایید.");
                }
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در فرآیند WS-Discovery: {ex.Message}");
            }
            finally
            {
                SetDiscoveryUiRunning(false, $"عملیات کشف پایان یافت. {_discoveredCameras.Count} دستگاه در لیست ثبت شد.");
            }
        }

        private async void StartSubnetScanButton_Click(object sender, RoutedEventArgs e)
        {
            string prefix = SubnetPrefixInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                MessageBox.Show("لطفاً پیشوند زیرشبکه معتبر (مانند 192.168.1) وارد نمایید.", "خطای ورودی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetDiscoveryUiRunning(true, $"در حال اسکن پورت‌های استریم (554, 3000, 80) در زیرشبکه {prefix}.1 تا 254...");
            DiscoveryProgressBar.IsIndeterminate = false;
            DiscoveryProgressBar.Value = 0;
            DiscoveryProgressBar.Maximum = 254;

            _scanCts = new CancellationTokenSource();

            try
            {
                var cameras = await _discoveryService.ScanSubnetAsync(prefix, 1, 254, _scanCts.Token);
                foreach (var cam in cameras)
                {
                    AddOrUpdateDiscoveredCamera(cam);
                }
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در اسکن زیرشبکه: {ex.Message}");
            }
            finally
            {
                SetDiscoveryUiRunning(false, $"اسکن زیرشبکه به اتمام رسید. مجموع دستگاه‌ها: {_discoveredCameras.Count}");
            }
        }

        private void ManualAddButton_Click(object sender, RoutedEventArgs e)
        {
            // افزودن یک ورودی دستی برای سناریوهایی که تجهیزات در ساب‌نت دیگری هستند یا فایروال مانع برودکست است
            string defaultIp = $"{SubnetPrefixInput.Text.Trim()}.168";
            var manualCam = new DiscoveredCamera
            {
                Id = $"MANUAL-{Guid.NewGuid():N}",
                IpAddress = defaultIp,
                HttpPort = 80,
                RtspPort = 554,
                Brand = "Tiandy (تیاندی)",
                Model = "افزودن دستی کاربر",
                DiscoverySource = "Manual",
                State = DeviceDiscoveryState.AuthenticationRequired,
                StateMessage = "ورودی دستی - آماده تنظیم نام کاربری، رمز عبور و تست استریم"
            };

            AddOrUpdateDiscoveredCamera(manualCam);
            CamerasListBox.SelectedItem = manualCam;
            LogEvent($"دستگاه دستی با آدرس موقت {defaultIp} به لیست اضافه شد. لطفاً آدرس و مشخصات را در پنل مقابل تنظیم نمایید.");
        }

        private void AddOrUpdateDiscoveredCamera(DiscoveredCamera cam)
        {
            var existing = _discoveredCameras.FirstOrDefault(c => string.Equals(c.IpAddress, cam.IpAddress, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Brand = cam.Brand;
                existing.Model = cam.Model;
                existing.OnvifServiceUrl = cam.OnvifServiceUrl;
                existing.HttpPort = cam.HttpPort;
                existing.State = cam.State;
                existing.StateMessage = cam.StateMessage;
            }
            else
            {
                _discoveredCameras.Add(cam);
            }
            DiscoveredCountText.Text = _discoveredCameras.Count.ToString();
        }

        private void OnScanProgress(int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                DiscoveryProgressBar.Value = current;
                DiscoveryProgressBar.Maximum = total;
                DiscoveryStatusText.Text = $"در حال اسکن آی‌پی‌ها ({current}/{total})...";
            });
        }

        private void SetDiscoveryUiRunning(bool isRunning, string status)
        {
            StartOnvifDiscoveryButton.IsEnabled = !isRunning;
            StartSubnetScanButton.IsEnabled = !isRunning;
            ManualAddButton.IsEnabled = !isRunning;
            DiscoveryProgressBar.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            DiscoveryProgressBar.IsIndeterminate = isRunning;
            DiscoveryStatusText.Text = status;
        }

        #endregion

        #region مدیریت انتخاب دوربین، اعتبارسنجی و دریافت پروفایل‌ها

        private void CamerasListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CamerasListBox.SelectedItem is not DiscoveredCamera selected)
                return;

            StopPreviewPlayback();

            SelectedCameraTitleText.Text = $"{selected.Brand} - {selected.Model}";
            SelectedCameraSubText = new TextBlock { Text = $"منبع کشف: {selected.DiscoverySource} | پورت وب: {selected.HttpPort} | پورت RTSP: {selected.RtspPort}" };
            SelectedCameraIpBadge.Text = selected.IpAddress;

            UsernameInput.Text = selected.Username;
            PasswordInput.Password = selected.Password;

            DeviceModelText.Text = selected.Model;
            DeviceFirmwareText.Text = selected.FirmwareVersion;
            DeviceSerialText.Text = selected.SerialNumber;

            RefreshProfilesUi(selected);
        }

        private async void AuthenticateButton_Click(object sender, RoutedEventArgs e)
        {
            if (CamerasListBox.SelectedItem is not DiscoveredCamera selected)
            {
                MessageBox.Show("لطفاً ابتدا یک دوربین را از لیست انتخاب فرمایید.", "پیام", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string user = UsernameInput.Text.Trim();
            string pass = PasswordInput.Password;

            if (string.IsNullOrEmpty(user))
            {
                MessageBox.Show("لطفاً نام کاربری را وارد نمایید.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AuthenticateButton.IsEnabled = false;
            AuthenticateButton.Content = "در حال بررسی...";

            try
            {
                bool success = await _clientService.AuthenticateAndLoadProfilesAsync(selected, user, pass);

                DeviceModelText.Text = selected.Model;
                DeviceFirmwareText.Text = selected.FirmwareVersion;
                DeviceSerialText.Text = selected.SerialNumber;

                RefreshProfilesUi(selected);

                // بازنشانی اجباری برای به‌روزرسانی رنگ برچسب در لیست
                CamerasListBox.Items.Refresh();

                if (success)
                {
                    LogEvent($"اعتبارسنجی با موفقیت انجام شد. استریم اصلی و فرعی برای {selected.IpAddress} در دسترس است.");
                }
                else
                {
                    LogEvent($"احراز هویت یا وب‌سرویس مستقیم با خطا مواجه شد. مسیرهای استاندارد سازنده ({selected.Brand}) تنظیم گردید.");
                }
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در اعتبارسنجی: {ex.Message}");
            }
            finally
            {
                AuthenticateButton.IsEnabled = true;
                AuthenticateButton.Content = "🔑 تست هویت و دریافت استریم‌ها";
            }
        }

        private void RefreshProfilesUi(DiscoveredCamera camera)
        {
            ProfilesComboBox.ItemsSource = null;

            if (camera.Profiles.Count > 0)
            {
                ProfilesComboBox.ItemsSource = camera.Profiles;
                ProfilesComboBox.SelectedIndex = 0;
            }
            else
            {
                // ایجاد پروفایل‌های پیش‌فرض
                string user = UsernameInput.Text.Trim();
                string pass = PasswordInput.Password;
                string mainUri = OnvifClientService.GenerateVendorRtspUri(camera.Brand, camera.IpAddress, camera.RtspPort, user, pass, 1);
                string subUri = OnvifClientService.GenerateVendorRtspUri(camera.Brand, camera.IpAddress, camera.RtspPort, user, pass, 2);

                var defaultProfiles = new[]
                {
                    new CameraStreamProfile { Name = "استریم اصلی (Main Stream)", RtspUri = mainUri, Resolution = "1080p/4K", IsSubStream = false },
                    new CameraStreamProfile { Name = "استریم فرعی (Sub Stream)", RtspUri = subUri, Resolution = "D1/720p", IsSubStream = true }
                };
                ProfilesComboBox.ItemsSource = defaultProfiles;
                ProfilesComboBox.SelectedIndex = 0;
            }
        }

        private void ProfilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProfilesComboBox.SelectedItem is CameraStreamProfile selectedProfile)
            {
                // نمایش امن آدرس RTSP با ماسک کلمه عبور
                MaskedRtspUrlBox.Text = CredentialProtectionService.MaskRtspUrl(selectedProfile.RtspUri);
            }
        }

        #endregion

        #region تست پخش زنده تصویر با موتور بومی LibVLC

        private void PreviewPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProfilesComboBox.SelectedItem is not CameraStreamProfile selectedProfile || string.IsNullOrEmpty(selectedProfile.RtspUri))
            {
                MessageBox.Show("لطفاً یک پروفایل استریم معتبر انتخاب نمایید.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StopPreviewPlayback();

            try
            {
                if (_libVlc == null)
                {
                    _libVlc = new LibVLC("--rtsp-tcp", "--network-caching=300", "--avcodec-hw=any");
                }

                _previewPlayer = new VlcMediaPlayer(_libVlc);
                PreviewVideoView.MediaPlayer = _previewPlayer;

                _previewPlayer.Playing += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        PreviewPlaceholder.Visibility = Visibility.Collapsed;
                        LogEvent("پیش‌نمایش زنده: فریم‌های ویدیویی واقعی با LibVLC در حال دریافت و رندر هستند.");
                    });
                };

                _previewPlayer.EncounteredError += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        PreviewPlaceholder.Visibility = Visibility.Visible;
                        LogEvent("پیش‌نمایش زنده: خطا در برقراری اتصال استریم RTSP پیش‌نمایش.");
                        StopPreviewPlayback();
                    });
                };

                string streamUrl = selectedProfile.RtspUri;
                // تزریق کلمه عبور جاری در صورتی که در آدرس نباشد
                string user = UsernameInput.Text.Trim();
                string pass = PasswordInput.Password;
                if (!string.IsNullOrEmpty(user) && streamUrl.IndexOf('@') < 0 && streamUrl.StartsWith("rtsp://"))
                {
                    string after = streamUrl.Substring("rtsp://".Length);
                    streamUrl = $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{after}";
                }

                using var media = new Media(_libVlc, new Uri(streamUrl));
                _previewPlayer.Play(media);

                PreviewPlayButton.IsEnabled = false;
                PreviewStopButton.IsEnabled = true;
                LogEvent($"در حال آغاز تست پیش‌نمایش زنده روی آدرس: {CredentialProtectionService.MaskRtspUrl(streamUrl)}");
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در شروع پیش‌نمایش زنده: {ex.Message}");
                StopPreviewPlayback();
            }
        }

        private void PreviewStopButton_Click(object sender, RoutedEventArgs e)
        {
            StopPreviewPlayback();
            LogEvent("پیش‌نمایش زنده توسط کاربر متوقف شد.");
        }

        private void StopPreviewPlayback()
        {
            if (_previewPlayer != null)
            {
                _previewPlayer.Stop();
                _previewPlayer.Dispose();
                _previewPlayer = null;
            }
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlayButton.IsEnabled = true;
            PreviewStopButton.IsEnabled = false;
        }

        #endregion

        #region ثبت دوربین در رجیستری پایدار با رمزنگاری DPAPI

        private void RegisterCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (CamerasListBox.SelectedItem is not DiscoveredCamera selected)
            {
                MessageBox.Show("لطفاً یک دوربین را جهت ثبت انتخاب فرمایید.", "پیام", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string user = UsernameInput.Text.Trim();
            string pass = PasswordInput.Password;

            if (string.IsNullOrWhiteSpace(user))
            {
                MessageBox.Show("نام کاربری دوربین الزامی است.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // رمزنگاری امن پسورد با مکانیزم Windows DPAPI
            string encryptedPassword = CredentialProtectionService.EncryptSecret(pass);

            // استخراج یا تولید آدرس‌های اصلی و فرعی
            string mainUri = selected.MainStreamUri;
            string subUri = selected.SubStreamUri;

            if (string.IsNullOrEmpty(mainUri))
            {
                mainUri = OnvifClientService.GenerateVendorRtspUri(selected.Brand, selected.IpAddress, selected.RtspPort, user, pass, 1);
            }
            if (string.IsNullOrEmpty(subUri))
            {
                subUri = OnvifClientService.GenerateVendorRtspUri(selected.Brand, selected.IpAddress, selected.RtspPort, user, pass, 2);
            }

            string preferredStream = "Main";
            if (ProfilesComboBox.SelectedItem is CameraStreamProfile selectedProfile && selectedProfile.IsSubStream)
            {
                preferredStream = "Sub";
            }

            var registeredCamera = new RegisteredCamera
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"{selected.Brand} - {selected.IpAddress}",
                IpAddress = selected.IpAddress,
                RtspPort = selected.RtspPort,
                Brand = selected.Brand,
                Model = selected.Model,
                Username = user,
                EncryptedPassword = encryptedPassword,
                MainStreamRtspUri = mainUri,
                SubStreamRtspUri = subUri,
                PreferredStreamType = preferredStream,
                RegisteredAt = DateTime.Now
            };

            bool saved = _registryService.RegisterOrUpdateCamera(registeredCamera, out string message);
            if (saved)
            {
                selected.State = DeviceDiscoveryState.StreamAvailable;
                selected.StateMessage = "دوربین با موفقیت در رجیستری پایدار مهرگان ثبت شد.";
                CamerasListBox.Items.Refresh();

                LogEvent($"[رجیستری پایدار] {message}");
                MessageBox.Show(
                    $"دوربین با مشخصات زیر ثبت گردید:\n\nنام: {registeredCamera.Name}\nآدرس IP: {registeredCamera.IpAddress}\nبرند: {registeredCamera.Brand}\nاستریم پیش‌فرض: {registeredCamera.PreferredStreamType}\n\nاطلاعات کاربری با Windows DPAPI امن‌سازی گردید.",
                    "ثبت موفق دوربین",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                LogEvent($"[خطای رجیستری] {message}");
                MessageBox.Show(message, "خطا در ثبت", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region کمکی‌های UI

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            DiscoveryLogListBox.Items.Clear();
        }

        private void LogEvent(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string entry = $"[{timestamp}] {message}";
                DiscoveryLogListBox.Items.Add(entry);
                if (DiscoveryLogListBox.Items.Count > 150)
                {
                    DiscoveryLogListBox.Items.RemoveAt(0);
                }
                DiscoveryLogListBox.ScrollIntoView(entry);
            });
        }

        private void DiscoveryWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _scanCts?.Cancel();
            StopPreviewPlayback();

            if (_libVlc != null)
            {
                _libVlc.Dispose();
                _libVlc = null;
            }
        }

        #endregion
    }
}
