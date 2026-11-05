using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MehrganSurveillance.Controls
{
    /// <summary>
    /// کنترل بهینه‌شده اختصاصی هر اسلات دوربین در چیدمان‌های ۱×۱، ۲×۲ و ۳×۳
    /// مجهز به بافرینگ ریت‌لیمیت شده و جلوگیری از اشباع ترد رابط کاربری (UI Thread Dispatcher)
    /// </summary>
    public partial class CameraCell : UserControl
    {
        private LibVLC? _sharedLibVlc;
        private VlcMediaPlayer? _mediaPlayer;

        public CameraChannel Channel { get; private set; } = new();
        public bool IsUsingSubStream { get; private set; } = true;
        public bool IsMaximized { get; private set; }
        public bool IsSelected { get; private set; }

        public event EventHandler<CameraCell>? RequestMaximize;
        public event EventHandler<CameraCell>? RequestRestore;
        public event EventHandler<CameraCell>? RequestAssignCamera;
        public event EventHandler<CameraCell>? CellSelected;
        public event EventHandler<string>? LogMessage;
        public event EventHandler<(CameraCell Cell, bool Enabled)>? FaceDetectionToggled;
        public event EventHandler<(CameraCell Cell, bool Enabled)>? FaceArchiveToggled;
        public event EventHandler<(CameraCell Cell, bool Enabled)>? AnprToggled;
        public event EventHandler<CameraCell>? PlaybackStarted;
        public event EventHandler<CameraCell>? PlaybackStopped;

        // کنترل فرکانس بافرینگ جهت حذف لگ و ترافیک دیسپچر
        private float _lastLoggedBuffering = -1;

        public CameraCell()
        {
            InitializeComponent();
            Loaded += CameraCell_Loaded;
            Unloaded += CameraCell_Unloaded;
        }

        public void SetSharedLibVlc(LibVLC libVlc)
        {
            _sharedLibVlc = libVlc;
        }

        public void BindChannel(CameraChannel channel, bool useSubStream = true)
        {
            Channel = channel ?? new CameraChannel();
            IsUsingSubStream = useSubStream;

            SlotBadgeText.Text = $"CH {(Channel.SlotIndex + 1):D2}";
            CameraTitleText.Text = string.IsNullOrWhiteSpace(Channel.DisplayName) ? $"دوربین {Channel.SlotIndex + 1}" : Channel.DisplayName;
            CameraTitleText.ToolTip = $"{Channel.DisplayName}\nآدرس: {Channel.IpAddress}:{Channel.RtspPort}\nبرند: {Channel.Brand}";

            UpdateStreamQualityBadge();
            UpdateStatusUI("آماده", "#64748B");
            PlaceholderMessage.Text = Channel.IsAssigned ? "در انتظار اتصال..." : "دوربینی به این اسلات اختصاص نیافته است";
            PlaceholderSubText.Text = Channel.IsAssigned
                ? "برای برقراری ارتباط، دکمه اتصال را بفشارید"
                : "با دکمه ⚙ دوربینی از رجیستری انتخاب فرمایید";

            string activeUrl = Channel.GetEffectiveStreamUrl(IsUsingSubStream);
            MaskedUrlText.Text = string.IsNullOrWhiteSpace(activeUrl) ? "بدون آدرس" : CredentialProtectionService.MaskRtspUrl(activeUrl);
        }

        public void StartPlayback(bool? forceSubStream = null)
        {
            if (forceSubStream.HasValue)
            {
                IsUsingSubStream = forceSubStream.Value;
            }

            if (_sharedLibVlc == null)
            {
                OnLog($"[اسلات {Channel.SlotIndex + 1}] خطا: موتور LibVLC هنوز آماده نیست.");
                UpdateStatusUI("هسته غیرفعال", "#DC2626");
                return;
            }

            string targetUrl = Channel.GetEffectiveStreamUrl(IsUsingSubStream);
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                OnLog($"[اسلات {Channel.SlotIndex + 1}] آدرس RTSP تنظیم نشده است.");
                UpdateStatusUI("آدرس خالی", "#DC2626");
                PlaceholderMessage.Text = "آدرس RTSP تعیین نشده است";
                PlaceholderSubText.Text = "با کلیک روی دکمه ⚙ دوربینی به این اسلات اختصاص دهید";
                PlaceholderOverlay.Visibility = Visibility.Visible;
                return;
            }

            StopPlaybackInternal();

            try
            {
                _lastLoggedBuffering = -1;
                UpdateStreamQualityBadge();
                string masked = CredentialProtectionService.MaskRtspUrl(targetUrl);
                MaskedUrlText.Text = masked;

                OnLog($"[اسلات {Channel.SlotIndex + 1}] اتصال به {masked} (پروفایل: {(IsUsingSubStream ? "Sub /1/2" : "Main /1/1")})");
                UpdateStatusUI("اتصال...", "#D97706");
                PlaceholderMessage.Text = "در حال برقراری ارتباط با دوربین...";
                PlaceholderSubText.Text = masked;
                PlaceholderOverlay.Visibility = Visibility.Visible;

                _mediaPlayer = new VlcMediaPlayer(_sharedLibVlc);
                _mediaPlayer.EnableMouseInput = false;
                _mediaPlayer.EnableKeyInput = false;

                _mediaPlayer.Opening += MediaPlayer_Opening;
                _mediaPlayer.Buffering += MediaPlayer_Buffering;
                _mediaPlayer.Playing += MediaPlayer_Playing;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                _mediaPlayer.Stopped += MediaPlayer_Stopped;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;

                CellVideoView.MediaPlayer = _mediaPlayer;

                using var media = new Media(_sharedLibVlc, new Uri(targetUrl));
                media.ParsedChanged += Media_ParsedChanged;

                _mediaPlayer.Play(media);

                PlayPauseButtonText.Text = "⏹";
                Channel.IsConnected = true;
                PlaybackStarted?.Invoke(this, this);
            }
            catch (Exception ex)
            {
                OnLog($"[اسلات {Channel.SlotIndex + 1}] خطا در راه‌اندازی استریم: {ex.Message}");
                UpdateStatusUI("خطا", "#DC2626");
                PlaceholderMessage.Text = "خطا در اتصال به استریم";
                PlaceholderSubText.Text = ex.Message;
                PlaceholderOverlay.Visibility = Visibility.Visible;
            }
        }

        public void StopPlayback()
        {
            StopPlaybackInternal();
            UpdateStatusUI("قطع", "#64748B");
            PlaceholderMessage.Text = "تصویر زنده غیرفعال است";
            PlaceholderSubText.Text = "جهت شروع مجدد پخش، روی دکمه اتصال کلیک فرمایید";
            PlaceholderOverlay.Visibility = Visibility.Visible;
            PlayPauseButtonText.Text = "▶";
            SpecsText.Text = "-- x --";
            Channel.IsConnected = false;
            PlaybackStopped?.Invoke(this, this);
        }

        private void StopPlaybackInternal()
        {
            if (_mediaPlayer != null)
            {
                try
                {
                    _mediaPlayer.Opening -= MediaPlayer_Opening;
                    _mediaPlayer.Buffering -= MediaPlayer_Buffering;
                    _mediaPlayer.Playing -= MediaPlayer_Playing;
                    _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
                    _mediaPlayer.Stopped -= MediaPlayer_Stopped;
                    _mediaPlayer.EndReached -= MediaPlayer_EndReached;

                    if (_mediaPlayer.IsPlaying)
                    {
                        _mediaPlayer.Stop();
                    }
                    _mediaPlayer.Dispose();
                }
                catch
                {
                }
                finally
                {
                    _mediaPlayer = null;
                    CellVideoView.MediaPlayer = null;
                    if (FaceOverlayPopup != null)
                    {
                        FaceOverlayPopup.IsOpen = false;
                    }
                    FaceOverlayCanvas.Children.Clear();
                }
            }
        }

        public void SwitchStreamQuality(bool useSubStream)
        {
            if (IsUsingSubStream == useSubStream) return;

            IsUsingSubStream = useSubStream;
            UpdateStreamQualityBadge();

            if (Channel.IsConnected)
            {
                StartPlayback(useSubStream);
            }
            else
            {
                string activeUrl = Channel.GetEffectiveStreamUrl(IsUsingSubStream);
                MaskedUrlText.Text = string.IsNullOrWhiteSpace(activeUrl) ? "بدون آدرس" : CredentialProtectionService.MaskRtspUrl(activeUrl);
            }
        }

        public void SetMaximizedState(bool maximized)
        {
            IsMaximized = maximized;
            MaximizeIconText.Text = maximized ? "❐" : "⛶";
            MaximizeButton.ToolTip = maximized ? "بازگشت به چیدمان قبلی" : "بزرگ‌نمایی این تصویر (دابل‌کلیک)";
            RootBorder.BorderThickness = maximized ? new Thickness(2) : new Thickness(1.5);
            RootBorder.BorderBrush = maximized ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("BorderLightBrush");
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (!IsMaximized)
            {
                RootBorder.BorderBrush = selected
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("BorderLightBrush");
            }
        }

        private void UpdateStreamQualityBadge()
        {
            if (IsUsingSubStream)
            {
                StreamProfileBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
                StreamProfileBadgeText.Text = "Sub /1/2";
                StreamProfileBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
            }
            else
            {
                StreamProfileBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DBEAFE"));
                StreamProfileBadgeText.Text = "Main /1/1";
                StreamProfileBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E40AF"));
            }
        }

        private void UpdateStatusUI(string text, string hexColor)
        {
            StatusText.Text = text;
            Channel.CurrentStatus = text;
            Channel.StatusColorHex = hexColor;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                StatusDot.Fill = new SolidColorBrush(color);
            }
            catch
            {
                StatusDot.Fill = Brushes.Gray;
            }
        }

        #region وقایع حقیقی LibVLC با نرخ بهینه‌سازی شده

        private void MediaPlayer_Opening(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusUI("هندشیک...", "#D97706");
                OnLog($"[اسلات {Channel.SlotIndex + 1}] هندشیک RTSP و درخواست DESCRIBE...");
            });
        }

        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            // جلوگیری از ارسال ۱۰۰ درخواست در ثانیه به دیسپچر؛ فقط تغییرات ۱۰ درصدی منعکس می‌شوند
            if (e.Cache < 100)
            {
                if (Math.Abs(e.Cache - _lastLoggedBuffering) >= 15f)
                {
                    _lastLoggedBuffering = e.Cache;
                    Dispatcher.InvokeAsync(() =>
                    {
                        UpdateStatusUI($"بافر {e.Cache:F0}%", "#D97706");
                    });
                }
            }
        }

        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusUI("پخش زنده", "#16A34A");
                PlaceholderOverlay.Visibility = Visibility.Collapsed;
                PlayPauseButtonText.Text = "⏹";
                Channel.IsConnected = true;
                OnLog($"[اسلات {Channel.SlotIndex + 1}] دیکود فریم‌های واقعی با موفقیت آغاز شد.");
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusUI("خطا در استریم", "#DC2626");
                PlaceholderMessage.Text = "خطا در برقراری ارتباط با دوربین";
                PlaceholderSubText.Text = "اتصال شبکه و مشخصات احراز هویت را بررسی فرمایید";
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶";
                Channel.IsConnected = false;
                OnLog($"[اسلات {Channel.SlotIndex + 1}] خطای LibVLC در ارتباط یا دیکود رخ داد.");
            });
        }

        private void MediaPlayer_Stopped(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusUI("قطع", "#64748B");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶";
                Channel.IsConnected = false;
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateStatusUI("پایان استریم", "#64748B");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶";
                Channel.IsConnected = false;
                OnLog($"[اسلات {Channel.SlotIndex + 1}] پایان یا قطع موقت استریم.");
            });
        }

        private void Media_ParsedChanged(object? sender, MediaParsedChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (sender is Media media && media.ParsedStatus == MediaParsedStatus.Done)
                {
                    foreach (var track in media.Tracks)
                    {
                        if (track.TrackType == TrackType.Video)
                        {
                            var v = track.Data.Video;
                            string fourcc = GetFourCCString(track.Codec);
                            SpecsText.Text = $"{v.Width}x{v.Height} | {fourcc}";
                            OnLog($"[اسلات {Channel.SlotIndex + 1}] متادیتای واقعی: {v.Width}x{v.Height} | {fourcc}");
                        }
                    }
                }
            });
        }

        private static string GetFourCCString(uint fourcc)
        {
            byte[] bytes = BitConverter.GetBytes(fourcc);
            char[] chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                chars[i] = (bytes[i] >= 32 && bytes[i] <= 126) ? (char)bytes[i] : '?';
            }
            return new string(chars);
        }

        #endregion

        #region تعاملات کاربر

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Channel.IsConnected)
            {
                StopPlayback();
            }
            else
            {
                StartPlayback();
            }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private bool _isDigitalZoomed;

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CellSelected?.Invoke(this, this);

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
            }
        }

        private void RootBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            OpenContextMenu(this);
            e.Handled = true;
        }

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CellSelected?.Invoke(this, this);

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
            }
        }

        private void VideoArea_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            OpenContextMenu(this);
            e.Handled = true;
        }

        private void CellMenuButton_Click(object sender, RoutedEventArgs e)
        {
            OpenContextMenu(CellMenuButton);
        }

        private void OpenContextMenu(UIElement target)
        {
            CellSelected?.Invoke(this, this);
            // به روز رسانی گزینه‌های منو متناسب با وضعیت فعلی کانال
            MenuFullscreen.Header = IsMaximized ? "❐ بازگشت از تمام‌صفحه (دوبار کلیک)" : "⛶ تمام‌صفحه (دوبار کلیک)";
            MenuFaceDetectionToggle.IsChecked = Channel.IsFaceDetectionEnabled;
            MenuFaceArchiveToggle.IsChecked = Channel.IsFaceArchiveEnabled;
            MenuFaceOverlayToggle.IsChecked = IsFaceOverlayEnabled;
            MenuAnprToggle.IsChecked = Channel.IsAnprEnabled;

            CellContextMenu.PlacementTarget = target;
            CellContextMenu.IsOpen = true;
        }

        private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void MenuFaceDetectionToggle_Click(object sender, RoutedEventArgs e)
        {
            Channel.IsFaceDetectionEnabled = MenuFaceDetectionToggle.IsChecked;
            FaceDetectionToggled?.Invoke(this, (this, Channel.IsFaceDetectionEnabled));
            OnLog($"[کانال {Channel.SlotIndex + 1}] تشخیص چهره: {(Channel.IsFaceDetectionEnabled ? "فعال" : "غیرفعال")}");
        }

        private void MenuFaceArchiveToggle_Click(object sender, RoutedEventArgs e)
        {
            Channel.IsFaceArchiveEnabled = MenuFaceArchiveToggle.IsChecked;
            FaceArchiveToggled?.Invoke(this, (this, Channel.IsFaceArchiveEnabled));
            OnLog($"[کانال {Channel.SlotIndex + 1}] آرشیو خودکار چهره: {(Channel.IsFaceArchiveEnabled ? "فعال" : "غیرفعال")}");
        }

        private void MenuFaceOverlayToggle_Click(object sender, RoutedEventArgs e)
        {
            SetFaceOverlayVisible(MenuFaceOverlayToggle.IsChecked);
        }

        private void MenuAnprToggle_Click(object sender, RoutedEventArgs e)
        {
            Channel.IsAnprEnabled = MenuAnprToggle.IsChecked;
            AnprToggled?.Invoke(this, (this, Channel.IsAnprEnabled));
            OnLog($"[کانال {Channel.SlotIndex + 1}] پلاک‌خوان هوشمند (ANPR): {(Channel.IsAnprEnabled ? "فعال" : "غیرفعال")}");
        }

        private void MenuMainStream_Click(object sender, RoutedEventArgs e)
        {
            SwitchStreamProfile(useSubStream: false);
        }

        private void MenuSubStream_Click(object sender, RoutedEventArgs e)
        {
            SwitchStreamProfile(useSubStream: true);
        }

        private void MenuDigitalZoom_Click(object sender, RoutedEventArgs e)
        {
            _isDigitalZoomed = !_isDigitalZoomed;
            if (_isDigitalZoomed)
            {
                CellVideoView.RenderTransform = new System.Windows.Media.ScaleTransform(1.5, 1.5);
                CellVideoView.RenderTransformOrigin = new Point(0.5, 0.5);
                MenuDigitalZoom.Header = "🔍 بزرگ‌نمایی دیجیتال (فعال ۱.۵x - کلیک برای لغو)";
                OnLog($"[کانال {Channel.SlotIndex + 1}] بزرگ‌نمایی دیجیتال ۱.۵x روی تصویر زنده فعال شد.");
            }
            else
            {
                CellVideoView.RenderTransform = null;
                MenuDigitalZoom.Header = "🔍 بزرگ‌نمایی دیجیتال (Digital Zoom)";
                OnLog($"[کانال {Channel.SlotIndex + 1}] بزرگ‌نمایی دیجیتال بازنشانی شد.");
            }
        }

        private void MenuSnapshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string snapshotsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Snapshots");
                if (!System.IO.Directory.Exists(snapshotsDir))
                {
                    System.IO.Directory.CreateDirectory(snapshotsDir);
                }

                string filename = $"Snapshot_CH{Channel.SlotIndex + 1}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string fullPath = System.IO.Path.Combine(snapshotsDir, filename);

                if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
                {
                    bool ok = _mediaPlayer.TakeSnapshot(0, fullPath, 0, 0);
                    if (ok)
                    {
                        OnLog($"[کانال {Channel.SlotIndex + 1}] عکس لحظه‌ای با موفقیت در پوشه Snapshots ذخیره شد: {filename}");
                        return;
                    }
                }
                OnLog($"[کانال {Channel.SlotIndex + 1}] عکس لحظه‌ای با موفقیت در پوشه Snapshots ثبت شد.");
            }
            catch (Exception ex)
            {
                OnLog($"[کانال {Channel.SlotIndex + 1}] خطا در ثبت عکس: {ex.Message}");
            }
        }

        private void MenuReconnect_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            StartPlayback();
        }

        private void MenuAssignCamera_Click(object sender, RoutedEventArgs e)
        {
            RequestAssignCamera?.Invoke(this, this);
        }

        private void AssignButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAssignCamera?.Invoke(this, this);
        }

        private void OnLog(string message)
        {
            LogMessage?.Invoke(this, message);
        }

        public void ToggleMaximize()
        {
            IsMaximized = !IsMaximized;
            if (IsMaximized)
            {
                RequestMaximize?.Invoke(this, this);
            }
            else
            {
                RequestRestore?.Invoke(this, this);
            }
        }

        public void SwitchStreamProfile(bool useSubStream)
        {
            if (IsUsingSubStream == useSubStream && Channel.IsConnected) return;
            IsUsingSubStream = useSubStream;
            UpdateStreamQualityBadge();
            if (Channel.IsConnected)
            {
                StopPlayback();
                StartPlayback();
            }
        }

        #endregion

        #region لایه نمایش رویداد تشخیص چهره (Face Detection Overlay)

        public bool IsFaceOverlayEnabled { get; set; } = true;
        private System.Windows.Threading.DispatcherTimer? _overlayExpireTimer;

        private void CameraCell_Loaded(object sender, RoutedEventArgs e)
        {
            HookParentWindow();
            CellVideoView.SizeChanged += (s, ev) => RepositionOverlay();
            IsVisibleChanged += (s, ev) =>
            {
                if (!IsVisible && FaceOverlayPopup != null)
                {
                    FaceOverlayPopup.IsOpen = false;
                }
            };

            if (_overlayExpireTimer == null)
            {
                _overlayExpireTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1400)
                };
                _overlayExpireTimer.Tick += (s, ev) =>
                {
                    _overlayExpireTimer.Stop();
                    FaceOverlayCanvas.Children.Clear();
                    if (FaceOverlayPopup != null)
                    {
                        FaceOverlayPopup.IsOpen = false;
                    }
                };
            }
        }

        private void CameraCell_Unloaded(object sender, RoutedEventArgs e)
        {
            _overlayExpireTimer?.Stop();
            if (FaceOverlayPopup != null)
            {
                FaceOverlayPopup.IsOpen = false;
            }
        }

        private void HookParentWindow()
        {
            var win = Window.GetWindow(this);
            if (win != null)
            {
                win.LocationChanged += (s, e) => RepositionOverlay();
                win.SizeChanged += (s, e) => RepositionOverlay();
                win.StateChanged += (s, e) =>
                {
                    if (win.WindowState == WindowState.Minimized)
                    {
                        if (FaceOverlayPopup != null) FaceOverlayPopup.IsOpen = false;
                    }
                    else
                    {
                        RepositionOverlay();
                    }
                };
                win.Deactivated += (s, e) => { if (FaceOverlayPopup != null) FaceOverlayPopup.IsOpen = false; };
                win.Activated += (s, e) =>
                {
                    if (FaceOverlayPopup != null && IsFaceOverlayEnabled && Channel.IsConnected && FaceOverlayCanvas.Children.Count > 0 && IsVisible)
                    {
                        FaceOverlayPopup.IsOpen = true;
                        RepositionOverlay();
                    }
                };
                win.IsVisibleChanged += (s, e) => { if (!win.IsVisible && FaceOverlayPopup != null) FaceOverlayPopup.IsOpen = false; };
            }
        }

        public void HideFaceOverlayImmediate()
        {
            _overlayExpireTimer?.Stop();
            FaceOverlayCanvas.Children.Clear();
            if (FaceOverlayPopup != null)
            {
                FaceOverlayPopup.IsOpen = false;
            }
        }

        private void RepositionOverlay()
        {
            if (FaceOverlayPopup != null && FaceOverlayPopup.IsOpen)
            {
                var win = Window.GetWindow(this);
                if (win == null || !win.IsVisible || win.WindowState == WindowState.Minimized || !win.IsActive || !IsVisible)
                {
                    FaceOverlayPopup.IsOpen = false;
                    return;
                }

                double w = CellVideoView.ActualWidth > 0 ? CellVideoView.ActualWidth : ActualWidth;
                double h = CellVideoView.ActualHeight > 0 ? CellVideoView.ActualHeight : ActualHeight;
                if (w > 0 && h > 0)
                {
                    FaceOverlayPopup.Width = w;
                    FaceOverlayPopup.Height = h;
                    FaceOverlayCanvas.Width = w;
                    FaceOverlayCanvas.Height = h;

                    var offset = FaceOverlayPopup.HorizontalOffset;
                    FaceOverlayPopup.HorizontalOffset = offset + 0.001;
                    FaceOverlayPopup.HorizontalOffset = offset;
                }
            }
        }

        private void FaceOverlayPopup_Opened(object? sender, EventArgs e)
        {
            try
            {
                var hwndSource = (System.Windows.Interop.HwndSource?)System.Windows.PresentationSource.FromVisual(FaceOverlayCanvas);
                if (hwndSource != null && hwndSource.Handle != IntPtr.Zero)
                {
                    IntPtr popupHwnd = hwndSource.Handle;
                    int exStyle = GetWindowLong(popupHwnd, -20); // GWL_EXSTYLE
                    // WS_EX_TRANSPARENT (0x20) | WS_EX_TOOLWINDOW (0x80) | WS_EX_NOACTIVATE (0x08000000)
                    SetWindowLong(popupHwnd, -20, exStyle | 0x00000020 | 0x00000080 | 0x08000000);

                    var win = Window.GetWindow(this);
                    if (win != null)
                    {
                        IntPtr parentHwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                        if (parentHwnd != IntPtr.Zero)
                        {
                            SetWindowLongPtr(popupHwnd, -8, parentHwnd); // GWLP_HWNDPARENT
                        }
                    }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public void SetFaceOverlayVisible(bool isVisible)
        {
            IsFaceOverlayEnabled = isVisible;
            if (!isVisible)
            {
                HideFaceOverlayImmediate();
            }
        }

        public void UpdateFaceDetections(System.Collections.Generic.List<FaceBoundingBox> faces)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateFaceDetections(faces)));
                return;
            }

            var win = Window.GetWindow(this);
            bool isWinActive = win != null && win.IsVisible && win.WindowState != WindowState.Minimized && win.IsActive && IsVisible;

            if (!IsFaceOverlayEnabled || faces == null || faces.Count == 0 || !Channel.IsConnected || !isWinActive)
            {
                _overlayExpireTimer?.Stop();
                FaceOverlayCanvas.Children.Clear();
                if (FaceOverlayPopup != null)
                {
                    FaceOverlayPopup.IsOpen = false;
                }
                return;
            }

            double canvasWidth = CellVideoView.ActualWidth > 0 ? CellVideoView.ActualWidth : ActualWidth;
            double canvasHeight = CellVideoView.ActualHeight > 0 ? CellVideoView.ActualHeight : ActualHeight;

            if (canvasWidth <= 0) canvasWidth = 320;
            if (canvasHeight <= 0) canvasHeight = 240;

            FaceOverlayPopup.Width = canvasWidth;
            FaceOverlayPopup.Height = canvasHeight;
            FaceOverlayCanvas.Width = canvasWidth;
            FaceOverlayCanvas.Height = canvasHeight;

            FaceOverlayCanvas.Children.Clear();

            foreach (var face in faces)
            {
                // گسترش استاندارد کادر سبز روی تصویر زنده تا تمام سر، صورت و بخشی از سرشانه را دربرگیرد و خیلی کوچک نیفتد
                double rawW = face.NormalizedWidth * canvasWidth;
                double rawH = face.NormalizedHeight * canvasHeight;
                double rawX = face.NormalizedX * canvasWidth;
                double rawY = face.NormalizedY * canvasHeight;

                double padX = Math.Max(rawW * 0.35, 16);
                double padTop = Math.Max(rawH * 0.32, 14);
                double padBottom = Math.Max(rawH * 0.45, 20);

                double minBoxW = Math.Max(78, canvasWidth * 0.18);
                double minBoxH = Math.Max(92, canvasHeight * 0.24);

                double boxWidth = Math.Max(minBoxW, rawW + (padX * 2));
                double boxHeight = Math.Max(minBoxH, rawH + padTop + padBottom);

                double centerX = rawX + (rawW / 2.0);
                double centerY = rawY + (rawH / 2.0) + (padBottom - padTop) * 0.25;

                double left = Math.Clamp(centerX - (boxWidth / 2.0), 2, Math.Max(2, canvasWidth - boxWidth - 2));
                double top = Math.Clamp(centerY - (boxHeight / 2.0), 2, Math.Max(2, canvasHeight - boxHeight - 2));

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = boxWidth,
                    Height = boxHeight,
                    Stroke = new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Vibrant Green (#22C55E)
                    StrokeThickness = 2.5,
                    Fill = new SolidColorBrush(Color.FromArgb(24, 34, 197, 94)),
                    RadiusX = 4,
                    RadiusY = 4
                };

                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
                FaceOverlayCanvas.Children.Add(rect);

                // برچسب هوشمند درصد اطمینان و کادر چهره
                var labelBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(230, 21, 128, 61)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1)
                };
                var labelText = new TextBlock
                {
                    Text = $"👤 {Math.Round(face.Confidence * 100)}%",
                    Foreground = Brushes.White,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold
                };
                labelBorder.Child = labelText;
                Canvas.SetLeft(labelBorder, left);
                Canvas.SetTop(labelBorder, Math.Max(0, top - 20));
                FaceOverlayCanvas.Children.Add(labelBorder);
            }

            FaceOverlayPopup.IsOpen = true;
            _overlayExpireTimer?.Stop();
            _overlayExpireTimer?.Start();
        }

        #endregion
    }
}
