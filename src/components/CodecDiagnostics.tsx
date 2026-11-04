import React, { useState } from 'react';
import {
  AlertTriangle,
  HelpCircle,
  Cpu,
  Monitor,
  Globe,
  Terminal,
  Copy,
  Check,
  ShieldAlert,
  FileSearch,
} from 'lucide-react';
import { FAILURE_HYPOTHESES, DiagnosticHypothesis } from '../utils/tiandySpecs';

export const CodecDiagnostics: React.FC = () => {
  const [selectedHypothesisId, setSelectedHypothesisId] = useState<string>(
    FAILURE_HYPOTHESES[0].id
  );
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  const activeHypothesis =
    FAILURE_HYPOTHESES.find((h) => h.id === selectedHypothesisId) || FAILURE_HYPOTHESES[0];

  const handleCopy = (text: string, key: string) => {
    navigator.clipboard.writeText(text);
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
  };

  return (
    <div className="space-y-6">
      {/* سربرگ */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <div className="text-xs font-bold text-amber-700 uppercase tracking-wider">
            بررسی دلایل ریشه‌ای مشکلات گذشته بدون سوگیری
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-display">
            تحلیل فرضیات خرابی پخش تصویر در وب و ویندوز
          </h2>
        </div>
        <div className="text-xs text-slate-500 max-w-sm leading-relaxed">
          هیچ فرضیه‌ای (از جمله کدک S+265) نباید علت قطعی تلقی شود تا زمانی که شواهد تجربی در خروجی لاگ‌های FFprobe آن را اثبات کند.
        </div>
      </div>

      {/* ۴ کارت فرضیه */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 text-xs">
        {FAILURE_HYPOTHESES.map((hypo) => (
          <div
            key={hypo.id}
            onClick={() => setSelectedHypothesisId(hypo.id)}
            className={`p-5 rounded-xl border transition-all cursor-pointer space-y-2.5 ${
              selectedHypothesisId === hypo.id
                ? 'bg-cyan-50/70 border-cyan-400 shadow-xs'
                : 'bg-white border-slate-200 hover:bg-slate-50'
            }`}
          >
            <div className="flex items-center justify-between">
              <span className="font-bold text-slate-800">{hypo.title.split(':')[0]}</span>
              <span className="text-[10px] px-2 py-0.5 rounded font-bold bg-amber-100 text-amber-800">
                فرضیه آزمایشی
              </span>
            </div>
            <p className="text-slate-600 leading-relaxed text-[11px] line-clamp-3">
              {hypo.description}
            </p>
            <div className="text-cyan-800 font-bold text-[11px] pt-2 border-t border-slate-200/80">
              مشاهده روش آزمون و دستور &larr;
            </div>
          </div>
        ))}
      </div>

      {/* جزئیات فرضیه انتخابی و دستور اثبات آن */}
      <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-5">
        <div className="border-b border-slate-100 pb-4">
          <div className="flex items-center gap-2 text-xs font-bold text-cyan-700 mb-1">
            <FileSearch className="w-4 h-4" />
            <span>دسته‌بندی فنی: {activeHypothesis.category}</span>
          </div>
          <h3 className="text-base font-bold text-slate-900 font-display">
            {activeHypothesis.title}
          </h3>
        </div>

        {/* شرح فرضیه */}
        <div className="space-y-1.5">
          <div className="text-xs font-bold text-slate-700">شرح مکانیسم فرضیه خرابی</div>
          <p className="text-xs text-slate-600 leading-relaxed bg-slate-50 p-3.5 rounded-lg border border-slate-200/80">
            {activeHypothesis.description}
          </p>
        </div>

        {/* روش آزمون برای اثبات یا رد فرضیه */}
        <div className="space-y-1.5">
          <div className="text-xs font-bold text-slate-700">روش آزمون علمی جهت اثبات یا رد فرضیه</div>
          <p className="text-xs text-slate-800 font-medium leading-relaxed bg-cyan-50/60 p-3.5 rounded-lg border border-cyan-200">
            {activeHypothesis.testMethod}
          </p>
        </div>

        {/* دستور اعتبارسنجی با FFprobe یا VLC */}
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-slate-700">دستور تست در ترمینال</span>
            <button
              onClick={() => handleCopy(activeHypothesis.verificationCommand, 'hypo-cmd')}
              className="text-xs text-slate-600 hover:text-slate-900 px-2 py-1 rounded bg-slate-100 border border-slate-200 flex items-center gap-1 font-medium transition-colors"
            >
              {copiedKey === 'hypo-cmd' ? (
                <>
                  <Check className="w-3.5 h-3.5 text-emerald-600" />
                  <span className="text-emerald-700">کپی شد</span>
                </>
              ) : (
                <>
                  <Copy className="w-3.5 h-3.5" />
                  <span>کپی دستور</span>
                </>
              )}
            </button>
          </div>
          <div className="p-3 rounded-lg bg-slate-900 font-mono text-xs text-cyan-300 break-all select-all border border-slate-800 text-left" dir="ltr">
            {activeHypothesis.verificationCommand}
          </div>
        </div>
      </div>
    </div>
  );
};
