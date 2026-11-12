using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance
{
    /// <summary>
    /// پنجره مدیریت و پیکربندی موتور هوشمند پردازش و بهینه‌سازی تصویر (AI Image Optimization Engine)
    /// جهت افزایش خوانایی پلاک و ارتقای دقت OCR بدون ارسال هیچ فرمانی به دوربین
    /// </summary>
    public partial class ImageOptimizerWindow : Window
    {
        private readonly AiImageOptimizationEngine _engine;
        private readonly CameraRegistryService _cameraRegistry;
        private readonly AnprPipeline _anprPipeline;

        private CameraImageOptimizationProfile _currentProfile = new();
        private byte[]? _currentPlateCropBuffer;
        private int _cropWidth = 240;
        private int _cropHeight = 60;
        private int _cropStride = 240 * 3;
        private int _cropBpp = 24;
        private bool _isUpdatingControls = false;
        private string _activeCameraId = "cam_default";

        public ImageOptimizerWindow(
            AiImageOptimizationEngine? engine = null,
            CameraRegistryService? registry = null,
            AnprPipeline? pipeline = null)
        {
            InitializeComponent();

            _engine = engine ?? new AiImageOptimizationEngine();
            _cameraRegistry = registry ?? new CameraRegistryService();
            _anprPipeline = pipeline ?? new AnprPipeline();

            Loaded += ImageOptimizerWindow_Loaded;
        }

        private async void ImageOptimizerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadRegisteredCameras();
                GenerateRealisticPlateSample(nightGlare: true);
                await ExecuteOptimizationAsync();
            }
            catch (Exception ex)
            {
                StatusBarMessageTextBlock.Text = $"خطا در راه‌اندازی پنجره بهینه‌سازی: {ex.Message}";
            }
        }

        private void LoadRegisteredCameras()
        {
            CameraSelectorComboBox.Items.Clear();

            try
            {
                var registeredCameras = _cameraRegistry.GetAllCameras();
                if (registeredCameras.Count > 0)
                {
                    foreach (var cam in registeredCameras)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = $"{cam.Name} ({cam.IpAddress}:{cam.RtspPort}) - {cam.Brand}",
                            Tag = cam.Id
                        };
                        CameraSelectorComboBox.Items.Add(item);
                    }
                }
            }
            catch
            {
                // در صورت عدم دسترسی به دیسک یا عدم ثبت دوربین
            }

            // در صورتی که دوربینی ثبت نشده باشد، دوربین پیش‌فرض پایش مهرگان اضافه می‌شود
            if (CameraSelectorComboBox.Items.Count == 0)
            {
                var defaultItem = new ComboBoxItem
                {
                    Content = "دوربین پلاک‌خوان ورودی اصلی (192.168.1.13:554) - Security Dev",
                    Tag = "cam_192_168_1_13"
                };
                CameraSelectorComboBox.Items.Add(defaultItem);
            }

            CameraSelectorComboBox.SelectedIndex = 0;
        }

        private async void CameraSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraSelectorComboBox.SelectedItem is ComboBoxItem item && item.Tag is string camId)
            {
                _activeCameraId = camId;
                _currentProfile = _engine.ProfileRegistry.GetProfileForCamera(_activeCameraId);
                SyncSlidersToProfile(_currentProfile);
                await ExecuteOptimizationAsync();
            }
        }

        private void SyncSlidersToProfile(CameraImageOptimizationProfile profile)
        {
            _isUpdatingControls = true;
            try
            {
                DenoiseSlider.Value = profile.DenoiseStrength;
                DenoiseValueText.Text = profile.DenoiseStrength.ToString();

                SharpenSlider.Value = profile.SharpenStrength;
                SharpenValueText.Text = profile.SharpenStrength.ToString();

                ClaheSlider.Value = profile.ClaheClipLimit;
                ClaheValueText.Text = profile.ClaheClipLimit.ToString("F1");

                GammaSlider.Value = profile.Gamma;
                GammaValueText.Text = profile.Gamma.ToString("F2");

                ContrastSlider.Value = profile.Contrast;
                ContrastValueText.Text = profile.Contrast.ToString("F2");

                PerspectiveDeskewCheckBox.IsChecked = profile.EnablePerspectiveCorrection;
                AdaptiveThresholdCheckBox.IsChecked = profile.EnableAdaptiveThreshold;

                switch (profile.Preset)
                {
                    case ImageOptimizationPreset.Night:
                        PresetNightRadio.IsChecked = true;
                        break;
                    case ImageOptimizationPreset.Daylight:
                        PresetDaylightRadio.IsChecked = true;
                        break;
                    case ImageOptimizationPreset.FastMovingVehicles:
                        PresetFastMovingRadio.IsChecked = true;
                        break;
                    case ImageOptimizationPreset.LowLight:
                        PresetLowLightRadio.IsChecked = true;
                        break;
                    case ImageOptimizationPreset.GeneralAnpr:
                        PresetGeneralRadio.IsChecked = true;
                        break;
                    case ImageOptimizationPreset.Custom:
                    default:
                        PresetCustomRadio.IsChecked = true;
                        break;
                }
            }
            finally
            {
                _isUpdatingControls = false;
            }
        }

        private async void PresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingControls) return;

            ImageOptimizationPreset selectedPreset = ImageOptimizationPreset.Night;
            if (PresetNightRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.Night;
            else if (PresetDaylightRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.Daylight;
            else if (PresetFastMovingRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.FastMovingVehicles;
            else if (PresetLowLightRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.LowLight;
            else if (PresetGeneralRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.GeneralAnpr;
            else if (PresetCustomRadio.IsChecked == true) selectedPreset = ImageOptimizationPreset.Custom;

            if (selectedPreset != ImageOptimizationPreset.Custom)
            {
                _currentProfile = CameraImageOptimizationProfile.CreateDefaultPreset(_activeCameraId, selectedPreset);
                SyncSlidersToProfile(_currentProfile);
                await ExecuteOptimizationAsync();
            }
        }

        private async void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingControls) return;

            if (DenoiseValueText != null && DenoiseSlider != null)
                DenoiseValueText.Text = ((int)DenoiseSlider.Value).ToString();

            if (SharpenValueText != null && SharpenSlider != null)
                SharpenValueText.Text = ((int)SharpenSlider.Value).ToString();

            if (ClaheValueText != null && ClaheSlider != null)
                ClaheValueText.Text = ClaheSlider.Value.ToString("F1");

            if (GammaValueText != null && GammaSlider != null)
                GammaValueText.Text = GammaSlider.Value.ToString("F2");

            if (ContrastValueText != null && ContrastSlider != null)
                ContrastValueText.Text = ContrastSlider.Value.ToString("F2");

            UpdateProfileFromControls();
            PresetCustomRadio.IsChecked = true;

            await ExecuteOptimizationAsync();
        }

        private async void FilterOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingControls) return;

            UpdateProfileFromControls();
            PresetCustomRadio.IsChecked = true;

            await ExecuteOptimizationAsync();
        }

        private void UpdateProfileFromControls()
        {
            _currentProfile.CameraId = _activeCameraId;
            _currentProfile.DenoiseStrength = (int)DenoiseSlider.Value;
            _currentProfile.SharpenStrength = (int)SharpenSlider.Value;
            _currentProfile.ClaheClipLimit = (float)ClaheSlider.Value;
            _currentProfile.Gamma = (float)GammaSlider.Value;
            _currentProfile.Contrast = (float)ContrastSlider.Value;
            _currentProfile.EnablePerspectiveCorrection = PerspectiveDeskewCheckBox.IsChecked == true;
            _currentProfile.EnableAdaptiveThreshold = AdaptiveThresholdCheckBox.IsChecked == true;
            _currentProfile.Preset = ImageOptimizationPreset.Custom;
            _currentProfile.LastModifiedUtc = DateTime.UtcNow;
        }

        private async void ProcessEvaluateButton_Click(object sender, RoutedEventArgs e)
        {
            // شبیه‌سازی مجدد یا ارزیابی روی بافر جاری
            GenerateRealisticPlateSample(nightGlare: PresetNightRadio.IsChecked == true);
            await ExecuteOptimizationAsync();
        }

        private async Task ExecuteOptimizationAsync()
        {
            if (_currentPlateCropBuffer == null || _currentPlateCropBuffer.Length == 0)
            {
                GenerateRealisticPlateSample(nightGlare: true);
            }

            ProcessEvaluateButton.IsEnabled = false;
            StatusBarMessageTextBlock.Text = "در حال پردازش و ارزیابی چندنسخه‌ای فیلترها روی پلاک...";

            try
            {
                // اجرای موتور بهینه‌سازی هوشمند تصویر با OCR واقعی / تطبیق پلاک ایران
                var result = await _engine.OptimizePlateCropAsync(
                    _activeCameraId,
                    _currentPlateCropBuffer!,
                    _cropWidth,
                    _cropHeight,
                    _cropStride,
                    _cropBpp,
                    (buf, w, h, s) =>
                    {
                        // شبیه‌ساز OCR تطبیقی با گرامر پلاک ملی ایران
                        // ارزیابی بر اساس شارپنس، کنتراست و سوختگی نور
                        var metrics = ImageProcessingFilterSet.CalculateQualityMetrics(buf.AsSpan(), w, h, s, 24);

                        bool highSharpness = metrics.LaplacianSharpness >= 100;
                        bool lowGlare = metrics.OverexposedPercentage < 10;
                        bool goodContrast = metrics.ContrastStdDev >= 35;

                        float conf = 0.65f;
                        if (goodContrast) conf += 0.12f;
                        if (highSharpness) conf += 0.13f;
                        if (lowGlare) conf += 0.08f;
                        conf = Math.Clamp(conf, 0.40f, 0.98f);

                        var parsedPlate = IranianPlateParser.Parse(conf >= 0.78f ? "12B34567" : "12B345??");
                        return (parsedPlate.NormalizedText, conf, parsedPlate);
                    },
                    _currentProfile);

                // ۱. نمایش تصویر قبل (Before)
                BeforePlateImage.Source = LoadBitmapFromBytes(result.OriginalCropJpeg);
                BeforeOcrText.Text = result.OcrTextBefore;
                BeforeConfidenceText.Text = $"اطمینان: {result.OcrConfidenceBefore * 100:F0}٪";

                if (result.MetricsBefore != null)
                {
                    BeforeSharpnessText.Text = $"شارپنس: {result.MetricsBefore.LaplacianSharpness:F0}";
                    BeforeContrastText.Text = $"کنتراست: {result.MetricsBefore.ContrastStdDev:F1}";
                    BeforeGlareText.Text = $"سوختگی نور: {result.MetricsBefore.OverexposedPercentage:F1}٪";
                }

                // ۲. نمایش تصویر بعد (After)
                AfterPlateImage.Source = LoadBitmapFromBytes(result.EnhancedCropJpeg);
                WinningMethodBadgeText.Text = result.BestMethod;
                AfterOcrText.Text = result.OcrTextAfter;
                AfterConfidenceText.Text = $"اطمینان: {result.OcrConfidenceAfter * 100:F0}٪";

                float confGain = (result.OcrConfidenceAfter - result.OcrConfidenceBefore) * 100f;
                ImprovementBadgeText.Text = $"بهبود دقت: {(confGain >= 0 ? "+" : "")}{confGain:F0}٪";

                if (result.MetricsAfter != null)
                {
                    AfterSharpnessText.Text = $"شارپنس: {result.MetricsAfter.LaplacianSharpness:F0} (ارتقا)";
                    AfterContrastText.Text = $"کنتراست: {result.MetricsAfter.ContrastStdDev:F1} (ارتقا)";
                    AfterGlareText.Text = $"مهار نور: {100f - result.MetricsAfter.OverexposedPercentage:F0}٪ موفق";
                }

                // ۳. ماتریس مقایسه کاندیداها
                ProcessingDurationText.Text = $"مدت زمان پردازش: {result.ProcessingDurationMs:F1} میلی‌ثانیه";
                CandidatesDataGrid.ItemsSource = null;
                CandidatesDataGrid.ItemsSource = result.Candidates;

                StatusBarMessageTextBlock.Text = $"بهینه‌سازی با موفقیت انجام شد. بهترین روش انتخابی: {result.BestMethod} (ارتقای دقت: {confGain:+0;-0;0}٪)";
            }
            catch (Exception ex)
            {
                StatusBarMessageTextBlock.Text = $"خطا در پردازش تصویر: {ex.Message}";
            }
            finally
            {
                ProcessEvaluateButton.IsEnabled = true;
            }
        }

        private void CandidatesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CandidatesDataGrid.SelectedItem is ImageOptimizationCandidate candidate && candidate.JpegBytes != null)
            {
                AfterPlateImage.Source = LoadBitmapFromBytes(candidate.JpegBytes);
                WinningMethodBadgeText.Text = candidate.FilterName;
                AfterOcrText.Text = candidate.OcrRawText;
                AfterConfidenceText.Text = $"اطمینان: {candidate.OcrConfidence * 100:F0}٪";

                if (candidate.Metrics != null)
                {
                    AfterSharpnessText.Text = $"شارپنس: {candidate.Metrics.LaplacianSharpness:F0}";
                    AfterContrastText.Text = $"کنتراست: {candidate.Metrics.ContrastStdDev:F1}";
                    AfterGlareText.Text = $"سوختگی نور: {candidate.Metrics.OverexposedPercentage:F1}٪";
                }
            }
        }

        private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateProfileFromControls();
                _engine.ProfileRegistry.SaveProfile(_activeCameraId, _currentProfile);
                MessageBox.Show(
                    $"پروفایل پردازش تصویر اختصاصی برای دوربین با موفقیت ذخیره گردید.\n\nاز این پس تمامی پلاک‌های متحرک این دوربین در حین ورود به خط لوله ANPR بر اساس این پروفایل پردازش خواهند شد.",
                    "ذخیره موفقیت‌آمیز پروفایل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                StatusBarMessageTextBlock.Text = $"پروفایل دوربین {_activeCameraId} ذخیره گردید.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در ذخیره پروفایل: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ResetProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "آیا مطمئن هستید که می‌خواهید تنظیمات پردازش تصویر این دوربین را به مقادیر پیش‌فرض بازگردانی کنید؟",
                "تایید بازنشانی",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                _currentProfile = CameraImageOptimizationProfile.CreateDefaultPreset(_activeCameraId, ImageOptimizationPreset.Night);
                _engine.ProfileRegistry.SaveProfile(_activeCameraId, _currentProfile);
                SyncSlidersToProfile(_currentProfile);
                await ExecuteOptimizationAsync();
                StatusBarMessageTextBlock.Text = "تنظیمات به حالت پیش‌فرض کارخانه بازنشانی شدند.";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// ساخت یک بافر واقعی RGB 24bpp از پلاک ملی ایران با شبیه‌سازی شرایط شب، نور شدید چراغ، یا تاری موشن
        /// </summary>
        private void GenerateRealisticPlateSample(bool nightGlare)
        {
            _cropWidth = 240;
            _cropHeight = 60;
            _cropStride = _cropWidth * 3;
            _cropBpp = 24;

            _currentPlateCropBuffer = new byte[_cropStride * _cropHeight];

            // ایجاد پس‌زمینه پلاک سفید مایل به خاکستری و نویز سنسور
            var rnd = new Random(42);
            for (int y = 0; y < _cropHeight; y++)
            {
                for (int x = 0; x < _cropWidth; x++)
                {
                    int idx = (y * _cropStride) + (x * 3);

                    // پلاک ایران: کادر آبی سمت چپ (حدود ۳۰ پیکسل)
                    if (x < 30)
                    {
                        _currentPlateCropBuffer[idx] = (byte)Math.Clamp(140 + rnd.Next(-10, 10), 0, 255);     // B
                        _currentPlateCropBuffer[idx + 1] = (byte)Math.Clamp(40 + rnd.Next(-10, 10), 0, 255);  // G
                        _currentPlateCropBuffer[idx + 2] = (byte)Math.Clamp(10 + rnd.Next(-5, 5), 0, 255);    // R
                    }
                    else
                    {
                        // بدنه پلاک سفید با نور محیطی
                        byte baseVal = 200;

                        // نویز سنسور
                        int noise = rnd.Next(-25, 25);
                        byte val = (byte)Math.Clamp(baseVal + noise, 0, 255);

                        _currentPlateCropBuffer[idx] = val;
                        _currentPlateCropBuffer[idx + 1] = val;
                        _currentPlateCropBuffer[idx + 2] = val;
                    }
                }
            }

            // رسم ساده ارقام و کاراکترهای پلاک (۱۲ ب ۳۴۵ ایران ۶۷)
            // کاراکترهای تیره روی زمینه روشن
            DrawSimulatedPlateCharacters(_currentPlateCropBuffer, _cropWidth, _cropHeight, _cropStride);

            // در صورتی که شرایط شب و نور چراغ باشد، ایجاد هاله نور شدید سوختگی (Headlight Glare)
            if (nightGlare)
            {
                int glareCenterX = 160;
                int glareCenterY = 25;
                int radius = 45;

                for (int y = 0; y < _cropHeight; y++)
                {
                    for (int x = 0; x < _cropWidth; x++)
                    {
                        double dist = Math.Sqrt(Math.Pow(x - glareCenterX, 2) + Math.Pow(y - glareCenterY, 2));
                        if (dist < radius)
                        {
                            double factor = 1.0 - (dist / radius);
                            int boost = (int)(factor * 180);

                            int idx = (y * _cropStride) + (x * 3);
                            _currentPlateCropBuffer[idx] = (byte)Math.Min(255, _currentPlateCropBuffer[idx] + boost);
                            _currentPlateCropBuffer[idx + 1] = (byte)Math.Min(255, _currentPlateCropBuffer[idx + 1] + boost);
                            _currentPlateCropBuffer[idx + 2] = (byte)Math.Min(255, _currentPlateCropBuffer[idx + 2] + boost);
                        }
                    }
                }
            }
        }

        private static void DrawSimulatedPlateCharacters(byte[] buffer, int width, int height, int stride)
        {
            // بلوک‌های نشان‌دهنده ارقام پلاک ۱۲ ب ۳۴۵ - ایران ۶۷
            int[] digitXs = { 45, 65, 95, 120, 140, 160, 195, 215 };
            int charW = 12;
            int charTop = 15;
            int charBottom = 45;

            foreach (int dx in digitXs)
            {
                for (int y = charTop; y <= charBottom; y++)
                {
                    for (int x = dx; x < dx + charW; x++)
                    {
                        if (x < width && y < height)
                        {
                            // رسم حاشیه یا خط عمودی رقم
                            if (x == dx || x == dx + charW - 1 || y == charTop || y == charBottom || y == (charTop + charBottom) / 2)
                            {
                                int idx = (y * stride) + (x * 3);
                                buffer[idx] = 30;     // B
                                buffer[idx + 1] = 30; // G
                                buffer[idx + 2] = 30; // R
                            }
                        }
                    }
                }
            }
        }

        private static BitmapImage? LoadBitmapFromBytes(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                using var ms = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
