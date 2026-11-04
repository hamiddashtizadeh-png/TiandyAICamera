using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services
{
    /// <summary>
    /// کلاینت تعاملی ONVIF جهت اعتبارسنجی (WS-UsernameToken PasswordDigest)،
    /// دریافت مشخصات دستگاه (GetDeviceInformation)،
    /// استخراج پروفایل‌های ویدیویی (GetProfiles) و آدرس مستقیم استریم‌ها (GetStreamUri).
    /// در صورت عدم پشتیبانی از ONVIF، تولید هوشمند مسیرهای استریم سازندگان (Tiandy, Dahua, Hikvision) انجام می‌شود.
    /// </summary>
    public class OnvifClientService
    {
        private readonly HttpClient _httpClient;

        public event Action<string>? LogMessage;

        public OnvifClientService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        /// <summary>
        /// تست احراز هویت و دریافت اطلاعات دستگاه و پروفایل‌های استریم
        /// </summary>
        public async Task<bool> AuthenticateAndLoadProfilesAsync(DiscoveredCamera camera, string username, string password, CancellationToken ct = default)
        {
            camera.Username = username;
            camera.Password = password;
            camera.State = DeviceDiscoveryState.Authenticating;
            camera.StateMessage = "در حال احراز هویت و بررسی پشتیبانی از ONVIF...";

            Log($"شروع احراز هویت برای دوربین {camera.IpAddress} با پروتکل ONVIF...");

            try
            {
                string deviceServiceUrl = camera.OnvifServiceUrl;
                if (string.IsNullOrEmpty(deviceServiceUrl))
                {
                    deviceServiceUrl = $"http://{camera.IpAddress}:{camera.HttpPort}/onvif/device_service";
                }

                // گام ۱: فراخوانی GetDeviceInformation
                var (devSuccess, manufacturer, model, fw, serial) = await GetDeviceInformationAsync(deviceServiceUrl, username, password, ct);
                if (devSuccess)
                {
                    camera.Brand = !string.IsNullOrWhiteSpace(manufacturer) ? manufacturer : camera.Brand;
                    camera.Model = !string.IsNullOrWhiteSpace(model) ? model : camera.Model;
                    camera.FirmwareVersion = fw;
                    camera.SerialNumber = serial;
                    camera.State = DeviceDiscoveryState.Authenticated;
                    Log($"احراز هویت موفق: سازنده: {camera.Brand} | مدل: {camera.Model} | فریمور: {fw}");
                }
                else
                {
                    Log($"فراخوانی اولیه ONVIF DeviceInfo با خطا مواجه شد. در حال آزمایش روش‌های جایگزین و تطبیق الگوی استریم...");
                }

                // گام ۲: دریافت آدرس Media Service
                string mediaServiceUrl = await GetMediaServiceUrlAsync(deviceServiceUrl, username, password, ct);
                if (string.IsNullOrEmpty(mediaServiceUrl))
                {
                    mediaServiceUrl = $"http://{camera.IpAddress}:{camera.HttpPort}/onvif/media_service";
                }

                // گام ۳: فراخوانی GetProfiles
                var profiles = await GetProfilesAsync(mediaServiceUrl, username, password, ct);

                if (profiles.Count > 0)
                {
                    camera.Profiles.Clear();
                    // گام ۴: دریافت آدرس‌های RTSP برای هر پروفایل
                    for (int i = 0; i < profiles.Count; i++)
                    {
                        var prof = profiles[i];
                        string rtspUri = await GetStreamUriAsync(mediaServiceUrl, prof.Token, username, password, ct);

                        if (!string.IsNullOrEmpty(rtspUri))
                        {
                            prof.RtspUri = InjectCredentialsIntoRtspUri(rtspUri, username, password);
                        }
                        else
                        {
                            // مسیر جایگزین در صورت بازنگرداندن آدرس کامل توسط دوربین
                            prof.RtspUri = GenerateVendorRtspUri(camera.Brand, camera.IpAddress, camera.RtspPort, username, password, i == 0 ? 1 : 2);
                        }

                        prof.IsSubStream = (i > 0);
                        camera.Profiles.Add(prof);
                        Log($"پروفایل شناسایی شد: {prof.DisplayLabel}");
                    }

                    camera.MainStreamUri = camera.Profiles[0].RtspUri;
                    camera.SubStreamUri = camera.Profiles.Count > 1 ? camera.Profiles[1].RtspUri : camera.MainStreamUri;
                    camera.State = DeviceDiscoveryState.StreamAvailable;
                    camera.StateMessage = $"احراز هویت و دریافت {camera.Profiles.Count} پروفایل استریم با موفقیت انجام شد.";
                    return true;
                }
                else
                {
                    // در صورتی که دریافت پروفایل‌های ONVIF ناموفق بود، تولید هوشمند مسیرهای مستند سازنده
                    Log("دریافت پروفایل‌های ONVIF بدون پاسخ ماند. تولید مسیرهای استاندارد بر اساس برند شناسایی‌شده...");
                    PopulateFallbackVendorProfiles(camera, username, password);

                    camera.State = DeviceDiscoveryState.StreamAvailable;
                    camera.StateMessage = "مسیرهای استاندارد استریم سازنده تخصیص داده شد.";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"خطا در ارتباط ONVIF: {ex.Message}");
                // حتی در صورت خطای کامل وب‌سرویس ONVIF، تولید مسیرهای استریم سازنده جهت حفظ پایداری
                PopulateFallbackVendorProfiles(camera, username, password);
                camera.State = DeviceDiscoveryState.AuthenticationRequired;
                camera.StateMessage = $"خطا در وب‌سرویس ONVIF ({ex.Message})؛ مسیرهای پیش‌فرض سازنده تنظیم گردید.";
                return false;
            }
        }

        private async Task<(bool success, string manufacturer, string model, string fw, string serial)> GetDeviceInformationAsync(
            string serviceUrl, string username, string password, CancellationToken ct)
        {
            string soap = BuildSoapEnvelope(
                @"<GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>",
                username, password);

            try
            {
                using var response = await SendSoapRequestAsync(serviceUrl, soap, ct);
                if (!response.IsSuccessStatusCode)
                    return (false, "", "", "", "");

                string content = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(content);

                string manufacturer = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Manufacturer")?.Value ?? "";
                string model = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Model")?.Value ?? "";
                string fw = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FirmwareVersion")?.Value ?? "";
                string serial = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SerialNumber")?.Value ?? "";

                return (true, manufacturer, model, fw, serial);
            }
            catch
            {
                return (false, "", "", "", "");
            }
        }

        private async Task<string> GetMediaServiceUrlAsync(string deviceServiceUrl, string username, string password, CancellationToken ct)
        {
            string soap = BuildSoapEnvelope(
                @"<GetCapabilities xmlns=""http://www.onvif.org/ver10/device/wsdl"">
                    <Category>Media</Category>
                  </GetCapabilities>",
                username, password);

            try
            {
                using var response = await SendSoapRequestAsync(deviceServiceUrl, soap, ct);
                if (!response.IsSuccessStatusCode)
                    return "";

                string content = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(content);

                var xAddr = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr" &&
                    e.Parent != null && e.Parent.Name.LocalName == "Media")?.Value;

                return xAddr ?? "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<List<CameraStreamProfile>> GetProfilesAsync(string mediaServiceUrl, string username, string password, CancellationToken ct)
        {
            var list = new List<CameraStreamProfile>();
            string soap = BuildSoapEnvelope(
                @"<GetProfiles xmlns=""http://www.onvif.org/ver10/media/wsdl""/>",
                username, password);

            try
            {
                using var response = await SendSoapRequestAsync(mediaServiceUrl, soap, ct);
                if (!response.IsSuccessStatusCode)
                    return list;

                string content = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(content);

                var profileElements = doc.Descendants().Where(e => e.Name.LocalName == "Profiles").ToList();
                int index = 0;
                foreach (var p in profileElements)
                {
                    string token = p.Attribute("token")?.Value ?? $"Profile_{index + 1}";
                    string name = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? $"استریم {index + 1}";

                    // استخراج مشخصات انکودر ویدیو
                    var videoConf = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
                    string encoding = videoConf?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value ?? "H264";

                    var resolution = videoConf?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Resolution");
                    string width = resolution?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value ?? "";
                    string height = resolution?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value ?? "";

                    string resText = (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height)) ? $"{width}x{height}" : (index == 0 ? "1080p (تخمینی)" : "D1/Sub (تخمینی)");

                    list.Add(new CameraStreamProfile
                    {
                        Name = name,
                        Token = token,
                        Resolution = resText,
                        Encoding = encoding,
                        IsSubStream = (index > 0)
                    });
                    index++;
                }
            }
            catch (Exception ex)
            {
                Log($"خطا در دریافت پروفایل‌های مدیا: {ex.Message}");
            }

            return list;
        }

        private async Task<string> GetStreamUriAsync(string mediaServiceUrl, string profileToken, string username, string password, CancellationToken ct)
        {
            string soap = BuildSoapEnvelope(
                $@"<GetStreamUri xmlns=""http://www.onvif.org/ver10/media/wsdl"">
                    <StreamSetup>
                      <Stream xmlns=""http://www.onvif.org/ver10/schema"">RTP-Unicast</Stream>
                      <Transport xmlns=""http://www.onvif.org/ver10/schema"">
                        <Protocol>RTSP</Protocol>
                      </Transport>
                    </StreamSetup>
                    <ProfileToken>{profileToken}</ProfileToken>
                  </GetStreamUri>",
                username, password);

            try
            {
                using var response = await SendSoapRequestAsync(mediaServiceUrl, soap, ct);
                if (!response.IsSuccessStatusCode)
                    return "";

                string content = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(content);

                string uri = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value ?? "";
                return uri.Trim();
            }
            catch
            {
                return "";
            }
        }

        private void PopulateFallbackVendorProfiles(DiscoveredCamera camera, string username, string password)
        {
            camera.Profiles.Clear();

            string mainUri = GenerateVendorRtspUri(camera.Brand, camera.IpAddress, camera.RtspPort, username, password, 1);
            string subUri = GenerateVendorRtspUri(camera.Brand, camera.IpAddress, camera.RtspPort, username, password, 2);

            camera.Profiles.Add(new CameraStreamProfile
            {
                Name = "استریم اصلی (Main Stream)",
                Token = "Profile_Main",
                RtspUri = mainUri,
                Resolution = "کیفیت اصلی (HD/4K)",
                Encoding = "H.264 / H.265",
                IsSubStream = false
            });

            camera.Profiles.Add(new CameraStreamProfile
            {
                Name = "استریم فرعی (Sub Stream)",
                Token = "Profile_Sub",
                RtspUri = subUri,
                Resolution = "کیفیت سبک شبکه (D1 / 720p)",
                Encoding = "H.264",
                IsSubStream = true
            });

            camera.MainStreamUri = mainUri;
            camera.SubStreamUri = subUri;
        }

        /// <summary>
        /// ساخت آدرس‌های استاندارد سازندگان مختلف بر اساس مستندات مهندسی:
        /// ۱. تیاندی (Tiandy): rtsp://user:pass@ip:port/CHANNEL/STREAM -> /1/1 و /1/2
        /// ۲. داهوا (Dahua): /cam/realmonitor?channel=1&subtype=0 و subtype=1
        /// ۳. هایک‌ویژن (Hikvision): /Streaming/Channels/101 و 102
        /// </summary>
        public static string GenerateVendorRtspUri(string brand, string ip, int port, string user, string pass, int streamNumber)
        {
            string cleanBrand = brand.ToLowerInvariant();
            string cred = (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass)) ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@" : "";

            if (cleanBrand.Contains("tiandy") || cleanBrand.Contains("تیاندی"))
            {
                // فرمت رسمی و اثبات‌شده تیاندی: /1/1 (استریم ۱)، /1/2 (استریم ۲)
                return $"rtsp://{cred}{ip}:{port}/1/{streamNumber}";
            }
            else if (cleanBrand.Contains("dahua") || cleanBrand.Contains("داهوا"))
            {
                int subtype = (streamNumber == 1) ? 0 : 1;
                return $"rtsp://{cred}{ip}:{port}/cam/realmonitor?channel=1&subtype={subtype}";
            }
            else if (cleanBrand.Contains("hikvision") || cleanBrand.Contains("هایک"))
            {
                int channelId = (streamNumber == 1) ? 101 : 102;
                return $"rtsp://{cred}{ip}:{port}/Streaming/Channels/{channelId}";
            }
            else
            {
                // مسیر عمومی سازگار
                string path = (streamNumber == 1) ? "/live/main" : "/live/sub";
                return $"rtsp://{cred}{ip}:{port}{path}";
            }
        }

        private static string InjectCredentialsIntoRtspUri(string rawRtspUri, string username, string password)
        {
            if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
                return rawRtspUri;

            if (!rawRtspUri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                return rawRtspUri;

            // اگر پسورد قبلاً در آدرس نبود، تزریق می‌کنیم
            if (rawRtspUri.IndexOf('@') < 0)
            {
                string afterPrefix = rawRtspUri.Substring("rtsp://".Length);
                string encodedUser = Uri.EscapeDataString(username);
                string encodedPass = Uri.EscapeDataString(password);
                return $"rtsp://{encodedUser}:{encodedPass}@{afterPrefix}";
            }

            return rawRtspUri;
        }

        private async Task<HttpResponseMessage> SendSoapRequestAsync(string url, string soapXml, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(soapXml, Encoding.UTF8, "application/soap+xml");
            return await _httpClient.SendAsync(request, ct);
        }

        /// <summary>
        /// ساخت Envelope پروتکل SOAP همراه با WS-Security UsernameToken (PasswordDigest)
        /// </summary>
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

        private void Log(string message)
        {
            LogMessage?.Invoke(message);
        }
    }
}
