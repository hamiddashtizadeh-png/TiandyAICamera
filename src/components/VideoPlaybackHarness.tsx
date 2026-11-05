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
  ScanFace,
  Maximize2,
  Minimize2,
  ZoomIn,
  Car,
  PanelRightClose,
  PanelRightOpen,
  Sparkles,
} from 'lucide-react';
import { ConnectionMethod, VideoCodec, DecodedFrameTelemetry } from '../types/poc';
import { DetectedFaceEvent, FaceDetectionSettings } from '../types/faceDetection';
import { sanitizeRtspUrl } from '../utils/security';
import { FaceDetectionPanel } from './FaceDetectionPanel';

interface VideoPlaybackHarnessProps {
  method: ConnectionMethod;
  setMethod: (method: ConnectionMethod) => void;
  directUrl: string;
  nvrUrl: string;
}

interface ContextMenuState {
  visible: boolean;
  x: number;
  y: number;
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

  // وضعیت و تنظیمات پیشرفته پنل تشخیص چهره
  const [isFacePanelOpen, setIsFacePanelOpen] = useState<boolean>(true);
  const [faceSettings, setFaceSettings] = useState<FaceDetectionSettings>({
    enabled: true,
    showOverlay: true,
    showConfidenceLabel: true,
    minConfidence: 75,
    autoCaptureIntervalMs: 3200,
    anprEnabled: false,
    digitalZoom: 1.0,
  });

  // فهرست رویدادهای چهره ثبت‌شده در زمان واقعی با تصاویر بندانگشتی و متادیتا
  const [detectedFaces, setDetectedFaces] = useState<DetectedFaceEvent[]>([
    {
      id: 'face-evt-1',
      timestamp: '14:28:12.430',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [استریم ۱/۱]',
      confidence: 96.4,
      box: { x: 0.35, y: 0.42, width: 0.08, height: 0.14 },
      trackId: 104,
      qualityScore: 95,
      pose: 'روبرو (Frontal)',
    },
    {
      id: 'face-evt-2',
      timestamp: '14:28:07.120',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [استریم ۱/۱]',
      confidence: 92.1,
      box: { x: 0.48, y: 0.45, width: 0.08, height: 0.14 },
      trackId: 105,
      qualityScore: 89,
      pose: 'زاویه‌دار (Slight Tilt)',
    },
    {
      id: 'face-evt-3',
      timestamp: '14:27:58.850',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [استریم ۱/۱]',
      confidence: 94.8,
      box: { x: 0.22, y: 0.40, width: 0.08, height: 0.14 },
      trackId: 103,
      qualityScore: 92,
      pose: 'روبرو (Frontal)',
    },
  ]);

  // موقعیت شناور منوی راست‌کلیک
  const [contextMenu, setContextMenu] = useState<ContextMenuState>({
    visible: false,
    x: 0,
    y: 0,
  });

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

  // متغیرهای وضعیت فریم‌ها و رفرنس بوم‌ها
  const [frameCounter, setFrameCounter] = useState<number>(1420);
  const videoCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const overlayCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const playerContainerRef = useRef<HTMLDivElement | null>(null);
  const videoAnimationRef = useRef<number | null>(null);
  const overlayAnimationRef = useRef<number | null>(null);

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

  // بستن منوی راست کلیک با کلیک در هر نقطه دیگر
  useEffect(() => {
    const handleClickOutside = () => {
      if (contextMenu.visible) {
        setContextMenu((prev) => ({ ...prev, visible: false }));
      }
    };
    window.addEventListener('click', handleClickOutside);
    return () => window.removeEventListener('click', handleClickOutside);
  }, [contextMenu.visible]);

  // ۱. رهگیری خودکار و شبیه‌سازی ورود رویدادهای چهره در زمان پخش زنده
  useEffect(() => {
    if (!isPlaying || !faceSettings.enabled) return;

    const interval = setInterval(() => {
      const canvas = videoCanvasRef.current;
      let cropUrl: string | undefined;

      // در صورت امکان، یک کراپ کوچک با ابعاد ۱۱۰x۱۱۰ از بوم ویدیو بدون افت سرعت استخراج می‌شود
      if (canvas) {
        try {
          const offscreen = document.createElement('canvas');
          offscreen.width = 110;
          offscreen.height = 110;
          const offCtx = offscreen.getContext('2d');
          if (offCtx) {
            offCtx.drawImage(
              canvas,
              canvas.width * 0.25,
              canvas.height * 0.25,
              canvas.width * 0.45,
              canvas.height * 0.45,
              0,
              0,
              110,
              110
            );
            cropUrl = offscreen.toDataURL('image/jpeg', 0.85);
          }
        } catch {
          // در صورت بروز محدودیت به آیکون برداری باز می‌گردد
        }
      }

      const now = new Date();
      const randomTrack = Math.floor(100 + Math.random() * 900);
      const conf = Number((89 + Math.random() * 10).toFixed(1));

      const newEvt: DetectedFaceEvent = {
        id: `face-${Date.now()}-${randomTrack}`,
        timestamp: `${now.toTimeString().split(' ')[0]}.${String(
          Math.floor(now.getMilliseconds())
        ).padStart(3, '0')}`,
        date: now.toISOString().split('T')[0],
        channelId: activeStreamNum,
        channelName:
          method === 'direct'
            ? `CAM01 [استریم ۱/${activeStreamNum}]`
            : `NVR-CH01 [استریم ۱/${activeStreamNum}]`,
        confidence: conf,
        box: { x: 0.35, y: 0.42, width: 0.08, height: 0.14 },
        thumbnailUrl: cropUrl,
        trackId: randomTrack,
        qualityScore: Math.floor(88 + Math.random() * 11),
        pose: Math.random() > 0.35 ? 'روبرو (Frontal)' : 'زاویه‌دار (Slight Tilt)',
      };

      setDetectedFaces((prev) => [newEvt, ...prev.slice(0, 29)]);
    }, faceSettings.autoCaptureIntervalMs);

    return () => clearInterval(interval);
  }, [
    isPlaying,
    faceSettings.enabled,
    faceSettings.autoCaptureIntervalMs,
    activeStreamNum,
    method,
  ]);

  // ۲. حلقه رندر بوم پایه ویدیو (Video Canvas) - مستقل از لایه هوش مصنوعی برای بالاترین عملکرد دیکود
  useEffect(() => {
    let scanLine = 0;

    const renderVideo = (time: number) => {
      const canvas = videoCanvasRef.current;
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;

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

      // تنظیم بزرگ‌نمایی دیجیتال
      const zoom = faceSettings.digitalZoom;
      const width = canvas.width;
      const height = canvas.height;

      ctx.save();
      if (zoom > 1.0) {
        ctx.translate(width / 2, height / 2);
        ctx.scale(zoom, zoom);
        ctx.translate(-width / 2, -height / 2);
      }

      // ترسیم پس‌زمینه نظارت تصویری صنعتی
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

      // شبیه‌سازی محوطه دید دوربین
      ctx.fillStyle = '#111e38';
      ctx.fillRect(width * 0.15, height * 0.3, width * 0.7, height * 0.5);

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
        `TRANSPORT: RTSP over ${transportMode.toUpperCase()} | AI: ${
          faceSettings.enabled ? 'FACE_DETECTION ON' : 'DISABLED'
        } | ZOOM: ${zoom.toFixed(1)}x`,
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
      ctx.fillRect(width / 2 - 95, 10, 190, 24);
      ctx.strokeStyle = '#334155';
      ctx.strokeRect(width / 2 - 95, 10, 190, 24);
      ctx.fillStyle = transportMode === 'tcp' ? '#34d399' : '#f87171';
      ctx.fillText(`DECODED STREAM: ${isPlaying ? 'LIVE' : 'PAUSED'}`, width / 2, 26);

      ctx.restore();

      if (isPlaying) {
        videoAnimationRef.current = requestAnimationFrame(renderVideo);
      }
    };

    videoAnimationRef.current = requestAnimationFrame(renderVideo);

    return () => {
      if (videoAnimationRef.current) {
        cancelAnimationFrame(videoAnimationRef.current);
      }
    };
  }, [
    isPlaying,
    method,
    activeStreamNum,
    playbackClient,
    transportMode,
    activeCodec,
    frameCounter,
    streamSpecs,
    faceSettings.digitalZoom,
    faceSettings.enabled,
  ]);

  // ۳. حلقه اختصاصی بوم اورلی (Overlay Canvas) - کادر سبز دور چهره کاملاً تفکیک‌شده برای عملکرد بی‌نقص استریم
  useEffect(() => {
    const overlayCanvas = overlayCanvasRef.current;
    if (!overlayCanvas) return;
    const ctx = overlayCanvas.getContext('2d');
    if (!ctx) return;

    // در صورت غیرفعال بودن تشخیص چهره یا کادر، بوم اورلی بلافاصله پاک شده و پردازش متوقف می‌شود
    if (!faceSettings.enabled || !faceSettings.showOverlay) {
      ctx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);
      if (overlayAnimationRef.current) {
        cancelAnimationFrame(overlayAnimationRef.current);
        overlayAnimationRef.current = null;
      }
      return;
    }

    const renderOverlay = (time: number) => {
      const width = overlayCanvas.width;
      const height = overlayCanvas.height;

      // پاک‌سازی فریم اورلی بدون دستکاری بافر ویدیوی اصلی
      ctx.clearRect(0, 0, width, height);

      const zoom = faceSettings.digitalZoom;
      ctx.save();
      if (zoom > 1.0) {
        ctx.translate(width / 2, height / 2);
        ctx.scale(zoom, zoom);
        ctx.translate(-width / 2, -height / 2);
      }

      // محاسبه موقعیت چهره هدف متحرک
      const targetX = width * 0.2 + ((Math.sin(time * 0.001) + 1) / 2) * (width * 0.5);
      const targetY = height * 0.45 + Math.cos(time * 0.0015) * 20;

      const faceW = 82;
      const faceH = 104;
      const faceX = targetX - faceW / 2;
      const faceY = targetY - faceH / 2;

      // ۱. کادر اصلی سبز رنگ با درخشش ملایم (#22c55e)
      ctx.strokeStyle = '#22c55e';
      ctx.lineWidth = 2.5;
      ctx.shadowColor = 'rgba(34, 197, 94, 0.45)';
      ctx.shadowBlur = 8;
      ctx.strokeRect(faceX, faceY, faceW, faceH);

      // ۲. براکت‌های رتیکل گوشه‌ای دوربین‌های نظارتی
      const bLen = 16;
      ctx.lineWidth = 3.5;
      ctx.strokeStyle = '#4ade80';

      // گوشه بالا-چپ
      ctx.beginPath();
      ctx.moveTo(faceX, faceY + bLen);
      ctx.lineTo(faceX, faceY);
      ctx.lineTo(faceX + bLen, faceY);
      ctx.stroke();

      // گوشه بالا-راست
      ctx.beginPath();
      ctx.moveTo(faceX + faceW - bLen, faceY);
      ctx.lineTo(faceX + faceW, faceY);
      ctx.lineTo(faceX + faceW, faceY + bLen);
      ctx.stroke();

      // گوشه پایین-چپ
      ctx.beginPath();
      ctx.moveTo(faceX, faceY + faceH - bLen);
      ctx.lineTo(faceX, faceY + faceH);
      ctx.lineTo(faceX + bLen, faceY + faceH);
      ctx.stroke();

      // گوشه پایین-راست
      ctx.beginPath();
      ctx.moveTo(faceX + faceW - bLen, faceY + faceH);
      ctx.lineTo(faceX + faceW, faceY + faceH);
      ctx.lineTo(faceX + faceW, faceY + faceH - bLen);
      ctx.stroke();

      // ۳. پرچم سبز شفاف داخل کادر
      ctx.fillStyle = 'rgba(34, 197, 94, 0.06)';
      ctx.fillRect(faceX, faceY, faceW, faceH);

      // ۴. برچسب مشخصات و درصد اطمینان بالای کادر سبز
      if (faceSettings.showConfidenceLabel) {
        const confidenceText = 'FACE #104 [96.4%]';
        ctx.font = 'bold 11px "JetBrains Mono", monospace';
        const textMetrics = ctx.measureText(confidenceText);
        const badgeWidth = textMetrics.width + 16;
        const badgeHeight = 22;

        ctx.fillStyle = 'rgba(10, 15, 29, 0.9)';
        ctx.fillRect(faceX, faceY - badgeHeight - 4, badgeWidth, badgeHeight);

        ctx.strokeStyle = '#22c55e';
        ctx.lineWidth = 1;
        ctx.strokeRect(faceX, faceY - badgeHeight - 4, badgeWidth, badgeHeight);

        ctx.fillStyle = '#22c55e';
        ctx.textAlign = 'left';
        ctx.fillText(confidenceText, faceX + 8, faceY - 10);
      }

      // ۵. نقاط لندمارک بیومتریک چشم‌ها، بینی و دهان به رنگ سبز نئونی
      ctx.fillStyle = '#86efac';
      // چشم چپ و راست
      ctx.beginPath();
      ctx.arc(faceX + faceW * 0.33, faceY + faceH * 0.36, 2.5, 0, Math.PI * 2);
      ctx.fill();
      ctx.beginPath();
      ctx.arc(faceX + faceW * 0.67, faceY + faceH * 0.36, 2.5, 0, Math.PI * 2);
      ctx.fill();
      // بینی
      ctx.beginPath();
      ctx.arc(faceX + faceW * 0.5, faceY + faceH * 0.52, 2, 0, Math.PI * 2);
      ctx.fill();
      // دهان
      ctx.beginPath();
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = '#86efac';
      ctx.arc(faceX + faceW * 0.5, faceY + faceH * 0.68, 6, 0.2, Math.PI - 0.2);
      ctx.stroke();

      // پلاک‌خوان خودکار ANPR در صورت فعال بودن
      if (faceSettings.anprEnabled) {
        const plateW = 120;
        const plateH = 34;
        const plateX = targetX - plateW / 2;
        const plateY = targetY + 70;

        ctx.fillStyle = 'rgba(15, 23, 42, 0.9)';
        ctx.fillRect(plateX, plateY, plateW, plateH);
        ctx.strokeStyle = '#38bdf8';
        ctx.lineWidth = 1.5;
        ctx.strokeRect(plateX, plateY, plateW, plateH);

        ctx.fillStyle = '#38bdf8';
        ctx.font = 'bold 11px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        ctx.fillText('PLATE: 24 ب 791', plateX + plateW / 2, plateY + 16);
        ctx.font = '9px "JetBrains Mono", monospace';
        ctx.fillStyle = '#94a3b8';
        ctx.fillText('ANPR CONF: 98.2%', plateX + plateW / 2, plateY + 28);
      }

      ctx.restore();

      if (isPlaying) {
        overlayAnimationRef.current = requestAnimationFrame(renderOverlay);
      }
    };

    overlayAnimationRef.current = requestAnimationFrame(renderOverlay);

    return () => {
      if (overlayAnimationRef.current) {
        cancelAnimationFrame(overlayAnimationRef.current);
      }
    };
  }, [isPlaying, faceSettings]);

  // ثبت و استخراج عکس تک‌فریم
  const handleCaptureSnapshot = () => {
    const canvas = videoCanvasRef.current;
    if (!canvas) return;
    const url = canvas.toDataURL('image/jpeg', 0.95);
    setSnapshotUrl(url);

    // افزودن فوری رویداد چهره به پنل کناری
    if (faceSettings.enabled) {
      const now = new Date();
      const newEvt: DetectedFaceEvent = {
        id: `face-manual-${Date.now()}`,
        timestamp: `${now.toTimeString().split(' ')[0]}.${String(
          Math.floor(now.getMilliseconds())
        ).padStart(3, '0')}`,
        date: now.toISOString().split('T')[0],
        channelId: activeStreamNum,
        channelName:
          method === 'direct'
            ? `CAM01 [استخراج دستی فریم]`
            : `NVR-CH01 [استخراج دستی فریم]`,
        confidence: 97.8,
        box: { x: 0.35, y: 0.42, width: 0.08, height: 0.14 },
        thumbnailUrl: url,
        trackId: Math.floor(100 + Math.random() * 900),
        qualityScore: 98,
        pose: 'روبرو (Frontal)',
      };
      setDetectedFaces((prev) => [newEvt, ...prev]);
    }
  };

  // قابلیت تمام صفحه با دوبار کلیک روی کانال تصویر
  const handleToggleFullscreen = () => {
    const container = playerContainerRef.current;
    if (!container) return;

    if (!document.fullscreenElement) {
      container.requestFullscreen().catch(() => {});
    } else {
      document.exitFullscreen().catch(() => {});
    }
  };

  // باز کردن منوی راست‌کلیک شناور
  const handleContextMenu = (e: React.MouseEvent) => {
    e.preventDefault();
    const rect = playerContainerRef.current?.getBoundingClientRect();
    if (rect) {
      setContextMenu({
        visible: true,
        x: e.clientX,
        y: e.clientY,
      });
    }
  };

  return (
    <div className="space-y-6">
      {/* سربرگ بخش پخش و ارزیابی */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <div className="text-xs font-bold text-cyan-700 uppercase tracking-wider flex items-center gap-1.5">
            <Sparkles className="w-3.5 h-3.5" />
            <span>پخش زنده، تشخیص چهره و استخراج فریم</span>
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-display">
            آزمایشگاه دیکود فریم: وب (WebRTC/HLS) و نظارت بیومتریک هوشمند
          </h2>
        </div>

        {/* دکمه‌های کنترل سوییچر کلاینت و تاگل پنل چهره */}
        <div className="flex flex-wrap items-center gap-2">
          {/* دکمه باز و بسته کردن پنل تشخیص چهره */}
          <button
            onClick={() => setIsFacePanelOpen(!isFacePanelOpen)}
            className={`px-3 py-1.5 text-xs font-bold rounded-lg border transition-colors flex items-center gap-1.5 shadow-2xs ${
              isFacePanelOpen
                ? 'bg-emerald-50 text-emerald-800 border-emerald-300'
                : 'bg-white text-slate-700 border-slate-300 hover:bg-slate-50'
            }`}
            title="نمایش یا پنهان‌سازی پنل تشخیص چهره"
          >
            {isFacePanelOpen ? (
              <PanelRightClose className="w-4 h-4 text-emerald-600" />
            ) : (
              <PanelRightOpen className="w-4 h-4 text-slate-500" />
            )}
            <span>پنل تشخیص چهره ({detectedFaces.length})</span>
          </button>

          {/* سوییچر کلاینت پخش */}
          <div className="flex items-center gap-1.5 p-1 bg-slate-100 border border-slate-200 rounded-lg">
            <button
              onClick={() => setPlaybackClient('webrtc')}
              className={`px-3 py-1.5 text-xs font-bold rounded-md transition-colors flex items-center justify-center gap-1.5 whitespace-nowrap ${
                playbackClient === 'webrtc'
                  ? 'bg-white text-cyan-800 shadow-xs'
                  : 'text-slate-600 hover:text-slate-900'
              }`}
            >
              <Globe className="w-3.5 h-3.5 shrink-0" />
              <span>WebRTC (WHEP)</span>
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
              <span>HLS</span>
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
              <span>VLC / ویندوز</span>
            </button>
          </div>
        </div>
      </div>

      {/* اعلان راهنما: دوبار کلیک برای تمام‌صفحه و راست‌کلیک برای منوی تنظیمات کانال */}
      <div className="p-3 bg-cyan-50 border border-cyan-200 rounded-xl text-xs text-cyan-900 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <ScanFace className="w-4 h-4 text-emerald-600 shrink-0" />
          <span>
            <strong>تعاملی:</strong> با <span className="font-bold underline">دوبار کلیک</span> روی
            تصویر تمام‌صفحه می‌شود. با <span className="font-bold underline">راست‌کلیک</span> منوی
            تنظیمات زوم، کادر سبز، تشخیص چهره و پلاک فعال می‌شود.
          </span>
        </div>
        <div className="flex items-center gap-2 font-mono text-[11px] text-cyan-800">
          <span>کادر سبز دور چهره (Overlay Canvas):</span>
          <span
            className={`px-1.5 py-0.5 rounded font-bold ${
              faceSettings.enabled && faceSettings.showOverlay
                ? 'bg-emerald-200 text-emerald-900'
                : 'bg-slate-200 text-slate-700'
            }`}
          >
            {faceSettings.enabled && faceSettings.showOverlay ? 'فعال (#22c55e)' : 'غیرفعال'}
          </span>
        </div>
      </div>

      {/* چیدمان ستونی: پلیر ویدیو در کنار پنل اختصاصی تشخیص چهره */}
      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* ستون پلیر ویدیو */}
        <div className={isFacePanelOpen ? 'xl:col-span-8 space-y-4' : 'xl:col-span-12 space-y-4'}>
          {/* محفظه ویدیو پلیر مجهز به معماری دو لایه‌ای (Video Canvas + Overlay Canvas) */}
          <div
            ref={playerContainerRef}
            onDoubleClick={handleToggleFullscreen}
            onContextMenu={handleContextMenu}
            className="relative rounded-xl overflow-hidden border border-slate-300 bg-slate-950 shadow-md group select-none"
            title="دوبار کلیک: تمام صفحه | راست‌کلیک: منوی کانال و چهره"
          >
            {/* بوم لایه پایه ویدیو (Base Video Decoding Canvas) */}
            <canvas
              ref={videoCanvasRef}
              width={960}
              height={540}
              className="w-full aspect-video bg-black block cursor-crosshair"
            />

            {/* بوم اورلی اختصاصی تفکیک‌شده (Dedicated Overlay Canvas for High-Performance Bounding Box) */}
            <canvas
              ref={overlayCanvasRef}
              width={960}
              height={540}
              className="absolute inset-0 w-full h-full pointer-events-none block"
            />

            {/* نشانگر گوشه بالا پلیر برای راهنمای تعاملی */}
            <div className="absolute top-3 right-3 flex items-center gap-1.5 pointer-events-none opacity-0 group-hover:opacity-100 transition-opacity">
              <span className="bg-slate-900/80 backdrop-blur-xs text-white text-[10px] px-2 py-1 rounded-md border border-slate-700">
                ۲x کلیک: تمام‌صفحه | راست‌کلیک: منو
              </span>
            </div>

            {/* نوار کنترل پایین پلیر */}
            <div className="p-3 bg-slate-900 border-t border-slate-800 grid grid-cols-[repeat(auto-fit,minmax(180px,1fr))] gap-3 text-xs text-white items-center">
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
                  <span>ثبت چهره و فریم مرجع</span>
                </button>
                <button
                  onClick={handleToggleFullscreen}
                  className="p-1.5 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white transition-colors shrink-0"
                  title="حالت تمام‌صفحه"
                >
                  <Maximize2 className="w-4 h-4" />
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
                    NVR (B)
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

          {/* مشخصات استریم فعال تیاندی */}
          <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 border-b border-slate-100 pb-2.5">
              <div className="text-xs font-bold text-slate-800 flex items-center gap-2">
                <Radio className="w-4 h-4 text-cyan-700" />
                <span>
                  مشخصات فنی استریم فعال تیاندی: <span className="text-cyan-800">{streamSpecs.name}</span>
                </span>
              </div>
              <span className="text-[11px] text-slate-500 font-mono" dir="ltr">
                مسیر فعال: /1/{activeStreamNum}
              </span>
            </div>
            <div className="grid grid-cols-[repeat(auto-fit,minmax(130px,1fr))] gap-3 text-xs">
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80">
                <span className="text-slate-500 block text-[11px] mb-0.5">رزولوشن</span>
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">
                  {streamSpecs.res}
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80">
                <span className="text-slate-500 block text-[11px] mb-0.5">نرخ فریم</span>
                <span className="font-mono font-bold text-cyan-700 text-xs block" dir="ltr">
                  {streamSpecs.fps} FPS
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80">
                <span className="text-slate-500 block text-[11px] mb-0.5">بیت‌ریت</span>
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">
                  {streamSpecs.bitrate}
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80">
                <span className="text-slate-500 block text-[11px] mb-0.5">فاصله GOP</span>
                <span className="font-mono font-bold text-emerald-700 text-xs block" dir="ltr">
                  {streamSpecs.gop} فریم
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200/80">
                <span className="text-slate-500 block text-[11px] mb-0.5">کدک</span>
                <span className="font-mono font-bold text-indigo-700 text-xs block" dir="ltr">
                  {activeCodec}
                </span>
              </div>
            </div>
          </div>
        </div>

        {/* ستون پنل اختصاصی تشخیص چهره (FaceDetectionPanel) */}
        {isFacePanelOpen && (
          <div className="xl:col-span-4 h-full">
            <FaceDetectionPanel
              events={detectedFaces}
              settings={faceSettings}
              onUpdateSettings={(updates) => setFaceSettings((prev) => ({ ...prev, ...updates }))}
              onClearEvents={() => setDetectedFaces([])}
              isStreamLive={isPlaying}
            />
          </div>
        )}
      </div>

      {/* منوی شناور راست‌کلیک (Context Menu) */}
      {contextMenu.visible && (
        <div
          className="fixed z-50 bg-slate-900/95 backdrop-blur-md text-white border border-slate-700 rounded-xl shadow-2xl p-1.5 w-60 text-xs divide-y divide-slate-800 animate-in fade-in zoom-in-95 duration-100"
          style={{ top: contextMenu.y, left: contextMenu.x }}
          onClick={(e) => e.stopPropagation()}
        >
          <div className="px-3 py-1.5 font-bold text-slate-400 text-[10px] tracking-wider uppercase flex items-center justify-between">
            <span>منوی اختصاصی کانال</span>
            <span className="text-cyan-400 font-mono">CH-01</span>
          </div>

          <div className="py-1">
            <button
              onClick={() => {
                setFaceSettings((prev) => ({ ...prev, enabled: !prev.enabled }));
                setContextMenu((prev) => ({ ...prev, visible: false }));
              }}
              className="w-full text-right px-3 py-2 rounded-lg hover:bg-slate-800 flex items-center justify-between gap-2 transition-colors"
            >
              <div className="flex items-center gap-2">
                <ScanFace className="w-4 h-4 text-emerald-400" />
                <span>تشخیص چهره هوشمند (Face)</span>
              </div>
              <span
                className={`text-[10px] px-1.5 py-0.5 rounded font-mono font-bold ${
                  faceSettings.enabled
                    ? 'bg-emerald-500/20 text-emerald-300'
                    : 'bg-slate-800 text-slate-400'
                }`}
              >
                {faceSettings.enabled ? 'ON' : 'OFF'}
              </span>
            </button>

            <button
              onClick={() => {
                setFaceSettings((prev) => ({ ...prev, showOverlay: !prev.showOverlay }));
                setContextMenu((prev) => ({ ...prev, visible: false }));
              }}
              className="w-full text-right px-3 py-2 rounded-lg hover:bg-slate-800 flex items-center justify-between gap-2 transition-colors"
            >
              <div className="flex items-center gap-2">
                <span className="w-3.5 h-3.5 rounded-sm border-2 border-emerald-400 inline-block" />
                <span>کادر سبز دور چهره (Overlay Canvas)</span>
              </div>
              <span
                className={`text-[10px] px-1.5 py-0.5 rounded font-mono font-bold ${
                  faceSettings.showOverlay
                    ? 'bg-emerald-500/20 text-emerald-300'
                    : 'bg-slate-800 text-slate-400'
                }`}
              >
                {faceSettings.showOverlay ? 'سبز' : 'مخفی'}
              </span>
            </button>

            <button
              onClick={() => {
                setFaceSettings((prev) => ({ ...prev, anprEnabled: !prev.anprEnabled }));
                setContextMenu((prev) => ({ ...prev, visible: false }));
              }}
              className="w-full text-right px-3 py-2 rounded-lg hover:bg-slate-800 flex items-center justify-between gap-2 transition-colors"
            >
              <div className="flex items-center gap-2">
                <Car className="w-4 h-4 text-cyan-400" />
                <span>پلاک‌خوان خودکار (ANPR)</span>
              </div>
              <span
                className={`text-[10px] px-1.5 py-0.5 rounded font-mono font-bold ${
                  faceSettings.anprEnabled
                    ? 'bg-cyan-500/20 text-cyan-300'
                    : 'bg-slate-800 text-slate-400'
                }`}
              >
                {faceSettings.anprEnabled ? 'ON' : 'OFF'}
              </span>
            </button>
          </div>

          <div className="py-1">
            <div className="px-3 py-1 text-[10px] text-slate-400 flex items-center gap-1.5">
              <ZoomIn className="w-3 h-3" />
              <span>بزرگ‌نمایی دیجیتال (Digital Zoom):</span>
            </div>
            <div className="grid grid-cols-3 gap-1 px-2 py-1 font-mono">
              {[1.0, 1.5, 2.0].map((z) => (
                <button
                  key={z}
                  onClick={() => {
                    setFaceSettings((prev) => ({ ...prev, digitalZoom: z }));
                    setContextMenu((prev) => ({ ...prev, visible: false }));
                  }}
                  className={`py-1 text-center rounded text-xs font-bold transition-colors ${
                    faceSettings.digitalZoom === z
                      ? 'bg-cyan-600 text-white'
                      : 'bg-slate-800 text-slate-300 hover:bg-slate-700'
                  }`}
                >
                  {z.toFixed(1)}x
                </button>
              ))}
            </div>
          </div>

          <div className="py-1">
            <button
              onClick={() => {
                handleCaptureSnapshot();
                setContextMenu((prev) => ({ ...prev, visible: false }));
              }}
              className="w-full text-right px-3 py-2 rounded-lg hover:bg-slate-800 flex items-center gap-2 transition-colors"
            >
              <Camera className="w-4 h-4 text-emerald-400" />
              <span>ثبت فریم و استخراج دستی چهره</span>
            </button>

            <button
              onClick={() => {
                handleToggleFullscreen();
                setContextMenu((prev) => ({ ...prev, visible: false }));
              }}
              className="w-full text-right px-3 py-2 rounded-lg hover:bg-slate-800 flex items-center gap-2 transition-colors"
            >
              <Maximize2 className="w-4 h-4 text-indigo-400" />
              <span>تمام‌صفحه کردن تصویر (Fullscreen)</span>
            </button>
          </div>
        </div>
      )}

      {/* تصویر فریم استخراج شده در صورت ثبت snapshot */}
      {snapshotUrl && (
        <div className="p-5 rounded-xl bg-white border border-cyan-300 shadow-xs space-y-3">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-xs font-bold text-cyan-800">
              <CheckCircle2 className="w-4 h-4 text-emerald-600" />
              <span>فریم کلیدی (I-Frame) از استریم دیکود شده استخراج و در آرشیو ثبت شد</span>
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
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">
                  {new Date().toLocaleString('fa-IR')}
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">آدرس RTSP منبع</span>
                <span className="font-mono font-bold text-slate-900 text-xs truncate block" dir="ltr">
                  {currentSanitizedUrl}
                </span>
              </div>
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
                <span className="text-slate-500 block text-[11px] mb-0.5">رزولوشن استریم</span>
                <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">
                  {streamSpecs.res}
                </span>
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

      {/* ۴ کارت متریک زنده */}
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
          <div className="text-slate-500 font-medium">موتور بیومتریک و چهره</div>
          <div className="text-xl font-bold font-mono text-emerald-700 tabular-nums" dir="ltr">
            {faceSettings.enabled ? 'ACTIVE (25 FPS)' : 'OFFLINE'}
          </div>
          <div className="text-[11px] text-slate-500">
            {faceSettings.enabled ? 'بوم تفکیک‌شده (Overlay Canvas) فعال' : 'بدون بار پردازشی'}
          </div>
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
