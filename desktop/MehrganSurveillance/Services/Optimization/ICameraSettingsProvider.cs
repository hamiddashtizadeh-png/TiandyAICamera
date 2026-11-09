using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// اینترفیس ارائه‌دهنده تنظیمات و اعمال پارامترهای تصویرسازی دوربین
    /// </summary>
    public interface ICameraSettingsProvider
    {
        /// <summary>
        /// نام ارائه‌دهنده (مثلا Tiandy Native, Dahua RPC, Hikvision ISAPI, ONVIF Imaging)
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// اولویت ارائه‌دهنده (ارائه‌دهنده‌های بومی اولویت بالاتر دارند)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// بررسی اینکه آیا این ارائه‌دهنده توانایی پشتیبانی از این دوربین و برند را دارد
        /// </summary>
        bool CanHandle(string manufacturer, string model);

        /// <summary>
        /// شناسایی قابلیت‌های واقعی دوربین
        /// هیچ قابلیتی بدون تست واقعی نباید مثبت برگردانده شود.
        /// </summary>
        Task<CameraCapabilityProfile> DetectCapabilitiesAsync(
            string ip, int port, string username, string password, CancellationToken ct = default);

        /// <summary>
        /// دریافت اسنپ‌شات از تنظیمات فعلی تصویر دوربین
        /// </summary>
        Task<CameraSettingsSnapshot> GetCurrentSettingsAsync(
            string ip, int port, string username, string password, CancellationToken ct = default);

        /// <summary>
        /// اعمال تنظیمات مشخص بر روی دوربین
        /// </summary>
        Task<bool> ApplySettingsAsync(
            string ip, int port, string username, string password,
            CameraSettingsSnapshot targetSettings, CameraCapabilityProfile capabilities,
            CancellationToken ct = default);
    }
}
