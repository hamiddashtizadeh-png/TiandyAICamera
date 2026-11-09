import React from 'react';
import { ShieldCheck, Download, Video, CheckCircle2, FileText } from 'lucide-react';

export type AppNavTab =
  | 'plan'
  | 'executor'
  | 'player'
  | 'faces'
  | 'optimizer'
  | 'scanner'
  | 'mediamtx'
  | 'diagnostics'
  | 'report';

interface HeaderProps {
  activeTab: AppNavTab;
  setActiveTab: (tab: AppNavTab) => void;
  onOpenExport: () => void;
}

export const Header: React.FC<HeaderProps> = ({
  activeTab,
  setActiveTab,
  onOpenExport,
}) => {
  return (
    <header className="sticky top-0 z-40 bg-white/90 backdrop-blur-md border-b border-slate-200 shadow-xs">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between gap-6 h-16">
          {/* بخش اول: نام سامانه */}
          <div className="flex items-center gap-3 shrink-0">
            <div className="w-9 h-9 rounded-lg bg-cyan-50 border border-cyan-200 flex items-center justify-center text-cyan-700 shadow-xs">
              <Video className="w-5 h-5" />
            </div>
            <div>
              <span className="text-base font-bold text-slate-900 tracking-tight whitespace-nowrap font-display">
                سامانه ممیزی استریم تیاندی (Tiandy POC)
              </span>
              <div className="text-[11px] text-slate-500 font-medium">
                اعتبارسنجی دیکود فریم · فرمت /1/1، /1/2، /1/3
              </div>
            </div>
          </div>

          {/* بخش دوم: منوی تب‌ها با ناوبری یکپارچه */}
          <nav className="hidden lg:flex items-center gap-5 text-xs font-medium">
            <button
              onClick={() => setActiveTab('plan')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'plan'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              ممیزی و یافته‌ها
            </button>
            <button
              onClick={() => setActiveTab('executor')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'executor'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              پروتکل آزمون مستقل (A و B)
            </button>
            <button
              onClick={() => setActiveTab('player')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium flex items-center gap-1.5 ${
                activeTab === 'player'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              <span>پخش زنده و دیکود فریم</span>
            </button>
            <button
              onClick={() => setActiveTab('faces')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium flex items-center gap-1.5 ${
                activeTab === 'faces'
                  ? 'border-emerald-600 text-emerald-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              <span>رویدادهای چهره (AI)</span>
              <span className="w-2 h-2 rounded-full bg-emerald-500 animate-pulse" title="سیستم فعال پایش چهره" />
            </button>
            <button
              onClick={() => setActiveTab('optimizer')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium flex items-center gap-1.5 ${
                activeTab === 'optimizer'
                  ? 'border-blue-600 text-blue-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              <span>بهینه‌ساز دوربین (فاز ۸)</span>
              <span className="w-2 h-2 rounded-full bg-sky-500" title="موتور بهینه‌ساز ONVIF تیاندی" />
            </button>
            <button
              onClick={() => setActiveTab('scanner')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'scanner'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              جنراتور RTSP و مسیرها
            </button>
            <button
              onClick={() => setActiveTab('mediamtx')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'mediamtx'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              پیکربندی MediaMTX
            </button>
            <button
              onClick={() => setActiveTab('diagnostics')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'diagnostics'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              بررسی فرضیات خرابی
            </button>
            <button
              onClick={() => setActiveTab('report')}
              className={`transition-colors whitespace-nowrap shrink-0 py-2 border-b-2 font-medium ${
                activeTab === 'report'
                  ? 'border-cyan-600 text-cyan-800 font-bold'
                  : 'border-transparent text-slate-600 hover:text-slate-900'
              }`}
            >
              شواهد و گزارش ممیزی
            </button>
          </nav>

          {/* بخش سوم: دکمه اقدام اصلی */}
          <div className="flex items-center gap-3 shrink-0">
            <button
              onClick={onOpenExport}
              className="px-3.5 py-2 text-xs font-semibold text-white bg-slate-900 hover:bg-slate-800 rounded-md transition-colors flex items-center gap-2 whitespace-nowrap shadow-xs"
            >
              <Download className="w-3.5 h-3.5" />
              <span>خروجی اسکریپت‌ها و گزارش</span>
            </button>
          </div>
        </div>
      </div>
    </header>
  );
};
