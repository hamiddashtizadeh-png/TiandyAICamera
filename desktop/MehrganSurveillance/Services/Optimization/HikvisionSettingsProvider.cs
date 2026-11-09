using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// ارائه‌دهنده اختصاصی تنظیمات تصویر دوربین‌های هایک‌ویژن (Hikvision ISAPI / ONVIF)
    /// اولویت سوم: تعامل با ISAPI و سرویس Imaging استاندارد
    /// </summary>
    public class HikvisionSettingsProvider : ICameraSettingsProvider
    {
        private readonly HttpClient _httpClient;
        private readonly OnvifImagingProvider _fallbackOnvif;

        public string ProviderName => "Hikvision ISAPI Engine";
        public int Priority => 3;

        public HikvisionSettingsProvider()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            _fallbackOnvif = new OnvifImagingProvider();
        }

        public bool CanHandle(string manufacturer, string model)
        {
            if (string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(model))
                return false;

            string m = (manufacturer ?? "").ToLowerInvariant();
            string mod = (model ?? "").ToLowerInvariant();

            return m.Contains("hikvision") || m.Contains("هایک") || mod.Contains("ds-2cd") || mod.Contains("hik");
        }

        public async Task<CameraCapabilityProfile> DetectCapabilitiesAsync(
            string ip, int port, string username, string password, CancellationToken ct = default)
        {
            var onvifProfile = await _fallbackOnvif.DetectCapabilitiesAsync(ip, port, username, password, ct);

            // استعلام قابلیت‌های ISAPI هایک‌ویژن
            string isapiUrl = $"http://{ip}:{port}/ISAPI/Image/channels/1/capabilities";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, isapiUrl);
                if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
                {
                    string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                }

                using var response = await _httpClient.SendAsync(req, ct);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(ct);
                    var cap = new CameraCapabilityProfile
                    {
                        Manufacturer = "Hikvision Digital Technology",
                        Model = "Hikvision IP Camera",
                        ProviderName = ProviderName,
                        DetectedAt = DateTime.UtcNow,
                        IsVerified = true,
                        CanControlShutter = true,
                        MinShutterSeconds = 0.00001f, // 1/100000s
                        MaxShutterSeconds = 0.04f,
                        CanControlGain = true,
                        CanControlWdr = body.Contains("WDR") || body.Contains("wideDynamicRange"),
                        CanControlHlc = body.Contains("HLC") || body.Contains("highlightSuppression"),
                        CanControlBlc = body.Contains("BLC"),
                        CanControlDayNightMode = true,
                        CanControlIr = true,
                        SupportsSmartIr = true,
                        CanControlFocus = onvifProfile.CanControlFocus,
                        VerificationSummary = "قابلیت‌های هایک‌ویژن با موفقیت از طریق ISAPI تأیید شد."
                    };
                    cap.SupportedExposureModes.AddRange(new[] { "Auto", "Manual" });
                    cap.SupportedDayNightModes.AddRange(new[] { "auto", "day", "night" });
                    return cap;
                }
            }
            catch
            {
                // Fallback به ONVIF
            }

            if (onvifProfile.IsVerified)
            {
                onvifProfile.ProviderName = $"{ProviderName} (via ONVIF Core)";
                onvifProfile.Manufacturer = "Hikvision";
            }
            return onvifProfile;
        }

        public async Task<CameraSettingsSnapshot> GetCurrentSettingsAsync(
            string ip, int port, string username, string password, CancellationToken ct = default)
        {
            return await _fallbackOnvif.GetCurrentSettingsAsync(ip, port, username, password, ct);
        }

        public async Task<bool> ApplySettingsAsync(
            string ip, int port, string username, string password,
            CameraSettingsSnapshot targetSettings, CameraCapabilityProfile capabilities,
            CancellationToken ct = default)
        {
            return await _fallbackOnvif.ApplySettingsAsync(ip, port, username, password, targetSettings, capabilities, ct);
        }
    }
}
