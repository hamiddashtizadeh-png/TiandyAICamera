import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  Film,
  Play,
  Pause,
  SkipBack,
  SkipForward,
  Upload,
  Video,
  Download,
  SlidersHorizontal,
  Maximize2,
  X,
  CheckCircle2,
  RefreshCw,
  Crosshair,
  Columns2,
  SplitSquareHorizontal,
  Image as ImageIcon,
  AlertCircle,
} from 'lucide-react';
import {
  DetectedFaceEvent,
  FrameInterpolationConfig,
  FrameInterpolationResult,
  InterpolationFactor,
  UpscaleFactor,
} from '../types/faceDetection';
import {
  exportSequenceAsWebM,
  extractSegmentFromImageFiles,
  extractSegmentFromVideoFile,
  generateJitterySegmentFromSource,
  runFrameInterpolationPipeline,
} from '../services/frameInterpolationEngine';

interface FaceInterpolationToolProps {
  events: DetectedFaceEvent[];
  selectedEvent: DetectedFaceEvent | null;
  onSelectEvent?: (event: DetectedFaceEvent | null) => void;
  onCaptureLiveSegment?: () => Promise<string[]>;
  onUpdateEventThumbnail?: (
    eventId: string,
    enhancedDataUrl: string,
    newQualityScore: number
  ) => void;
  isStreamLive?: boolean;
  isExpandedModal?: boolean;
  onToggleExpand?: (expanded: boolean) => void;
}

const DEFAULT_CONFIG: FrameInterpolationConfig = {
  interpolationFactor: 4,
  upscaleFactor: 3,
  stabilizationStrength: 0.9,
  deblurStrength: 0.65,
  temporalDenoise: true,
  sourceFps: 8,
};

export const FaceInterpolationTool: React.FC<FaceInterpolationToolProps> = ({
  events,
  selectedEvent,
  onSelectEvent,
  onCaptureLiveSegment,
  onUpdateEventThumbnail,
  isStreamLive = true,
  isExpandedModal = false,
  onToggleExpand,
}) => {
  const [config, setConfig] = useState<FrameInterpolationConfig>(DEFAULT_CONFIG);
  const [sourceFrames, setSourceFrames] = useState<string[]>([]);
  const [sourceLabel, setSourceLabel] = useState<string>('');
  const [result, setResult] = useState<FrameInterpolationResult | null>(null);

  const [isProcessing, setIsProcessing] = useState<boolean>(false);
  const [isCapturingLive, setIsCapturingLive] = useState<boolean>(false);
  const [isExportingVideo, setIsExportingVideo] = useState<boolean>(false);
  const [progressStage, setProgressStage] = useState<string>('');
  const [progressPct, setProgressPct] = useState<number>(0);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [appliedToEventId, setAppliedToEventId] = useState<string | null>(null);

  // Playback & Inspection controls
  const [isPlaying, setIsPlaying] = useState<boolean>(true);
  const [playbackSpeed, setPlaybackSpeed] = useState<0.25 | 0.5 | 1>(0.5);
  const [currentFrameIdx, setCurrentFrameIdx] = useState<number>(0);
  const [viewMode, setViewMode] = useState<'dual' | 'wipe'>('dual');
  const [wipePercent, setWipePercent] = useState<number>(52);
  const [showFlowVectors, setShowFlowVectors] = useState<boolean>(false);
  const [showBiometricGrid, setShowBiometricGrid] = useState<boolean>(true);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const beforeCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const afterCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const wipeCanvasRef = useRef<HTMLCanvasElement | null>(null);

  // Image caches for flicker-free synchronized playback
  const sourceImgCacheRef = useRef<HTMLImageElement[]>([]);
  const processedImgCacheRef = useRef<HTMLImageElement[]>([]);
  const lastLoadedEventIdRef = useRef<string | null>(null);

  const runProcessing = useCallback(
    async (framesToProcess: string[], label: string, activeConfig: FrameInterpolationConfig) => {
      if (framesToProcess.length < 2) {
        setErrorMessage('حداقل ۲ فریم برای اجرای تثبیت و درونیابی مورد نیاز است.');
        return;
      }
      setErrorMessage(null);
      setIsProcessing(true);
      setAppliedToEventId(null);
      setProgressPct(5);
      setProgressStage('آماده‌سازی بافر فریم‌ها...');

      try {
        const output = await runFrameInterpolationPipeline(
          framesToProcess,
          activeConfig,
          label,
          (stage, pct) => {
            setProgressStage(stage);
            setProgressPct(pct);
          }
        );

        // Preload images into cache for smooth canvas rendering
        const loadImg = (url: string) =>
          new Promise<HTMLImageElement>((resolve) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = () => resolve(img);
            img.src = url;
          });

        const [srcImgs, procImgs] = await Promise.all([
          Promise.all(output.sourceFrames.map(loadImg)),
          Promise.all(output.processedFrames.map((f) => loadImg(f.dataUrl))),
        ]);

        sourceImgCacheRef.current = srcImgs;
        processedImgCacheRef.current = procImgs;

        setResult(output);
        setCurrentFrameIdx(0);
        setIsPlaying(true);
      } catch (err) {
        setErrorMessage(
          err instanceof Error ? err.message : 'خطا در اجرای موتور درونیابی فریم.'
        );
      } finally {
        setIsProcessing(false);
      }
    },
    []
  );

  // Load segment automatically when a face event is selected or on initial mount
  const loadFromFaceEvent = useCallback(
    async (ev: DetectedFaceEvent | null, autoProcess = true) => {
      const targetTrack = ev ? ev.trackId : 104;
      const label = ev
        ? `سگمنت چهره #${ev.trackId} (${ev.channelName})`
        : 'سگمنت نمونه کم‌وضوح دوربین نظارتی (96×96)';

      setErrorMessage(null);
      setProgressStage('استخراج سگمنت کم‌وضوح با لرزش حرکتی...');
      const frames = await generateJitterySegmentFromSource(
        ev?.thumbnailUrl,
        targetTrack,
        8,
        96
      );
      setSourceFrames(frames);
      setSourceLabel(label);
      if (autoProcess) {
        await runProcessing(frames, label, config);
      }
    },
    [config, runProcessing]
  );

  useEffect(() => {
    const activeEvent = selectedEvent || events[0] || null;
    const key = activeEvent ? activeEvent.id : '__default_sample__';
    if (lastLoadedEventIdRef.current !== key) {
      lastLoadedEventIdRef.current = key;
      void loadFromFaceEvent(activeEvent, true);
    }
  }, [selectedEvent, events, loadFromFaceEvent]);

  // Capture live low-res segment directly from VideoPlaybackHarness canvas
  const handleCaptureLive = async () => {
    if (!onCaptureLiveSegment) return;
    setErrorMessage(null);
    setIsCapturingLive(true);
    try {
      const captured = await onCaptureLiveSegment();
      if (captured.length < 2) {
        setErrorMessage('امکان ثبت سگمنت زنده از بوم ویدیو فراهم نشد. از پخش بودن جریان مطمئن شوید.');
        return;
      }
      const label = `سگمنت زنده استریم (${captured.length} فریم متوالی)`;
      setSourceFrames(captured);
      setSourceLabel(label);
      await runProcessing(captured, label, config);
    } catch (err) {
      setErrorMessage(
        err instanceof Error ? err.message : 'خطا در ضبط سگمنت زنده از استریم.'
      );
    } finally {
      setIsCapturingLive(false);
    }
  };

  // Upload real video file or image sequence
  const handleFileUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files || []);
    e.target.value = '';
    if (files.length === 0) return;

    setErrorMessage(null);
    setIsProcessing(true);
    setProgressStage('در حال استخراج فریم‌ها از فایل ورودی...');
    setProgressPct(10);

    try {
      const first = files[0];
      let extracted: string[] = [];
      let label = first.name;

      if (first.type.startsWith('video/')) {
        extracted = await extractSegmentFromVideoFile(first, 10, 112);
        label = `کلیپ ویدیویی: ${first.name}`;
      } else {
        extracted = await extractSegmentFromImageFiles(files, 112);
        label =
          files.length > 1
            ? `توالی ${files.length} تصویر (${first.name})`
            : `سگمنت بازسازی‌شده از: ${first.name}`;
      }

      if (extracted.length < 2) {
        setErrorMessage('تعداد فریم استخراج‌شده برای درونیابی کافی نیست.');
        setIsProcessing(false);
        return;
      }

      setSourceFrames(extracted);
      setSourceLabel(label);
      await runProcessing(extracted, label, config);
    } catch (err) {
      setIsProcessing(false);
      setErrorMessage(
        err instanceof Error ? err.message : 'خطا در خواندن فایل ورودی.'
      );
    }
  };

  // Synchronized animation loop
  useEffect(() => {
    if (!result || !isPlaying || result.processedFrames.length <= 1) return;
    const effectiveFps = Math.max(2, result.metrics.outputFps * playbackSpeed);
    const intervalMs = Math.round(1000 / effectiveFps);

    const timer = window.setInterval(() => {
      setCurrentFrameIdx((prev) => (prev + 1) % result.processedFrames.length);
    }, intervalMs);

    return () => window.clearInterval(timer);
  }, [result, isPlaying, playbackSpeed]);

  // Draw overlays helper (optical flow vectors + biometric alignment grid)
  const drawInspectionOverlays = useCallback(
    (
      ctx: CanvasRenderingContext2D,
      width: number,
      height: number,
      frameIndex: number
    ) => {
      if (!result) return;
      const item = result.processedFrames[frameIndex];
      if (!item) return;

      if (showBiometricGrid) {
        ctx.save();
        ctx.strokeStyle = 'rgba(16, 185, 129, 0.38)';
        ctx.lineWidth = 1;
        ctx.setLineDash([4, 4]);
        // Vertical facial symmetry line
        ctx.beginPath();
        ctx.moveTo(width / 2, 10);
        ctx.lineTo(width / 2, height - 10);
        // Horizontal inter-ocular reference line (~45% height)
        ctx.moveTo(12, height * 0.44);
        ctx.lineTo(width - 12, height * 0.44);
        ctx.stroke();

        // Subtle corner framing brackets
        ctx.setLineDash([]);
        ctx.strokeStyle = 'rgba(16, 185, 129, 0.72)';
        ctx.lineWidth = 1.5;
        const m = 10;
        const len = 14;
        ctx.beginPath();
        ctx.moveTo(m, m + len);
        ctx.lineTo(m, m);
        ctx.lineTo(m + len, m);
        ctx.moveTo(width - m - len, m);
        ctx.lineTo(width - m, m);
        ctx.lineTo(width - m, m + len);
        ctx.moveTo(m, height - m - len);
        ctx.lineTo(m, height - m);
        ctx.lineTo(m + len, height - m);
        ctx.moveTo(width - m - len, height - m);
        ctx.lineTo(width - m, height - m);
        ctx.lineTo(width - m, height - m - len);
        ctx.stroke();
        ctx.restore();
      }

      if (showFlowVectors && item.motionVectors.length > 0) {
        ctx.save();
        ctx.strokeStyle = 'rgba(34, 211, 238, 0.85)';
        ctx.fillStyle = 'rgba(34, 211, 238, 0.9)';
        ctx.lineWidth = 1.25;
        const scale = Math.max(2.5, width / 64);

        for (const cell of item.motionVectors) {
          const mag = Math.hypot(cell.vx, cell.vy);
          if (mag < 0.25) continue;
          const x0 = cell.gx * width;
          const y0 = cell.gy * height;
          const x1 = x0 + cell.vx * scale;
          const y1 = y0 + cell.vy * scale;

          ctx.beginPath();
          ctx.moveTo(x0, y0);
          ctx.lineTo(x1, y1);
          ctx.stroke();

          ctx.beginPath();
          ctx.arc(x1, y1, 1.8, 0, Math.PI * 2);
          ctx.fill();
        }
        ctx.restore();
      }
    },
    [result, showBiometricGrid, showFlowVectors]
  );

  // Render active frame onto Before/After or Wipe canvas
  useEffect(() => {
    if (!result) return;
    const item = result.processedFrames[currentFrameIdx];
    if (!item) return;

    const srcImg = sourceImgCacheRef.current[item.sourceIndex];
    const procImg = processedImgCacheRef.current[currentFrameIdx];
    if (!srcImg || !procImg) return;

    if (viewMode === 'dual') {
      const bCanvas = beforeCanvasRef.current;
      const aCanvas = afterCanvasRef.current;
      if (bCanvas) {
        const bCtx = bCanvas.getContext('2d');
        if (bCtx) {
          bCtx.imageSmoothingEnabled = false; // Show authentic raw low-res pixels
          bCtx.clearRect(0, 0, bCanvas.width, bCanvas.height);
          bCtx.drawImage(srcImg, 0, 0, bCanvas.width, bCanvas.height);
        }
      }
      if (aCanvas) {
        const aCtx = aCanvas.getContext('2d');
        if (aCtx) {
          aCtx.imageSmoothingEnabled = true;
          aCtx.imageSmoothingQuality = 'high';
          aCtx.clearRect(0, 0, aCanvas.width, aCanvas.height);
          aCtx.drawImage(procImg, 0, 0, aCanvas.width, aCanvas.height);
          drawInspectionOverlays(aCtx, aCanvas.width, aCanvas.height, currentFrameIdx);
        }
      }
    } else {
      const wCanvas = wipeCanvasRef.current;
      if (wCanvas) {
        const wCtx = wCanvas.getContext('2d');
        if (wCtx) {
          const w = wCanvas.width;
          const h = wCanvas.height;
          const splitX = Math.round((wipePercent / 100) * w);

          // Left side: enhanced (After)
          wCtx.clearRect(0, 0, w, h);
          wCtx.imageSmoothingEnabled = true;
          wCtx.imageSmoothingQuality = 'high';
          wCtx.drawImage(procImg, 0, 0, w, h);
          drawInspectionOverlays(wCtx, w, h, currentFrameIdx);

          // Right side (from splitX to w): raw jittery input (Before)
          wCtx.save();
          wCtx.beginPath();
          wCtx.rect(splitX, 0, w - splitX, h);
          wCtx.clip();
          wCtx.imageSmoothingEnabled = false;
          wCtx.drawImage(srcImg, 0, 0, w, h);
          wCtx.restore();

          // Divider line
          wCtx.strokeStyle = '#10b981';
          wCtx.lineWidth = 2;
          wCtx.beginPath();
          wCtx.moveTo(splitX, 0);
          wCtx.lineTo(splitX, h);
          wCtx.stroke();
        }
      }
    }
  }, [result, currentFrameIdx, viewMode, wipePercent, drawInspectionOverlays]);

  const handleDownloadBestPng = () => {
    if (!result) return;
    const link = document.createElement('a');
    link.href = result.bestFrame.dataUrl;
    link.download = `face-verified-upscaled-${Date.now()}.png`;
    link.click();
  };

  const handleExportWebM = async () => {
    if (!result) return;
    setIsExportingVideo(true);
    setErrorMessage(null);
    try {
      const blob = await exportSequenceAsWebM(
        result.processedFrames,
        result.metrics.outputFps,
        2
      );
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `face-stabilized-${result.metrics.outputFps}fps-${Date.now()}.webm`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    } catch (err) {
      setErrorMessage(
        err instanceof Error ? err.message : 'خطا در ساخت فایل ویدیویی WebM.'
      );
    } finally {
      setIsExportingVideo(false);
    }
  };

  const handleApplyToEvent = () => {
    if (!result || !selectedEvent || !onUpdateEventThumbnail) return;
    onUpdateEventThumbnail(
      selectedEvent.id,
      result.bestFrame.dataUrl,
      result.metrics.verificationScoreAfter
    );
    setAppliedToEventId(selectedEvent.id);
  };

  const activeItem = result?.processedFrames[currentFrameIdx] || null;
  const canvasPx = isExpandedModal ? 300 : 210;

  return (
    <div className="flex flex-col h-full bg-white text-slate-900">
      {/* Hidden file input for video or image sequence upload */}
      <input
        ref={fileInputRef}
        type="file"
        accept="video/*,image/*"
        multiple
        onChange={handleFileUpload}
        className="hidden"
      />

      {/* Top Tool Bar */}
      <div className="px-4 py-3 bg-slate-50 border-b border-slate-200 flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Film className="w-4 h-4 text-cyan-700 shrink-0" />
            <h4 className="text-xs font-bold text-slate-900 truncate">
              درونیابی فریم، تثبیت لرزش و بزرگ‌نمایی چهره (AI Frame Interpolation)
            </h4>
          </div>
          <p className="text-[11px] text-slate-500 mt-0.5 truncate">
            {sourceLabel || 'تبدیل سگمنت کم‌وضوح و لرزان به جریان روان و شفاف جهت احراز هویت'}
          </p>
        </div>

        <div className="flex items-center gap-1.5 shrink-0">
          {onCaptureLiveSegment && (
            <button
              onClick={handleCaptureLive}
              disabled={isCapturingLive || isProcessing || !isStreamLive}
              className="px-2.5 py-1.5 text-[11px] font-semibold rounded-lg bg-emerald-600 hover:bg-emerald-700 disabled:opacity-50 text-white flex items-center gap-1.5 transition-colors whitespace-nowrap"
              title="ثبت ۸ فریم متوالی از بوم ویدیو زنده جهت تثبیت"
            >
              <Video className="w-3.5 h-3.5" />
              <span>{isCapturingLive ? 'در حال ثبت...' : 'ضبط از زنده'}</span>
            </button>
          )}

          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={isProcessing}
            className="px-2.5 py-1.5 text-[11px] font-semibold rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-700 flex items-center gap-1.5 transition-colors whitespace-nowrap"
            title="آپلود کلیپ ویدیویی کوتاه یا توالی تصاویر چهره"
          >
            <Upload className="w-3.5 h-3.5 text-cyan-700" />
            <span>آپلود ویدیو / عکس</span>
          </button>

          {onToggleExpand && (
            <button
              onClick={() => onToggleExpand(!isExpandedModal)}
              className="p-1.5 rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-700 transition-colors"
              title={isExpandedModal ? 'بستن نمای کامل' : 'باز کردن کارگاه تمام‌صفحه'}
            >
              {isExpandedModal ? (
                <X className="w-4 h-4" />
              ) : (
                <Maximize2 className="w-3.5 h-3.5" />
              )}
            </button>
          )}
        </div>
      </div>

      {/* Main Scrollable Content */}
      <div
        className={`flex-1 overflow-y-auto p-4 space-y-4 ${
          isExpandedModal ? 'max-h-[82vh]' : 'max-h-[540px]'
        }`}
      >
        {/* Source Event Selector Row */}
        {events.length > 0 && (
          <div className="flex flex-wrap items-center justify-between gap-2 pb-3 border-b border-slate-100">
            <label className="text-[11px] font-semibold text-slate-600 shrink-0">
              انتخاب از رویدادهای چهره:
            </label>
            <select
              value={selectedEvent?.id || ''}
              onChange={(e) => {
                const found = events.find((ev) => ev.id === e.target.value) || null;
                onSelectEvent?.(found);
                lastLoadedEventIdRef.current = found ? found.id : '__manual__';
                void loadFromFaceEvent(found, true);
              }}
              className="flex-1 min-w-[180px] text-xs bg-slate-50 border border-slate-200 rounded-lg px-2.5 py-1.5 text-slate-800 focus:outline-hidden focus:border-cyan-600"
            >
              {events.map((ev) => (
                <option key={ev.id} value={ev.id}>
                  چهره #{ev.trackId} — {ev.channelName} ({ev.timestamp}) · کیفیت {ev.qualityScore}%
                </option>
              ))}
            </select>
          </div>
        )}

        {/* Error Alert */}
        {errorMessage && (
          <div className="p-3 rounded-lg bg-rose-50 border border-rose-200 text-rose-800 text-xs flex items-center gap-2">
            <AlertCircle className="w-4 h-4 shrink-0 text-rose-600" />
            <span>{errorMessage}</span>
          </div>
        )}

        {/* Processing Progress Bar */}
        {isProcessing && (
          <div className="p-3 rounded-lg bg-slate-900 text-white space-y-2">
            <div className="flex items-center justify-between text-xs">
              <span className="font-medium text-slate-200">{progressStage}</span>
              <span className="font-mono tabular-nums text-cyan-300 font-bold">
                {progressPct}%
              </span>
            </div>
            <div className="w-full h-1.5 bg-slate-800 rounded-full overflow-hidden">
              <div
                className="h-full bg-emerald-500 transition-all duration-150"
                style={{ width: `${progressPct}%` }}
              />
            </div>
          </div>
        )}

        {/* Viewport Section: Dual Side-by-Side OR Wipe Slider */}
        <div className="space-y-2.5">
          <div className="flex flex-wrap items-center justify-between gap-2">
            {/* View Mode Segmented Switcher */}
            <div className="inline-flex items-center p-0.5 bg-slate-100 rounded-lg border border-slate-200">
              <button
                onClick={() => setViewMode('dual')}
                className={`px-2.5 py-1 text-[11px] font-semibold rounded-md flex items-center gap-1 transition-colors ${
                  viewMode === 'dual'
                    ? 'bg-white text-slate-900 shadow-2xs'
                    : 'text-slate-600 hover:text-slate-900'
                }`}
              >
                <Columns2 className="w-3.5 h-3.5" />
                <span>مقایسه همزمان</span>
              </button>
              <button
                onClick={() => setViewMode('wipe')}
                className={`px-2.5 py-1 text-[11px] font-semibold rounded-md flex items-center gap-1 transition-colors ${
                  viewMode === 'wipe'
                    ? 'bg-white text-slate-900 shadow-2xs'
                    : 'text-slate-600 hover:text-slate-900'
                }`}
              >
                <SplitSquareHorizontal className="w-3.5 h-3.5" />
                <span>اسلایدر قبل/بعد</span>
              </button>
            </div>

            {/* Diagnostic Overlays */}
            <div className="flex items-center gap-3 text-[11px] text-slate-600">
              <label className="flex items-center gap-1.5 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={showBiometricGrid}
                  onChange={(e) => setShowBiometricGrid(e.target.checked)}
                  className="w-3.5 h-3.5 rounded text-emerald-600 border-slate-300"
                />
                <span>تراز چهره</span>
              </label>
              <label className="flex items-center gap-1.5 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={showFlowVectors}
                  onChange={(e) => setShowFlowVectors(e.target.checked)}
                  className="w-3.5 h-3.5 rounded text-cyan-600 border-slate-300"
                />
                <span>بردارهای حرکت</span>
              </label>
            </div>
          </div>

          {viewMode === 'dual' ? (
            <div className="grid grid-cols-2 gap-3">
              {/* Before (Raw Jittery Low-Res) */}
              <div className="rounded-xl bg-slate-950 border border-slate-800 overflow-hidden flex flex-col">
                <div className="px-2.5 py-1.5 bg-slate-900 border-b border-slate-800 flex items-center justify-between text-[11px]">
                  <span className="font-semibold text-slate-300">ورودی خام لرزان</span>
                  {result && (
                    <span className="font-mono tabular-nums text-amber-300" dir="ltr">
                      {result.metrics.inputWidth}×{result.metrics.inputHeight} · {result.metrics.inputFps}fps
                    </span>
                  )}
                </div>
                <div className="p-2 flex items-center justify-center bg-slate-950 aspect-square">
                  <canvas
                    ref={beforeCanvasRef}
                    width={canvasPx}
                    height={canvasPx}
                    className="w-full h-full object-contain rounded"
                    style={{ imageRendering: 'pixelated' }}
                  />
                </div>
                {activeItem && (
                  <div className="px-2.5 py-1 bg-slate-900/80 border-t border-slate-800 text-[10px] text-slate-400 flex items-center justify-between font-mono tabular-nums" dir="ltr">
                    <span>SRC #{activeItem.sourceIndex + 1}</span>
                    <span>
                      Δ({activeItem.shiftX > 0 ? '+' : ''}
                      {activeItem.shiftX}, {activeItem.shiftY > 0 ? '+' : ''}
                      {activeItem.shiftY})px
                    </span>
                  </div>
                )}
              </div>

              {/* After (Stabilized + Interpolated + Upscaled) */}
              <div className="rounded-xl bg-slate-950 border border-emerald-700/60 overflow-hidden flex flex-col">
                <div className="px-2.5 py-1.5 bg-slate-900 border-b border-slate-800 flex items-center justify-between text-[11px]">
                  <span className="font-semibold text-emerald-300">خروجی تثبیت و درونیابی AI</span>
                  {result && (
                    <span className="font-mono tabular-nums text-emerald-300" dir="ltr">
                      {result.metrics.outputWidth}×{result.metrics.outputHeight} · {result.metrics.outputFps}fps
                    </span>
                  )}
                </div>
                <div className="p-2 flex items-center justify-center bg-slate-950 aspect-square">
                  <canvas
                    ref={afterCanvasRef}
                    width={canvasPx}
                    height={canvasPx}
                    className="w-full h-full object-contain rounded"
                  />
                </div>
                {activeItem && (
                  <div className="px-2.5 py-1 bg-slate-900/80 border-t border-slate-800 text-[10px] flex items-center justify-between font-mono tabular-nums" dir="ltr">
                    <span className={activeItem.isInterpolated ? 'text-cyan-300 font-bold' : 'text-emerald-300 font-bold'}>
                      {activeItem.isInterpolated
                        ? `AI-INTERP α=${activeItem.alpha.toFixed(2)}`
                        : `KEYFRAME #${activeItem.sourceIndex + 1}`}
                    </span>
                    <span className="text-slate-300">Sharp: {activeItem.sharpness}</span>
                  </div>
                )}
              </div>
            </div>
          ) : (
            /* Single Split-Wipe Inspector */
            <div className="rounded-xl bg-slate-950 border border-slate-800 overflow-hidden">
              <div className="px-3 py-1.5 bg-slate-900 border-b border-slate-800 flex items-center justify-between text-[11px] text-slate-300">
                <span className="text-emerald-300 font-semibold">چپ: بازسازی و تثبیت AI</span>
                <span className="text-amber-300 font-semibold">راست: ورودی خام کم‌وضوح</span>
              </div>
              <div className="p-3 flex flex-col items-center gap-2.5 bg-slate-950">
                <canvas
                  ref={wipeCanvasRef}
                  width={320}
                  height={320}
                  className="w-full max-w-[320px] aspect-square object-contain rounded border border-slate-800"
                />
                <div className="w-full max-w-[320px] flex items-center gap-2 text-[11px] text-slate-300">
                  <span>قبل</span>
                  <input
                    type="range"
                    min={5}
                    max={95}
                    value={wipePercent}
                    onChange={(e) => setWipePercent(Number(e.target.value))}
                    className="flex-1 accent-emerald-500 cursor-pointer"
                    dir="ltr"
                  />
                  <span>بعد</span>
                </div>
              </div>
            </div>
          )}

          {/* Synchronized Playback & Frame Scrubber */}
          {result && (
            <div className="p-3 rounded-xl bg-slate-50 border border-slate-200 space-y-2.5">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-1.5">
                  <button
                    onClick={() => {
                      setIsPlaying(false);
                      setCurrentFrameIdx(
                        (prev) =>
                          (prev - 1 + result.processedFrames.length) %
                          result.processedFrames.length
                      );
                    }}
                    className="p-1.5 rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-700"
                    title="فریم قبلی"
                  >
                    <SkipForward className="w-3.5 h-3.5" />
                  </button>
                  <button
                    onClick={() => setIsPlaying((p) => !p)}
                    className="px-3 py-1.5 rounded-lg bg-slate-900 hover:bg-slate-800 text-white text-xs font-semibold flex items-center gap-1.5"
                  >
                    {isPlaying ? (
                      <>
                        <Pause className="w-3.5 h-3.5" />
                        <span>توقف</span>
                      </>
                    ) : (
                      <>
                        <Play className="w-3.5 h-3.5" />
                        <span>پخش همزمان</span>
                      </>
                    )}
                  </button>
                  <button
                    onClick={() => {
                      setIsPlaying(false);
                      setCurrentFrameIdx(
                        (prev) => (prev + 1) % result.processedFrames.length
                      );
                    }}
                    className="p-1.5 rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-700"
                    title="فریم بعدی"
                  >
                    <SkipBack className="w-3.5 h-3.5" />
                  </button>
                </div>

                {/* Slow-Motion Inspection Speed */}
                <div className="flex items-center gap-1 bg-white border border-slate-200 rounded-lg p-0.5 text-[11px]">
                  {([0.25, 0.5, 1] as const).map((spd) => (
                    <button
                      key={spd}
                      onClick={() => setPlaybackSpeed(spd)}
                      className={`px-2 py-0.5 rounded font-mono tabular-nums transition-colors ${
                        playbackSpeed === spd
                          ? 'bg-cyan-700 text-white font-bold'
                          : 'text-slate-600 hover:bg-slate-100'
                      }`}
                      dir="ltr"
                    >
                      {spd}×
                    </button>
                  ))}
                </div>

                <div className="text-[11px] text-slate-600 font-mono tabular-nums" dir="ltr">
                  Frame {currentFrameIdx + 1} / {result.processedFrames.length}
                </div>
              </div>

              {/* Scrubber with keyframe vs interpolated indicators */}
              <div className="space-y-1">
                <input
                  type="range"
                  min={0}
                  max={Math.max(0, result.processedFrames.length - 1)}
                  value={currentFrameIdx}
                  onChange={(e) => {
                    setIsPlaying(false);
                    setCurrentFrameIdx(Number(e.target.value));
                  }}
                  className="w-full accent-cyan-700 cursor-pointer"
                  dir="ltr"
                />
                <div className="flex items-center justify-between gap-0.5 h-1.5" dir="ltr">
                  {result.processedFrames.map((f) => (
                    <button
                      key={f.index}
                      onClick={() => {
                        setIsPlaying(false);
                        setCurrentFrameIdx(f.index);
                      }}
                      title={
                        f.isInterpolated
                          ? `فریم درونیابی‌شده AI #${f.index + 1}`
                          : `فریم کلیدی اصلی #${f.sourceIndex + 1}`
                      }
                      className={`h-full flex-1 rounded-xs transition-opacity ${
                        f.index === currentFrameIdx
                          ? 'bg-slate-900 opacity-100'
                          : f.index === result.metrics.bestFrameIndex
                          ? 'bg-emerald-500 opacity-90'
                          : f.isInterpolated
                          ? 'bg-cyan-400/70 hover:opacity-100'
                          : 'bg-slate-400 hover:opacity-100'
                      }`}
                    />
                  ))}
                </div>
                <div className="flex items-center justify-between text-[10px] text-slate-500 pt-0.5">
                  <span>خاکستری: فریم اصلی · فیروزه‌ای: فریم درونیابی AI · سبز: بهترین فریم احراز هویت</span>
                  <button
                    onClick={() => {
                      setIsPlaying(false);
                      setCurrentFrameIdx(result.metrics.bestFrameIndex);
                    }}
                    className="text-emerald-700 hover:text-emerald-800 font-semibold whitespace-nowrap"
                  >
                    پرش به بهترین فریم (#{result.metrics.bestFrameIndex + 1})
                  </button>
                </div>
              </div>
            </div>
          )}
        </div>

        {/* Quantitative Identity-Verification Metrics (Unboxed Clean Tabular Grid) */}
        {result && (
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 py-3 border-y border-slate-200 text-xs">
            <div>
              <div className="text-[11px] text-slate-500">کاهش لرزش (RMS Jitter)</div>
              <div className="font-mono tabular-nums font-bold text-slate-900 mt-0.5" dir="ltr">
                {result.metrics.jitterRmsBeforePx}px → {result.metrics.jitterRmsAfterPx}px
              </div>
              <div className="text-[11px] text-emerald-700 font-semibold font-mono tabular-nums">
                -{result.metrics.stabilizationGainPct}% لرزش
              </div>
            </div>

            <div>
              <div className="text-[11px] text-slate-500">وضوح و نرخ فریم خروجی</div>
              <div className="font-mono tabular-nums font-bold text-slate-900 mt-0.5" dir="ltr">
                {result.metrics.outputWidth}×{result.metrics.outputHeight}px
              </div>
              <div className="text-[11px] text-cyan-700 font-semibold font-mono tabular-nums" dir="ltr">
                {result.metrics.inputFps} → {result.metrics.outputFps} FPS ({result.metrics.outputFrameCount}f)
              </div>
            </div>

            <div>
              <div className="text-[11px] text-slate-500">وضوح لبه (Laplacian)</div>
              <div className="font-mono tabular-nums font-bold text-slate-900 mt-0.5" dir="ltr">
                {result.metrics.sharpnessBefore} → {result.metrics.sharpnessAfter}
              </div>
              <div className="text-[11px] text-emerald-700 font-semibold font-mono tabular-nums" dir="ltr">
                {result.metrics.sharpnessGainPct >= 0 ? '+' : ''}
                {result.metrics.sharpnessGainPct}% جزئیات
              </div>
            </div>

            <div>
              <div className="text-[11px] text-slate-500">آمادگی احراز هویت (ISO)</div>
              <div className="font-mono tabular-nums font-bold text-slate-900 mt-0.5" dir="ltr">
                {result.metrics.verificationScoreBefore}% → {result.metrics.verificationScoreAfter}%
              </div>
              <div className="text-[11px] text-slate-500 font-mono tabular-nums" dir="ltr">
                پردازش: {result.metrics.processingTimeMs} ms
              </div>
            </div>
          </div>
        )}

        {/* Engine Parameter Controls */}
        <div className="space-y-3">
          <div className="flex items-center gap-1.5 text-xs font-bold text-slate-800">
            <SlidersHorizontal className="w-3.5 h-3.5 text-cyan-700" />
            <span>تنظیمات موتور درونیابی، تثبیت و بزرگ‌نمایی</span>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
            {/* Interpolation Multiplier */}
            <div className="space-y-1.5">
              <span className="text-[11px] text-slate-600 font-medium block">
                ضریب درونیابی فریم (Optical Flow)
              </span>
              <div className="grid grid-cols-2 gap-1.5">
                {([2, 4] as InterpolationFactor[]).map((factor) => (
                  <button
                    key={factor}
                    onClick={() =>
                      setConfig((prev) => ({ ...prev, interpolationFactor: factor }))
                    }
                    className={`py-1.5 px-2.5 rounded-lg text-xs font-semibold border transition-colors ${
                      config.interpolationFactor === factor
                        ? 'bg-slate-900 text-white border-slate-900'
                        : 'bg-white text-slate-700 border-slate-200 hover:bg-slate-50'
                    }`}
                  >
                    {factor}× ({config.sourceFps * factor} FPS)
                  </button>
                ))}
              </div>
            </div>

            {/* Super-Resolution Upscale Factor */}
            <div className="space-y-1.5">
              <span className="text-[11px] text-slate-600 font-medium block">
                ضریب بزرگ‌نمایی فوق‌وضوح (Catmull-Rom SR)
              </span>
              <div className="grid grid-cols-3 gap-1.5">
                {([2, 3, 4] as UpscaleFactor[]).map((factor) => (
                  <button
                    key={factor}
                    onClick={() =>
                      setConfig((prev) => ({ ...prev, upscaleFactor: factor }))
                    }
                    className={`py-1.5 px-2 rounded-lg text-xs font-mono font-bold border transition-colors ${
                      config.upscaleFactor === factor
                        ? 'bg-cyan-700 text-white border-cyan-700'
                        : 'bg-white text-slate-700 border-slate-200 hover:bg-slate-50'
                    }`}
                    dir="ltr"
                  >
                    {factor}×
                  </button>
                ))}
              </div>
            </div>

            {/* Stabilization Strength Slider */}
            <div className="space-y-1">
              <div className="flex items-center justify-between text-[11px]">
                <span className="text-slate-600 font-medium">قدرت تثبیت لرزش دوربین</span>
                <span className="font-mono tabular-nums font-bold text-slate-800">
                  {Math.round(config.stabilizationStrength * 100)}%
                </span>
              </div>
              <input
                type="range"
                min={0}
                max={100}
                value={Math.round(config.stabilizationStrength * 100)}
                onChange={(e) =>
                  setConfig((prev) => ({
                    ...prev,
                    stabilizationStrength: Number(e.target.value) / 100,
                  }))
                }
                className="w-full accent-emerald-600 cursor-pointer"
                dir="ltr"
              />
            </div>

            {/* Deblur & Detail Reconstruction Slider */}
            <div className="space-y-1">
              <div className="flex items-center justify-between text-[11px]">
                <span className="text-slate-600 font-medium">بازسازی جزئیات چهره (Y-Channel)</span>
                <span className="font-mono tabular-nums font-bold text-slate-800">
                  {Math.round(config.deblurStrength * 100)}%
                </span>
              </div>
              <input
                type="range"
                min={0}
                max={100}
                value={Math.round(config.deblurStrength * 100)}
                onChange={(e) =>
                  setConfig((prev) => ({
                    ...prev,
                    deblurStrength: Number(e.target.value) / 100,
                  }))
                }
                className="w-full accent-cyan-700 cursor-pointer"
                dir="ltr"
              />
            </div>
          </div>

          <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
            <label className="flex items-center gap-2 text-xs text-slate-700 cursor-pointer select-none">
              <input
                type="checkbox"
                checked={config.temporalDenoise}
                onChange={(e) =>
                  setConfig((prev) => ({ ...prev, temporalDenoise: e.target.checked }))
                }
                className="w-3.5 h-3.5 rounded text-emerald-600 border-slate-300"
              />
              <span>حذف نویز زمانی چندفریمی (Temporal Multi-Frame Fusion)</span>
            </label>

            <button
              onClick={() => void runProcessing(sourceFrames, sourceLabel, config)}
              disabled={isProcessing || sourceFrames.length < 2}
              className="px-4 py-2 rounded-lg bg-slate-900 hover:bg-slate-800 disabled:opacity-50 text-white text-xs font-bold flex items-center gap-2 transition-colors shadow-xs whitespace-nowrap"
            >
              <RefreshCw className={`w-3.5 h-3.5 ${isProcessing ? 'animate-spin' : ''}`} />
              <span>{isProcessing ? 'در حال پردازش...' : 'اعمال و بازسازی مجدد AI'}</span>
            </button>
          </div>
        </div>

        {/* Best Keyframe Identity Verification Strip & Export Actions */}
        {result && (
          <div className="pt-3 border-t border-slate-200 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <img
                src={result.bestFrame.dataUrl}
                alt="بهترین فریم تثبیت و بزرگ‌نمایی شده برای احراز هویت"
                className="w-14 h-14 rounded-lg object-cover border border-emerald-300 bg-slate-900 shrink-0"
              />
              <div className="text-xs space-y-0.5">
                <div className="font-bold text-slate-900 flex items-center gap-1.5">
                  <Crosshair className="w-3.5 h-3.5 text-emerald-600" />
                  <span>بهترین فریم احراز هویت (فریم #{result.metrics.bestFrameIndex + 1})</span>
                </div>
                <div className="text-[11px] text-slate-500 font-mono tabular-nums" dir="ltr">
                  {result.bestFrame.width}×{result.bestFrame.height}px · Sharpness {result.bestFrame.sharpness} · ISO {result.metrics.verificationScoreAfter}%
                </div>
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              {selectedEvent && onUpdateEventThumbnail && (
                <button
                  onClick={handleApplyToEvent}
                  className={`px-3 py-1.5 rounded-lg text-[11px] font-bold flex items-center gap-1.5 transition-colors whitespace-nowrap ${
                    appliedToEventId === selectedEvent.id
                      ? 'bg-emerald-100 text-emerald-800 border border-emerald-300'
                      : 'bg-emerald-600 hover:bg-emerald-700 text-white'
                  }`}
                >
                  <CheckCircle2 className="w-3.5 h-3.5" />
                  <span>
                    {appliedToEventId === selectedEvent.id
                      ? 'در رویداد ثبت شد'
                      : 'به‌روزرسانی تامبنیل رویداد'}
                  </span>
                </button>
              )}

              <button
                onClick={handleDownloadBestPng}
                className="px-3 py-1.5 rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-800 text-[11px] font-semibold flex items-center gap-1.5 transition-colors whitespace-nowrap"
              >
                <ImageIcon className="w-3.5 h-3.5 text-cyan-700" />
                <span>دانلود PNG</span>
              </button>

              <button
                onClick={() => void handleExportWebM()}
                disabled={isExportingVideo}
                className="px-3 py-1.5 rounded-lg bg-cyan-700 hover:bg-cyan-800 disabled:opacity-50 text-white text-[11px] font-semibold flex items-center gap-1.5 transition-colors whitespace-nowrap"
              >
                <Download className="w-3.5 h-3.5" />
                <span>{isExportingVideo ? 'در حال خروجی...' : 'ذخیره کلیپ WebM'}</span>
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
