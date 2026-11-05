using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance;

public partial class FaceArchiveWindow : Window
{
    private readonly FaceArchiveService? _archiveService;
    private readonly FaceDetectionPipeline? _facePipeline;
    private List<FaceEventRecord> _currentEvents = new();

    public FaceArchiveWindow(FaceArchiveService? archiveService = null, FaceDetectionPipeline? facePipeline = null)
    {
        InitializeComponent();
        _archiveService = archiveService;
        _facePipeline = facePipeline;

        Loaded += FaceArchiveWindow_Loaded;
    }

    private async void FaceArchiveWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_facePipeline != null)
        {
            FaceDetectionToggle.IsChecked = _facePipeline.Config.IsEnabled;
        }

        if (_archiveService != null)
        {
            FaceArchiveToggle.IsChecked = _archiveService.Config.IsEnabled;
            await _archiveService.InitializeAsync();
        }

        UpdateDiskUsageDisplay();
        await LoadEventsAsync();
    }

    private void UpdateDiskUsageDisplay()
    {
        try
        {
            if (_archiveService != null)
            {
                string root = _archiveService.Config.ArchiveRootPath;
                if (Directory.Exists(root))
                {
                    long totalBytes = 0;
                    foreach (var file in Directory.EnumerateFiles(root, "*.enc", SearchOption.AllDirectories))
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    double mb = Math.Round(totalBytes / (1024.0 * 1024.0), 2);
                    DiskUsageText.Text = $"{mb} مگابایت";
                }
                else
                {
                    DiskUsageText.Text = "۰ مگابایت";
                }

                TotalArchivedBadge.Text = $"{_archiveService.TotalCropsArchived} تصویر ثبت‌شده";
            }
        }
        catch (Exception ex)
        {
            DiskUsageText.Text = "خطا در استعلام";
            StatusMessageText.Text = $"خطا در استعلام دیسک: {ex.Message}";
        }
    }

    private async Task LoadEventsAsync()
    {
        if (_archiveService == null) return;

        try
        {
            var filter = new FaceArchiveFilter
            {
                FromUtc = FromDatePicker.SelectedDate?.ToUniversalTime(),
                ToUtc = ToDatePicker.SelectedDate?.ToUniversalTime().AddDays(1),
                CameraId = CameraFilterCombo.SelectedItem as string,
                PageSize = 100,
                PageIndex = 0
            };

            _currentEvents = await _archiveService.QueryEventsAsync(filter);
            EventsDataGrid.ItemsSource = _currentEvents;
            TotalArchivedBadge.Text = $"{_currentEvents.Count} رویداد ثبت‌شده";
        }
        catch (Exception ex)
        {
            StatusMessageText.Text = $"خطا در بارگذاری رویدادها: {ex.Message}";
        }
    }

    private void FaceDetectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_facePipeline == null) return;
        bool isEnabled = FaceDetectionToggle.IsChecked == true;

        if (isEnabled)
        {
            _facePipeline.EnablePipeline();
        }
        else
        {
            _facePipeline.DisablePipeline();
            // غیرفعال‌سازی خودکار آرشیو در صورت خاموش شدن تشخیص چهره
            if (_archiveService != null)
            {
                _archiveService.Config.IsEnabled = false;
                FaceArchiveToggle.IsChecked = false;
            }
        }
    }

    private void FaceArchiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveService == null) return;
        bool isEnabled = FaceArchiveToggle.IsChecked == true;

        if (isEnabled && _facePipeline != null && !_facePipeline.Config.IsEnabled)
        {
            MessageBox.Show("برای فعال‌سازی آرشیو، ابتدا باید خط لوله تشخیص چهره فعال باشد.", "کنترل امنیتی", MessageBoxButton.OK, MessageBoxImage.Warning);
            FaceArchiveToggle.IsChecked = false;
            return;
        }

        _archiveService.Config.IsEnabled = isEnabled;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateDiskUsageDisplay();
        await LoadEventsAsync();
    }

    private async void ApplyFilter_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync();
    }

    private void EventsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventsDataGrid.SelectedItem is FaceEventRecord selectedEvent)
        {
            CropsItemsControl.ItemsSource = selectedEvent.Crops;
        }
    }

    private async void SingleCropDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FaceCropRecord crop)
        {
            var confirm = MessageBox.Show($"آیا از حذف دائمی برش شماره {crop.CropIndex} اطمینان دارید؟", "تأیید حذف", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (_archiveService != null)
            {
                bool deleted = await _archiveService.DeleteCropAsync(crop.CropId, crop.EventId, crop.EncryptedFilePath);
                if (deleted)
                {
                    StatusMessageText.Text = "برش چهره با موفقیت حذف گردید.";
                    await LoadEventsAsync();
                    UpdateDiskUsageDisplay();
                }
                else
                {
                    MessageBox.Show("خطا در حذف برش چهره.", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void BulkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEvents.Count == 0) return;

        var confirm = MessageBox.Show($"آیا از حذف گروهی {_currentEvents.Count} رویداد فیلترشده اطمینان دارید؟", "تأیید حذف گروهی", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (_archiveService != null)
        {
            int deletedCount = 0;
            foreach (var ev in _currentEvents)
            {
                foreach (var crop in ev.Crops)
                {
                    if (await _archiveService.DeleteCropAsync(crop.CropId, crop.EventId, crop.EncryptedFilePath))
                    {
                        deletedCount++;
                    }
                }
            }

            StatusMessageText.Text = $"{deletedCount} برش چهره به طور کامل حذف شدند.";
            await LoadEventsAsync();
            UpdateDiskUsageDisplay();
        }
    }
}
