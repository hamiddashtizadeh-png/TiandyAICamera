using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services
{
    /// <summary>
    /// سرویس رجیستری پایدار دوربین‌های تاییدشده در سامانه مهرگان
    /// اطلاعات در مسیر محلی کاربر ذخیره شده و پسوردها با DPAPI رمزنگاری می‌شوند.
    /// </summary>
    public class CameraRegistryService
    {
        private static readonly object FileLock = new();
        private readonly string _registryFilePath;

        public CameraRegistryService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folderPath = Path.Combine(appData, "MehrganSurveillance");
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            _registryFilePath = Path.Combine(folderPath, "cameras.json");
        }

        /// <summary>
        /// دریافت لیست تمام دوربین‌های ذخیره‌شده
        /// </summary>
        public List<RegisteredCamera> GetAllCameras()
        {
            lock (FileLock)
            {
                if (!File.Exists(_registryFilePath))
                    return new List<RegisteredCamera>();

                try
                {
                    string json = File.ReadAllText(_registryFilePath);
                    return JsonSerializer.Deserialize<List<RegisteredCamera>>(json) ?? new List<RegisteredCamera>();
                }
                catch (Exception)
                {
                    return new List<RegisteredCamera>();
                }
            }
        }

        /// <summary>
        /// افزودن یا به‌روزرسانی دوربین با جلوگیری از ثبت تکراری (Duplicate Prevention بر اساس IP و Port)
        /// </summary>
        public bool RegisterOrUpdateCamera(RegisteredCamera camera, out string message)
        {
            lock (FileLock)
            {
                try
                {
                    var list = GetAllCameras();

                    // بررسی وجود دستگاه با آدرس IP و پورت یکسان
                    var existing = list.FirstOrDefault(c =>
                        string.Equals(c.IpAddress, camera.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                        c.RtspPort == camera.RtspPort);

                    if (existing != null)
                    {
                        // به‌روزرسانی اطلاعات دوربین موجود به جای ایجاد دابلیکیت
                        existing.Name = camera.Name;
                        existing.Brand = camera.Brand;
                        existing.Model = camera.Model;
                        existing.Username = camera.Username;
                        if (!string.IsNullOrEmpty(camera.EncryptedPassword))
                        {
                            existing.EncryptedPassword = camera.EncryptedPassword;
                        }
                        existing.MainStreamRtspUri = camera.MainStreamRtspUri;
                        existing.SubStreamRtspUri = camera.SubStreamRtspUri;
                        existing.PreferredStreamType = camera.PreferredStreamType;

                        SaveAll(list);
                        message = $"اطلاعات دوربین موجود ({camera.IpAddress}) به‌روزرسانی شد.";
                        return true;
                    }

                    list.Add(camera);
                    SaveAll(list);
                    message = $"دوربین «{camera.Name}» ({camera.IpAddress}) با موفقیت به رجیستری افزوده شد.";
                    return true;
                }
                catch (Exception ex)
                {
                    message = $"خطا در ذخیره‌سازی رجیستری: {ex.Message}";
                    return false;
                }
            }
        }

        /// <summary>
        /// حذف دوربین از رجیستری بر اساس شناسه
        /// </summary>
        public bool DeleteCamera(string id)
        {
            lock (FileLock)
            {
                try
                {
                    var list = GetAllCameras();
                    int count = list.RemoveAll(c => c.Id == id);
                    if (count > 0)
                    {
                        SaveAll(list);
                        return true;
                    }
                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private void SaveAll(List<RegisteredCamera> list)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(list, options);
            File.WriteAllText(_registryFilePath, json);
        }
    }
}
