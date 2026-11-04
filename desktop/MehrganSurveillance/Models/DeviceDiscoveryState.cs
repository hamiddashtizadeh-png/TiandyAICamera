namespace MehrganSurveillance.Models
{
    /// <summary>
    /// وضعیت‌های چرخه حیات شناسایی و اعتبارسنجی دوربین در شبکه محلی
    /// </summary>
    public enum DeviceDiscoveryState
    {
        /// <summary>
        /// شناسایی اولیه در شبکه (از طریق WS-Discovery یا اسکن پورت)
        /// </summary>
        Discovered,

        /// <summary>
        /// نیازمند دریافت نام کاربری و رمز عبور
        /// </summary>
        AuthenticationRequired,

        /// <summary>
        /// در حال ارسال درخواست احراز هویت و دریافت پروفایل‌ها
        /// </summary>
        Authenticating,

        /// <summary>
        /// احراز هویت موفق با پروتکل ONVIF یا RTSP
        /// </summary>
        Authenticated,

        /// <summary>
        /// استریم فعال و آماده پخش شناسایی شد
        /// </summary>
        StreamAvailable,

        /// <summary>
        /// بروز خطا در اتصال، احراز هویت یا تایم‌اوت شبکه
        /// </summary>
        ConnectionFailed
    }
}
