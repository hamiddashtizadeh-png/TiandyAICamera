using System.Collections.Generic;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// نمایانگر دستگاه یا دوربین کشف‌شده در شبکه محلی
    /// </summary>
    public class DiscoveredCamera
    {
        public string Id { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int HttpPort { get; set; } = 80;
        public int RtspPort { get; set; } = 554;
        public string Brand { get; set; } = "شناسایی‌نشده";
        public string Model { get; set; } = "نامشخص";
        public string FirmwareVersion { get; set; } = "نامشخص";
        public string SerialNumber { get; set; } = "نامشخص";
        public string OnvifServiceUrl { get; set; } = string.Empty;
        public bool IsOnvifSupported { get; set; } = true;
        public string DiscoverySource { get; set; } = "WS-Discovery"; // "WS-Discovery" یا "SubnetScan" یا "Manual"

        public DeviceDiscoveryState State { get; set; } = DeviceDiscoveryState.Discovered;
        public string StateMessage { get; set; } = "کشف شد - آماده دریافت اطلاعات احراز هویت";

        public string Username { get; set; } = "admin";
        // کلمه عبور فقط در طول نشست کشف در حافظه زنده نگه‌داری می‌شود و هیچ‌گاه لاگ نمی‌گردد
        public string Password { get; set; } = string.Empty;

        public string MainStreamUri { get; set; } = string.Empty;
        public string SubStreamUri { get; set; } = string.Empty;

        public List<CameraStreamProfile> Profiles { get; set; } = new();

        public string DisplayTitle => $"{Brand} ({IpAddress}) - {Model}";

        public string StatusColorHex => State switch
        {
            DeviceDiscoveryState.StreamAvailable => "#16A34A",       // سبز
            DeviceDiscoveryState.Authenticated => "#0D9488",         // فیروزه‌ای
            DeviceDiscoveryState.Authenticating => "#2563EB",        // آبی
            DeviceDiscoveryState.AuthenticationRequired => "#D97706", // کهربایی/نارنجی
            DeviceDiscoveryState.ConnectionFailed => "#DC2626",      // قرمز
            _ => "#64748B"                                           // خاکستری (کشف اولیه)
        };

        public string StatePersianText => State switch
        {
            DeviceDiscoveryState.Discovered => "کشف شد",
            DeviceDiscoveryState.AuthenticationRequired => "نیاز به اعتبارسنجی",
            DeviceDiscoveryState.Authenticating => "در حال ارتباط...",
            DeviceDiscoveryState.Authenticated => "احراز هویت شد",
            DeviceDiscoveryState.StreamAvailable => "استریم تایید شد",
            DeviceDiscoveryState.ConnectionFailed => "خطا در اتصال",
            _ => "نامشخص"
        };
    }
}
