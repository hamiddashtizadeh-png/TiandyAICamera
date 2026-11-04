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

        // کنترل فرکانس بافرینگ جهت حذف لگ و ترافیک دیسپچر
        private float _lastLoggedBuffering = -1;

        public CameraCell()
        {
            InitializeComponent();
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

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CellSelected?.Invoke(this, this);

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
            }
        }

        private void ToggleMaximize()
        {
            if (IsMaximized)
            {
                RequestRestore?.Invoke(this, this);
            }
            else
            {
                RequestMaximize?.Invoke(this, this);
            }
        }

        private void AssignButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAssignCamera?.Invoke(this, this);
        }

        private void OnLog(string message)
        {
            LogMessage?.Invoke(this, message);
        }

        #endregion
    }
}
