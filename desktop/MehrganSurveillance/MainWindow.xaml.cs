using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MehrganSurveillance.Controls;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance
{
    /// <summary>
    /// منطق تجاری فرم اصلی سامانه نظارت تصویری هوشمند مهرگان (VMS Enterprise)
    /// بهینه‌سازی شده با لودینگ غیرمسدودکننده (Non-blocking Async)، صف لاگ‌های باچ‌شده و حذف کامل لگ رابط کاربری
    /// </summary>
    public partial class MainWindow : Window
    {
        private LibVLC? _sharedLibVlc;
        private readonly CameraRegistryService _registryService = new();
        private List<RegisteredCamera> _registeredCameras = new();

        private readonly List<CameraCell> _cells = new();
        private readonly List<CameraChannel> _channels = new();

        private int _currentLayout = 4; // 1, 4, or 9
        private int _previousLayout = 4;
        private int? _maximizedSlotIndex = null;
        private int _selectedSlotIndex = 0;

        // سرویس‌های هوش مصنوعی و آرشیو محلی چهره و پلاک‌خوان ANPR
        private LocalFrameAcquisitionPipeline? _framePipeline;
        private readonly FaceDetectionPipeline _facePipeline = new();
        private FaceArchiveService? _archiveService;
        private readonly AnprPipeline _anprPipeline = new();
        private AnprArchiveService? _anprArchiveService;
        private readonly System.Collections.ObjectModel.ObservableCollection<LiveFaceFeedItem> _liveFaceFeed = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastLiveFeedTimePerCamera = new();
        private long _liveFaceCounter;

        // صف لاگ‌های باچ‌شده و تایمر اختصاصی جهت جلوگیری از فریز UI
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly DispatcherTimer _logBatchTimer = new();
        private readonly DispatcherTimer _clockTimer = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            KeyDown += MainWindow_KeyDown;

            // راه‌اندازی تایمر ساعت زنده مانیتورینگ
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => LiveClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            // راه‌اندازی تایمر باچینگ لاگ‌ها (هر ۲۵۰ میلی‌ثانیه یک‌بار لاگ‌های انباشته درج می‌شوند)
            _logBatchTimer.Interval = TimeSpan.FromMilliseconds(250);
            _logBatchTimer.Tick += LogBatchTimer_Tick;
            _logBatchTimer.Start();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterCells();

            // نمایش سریع وضعیت در حال بارگذاری بدون فریز پنجره
            EngineStatusText.Text = "در حال راه‌اندازی موتور...";
            EngineStatusDot.Fill = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            ActiveStreamsSummaryText.Text = "در حال بارگذاری بدون وقفه هسته LibVLC و رجیستری...";

            try
            {
                // ۱. مقداردهی اولیه LibVLC به صورت ناهمگام در پس‌زمینه (جلوگیری قطعی از فریز باز شدن برنامه)
                await Task.Run(() =>
                {
                    Core.Initialize();
                    string[] options = new[]
                    {
                        "--rtsp-tcp",
                        "--network-caching=300",
                        "--avcodec-hw=any",
                        "--no-video-title-show",
                        "--no-sub-autodetect-file",
                        "--clock-jitter=0"
                    };
                    _sharedLibVlc = new LibVLC(options);
                });

                LogEvent("هسته بومی LibVLC و شتاب‌دهنده گرافیکی با موفقیت در پس‌زمینه راه‌اندازی شد.");
                EngineStatusText.Text = "موتور فعال (TCP)";
                EngineStatusDot.Fill = (Brush)FindResource("SuccessBrush");

                // ۲. تزریق نمونه اشتراکی به تمام ۹ سلول
                if (_sharedLibVlc != null)
                {
                    foreach (var cell in _cells)
                    {
                        cell.SetSharedLibVlc(_sharedLibVlc);
                    }
                }

                // ۳. بارگذاری لیست دوربین‌های ثبت‌شده در رجیستری به صورت غیرهمگام
                await LoadRegisteredCamerasAsync();

                // ۴. مقداردهی اولیه خط لوله دریافت فریم، پایپ‌لاین تشخیص چهره و آرشیو محلی
                _framePipeline = new LocalFrameAcquisitionPipeline(_sharedLibVlc);
                _framePipeline.LogMessage += (msg) => LogEvent(msg);

                _facePipeline.Initialize();
                _facePipeline.EnablePipeline();
                _framePipeline.RegisterConsumer(_facePipeline);

                _archiveService = new FaceArchiveService();
                _archiveService.Config.IsEnabled = true;
                await _archiveService.InitializeAsync();

                // ۵. مقداردهی اولیه پایپ‌لاین پلاک‌خوان هوشمند و آرشیو محلی ANPR
                _anprPipeline.Initialize();
                _framePipeline.RegisterConsumer(_anprPipeline);
                _anprArchiveService = new AnprArchiveService();
                await _anprArchiveService.InitializeAsync();

                _anprPipeline.PlateRecognized += (anprResult) =>
                {
                    if (_anprArchiveService != null)
                    {
                        _ = _anprArchiveService.RecordEventAsync(anprResult);
                    }
                    LogEvent($"پلاک خودرو کشف شد: {anprResult.Plate.NormalizedText} (اطمینان: {anprResult.OcrConfidence:P0})");
                };

                LiveFaceCaptureListBox.ItemsSource = _liveFaceFeed;

                // اتصال رویداد تشخیص فریم به آرشیو، نمایش کادر سبز روی سلول و افزودن به پنل زنده با کنترل نرخ ثبت و استخراج عکس
                _facePipeline.FrameFaceDetected += (result, frame) =>
                {
                    _archiveService?.OnFaceDetected(result, frame);

                    var faces = result.Faces;
                    int chIndex = result.ChannelIndex;
                    string camId = result.CameraId;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (chIndex >= 0 && chIndex < _cells.Count)
                        {
                            _cells[chIndex].UpdateFaceDetections(faces);
                        }

                        if (faces != null && faces.Count > 0)
                        {
                            // کنترل نرخ ثبت رویدادهای زنده در فید (حداکثر یک‌بار در هر ۲ ثانیه برای هر دوربین)
                            var now = DateTime.UtcNow;
                            if (_lastLiveFeedTimePerCamera.TryGetValue(camId, out var lastTime))
                            {
                                if ((now - lastTime).TotalSeconds < 2.0)
                                {
                                    return; // جلوگیری از اسپم فید و افزایش بی‌رویه شمارنده
                                }
                            }
                            _lastLiveFeedTimePerCamera[camId] = now;

                            string camName = (chIndex >= 0 && chIndex < _channels.Count)
                                ? _channels[chIndex].DisplayName
                                : $"کانال {chIndex + 1}";

                            foreach (var face in faces)
                            {
                                var thumb = CreateThumbnailFromFrame(frame, face);
                                Interlocked.Increment(ref _liveFaceCounter);
                                _liveFaceFeed.Insert(0, new LiveFaceFeedItem
                                {
                                    CameraName = camName,
                                    TimestampString = DateTime.Now.ToString("HH:mm:ss"),
                                    ConfidenceText = $"{Math.Round(face.Confidence * 100)}%",
                                    ChannelIndex = chIndex,
                                    Thumbnail = thumb
                                });

                                while (_liveFaceFeed.Count > 50)
                                {
                                    _liveFaceFeed.RemoveAt(_liveFaceFeed.Count - 1);
                                }
                            }

                            LiveFaceCountBadgeText.Text = _liveFaceCounter.ToString();
                        }
                    }));
                };

                // ۴. مقداردهی اسلات‌های ۹ گانه
                InitializeCameraSlots();

                // ۵. انتخاب اسلات اولیه پس از ایجاد کامل کانال‌ها
                SelectSlot(0);

                // ۶. تنظیم چیدمان پیش‌فرض ۲×۲
                SetLayout(4);

                ActiveStreamsSummaryText.Text = "چیدمان ۲×۲ فعال (۴ اسلات آماده پایش زنده)";
            }
            catch (Exception ex)
            {
                EngineStatusText.Text = "خطای هسته";
                EngineStatusDot.Fill = (Brush)FindResource("DangerBrush");
                LogEvent($"خطا در راه‌اندازی پس‌زمینه LibVLC: {ex.Message}");
                ActiveStreamsSummaryText.Text = "خطا در بارگذاری موتور ویدیویی";
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
                cell.RequestMaximize += Cell_RequestMaximize;
                cell.RequestRestore += Cell_RequestRestore;
                cell.RequestAssignCamera += Cell_RequestAssignCamera;
                cell.CellSelected += Cell_CellSelected;
                cell.LogMessage += (s, msg) => LogEvent(msg);
                cell.PlaybackStarted += Cell_PlaybackStarted;
                cell.PlaybackStopped += Cell_PlaybackStopped;
                cell.FaceDetectionToggled += Cell_FaceDetectionToggled;
                cell.FaceArchiveToggled += Cell_FaceArchiveToggled;
                cell.AnprToggled += Cell_AnprToggled;
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
                    channel.IpAddress = string.Empty;
                    channel.MainStreamUrl = string.Empty;
                    channel.SubStreamUrl = string.Empty;
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
            if (_maximizedSlotIndex.HasValue)
            {
                ExitMaximizedInternal();
            }

            _currentLayout = count;
            _previousLayout = count;

            UpdateLayoutButtonStyles();

            if (count == 1)
            {
                CameraGrid.Rows = 1;
                CameraGrid.Columns = 1;

                for (int i = 0; i < 9; i++)
                {
                    if (i == 0)
                    {
                        _cells[i].Visibility = Visibility.Visible;
                        _cells[i].SwitchStreamQuality(useSubStream: false);
                    }
                    else
                    {
                        _cells[i].Visibility = Visibility.Collapsed;
                        _cells[i].StopPlayback();
                    }
                }
                ActiveStreamsSummaryText.Text = "چیدمان ۱×۱ فعال (استریم باکیفیت اصلی /1/1)";
                LogEvent("چیدمان به تک‌تصویر (۱×۱) سوئیچ شد.");
            }
            else if (count == 4)
            {
                CameraGrid.Rows = 2;
                CameraGrid.Columns = 2;

                for (int i = 0; i < 9; i++)
                {
                    if (i < 4)
                    {
                        _cells[i].Visibility = Visibility.Visible;
                        _cells[i].SwitchStreamQuality(useSubStream: true);
                    }
                    else
                    {
                        _cells[i].Visibility = Visibility.Collapsed;
                        _cells[i].StopPlayback();
                    }
                }
                ActiveStreamsSummaryText.Text = "چیدمان ۲×۲ فعال (۴ اسلات با استریم بهینه فرعی /1/2)";
                LogEvent("چیدمان به ۴ تصویر (۲×۲) سوئیچ شد.");
            }
            else if (count == 9)
            {
                CameraGrid.Rows = 3;
                CameraGrid.Columns = 3;

                for (int i = 0; i < 9; i++)
                {
                    _cells[i].Visibility = Visibility.Visible;
                    _cells[i].SwitchStreamQuality(useSubStream: true);
                }
                ActiveStreamsSummaryText.Text = "چیدمان ۳×۳ فعال (۹ اسلات نظارت همزمان)";
                LogEvent("چیدمان به ۹ تصویر (۳×۳) سوئیچ شد.");
            }
        }

        private void UpdateLayoutButtonStyles()
        {
            var activeBrush = (Brush)FindResource("PrimaryBrush");
            var inactiveBrush = Brushes.Transparent;

            Layout1x1Button.Background = (_currentLayout == 1 && !_maximizedSlotIndex.HasValue) ? activeBrush : inactiveBrush;
            Layout2x2Button.Background = (_currentLayout == 4 && !_maximizedSlotIndex.HasValue) ? activeBrush : inactiveBrush;
            Layout3x3Button.Background = (_currentLayout == 9 && !_maximizedSlotIndex.HasValue) ? activeBrush : inactiveBrush;
        }

        #endregion

        #region بزرگ‌نمایی و بازگشت (Maximize / Restore Workflow)

        private void Cell_RequestMaximize(object? sender, CameraCell cell)
        {
            int slot = cell.Channel.SlotIndex;
            LogEvent($"درخواست بزرگ‌نمایی کانال {slot + 1} دریافت شد.");

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
                    _cells[i].SwitchStreamQuality(useSubStream: false);
                }
                else
                {
                    _cells[i].Visibility = Visibility.Collapsed;
                    _cells[i].SetMaximizedState(false);
                }
            }

            MaximizedBannerText.Text = $"حالت تمام‌صفحه «{cell.Channel.DisplayName}» (کانال {slot + 1}) فعال است — استریم اصلی (Main-Stream /1/1).";
            MaximizedBanner.Visibility = Visibility.Visible;
            ActiveStreamsSummaryText.Text = $"حالت تمام‌صفحه کانال {slot + 1} با رزولوشن کامل";

            UpdateLayoutButtonStyles();
        }

        private void Cell_RequestRestore(object? sender, CameraCell cell) => ExitMaximizedInternal();
        private void RestoreMaximized_Click(object sender, RoutedEventArgs e) => ExitMaximizedInternal();

        private void ExitMaximizedInternal()
        {
            if (!_maximizedSlotIndex.HasValue) return;

            int slot = _maximizedSlotIndex.Value;
            LogEvent($"خروج از تمام‌صفحه کانال {slot + 1} و بازگشت به چیدمان {_previousLayout} تایی.");

            _cells[slot].SetMaximizedState(false);
            _maximizedSlotIndex = null;
            MaximizedBanner.Visibility = Visibility.Collapsed;

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

        #region انتخاب و تخصیص اسلات (Slot Selection & Inspection)

        private void Cell_CellSelected(object? sender, CameraCell cell)
        {
            SelectSlot(cell.Channel.SlotIndex);
        }

        private void SelectSlot(int index)
        {
            if (index < 0 || index >= _cells.Count || index >= _channels.Count) return;

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
            RegistryCamerasListBox.Focus();
        }

        private void AssignCurrentItemToActiveSlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is RegisteredCamera reg)
            {
                AssignCameraToSlot(reg, _selectedSlotIndex);
            }
        }

        private void RegistryCamerasListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // انتخاب در لیست
        }

        private void AssignCameraToSlot(RegisteredCamera reg, int slotIndex)
        {
            var channel = _channels[slotIndex];
            channel.DisplayName = reg.Name;
            channel.IpAddress = reg.IpAddress;
            channel.RtspPort = reg.RtspPort;
            channel.Brand = reg.Brand;

            string decPass = CredentialProtectionService.DecryptSecret(reg.EncryptedPassword);
            channel.MainStreamUrl = BuildFullRtspUrl(reg.MainStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/1");
            channel.SubStreamUrl = BuildFullRtspUrl(reg.SubStreamRtspUri, reg.IpAddress, reg.RtspPort, reg.Username, decPass, "/1/2");

            bool useSub = _currentLayout > 1 && !_maximizedSlotIndex.HasValue;
            _cells[slotIndex].BindChannel(channel, useSubStream: useSub);

            ManualRtspInput.Text = channel.MainStreamUrl;
            ActiveSlotLabel.Text = $"کانال {slotIndex + 1} ({channel.DisplayName})";

            LogEvent($"دوربین «{reg.Name}» به اسلات کانال {slotIndex + 1} اختصاص یافت.");
        }

        private void ApplyManualRtsp_Click(object sender, RoutedEventArgs e)
        {
            string url = ManualRtspInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("لطفاً آدرس RTSP معتبر وارد فرمایید.", "پیام", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var channel = _channels[_selectedSlotIndex];
            channel.MainStreamUrl = url;
            channel.SubStreamUrl = channel.GetEffectiveStreamUrl(useSubStream: true);

            bool useSub = _currentLayout > 1 && !_maximizedSlotIndex.HasValue;
            _cells[_selectedSlotIndex].BindChannel(channel, useSubStream: useSub);

            string masked = CredentialProtectionService.MaskRtspUrl(url);
            LogEvent($"آدرس دستی {masked} به اسلات کانال {_selectedSlotIndex + 1} اعمال گردید.");
        }

        private void Preset11_Click(object sender, RoutedEventArgs e) => SetStreamPathInManualInput("/1/1");
        private void Preset12_Click(object sender, RoutedEventArgs e) => SetStreamPathInManualInput("/1/2");
        private void Preset13_Click(object sender, RoutedEventArgs e) => SetStreamPathInManualInput("/1/3");

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

        #region کنترل گروهی استریم‌ها

        private void ConnectAllButton_Click(object sender, RoutedEventArgs e)
        {
            LogEvent("کاربر دکمه اتصال همه استریم‌های فعال را فشرد.");
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
            LogEvent("کاربر دکمه قطع کلیه استریم‌ها را فشرد.");
            for (int i = 0; i < 9; i++)
            {
                _cells[i].StopPlayback();
            }
        }

        #endregion

        #region رجیستری و پنجره کشف هوشمند

        private async void ReloadRegistryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadRegisteredCamerasAsync();
        }

        private async Task LoadRegisteredCamerasAsync()
        {
            try
            {
                _registeredCameras = await Task.Run(() => _registryService.GetAllCameras());
                RegistryCamerasListBox.ItemsSource = null;
                RegistryCamerasListBox.ItemsSource = _registeredCameras;
                RegistryCountText.Text = _registeredCameras.Count.ToString();

                LogEvent($"تعداد {_registeredCameras.Count} دوربین از رجیستری پایدار با موفقیت فراخوانی شد.");
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در فراخوانی رجیستری: {ex.Message}");
            }
        }

        private void SearchCameraInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchCameraInput.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                RegistryCamerasListBox.ItemsSource = _registeredCameras;
            }
            else
            {
                RegistryCamerasListBox.ItemsSource = _registeredCameras.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(c.Brand) && c.Brand.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(c.IpAddress) && c.IpAddress.Contains(query, StringComparison.OrdinalIgnoreCase))
                ).ToList();
            }
        }

        private async void OpenDiscoveryWindow_Click(object sender, RoutedEventArgs e)
        {
            LogEvent("کاربر پنجره کشف هوشمند دوربین‌ها را گشود.");
            var discoveryWin = new DiscoveryWindow
            {
                Owner = this
            };

            discoveryWin.ShowDialog();
            await LoadRegisteredCamerasAsync();
        }

        #endregion

        #region لاگ‌های باچ‌شده با عملکرد بالا (High-Performance Batched Logging)

        private void LogEvent(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logQueue.Enqueue($"[{timestamp}] {message}");
        }

        private void LogBatchTimer_Tick(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            int processed = 0;
            string? lastItem = null;

            while (_logQueue.TryDequeue(out string? item) && processed < 30)
            {
                EventLogListBox.Items.Add(item);
                lastItem = item;
                processed++;
            }

            // نگه داشتن حداکثر ۱۵۰ ردیف لاگ در حافظه جهت تضمین سرعت و عدم افت فریم
            while (EventLogListBox.Items.Count > 150)
            {
                EventLogListBox.Items.RemoveAt(0);
            }

            if (lastItem != null && DiagnosticsDrawer.Visibility == Visibility.Visible)
            {
                EventLogListBox.ScrollIntoView(lastItem);
            }
        }

        private void ToggleLogPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsDrawer.Visibility == Visibility.Visible)
            {
                DiagnosticsDrawer.Visibility = Visibility.Collapsed;
                ToggleLogPanelButton.Content = "📋 وقایع حقیقی LibVLC ▲";
            }
            else
            {
                DiagnosticsDrawer.Visibility = Visibility.Visible;
                ToggleLogPanelButton.Content = "📋 وقایع حقیقی LibVLC ▼";
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            EventLogListBox.Items.Clear();
            LogEvent("تاریخچه لاگ‌های LibVLC پاک‌سازی شد.");
        }

        #endregion

        #region خروج ایمن

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _clockTimer.Stop();
            _logBatchTimer.Stop();

            foreach (var cell in _cells)
            {
                cell.StopPlayback();
            }

            if (_framePipeline != null)
            {
                _ = _framePipeline.DisposeAsync();
            }

            if (_sharedLibVlc != null)
            {
                try
                {
                    _sharedLibVlc.Dispose();
                }
                catch { }
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

        private void OpenFaceArchiveWindow_Click(object sender, RoutedEventArgs e)
        {
            var archiveWin = new FaceArchiveWindow(_archiveService, _facePipeline)
            {
                Owner = this
            };
            archiveWin.ShowDialog();
        }

        private void OpenAnprWindow_Click(object sender, RoutedEventArgs e)
        {
            var anprWin = new AnprWindow(_anprPipeline, _anprArchiveService)
            {
                Owner = this
            };
            anprWin.ShowDialog();
        }

        private void ToggleLiveFacePanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (LiveFacePanelBorder.Visibility == Visibility.Visible)
            {
                LiveFacePanelBorder.Visibility = Visibility.Collapsed;
                LiveFacePanelColumn.Width = new GridLength(0);
            }
            else
            {
                LiveFacePanelBorder.Visibility = Visibility.Visible;
                LiveFacePanelColumn.Width = new GridLength(280);
            }
        }

        private void CloseLiveFacePanel_Click(object sender, RoutedEventArgs e)
        {
            LiveFacePanelBorder.Visibility = Visibility.Collapsed;
            LiveFacePanelColumn.Width = new GridLength(0);
        }

        private void ClearLiveFacesList_Click(object sender, RoutedEventArgs e)
        {
            _liveFaceFeed.Clear();
            _liveFaceCounter = 0;
            LiveFaceCountBadgeText.Text = "۰";
        }

        private void FaceOverlayLiveToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = FaceOverlayLiveToggle.IsChecked == true;
            foreach (var cell in _cells)
            {
                cell.SetFaceOverlayVisible(isChecked);
            }
        }

        private void ViewFaceInArchive_Click(object sender, RoutedEventArgs e)
        {
            OpenFaceArchiveWindow_Click(sender, e);
        }

        private void MasterFaceDetectionToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isEnabled = MasterFaceDetectionToggle.IsChecked == true;
            if (isEnabled)
            {
                _facePipeline.EnablePipeline();
                FaceAiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                FaceAiStatusText.Text = "هوش مصنوعی چهره فعال است";
                LogEvent("پردازش هوش مصنوعی چهره توسط کاربر به صورت سراسری فعال شد.");

                // راه‌اندازی استخراج فریم برای کانال‌های فعال
                for (int i = 0; i < _cells.Count; i++)
                {
                    if (_channels[i].IsAssigned || _channels[i].IsConnected)
                    {
                        StartAiAcquisitionForChannel(_channels[i]);
                    }
                }
            }
            else
            {
                _facePipeline.DisablePipeline();
                FaceAiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                FaceAiStatusText.Text = "هوش مصنوعی چهره غیرفعال شد";
                LogEvent("پردازش هوش مصنوعی چهره غیرفعال شد.");

                foreach (var cell in _cells)
                {
                    cell.UpdateFaceDetections(new List<FaceBoundingBox>());
                }
            }
        }

        private void StartAiAcquisitionForChannel(CameraChannel channel)
        {
            if (_framePipeline == null || channel == null) return;
            if (channel.IsFaceDetectionEnabled || channel.IsAnprEnabled)
            {
                _ = _framePipeline.StartChannelAcquisitionAsync(channel);
            }
        }

        private void StopAiAcquisitionForChannel(int slotIndex)
        {
            if (_framePipeline == null) return;
            _ = _framePipeline.StopChannelAcquisitionAsync(slotIndex);
        }

        private void Cell_PlaybackStarted(object? sender, CameraCell cell)
        {
            if (cell?.Channel != null)
            {
                StartAiAcquisitionForChannel(cell.Channel);
            }
        }

        private void Cell_PlaybackStopped(object? sender, CameraCell cell)
        {
            if (cell?.Channel != null)
            {
                StopAiAcquisitionForChannel(cell.Channel.SlotIndex);
            }
        }

        private void Cell_FaceDetectionToggled(object? sender, (CameraCell Cell, bool Enabled) args)
        {
            if (args.Enabled)
            {
                StartAiAcquisitionForChannel(args.Cell.Channel);
            }
            else if (!args.Cell.Channel.IsAnprEnabled)
            {
                StopAiAcquisitionForChannel(args.Cell.Channel.SlotIndex);
                args.Cell.UpdateFaceDetections(new List<FaceBoundingBox>());
            }
        }

        private void Cell_FaceArchiveToggled(object? sender, (CameraCell Cell, bool Enabled) args)
        {
            if (_archiveService != null)
            {
                _archiveService.Config.IsEnabled = args.Enabled;
            }
        }

        private void Cell_AnprToggled(object? sender, (CameraCell Cell, bool Enabled) args)
        {
            if (args.Enabled)
            {
                StartAiAcquisitionForChannel(args.Cell.Channel);
            }
            else if (!args.Cell.Channel.IsFaceDetectionEnabled)
            {
                StopAiAcquisitionForChannel(args.Cell.Channel.SlotIndex);
            }
        }

        private static System.Windows.Media.ImageSource? CreateThumbnailFromFrame(AcquiredFrame frame, FaceBoundingBox face)
        {
            try
            {
                var (cx, cy, cw, ch) = face.ToPixelCoordinates(frame.Width, frame.Height);
                if (cw <= 4 || ch <= 4) return null;

                // حاشیه برای قاب‌بندی زیباتر پرتره
                int padX = (int)(cw * 0.12);
                int padY = (int)(ch * 0.12);
                cx = Math.Max(0, cx - padX);
                cy = Math.Max(0, cy - padY);
                cw = Math.Min(frame.Width - cx, cw + (padX * 2));
                ch = Math.Min(frame.Height - cy, ch + (padY * 2));

                int stride = cw * 4;
                byte[] crop = new byte[stride * ch];
                var raw = frame.PixelData.Span;

                for (int y = 0; y < ch; y++)
                {
                    int srcOffset = ((cy + y) * frame.Stride) + (cx * 4);
                    int dstOffset = y * stride;
                    if (srcOffset + stride <= raw.Length && dstOffset + stride <= crop.Length)
                    {
                        raw.Slice(srcOffset, stride).CopyTo(crop.AsSpan(dstOffset, stride));
                    }
                }

                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                    cw, ch, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null, crop, stride);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
        #endregion
    }

    /// <summary>
    /// مدل نمایشی فید زنده‌ی چهره‌های شناسایی‌شده در پنل کناری
    /// </summary>
    public class LiveFaceFeedItem
    {
        public string CameraName { get; init; } = string.Empty;
        public string TimestampString { get; init; } = string.Empty;
        public string ConfidenceText { get; init; } = string.Empty;
        public int ChannelIndex { get; init; }
        public System.Windows.Media.ImageSource? Thumbnail { get; init; }
    }
}
