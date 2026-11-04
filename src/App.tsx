import React, { useState } from 'react';
import { Header } from './components/Header';
import { TestPlanOverview } from './components/TestPlanOverview';
import { MethodExecutor } from './components/MethodExecutor';
import { VideoPlaybackHarness } from './components/VideoPlaybackHarness';
import { RtspMatrixScanner } from './components/RtspMatrixScanner';
import { MediaMTXConfigurator } from './components/MediaMTXConfigurator';
import { CodecDiagnostics } from './components/CodecDiagnostics';
import { ComparisonReportView } from './components/ComparisonReportView';
import { ExportModal } from './components/ExportModal';

import {
  INITIAL_DIRECT_CONFIG,
  INITIAL_NVR_CONFIG,
  DIRECT_AUDIT_STEPS,
  NVR_AUDIT_STEPS,
} from './data/pocTestPlanData';
import { ConnectionMethod, StreamEndpointConfig, AuditStep, VerificationState } from './types/poc';
import { generateTiandyRtspUrl } from './utils/tiandySpecs';

export default function App() {
  const [activeTab, setActiveTab] = useState<
    'plan' | 'executor' | 'player' | 'scanner' | 'mediamtx' | 'diagnostics' | 'report'
  >('plan');

  const [activeMethod, setActiveMethod] = useState<ConnectionMethod>('direct');
  const [directConfig, setDirectConfig] = useState<StreamEndpointConfig>(INITIAL_DIRECT_CONFIG);
  const [nvrConfig, setNvrConfig] = useState<StreamEndpointConfig>(INITIAL_NVR_CONFIG);

  const [directSteps, setDirectSteps] = useState<AuditStep[]>(DIRECT_AUDIT_STEPS);
  const [nvrSteps, setNvrSteps] = useState<AuditStep[]>(NVR_AUDIT_STEPS);

  const [isExportOpen, setIsExportOpen] = useState<boolean>(false);

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
