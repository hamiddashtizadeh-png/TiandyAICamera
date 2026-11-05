using System;
using System.Text.RegularExpressions;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// مدل داده‌ای کانال یا اسلات دوربین در چیدمان چنددوربینه
    /// </summary>
    public class CameraChannel
    {
        public int SlotIndex { get; set; } // اندیس خانه از 0 تا 8
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = "دوربین بدون نام";
        public string IpAddress { get; set; } = string.Empty;
        public int RtspPort { get; set; } = 554;
        public string Brand { get; set; } = "Tiandy";

        /// <summary>
        /// آدرس استریم اصلی با کیفیت و رزولوشن بالا (مناسب حالت تمام‌صفحه و تک‌دوربینه)
        /// فرمت مستند تیاندی: rtsp://user:pass@ip:554/1/1
        /// </summary>
        public string MainStreamUrl { get; set; } = string.Empty;

        /// <summary>
        /// آدرس استریم فرعی با بیت‌ریت سبک (الزامی برای نماهای ۴ و ۹ تایی جهت جلوگیری از اشباع پردازنده و گرافیک)
        /// فرمت مستند تیاندی: rtsp://user:pass@ip:554/1/2
        /// </summary>
        public string SubStreamUrl { get; set; } = string.Empty;

        public bool IsAssigned => !string.IsNullOrWhiteSpace(MainStreamUrl) || !string.IsNullOrWhiteSpace(SubStreamUrl);
        public bool IsConnected { get; set; }
        public string CurrentStatus { get; set; } = "آماده اتصال";
        public string StatusColorHex { get; set; } = "#64748B"; // خاکستری

        // تنظیمات هوش مصنوعی و آرشیو مختص هر کانال
        public bool IsFaceDetectionEnabled { get; set; } = true;
        public bool IsFaceArchiveEnabled { get; set; } = true;
        public bool IsAnprEnabled { get; set; } = true;

        /// <summary>
        /// دریافت آدرس موثر استریم با در نظر گرفتن نوع تقاضا (فرعی یا اصلی) و منطق تبدیل خودکار تیاندی
        /// </summary>
        public string GetEffectiveStreamUrl(bool useSubStream)
        {
            if (useSubStream)
            {
                if (!string.IsNullOrWhiteSpace(SubStreamUrl))
                    return SubStreamUrl;

                if (!string.IsNullOrWhiteSpace(MainStreamUrl))
                    return DeriveTiandySubStream(MainStreamUrl);

                return string.Empty;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(MainStreamUrl))
                    return MainStreamUrl;

                if (!string.IsNullOrWhiteSpace(SubStreamUrl))
                    return DeriveTiandyMainStream(SubStreamUrl);

                return string.Empty;
            }
        }

        private static string DeriveTiandySubStream(string mainUrl)
        {
            if (string.IsNullOrWhiteSpace(mainUrl)) return string.Empty;
            // اگر آدرس با /1/1 پایان می‌یابد به /1/2 تبدیل می‌گردد
            if (mainUrl.EndsWith("/1/1"))
                return mainUrl.Substring(0, mainUrl.Length - 4) + "/1/2";

            return Regex.Replace(mainUrl, @"/1$", "/2");
        }

        private static string DeriveTiandyMainStream(string subUrl)
        {
            if (string.IsNullOrWhiteSpace(subUrl)) return string.Empty;
            if (subUrl.EndsWith("/1/2"))
                return subUrl.Substring(0, subUrl.Length - 4) + "/1/1";

            return Regex.Replace(subUrl, @"/2$", "/1");
        }
    }
}
