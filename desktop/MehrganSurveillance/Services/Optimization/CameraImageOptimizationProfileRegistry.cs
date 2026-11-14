using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// رجیستری پایدار پروفایل‌های بهینه‌سازی پردازش تصویر به تفکیک دوربین
    /// ذخیره اطلاعات در مسیر محلی کاربر در قالب JSON بدون هیچ‌گونه تداخل با سخت‌افزار دوربین
    /// </summary>
    public class CameraImageOptimizationProfileRegistry
    {
        private static readonly object FileLock = new();
        private readonly string _storageFilePath;
        private readonly ConcurrentDictionary<string, CameraImageOptimizationProfile> _cache = new(StringComparer.OrdinalIgnoreCase);

        public CameraImageOptimizationProfileRegistry()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "MehrganSurveillance", "ImageOptimization");
            Directory.CreateDirectory(dir);
            _storageFilePath = Path.Combine(dir, "camera_image_profiles.json");

            LoadProfiles();
        }

        /// <summary>
        /// دریافت پروفایل تنظیمات پردازش تصویر برای یک دوربین مشخص
        /// در صورت عدم وجود، یک پروفایل پیش‌فرض هوشمند شبانه ایجاد می‌گردد
        /// </summary>
        public CameraImageOptimizationProfile GetProfileForCamera(string cameraId, string cameraName = "")
        {
            if (string.IsNullOrWhiteSpace(cameraId)) cameraId = "default";

            if (_cache.TryGetValue(cameraId, out var profile))
            {
                return profile.Clone();
            }

            var defaultProfile = CameraImageOptimizationProfile.CreatePreset(
                cameraId,
                !string.IsNullOrWhiteSpace(cameraName) ? cameraName : $"دوربین {cameraId}",
                ImageOptimizationPreset.Night);

            _cache[cameraId] = defaultProfile;
            SaveAll();

            return defaultProfile.Clone();
        }

        /// <summary>
        /// ذخیره یا به‌روزرسانی پروفایل تنظیمات اختصاصی دوربین
        /// </summary>
        public bool SaveProfileForCamera(CameraImageOptimizationProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CameraId)) return false;

            profile.UpdatedAtUtc = DateTime.UtcNow;
            _cache[profile.CameraId] = profile.Clone();

            return SaveAll();
        }

        /// <summary>
        /// ذخیره پروفایل با تعیین صریح شناسه دوربین
        /// </summary>
        public bool SaveProfile(string cameraId, CameraImageOptimizationProfile profile)
        {
            if (profile == null) return false;
            profile.CameraId = cameraId;
            return SaveProfileForCamera(profile);
        }

        /// <summary>
        /// بازگردانی تنظیمات دوربین به پریست پیش‌فرض کارخانه‌ای
        /// </summary>
        public CameraImageOptimizationProfile ResetProfile(string cameraId, string cameraName, ImageOptimizationPreset preset)
        {
            var p = CameraImageOptimizationProfile.CreatePreset(cameraId, cameraName, preset);
            _cache[cameraId] = p;
            SaveAll();
            return p.Clone();
        }

        /// <summary>
        /// دریافت لیست تمام پروفایل‌های ذخیره‌شده
        /// </summary>
        public List<CameraImageOptimizationProfile> GetAllProfiles()
        {
            return _cache.Values.Select(p => p.Clone()).ToList();
        }

        private void LoadProfiles()
        {
            lock (FileLock)
            {
                if (!File.Exists(_storageFilePath)) return;

                try
                {
                    string json = File.ReadAllText(_storageFilePath);
                    var list = JsonSerializer.Deserialize<List<CameraImageOptimizationProfile>>(json);
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            if (!string.IsNullOrEmpty(item.CameraId))
                            {
                                _cache[item.CameraId] = item;
                            }
                        }
                    }
                }
                catch
                {
                    // حفظ حافظه جاری در صورت خطای فایل
                }
            }
        }

        private bool SaveAll()
        {
            lock (FileLock)
            {
                try
                {
                    var list = _cache.Values.ToList();
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(list, options);
                    File.WriteAllText(_storageFilePath, json);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
