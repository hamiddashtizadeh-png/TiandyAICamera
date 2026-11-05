using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance;

/// <summary>
/// پنجره اختصاصی پلاک‌خوان هوشمند خودرو (ANPR) با پشتیبانی از استانداردهای پلاک ملی ایران
/// </summary>
public partial class AnprWindow : Window
{
    private readonly AnprPipeline _pipeline;
    private readonly AnprArchiveService _archiveService;
    private readonly List<AnprResult> _loadedEvents = new();

    public AnprWindow(AnprPipeline? pipeline = null, AnprArchiveService? archiveService = null)
    {
        InitializeComponent();

        _pipeline = pipeline ?? new AnprPipeline();
        _archiveService = archiveService ?? new AnprArchiveService();

        Loaded += AnprWindow_Loaded;
        Closed += AnprWindow_Closed;
    }

    private async void AnprWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _archiveService.InitializeAsync();
            _pipeline.PlateRecognized += OnLivePlateRecognized;

            AnprEnableToggle.IsChecked = _pipeline.Config.IsEnabled;
            UpdateEngineBadge();

            // پر کردن دوربین‌های پیش‌فرض
            CameraFilterCombo.Items.Clear();
            CameraFilterCombo.Items.Add("همه دوربین‌ها");
            CameraFilterCombo.SelectedIndex = 0;

            await LoadEventsAsync();
            await UpdateStatisticsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در راه‌اندازی ماژول پلاک‌خوان: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AnprWindow_Closed(object? sender, EventArgs e)
    {
        _pipeline.PlateRecognized -= OnLivePlateRecognized;
    }

    private void OnLivePlateRecognized(AnprResult result)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(120);
            await LoadEventsAsync(PlateSearchBox.Text);
            await UpdateStatisticsAsync();
        });
    }

    private void UpdateEngineBadge()
    {
        if (_pipeline.ModelStatus.IsReady)
        {
            AiEngineStatusBadge.Text = $"موتور فعال ONNX ({_pipeline.ModelStatus.ExecutionProvider})";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else if (_pipeline.IsInitialized)
        {
            AiEngineStatusBadge.Text = $"موتور پلاک‌خوان فعال ({_pipeline.ModelStatus.ExecutionProvider} / تحلیل کادر و کاراکتر)";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }
        else
        {
            AiEngineStatusBadge.Text = "در انتظار راه‌اندازی موتور";
            AiEngineStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }
    }

    private async Task LoadEventsAsync(string? query = null)
    {
        try
        {
            DateTime? from = FromDatePicker.SelectedDate?.ToUniversalTime();
            DateTime? to = ToDatePicker.SelectedDate?.ToUniversalTime().AddDays(1);

            string? cam = CameraFilterCombo.SelectedIndex > 0 ? CameraFilterCombo.SelectedItem?.ToString() : null;

            AnprValidationStatus? status = null;
            if (StatusFilterCombo.SelectedIndex == 1) status = AnprValidationStatus.Verified;
            else if (StatusFilterCombo.SelectedIndex == 2) status = AnprValidationStatus.Partial;
            else if (StatusFilterCombo.SelectedIndex == 3) status = AnprValidationStatus.Uncertain;

            var events = await _archiveService.SearchAsync(query, cam, from, to, status, limit: 150);

            _loadedEvents.Clear();
            _loadedEvents.AddRange(events);

            AnprEventsGrid.ItemsSource = null;
            AnprEventsGrid.ItemsSource = _loadedEvents;

            if (_loadedEvents.Count > 0)
            {
                AnprEventsGrid.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در بازیابی رویدادها: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task UpdateStatisticsAsync()
    {
        try
        {
            var (total, verified, today, avgConf) = await _archiveService.GetStatisticsAsync();
            TotalEventsText.Text = IranianPlateNormalizer.ToPersianDigits(total.ToString("N0"));
            VerifiedEventsText.Text = IranianPlateNormalizer.ToPersianDigits(verified.ToString("N0"));
            TodayEventsText.Text = IranianPlateNormalizer.ToPersianDigits(today.ToString("N0"));
            AvgConfidenceText.Text = IranianPlateNormalizer.ToPersianDigits($"{avgConf * 100:F0}%");
        }
        catch { }
    }

    private void AnprEnableToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enable = AnprEnableToggle.IsChecked == true;
        if (enable)
        {
            _pipeline.EnablePipeline();
        }
        else
        {
            _pipeline.DisablePipeline();
        }
    }

    private void AutoArchiveToggle_Click(object sender, RoutedEventArgs e)
    {
        // وضعیت ثبت خودکار
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync(PlateSearchBox.Text);
        await UpdateStatisticsAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync(PlateSearchBox.Text);
    }

    private async void PlateSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LoadEventsAsync(PlateSearchBox.Text);
        }
    }

    private void QuickLetter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string letter)
        {
            PlateSearchBox.Text += letter;
            PlateSearchBox.Focus();
            PlateSearchBox.CaretIndex = PlateSearchBox.Text.Length;
        }
    }

    private void AnprEventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnprEventsGrid.SelectedItem is not AnprResult item) return;

        // به‌روزرسانی پلاک گرافیکی
        var plate = item.Plate;
        PlatePart1Text.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.Part1) ? "--" : plate.Part1);
        PlateLetterText.Text = string.IsNullOrEmpty(plate.LetterText) ? "؟" : plate.LetterText;
        PlatePart2Text.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.Part2) ? "---" : plate.Part2);
        PlateIranCodeText.Text = IranianPlateNormalizer.ToPersianDigits(string.IsNullOrEmpty(plate.IranCode) ? "--" : plate.IranCode);

        // رنگ‌بندی کارت پلاک بر اساس نوع
        ApplyPlateColorTheme(plate.PlateType);

        // مقادیر متادیتا
        RawOcrValue.Text = string.IsNullOrWhiteSpace(plate.RawOcr) ? "ندارد" : plate.RawOcr;
        EventIdValue.Text = item.EventId.ToString("D");
        ModelInfoValue.Text = item.ModelInfo;
        LatencyValue.Text = $"{item.InferenceDurationMs:F1} میلی‌ثانیه";
        EpValue.Text = item.ExecutionProvider;

        // بارگذاری تصویر در صورت وجود فایل محلی
        LoadImagePreview(PlateCropImage, item.EncryptedPlateCropPath);
        LoadImagePreview(VehicleCropImage, item.EncryptedVehicleCropPath);
    }

    private void ApplyPlateColorTheme(IranianPlateType plateType)
    {
        switch (plateType)
        {
            case IranianPlateType.Taxi:
            case IranianPlateType.Public:
                // زرد با فونت مشکی
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                SetPlateTextColors(Color.FromRgb(15, 23, 42));
                break;

            case IranianPlateType.Government:
                // قرمز دولتی با فونت سفید
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                SetPlateTextColors(Colors.White);
                break;

            case IranianPlateType.Police:
                // سبز پلیس با فونت سفید
                PlateVisualCard.Background = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                SetPlateTextColors(Colors.White);
                break;

            default:
                // پلاک سفید شخصی استاندارد
                PlateVisualCard.Background = Brushes.White;
                SetPlateTextColors(Color.FromRgb(15, 23, 42));
                break;
        }
    }

    private void SetPlateTextColors(Color color)
    {
        var brush = new SolidColorBrush(color);
        PlatePart1Text.Foreground = brush;
        PlateLetterText.Foreground = brush;
        PlatePart2Text.Foreground = brush;
        PlateIranCodeText.Foreground = brush;
    }

    private void LoadImagePreview(Image imageControl, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            imageControl.Source = null;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            imageControl.Source = bmp;
        }
        catch
        {
            imageControl.Source = null;
        }
    }

    private async void BulkDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AnprEventsGrid.SelectedItems.OfType<AnprResult>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("هیچ رویدادی برای حذف انتخاب نشده است.", "توجه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"آیا از حذف دائم {IranianPlateNormalizer.ToPersianDigits(selected.Count.ToString())} رویداد پلاک‌خوانی از دیتابیس محلی اطمینان دارید؟",
            "تایید حذف",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                int deleted = await _archiveService.DeleteEventsAsync(selected.Select(s => s.EventId));
                MessageBox.Show($"{IranianPlateNormalizer.ToPersianDigits(deleted.ToString())} رویداد با موفقیت حذف گردید.", "حذف موفق", MessageBoxButton.OK, MessageBoxImage.Information);

                await LoadEventsAsync(PlateSearchBox.Text);
                await UpdateStatisticsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در حذف رویدادها: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
