using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance
{
    /// <summary>
    /// استودیوی شفاف‌سازی، رفع تاری (Deblur) و فوق‌وضوح (Super-Resolution) برای تصاویر پلاک خودرو و چهره انسان
    /// </summary>
    public partial class PhotoEnhancerWindow : Window
    {
        private readonly AiPhotoEnhancementService _enhancerService = new();
        private readonly AnprPipeline? _anprPipeline;

        private byte[]? _sourceBgra;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _sourceStride;

        private PhotoEnhancementResult? _lastResult;
        private CancellationTokenSource? _activeCts;
        private bool _isSuppressingPresetSync;
        private bool _autoRunOnLoaded;

        public PhotoEnhancerWindow(AnprPipeline? anprPipeline = null)
        {
            InitializeComponent();
            _anprPipeline = anprPipeline;
            Loaded += PhotoEnhancerWindow_Loaded;
            Closed += PhotoEnhancerWindow_Closed;
        }

        public PhotoEnhancerWindow(
            byte[] encodedImageBytes,
            PhotoEnhancementTarget initialTarget,
            string sourceTitle = "",
            AnprPipeline? anprPipeline = null) : this(anprPipeline)
        {
            ApplyTargetPresetUI(initialTarget);
            LoadEncodedImageBytes(encodedImageBytes, string.IsNullOrWhiteSpace(sourceTitle) ? "تصویر ارسالی از سامانه" : sourceTitle);
            _autoRunOnLoaded = _sourceBgra != null;
        }

        private async void PhotoEnhancerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshEngineStatusBadge();

            if (_autoRunOnLoaded && _sourceBgra != null)
            {
                _autoRunOnLoaded = false;
                await RunEnhancementAsync();
            }
        }

        private void PhotoEnhancerWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                _activeCts?.Cancel();
                _activeCts?.Dispose();
            }
            catch { }
            _enhancerService.Dispose();
        }

        private PhotoEnhancementTarget GetSelectedTarget()
        {
            if (TargetFaceRadio.IsChecked == true) return PhotoEnhancementTarget.FacePortrait;
            if (TargetGeneralRadio.IsChecked == true) return PhotoEnhancementTarget.GeneralScene;
            return PhotoEnhancementTarget.LicensePlate;
        }

        private int GetSelectedScale()
        {
            if (Scale1xRadio.IsChecked == true) return 1;
            if (Scale4xRadio.IsChecked == true) return 4;
            return 2;
        }

        private void ApplyTargetPresetUI(PhotoEnhancementTarget target)
        {
            _isSuppressingPresetSync = true;
            try
            {
                switch (target)
                {
                    case PhotoEnhancementTarget.FacePortrait:
                        TargetFaceRadio.IsChecked = true;
                        Scale2xRadio.IsChecked = true;
                        DeblurIterSlider.Value = 5;
                        SharpSlider.Value = 0.55;
                        DenoiseSlider.Value = 0.45;
                        break;

                    case PhotoEnhancementTarget.GeneralScene:
                        TargetGeneralRadio.IsChecked = true;
                        Scale2xRadio.IsChecked = true;
                        DeblurIterSlider.Value = 6;
                        SharpSlider.Value = 0.70;
                        DenoiseSlider.Value = 0.30;
                        break;

                    case PhotoEnhancementTarget.LicensePlate:
                    default:
                        TargetPlateRadio.IsChecked = true;
                        Scale2xRadio.IsChecked = true;
                        DeblurIterSlider.Value = 8;
                        SharpSlider.Value = 0.95;
                        DenoiseSlider.Value = 0.25;
                        break;
                }

                UpdateSliderTexts();
            }
            finally
            {
                _isSuppressingPresetSync = false;
            }
        }

        private void RefreshEngineStatusBadge()
        {
            var probe = _enhancerService.ProbeAvailableModel(GetSelectedTarget());
            if (probe.Available)
            {
                EngineStatusBadgeText.Text = $"ONNX فعال: {probe.ModelName} ({probe.ExecutionProvider})";
                EngineStatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                EngineStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                EngineStatusBadgeText.Text = "موتور محلی آماده (Richardson-Lucy + Catmull-Rom 2x/4x)";
                EngineStatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                EngineStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            }
        }

        private void LoadEncodedImageBytes(byte[] rawBytes, string title)
        {
            if (rawBytes == null || rawBytes.Length == 0) return;

            try
            {
                var (bgra, w, h, stride) = AiPhotoEnhancementService.DecodeImageBytesToBgra32(rawBytes);
                _sourceBgra = bgra;
                _sourceWidth = w;
                _sourceHeight = h;
                _sourceStride = stride;
                _lastResult = null;

                BeforeImage.Source = CreateFrozenBitmapFromBgra(bgra, w, h, stride);
                BeforePlaceholderText.Visibility = Visibility.Collapsed;
                BeforeResolutionText.Text = $"{w} × {h} px";
                SourceInfoText.Text = $"منبع فعال: {title} ({w}×{h} پیکسل)";

                var m = ImageProcessingFilterSet.CalculateQualityMetrics(bgra.AsSpan(), w, h, stride, 4);
                BeforeSharpnessBadge.Text = $"وضوح لبه: {m.LaplacianSharpness:F0}";
                BeforeBlurBadge.Text = $"شاخص تاری: {m.MotionBlurScore:F0}%";
                BeforeContrastBadge.Text = $"کنتراست: {m.ContrastStdDev:F1}";

                AfterImage.Source = null;
                SingleLargeAfterImage.Source = null;
                AfterPlaceholderText.Visibility = Visibility.Visible;
                SaveEnhancedButton.IsEnabled = false;
                CopyEnhancedButton.IsEnabled = false;
                OcrComparisonBox.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = $"تصویر با ابعاد {w}×{h} بارگذاری شد. برای شروع روی «اجرای شفاف‌سازی» کلیک کنید.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در خواندن تصویر ورودی: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "انتخاب تصویر تار پلاک یا چهره",
                Filter = "فایل‌های تصویر|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|همه فایل‌ها|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(dlg.FileName);
                    LoadEncodedImageBytes(bytes, Path.GetFileName(dlg.FileName));
                    _ = RunEnhancementAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"امکان باز کردن فایل وجود ندارد: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void PasteClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    MessageBox.Show("در حال حاضر هیچ تصویری در کلیپ‌بورد (Clipboard) وجود ندارد.", "کلیپ‌بورد خالی", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                BitmapSource? clipBmp = Clipboard.GetImage();
                if (clipBmp == null) return;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(clipBmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);

                LoadEncodedImageBytes(ms.ToArray(), "تصویر کلیپ‌بورد");
                _ = RunEnhancementAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در خواندن تصویر از کلیپ‌بورد: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                string file = files[0];
                if (File.Exists(file))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        LoadEncodedImageBytes(bytes, Path.GetFileName(file));
                        _ = RunEnhancementAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"فایل رهاشده تصویر معتبری نیست: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void TargetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isSuppressingPresetSync) return;
            ApplyTargetPresetUI(GetSelectedTarget());
            RefreshEngineStatusBadge();
        }

        private void ParamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            UpdateSliderTexts();
        }

        private void UpdateSliderTexts()
        {
            if (DeblurIterValueText != null && DeblurIterSlider != null)
                DeblurIterValueText.Text = ((int)DeblurIterSlider.Value).ToString();

            if (SharpValueText != null && SharpSlider != null)
                SharpValueText.Text = SharpSlider.Value.ToString("F2");

            if (DenoiseValueText != null && DenoiseSlider != null)
                DenoiseValueText.Text = DenoiseSlider.Value.ToString("F2");
        }

        private async void EnhanceButton_Click(object sender, RoutedEventArgs e)
        {
            await RunEnhancementAsync();
        }

        private async Task RunEnhancementAsync()
        {
            if (_sourceBgra == null || _sourceWidth < 8 || _sourceHeight < 8)
            {
                MessageBox.Show("ابتدا یک تصویر پلاک یا چهره را از سیستم انتخاب کنید یا داخل پنجره بکشید.", "تصویر انتخاب نشده", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _activeCts?.Cancel();
                _activeCts?.Dispose();
            }
            catch { }

            _activeCts = new CancellationTokenSource();
            var token = _activeCts.Token;

            EnhanceButton.IsEnabled = false;
            SaveEnhancedButton.IsEnabled = false;
            CopyEnhancedButton.IsEnabled = false;
            StatusTextBlock.Text = "⏳ در حال اجرای الگوریتم رفع تاری (Richardson-Lucy) و بزرگ‌نمایی فوق‌وضوح...";

            try
            {
                var options = new PhotoEnhancementOptions
                {
                    Target = GetSelectedTarget(),
                    UpscaleFactor = GetSelectedScale(),
                    DeblurIterations = (int)DeblurIterSlider.Value,
                    SharpnessStrength = (float)SharpSlider.Value,
                    DenoiseStrength = (float)DenoiseSlider.Value,
                    ApplyLightingRecovery = LightingRecoveryCheckBox.IsChecked == true,
                    PreferOnnxSuperResIfAvailable = PreferOnnxCheckBox.IsChecked == true
                };

                var result = await _enhancerService.EnhanceAsync(
                    _sourceBgra,
                    _sourceWidth,
                    _sourceHeight,
                    _sourceStride,
                    options,
                    _anprPipeline,
                    token);

                if (token.IsCancellationRequested) return;

                if (!result.Success)
                {
                    StatusTextBlock.Text = $"خطا: {result.ErrorMessage}";
                    MessageBox.Show(result.ErrorMessage, "خطای پردازش", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _lastResult = result;

                var enhancedBmp = CreateFrozenBitmapFromBgra(
                    result.EnhancedBgraBuffer,
                    result.EnhancedWidth,
                    result.EnhancedHeight,
                    result.EnhancedStride);

                AfterImage.Source = enhancedBmp;
                SingleLargeAfterImage.Source = enhancedBmp;
                AfterPlaceholderText.Visibility = Visibility.Collapsed;
                AfterResolutionText.Text = $"{result.EnhancedWidth} × {result.EnhancedHeight} px";

                float sharpBefore = Math.Max(1f, result.MetricsBefore.LaplacianSharpness);
                float sharpGainPct = ((result.MetricsAfter.LaplacianSharpness - sharpBefore) / sharpBefore) * 100f;
                float blurDrop = Math.Max(0f, result.MetricsBefore.MotionBlurScore - result.MetricsAfter.MotionBlurScore);

                SharpnessGainBadge.Text = $"بهبود وضوح: {(sharpGainPct >= 0 ? "+" : "")}{sharpGainPct:F0}٪";
                AfterSharpnessBadge.Text = $"وضوح لبه: {result.MetricsAfter.LaplacianSharpness:F0}";
                AfterBlurBadge.Text = $"کاهش تاری: -{blurDrop:F0}٪";
                AfterDurationBadge.Text = $"زمان پردازش: {result.ProcessingDurationMs:F0} ms";

                MethodSummaryText.Text = $"روش اجراشده: {result.MethodSummaryFa} | موتور: {result.ExecutionProvider}";

                if (!string.IsNullOrWhiteSpace(result.PlateOcrAfter))
                {
                    OcrComparisonBox.Visibility = Visibility.Visible;
                    string bTxt = string.IsNullOrWhiteSpace(result.PlateOcrBefore) ? "--" : result.PlateOcrBefore;
                    OcrComparisonValueText.Text = $"قبل: {bTxt} ({result.PlateOcrConfBefore:P0})  ←  بعد: {result.PlateOcrAfter} ({result.PlateOcrConfAfter:P0})";
                }
                else
                {
                    OcrComparisonBox.Visibility = Visibility.Collapsed;
                }

                SaveEnhancedButton.IsEnabled = true;
                CopyEnhancedButton.IsEnabled = true;
                StatusTextBlock.Text = $"✅ شفاف‌سازی و بزرگ‌نمایی با موفقیت تکمیل شد ({result.EnhancedWidth}×{result.EnhancedHeight} در {result.ProcessingDurationMs:F0} میلی‌ثانیه).";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"خطا در شفاف‌سازی تصویر: {ex.Message}";
                MessageBox.Show($"خطا در اجرای پردازش هوش مصنوعی: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EnhanceButton.IsEnabled = true;
            }
        }

        private void SaveEnhancedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult == null || _lastResult.EnhancedBgraBuffer.Length == 0) return;

            var dlg = new SaveFileDialog
            {
                Title = "ذخیره تصویر شفاف‌شده",
                FileName = $"Enhanced_{_lastResult.Target}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Filter = "تصویر بدون افت کیفیت (PNG)|*.png|تصویر JPEG|*.jpg"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    if (dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        var bmp = BitmapSource.Create(
                            _lastResult.EnhancedWidth,
                            _lastResult.EnhancedHeight,
                            96, 96,
                            PixelFormats.Bgra32,
                            null,
                            _lastResult.EnhancedBgraBuffer,
                            _lastResult.EnhancedStride);
                        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using var fs = File.Create(dlg.FileName);
                        enc.Save(fs);
                    }
                    else
                    {
                        File.WriteAllBytes(dlg.FileName, _lastResult.EnhancedPngBytes);
                    }

                    StatusTextBlock.Text = $"تصویر با موفقیت در مسیر انتخابی ذخیره شد: {Path.GetFileName(dlg.FileName)}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"خطا در ذخیره فایل: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CopyEnhancedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult == null || _lastResult.EnhancedBgraBuffer.Length == 0) return;

            try
            {
                var bmp = CreateFrozenBitmapFromBgra(
                    _lastResult.EnhancedBgraBuffer,
                    _lastResult.EnhancedWidth,
                    _lastResult.EnhancedHeight,
                    _lastResult.EnhancedStride);
                if (bmp != null)
                {
                    Clipboard.SetImage(bmp);
                    StatusTextBlock.Text = "✅ تصویر واضح‌شده در کلیپ‌بورد کپی شد (Ctrl+V در هر برنامه‌ای آماده درج است).";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در کپی به کلیپ‌بورد: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool single = SingleLargeModeRadio.IsChecked == true;
            BeforeAfterSplitGrid.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            SingleLargeAfterGrid.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ZoomMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool actual = ActualPixelsZoomCheckBox.IsChecked == true;
            Stretch targetStretch = actual ? Stretch.None : Stretch.Uniform;
            BeforeImage.Stretch = targetStretch;
            AfterImage.Stretch = targetStretch;
            SingleLargeAfterImage.Stretch = targetStretch;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static BitmapSource? CreateFrozenBitmapFromBgra(byte[] bgra, int width, int height, int stride)
        {
            if (bgra == null || bgra.Length == 0 || width <= 0 || height <= 0 || stride <= 0) return null;
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            bmp.Freeze();
            return bmp;
        }
    }
}
