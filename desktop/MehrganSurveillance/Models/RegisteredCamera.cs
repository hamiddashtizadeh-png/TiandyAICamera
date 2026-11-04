using System;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// مدل ثبت دوربین در رجیستری پایدار سامانه مهرگان با رمزنگاری DPAPI
    /// </summary>
    public class RegisteredCamera
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "دوربین جدید";
        public string IpAddress { get; set; } = string.Empty;
        public int RtspPort { get; set; } = 554;
        public string Brand { get; set; } = "Generic";
        public string Model { get; set; } = string.Empty;
        public string Username { get; set; } = "admin";

        /// <summary>
        /// کلمه عبور محافظت‌شده با استفاده از Windows Data Protection API (DPAPI)
        /// هرگز به صورت متن خام در دیسک ذخیره نمی‌گردد.
        /// </summary>
        public string EncryptedPassword { get; set; } = string.Empty;

        public string MainStreamRtspUri { get; set; } = string.Empty;
        public string SubStreamRtspUri { get; set; } = string.Empty;
        public string PreferredStreamType { get; set; } = "Main"; // "Main" یا "Sub"

        public DateTime RegisteredAt { get; set; } = DateTime.Now;

        public string DisplaySummary => $"{Name} [{Brand}] ({IpAddress})";
    }
}
