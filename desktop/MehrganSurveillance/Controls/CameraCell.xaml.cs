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
    /// کنترل اختصاصی هر اسلات دوربین در چیدمان‌های ۱×۱، ۲×۲ و ۳×۳
    /// هر سلول دارای VlcMediaPlayer مستقل بوده اما همگی از یک نمونه اشتراکی LibVLC استفاده می‌نمایند.
    /// </summary>
    public partial class CameraCell : UserControl
    {
        // رفرنس به نمونه اشتراکی سراسری موتور LibVLC (هرگز نباید توسط سلول Dispose شود)
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

        public CameraCell()
        {
            InitializeComponent();
        }

        /// <summary>
        /// تزریق رفرنس نمونه اشتراکی LibVLC از فرم اصلی
        /// </summary>
        public void SetSharedLibVlc(LibVLC libVlc)
        {
            _sharedLibVlc = libVlc;
        }

        /// <summary>
        /// انتساب کانال مشخص به این سلول و به‌روزرسانی اولیه برچسب‌ها
        /// </summary>
        public void BindChannel(CameraChannel channel, bool useSubStream = true)
        {
            Channel = channel ?? new CameraChannel();
            IsUsingSubStream = useSubStream;

            SlotBadgeText.Text = $"کانال {Channel.SlotIndex + 1}";
            CameraTitleText.Text = string.IsNullOrWhiteSpace(Channel.DisplayName) ? $"دوربین {Channel.SlotIndex + 1}" : Channel.DisplayName;
            CameraTitleText.ToolTip = $"{Channel.DisplayName}\nIP: {Channel.IpAddress}:{Channel.RtspPort}\nBrand: {Channel.Brand}";

            UpdateStreamQualityBadge();
            UpdateStatusUI("آماده اتصال", "#64748B");
            PlaceholderMessage.Text = Channel.IsAssigned ? "در انتظار اتصال..." : "دوربینی به این اسلات اختصاص نیافته است";
            PlaceholderSubText.Text = Channel.IsAssigned
                ? "برای برقراری ارتباط، دکمه اتصال را بفشارید"
                : "با کلیک روی آیکون چرخ‌دنده (⚙️) دوربینی از رجیستری انتخاب فرمایید";

            string activeUrl = Channel.GetEffectiveStreamUrl(IsUsingSubStream);
            MaskedUrlText.Text = string.IsNullOrWhiteSpace(activeUrl) ? "بدون آدرس" : CredentialProtectionService.MaskRtspUrl(activeUrl);
        }

        /// <summary>
        /// آغاز پخش ویدیوی واقعی بر بستر RTSP TCP
        /// </summary>
        public void StartPlayback(bool? forceSubStream = null)
        {
            if (forceSubStream.HasValue)
            {
                IsUsingSubStream = forceSubStream.Value;
            }

            if (_sharedLibVlc == null)
            {
                OnLog($"[کانال {Channel.SlotIndex + 1}] خطا: موتور هسته LibVLC در دسترس نیست.");
                UpdateStatusUI("خطای هسته LibVLC", "#DC2626");
                return;
            }

            string targetUrl = Channel.GetEffectiveStreamUrl(IsUsingSubStream);
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                OnLog($"[کانال {Channel.SlotIndex + 1}] آدرس RTSP برای این اسلات تنظیم نشده است.");
                UpdateStatusUI("آدرس خالی", "#DC2626");
                PlaceholderMessage.Text = "آدرس RTSP تعیین نشده است";
                PlaceholderSubText.Text = "لطفاً با دکمه ⚙️ دوربینی را به این اسلات تخصیص دهید";
                PlaceholderOverlay.Visibility = Visibility.Visible;
                return;
            }

            // متوقف‌سازی و آزادسازی ایمن مدیاپلیر قبلی در صورت وجود
            StopPlaybackInternal();

            try
            {
                UpdateStreamQualityBadge();
                string masked = CredentialProtectionService.MaskRtspUrl(targetUrl);
                MaskedUrlText.Text = masked;

                OnLog($"[کانال {Channel.SlotIndex + 1}] در حال اتصال به: {masked} (حالت: {(IsUsingSubStream ? "Sub-Stream" : "Main-Stream")})");
                UpdateStatusUI("در حال اتصال...", "#D97706");
                PlaceholderMessage.Text = "در حال برقراری ارتباط با دوربین...";
                PlaceholderSubText.Text = masked;
                PlaceholderOverlay.Visibility = Visibility.Visible;

                // ایجاد نمونه مدیاپلیر مستقل از LibVLC اشتراکی
                _mediaPlayer = new VlcMediaPlayer(_sharedLibVlc);

                // ثبت رویدادهای حقیقی بدون شبیه‌سازی
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

                PlayPauseButtonText.Text = "⏹ قطع";
                Channel.IsConnected = true;
            }
            catch (Exception ex)
            {
                OnLog($"[کانال {Channel.SlotIndex + 1}] خطا در راه‌اندازی مدیاپلیر: {ex.Message}");
                UpdateStatusUI("خطا در ایجاد نشست", "#DC2626");
                PlaceholderMessage.Text = "خطا در اتصال به دوربین";
                PlaceholderSubText.Text = ex.Message;
                PlaceholderOverlay.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// متوقف‌سازی و آزادسازی کامل منابع مدیاپلیر بدون دستکاری LibVLC اشتراکی
        /// </summary>
        public void StopPlayback()
        {
            StopPlaybackInternal();
            UpdateStatusUI("اتصال قطع شد", "#64748B");
            PlaceholderMessage.Text = "تصویر زنده غیرفعال است";
            PlaceholderSubText.Text = "جهت شروع مجدد پخش، روی دکمه اتصال کلیک فرمایید";
            PlaceholderOverlay.Visibility = Visibility.Visible;
            PlayPauseButtonText.Text = "▶ اتصال";
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
                    // نادیده‌گیری خطاهای احتمالی هنگام آزادسازی
                }
                finally
                {
                    _mediaPlayer = null;
                    CellVideoView.MediaPlayer = null;
                }
            }
        }

        /// <summary>
        /// تعویض کیفیت استریم بین فرعی و اصلی (مثلاً هنگام تمام‌صفحه شدن یا بازگشت)
        /// </summary>
        public void SwitchStreamQuality(bool useSubStream)
        {
            if (IsUsingSubStream == useSubStream) return;

            IsUsingSubStream = useSubStream;
            UpdateStreamQualityBadge();

            // اگر در حال حاضر تصویر در حال پخش است، با آدرس جدید ری‌استارت می‌شود
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
            RootBorder.BorderThickness = maximized ? new Thickness(2.5) : new Thickness(1.5);
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
                StreamProfileBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7")); // Amber
                StreamProfileBadgeText.Text = "Sub-Stream (/1/2)";
                StreamProfileBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
            }
            else
            {
                StreamProfileBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DBEAFE")); // Blue
                StreamProfileBadgeText.Text = "Main-Stream (/1/1)";
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

        #region وقایع حقیقی دیکود LibVLC (بدون شبیه‌سازی)

        private void MediaPlayer_Opening(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusUI("هندشیک RTSP...", "#D97706");
                OnLog($"[کانال {Channel.SlotIndex + 1}] هندشیک سوکت RTSP و درخواست DESCRIBE...");
            });
        }

        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.Cache < 100)
                {
                    UpdateStatusUI($"بافرینگ ({e.Cache:F0}%)...", "#D97706");
                }
            });
        }

        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusUI("پخش زنده", "#16A34A");
                PlaceholderOverlay.Visibility = Visibility.Collapsed;
                PlayPauseButtonText.Text = "⏹ قطع";
                Channel.IsConnected = true;
                OnLog($"[کانال {Channel.SlotIndex + 1}] پخش ویدیوی واقعی آغاز شد.");
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusUI("خطا در ارتباط", "#DC2626");
                PlaceholderMessage.Text = "خطا در دریافت یا دیکود استریم RTSP";
                PlaceholderSubText.Text = "اتصال شبکه و نام کاربری/کلمه عبور دوربین را بررسی فرمایید";
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶ اتصال";
                Channel.IsConnected = false;
                OnLog($"[کانال {Channel.SlotIndex + 1}] خطا در استریم ویدیوی LibVLC رخ داد.");
            });
        }

        private void MediaPlayer_Stopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusUI("متوقف شد", "#64748B");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶ اتصال";
                Channel.IsConnected = false;
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusUI("پایان استریم", "#64748B");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                PlayPauseButtonText.Text = "▶ اتصال";
                Channel.IsConnected = false;
                OnLog($"[کانال {Channel.SlotIndex + 1}] پایان استریم یا قطع شدن دوربین.");
            });
        }

        private void Media_ParsedChanged(object? sender, MediaParsedChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
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
                            OnLog($"[کانال {Channel.SlotIndex + 1}] ابعاد واقعی: {v.Width}x{v.Height} | کدک: {fourcc}");
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

        #region رخدادهای کاربر و کنترل‌ها

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
