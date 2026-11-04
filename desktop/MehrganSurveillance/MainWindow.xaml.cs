using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using LibVLCSharp.Shared;

namespace MehrganSurveillance
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// پیاده‌سازی پخش ویدیوی واقعی با موتور LibVLC بر بستر RTSP over TCP
    /// </summary>
    public partial class MainWindow : Window
    {
        private LibVLC? _libVlc;
        private MediaPlayer? _mediaPlayer;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // مقداردهی اولیه به کتابخانه‌های محلی LibVLC (libvlc.dll)
                Core.Initialize();
                LogEvent("هسته بومی LibVLC با موفقیت در ویندوز بارگذاری شد.");
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در راه‌اندازی اولیه هسته LibVLC: {ex.Message}");
                UpdateStatus("خطا در راه‌اندازی LibVLC", "#DC2626");
                MessageBox.Show(
                    $"خطا در بارگذاری موتور LibVLC:\n{ex.Message}\n\nلطفاً اطمینان حاصل کنید کتابخانه‌های VideoLAN.LibVLC.Windows در پوشه خروجی قرار دارند.",
                    "خطای راه‌اندازی",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string url = RtspUrlInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("لطفاً آدرس RTSP معتبر وارد نمایید.", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int networkCaching = 300;
            if (int.TryParse(NetworkCachingInput.Text.Trim(), out int parsedCaching) && parsedCaching >= 50)
            {
                networkCaching = parsedCaching;
            }

            // متوقف‌سازی سشن قبلی در صورت وجود
            StopPlayback();

            try
            {
                // پیکربندی آرگومان‌های حیاتی LibVLC:
                // ۱. اجبار به RTSP روی TCP (--rtsp-tcp) جهت حذف اتلاف پکت
                // ۲. تنظیم بافر کم‌تاخیر شبکه (--network-caching)
                // ۳. استفاده از شتاب‌دهنده سخت‌افزاری گرافیک ویندوز (D3D11VA/DirectX)
                string[] options = new[]
                {
                    "--rtsp-tcp",
                    $"--network-caching={networkCaching}",
                    "--avcodec-hw=any",
                    "--no-video-title-show",
                    "--no-sub-autodetect-file",
                    "--clock-jitter=0"
                };

                _libVlc = new LibVLC(options);
                _mediaPlayer = new MediaPlayer(_libVlc);

                // ثبت لیسنرهای وقایع حقیقی دیکود و ارتباط LibVLC
                _mediaPlayer.Opening += MediaPlayer_Opening;
                _mediaPlayer.Buffering += MediaPlayer_Buffering;
                _mediaPlayer.Playing += MediaPlayer_Playing;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                _mediaPlayer.Stopped += MediaPlayer_Stopped;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;

                // اتصال پلیر به کنترل تصویر WPF
                VlcVideoView.MediaPlayer = _mediaPlayer;

                // ماسک‌سازی پسورد برای نمایش امن در لاگ‌ها
                string safeLogUrl = Regex.Replace(url, @"(:)([^:@/]+)(@)", "$1••••••••$3");
                LogEvent($"در حال ارسال درخواست اتصال RTSP به: {safeLogUrl}");
                LogEvent($"پروتکل لایه انتقال: RTSP over TCP | بافر شبکه: {networkCaching}ms");

                using var media = new Media(_libVlc, new Uri(url));
                // اعلام پارس برای استخراج اطلاعات فنی استریم
                media.ParsedChanged += Media_ParsedChanged;
                _mediaPlayer.Play(media);

                UpdateStatus("در حال برقراری اتصال...", "#D97706");
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LogEvent($"خطا در ایجاد نشست مدیاپلیر: {ex.Message}");
                UpdateStatus("خطا در اتصال", "#DC2626");
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            LogEvent("کاربر دکمه قطع اتصال را فشرد.");
            StopPlayback();
            UpdateStatus("اتصال قطع شد", "#64748B");
            PlaceholderOverlay.Visibility = Visibility.Visible;
            StreamSpecsText.Text = "اتصال قطع است";
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
        }

        private void StopPlayback()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            if (_libVlc != null)
            {
                _libVlc.Dispose();
                _libVlc = null;
            }
        }

        #region وقایع حقیقی LibVLC (Real Media Player Events)

        private void MediaPlayer_Opening(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogEvent("[LibVLC Event] Opening: هندشیک سوکت RTSP و ارسال درخواست DESCRIBE...");
                UpdateStatus("در حال هندشیک RTSP...", "#D97706");
            });
        }

        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.Cache < 100)
                {
                    LogEvent($"[LibVLC Event] Buffering: دریافت و پرکردن بافر فریم ({e.Cache:F0}%)...");
                    UpdateStatus($"بافرینگ ({e.Cache:F0}%)...", "#D97706");
                }
            });
        }

        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogEvent("[LibVLC Event] Playing: دیکود فریم‌های ویدیویی واقعی آغاز شد.");
                UpdateStatus("در حال پخش تصویر زنده (Real Video)", "#16A34A");
                PlaceholderOverlay.Visibility = Visibility.Collapsed;
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogEvent("[LibVLC Error] EncounteredError: خطا در دسترسی به آدرس یا دیکود استریم رخ داد.");
                UpdateStatus("خطا در استریم ویدیو", "#DC2626");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
            });
        }

        private void MediaPlayer_Stopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogEvent("[LibVLC Event] Stopped: پخش متوقف گردید.");
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LogEvent("[LibVLC Event] EndReached: انتهای استریم یا قطع شدن منبع.");
                UpdateStatus("استریم خاتمه یافت", "#64748B");
                PlaceholderOverlay.Visibility = Visibility.Visible;
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
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
                            var video = track.Data.Video;
                            string fourcc = GetFourCCString(track.Codec);
                            string specs = $"{video.Width}x{video.Height} | کدک: {fourcc} ({track.OriginalFourcc:X})";
                            StreamSpecsText.Text = specs;
                            LogEvent($"[اطلاعات فنی فریم] ابعاد تصویر واقعی: {video.Width}x{video.Height} پیکسل | کدک چهارحرفی: {fourcc}");
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

        #region کمکی‌های UI و مدیریت رخدادها

        private void UpdateStatus(string text, string hexColor)
        {
            StatusBadgeText.Text = text;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                StatusDot.Fill = new SolidColorBrush(color);
            }
            catch
            {
                // Fallback
            }
        }

        private void LogEvent(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string entry = $"[{timestamp}] {message}";
            EventLogListBox.Items.Add(entry);
            // پیمایش خودکار به انتهای لیست لاگ‌ها
            EventLogListBox.ScrollIntoView(entry);
        }

        private void Stream1Preset_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInUrl("/1/1");
        }

        private void Stream2Preset_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInUrl("/1/2");
        }

        private void Stream3Preset_Click(object sender, RoutedEventArgs e)
        {
            SetStreamPathInUrl("/1/3");
        }

        private void SetStreamPathInUrl(string newPath)
        {
            string current = RtspUrlInput.Text.Trim();
            // جایگزینی مسیر انتهای آدرس RTSP با مسیر انتخاب شده
            string updated = Regex.Replace(current, @"(/\d+/\d+|/live\d+|/ch\d+/[^/]+|/Streaming/Channels/\d+)$", newPath);
            if (updated == current && !current.EndsWith(newPath))
            {
                // در صورت عدم تطابق با الگو، مسیر مستقیماً الحاق یا جایگزین می‌شود
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
            RtspUrlInput.Text = updated;
            LogEvent($"مسیر استریم تیاندی به {newPath} تغییر یافت.");
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            StopPlayback();
        }

        #endregion
    }
}
