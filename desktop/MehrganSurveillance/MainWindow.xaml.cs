using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MehrganSurveillance.Controls;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance
{
    /// <summary>
    /// پنجره اصلی سامانه نظارت تصویری هوشمند مهرگان
    /// مدیریت پخش چنددوربینه زنده در چیدمان‌های ۱×۱، ۲×۲ و ۳×۳ با LibVLCSharp و RTSP over TCP
    /// </summary>
    public partial class MainWindow : Window
    {
        // نمونه اشتراکی سراسری موتور LibVLC (تنها یک نمونه در کل برنامه ساخته و نگهداری می‌شود)
        private LibVLC? _sharedLibVlc;
        private readonly CameraRegistryService _registryService = new();
        private List<RegisteredCamera> _registeredCameras = new();

        private readonly List<CameraCell> _cells = new();
        private readonly List<CameraChannel> _channels = new();

        private int _currentLayout = 4; // 1, 4, or 9
        private int _previousLayout = 4;
        private int? _maximizedSlotIndex = null;
        private int _selectedSlotIndex = 0;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // ۱. مقداردهی اولیه هسته باینری نیتیو LibVLC
                Core.Initialize();
                LogEvent("هسته بومی LibVLC با موفقیت در ویندوز بارگذاری گردید.");

                // ۲. ایجاد نمونه اشتراکی سراسری LibVLC با تنظیمات RTSP over TCP و کدک سخت‌افزاری
                string[] options = new[]
                {
                    "--rtsp-tcp",               // پروتکل لایه انتقال قطعی TCP
                    "--network-caching=300",     // بافر بهینه شبکه
                    "--avcodec-hw=any",          // شتاب‌دهنده گرافیکی خودکار (D3D11VA/DirectX) با بازگشت نرم‌افزاری
                    "--no-video-title-show",     // عدم نمایش نام ویدیو روی فریم
                    "--no-sub-autodetect-file",
                    "--clock-jitter=0"
                };

                _sharedLibVlc = new LibVLC(options);
                LogEvent("نمونه اشتراکی موتور LibVLC (Shared Instance) با پارامترهای کم‌تاخیر راه‌اندازی شد.");

                // ۳. ثبت ۹ سلول کنترل و تزریق LibVLC اشتراکی به هر یک
                RegisterCells();

                // ۴. بارگذاری اطلاعات دوربین‌ها از رجیستری پایدار و انتساب اولیه اسلات‌ها
                LoadRegisteredCameras();
                InitializeCameraSlots();

                // ۵. تنظیم چیدمان پیش‌فرض ۲×۲ (چهار تصویر)
                SetLayout(4);

                SelectSlot(0);
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در راه‌اندازی موتور چنددوربینه LibVLC: {ex.Message}");
                MessageBox.Show(
                    $"خطا در راه‌اندازی موتور ویدیویی LibVLC:\n{ex.Message}\n\nلطفاً اطمینان حاصل کنید پیش‌نیازهای VideoLAN.LibVLC.Windows در دسترس هستند.",
                    "خطای راه‌اندازی",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RegisterCells()
        {
            _cells.Clear();
            _cells.AddRange(new[]
            {
                CameraCell0, CameraCell1, CameraCell2,
                CameraCell3, CameraCell4, CameraCell5,
                CameraCell6, CameraCell7, CameraCell8
            });

            for (int i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                if (_sharedLibVlc != null)
                {
                    cell.SetSharedLibVlc(_sharedLibVlc);
                }

                cell.RequestMaximize += Cell_RequestMaximize;
                cell.RequestRestore += Cell_RequestRestore;
                cell.RequestAssignCamera += Cell_RequestAssignCamera;
                cell.CellSelected += Cell_CellSelected;
                cell.LogMessage += (s, msg) => LogEvent(msg);
            }
        }

        private void InitializeCameraSlots()
        {
            _channels.Clear();

            for (int i = 0; i < 9; i++)
            {
                var channel = new CameraChannel
                {
                    SlotIndex = i,
                    DisplayName = $"دوربین اسلات {i + 1}"
                };

                // اگر در رجیستری دوربینی متناظر با این اسلات وجود دارد، تخصیص داده می‌شود
                if (i < _registeredCameras.Count)
                {
                    var reg = _registeredCameras[i];
                    channel.DisplayName = reg.Name;
                    channel.IpAddress = reg.IpAddress;
                    channel.RtspPort = reg.RtspPort;
                    channel.Brand = reg.Brand;

                    string decPass = CredentialProtectionService.DecryptSecret(reg.EncryptedPassword);
                    channel.MainStreamUrl = BuildFullRtspUrl(reg.MainStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/1");
                    channel.SubStreamUrl = BuildFullRtspUrl(reg.SubStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/2");
                }
                else
                {
                    // اسلات‌های بعدی در صورت خالی بودن، با الگوی پیش‌فرض تیاندی آماده نگه داشته می‌شوند
                    channel.IpAddress = $"192.168.1.{100 + i}";
                    channel.MainStreamUrl = $"rtsp://admin:password@192.168.1.{100 + i}:554/1/1";
                    channel.SubStreamUrl = $"rtsp://admin:password@192.168.1.{100 + i}:554/1/2";
                }

                _channels.Add(channel);
                _cells[i].BindChannel(channel, useSubStream: _currentLayout > 1);
            }
        }

        private static string BuildFullRtspUrl(string rawUri, string ip, int port, string user, string pass, string defaultPath)
        {
            if (string.IsNullOrWhiteSpace(rawUri))
            {
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                {
                    return $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{ip}:{port}{defaultPath}";
                }
                return $"rtsp://{ip}:{port}{defaultPath}";
            }

            if (rawUri.IndexOf('@') < 0 && !string.IsNullOrEmpty(user))
            {
                string afterPrefix = rawUri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                    ? rawUri.Substring("rtsp://".Length)
                    : rawUri;

                return $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{afterPrefix}";
            }

            return rawUri;
        }

        #region مدیریت چیدمان ماتریسی (Layout Management: 1x1, 2x2, 3x3)

        public void SetLayout(int count)
        {
            // اگر در حالت تمام‌صفحه بودیم، ابتدا از تمام‌صفحه خارج می‌شویم
            if (_maximizedSlotIndex.HasValue)
            {
                ExitMaximizedInternal();
            }

            _currentLayout = count;
            _previousLayout = count;

            UpdateLayoutButtonStyles();

            bool isMultiCamera = count > 1;

            if (count == 1)
            {
                CameraGrid.Rows = 1;
                CameraGrid.Columns = 1;

                // در حالت ۱×۱ تنها سلول اول (یا سلول فعال) با کیفیت اصلی Main-stream نمایش می‌یابد
                for (int i = 0; i < 9; i++)
                {
                    if (i == 0)
                    {
                        _cells[i].Visibility = Visibility.Visible;
                        _cells[i].SwitchStreamQuality(useSubStream: false); // استریم اصلی باکیفیت /1/1
                    }
                    else
                    {
                        _cells[i].Visibility = Visibility.Collapsed;
                        _cells[i].StopPlayback(); // آزادسازی منابع سلول‌های نامرئی
                    }
                }

                LogEvent("چیدمان به حالت تک‌تصویر (۱×۱) تغییر یافت. استریم اصلی (/1/1) با رزولوشن کامل فعال شد.");
            }
            else if (count == 4)
            {
                CameraGrid.Rows = 2;
                CameraGrid.Columns = 2;

                // در حالت ۲×۲ سلول‌های ۰ تا ۳ فعال شده و برای بهینه‌سازی منابع به Sub-stream سوئیچ می‌نمایند
                for (int i = 0; i < 9; i++)
                {
                    if (i < 4)
                    {
                        _cells[i].Visibility = Visibility.Visible;
                        _cells[i].SwitchStreamQuality(useSubStream: true); // استریم سبک فرعی /1/2
                    }
                    else
                    {
                        _cells[i].Visibility = Visibility.Collapsed;
                        _cells[i].StopPlayback(); // توقف سلول‌های غیرفعال جهت حفظ GPU/CPU
                    }
                }

                LogEvent("چیدمان به حالت ۴ تصویر (۲×۲) تغییر یافت. استریم‌های فرعی (/1/2) جهت مدیریت پهنای‌باند فعال شدند.");
            }
            else if (count == 9)
            {
                CameraGrid.Rows = 3;
                CameraGrid.Columns = 3;

                // در حالت ۳×۳ تمام ۹ سلول با Sub-stream برای جلوگیری از اشباع کارت گرافیک اجرا می‌شوند
                for (int i = 0; i < 9; i++)
                {
                    _cells[i].Visibility = Visibility.Visible;
                    _cells[i].SwitchStreamQuality(useSubStream: true);
                }

                LogEvent("چیدمان به حالت ۹ تصویر (۳×۳) تغییر یافت. استریم‌های فرعی (/1/2) برای حداکثر ۹ دوربین همزمان فعال شدند.");
            }
        }

        private void UpdateLayoutButtonStyles()
        {
            var activeStyle = (Style)FindResource("PrimaryButtonStyle");
            var inactiveStyle = (Style)FindResource("SecondaryButtonStyle");

            Layout1x1Button.Style = (_currentLayout == 1 && !_maximizedSlotIndex.HasValue) ? activeStyle : inactiveStyle;
            Layout2x2Button.Style = (_currentLayout == 4 && !_maximizedSlotIndex.HasValue) ? activeStyle : inactiveStyle;
            Layout3x3Button.Style = (_currentLayout == 9 && !_maximizedSlotIndex.HasValue) ? activeStyle : inactiveStyle;
        }

        #endregion

        #region بزرگ‌نمایی و بازگشت (Maximize / Restore Workflow)

        private void Cell_RequestMaximize(object? sender, CameraCell cell)
        {
            int slot = cell.Channel.SlotIndex;
            LogEvent($"درخواست بزرگ‌نمایی کانال {slot + 1} دریافت گردید.");

            _previousLayout = _currentLayout;
            _maximizedSlotIndex = slot;

            CameraGrid.Rows = 1;
            CameraGrid.Columns = 1;

            for (int i = 0; i < 9; i++)
            {
                if (i == slot)
                {
                    _cells[i].Visibility = Visibility.Visible;
                    _cells[i].SetMaximizedState(true);
                    // سوئیچ به استریم اصلی (/1/1) با وضوح بالا
                    _cells[i].SwitchStreamQuality(useSubStream: false);
                }
                else
                {
                    _cells[i].Visibility = Visibility.Collapsed;
                    _cells[i].SetMaximizedState(false);
                }
            }

            MaximizedBannerText.Text = $"حالت تمام‌صفحه «{cell.Channel.DisplayName}» (کانال {slot + 1}) فعال است — استریم باکیفیت اصلی (Main-Stream /1/1).";
            MaximizedBanner.Visibility = Visibility.Visible;

            UpdateLayoutButtonStyles();
        }

        private void Cell_RequestRestore(object? sender, CameraCell cell)
        {
            ExitMaximizedInternal();
        }

        private void RestoreMaximized_Click(object sender, RoutedEventArgs e)
        {
            ExitMaximizedInternal();
        }

        private void ExitMaximizedInternal()
        {
            if (!_maximizedSlotIndex.HasValue) return;

            int slot = _maximizedSlotIndex.Value;
            LogEvent($"خروج از حالت تمام‌صفحه کانال {slot + 1} و بازگشت به چیدمان قبلی ({_previousLayout} تایی).");

            _cells[slot].SetMaximizedState(false);
            _maximizedSlotIndex = null;
            MaximizedBanner.Visibility = Visibility.Collapsed;

            // بازگرداندن چیدمان قبلی
            SetLayout(_previousLayout);
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _maximizedSlotIndex.HasValue)
            {
                ExitMaximizedInternal();
                e.Handled = true;
            }
        }

        #endregion

        #region کنترل و انتساب اسلات‌ها (Slot Selection & Assignment)

        private void Cell_CellSelected(object? sender, CameraCell cell)
        {
            SelectSlot(cell.Channel.SlotIndex);
        }

        private void SelectSlot(int index)
        {
            if (index < 0 || index >= _cells.Count) return;

            _selectedSlotIndex = index;
            for (int i = 0; i < _cells.Count; i++)
            {
                _cells[i].SetSelected(i == index);
            }

            var ch = _channels[index];
            ActiveSlotLabel.Text = $"کانال {index + 1} ({ch.DisplayName})";
            ManualRtspInput.Text = ch.MainStreamUrl;
        }

        private void Cell_RequestAssignCamera(object? sender, CameraCell cell)
        {
            SelectSlot(cell.Channel.SlotIndex);
            // باز کردن لیست کشویی انتخاب دوربین
            RegistrySelectComboBox.IsDropDownOpen = true;
        }

        private void AssignSelectedRegistryCamera_Click(object sender, RoutedEventArgs e)
        {
            if (RegistrySelectComboBox.SelectedItem is not RegisteredCamera reg)
            {
                MessageBox.Show("لطفاً ابتدا یک دوربین از فهرست ثبت‌شده انتخاب فرمایید.", "پیام", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var channel = _channels[_selectedSlotIndex];
            channel.DisplayName = reg.Name;
            channel.IpAddress = reg.IpAddress;
            channel.RtspPort = reg.RtspPort;
            channel.Brand = reg.Brand;

            string decPass = CredentialProtectionService.DecryptSecret(reg.EncryptedPassword);
            channel.MainStreamUrl = BuildFullRtspUrl(reg.MainStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/1");
            channel.SubStreamUrl = BuildFullRtspUrl(reg.SubStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/2");

            bool useSub = _currentLayout > 1 && !_maximizedSlotIndex.HasValue;
            _cells[_selectedSlotIndex].BindChannel(channel, useSubStream: useSub);

            ManualRtspInput.Text = channel.MainStreamUrl;
            ActiveSlotLabel.Text = $"کانال {_selectedSlotIndex + 1} ({channel.DisplayName})";

            LogEvent($"دوربین «{reg.Name}» ({reg.IpAddress}) به اسلات کانال {_selectedSlotIndex + 1} اختصاص یافت.");
        }

        private void ApplyManualRtsp_Click(object sender, RoutedEventArgs e)
        {
            string url = ManualRtspInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("لطفاً آدرس RTSP معتبر وارد نمایید.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var channel = _channels[_selectedSlotIndex];
            channel.MainStreamUrl = url;
            // اشتقاق خودکار استریم فرعی برای تیاندی
            channel.SubStreamUrl = channel.GetEffectiveStreamUrl(useSubStream: true);

            bool useSub = _currentLayout > 1 && !_maximizedSlotIndex.HasValue;
            _cells[_selectedSlotIndex].BindChannel(channel, useSubStream: useSub);

            string masked = CredentialProtectionService.MaskRtspUrl(url);
            LogEvent($"آدرس دستی {masked} به اسلات کانال {_selectedSlotIndex + 1} اعمال شد.");
        }

        private void Preset11_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInManualInput("/1/1");
        }

        private void Preset12_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInManualInput("/1/2");
        }

        private void Preset13_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInManualInput("/1/3");
        }

        private void SetStreamPathInManualInput(string newPath)
        {
            string current = ManualRtspInput.Text.Trim();
            string updated = Regex.Replace(current, @"(/\d+/\d+|/live\d+|/ch\d+/[^/]+|/Streaming/Channels/\d+)$", newPath);
            if (updated == current && !current.EndsWith(newPath))
            {
                int slashIndex = current.IndexOf('/', "rtsp://".Length);
                if (slashIndex > 0)
                {
                    updated = current.Substring(0, slashIndex) + newPath;
                }
                else
                {
                    updated = current + newPath;
                }
            }
            ManualRtspInput.Text = updated;
        }

        #endregion

        #region عملیات گروهی اتصال / قطع (Connect / Disconnect All)

        private void ConnectAllButton_Click(object sender, RoutedEventArgs e)
        {
            LogEvent($"کاربر دستور اتصال همه استریم‌های چیدمان فعال ({(_maximizedSlotIndex.HasValue ? "تمام‌صفحه" : _currentLayout + " تایی")}) را صادر کرد.");

            for (int i = 0; i < 9; i++)
            {
                if (_cells[i].Visibility == Visibility.Visible)
                {
                    _cells[i].StartPlayback();
                }
            }
        }

        private void DisconnectAllButton_Click(object sender, RoutedEventArgs e)
        {
            LogEvent("کاربر دستور قطع همه استریم‌ها را صادر کرد.");
            for (int i = 0; i < 9; i++)
            {
                _cells[i].StopPlayback();
            }
        }

        #endregion

        #region لاگ‌ها، رجیستری و پنجره کشف

        private void OpenDiscoveryWindow_Click(object sender, RoutedEventArgs e)
        {
            LogEvent("کاربر پنجره کشف هوشمند دوربین‌ها (ONVIF & Scan) را گشود.");
            var discoveryWin = new DiscoveryWindow
            {
                Owner = this
            };

            discoveryWin.ShowDialog();

            // پس از بستن پنجره، دوربین‌های جدید به رجیستری و لیست اضافه می‌شوند
            LoadRegisteredCameras();
        }

        private void LoadRegisteredCameras()
        {
            try
            {
                _registeredCameras = _registryService.GetAllCameras();
                RegistrySelectComboBox.ItemsSource = null;

                if (_registeredCameras.Count > 0)
                {
                    RegistrySelectComboBox.ItemsSource = _registeredCameras;
                    RegistrySelectComboBox.DisplayMemberPath = "DisplaySummary";
                    RegistrySelectComboBox.SelectedIndex = 0;
                    LogEvent($"تعداد {_registeredCameras.Count} دوربین از رجیستری پایدار مهرگان فراخوانی شد.");
                }
                else
                {
                    RegistrySelectComboBox.ItemsSource = new[] { "دوربینی ثبت نشده است (روی کشف هوشمند کلیک فرمایید)" };
                    RegistrySelectComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در بارگذاری رجیستری: {ex.Message}");
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            EventLogListBox.Items.Clear();
            LogEvent("تاریخچه لاگ‌ها پاک‌سازی گردید.");
        }

        private void LogEvent(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string entry = $"[{timestamp}] {message}";
                EventLogListBox.Items.Add(entry);
                EventLogListBox.ScrollIntoView(entry);
            });
        }

        #endregion

        #region خروج و آزادسازی ایمن منابع

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            LogEvent("در حال بستن سامانه و آزادسازی امن منابع پردازش ویدیو...");

            // ۱. توقف تمام ۹ سلول و آزادسازی مدیاپلیرهای مربوطه
            foreach (var cell in _cells)
            {
                cell.StopPlayback();
            }

            // ۲. آزادسازی موتور اشتراکی LibVLC (تنها در این بخش و به صورت سراسری انجام می‌شود)
            if (_sharedLibVlc != null)
            {
                try
                {
                    _sharedLibVlc.Dispose();
                }
                catch
                {
                    // نادیده‌گیری خطای احتمالی در زمان بسته‌شدن برنامه
                }
                finally
                {
                    _sharedLibVlc = null;
                }
            }
        }

        #endregion

        #region دکمه‌های ناوبری چیدمان
        private void Layout1x1Button_Click(object sender, RoutedEventArgs e) => SetLayout(1);
        private void Layout2x2Button_Click(object sender, RoutedEventArgs e) => SetLayout(4);
        private void Layout3x3Button_Click(object sender, RoutedEventArgs e) => SetLayout(9);
        #endregion
    }
}
