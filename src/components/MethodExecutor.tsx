import React, { useState } from 'react';
import {
  Camera,
  Server,
  Terminal,
  CheckCircle2,
  AlertCircle,
  Copy,
  Check,
  Play,
  RotateCcw,
  ShieldCheck,
  Clock,
  Layers,
  HelpCircle,
  FileText,
} from 'lucide-react';
import { AuditStep, ConnectionMethod, StreamEndpointConfig, VerificationState } from '../types/poc';
import { sanitizeCommandString } from '../utils/security';

interface MethodExecutorProps {
  method: ConnectionMethod;
  setMethod: (method: ConnectionMethod) => void;
  directSteps: AuditStep[];
  nvrSteps: AuditStep[];
  onUpdateStepStatus: (method: ConnectionMethod, stepId: string, state: VerificationState) => void;
  directConfig: StreamEndpointConfig;
  nvrConfig: StreamEndpointConfig;
  onUpdateConfig: (method: ConnectionMethod, config: Partial<StreamEndpointConfig>) => void;
}

export const MethodExecutor: React.FC<MethodExecutorProps> = ({
  method,
  setMethod,
  directSteps,
  nvrSteps,
  onUpdateStepStatus,
  directConfig,
  nvrConfig,
  onUpdateConfig,
}) => {
  const currentSteps = method === 'direct' ? directSteps : nvrSteps;
  const currentConfig = method === 'direct' ? directConfig : nvrConfig;

  const [activeStepId, setActiveStepId] = useState<string>(currentSteps[0]?.id || '');
  const [copiedKey, setCopiedKey] = useState<string | null>(null);
  const [runningStepId, setRunningStepId] = useState<string | null>(null);
  const [executionLogs, setExecutionLogs] = useState<Record<string, string[]>>({});

  const activeStep = currentSteps.find((s) => s.id === activeStepId) || currentSteps[0];

  const handleCopy = (text: string, id: string) => {
    navigator.clipboard.writeText(text);
    setCopiedKey(id);
    setTimeout(() => setCopiedKey(null), 2000);
  };

  const handleRunSimulation = (step: AuditStep) => {
    setRunningStepId(step.id);
    const existing = executionLogs[step.id] || [];
    const timestamp = new Date().toLocaleTimeString('fa-IR');

    const newLogs = [
      ...existing,
      `[${timestamp}] [آغاز ارزیابی] اجرای مرحله: ${step.title}`,
      `[${timestamp}] [پروتکل انتقال] اجبار به استفاده از RTSP روی TCP (-rtsp_transport tcp)`,
      ...step.commands.map(
        (c: { command: string; expectedOutputSnippet?: string }) =>
          `[${timestamp}] [دستور اجرا] ${sanitizeCommandString(c.command)}\n       -> خروجی مورد انتظار: ${c.expectedOutputSnippet || 'تایید شد'}`
      ),
      `[${timestamp}] [توجه ممیزی] نتیجه فوق شبیه‌سازی تست است. برای تایید نهایی، دستورات را در ترمینال محلی اجرا و خروجی واقعی را ثبت کنید.`,
    ];

    setTimeout(() => {
      setExecutionLogs((prev) => ({ ...prev, [step.id]: newLogs }));
      onUpdateStepStatus(method, step.id, 'verified');
      setRunningStepId(null);
    }, 600);
  };

  const getBadgeStyle = (state: VerificationState) => {
    switch (state) {
      case 'verified':
        return 'bg-emerald-50 text-emerald-800 border-emerald-200';
      case 'untested':
        return 'bg-amber-50 text-amber-800 border-amber-200';
      case 'hypothesis':
        return 'bg-purple-50 text-purple-800 border-purple-200';
      case 'failed':
        return 'bg-rose-50 text-rose-800 border-rose-200';
    }
  };

  const getBadgeLabel = (state: VerificationState) => {
    switch (state) {
      case 'verified':
        return 'تاییدشده';
      case 'untested':
        return 'تست‌نشده (نیازمند اجرا)';
      case 'hypothesis':
        return 'فرضیه تحت بررسی';
      case 'failed':
        return 'ناموفق';
    }
  };

  return (
    <div className="space-y-6">
      {/* سوییچر متدها */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div
            className={`w-11 h-11 rounded-lg flex items-center justify-center border ${
              method === 'direct'
                ? 'bg-cyan-50 border-cyan-200 text-cyan-700'
                : 'bg-indigo-50 border-indigo-200 text-indigo-700'
            }`}
          >
            {method === 'direct' ? <Camera className="w-5 h-5" /> : <Server className="w-5 h-5" />}
          </div>
          <div>
            <div className="text-xs font-semibold text-slate-500">روش آزمون فعال</div>
            <h2 className="text-lg font-bold text-slate-900 font-display">
              {method === 'direct'
                ? 'روش اول: اتصال مستقیم به دوربین (Method A)'
                : 'روش دوم: اتصال از طریق NVR (Method B)'}
            </h2>
          </div>
        </div>

        {/* سوییچر تب‌ها */}
        <div className="flex items-center gap-1.5 p-1 bg-slate-100 rounded-lg self-start sm:self-auto border border-slate-200">
          <button
            onClick={() => {
              setMethod('direct');
              setActiveStepId(directSteps[0]?.id || '');
            }}
            className={`px-3.5 py-1.5 text-xs font-bold rounded-md transition-colors whitespace-nowrap ${
              method === 'direct'
                ? 'bg-white text-cyan-800 shadow-xs'
                : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            اتصال مستقیم (Method A)
          </button>
          <button
            onClick={() => {
              setMethod('nvr');
              setActiveStepId(nvrSteps[0]?.id || '');
            }}
            className={`px-3.5 py-1.5 text-xs font-bold rounded-md transition-colors whitespace-nowrap ${
              method === 'nvr'
                ? 'bg-white text-indigo-800 shadow-xs'
                : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            اتصال از طریق NVR (Method B)
          </button>
        </div>
      </div>

      {/* نوار پارامترهای تاییدشده و مستند با چیدمان شبکه‌ای انطباق‌پذیر (Auto-Fit Grid) */}
      <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs grid grid-cols-[repeat(auto-fit,minmax(160px,1fr))] gap-3 text-xs">
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">آدرس IP هدف</span>
          <span className="font-mono font-bold text-slate-900 text-xs truncate block" dir="ltr">{currentConfig.host}</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">پورت ویدیویی RTSP</span>
          <span className="font-mono font-bold text-slate-900 text-xs block" dir="ltr">{currentConfig.rtspPort}</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">پورت وب دوربین</span>
          <span className="font-mono font-bold text-cyan-700 text-xs block" dir="ltr">3000 (HTTP GUI)</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">مسیر استریم ۱</span>
          <span className="font-mono font-bold text-cyan-700 text-xs block" dir="ltr">{currentConfig.resolvedRtspPath}</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">پروتکل انتقال الزامی</span>
          <span className="font-mono font-bold text-emerald-700 text-xs block" dir="ltr">RTSP over TCP</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-50/80 border border-slate-200/70 transition-colors hover:bg-slate-50">
          <span className="text-slate-500 block text-[11px] mb-0.5">امنیت اعتبارنامه</span>
          <span className="font-mono text-emerald-700 flex items-center gap-1 font-bold text-xs">
            <ShieldCheck className="w-3.5 h-3.5" />
            ماسک در UI
          </span>
        </div>
      </div>

      {/* چیدمان ستونی: لیست مراحل در راست، جزئیات مرحله در چپ */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* لیست مراحل آزمون (۴ ستون در دسکتاپ، شبکه انطباق‌پذیر در تبلت/موبایل) */}
        <div className="lg:col-span-4 space-y-2">
          <div className="text-xs font-bold text-slate-700 px-1 mb-2">
            مراحل آزمون مستقل و اعتبارسنجی
          </div>

          <div className="grid grid-cols-[repeat(auto-fit,minmax(220px,1fr))] lg:grid-cols-1 gap-2">
            {currentSteps.map((step) => {
              const isActive = step.id === activeStepId;
              return (
                <button
                  key={step.id}
                  onClick={() => setActiveStepId(step.id)}
                  className={`w-full text-right p-3.5 rounded-lg border transition-all flex items-start gap-3 ${
                    isActive
                      ? 'bg-cyan-50/70 border-cyan-400 text-slate-900 shadow-xs ring-1 ring-cyan-400/40'
                      : 'bg-white border-slate-200 text-slate-700 hover:bg-slate-50 hover:border-slate-300'
                  }`}
                >
                  <div className="mt-0.5 shrink-0">
                    {step.verificationState === 'verified' ? (
                      <CheckCircle2 className="w-4 h-4 text-emerald-600" />
                    ) : (
                      <div className="w-5 h-5 rounded-full bg-slate-100 border border-slate-300 flex items-center justify-center text-[10px] text-slate-700 font-mono font-bold">
                        {step.stageNumber}
                      </div>
                    )}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-xs font-bold truncate text-slate-900">{step.title}</div>
                    <div className="text-[11px] text-slate-500 truncate mt-0.5">
                      {step.objective}
                    </div>
                  </div>
                </button>
              );
            })}
          </div>
        </div>

        {/* جزئیات مرحله انتخابی (۸ ستون) */}
        <div className="lg:col-span-8">
          {activeStep && (
            <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-5">
              {/* هدر مرحله */}
              <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-b border-slate-100 pb-4">
                <div>
                  <div className="text-xs font-mono text-cyan-700 font-bold mb-1">
                    مرحله {activeStep.stageNumber} · {method === 'direct' ? 'اتصال مستقیم' : 'اتصال از طریق NVR'}
                  </div>
                  <h3 className="text-base font-bold text-slate-900 font-display">
                    {activeStep.title}
                  </h3>
                </div>

                <div className="flex items-center gap-2">
                  <span
                    className={`text-xs px-2.5 py-1 rounded-full border font-bold ${getBadgeStyle(
                      activeStep.verificationState
                    )}`}
                  >
                    {getBadgeLabel(activeStep.verificationState)}
                  </span>

                  <button
                    onClick={() => handleRunSimulation(activeStep)}
                    disabled={runningStepId === activeStep.id}
                    className="px-3 py-1.5 text-xs font-bold bg-cyan-600 hover:bg-cyan-700 text-white rounded transition-colors flex items-center gap-1.5 whitespace-nowrap disabled:opacity-50"
                  >
                    <Play className="w-3.5 h-3.5" />
                    <span>
                      {runningStepId === activeStep.id ? 'در حال ثبت آزمون...' : 'ثبت آزمون شبیه‌سازی'}
                    </span>
                  </button>
                </div>
              </div>

              {/* هدف مرحله */}
              <div className="space-y-1.5">
                <div className="text-xs font-bold text-slate-700">هدف و دامنه آزمون</div>
                <p className="text-xs text-slate-600 leading-relaxed bg-slate-50 p-3.5 rounded-lg border border-slate-200/80">
                  {activeStep.objective}
                </p>
              </div>

              {/* پارامترهای فنی مرحله با چیدمان انطباق‌پذیر شبکه‌ای (Auto-Fit Grid) */}
              <div className="space-y-2">
                <div className="text-xs font-bold text-slate-700">پارامترهای فنی این مرحله</div>
                <div className="grid grid-cols-[repeat(auto-fit,minmax(210px,1fr))] gap-2.5 text-xs">
                  {Object.entries(activeStep.parameters).map(([key, val]: [string, string]) => (
                    <div
                      key={key}
                      className="p-3 rounded-lg bg-slate-50 border border-slate-200/80 flex justify-between gap-2 items-center hover:bg-slate-50/90 transition-colors"
                    >
                      <span className="text-slate-500 font-medium">{key}:</span>
                      <span className="font-mono text-slate-900 font-bold text-left truncate" dir="ltr">
                        {String(val)}
                      </span>
                    </div>
                  ))}
                </div>
              </div>

              {/* دستورات قابل اجرا با اعتبارنامه ماسک‌شده */}
              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <div className="text-xs font-bold text-slate-700 flex items-center gap-1.5">
                    <Terminal className="w-3.5 h-3.5 text-cyan-700" />
                    <span>دستورات خط فرمان جهت اجرای واقعی در محیط کاربر (CLI)</span>
                  </div>
                  <span className="text-[11px] text-slate-500 font-mono">
                    رمز عبور با $CAM_PASS ماسک شده است
                  </span>
                </div>

                <div className="space-y-3">
                  {activeStep.commands.map((cmd: { label: string; description: string; command: string; expectedOutputSnippet?: string }, idx: number) => {
                    const sanitized = sanitizeCommandString(cmd.command);
                    const copyKey = `${activeStep.id}-${idx}`;
                    return (
                      <div
                        key={idx}
                        className="p-3.5 rounded-lg bg-slate-50 border border-slate-200 space-y-2"
                      >
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-xs font-bold text-slate-900">{cmd.label}</span>
                          <button
                            onClick={() => handleCopy(sanitized, copyKey)}
                            className="text-xs text-slate-600 hover:text-slate-900 px-2 py-1 rounded bg-white border border-slate-200 hover:bg-slate-100 flex items-center gap-1 transition-colors font-medium shadow-2xs"
                            title="کپی دستور تمیز"
                          >
                            {copiedKey === copyKey ? (
                              <>
                                <Check className="w-3.5 h-3.5 text-emerald-600" />
                                <span className="text-emerald-700 text-[11px]">کپی شد</span>
                              </>
                            ) : (
                              <>
                                <Copy className="w-3.5 h-3.5" />
                                <span className="text-[11px]">کپی دستور</span>
                              </>
                            )}
                          </button>
                        </div>
                        <p className="text-xs text-slate-600">{cmd.description}</p>
                        <div className="p-2.5 rounded bg-slate-900 font-mono text-[11px] text-emerald-400 break-all select-all border border-slate-800 text-left" dir="ltr">
                          {sanitized}
                        </div>
                        {cmd.expectedOutputSnippet && (
                          <div className="text-[11px] text-slate-600 bg-white p-2 rounded border border-slate-200">
                            <span className="font-bold text-slate-700">خروجی مورد انتظار: </span>
                            <span>{cmd.expectedOutputSnippet}</span>
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              </div>

              {/* شواهد الزامی برای این مرحله */}
              <div className="p-3.5 rounded-lg bg-cyan-50/70 border border-cyan-200 text-xs text-cyan-900 space-y-1">
                <div className="font-bold flex items-center gap-1.5">
                  <FileText className="w-3.5 h-3.5 text-cyan-700" />
                  <span>شواهد مستند الزامی برای تایید این مرحله:</span>
                </div>
                <div className="leading-relaxed text-slate-700">{activeStep.evidenceRequired}</div>
              </div>

              {/* لاگ زنده شبیه‌سازی / ثبت نتایج */}
              {executionLogs[activeStep.id] && (
                <div className="space-y-2">
                  <div className="text-xs font-bold text-slate-700 flex items-center gap-1.5">
                    <Clock className="w-3.5 h-3.5 text-cyan-700" />
                    <span>گزارش ثبت رویدادهای ارزیابی</span>
                  </div>
                  <div className="p-3.5 rounded-lg bg-slate-900 font-mono text-xs text-slate-200 space-y-1 max-h-44 overflow-y-auto border border-slate-800 text-left" dir="ltr">
                    {executionLogs[activeStep.id].map((log, lidx) => (
                      <div key={lidx} className="whitespace-pre-wrap">
                        {log}
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
