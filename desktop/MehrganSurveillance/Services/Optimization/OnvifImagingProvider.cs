using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services.Optimization
{
    /// <summary>
    /// ارائه‌دهنده عمومی تنظیمات تصویر دوربین بر پایه پروتکل استاندارد ONVIF Imaging Service
    /// قابلیت تعامل با تمام دوربین‌های استاندارد بین‌المللی با احراز هویت امن WS-Security PasswordDigest
    /// </summary>
    public class OnvifImagingProvider : ICameraSettingsProvider
    {
        private readonly HttpClient _httpClient;

        public string ProviderName => "ONVIF Imaging Service";
        public int Priority => 10; // اولویت عمومی پس از ارائه‌دهندگان بومی

        public OnvifImagingProvider()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
        }

        public bool CanHandle(string manufacturer, string model)
        {
            // ارائه‌دهنده عمومی ONVIF برای تمامی دوربین‌ها به عنوان پایه قابل استفاده است
            return true;
        }

        public async Task<CameraCapabilityProfile> DetectCapabilitiesAsync(
            string ip, int port, string username, string password, CancellationToken ct = default)
        {
            var profile = new CameraCapabilityProfile
            {
                ProviderName = ProviderName,
                DetectedAt = DateTime.UtcNow,
                IsVerified = false
            };

            string imagingUrl = $"http://{ip}:{port}/onvif/imaging_service";
            string videoSourceToken = "VideoSource_1";

            // گام ۱: استعلام آپشن‌های تصویر GetOptions
            string getOptionsSoap = BuildSoapEnvelope(
                $@"<GetOptions xmlns=""http://www.onvif.org/ver20/imaging/wsdl"">
                    <VideoSourceToken>{videoSourceToken}</VideoSourceToken>
                   </GetOptions>",
                username, password);

            try
            {
                using var response = await SendSoapAsync(imagingUrl, getOptionsSoap, ct);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(ct);
                    var doc = XDocument.Parse(content);

                    // بررسی پشتیبانی از Exposure / Shutter
                    var exposureEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Exposure");
                    if (exposureEl != null)
                    {
                        profile.CanControlShutter = true;
                        var minEl = exposureEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "MinExposureTime" || e.Name.LocalName == "Min");
                        var maxEl = exposureEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "MaxExposureTime" || e.Name.LocalName == "Max");

                        if (minEl != null && float.TryParse(minEl.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float minExp))
                        {
                            profile.MinShutterSeconds = Math.Max(0.00001f, minExp / 1000f); // میلی‌ثانیه یا میکروثانیه به ثانیه
                        }
                        if (maxEl != null && float.TryParse(maxEl.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float maxExp))
                        {
                            profile.MaxShutterSeconds = Math.Min(1.0f, maxExp / 1000f);
                        }

                        var modes = exposureEl.Descendants().Where(e => e.Name.LocalName == "Mode").Select(e => e.Value).ToList();
                        if (modes.Count > 0)
                        {
                            profile.SupportedExposureModes.AddRange(modes);
                        }
                        else
                        {
                            profile.SupportedExposureModes.AddRange(new[] { "AUTO", "MANUAL" });
                        }

                        // Gain
                        var gainEl = exposureEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "Gain");
                        if (gainEl != null)
                        {
                            profile.CanControlGain = true;
                        }
                    }

                    // بررسی WDR
                    var wdrEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "WideDynamicRange");
                    if (wdrEl != null)
                    {
                        profile.CanControlWdr = true;
                    }

                    // بررسی فیلتر روز و شب (IrCutFilter)
                    var irCutEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "IrCutFilter");
                    if (irCutEl != null)
                    {
                        profile.CanControlDayNightMode = true;
                        profile.SupportedDayNightModes.AddRange(new[] { "ON", "OFF", "AUTO" });
                    }

                    // بررسی فوکوس
                    var focusEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Focus");
                    if (focusEl != null)
                    {
                        profile.CanControlFocus = true;
                    }

                    // بررسی قابلیت HLC (در پسوندها یا اکستنشن‌های ONVIF)
                    var hlcEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.IndexOf("HLC", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hlcEl != null)
                    {
                        profile.CanControlHlc = true;
                    }

                    profile.IsVerified = true;
                    profile.VerificationSummary = "قابلیت‌های استاندارد ONVIF Imaging با موفقیت ارزیابی و تأیید شد.";
                }
                else
                {
                    profile.VerificationSummary = $"سرویس ONVIF Imaging پاسخ {response.StatusCode} داد؛ استفاده از قابلیت‌های پایه.";
                }
            }
            catch (Exception ex)
            {
                profile.VerificationSummary = $"خطا در فراخوانی ONVIF Imaging: {ex.Message}";
            }

            return profile;
        }

        public async Task<CameraSettingsSnapshot> GetCurrentSettingsAsync(
            string ip, int port, string username, string password, CancellationToken ct = default)
        {
            var snapshot = new CameraSettingsSnapshot
            {
                CameraIp = ip,
                CapturedAt = DateTime.UtcNow
            };

            string imagingUrl = $"http://{ip}:{port}/onvif/imaging_service";
            string videoSourceToken = "VideoSource_1";

            string getSettingsSoap = BuildSoapEnvelope(
                $@"<GetImagingSettings xmlns=""http://www.onvif.org/ver20/imaging/wsdl"">
                    <VideoSourceToken>{videoSourceToken}</VideoSourceToken>
                   </GetImagingSettings>",
                username, password);

            try
            {
                using var response = await SendSoapAsync(imagingUrl, getSettingsSoap, ct);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(ct);
                    var doc = XDocument.Parse(content);

                    var exposure = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Exposure");
                    if (exposure != null)
                    {
                        string mode = exposure.Descendants().FirstOrDefault(e => e.Name.LocalName == "Mode")?.Value ?? "Auto";
                        snapshot.ExposureMode = mode;

                        string expTime = exposure.Descendants().FirstOrDefault(e => e.Name.LocalName == "ExposureTime")?.Value ?? "";
                        if (float.TryParse(expTime, NumberStyles.Any, CultureInfo.InvariantCulture, out float timeVal))
                        {
                            snapshot.ShutterSpeedSeconds = timeVal > 10 ? timeVal / 1000000f : timeVal;
                        }

                        string gain = exposure.Descendants().FirstOrDefault(e => e.Name.LocalName == "Gain")?.Value ?? "";
                        if (float.TryParse(gain, NumberStyles.Any, CultureInfo.InvariantCulture, out float gVal))
                        {
                            snapshot.Gain = gVal;
                        }
                    }

                    var wdr = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "WideDynamicRange");
                    if (wdr != null)
                    {
                        string mode = wdr.Descendants().FirstOrDefault(e => e.Name.LocalName == "Mode")?.Value ?? "OFF";
                        snapshot.WdrEnabled = mode.Equals("ON", StringComparison.OrdinalIgnoreCase);

                        string level = wdr.Descendants().FirstOrDefault(e => e.Name.LocalName == "Level")?.Value ?? "";
                        if (float.TryParse(level, NumberStyles.Any, CultureInfo.InvariantCulture, out float lvlVal))
                        {
                            snapshot.WdrLevel = lvlVal;
                        }
                    }

                    var irCut = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "IrCutFilter")?.Value;
                    if (!string.IsNullOrEmpty(irCut))
                    {
                        snapshot.DayNightMode = irCut;
                    }
                }
            }
            catch
            {
                // در صورت قطعی یا شبیه‌سازی، مقادیر اولیه پایدار حفظ می‌شوند
            }

            return snapshot;
        }

        public async Task<bool> ApplySettingsAsync(
            string ip, int port, string username, string password,
            CameraSettingsSnapshot targetSettings, CameraCapabilityProfile capabilities,
            CancellationToken ct = default)
        {
            if (targetSettings == null) return false;

            string imagingUrl = $"http://{ip}:{port}/onvif/imaging_service";
            string videoSourceToken = "VideoSource_1";

            // فقط پارامترهای تاییدشده و معتبر دوربین اعمال می‌شوند
            var exposureXml = new StringBuilder();
            exposureXml.Append("<Exposure xmlns=\"http://www.onvif.org/ver10/schema\">");
            exposureXml.Append($"<Mode>{(capabilities.CanControlShutter ? targetSettings.ExposureMode.ToUpperInvariant() : "AUTO")}</Mode>");

            if (capabilities.CanControlShutter && targetSettings.ExposureMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            {
                float expTime = Math.Clamp(targetSettings.ShutterSpeedSeconds, capabilities.MinShutterSeconds, capabilities.MaxShutterSeconds);
                exposureXml.Append($"<ExposureTime>{expTime.ToString("F6", CultureInfo.InvariantCulture)}</ExposureTime>");
            }

            if (capabilities.CanControlGain)
            {
                float gain = Math.Clamp(targetSettings.Gain, capabilities.MinGain, capabilities.MaxGain);
                exposureXml.Append($"<Gain>{gain.ToString("F1", CultureInfo.InvariantCulture)}</Gain>");
            }
            exposureXml.Append("</Exposure>");

            var wdrXml = "";
            if (capabilities.CanControlWdr)
            {
                wdrXml = $@"<WideDynamicRange xmlns=""http://www.onvif.org/ver10/schema"">
                                <Mode>{(targetSettings.WdrEnabled ? "ON" : "OFF")}</Mode>
                                <Level>{targetSettings.WdrLevel.ToString("F1", CultureInfo.InvariantCulture)}</Level>
                            </WideDynamicRange>";
            }

            var irCutXml = "";
            if (capabilities.CanControlDayNightMode)
            {
                string mode = targetSettings.DayNightMode.ToUpperInvariant();
                irCutXml = $@"<IrCutFilter xmlns=""http://www.onvif.org/ver10/schema"">{mode}</IrCutFilter>";
            }

            string soapBody = $@"<SetImagingSettings xmlns=""http://www.onvif.org/ver20/imaging/wsdl"">
                                    <VideoSourceToken>{videoSourceToken}</VideoSourceToken>
                                    <ImagingSettings>
                                        {exposureXml}
                                        {wdrXml}
                                        {irCutXml}
                                    </ImagingSettings>
                                    <ForcePersistence>true</ForcePersistence>
                                </SetImagingSettings>";

            string fullSoap = BuildSoapEnvelope(soapBody, username, password);

            try
            {
                using var response = await SendSoapAsync(imagingUrl, fullSoap, ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<HttpResponseMessage> SendSoapAsync(string url, string soap, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
            return await _httpClient.SendAsync(req, ct);
        }

        private static string BuildSoapEnvelope(string bodyContent, string username, string password)
        {
            string securityHeader = "";
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                byte[] nonce = new byte[16];
                RandomNumberGenerator.Fill(nonce);
                string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                byte[] createdBytes = Encoding.UTF8.GetBytes(created);
                byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? "");

                byte[] combined = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
                Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
                Buffer.BlockCopy(createdBytes, 0, combined, nonce.Length, createdBytes.Length);
                Buffer.BlockCopy(passwordBytes, 0, combined, nonce.Length + createdBytes.Length, passwordBytes.Length);

                byte[] hash = SHA1.HashData(combined);
                string passwordDigest = Convert.ToBase64String(hash);
                string nonceBase64 = Convert.ToBase64String(nonce);

                securityHeader = $@"
    <wsse:Security xmlns:wsse=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd""
                   xmlns:wsu=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">
      <wsse:UsernameToken>
        <wsse:Username>{System.Security.SecurityElement.Escape(username)}</wsse:Username>
        <wsse:Password Type=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"">{passwordDigest}</wsse:Password>
        <wsse:Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonceBase64}</wsse:Nonce>
        <wsu:Created>{created}</wsu:Created>
      </wsse:UsernameToken>
    </wsse:Security>";
            }

            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Header>
    {securityHeader}
  </s:Header>
  <s:Body>
    {bodyContent}
  </s:Body>
</s:Envelope>";
        }
    }
}
