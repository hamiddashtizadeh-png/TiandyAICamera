using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MehrganSurveillance.Services
{
    /// <summary>
    /// سرویس محافظت و رمزنگاری داده‌های حساس و کلمات عبور با Windows DPAPI
    /// تضمین می‌نماید که هیچ پسوردی به شکل متن ساده (Plain-text) در دیسک یا لاگ‌ها ذخیره یا چاپ نشود.
    /// </summary>
    public static class CredentialProtectionService
    {
        // آنتروپی اضافی اختصاصی برای سخت‌تر کردن دسترسی خارج از کانتکست برنامه
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MehrganAI_Surveillance_DPAPI_Entropy_2026");

        /// <summary>
        /// رمزنگاری کلمه عبور با DPAPI در سطح کاربر جاری ویندوز (DataProtectionScope.CurrentUser)
        /// </summary>
        public static string EncryptSecret(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
            catch (Exception)
            {
                // در صورت بروز خطا در محیط‌های غیر از ویندوز یا عدم دسترسی، هیچ متن خام پسوردی افشا نمی‌شود
                return string.Empty;
            }
        }

        /// <summary>
        /// رمزگشایی کلمه عبور با DPAPI در سطح کاربر جاری ویندوز
        /// </summary>
        public static string DecryptSecret(string cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64))
                return string.Empty;

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// ماسک‌سازی کلمه عبور در آدرس‌های RTSP یا متن‌ها برای نمایش امن در رابط کاربری و لاگ‌ها
        /// مثال: rtsp://admin:123456@192.168.1.168:554/1/1 -> rtsp://admin:••••••••@192.168.1.168:554/1/1
        /// </summary>
        public static string MaskRtspUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            return Regex.Replace(url, @"(:)([^:@/]+)(@)", "$1••••••••$3");
        }
    }
}
