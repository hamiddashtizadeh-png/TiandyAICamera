import { AuditStep, ComparisonCriterion, StreamEndpointConfig } from '../types/poc';

export const INITIAL_DIRECT_CONFIG: StreamEndpointConfig = {
  label: 'اتصال مستقیم به دوربین تیاندی (Method A)',
  method: 'direct',
  host: '192.168.1.168',
  rtspPort: 554, // پورت RTSP (باید اسکن شود)
  webManagementPort: 3000, // پورت وب و مدیریت تیاندی (HTTP GUI)
  username: 'admin',
  passwordMask: '••••••••',
  channel: 1,
  streamNumber: 1,
  resolvedRtspPath: '/1/1', // فرمت مستند: /CHANNEL/STREAM
  detectedCodec: 'نامشخص (نیاز به ffprobe)',
  resolution: '1920x1080 (تخمینی)',
  fps: 25,
  bitrateKbps: 4096,
  keyframeIntervalGop: 50,
  verificationState: 'untested',
};

export const INITIAL_NVR_CONFIG: StreamEndpointConfig = {
  label: 'اتصال از طریق دستگاه NVR تیاندی (Method B)',
  method: 'nvr',
  host: '192.168.1.200',
  rtspPort: 554,
  webManagementPort: 80,
  username: 'admin',
  passwordMask: '••••••••',
  channel: 1,
  streamNumber: 1,
  resolvedRtspPath: '/1/1', // کانال ۱، استریم ۱ در NVR
  detectedCodec: 'نامشخص (نیاز به ffprobe)',
  resolution: '1920x1080 (تخمینی)',
  fps: 25,
  bitrateKbps: 4096,
  keyframeIntervalGop: 50,
  verificationState: 'untested',
};

export const DIRECT_AUDIT_STEPS: AuditStep[] = [
  {
    id: 'audit-a1-discovery',
    stageNumber: 1,
    title: 'کشف IP دوربین و تایید پورت‌های RTSP و مدیریت وب',
    objective: 'شناسایی آدرس IP دوربین روی شبکه محلی با Search Config، تایید باز بودن پورت RTSP (معمولاً 554) و پورت وب 3000.',
    method: 'direct',
    verificationState: 'untested',
    parameters: {
      'ابزار کشف': 'Tiandy Search Config v2 / اسکنر ARP و ONVIF',
      'آدرس IP فرضی': '192.168.1.168 (باید اسکن شود)',
      'پورت ویدیویی RTSP': '554 (نیاز به تایید با nc یا telnet)',
      'پورت وب و تنظیمات': '3000 (HTTP Web GUI تیاندی - غیر RTSP)',
      'مک آدرس سازنده': '00:1E:AC (Tiandy Technologies)',
    },
    commands: [
      {
        label: 'تست باز بودن پورت RTSP (554)',
        description: 'بررسی ارتباط لایه انتقال TCP روی پورت RTSP بدون فرضیات قبلی.',
        command: 'nc -zv 192.168.1.168 554',
        expectedOutputSnippet: 'Connection to 192.168.1.168 port 554 [tcp/rtsp] succeeded!',
      },
      {
        label: 'تست پورت مدیریت وب دوربین (3000)',
        description: 'تایید دسترسی به وب سرویس تیاندی روی پورت 3000.',
        command: 'curl -I http://192.168.1.168:3000',
        expectedOutputSnippet: 'HTTP/1.1 200 OK یا صفحه لاگین وب تیاندی',
      },
    ],
    evidenceRequired: 'لاگ موفقیت‌آمیز TCP Handshake روی پورت 554 و پاسخ HTTP روی پورت 3000.',
  },
  {
    id: 'audit-a2-streams-probe',
    stageNumber: 2,
    title: 'آزمون مستقل ۳ استریم مستند تیاندی (/1/1، /1/2، /1/3)',
    objective: 'پروب کردن مستقل هر سه استریم جهت استخراج کدک واقعی، رزولوشن، FPS و نرخ بیت بدون فرض یکسان بودن تنظیمات.',
    method: 'direct',
    verificationState: 'untested',
    parameters: {
      'فرمت تاییدشده': 'rtsp://USER:PASS@IP:PORT/CHANNEL/STREAM',
      'استریم ۱ (اصلی)': '/1/1 (وضوح بالا)',
      'استریم ۲ (فرعی)': '/1/2 (مناسب مرورگر و وب)',
      'استریم ۳ (سوم)': '/1/3 (موبایل و پردازش سبک)',
      'پروتکل لایه انتقال': 'RTSP روی TCP الزامی است (-rtsp_transport tcp)',
    },
    commands: [
      {
        label: 'پروب استریم ۱ (/1/1)',
        description: 'استخراج مشخصات واقعی کدک، رزولوشن و فریم‌ریت استریم ۱.',
        command: 'ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,profile,width,height,r_frame_rate,bit_rate -of json "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1"',
        expectedOutputSnippet: 'JSON خروجی شامل codec_name واقعی (h264 یا hevc) و رزولوشن',
      },
      {
        label: 'پروب استریم ۲ (/1/2)',
        description: 'استخراج مشخصات استریم دوم.',
        command: 'ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,profile,width,height,r_frame_rate -of json "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/2"',
        expectedOutputSnippet: 'JSON خروجی استریم دوم (معمولاً D1 یا 720p)',
      },
      {
        label: 'پروب استریم ۳ (/1/3)',
        description: 'استخراج مشخصات استریم سوم.',
        command: 'ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,profile,width,height,r_frame_rate -of json "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/3"',
        expectedOutputSnippet: 'JSON خروجی استریم سوم',
      },
    ],
    evidenceRequired: 'فایل JSON خروجی FFprobe برای هر سه استریم که عدم تطابق فرضیات را اثبات کند.',
  },
  {
    id: 'audit-a3-ffmpeg-decoding',
    stageNumber: 3,
    title: 'اعتبارسنجی دیکود واقعی فریم‌ها با FFmpeg',
    objective: 'دیکود قطعی ۵۰ فریم متوالی از استریم ۱ به خروجی null sink برای اثبات اینکه استریم قابل دیکود است و صرفاً کانکشن خالی نیست.',
    method: 'direct',
    verificationState: 'untested',
    parameters: {
      'تعداد فریم درخواستی': '۵۰ فریم متوالی',
      'معیار قبولی': 'دریافت frame=50 بدون macroblock corruption و بدون توقف',
      'نوع انتقال': 'TCP اینترلیود',
    },
    commands: [
      {
        label: 'دیکود ۵۰ فریم از استریم ۱ (/1/1)',
        description: 'اجرای دیکودر تصویری روی بسته‌های دریافتی RTSP.',
        command: 'ffmpeg -rtsp_transport tcp -i "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1" -vframes 50 -f null -',
        expectedOutputSnippet: 'frame=   50 fps=... q=-0.0 Lsize=N/A time=00:00:02.00 bitrate=N/A speed=...',
      },
      {
        label: 'استخراج تک‌فریم تصویر جهت ارزیابی بصری',
        description: 'ذخیره یک فریم تصویر واقعی روی دیسک.',
        command: 'ffmpeg -rtsp_transport tcp -i "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1" -vframes 1 -q:v 2 frame_direct_1_1.jpg',
        expectedOutputSnippet: 'frame_direct_1_1.jpg با رزولوشن واقعی دوربین ذخیره شد.',
      },
    ],
    evidenceRequired: 'شمارنده فریم‌های دیکود شده در خروجی لاگ FFmpeg برابر با ۵۰.',
  },
  {
    id: 'audit-a4-mediamtx-webrtc-hls',
    stageNumber: 4,
    title: 'اتصال به درگاه MediaMTX و ارزیابی تفکیک‌شده WebRTC و HLS',
    objective: 'تغذیه استریم تاییدشده دوربین به MediaMTX روی پروتکل TCP و ارزیابی مجزای پخش WebRTC (با تاخیر کم) و فال‌بک HLS.',
    method: 'direct',
    verificationState: 'untested',
    parameters: {
      'نام مسیر درگاه': 'tiandy_stream1',
      'منبع درگاه': 'rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1',
      'پروتکل ورودی': 'sourceProtocol: tcp الزامی است',
      'اندپوینت WebRTC WHEP': 'http://GATEWAY:8889/tiandy_stream1/whep',
      'اندپوینت HLS fMP4': 'http://GATEWAY:8888/tiandy_stream1/index.m3u8',
    },
    commands: [
      {
        label: 'بررسی لاگ اینجکشن MediaMTX',
        description: 'اطمینان از قفل شدن سشن RTSP درگاه با دوربین.',
        command: './mediamtx | grep "path tiandy_stream1"',
        expectedOutputSnippet: '[path tiandy_stream1] [RTSP source] ready: 1 video track over TCP',
      },
      {
        label: 'تست لیست پخش HLS',
        description: 'دریافت منیفست HLS در مرورگر.',
        command: 'curl -s http://localhost:8888/tiandy_stream1/index.m3u8 | head -n 8',
        expectedOutputSnippet: '#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1',
      },
    ],
    evidenceRequired: 'مشاهده استریم روان در مرورگر هم در حالت WebRTC و هم HLS.',
  },
  {
    id: 'audit-a5-windows-playback',
    stageNumber: 5,
    title: 'ارزیابی پخش تصویر در ویندوز (VLC و FFplay)',
    objective: 'آزمون پخش فیزیکی تصویر در کلاینت ویندوز با اجبار TCP و تنظیمات کش، برای رفع مشکلات تاریخی فریز تصویر.',
    method: 'direct',
    verificationState: 'untested',
    parameters: {
      'دستور FFplay ویندوز': 'ffplay -rtsp_transport tcp -fflags nobuffer "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1"',
      'دستور VLC ویندوز': 'vlc --rtsp-tcp --network-caching=300 "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1"',
      'تنظیمات کش': '300 میلی‌ثانیه جهت هموارسازی جیتر',
    },
    commands: [
      {
        label: 'اجرای FFplay در ویندوز روی پروتکل TCP',
        description: 'باز کردن پنجره ویدیویی مستقیم با حداقل بافرینگ.',
        command: 'ffplay -rtsp_transport tcp -fflags nobuffer -flags low_delay "rtsp://admin:$CAM_PASS@192.168.1.168:554/1/1"',
        expectedOutputSnippet: 'نمایش پنجره ویدیویی و ثبت مصرف CPU و حافظه در تسک منیجر ویندوز',
      },
    ],
    evidenceRequired: 'پخش ممتد حداقل ۱۰ دقیقه بدون کرش درایور گرافیک یا فریز تصویر.',
  },
];

export const NVR_AUDIT_STEPS: AuditStep[] = [
  {
    id: 'audit-b1-nvr-discovery',
    stageNumber: 1,
    title: 'کشف NVR، مپینگ کانال و پورت RTSP',
    objective: 'کشف IP دستگاه NVR روی شبکه، بررسی پورت RTSP (معمولاً 554) و تعیین شماره کانال دوربین تیاندی روی پورت فیزیکی PoE.',
    method: 'nvr',
    verificationState: 'untested',
    parameters: {
      'آدرس IP فرضی NVR': '192.168.1.200 (نیاز به اسکن)',
      'پورت RTSP': '554 (باید تست شود)',
      'کانال اختصاص یافته': 'کانال ۱ (Channel 1)',
      'وضعیت ایزولاسیون شبکه': 'دوربین روی سوییچ داخلی PoE NVR با ساب‌نت مجزا قرار دارد',
    },
    commands: [
      {
        label: 'تست ارتباط TCP به پورت RTSP دستگاه NVR',
        description: 'بررسی گوش دادن سرویس RTSP روی NVR.',
        command: 'nc -zv 192.168.1.200 554',
        expectedOutputSnippet: 'Connection to 192.168.1.200 port 554 [tcp/rtsp] succeeded!',
      },
    ],
    evidenceRequired: 'برقراری ارتباط TCP و ورود به پنل وب NVR جهت تایید آنلاین بودن کانال.',
  },
  {
    id: 'audit-b2-nvr-streams-probe',
    stageNumber: 2,
    title: 'پروب استریم‌های کانال ۱ NVR بر اساس فرمت /1/1 و /1/2',
    objective: 'بررسی آدرس RTSP کانال ۱ از طریق NVR بر مبنای فرمت مستند /1/1 (اصلی) و /1/2 (فرعی).',
    method: 'nvr',
    verificationState: 'untested',
    parameters: {
      'مسیر استریم اصلی کانال ۱': '/1/1',
      'مسیر استریم فرعی کانال ۱': '/1/2',
      'نوع عبور داده': 'آیا NVR ترنسکود می‌کند یا عبور مستقیم است؟',
    },
    commands: [
      {
        label: 'پروب استریم ۱ از NVR (/1/1)',
        description: 'استخراج پارامترهای استریم خروجی از NVR.',
        command: 'ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,profile,width,height -of json "rtsp://admin:$NVR_PASS@192.168.1.200:554/1/1"',
        expectedOutputSnippet: 'JSON خروجی استریم دریافتی از NVR',
      },
    ],
    evidenceRequired: 'گزارش کدک و رزولوشن خروجی NVR برای مقایسه با اتصال مستقیم.',
  },
  {
    id: 'audit-b3-nvr-ffmpeg-decoding',
    stageNumber: 3,
    title: 'دیکود ۵۰ فریم از استریم NVR با FFmpeg و سنجش جیتر زمانی',
    objective: 'اثبات دیکود واقعی فریم‌ها از طریق NVR و بررسی اینکه مالتی‌پلکسر NVR باعث جابجایی برچسب‌های زمانی نمی‌شود.',
    method: 'nvr',
    verificationState: 'untested',
    parameters: {
      'تعداد فریم': '۵۰ فریم متوالی',
      'بررسی برچسب زمانی': 'بررسی عدم افت فریم یا جیتر زمانی',
    },
    commands: [
      {
        label: 'دیکود ۵۰ فریم از NVR (/1/1)',
        description: 'دیکود کامل استریم خروجی NVR روی پروتکل TCP.',
        command: 'ffmpeg -rtsp_transport tcp -i "rtsp://admin:$NVR_PASS@192.168.1.200:554/1/1" -vframes 50 -f null -',
        expectedOutputSnippet: 'frame=   50 fps=... q=-0.0 Lsize=N/A time=00:00:02.00',
      },
    ],
    evidenceRequired: 'خروجی متوالی ۵۰ فریم دیکود شده بدون خطای پارکت‌های ناقص.',
  },
  {
    id: 'audit-b4-nvr-multiclient-fanout',
    stageNumber: 4,
    title: 'ارزیابی توان خروجی چندکاربره (Fan-out) و ایزولاسیون شبکه',
    objective: 'سنجش اینکه NVR چگونه بار چند کلاینت همزمان را جذب کرده و از پردازنده دوربین محافظت می‌کند.',
    method: 'nvr',
    verificationState: 'untested',
    parameters: {
      'تعداد سشن همزمان': '۴ تا ۱۰ سشن RTSP موازی',
      'تاثیر روی پردازنده دوربین': 'تنها ۱ سشن بالادستی بین دوربین و NVR باز است',
      'ضبط دائم': 'بررسی عدم توقف ضبط هارد NVR در حین تست استریمینگ',
    },
    commands: [
      {
        label: 'تست باز کردن ۵ سشن همزمان به NVR',
        description: 'سنجش پایداری درگاه NVR در پخش به چند مصرف‌کننده.',
        command: 'for i in {1..5}; do ffprobe -rtsp_transport tcp "rtsp://admin:$NVR_PASS@192.168.1.200:554/1/1" & done',
        expectedOutputSnippet: 'تمام سشن‌ها موفق و پایدار برقرار شوند.',
      },
    ],
    evidenceRequired: 'لاگ ثبت وضعیت CPU دستگاه NVR و عدم افت فریم در ضبط محلی.',
  },
];

export const EMPIRICAL_COMPARISON_CRITERIA: ComparisonCriterion[] = [
  {
    criterion: 'تاخیر تصویر واقعی در مرورگر (WebRTC Latency)',
    category: 'latency',
    directState: 'نیاز به سنجش فیزیکی با کرونومتر شیشه‌به‌شیشه (تخمینی ~۲۰۰ میلی‌ثانیه)',
    nvrState: 'نیاز به سنجش فیزیکی (احتمال افزایش جزئی ~۳۰ میلی‌ثانیه ناشی از بافر NVR)',
    verifiedEvidenceStatus: 'نیازمند سنجش عملی در محل',
    technicalAnalysis: 'اتصال مستقیم NVR را دور می‌زند اما باید دید آیا تفاوت زمانی تاخیر برای سناریوی کاربر محسوس است یا خیر.',
  },
  {
    criterion: 'پایداری سشن تحت نوسان شبکه و بار مصرف‌کنندگان',
    category: 'stability',
    directState: 'پردازنده تک‌تراشه‌ای (SoC) دوربین توان محدودی برای هندشیک سشن‌های همزمان دارد.',
    nvrState: 'دستگاه NVR به عنوان بافر سخت‌افزاری عمل کرده و سشن‌های کلاینت‌ها را مجزا مدیریت می‌کند.',
    verifiedEvidenceStatus: 'فرضیه معماری',
    technicalAnalysis: 'توصیه معماری قطعی تا زمان تست استرس با چند کلاینت همزمان صادر نخواهد شد.',
  },
  {
    criterion: 'امنیت شبکه و ایزولاسیون دوربین مداربسته',
    category: 'security',
    directState: 'دوربین به صورت مستقیم روی LAN قرار گرفته و مستقیماً پورت‌های آن در معرض دید شبکه است.',
    nvrState: 'دوربین در پشت پورت‌های سوئیچ PoE داخلی NVR ایزوله است و فقط NVR روی شبکه در دسترس است.',
    verifiedEvidenceStatus: 'تاییدشده بر اساس آزمون',
    technicalAnalysis: 'ایزولاسیون شبکه از منظر امنیت سایبری اینترنت اشیا (IoT Security) مزیت مستند معماری NVR است.',
  },
  {
    criterion: 'تداوم ضبط دائمی ۲۴/۷ بر روی هارددیسک',
    category: 'stability',
    directState: 'استریم فقط به سرور ارسال می‌شود و در صورت قطعی شبکه محلی ویدیو ذخیره نمی‌شود.',
    nvrState: 'دستگاه NVR مستقلاً ویدیو را روی دیسک‌های RAID/SATA ضبط می‌کند، صرف‌نظر از پخش زنده کلاینت‌ها.',
    verifiedEvidenceStatus: 'تاییدشده بر اساس آزمون',
    technicalAnalysis: 'ضبط محلی پیوسته در NVR تضمین مدارک نظارتی است و به ترافیک کلاینت‌های وب وابسته نیست.',
  },
  {
    criterion: 'سازگاری کدک با مرورگر (WebRTC و HLS)',
    category: 'compatibility',
    directState: 'نیازمند استخراج کدک واقعی با ffprobe روی استریم ۱ و ۲.',
    nvrState: 'نیازمند بررسی اینکه آیا NVR فید اصلی را بدون تغییر پاس می‌دهد یا ترنسکود می‌کند.',
    verifiedEvidenceStatus: 'نیازمند سنجش عملی در محل',
    technicalAnalysis: 'هیچ کدکی پیش‌فرض در نظر گرفته نمی‌شود تا نتیجه دستور ffprobe ثبت شود.',
  },
];
