using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance
{
    /// <summary>
    /// استودیوی شفاف‌سازی، حذف شبکه مانیتور (Demoiré)، رفع تاری (TV-RL) و فوق‌وضوح (Super-Resolution)
    /// با نمایش تمام‌کادر (Fit)، مقایسه اسلایدری (Wipe)، زوم همگام و انتخاب کادر پلاک/چهره
    /// </summary>
    public partial class PhotoEnhancerWindow : Window
    {
        private readonly AiPhotoEnhancementService _enhancerService = new();
        private readonly AnprPipeline? _anprPipeline;

        // تصویر اصلی کامل (هرگز تغییر نمی‌کند — Original Frame Protection)
        private byte[]? _fullOriginalBgra;
        private int _fullOriginalWidth;
        private int _fullOriginalHeight;
        private int _fullOriginalStride;
        private string _sourceTitle = string.Empty;

        // تصویر فعال فعلی (یا کل عکس یا ناحیه برش‌خورده پلاک/چهره)
        private byte[]? _activeSourceBgra;
        private int _activeSourceWidth;
        private int _activeSourceHeight;
        private int _activeSourceStride;
        private bool _isCropped;

        private PhotoEnhancementResult? _lastResult;
        private CancellationTokenSource? _activeCts;
        private bool _isSuppressingPresetSync;
        private bool _autoRunOnLoaded;

        // وضعیت زوم: 0 = Fit (تمام‌کادر بدون بریدگی)، 1.0 = 100%، 2.0 = 200%، 4.0 = 400%
        private double _zoomLevel = 0.0;
        private bool _isSyncingScroll;

        // وضعیت ابزار انتخاب کادر (ROI Crop)
        private bool _isCropModeActive;
        private bool _isDraggingCrop;
        private Point _cropStartPoint;

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
            _autoRunOnLoaded = _activeSourceBgra != null;
        }

        private async void PhotoEnhancerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshEngineStatusBadge();
            ApplyZoomMode(0.0);

            if (_autoRunOnLoaded && _activeSourceBgra != null)
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
                        DeblurIterSlider.Value = 4;
                        SharpSlider.Value = 0.45;
                        DenoiseSlider.Value = 0.45;
                        MoireSlider.Value = 0.60;
                        break;

                    case PhotoEnhancementTarget.GeneralScene:
                        TargetGeneralRadio.IsChecked = true;
                        Scale1xRadio.IsChecked = true;
                        DeblurIterSlider.Value = 4;
                        SharpSlider.Value = 0.55;
                        DenoiseSlider.Value = 0.35;
                        MoireSlider.Value = 0.65;
                        break;

                    case PhotoEnhancementTarget.LicensePlate:
                    default:
                        TargetPlateRadio.IsChecked = true;
                        Scale2xRadio.IsChecked = true;
                        DeblurIterSlider.Value = 5;
                        SharpSlider.Value = 0.70;
                        DenoiseSlider.Value = 0.35;
                        MoireSlider.Value = 0.65;
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
                EngineStatusBadgeText.Text = "موتور محلی YCbCr (Demoiré + TV-RL + Catmull-Rom)";
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

                _fullOriginalBgra = bgra;
                _fullOriginalWidth = w;
                _fullOriginalHeight = h;
                _fullOriginalStride = stride;
                _sourceTitle = title;
                _isCropped = false;

                SetActiveSourceBuffer(bgra, w, h, stride, isCropped: false);

                // برای تصاویر بزرگ‌تر از ۹۰۰ پیکسل، پیش‌فرض مقیاس 1x تا سرعت و کیفیت بهینه باشد (و برای برش کوچک 2x/4x)
                if (Math.Max(w, h) >= 900)
                {
                    Scale1xRadio.IsChecked = true;
                }
                else
                {
                    Scale2xRadio.IsChecked = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در خواندن تصویر ورودی: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetActiveSourceBuffer(byte[] bgra, int w, int h, int stride, bool isCropped)
        {
            _activeSourceBgra = bgra;
            _activeSourceWidth = w;
            _activeSourceHeight = h;
            _activeSourceStride = stride;
            _isCropped = isCropped;
            _lastResult = null;

            var srcBmp = CreateFrozenBitmapFromBgra(bgra, w, h, stride);
            BeforeImage.Source = srcBmp;
            SplitBeforeImage.Source = srcBmp;
            BeforePlaceholderText.Visibility = Visibility.Collapsed;
            BeforeResolutionText.Text = $"{w} × {h} px";

            string cropTag = isCropped ? " [کادر برش‌خورده]" : "";
            SourceInfoText.Text = $"منبع: {_sourceTitle}{cropTag} ({w}×{h} px)";

            CropSelectModeButton.IsEnabled = true;
            ResetCropButton.IsEnabled = isCropped;

            // تشخیص فوری الگوی شبکه پیکسلی مانیتور (Moiré)
            var moire = AiPhotoEnhancementService.DetectScreenMoireGrid(bgra, w, h, stride);
            if (moire.HasScreenGrid)
            {
                MoireDetectedBadge.Visibility = Visibility.Visible;
                MoireDetectedBadgeText.Text = $"🖥 الگوی صفحه مانیتور شناسایی شد (دوره ~{moire.PeriodPx}px)";
                RemoveMoireCheckBox.IsChecked = true;
            }
            else
            {
                MoireDetectedBadge.Visibility = Visibility.Collapsed;
            }

            var m = ImageProcessingFilterSet.CalculateQualityMetrics(bgra.AsSpan(), w, h, stride, 4);
            BeforeSharpnessBadge.Text = $"وضوح: {m.LaplacianSharpness:F0}";
            BeforeBlurBadge.Text = $"تاری: {m.MotionBlurScore:F0}%";
            BeforeContrastBadge.Text = $"کنتراست: {m.ContrastStdDev:F1}";

            AfterImage.Source = null;
            SplitAfterImage.Source = null;
            SingleLargeAfterImage.Source = null;
            AfterPlaceholderText.Visibility = Visibility.Visible;
            SaveEnhancedButton.IsEnabled = false;
            CopyEnhancedButton.IsEnabled = false;
            OcrComparisonBox.Visibility = Visibility.Collapsed;

            ApplyZoomMode(0.0);
            StatusTextBlock.Text = $"تصویر با ابعاد {w}×{h} در حالت تمام‌کادر (Fit) بارگذاری شد.";
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

        private void ImportOnnxButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "انتخاب فایل مدل شبکه عصبی ONNX (Real-ESRGAN / GFPGAN / CodeFormer)",
                Filter = "مدل ONNX (*.onnx)|*.onnx"
            };

            if (dlg.ShowDialog(this) == true)
            {
                var (ok, _, msg) = _enhancerService.ImportOnnxModel(dlg.FileName);
                MessageBox.Show(msg, ok ? "نصب موفق مدل AI" : "خطای مدل ONNX", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                if (ok)
                {
                    RefreshEngineStatusBadge();
                    if (_activeSourceBgra != null)
                    {
                        _ = RunEnhancementAsync();
                    }
                }
            }
        }

        private void ToggleSidePanelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigSidePanel.Visibility == Visibility.Visible)
            {
                ConfigSidePanel.Visibility = Visibility.Collapsed;
                SidePanelColumn.Width = new GridLength(0);
                ToggleSidePanelBtn.Content = "▶ نمایش پنل تنظیمات";
            }
            else
            {
                ConfigSidePanel.Visibility = Visibility.Visible;
                SidePanelColumn.Width = new GridLength(335);
                ToggleSidePanelBtn.Content = "◀ مخفی کردن پنل";
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && File.Exists(files[0]))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(files[0]);
                    LoadEncodedImageBytes(bytes, Path.GetFileName(files[0]));
                    _ = RunEnhancementAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"فایل رهاشده تصویر معتبری نیست: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (MoireValueText != null && MoireSlider != null)
                MoireValueText.Text = MoireSlider.Value.ToString("F2");

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
            if (_activeSourceBgra == null || _activeSourceWidth < 8 || _activeSourceHeight < 8)
            {
                MessageBox.Show("ابتدا یک تصویر پلاک یا چهره را انتخاب کنید یا داخل پنجره بکشید.", "تصویر انتخاب نشده", MessageBoxButton.OK, MessageBoxImage.Information);
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
            StatusTextBlock.Text = "⏳ در حال پاکسازی شبکه مانیتور، رفع تاری YCbCr و بازسازی وضوح (چندنخی)...";

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
                    RemoveScreenMoire = RemoveMoireCheckBox.IsChecked == true,
                    MoireRemovalStrength = (float)MoireSlider.Value,
                    PreferOnnxSuperResIfAvailable = PreferOnnxCheckBox.IsChecked == true
                };

                var result = await _enhancerService.EnhanceAsync(
                    _activeSourceBgra,
                    _activeSourceWidth,
                    _activeSourceHeight,
                    _activeSourceStride,
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
                SplitAfterImage.Source = enhancedBmp;
                SingleLargeAfterImage.Source = enhancedBmp;
                AfterPlaceholderText.Visibility = Visibility.Collapsed;
                AfterResolutionText.Text = $"{result.EnhancedWidth} × {result.EnhancedHeight} px";

                if (result.DetectedScreenMoire)
                {
                    MoireDetectedBadge.Visibility = Visibility.Visible;
                    MoireDetectedBadgeText.Text = $"🖥 شبکه مانیتور پاکسازی شد (دوره ~{result.DetectedGridPeriodPx}px)";
                    SharpnessGainBadge.Text = "پاکسازی شبکه + شفاف‌سازی موفق";
                }
                else
                {
                    float sharpBefore = Math.Max(1f, result.MetricsBefore.LaplacianSharpness);
                    float sharpGainPct = ((result.MetricsAfter.LaplacianSharpness - sharpBefore) / sharpBefore) * 100f;
                    SharpnessGainBadge.Text = $"تغییر وضوح: {(sharpGainPct >= 0 ? "+" : "")}{sharpGainPct:F0}٪";
                }

                AfterSharpnessBadge.Text = $"وضوح: {result.MetricsAfter.LaplacianSharpness:F0}";
                AfterBlurBadge.Text = $"تاری: {result.MetricsAfter.MotionBlurScore:F0}%";
                AfterDurationBadge.Text = $"زمان: {result.ProcessingDurationMs:F0} ms";

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

                ApplyZoomMode(_zoomLevel);
                UpdateSplitClip();

                StatusTextBlock.Text = $"✅ شفاف‌سازی تکمیل شد ({result.EnhancedWidth}×{result.EnhancedHeight} در {result.ProcessingDurationMs:F0} میلی‌ثانیه).";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"خطا در شفاف‌سازی تصویر: {ex.Message}";
                MessageBox.Show($"خطا در اجرای پردازش: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EnhanceButton.IsEnabled = true;
            }
        }

        #region انتخاب کادر پلاک / چهره روی تصویر قبل (ROI Crop)

        private void CropSelectModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceBgra == null) return;

            _isCropModeActive = !_isCropModeActive;
            if (_isCropModeActive)
            {
                // برای انتخاب دقیق کادر، به نمای دوتایی تمام‌کادر می‌رویم
                SideBySideModeRadio.IsChecked = true;
                ApplyZoomMode(0.0);
                CropSelectModeButton.Content = "✕ لغو انتخاب کادر";
                CropSelectModeButton.Style = (Style)FindResource("DangerButtonStyle");
                CropHintInlineText.Visibility = Visibility.Visible;
                BeforeImageStage.Cursor = Cursors.Cross;
                StatusTextBlock.Text = "✂️ روی تصویر «قبل» کلیک و درگ کنید تا کادر دقیق پلاک یا چهره انتخاب و بازسازی شود.";
            }
            else
            {
                ExitCropModeUI();
            }
        }

        private void ExitCropModeUI()
        {
            _isCropModeActive = false;
            _isDraggingCrop = false;
            CropSelectionRect.Visibility = Visibility.Collapsed;
            CropSelectModeButton.Content = "✂️ انتخاب کادر پلاک / چهره روی عکس";
            CropSelectModeButton.Style = (Style)FindResource("SecondaryButtonStyle");
            CropHintInlineText.Visibility = Visibility.Collapsed;
            BeforeImageStage.Cursor = Cursors.Arrow;
        }

        private void ResetCropButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fullOriginalBgra == null) return;
            ExitCropModeUI();
            SetActiveSourceBuffer(_fullOriginalBgra, _fullOriginalWidth, _fullOriginalHeight, _fullOriginalStride, isCropped: false);
            Scale1xRadio.IsChecked = Math.Max(_fullOriginalWidth, _fullOriginalHeight) >= 900;
            Scale2xRadio.IsChecked = Math.Max(_fullOriginalWidth, _fullOriginalHeight) < 900;
            _ = RunEnhancementAsync();
        }

        private void BeforeImageStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isCropModeActive || BeforeImage.Source == null) return;

            _isDraggingCrop = true;
            _cropStartPoint = e.GetPosition(BeforeImage);
            Canvas.SetLeft(CropSelectionRect, _cropStartPoint.X);
            Canvas.SetTop(CropSelectionRect, _cropStartPoint.Y);
            CropSelectionRect.Width = 0;
            CropSelectionRect.Height = 0;
            CropSelectionRect.Visibility = Visibility.Visible;
            BeforeImageStage.CaptureMouse();
        }

        private void BeforeImageStage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingCrop || BeforeImage.ActualWidth <= 0 || BeforeImage.ActualHeight <= 0) return;

            Point cur = e.GetPosition(BeforeImage);
            double x = Math.Clamp(Math.Min(_cropStartPoint.X, cur.X), 0, BeforeImage.ActualWidth);
            double y = Math.Clamp(Math.Min(_cropStartPoint.Y, cur.Y), 0, BeforeImage.ActualHeight);
            double x2 = Math.Clamp(Math.Max(_cropStartPoint.X, cur.X), 0, BeforeImage.ActualWidth);
            double y2 = Math.Clamp(Math.Max(_cropStartPoint.Y, cur.Y), 0, BeforeImage.ActualHeight);

            Canvas.SetLeft(CropSelectionRect, x);
            Canvas.SetTop(CropSelectionRect, y);
            CropSelectionRect.Width = Math.Max(1, x2 - x);
            CropSelectionRect.Height = Math.Max(1, y2 - y);
        }

        private async void BeforeImageStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingCrop) return;
            _isDraggingCrop = false;
            BeforeImageStage.ReleaseMouseCapture();

            if (_activeSourceBgra == null || BeforeImage.ActualWidth <= 10 || BeforeImage.ActualHeight <= 10)
            {
                ExitCropModeUI();
                return;
            }

            double rectX = Canvas.GetLeft(CropSelectionRect);
            double rectY = Canvas.GetTop(CropSelectionRect);
            double rectW = CropSelectionRect.Width;
            double rectH = CropSelectionRect.Height;

            ExitCropModeUI();

            if (rectW < 12 || rectH < 12)
            {
                StatusTextBlock.Text = "کادر انتخابی بسیار کوچک بود؛ لطفاً ناحیه بزرگ‌تری بکشید.";
                return;
            }

            // تبدیل دقیق مختصات نمایشگر به مختصات پیکسل واقعی تصویر فعال
            double scaleX = _activeSourceWidth / BeforeImage.ActualWidth;
            double scaleY = _activeSourceHeight / BeforeImage.ActualHeight;

            int px = (int)Math.Round(rectX * scaleX);
            int py = (int)Math.Round(rectY * scaleY);
            int pw = (int)Math.Round(rectW * scaleX);
            int ph = (int)Math.Round(rectH * scaleY);

            var (cropBgra, cw, ch, cStride) = AiPhotoEnhancementService.CropBgra32(
                _activeSourceBgra, _activeSourceWidth, _activeSourceHeight, _activeSourceStride,
                px, py, pw, ph);

            SetActiveSourceBuffer(cropBgra, cw, ch, cStride, isCropped: true);

            // روی برش کوچک پلاک یا چهره، بزرگ‌نمایی 2x یا 4x فوق‌العاده موثر است
            if (Math.Max(cw, ch) < 280)
                Scale4xRadio.IsChecked = true;
            else
                Scale2xRadio.IsChecked = true;

            await RunEnhancementAsync();
        }

        #endregion

        #region کنترل زوم دقیق، تمام‌کادر (Fit) و مقایسه اسلایدری (Wipe)

        private void ZoomFitBtn_Click(object sender, RoutedEventArgs e) => ApplyZoomMode(0.0);
        private void Zoom100Btn_Click(object sender, RoutedEventArgs e) => ApplyZoomMode(1.0);
        private void Zoom200Btn_Click(object sender, RoutedEventArgs e) => ApplyZoomMode(2.0);
        private void Zoom400Btn_Click(object sender, RoutedEventArgs e) => ApplyZoomMode(4.0);

        private void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                double next = _zoomLevel <= 0 ? (e.Delta > 0 ? 1.0 : 0.0) : Math.Clamp(_zoomLevel + (e.Delta > 0 ? 0.5 : -0.5), 0.0, 4.0);
                if (next < 0.6) next = 0.0;
                ApplyZoomMode(next);
            }
        }

        private void ApplyZoomMode(double zoom)
        {
            _zoomLevel = zoom;

            // بروزرسانی رنگ دکمه‌های زوم
            var primary = (Style)FindResource("PrimaryButtonStyle");
            var secondary = (Style)FindResource("SecondaryButtonStyle");
            ZoomFitBtn.Style = zoom <= 0.01 ? primary : secondary;
            Zoom100Btn.Style = Math.Abs(zoom - 1.0) < 0.05 ? primary : secondary;
            Zoom200Btn.Style = Math.Abs(zoom - 2.0) < 0.05 ? primary : secondary;
            Zoom400Btn.Style = Math.Abs(zoom - 4.0) < 0.05 ? primary : secondary;

            if (zoom <= 0.01)
            {
                // حالت تمام‌کادر واقعی (Fit): اسکرول‌بارها Disabled می‌شوند تا WPF عکس را دقیقاً داخل کادر مقیاس دهد
                BeforeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                BeforeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                AfterScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                AfterScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                SingleLargeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                SingleLargeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

                BeforeImage.Width = double.NaN;
                BeforeImage.Height = double.NaN;
                BeforeImage.Stretch = Stretch.Uniform;

                AfterImage.Width = double.NaN;
                AfterImage.Height = double.NaN;
                AfterImage.Stretch = Stretch.Uniform;

                SingleLargeAfterImage.Width = double.NaN;
                SingleLargeAfterImage.Height = double.NaN;
                SingleLargeAfterImage.Stretch = Stretch.Uniform;
            }
            else
            {
                // حالت بزرگ‌نمایی دقیق همگام (Before و After با یک ابعاد بصری مشترک نمایش داده می‌شوند)
                BeforeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                BeforeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                AfterScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                AfterScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                SingleLargeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                SingleLargeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

                double baseW = _activeSourceWidth > 0 ? _activeSourceWidth : 800;
                double baseH = _activeSourceHeight > 0 ? _activeSourceHeight : 600;
                double viewW = baseW * zoom;
                double viewH = baseH * zoom;

                BeforeImage.Stretch = Stretch.Fill;
                BeforeImage.Width = viewW;
                BeforeImage.Height = viewH;

                AfterImage.Stretch = Stretch.Fill;
                AfterImage.Width = viewW;
                AfterImage.Height = viewH;

                SingleLargeAfterImage.Stretch = Stretch.Fill;
                SingleLargeAfterImage.Width = viewW;
                SingleLargeAfterImage.Height = viewH;
            }
        }

        private void BeforeScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSyncingScroll || _zoomLevel <= 0.01) return;
            _isSyncingScroll = true;
            try
            {
                AfterScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
                AfterScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            }
            finally
            {
                _isSyncingScroll = false;
            }
        }

        private void AfterScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSyncingScroll || _zoomLevel <= 0.01) return;
            _isSyncingScroll = true;
            try
            {
                BeforeScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
                BeforeScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            }
            finally
            {
                _isSyncingScroll = false;
            }
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool isSplit = SplitSliderModeRadio.IsChecked == true;
            bool isSingle = SingleLargeModeRadio.IsChecked == true;

            BeforeAfterSplitGrid.Visibility = (!isSplit && !isSingle) ? Visibility.Visible : Visibility.Collapsed;
            SplitWipeContainer.Visibility = isSplit ? Visibility.Visible : Visibility.Collapsed;
            SingleLargeAfterGrid.Visibility = isSingle ? Visibility.Visible : Visibility.Collapsed;

            if (isSplit)
            {
                UpdateSplitClip();
            }
        }

        private void SplitCompareStage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSplitClip();
        private void SplitPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSplitClip();

        private void UpdateSplitClip()
        {
            if (SplitAfterImage == null || SplitAfterClipRect == null || SplitCompareStage == null) return;

            double stageW = SplitCompareStage.ActualWidth;
            double stageH = SplitCompareStage.ActualHeight;
            if (stageW <= 1 || stageH <= 1) return;

            double frac = SplitPositionSlider != null ? (SplitPositionSlider.Value / 100.0) : 0.5;
            double dividerX = stageW * frac;

            double imgW = SplitAfterImage.ActualWidth > 0 ? SplitAfterImage.ActualWidth : stageW;
            double imgH = SplitAfterImage.ActualHeight > 0 ? SplitAfterImage.ActualHeight : stageH;
            double imgLeft = (stageW - imgW) / 2.0;
            double clipW = Math.Clamp(dividerX - imgLeft, 0, imgW);

            SplitAfterClipRect.Rect = new Rect(0, 0, clipW, imgH);
            Canvas.SetLeft(SplitDividerLine, dividerX);
            SplitDividerLine.Height = stageH;
        }

        #endregion

        #region ذخیره و کپی خروجی

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

                    StatusTextBlock.Text = $"تصویر با موفقیت ذخیره شد: {Path.GetFileName(dlg.FileName)}";
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
                    StatusTextBlock.Text = "✅ تصویر واضح‌شده در کلیپ‌بورد کپی شد (با Ctrl+V آماده درج است).";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در کپی به کلیپ‌بورد: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private static BitmapSource? CreateFrozenBitmapFromBgra(byte[] bgra, int width, int height, int stride)
        {
            if (bgra == null || bgra.Length == 0 || width <= 0 || height <= 0 || stride <= 0) return null;
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
            bmp.Freeze();
            return bmp;
        }

        #endregion
    }
}
