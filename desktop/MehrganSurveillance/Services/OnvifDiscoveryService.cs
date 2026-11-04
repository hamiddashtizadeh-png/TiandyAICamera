using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services
{
    /// <summary>
    /// سرویس کشف هوشمند دوربین‌ها در شبکه محلی:
    /// ۱. کشف خودکار مبتنی بر استاندارد بین‌المللی ONVIF WS-Discovery (پروتکل UDP Multicast)
    /// ۲. اسکن کمکی و امن زیرشبکه (Subnet Scan) با نرخ محدود (Rate Limited) برای پورت‌های متداول دوربین
    /// </summary>
    public class OnvifDiscoveryService
    {
        private const string MulticastAddress = "239.255.255.250";
        private const int WsDiscoveryPort = 3702;

        public event Action<string>? LogMessage;
        public event Action<int, int>? ScanProgress; // current, total

        /// <summary>
        /// ارسال پروب WS-Discovery و جمع‌آوری پاسخ دستگاه‌های ONVIF در شبکه
        /// </summary>
        public async Task<List<DiscoveredCamera>> DiscoverOnvifDevicesAsync(int timeoutMilliseconds = 3500, CancellationToken ct = default)
        {
            var discovered = new Dictionary<string, DiscoveredCamera>();
            Log("آغاز جستجوی خودکار WS-Discovery روی آدرس مالتی‌کست 239.255.255.250:3702...");

            string probeMessage = BuildWsDiscoveryProbeXml();
            byte[] probeBytes = Encoding.UTF8.GetBytes(probeMessage);

            using var udpClient = new UdpClient();
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udpClient.EnableBroadcast = true;
            udpClient.Client.ReceiveTimeout = timeoutMilliseconds;

            var targetEndpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), WsDiscoveryPort);

            try
            {
                // ارسال پروب مالتی‌کست
                await udpClient.SendAsync(probeBytes, probeBytes.Length, targetEndpoint);
                Log("بسته درخواست WS-Discovery Probe ارسال شد. در انتظار پاسخ دوربین‌ها...");

                var startTime = DateTime.UtcNow;
                while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMilliseconds && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var receiveTask = udpClient.ReceiveAsync();
                        var timeoutTask = Task.Delay(Math.Max(100, timeoutMilliseconds - (int)(DateTime.UtcNow - startTime).TotalMilliseconds), ct);

                        var completed = await Task.WhenAny(receiveTask, timeoutTask);
                        if (completed == timeoutTask)
                            break;

                        var result = await receiveTask;
                        string xmlResponse = Encoding.UTF8.GetString(result.Buffer);

                        var camera = ParseProbeMatch(xmlResponse, result.RemoteEndPoint.Address.ToString());
                        if (camera != null && !discovered.ContainsKey(camera.IpAddress))
                        {
                            discovered[camera.IpAddress] = camera;
                            Log($"دوربین ONVIF شناسایی شد: {camera.Brand} در آدرس {camera.IpAddress} (پورت سرویس: {camera.HttpPort})");
                        }
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"خطا در ارسال یا دریافت پروب WS-Discovery: {ex.Message}");
            }

            Log($"جستجوی ONVIF به پایان رسید. تعداد دوربین‌های کشف‌شده: {discovered.Count}");
            return discovered.Values.ToList();
        }

        /// <summary>
        /// اسکن کمکی زیرشبکه محلی برای تجهیزاتی که ONVIF آن‌ها غیرفعال است یا فقط RTSP دارند
        /// از یک بازه ایمن همراه با Semaphore جهت محدودسازی نرخ کانکشن‌ها استفاده می‌شود.
        /// </summary>
        public async Task<List<DiscoveredCamera>> ScanSubnetAsync(string subnetPrefix, int startHost = 1, int endHost = 254, CancellationToken ct = default)
        {
            var results = new List<DiscoveredCamera>();
            int total = endHost - startHost + 1;
            int current = 0;

            Log($"آغاز اسکن ایمن زیرشبکه {subnetPrefix}.{startHost} تا {subnetPrefix}.{endHost} (بررسی پورت‌های 554, 3000, 80, 8000)...");

            // محدودسازی نرخ هم‌زمانی به حداکثر ۲۰ اتصال جهت جلوگیری از فشار روی روتر یا سوئیچ
            using var semaphore = new SemaphoreSlim(20, 20);
            var tasks = new List<Task>();

            for (int host = startHost; host <= endHost; host++)
            {
                if (ct.IsCancellationRequested)
                    break;

                string ip = $"{subnetPrefix}.{host}";
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        var cam = await ProbeHostForCameraAsync(ip, ct);
                        if (cam != null)
                        {
                            lock (results)
                            {
                                results.Add(cam);
                            }
                            Log($"دوربین یا دستگاه ویدیو در آدرس {ip} کشف شد (برند احتمالی: {cam.Brand}).");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        int c = Interlocked.Increment(ref current);
                        ScanProgress?.Invoke(c, total);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
            Log($"اسکن زیرشبکه پایان یافت. تعداد تجهیزات یافت‌شده: {results.Count}");
            return results;
        }

        private async Task<DiscoveredCamera?> ProbeHostForCameraAsync(string ip, CancellationToken ct)
        {
            // پورت‌های رایج دوربین‌های مداربسته:
            // 554: پورت استاندارد RTSP
            // 3000: پورت اختصاصی وب و مدیریت تیاندی (Tiandy Management Port)
            // 80: پورت وب HTTP عمومی و وب ONVIF
            // 8000: پورت سرویس اختصاصی هایک‌ویژن (Hikvision SDK)
            // 8899: پورت ONVIF در پاره‌ای از بردهای XM/Generic
            int[] probePorts = { 554, 3000, 80, 8000, 8899 };

            bool port554Open = false;
            bool port3000Open = false;
            bool port8000Open = false;
            int openWebPort = 80;
            bool anyPortOpen = false;

            foreach (var port in probePorts)
            {
                if (ct.IsCancellationRequested)
                    return null;

                bool isOpen = await CheckPortOpenAsync(ip, port, timeoutMs: 350);
                if (isOpen)
                {
                    anyPortOpen = true;
                    if (port == 554) port554Open = true;
                    if (port == 3000) { port3000Open = true; openWebPort = 3000; }
                    if (port == 8000) { port8000Open = true; }
                    if (port == 80 || port == 8899) { openWebPort = port; }
                }
            }

            if (!anyPortOpen)
                return null;

            // استنتاج برند بر اساس پورت‌های فعال
            string guessedBrand = "استاندارد / نامشخص";
            string guessedModel = "دوربین شبکه";
            if (port3000Open)
            {
                guessedBrand = "Tiandy (تیاندی)";
                guessedModel = "دوربین یا NVR تیاندی (شناسایی با پورت ۳۰۰۰)";
            }
            else if (port8000Open)
            {
                guessedBrand = "Hikvision (هایک‌ویژن)";
                guessedModel = "دوربین یا دستگاه هایک‌ویژن";
            }
            else if (port554Open)
            {
                guessedBrand = "RTSP Camera";
                guessedModel = "پورت استریم ۵۵۴ فعال";
            }

            var camera = new DiscoveredCamera
            {
                Id = $"SCAN-{ip}",
                IpAddress = ip,
                HttpPort = openWebPort,
                RtspPort = 554,
                Brand = guessedBrand,
                Model = guessedModel,
                DiscoverySource = "SubnetScan",
                IsOnvifSupported = port3000Open || openWebPort == 80 || openWebPort == 8899,
                OnvifServiceUrl = $"http://{ip}:{openWebPort}/onvif/device_service",
                State = DeviceDiscoveryState.AuthenticationRequired,
                StateMessage = "پورت‌های استریم باز است - آماده ورود نام کاربری و رمز عبور"
            };

            return camera;
        }

        private async Task<bool> CheckPortOpenAsync(string ip, int port, int timeoutMs = 350)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(timeoutMs);

                var completed = await Task.WhenAny(connectTask, timeoutTask);
                if (completed == connectTask && client.Connected)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ساخت بسته XML پروب استانداردی WS-Discovery
        /// </summary>
        private static string BuildWsDiscoveryProbeXml()
        {
            string messageId = $"urn:uuid:{Guid.NewGuid()}";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
  <e:Header>
    <w:MessageID>{messageId}</w:MessageID>
    <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
    <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
  </e:Header>
  <e:Body>
    <d:Probe>
      <d:Types>dn:NetworkVideoTransmitter</d:Types>
    </d:Probe>
  </e:Body>
</e:Envelope>";
        }

        /// <summary>
        /// پارس خروجی ProbeMatch از دوربین و استخراج XAddrs و Scopes
        /// </summary>
        private DiscoveredCamera? ParseProbeMatch(string xml, string remoteIp)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

                var probeMatch = doc.Descendants(d + "ProbeMatch").FirstOrDefault();
                if (probeMatch == null)
                    return null;

                string xaddrs = probeMatch.Element(d + "XAddrs")?.Value ?? string.Empty;
                string scopes = probeMatch.Element(d + "Scopes")?.Value ?? string.Empty;
                string endpoint = probeMatch.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value ?? Guid.NewGuid().ToString();

                string serviceUrl = string.Empty;
                string ip = remoteIp;
                int port = 80;

                // استخراج آدرس و پورت سرویس از XAddrs
                if (!string.IsNullOrEmpty(xaddrs))
                {
                    var firstUrl = xaddrs.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(firstUrl) && Uri.TryCreate(firstUrl, UriKind.Absolute, out Uri? uri))
                    {
                        serviceUrl = firstUrl;
                        ip = uri.Host;
                        port = uri.Port > 0 ? uri.Port : 80;
                    }
                }

                if (string.IsNullOrEmpty(serviceUrl))
                {
                    serviceUrl = $"http://{ip}:{port}/onvif/device_service";
                }

                // تحلیل Scopes جهت تشخیص نام برند، مدل و سخت‌افزار
                string brand = "ONVIF Camera";
                string model = "استاندارد";

                if (scopes.IndexOf("tiandy", StringComparison.OrdinalIgnoreCase) >= 0 || port == 3000)
                {
                    brand = "Tiandy (تیاندی)";
                }
                else if (scopes.IndexOf("dahua", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    brand = "Dahua (داهوا)";
                }
                else if (scopes.IndexOf("hikvision", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    brand = "Hikvision (هایک‌ویژن)";
                }
                else if (scopes.IndexOf("uniview", StringComparison.OrdinalIgnoreCase) >= 0 || scopes.IndexOf("univ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    brand = "Uniview (یونی‌ویو)";
                }

                // استخراج سخت‌افزار یا مدل از اسکوپ‌ها: onvif://www.onvif.org/hardware/...
                var hwMatch = Regex.Match(scopes, @"hardware/([^\s]+)", RegexOptions.IgnoreCase);
                if (hwMatch.Success)
                {
                    model = Uri.UnescapeDataString(hwMatch.Groups[1].Value);
                }
                else
                {
                    var nameMatch = Regex.Match(scopes, @"name/([^\s]+)", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                    {
                        model = Uri.UnescapeDataString(nameMatch.Groups[1].Value);
                    }
                }

                return new DiscoveredCamera
                {
                    Id = endpoint,
                    IpAddress = ip,
                    HttpPort = port,
                    RtspPort = 554,
                    Brand = brand,
                    Model = model,
                    OnvifServiceUrl = serviceUrl,
                    IsOnvifSupported = true,
                    DiscoverySource = "WS-Discovery",
                    State = DeviceDiscoveryState.AuthenticationRequired,
                    StateMessage = "دستگاه ONVIF کشف شد - آماده احراز هویت"
                };
            }
            catch (Exception ex)
            {
                Log($"خطا در پارس پاسخ ProbeMatch: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// استخراج پیشوند زیرشبکه کارت شبکه پیش‌فرض محلی (مانند 192.168.1)
        /// </summary>
        public static string GetDefaultSubnetPrefix()
        {
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != OperationalStatus.Up ||
                        iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var ipProps = iface.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = addr.Address.GetAddressBytes();
                            // اگر آدرس محلی کلاس C یا B باشد
                            if (bytes[0] == 192 && bytes[1] == 168)
                            {
                                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
                            }
                            if (bytes[0] == 10)
                            {
                                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return "192.168.1";
        }

        private void Log(string message)
        {
            LogMessage?.Invoke(message);
        }
    }
}
