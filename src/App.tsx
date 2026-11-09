import React, { useState } from 'react';
import { Header, AppNavTab } from './components/Header';
import { TestPlanOverview } from './components/TestPlanOverview';
import { MethodExecutor } from './components/MethodExecutor';
import { VideoPlaybackHarness } from './components/VideoPlaybackHarness';
import { FaceDetectionPanel } from './components/FaceDetectionPanel';
import { RtspMatrixScanner } from './components/RtspMatrixScanner';
import { MediaMTXConfigurator } from './components/MediaMTXConfigurator';
import { CodecDiagnostics } from './components/CodecDiagnostics';
import { ComparisonReportView } from './components/ComparisonReportView';
import { ExportModal } from './components/ExportModal';
import { CameraOptimizer } from './components/CameraOptimizer';
import { DetectedFaceEvent, FaceDetectionSettings } from './types/faceDetection';

import {
  INITIAL_DIRECT_CONFIG,
  INITIAL_NVR_CONFIG,
  DIRECT_AUDIT_STEPS,
  NVR_AUDIT_STEPS,
} from './data/pocTestPlanData';
import { ConnectionMethod, StreamEndpointConfig, AuditStep, VerificationState } from './types/poc';
import { generateTiandyRtspUrl } from './utils/tiandySpecs';

export default function App() {
  const [activeTab, setActiveTab] = useState<AppNavTab>('plan');

  const [activeMethod, setActiveMethod] = useState<ConnectionMethod>('direct');
  const [directConfig, setDirectConfig] = useState<StreamEndpointConfig>(INITIAL_DIRECT_CONFIG);
  const [nvrConfig, setNvrConfig] = useState<StreamEndpointConfig>(INITIAL_NVR_CONFIG);

  const [directSteps, setDirectSteps] = useState<AuditStep[]>(DIRECT_AUDIT_STEPS);
  const [nvrSteps, setNvrSteps] = useState<AuditStep[]>(NVR_AUDIT_STEPS);

  const [isExportOpen, setIsExportOpen] = useState<boolean>(false);

  const [faceSettings, setFaceSettings] = useState<FaceDetectionSettings>({
    enabled: true,
    showOverlay: true,
    showConfidenceLabel: true,
    minConfidence: 75,
    autoCaptureIntervalMs: 3200,
    anprEnabled: false,
    digitalZoom: 1.0,
  });

  const [faceEvents, setFaceEvents] = useState<DetectedFaceEvent[]>([
    {
      id: 'app-face-1',
      timestamp: '14:28:12.430',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [تیاندی مستقیم]',
      confidence: 96.4,
      box: { x: 0.35, y: 0.42, width: 0.08, height: 0.14 },
      trackId: 104,
      qualityScore: 95,
      pose: 'روبرو (Frontal)',
    },
    {
      id: 'app-face-2',
      timestamp: '14:28:07.120',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [تیاندی مستقیم]',
      confidence: 92.1,
      box: { x: 0.48, y: 0.45, width: 0.08, height: 0.14 },
      trackId: 105,
      qualityScore: 89,
      pose: 'زاویه‌دار (Slight Tilt)',
    },
    {
      id: 'app-face-3',
      timestamp: '14:27:58.850',
      date: new Date().toISOString().split('T')[0],
      channelId: 1,
      channelName: 'CAM01 [تیاندی مستقیم]',
      confidence: 94.8,
      box: { x: 0.22, y: 0.40, width: 0.08, height: 0.14 },
      trackId: 103,
      qualityScore: 92,
      pose: 'روبرو (Frontal)',
    },
  ]);

  const handleUpdateStepStatus = (
    method: ConnectionMethod,
    stepId: string,
    state: VerificationState
  ) => {
    if (method === 'direct') {
      setDirectSteps((prev) =>
        prev.map((s) => (s.id === stepId ? { ...s, verificationState: state } : s))
      );
    } else {
      setNvrSteps((prev) =>
        prev.map((s) => (s.id === stepId ? { ...s, verificationState: state } : s))
      );
    }
  };

  const handleUpdateConfig = (
    method: ConnectionMethod,
    updates: Partial<StreamEndpointConfig>
  ) => {
    if (method === 'direct') {
      setDirectConfig((prev) => ({ ...prev, ...updates }));
    } else {
      setNvrConfig((prev) => ({ ...prev, ...updates }));
    }
  };

  const directRtspUrl = generateTiandyRtspUrl({
    host: directConfig.host,
    port: directConfig.rtspPort,
    channel: directConfig.channel,
    stream: directConfig.streamNumber,
    username: directConfig.username,
    safeForDisplay: true,
  });

  const nvrRtspUrl = generateTiandyRtspUrl({
    host: nvrConfig.host,
    port: nvrConfig.rtspPort,
    channel: nvrConfig.channel,
    stream: nvrConfig.streamNumber,
    username: nvrConfig.username,
    safeForDisplay: true,
  });

  return (
    <div className="min-h-screen bg-slate-100/70 text-slate-900 flex flex-col font-sans" dir="rtl">
      {/* سربرگ روشن با ناوبری یکپارچه */}
      <Header
        activeTab={activeTab}
        setActiveTab={setActiveTab}
        onOpenExport={() => setIsExportOpen(true)}
      />

      {/* بخش اصلی محتوا */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {activeTab === 'plan' && (
          <TestPlanOverview
            onSelectMethod={(method) => {
              setActiveMethod(method);
              setActiveTab('executor');
            }}
            onGoToPlayer={() => setActiveTab('player')}
          />
        )}

        {activeTab === 'executor' && (
          <MethodExecutor
            method={activeMethod}
            setMethod={setActiveMethod}
            directSteps={directSteps}
            nvrSteps={nvrSteps}
            onUpdateStepStatus={handleUpdateStepStatus}
            directConfig={directConfig}
            nvrConfig={nvrConfig}
            onUpdateConfig={handleUpdateConfig}
          />
        )}

        {activeTab === 'player' && (
          <VideoPlaybackHarness
            method={activeMethod}
            setMethod={setActiveMethod}
            directUrl={directRtspUrl}
            nvrUrl={nvrRtspUrl}
          />
        )}

        {activeTab === 'faces' && (
          <div className="space-y-6">
            <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
              <div>
                <div className="text-xs font-bold text-emerald-700 uppercase tracking-wider flex items-center gap-1.5">
                  <span className="w-2 h-2 rounded-full bg-emerald-500 animate-pulse" />
                  <span>سامانه مدیریت متمرکز رویدادهای چهره و هوش مصنوعی</span>
                </div>
                <h2 className="text-lg font-bold text-slate-900 font-display">
                  آرشیو و نظارت بر تشخیص لحظه‌ای چهره دوربین‌های نظارتی
                </h2>
              </div>
              <button
                onClick={() => setActiveTab('player')}
                className="px-4 py-2 bg-slate-900 hover:bg-slate-800 text-white text-xs font-bold rounded-lg transition-colors flex items-center gap-2 shrink-0 shadow-xs"
              >
                <span>مشاهده زنده بوم ویدیویی با کادر سبز</span>
                <span className="text-emerald-400 font-mono">←</span>
              </button>
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
              <div className="lg:col-span-8">
                <FaceDetectionPanel
                  events={faceEvents}
                  settings={faceSettings}
                  onUpdateSettings={(u) => setFaceSettings((prev) => ({ ...prev, ...u }))}
                  onClearEvents={() => setFaceEvents([])}
                  isStreamLive={true}
                />
              </div>
              <div className="lg:col-span-4 space-y-4">
                <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
                  <h4 className="text-xs font-bold text-slate-800 border-b border-slate-100 pb-2">
                    تنظیمات موتور تشخیص (Inference Engine)
                  </h4>
                  <div className="space-y-2.5 text-xs text-slate-600">
                    <div className="flex items-center justify-between">
                      <span>نرخ استخراج فریم:</span>
                      <span className="font-mono font-bold text-cyan-800">25 FPS</span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span>حساسیت تشخیص (Min Confidence):</span>
                      <span className="font-mono font-bold text-slate-800">{faceSettings.minConfidence}%</span>
                    </div>
                    <div className="flex items-center justify-between">
                      <span>حفاظت از کپسول آرشیو:</span>
                      <span className="font-bold text-emerald-700">AES-256-GCM / 96-bit</span>
                    </div>
                  </div>
                </div>

                <div className="p-4 rounded-xl bg-emerald-50 border border-emerald-200 text-xs text-emerald-900 space-y-1.5">
                  <div className="font-bold flex items-center gap-1.5 text-emerald-800">
                    <span>پایش بدون اتلاف نرخ فریم (Zero Frame Drop)</span>
                  </div>
                  <p className="text-[11px] leading-relaxed text-emerald-700">
                    با تفکیک بوم ویدیویی از بوم اورلی (Dual-Canvas Overlay)، پردازش کادرهای سبز و تله‌متری بیومتریک هیچ تاخیری در پایپ‌لاین دیکود فریم ایجاد نمی‌کند.
                  </p>
                </div>
              </div>
            </div>
          </div>
        )}

        {activeTab === 'optimizer' && <CameraOptimizer />}

        {activeTab === 'scanner' && <RtspMatrixScanner />}

        {activeTab === 'mediamtx' && <MediaMTXConfigurator />}

        {activeTab === 'diagnostics' && <CodecDiagnostics />}

        {activeTab === 'report' && <ComparisonReportView />}
      </main>

      {/* مودال خروجی اسکریپت‌ها و گزارش ممیزی */}
      <ExportModal
        isOpen={isExportOpen}
        onClose={() => setIsExportOpen(false)}
        directConfig={directConfig}
        nvrConfig={nvrConfig}
        directSteps={directSteps}
        nvrSteps={nvrSteps}
      />

      {/* پاورقی سامانه */}
      <footer className="border-t border-slate-200 bg-white py-6 text-xs text-slate-600 mt-auto">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 flex flex-col sm:flex-row items-center justify-between gap-4">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="font-bold text-slate-800">سامانه ممیزی استریم دوربین‌های تیاندی</span>
            <span aria-hidden="true">·</span>
            <span>فرمت مستند: <code className="font-mono text-cyan-800 font-bold">/CHANNEL/STREAM</code></span>
            <span aria-hidden="true">·</span>
            <span>پورت وب دوربین: <code className="font-mono text-cyan-800 font-bold">3000</code></span>
            <span aria-hidden="true">·</span>
            <span>انتقال الزامی: RTSP روی TCP</span>
          </div>
          <div>
            <span>اصل ممیزی: تفکیک یافته‌های مستند از فرضیات و سنجش فیزیکی در محل</span>
          </div>
        </div>
      </footer>
    </div>
  );
}
