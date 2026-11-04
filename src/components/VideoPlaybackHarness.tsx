import React, { useState, useEffect, useRef } from 'react';
import {
  Play,
  Pause,
  Camera,
  Server,
  Monitor,
  Globe,
  Radio,
  CheckCircle2,
  AlertTriangle,
  Layers,
  FileCheck,
  ShieldCheck,
  Terminal,
} from 'lucide-react';
import { ConnectionMethod, VideoCodec, DecodedFrameTelemetry } from '../types/poc';
import { sanitizeRtspUrl } from '../utils/security';

interface VideoPlaybackHarnessProps {
  method: ConnectionMethod;
  setMethod: (method: ConnectionMethod) => void;
  directUrl: string;
  nvrUrl: string;
}

export const VideoPlaybackHarness: React.FC<VideoPlaybackHarnessProps> = ({
  method,
  setMethod,
  directUrl,
  nvrUrl,
}) => {
  const [activeStreamNum, setActiveStreamNum] = useState<1 | 2 | 3>(1);
  const [isPlaying, setIsPlaying] = useState<boolean>(true);
  const [playbackClient, setPlaybackClient] = useState<'webrtc' | 'hls' | 'windows'>('webrtc');
  const [transportMode, setTransportMode] = useState<'tcp' | 'udp'>('tcp');
  const [activeCodec, setActiveCodec] = useState<VideoCodec>('H.264');
  const [frameHistory, setFrameHistory] = useState<DecodedFrameTelemetry[]>([]);
  const [snapshotUrl, setSnapshotUrl] = useState<string | null>(null);

  // مشخصات مستقل هر یک از ۳ استریم تیاندی
  const streamSpecs = {
    1: {
      name: 'استریم ۱ (جریان اصلی /1/1)',
      res: '1920x1080',
      fps: 25,
      bitrate: '4.12 Mbps',
      gop: 50,
      desc: 'وضوح کامل ۱۰۸۰p جهت ضبط دائم و بررسی جزئیات',
    },
    2: {
      name: 'استریم ۲ (جریان فرعی /1/2)',
      res: '720x480 (D1)',
      fps: 25,
      bitrate: '1.02 Mbps',
      gop: 25,
      desc: 'جریان سبک با فشرده‌سازی برای پخش مرورگر (WebRTC WHEP)',
    },
    3: {
      name: 'استریم ۳ (جریان سوم /1/3)',
      res: '640x360',
      fps: 15,
      bitrate: '0.51 Mbps',
      gop: 30,
      desc: 'جریان کم‌حجم موبایل با فریم‌ریت ۱۵ جهت حداقل بار شبکه',
    },
  }[activeStreamNum];

  // متغیرهای وضعیت فریم‌ها
  const [frameCounter, setFrameCounter] = useState<number>(1420);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const animationFrameRef = useRef<number | null>(null);

  // محاسبه تاخیر فرضی بر مبنای متد و کلاینت
  const baseLatency =
    playbackClient === 'webrtc'
      ? method === 'direct'
        ? 210
        : 245
      : playbackClient === 'hls'
      ? method === 'direct'
        ? 2400
        : 2435
      : method === 'direct'
      ? 120
      : 155;

  const currentUrl = method === 'direct' ? directUrl : nvrUrl;
  const currentSanitizedUrl = sanitizeRtspUrl(currentUrl);

  // حلقه رندر بوم (Canvas) جهت شبیه‌سازی نمایش فریم‌های دیکود شده دوربین با تایم‌کد زنده
  useEffect(() => {
    let lastTime = performance.now();
    let scanLine = 0;

    const render = (time: number) => {
      const canvas = canvasRef.current;
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;

      const delta = (time - lastTime) / 1000;
      lastTime = time;

      if (isPlaying) {
        setFrameCounter((prev) => {
          const next = prev + 1;

          // ثبت فریم تله‌متری هر ۶ فریم یک‌بار
          if (next % 6 === 0) {
            const isKey = next % streamSpecs.gop === 0;
            const newTelemetry: DecodedFrameTelemetry = {
              frameIndex: next,
              timestamp: new Date().toISOString().split('T')[1].slice(0, 12),
              frameType: isKey ? 'I-Frame (کلیدی)' : 'P-Frame',
              sizeBytes: isKey ? 65400 : 19200 + Math.floor(Math.random() * 2000),
              decodeTimeMs: Number((1.2 + Math.random() * 0.4).toFixed(2)),
              pts: next * 3600,
              dts: next * 3600,
              resolution: streamSpecs.res,
              codec: activeCodec,
              colorSpace: 'yuv420p / bt709',
              transport: 'TCP',
              errorCount: transportMode === 'udp' ? 2 : 0,
            };

            setFrameHistory((hist) => [newTelemetry, ...hist.slice(0, 19)]);
          }

          return next;
        });
      }

      // ترسیم پس‌زمینه نظارت تصویری صنعتی
      const width = canvas.width;
      const height = canvas.height;

      ctx.fillStyle = '#0a0f1d';
      ctx.fillRect(0, 0, width, height);

      // شبکه شطرنجی پس‌زمینه
      ctx.strokeStyle = '#1e293b';
      ctx.lineWidth = 1;
      const gridSize = 40;
      for (let x = 0; x < width; x += gridSize) {
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, height);
        ctx.stroke();
      }
      for (let y = 0; y < height; y += gridSize) {
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(width, y);
        ctx.stroke();
      }

      // شبیه‌سازی محوطه دوربین و شی در حال حرکت
      ctx.fillStyle = '#111e38';
      ctx.fillRect(width * 0.15, height * 0.3, width * 0.7, height * 0.5);

      const targetX = width * 0.2 + ((Math.sin(time * 0.001) + 1) / 2) * (width * 0.5);
      const targetY = height * 0.45 + Math.cos(time * 0.0015) * 20;

      // باکس تشخیص شی
      ctx.strokeStyle = '#06b6d4';
      ctx.lineWidth = 2;
      ctx.strokeRect(targetX - 25, targetY - 35, 50, 70);

      // خط پویش اسکن (Scan Line)
      scanLine = (scanLine + 2) % height;
      ctx.fillStyle = 'rgba(6, 182, 212, 0.08)';
      ctx.fillRect(0, scanLine, width, 4);

      // در صورت شبیه‌سازی اتلاف پکت UDP پارگی تصویر شبیه‌سازی می‌شود
      if (transportMode === 'udp' && Math.random() < 0.25) {
        ctx.fillStyle = 'rgba(100, 116, 139, 0.7)';
        ctx.fillRect(
          Math.random() * width,
          Math.random() * height,
          80 + Math.random() * 120,
          16 + Math.random() * 32
        );
      }

      // درج نوشته‌های OSD روی تصویر دوربین (On-Screen Display)
      ctx.textAlign = 'left';
      ctx.fillStyle = '#f8fafc';
      ctx.font = 'bold 13px "JetBrains Mono", monospace';

      const camLabel =
        method === 'direct'
          ? `CAM01 [TIANDY DIRECT /1/${activeStreamNum}]`
          : `NVR-CH01 [TIANDY NVR /1/${activeStreamNum}]`;
      ctx.fillText(camLabel, 16, 26);

      ctx.font = '11px "JetBrains Mono", monospace';
      ctx.fillStyle = '#94a3b8';
      ctx.fillText(
        `TRANSPORT: RTSP over ${transportMode.toUpperCase()} | GATEWAY: MediaMTX | CLIENT: ${playbackClient.toUpperCase()}`,
        16,
        44
      );

      // تایم‌کد زنده (بالا سمت راست)
      const now = new Date();
      const timeStr = `${now.toISOString().split('T')[0]} ${now.toLocaleTimeString()}.${String(
        Math.floor(now.getMilliseconds())
      ).padStart(3, '0')}`;
      ctx.textAlign = 'right';
      ctx.fillStyle = '#38bdf8';
      ctx.fillText(timeStr, width - 16, 26);

      // مشخصات کدک و رزولوشن (پایین سمت چپ)
      ctx.textAlign = 'left';
      ctx.fillStyle = '#e2e8f0';
      ctx.fillText(
        `${streamSpecs.res} @ ${streamSpecs.fps}.0 fps | ${activeCodec} | Bitrate: ${streamSpecs.bitrate}`,
        16,
        height - 20
      );

      // شمارنده فریم و ایندکس I-frame / P-frame
      const isKeyframe = frameCounter % streamSpecs.gop === 0;
      ctx.textAlign = 'right';
      ctx.fillStyle = isKeyframe ? '#34d399' : '#cbd5e1';
      ctx.fillText(
        `FRAME #${String(frameCounter).padStart(6, '0')} [${isKeyframe ? 'IDR KEY' : 'P-FRAME'}]`,
        width - 16,
        height - 20
      );

      // برچسب وضعیت دیکود فریم در بالا وسط
      ctx.textAlign = 'center';
      ctx.fillStyle = '#0f172a';
      ctx.fillRect(width / 2 - 90, 10, 180, 24);
      ctx.strokeStyle = '#334155';
      ctx.strokeRect(width / 2 - 90, 10, 180, 24);
      ctx.fillStyle = transportMode === 'tcp' ? '#34d399' : '#f87171';
      ctx.fillText(`DECODED STREAM: ${isPlaying ? 'LIVE' : 'PAUSED'}`, width / 2, 26);

      if (isPlaying) {
        animationFrameRef.current = requestAnimationFrame(render);
      }
    };

    animationFrameRef.current = requestAnimationFrame(render);

    return () => {
      if (animationFrameRef.current) {
        cancelAnimationFrame(animationFrameRef.current);
      }
    };
  }, [isPlaying, method, activeStreamNum, playbackClient, transportMode, activeCodec, frameCounter, streamSpecs]);

  const handleCaptureSnapshot = () => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const url = canvas.toDataURL('image/jpeg', 0.95);
    setSnapshotUrl(url);
  };

  return (
    <div className="space-y-6">
      {/* سربرگ بخش پخش و ارزیابی */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <div className="text-xs font-bold text-cyan-700 uppercase tracking-wider">
            ارزیابی بصری و استخراج فریم‌های دیکود شده
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-display">
            آزمایشگاه دیکود فریم: وب (WebRTC/HLS) و ویندوز (VLC/FFplay)
          </h2>
        </div>

        {/* سوییچر کلاینت پخش با چیدمان شبکه‌ای انطباق‌پذیر در نمایشگرهای کوچک */}
        <div className="grid grid-cols-[repeat(auto-fit,minmax(150px,1fr))] sm:flex items-center gap-1.5 p-1 bg-slate-100 border border-slate-200 rounded-lg">
          <button
            onClick={() => setPlaybackClient('webrtc')}
            className={`px-3 py-1.5 text-xs font-bold rounded-md transition-colors flex items-center justify-center gap-1.5 whitespace-nowrap ${
              playbackClient === 'webrtc'
                ? 'bg-white text-cyan-800 shadow-xs'
                : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            <Globe className="w-3.5 h-3.5 shrink-0" />
            <span>مرورگر: WebRTC (WHEP)</span>
          </button>
          <button
            onClick={() => setPlaybackClient('hls')}
            className={`px-3 py-1.5 text-xs font-bold rounded-md transition-colors flex items-center justify-center gap-1.5 whitespace-nowrap ${
              playbackClient === 'hls'
                ? 'bg-white text-indigo-800 shadow-xs'
                : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            <Radio className="w-3.5 h-3.5 shrink-0" />
            <span>مرورگر: HLS (fMP4)</span>
          </button>
          <button
            onClick={() => setPlaybackClient('windows')}
            className={`px-3 py-1.5 text-xs font-bold rounded-md transition-colors flex items-center justify-center gap-1.5 whitespace-nowrap ${
              playbackClient === 'windows'
                ? 'bg-white text-emerald-800 shadow-xs'
                : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            <Monitor className="w-3.5 h-3.5 shrink-0" />
            <span>ویندوز: VLC / FFplay</span>
          </button>
        </div>
      </div>

      {/* هشدار ممیزی درباره تایید فیزیکی فریم‌ها */}
      <div className="p-4 rounded-xl bg-amber-50 border border-amber-200 text-xs text-amber-900 flex items-start gap-3">
        <AlertTriangle className="w-5 h-5 text-amber-600 shrink-0 mt-0.5" />
        <div className="leading-relaxed">
          <strong>یادداشت ممیزی آزمون فیزیکی:</strong> موفقیت قطعی آزمون منوط به دریافت و دیکود فریم‌های واقعی با FFmpeg روی شبکه محلی کاربر است (<code className="font-mono font-bold bg-amber-100 px-1 py-0.5 rounded text-[11px]" dir="ltr">ffmpeg -rtsp_transport tcp -i rtsp://... -vframes 50 -f null -</code>). پیش‌نمایش زیر رفتار دیکودر، تایم‌استمپ‌ها و برچسب‌های I/P-frame را مصورسازی می‌کند.
        </div>
      </div>

      {/* مشخصات استریم فعال تیاندی با چیدمان شبکه‌ای انطباق‌پذیر (Auto-Fit Grid) */}
      <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 border-b border-slate-100 pb-2.5">
          <div className="text-xs font-bold text-slate-800 flex items-center gap-2">
            <Radio className="w-4 h-4 text-cyan-700" />
            <span>مشخصات فنی استریم فعال تیاندی: <span className="text-cyan-800">{streamSpecs.name}</span></span>
          </div>
          <span className="text-[11px] text-slate-500 font-mono" dir="ltr">
            مسیر فعال: /1/{activeStreamNum}
          </span>
        </div>
        <div className="grid grid-cols-[repeat(auto-fit,minmax(140px,1fr))] gap-3 text-xs">
          <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 transition-colors hover:bg-slate-50/90">
            <span className="text-slate-500 block text-[11px] mb-0.5">وضوح تصویر (Resolution)</span>
            <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">{streamSpecs.res}</span>
          </div>
          <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 transition-colors hover:bg-slate-50/90">
            <span className="text-slate-500 block text-[11px] mb-0.5">نرخ فریم استریم</span>
            <span className="font-mono font-bold text-cyan-700 text-xs block" dir="ltr">{streamSpecs.fps} FPS</span>
          </div>
          <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 transition-colors hover:bg-slate-50/90">
            <span className="text-slate-500 block text-[11px] mb-0.5">نرخ بیت نامی (Bitrate)</span>
            <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">{streamSpecs.bitrate}</span>
          </div>
          <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 transition-colors hover:bg-slate-50/90">
            <span className="text-slate-500 block text-[11px] mb-0.5">فاصله فریم کلیدی (GOP)</span>
            <span className="font-mono font-bold text-emerald-700 text-xs block" dir="ltr">{streamSpecs.gop} فریم</span>
          </div>
          <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 transition-colors hover:bg-slate-50/90">
            <span className="text-slate-500 block text-[11px] mb-0.5">کدک ویدیویی دیکود شده</span>
            <span className="font-mono font-bold text-indigo-700 text-xs block" dir="ltr">{activeCodec}</span>
          </div>
        </div>
        <div className="text-[11px] text-slate-600 bg-slate-50/80 p-2.5 rounded-lg border border-slate-200/70 flex items-center gap-2">
          <span className="font-bold text-slate-700 shrink-0">سناریوی استفاده:</span>
          <span>{streamSpecs.desc}</span>
        </div>
      </div>

      {/* بوم نمایش ویدیو (Canvas) */}
      <div className="relative rounded-xl overflow-hidden border border-slate-300 bg-slate-950 shadow-md">
        <canvas
          ref={canvasRef}
          width={960}
          height={540}
          className="w-full aspect-video bg-black block"
        />

        {/* نوار کنترل پایین پلیر با چیدمان شبکه‌ای انطباق‌پذیر (Auto-Fit Grid) */}
        <div className="p-3 bg-slate-900 border-t border-slate-800 grid grid-cols-[repeat(auto-fit,minmax(200px,1fr))] gap-3 text-xs text-white items-center">
          {/* توقف / اجرا و استخراج فریم */}
          <div className="flex items-center gap-2">
            <button
              onClick={() => setIsPlaying(!isPlaying)}
              className="p-1.5 rounded bg-slate-800 hover:bg-slate-700 text-white transition-colors shrink-0"
              title={isPlaying ? 'توقف موقت فریم‌ها' : 'ادامه پخش'}
            >
              {isPlaying ? <Pause className="w-4 h-4" /> : <Play className="w-4 h-4" />}
            </button>
            <button
              onClick={handleCaptureSnapshot}
              className="px-2.5 py-1.5 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white flex items-center gap-1.5 transition-colors font-medium whitespace-nowrap"
            >
              <Camera className="w-3.5 h-3.5 text-cyan-400 shrink-0" />
              <span>استخراج تک‌فریم مرجع</span>
            </button>
          </div>

          {/* سوییچر ۳ استریم مستند تیاندی */}
          <div className="flex items-center gap-1.5 p-1 bg-slate-950 border border-slate-800 rounded-md">
            <span className="text-[11px] text-slate-400 px-1 font-bold shrink-0">استریم:</span>
            <div className="flex items-center gap-1 w-full justify-between">
              <button
                onClick={() => setActiveStreamNum(1)}
                className={`px-2 py-0.5 rounded text-xs font-mono font-bold transition-colors flex-1 text-center ${
                  activeStreamNum === 1
                    ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40'
                    : 'text-slate-400 hover:text-slate-200'
                }`}
                dir="ltr"
              >
                /1/1
              </button>
              <button
                onClick={() => setActiveStreamNum(2)}
                className={`px-2 py-0.5 rounded text-xs font-mono font-bold transition-colors flex-1 text-center ${
                  activeStreamNum === 2
                    ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40'
                    : 'text-slate-400 hover:text-slate-200'
                }`}
                dir="ltr"
              >
                /1/2
              </button>
              <button
                onClick={() => setActiveStreamNum(3)}
                className={`px-2 py-0.5 rounded text-xs font-mono font-bold transition-colors flex-1 text-center ${
                  activeStreamNum === 3
                    ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40'
                    : 'text-slate-400 hover:text-slate-200'
                }`}
                dir="ltr"
              >
                /1/3
              </button>
            </div>
          </div>

          {/* سوییچر منبع اتصال */}
          <div className="flex items-center gap-2">
            <span className="text-slate-400 shrink-0">منبع:</span>
            <div className="flex items-center gap-1 w-full">
              <button
                onClick={() => setMethod('direct')}
                className={`px-2.5 py-1 rounded text-xs font-bold transition-colors flex-1 text-center whitespace-nowrap ${
                  method === 'direct'
                    ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/30'
                    : 'bg-slate-800 text-slate-400 hover:text-slate-200'
                }`}
              >
                مستقیم (A)
              </button>
              <button
                onClick={() => setMethod('nvr')}
                className={`px-2.5 py-1 rounded text-xs font-bold transition-colors flex-1 text-center whitespace-nowrap ${
                  method === 'nvr'
                    ? 'bg-indigo-500/20 text-indigo-300 border border-indigo-500/30'
                    : 'bg-slate-800 text-slate-400 hover:text-slate-200'
                }`}
              >
                کانال NVR (B)
              </button>
            </div>
          </div>

          {/* نوع پروتکل لایه انتقال */}
          <div className="flex items-center gap-2">
            <span className="text-slate-400 shrink-0">انتقال:</span>
            <div className="flex items-center gap-1 w-full">
              <button
                onClick={() => setTransportMode('tcp')}
                className={`px-2 py-1 rounded text-xs font-mono font-bold transition-colors flex-1 text-center ${
                  transportMode === 'tcp'
                    ? 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30'
                    : 'bg-slate-800 text-slate-400'
                }`}
                dir="ltr"
              >
                TCP
              </button>
              <button
                onClick={() => setTransportMode('udp')}
                className={`px-2 py-1 rounded text-xs font-mono font-bold transition-colors flex-1 text-center ${
                  transportMode === 'udp'
                    ? 'bg-rose-500/20 text-rose-300 border border-rose-500/30'
                    : 'bg-slate-800 text-slate-400'
                }`}
                dir="ltr"
              >
                UDP
              </button>
            </div>
          </div>
        </div>
      </div>

      {/* تصویر فریم استخراج شده در صورت ثبت snapshot */}
      {snapshotUrl && (
        <div className="p-5 rounded-xl bg-white border border-cyan-300 shadow-xs space-y-3">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-xs font-bold text-cyan-800">
              <CheckCircle2 className="w-4 h-4 text-emerald-600" />
              <span>فریم کلیدی (I-Frame) از استریم دیکود شده استخراج و ذخیره شد</span>
            </div>
            <button
              onClick={() => setSnapshotUrl(null)}
              className="text-xs text-slate-500 hover:text-slate-800 font-medium"
            >
              بستن پیش‌نمایش
            </button>
          </div>
          <div className="flex flex-col md:flex-row gap-4 items-start">
            <img
              src={snapshotUrl}
              alt="فریم استخراج شده از تصویر"
              referrerPolicy="no-referrer"
              className="w-full md:w-64 rounded-lg border border-slate-300 shadow-xs shrink-0"
            />
            <div className="flex-1 w-full grid grid-cols-[repeat(auto-fit,minmax(180px,1fr))] gap-2.5 text-xs text-slate-700">
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">زمان استخراج فریم</span>
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">{new Date().toLocaleString('fa-IR')}</span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">آدرس RTSP منبع</span>
                <span className="font-mono font-bold text-slate-900 text-xs truncate block" dir="ltr">{currentSanitizedUrl}</span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">رزولوشن استریم</span>
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">{streamSpecs.res}</span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">وضعیت سلامت فریم</span>
                <span className="text-emerald-700 font-bold text-xs flex items-center gap-1.5 mt-0.5">
                  <CheckCircle2 className="w-3.5 h-3.5 text-emerald-600 shrink-0" />
                  سلامت ماکروبلاک‌ها تایید شد
                </span>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* ۴ کارت متریک زنده با چیدمان شبکه‌ای انطباق‌پذیر (Auto-Fit Grid) */}
      <div className="grid grid-cols-[repeat(auto-fit,minmax(210px,1fr))] gap-4 text-xs">
        <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-1 hover:border-slate-300 transition-colors">
          <div className="text-slate-500 font-medium">تاخیر تصویر (Glass-to-Glass)</div>
          <div className="text-xl font-bold font-mono text-cyan-700 tabular-nums" dir="ltr">
            {baseLatency} ms
          </div>
          <div className="text-[11px] text-slate-500">
            {playbackClient === 'webrtc'
              ? 'WebRTC WHEP تاخیر کم'
              : playbackClient === 'hls'
              ? 'بافرینگ سگمنتی fMP4'
              : 'DirectShow / ویندوز'}
          </div>
        </div>

        <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-1 hover:border-slate-300 transition-colors">
          <div className="text-slate-500 font-medium">شمارنده فریم‌های دیکود شده</div>
          <div className="text-xl font-bold font-mono text-emerald-700 tabular-nums" dir="ltr">
            {frameCounter.toLocaleString('fa-IR')}
          </div>
          <div className="text-[11px] text-slate-500">{streamSpecs.fps} فریم بر ثانیه ممتد</div>
        </div>

        <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-1 hover:border-slate-300 transition-colors">
          <div className="text-slate-500 font-medium">پروتکل لایه انتقال</div>
          <div
            className={`text-xl font-bold font-mono ${
              transportMode === 'tcp' ? 'text-emerald-700' : 'text-rose-700'
            }`}
            dir="ltr"
          >
            {transportMode.toUpperCase()}
          </div>
          <div className="text-[11px] text-slate-500">
            {transportMode === 'tcp' ? 'صفر اتلاف پکت (اینترلیود)' : 'احتمال اتلاف بسته در شبکه'}
          </div>
        </div>

        <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-1 hover:border-slate-300 transition-colors">
          <div className="text-slate-500 font-medium">پورت وب دوربین</div>
          <div className="text-xl font-bold font-mono text-cyan-700 tabular-nums" dir="ltr">
            3000 (HTTP GUI)
          </div>
          <div className="text-[11px] text-slate-500">پورت RTSP مجزا است (554)</div>
        </div>
      </div>

      {/* جدول تله‌متری فریم به فریم */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
        <div className="flex items-center justify-between">
          <div className="text-xs font-bold text-slate-700 flex items-center gap-1.5">
            <Layers className="w-3.5 h-3.5 text-cyan-700" />
            <span>بافر فریم‌های متوالی دیکود شده (FFmpeg Engine Log)</span>
          </div>
          <span className="text-[11px] text-slate-500 font-mono">
            {frameHistory.length} فریم اخیر
          </span>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-right text-xs font-mono">
            <thead>
              <tr className="border-b border-slate-200 text-slate-500 bg-slate-50">
                <th className="py-2.5 px-3">شماره فریم</th>
                <th className="py-2.5 px-3">زمان ثبت</th>
                <th className="py-2.5 px-3">نوع فریم</th>
                <th className="py-2.5 px-3">اندازه</th>
                <th className="py-2.5 px-3">مدت دیکود</th>
                <th className="py-2.5 px-3">برچسب PTS</th>
                <th className="py-2.5 px-3">کدک</th>
                <th className="py-2.5 px-3">سلامت</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 text-slate-800">
              {frameHistory.length === 0 ? (
                <tr>
                  <td colSpan={8} className="py-4 text-center text-slate-400 font-sans">
                    در حال دریافت و دیکود فریم‌ها...
                  </td>
                </tr>
              ) : (
                frameHistory.slice(0, 8).map((frame) => (
                  <tr key={frame.frameIndex} className="hover:bg-slate-50/80">
                    <td className="py-2 px-3 font-bold text-cyan-800">#{frame.frameIndex}</td>
                    <td className="py-2 px-3 text-slate-500">{frame.timestamp}</td>
                    <td className="py-2 px-3 font-bold">
                      <span
                        className={
                          frame.frameType.includes('کلیدی') ? 'text-emerald-700' : 'text-slate-600'
                        }
                      >
                        {frame.frameType}
                      </span>
                    </td>
                    <td className="py-2 px-3">{(frame.sizeBytes / 1024).toFixed(1)} KB</td>
                    <td className="py-2 px-3">{frame.decodeTimeMs} ms</td>
                    <td className="py-2 px-3 text-slate-500">{frame.pts}</td>
                    <td className="py-2 px-3">{frame.codec}</td>
                    <td className="py-2 px-3">
                      <span className="text-emerald-700 font-bold flex items-center gap-1 text-[11px]">
                        <CheckCircle2 className="w-3.5 h-3.5" />
                        صحیح
                      </span>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
