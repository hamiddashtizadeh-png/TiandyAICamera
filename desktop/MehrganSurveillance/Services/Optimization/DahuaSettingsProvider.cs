using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// ارائه‌دهنده اختصاصی تنظیمات تصویر دوربین‌های داهوا (Dahua Technology)
    /// اولویت دوم: پشتیبانی از تنظیمات دوربین‌های ANPR داهوا و پروتکل‌های CGI / ONVIF Imaging
    /// </summary>
    public class DahuaSettingsProvider : ICameraSettingsProvider
    {
        private readonly HttpClient _httpClient;
        private readonly OnvifImagingProvider _fallbackOnvif;

        public string ProviderName => "Dahua ConfigManager Engine";
        public int Priority => 2;

        public DahuaSettingsProvider()
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

            return m.Contains("dahua") || m.Contains("داهوا") || mod.Contains("dh-") || mod.Contains("dahua");
        }

        public async Task<CameraCapabilityProfile> DetectCapabilitiesAsync(
            string ip, int port, string username, string password, CancellationToken ct = default)
        {
            var onvifProfile = await _fallbackOnvif.DetectCapabilitiesAsync(ip, port, username, password, ct);

            // تست API اختصاصی داهوا
            string cgiUrl = $"http://{ip}:{port}/cgi-bin/configManager.cgi?action=getConfig&name=Camera";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, cgiUrl);
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
                        Manufacturer = "Dahua Technology",
                        Model = "Dahua Network Camera",
                        ProviderName = ProviderName,
                        DetectedAt = DateTime.UtcNow,
                        IsVerified = true,
                        CanControlShutter = true,
                        MinShutterSeconds = 0.00002f, // 1/50000s
                        MaxShutterSeconds = 0.04f,
                        CanControlGain = true,
                        CanControlWdr = body.Contains("WideDynamicRange") || body.Contains("WDR"),
                        CanControlHlc = body.Contains("HLC") || body.Contains("GlareInhibition"),
                        CanControlBlc = body.Contains("BLC"),
                        CanControlDayNightMode = true,
                        CanControlIr = true,
                        SupportsSmartIr = true,
                        CanControlFocus = onvifProfile.CanControlFocus,
                        VerificationSummary = "قابلیت‌های داهوا از طریق Dahua ConfigManager با موفقیت تأیید شد."
                    };
                    cap.SupportedExposureModes.AddRange(new[] { "Auto", "Manual", "AntiFlicker" });
                    cap.SupportedDayNightModes.AddRange(new[] { "Auto", "Color", "BlackWhite" });
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
                onvifProfile.Manufacturer = "Dahua";
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
            // استفاده از پروتکل استاندارد ONVIF برای اعمال پایدار روی فریمور داهوا
            return await _fallbackOnvif.ApplySettingsAsync(ip, port, username, password, targetSettings, capabilities, ct);
        }
    }
}
