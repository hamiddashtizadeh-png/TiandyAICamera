import { VendorAdapter, StreamProfile } from '../types/poc';
import { MASKED_PASS } from './security';

export interface RtspCandidatePath {
  path: string;
  streamNumber: 1 | 2 | 3;
  category: 'primary_documented' | 'optional_fallback';
  description: string;
  verificationStatus: 'تاییدشده در مستندات' | 'کاندید تجربی (اختیاری)';
}

export interface DiagnosticHypothesis {
  id: string;
  title: string;
  category: 'کدک تصویر' | 'پروتکل انتقال' | 'سازگاری مرورگر' | 'شتاب‌دهنده گرافیکی ویندوز';
  description: string;
  isProvenCause: boolean;
  testMethod: string;
  verificationCommand: string;
}

/**
 * فرمت مستند و تاییدشده آدرس RTSP در تجهیزات تیاندی:
 * rtsp://USERNAME:PASSWORD@IP:PORT/CHANNEL/STREAM
 *
 * پارامترها:
 * - کانال (Channel): 1 تا N
 * - استریم ۱ (جریان اصلی): /1/1
 * - استریم ۲ (جریان فرعی وب): /1/2
 * - استریم ۳ (جریان سوم/موبایل): /1/3
 * - پورت RTSP: معمولاً 554 (باید اسکن شود)
 * - پورت مدیریت وب دوربین: 3000 (HTTP GUI، غیر RTSP)
 */
export function generateTiandyRtspUrl(params: {
  host: string;
  port?: number;
  channel?: number;
  stream?: 1 | 2 | 3;
  username?: string;
  password?: string;
  safeForDisplay?: boolean;
}): string {
  const {
    host,
    port = 554,
    channel = 1,
    stream = 1,
    username = 'admin',
    password = '',
    safeForDisplay = true,
  } = params;

  const passStr = safeForDisplay ? MASKED_PASS : password || 'password';
  const portPart = port === 554 ? '' : `:${port}`;

  if (!username && !password) {
    return `rtsp://${host}${portPart}/${channel}/${stream}`;
  }

  return `rtsp://${username}:${passStr}@${host}${portPart}/${channel}/${stream}`;
}

/**
 * ۳ استریم مستقل مستند تیاندی
 */
export const DOCUMENTED_TIANDY_STREAMS: StreamProfile[] = [
  {
    streamNumber: 1,
    name: 'استریم ۱ (جریان اصلی /1/1)',
    channel: 1,
    rtspPath: '/1/1',
    targetCodec: 'H.264',
    detectedCodec: 'نامشخص (نیاز به ffprobe)',
    resolution: '1920x1080 (تخمینی)',
    fps: 25,
    bitrateKbps: 4096,
    gopSize: 50,
    isDecoded: false,
    webRtcCompatible: false,
    hlsCompatible: false,
    windowsCompatible: false,
    lastDecodedFramesCount: 0,
    verificationState: 'untested',
    notes: 'جریان با وضوح بالا برای ضبط اصلی و نظارت دقیق.',
  },
  {
    streamNumber: 2,
    name: 'استریم ۲ (جریان فرعی وب /1/2)',
    channel: 1,
    rtspPath: '/1/2',
    targetCodec: 'H.264',
    detectedCodec: 'نامشخص (نیاز به ffprobe)',
    resolution: '720x480 / D1 (تخمینی)',
    fps: 25,
    bitrateKbps: 1024,
    gopSize: 25,
    isDecoded: false,
    webRtcCompatible: false,
    hlsCompatible: false,
    windowsCompatible: false,
    lastDecodedFramesCount: 0,
    verificationState: 'untested',
    notes: 'جریان سبک بهینه‌شده برای WebRTC در مرورگر وب.',
  },
  {
    streamNumber: 3,
    name: 'استریم ۳ (جریان سوم /1/3)',
    channel: 1,
    rtspPath: '/1/3',
    targetCodec: 'H.264',
    detectedCodec: 'نامشخص (نیاز به ffprobe)',
    resolution: '640x360 (تخمینی)',
    fps: 15,
    bitrateKbps: 512,
    gopSize: 30,
    isDecoded: false,
    webRtcCompatible: false,
    hlsCompatible: false,
    windowsCompatible: false,
    lastDecodedFramesCount: 0,
    verificationState: 'untested',
    notes: 'جریان کم‌حجم برای تصویر بندانگشتی یا موبایل.',
  },
];

/**
 * مسیرهای کاندید RTSP با تفکیک مستند و اختیاری
 */
export const CANDIDATE_RTSP_PATHS: RtspCandidatePath[] = [
  {
    path: '/1/1',
    streamNumber: 1,
    category: 'primary_documented',
    description: 'مسیر رسمی تیاندی برای کانال ۱، استریم ۱ (جریان اصلی)',
    verificationStatus: 'تاییدشده در مستندات',
  },
  {
    path: '/1/2',
    streamNumber: 2,
    category: 'primary_documented',
    description: 'مسیر رسمی تیاندی برای کانال ۱، استریم ۲ (جریان فرعی وب)',
    verificationStatus: 'تاییدشده در مستندات',
  },
  {
    path: '/1/3',
    streamNumber: 3,
    category: 'primary_documented',
    description: 'مسیر رسمی تیاندی برای کانال ۱، استریم ۳ (جریان سبک)',
    verificationStatus: 'تاییدشده در مستندات',
  },
  {
    path: '/live1',
    streamNumber: 1,
    category: 'optional_fallback',
    description: 'مسیر تجربی در فریم‌ورهای Easy7 (کاندید اختیاری)',
    verificationStatus: 'کاندید تجربی (اختیاری)',
  },
  {
    path: '/live2',
    streamNumber: 2,
    category: 'optional_fallback',
    description: 'مسیر تجربی استریم ۲ در برخی فریم‌ورها (کاندید اختیاری)',
    verificationStatus: 'کاندید تجربی (اختیاری)',
  },
  {
    path: '/Streaming/Channels/101',
    streamNumber: 1,
    category: 'optional_fallback',
    description: 'فرمت ISAPI / سازگاری ONVIF (کاندید اختیاری)',
    verificationStatus: 'کاندید تجربی (اختیاری)',
  },
  {
    path: '/ch01/0',
    streamNumber: 1,
    category: 'optional_fallback',
    description: 'فرمت کانال NVRهای قدیمی بدون فرمت اسلش (کاندید اختیاری)',
    verificationStatus: 'کاندید تجربی (اختیاری)',
  },
];

/**
 * فرضیات بررسی علل مشکلات گذشته (بدون اثبات پیش‌فرض)
 */
export const FAILURE_HYPOTHESES: DiagnosticHypothesis[] = [
  {
    id: 's265-hypothesis',
    title: 'فرضیه ۱: فعال بودن کدک اختصاصی S+265',
    category: 'کدک تصویر',
    description: 'دوربین ممکن است به طور پیش‌فرض روی الگوریتم S+265 باشد. این کدک ساختار هدرهای اسلایس را دستکاری می‌کند و ممکن است در مرورگرها یا ویندوز فریز ایجاد کند. این صرفاً یک فرضیه محتمل است و باید با ffprobe تست شود.',
    isProvenCause: false,
    testMethod: 'بررسی NAL Unitها و ساختار فریم‌های کلیدی در لاگ FFprobe',
    verificationCommand: 'ffprobe -v error -rtsp_transport tcp -show_entries stream=codec_name "rtsp://admin:$CAM_PASS@HOST:554/1/1"',
  },
  {
    id: 'udp-loss-hypothesis',
    title: 'فرضیه ۲: اتلاف بسته‌های ویدیویی روی UDP',
    category: 'پروتکل انتقال',
    description: 'استفاده از پروتکل پیش‌فرض UDP در شبکه‌های محلی موجب از دست رفتن بسته‌های حجیم فریم کلیدی شده و تصویر را پاره یا فریز می‌کند.',
    isProvenCause: false,
    testMethod: 'اجبار به استفاده از پروتکل TCP در ffmpeg و vlc',
    verificationCommand: 'ffmpeg -rtsp_transport tcp -i "rtsp://admin:$CAM_PASS@HOST:554/1/1" -vframes 50 -f null -',
  },
  {
    id: 'webrtc-sdp-hypothesis',
    title: 'فرضیه ۳: رد شدن پروفایل H.264 در مذاکره SDP',
    category: 'سازگاری مرورگر',
    description: 'مرورگر ممکن است پروفایل High Profile ارسالی توسط دوربین را نپذیرد یا نیازمند ترنسکود به Baseline باشد.',
    isProvenCause: false,
    testMethod: 'بررسی لاگ WHEP و SDP Answer در MediaMTX',
    verificationCommand: 'curl -i -X POST http://GATEWAY:8889/tiandy_stream1/whep -H "Content-Type: application/sdp"',
  },
  {
    id: 'windows-d3d11va-hypothesis',
    title: 'فرضیه ۴: کرش شتاب سخت‌افزاری ویندوز (D3D11VA)',
    category: 'شتاب‌دهنده گرافیکی ویندوز',
    description: 'درایور کارت گرافیک ویندوز در مواجهه با فواصل متغیر GOP در VLC کرش کرده است.',
    isProvenCause: false,
    testMethod: 'تست در VLC با فلگ --avcodec-hw=none و اجبار به --rtsp-tcp',
    verificationCommand: 'vlc --rtsp-tcp --network-caching=300 --avcodec-hw=none "rtsp://admin:$CAM_PASS@HOST:554/1/1"',
  },
];

/**
 * آداپتورهای سازندگان جهت استقلال درگاه
 */
export const VENDOR_ADAPTERS: VendorAdapter[] = [
  {
    id: 'tiandy',
    name: 'تیاندی - Tiandy (مستند: /CHANNEL/STREAM)',
    isPrimary: true,
    rtspUrlTemplate: 'rtsp://USER:PASS@HOST:PORT/{channel}/{stream}',
    generateUrl: ({ host, port, channel, stream }) =>
      `rtsp://${host}:${port}/${channel}/${stream}`,
    managementPortDefault: 3000,
    rtspPortDefault: 554,
    description: 'فرمت مستند: /1/1، /1/2، /1/3. پورت وب 3000.',
  },
  {
    id: 'onvif',
    name: 'استاندارد جهانی ONVIF Profile S (پشتیبان)',
    isPrimary: false,
    rtspUrlTemplate: 'rtsp://USER:PASS@HOST:PORT/Streaming/Channels/{channel}0{stream}',
    generateUrl: ({ host, port, channel, stream }) =>
      `rtsp://${host}:${port}/Streaming/Channels/${channel}0${stream}`,
    managementPortDefault: 80,
    rtspPortDefault: 554,
    description: 'استاندارد باز بین‌المللی سازگاری دوربین‌ها.',
  },
  {
    id: 'hikvision',
    name: 'هایک‌ویژن / ISAPI (پشتیبان)',
    isPrimary: false,
    rtspUrlTemplate: 'rtsp://USER:PASS@HOST:PORT/Streaming/Channels/{channel}0{stream}',
    generateUrl: ({ host, port, channel, stream }) =>
      `rtsp://${host}:${port}/Streaming/Channels/${channel}0${stream}`,
    managementPortDefault: 80,
    rtspPortDefault: 554,
    description: 'الگوی آدرس‌دهی ISAPI.',
  },
  {
    id: 'dahua',
    name: 'داهوآ / Dahua (پشتیبان)',
    isPrimary: false,
    rtspUrlTemplate: 'rtsp://USER:PASS@HOST:PORT/cam/realmonitor?channel={channel}&subtype={stream-1}',
    generateUrl: ({ host, port, channel, stream }) =>
      `rtsp://${host}:${port}/cam/realmonitor?channel=${channel}&subtype=${stream - 1}`,
    managementPortDefault: 80,
    rtspPortDefault: 554,
    description: 'الگوی subtype صفرمبنا.',
  },
];
