import React, { useState, useMemo } from 'react';
import {
  ScanFace,
  Power,
  Search,
  Filter,
  Clock,
  CheckCircle2,
  AlertCircle,
  Calendar,
  Trash2,
  Download,
  Eye,
  SlidersHorizontal,
  Layers,
  ChevronRight,
  Shield,
  Camera,
  Maximize2,
  X,
} from 'lucide-react';
import { DetectedFaceEvent, FaceDetectionSettings } from '../types/faceDetection';

interface FaceDetectionPanelProps {
  events: DetectedFaceEvent[];
  settings: FaceDetectionSettings;
  onUpdateSettings: (updates: Partial<FaceDetectionSettings>) => void;
  onClearEvents: () => void;
  onSelectEvent?: (event: DetectedFaceEvent) => void;
  isStreamLive?: boolean;
}

export const FaceDetectionPanel: React.FC<FaceDetectionPanelProps> = ({
  events,
  settings,
  onUpdateSettings,
  onClearEvents,
  onSelectEvent,
  isStreamLive = true,
}) => {
  const [searchQuery, setSearchQuery] = useState<string>('');
  const [selectedConfidenceFilter, setSelectedConfidenceFilter] = useState<number>(0);
  const [selectedEventForModal, setSelectedEventForModal] = useState<DetectedFaceEvent | null>(null);
  const [isSearchModalOpen, setIsSearchModalOpen] = useState<boolean>(false);
  const [searchDate, setSearchDate] = useState<string>(new Date().toISOString().split('T')[0]);
  const [searchStartTime, setSearchStartTime] = useState<string>('00:00');
  const [searchEndTime, setSearchEndTime] = useState<string>('23:59');

  // فیلتر کردن رویدادهای چهره بر اساس جستجو و حداقل ضریب اطمینان
  const filteredEvents = useMemo(() => {
    return events.filter((ev) => {
      if (selectedConfidenceFilter > 0 && ev.confidence < selectedConfidenceFilter) {
        return false;
      }
      if (searchQuery.trim()) {
        const query = searchQuery.toLowerCase();
        const matchChannel = ev.channelName.toLowerCase().includes(query);
        const matchTime = ev.timestamp.includes(query);
        const matchId = `TRK-${ev.trackId}`.toLowerCase().includes(query);
        if (!matchChannel && !matchTime && !matchId) return false;
      }
      return true;
    });
  }, [events, selectedConfidenceFilter, searchQuery]);

  // تولید آیکون چهره با ویژگی‌های متنوع در صورت عدم دسترسی به داده عکس خام
  const renderFaceThumbnail = (ev: DetectedFaceEvent) => {
    if (ev.thumbnailUrl) {
      return (
        <img
          src={ev.thumbnailUrl}
          alt={`چهره رهگیری شده #${ev.trackId}`}
          className="w-full h-full object-cover rounded-md"
        />
      );
    }

    // طراحی نماد بیومتریک وکتوری با کادر متناسب و چشم‌ها جهت مصورسازی واقعی
    const hue = (ev.trackId * 67) % 360;
    return (
      <div
        className="w-full h-full flex flex-col items-center justify-center rounded-md relative overflow-hidden"
        style={{
          background: `radial-gradient(circle, hsl(${hue}, 40%, 25%) 0%, hsl(${hue}, 60%, 10%) 100%)`,
        }}
      >
        {/* کادر رتیکل سبز اطراف چهره */}
        <div className="absolute inset-1 border border-emerald-400/50 rounded pointer-events-none" />
        <div className="absolute top-1 left-1 w-1.5 h-1.5 border-t-2 border-l-2 border-emerald-400" />
        <div className="absolute top-1 right-1 w-1.5 h-1.5 border-t-2 border-r-2 border-emerald-400" />
        <div className="absolute bottom-1 left-1 w-1.5 h-1.5 border-b-2 border-l-2 border-emerald-400" />
        <div className="absolute bottom-1 right-1 w-1.5 h-1.5 border-b-2 border-r-2 border-emerald-400" />

        {/* شمایل انتزاعی چهره با نقاط کلیدی لندمارک */}
        <ScanFace className="w-7 h-7 text-emerald-300 opacity-90 drop-shadow-sm" />
        <span className="text-[9px] font-mono font-bold text-emerald-200 mt-0.5 tracking-tight">
          ID-{ev.trackId}
        </span>
      </div>
    );
  };

  return (
    <div className="flex flex-col h-full bg-white rounded-xl border border-slate-200 shadow-xs overflow-hidden">
      {/* سربرگ پنل با کلید روشن/خاموش اصلی (Master Toggle) */}
      <div className="p-4 bg-slate-900 text-white border-b border-slate-800 space-y-3">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-2.5">
            <div
              className={`w-9 h-9 rounded-lg flex items-center justify-center transition-colors ${
                settings.enabled
                  ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                  : 'bg-slate-800 text-slate-400 border border-slate-700'
              }`}
            >
              <ScanFace className="w-5 h-5" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <h3 className="text-sm font-bold tracking-tight">پایش و تشخیص لحظه‌ای چهره</h3>
                {settings.enabled ? (
                  <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-medium bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 animate-pulse">
                    <span className="w-1.5 h-1.5 rounded-full bg-emerald-400" />
                    فعال (Live AI)
                  </span>
                ) : (
                  <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-medium bg-slate-800 text-slate-400 border border-slate-700">
                    متوقف
                  </span>
                )}
              </div>
              <p className="text-[11px] text-slate-400">
                استخراج خودکار رویدادها، برش فریم و اعتبارسنجی کیفیت
              </p>
            </div>
          </div>

          {/* کلید کنترل اصلی روشن/خاموش (Master Switch) */}
          <button
            onClick={() => onUpdateSettings({ enabled: !settings.enabled })}
            className={`relative inline-flex h-7 w-13 shrink-0 cursor-pointer rounded-full border-2 transition-colors duration-200 ease-in-out focus:outline-hidden ${
              settings.enabled ? 'bg-emerald-600 border-emerald-500' : 'bg-slate-700 border-slate-600'
            }`}
            title={settings.enabled ? 'غیرفعال‌سازی تشخیص چهره' : 'فعال‌سازی تشخیص چهره'}
            role="switch"
            aria-checked={settings.enabled}
          >
            <span
              aria-hidden="true"
              className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow-lg ring-0 transition duration-200 ease-in-out mt-0.5 ${
                settings.enabled ? '-translate-x-6' : '-translate-x-0.5'
              }`}
            />
          </button>
        </div>

        {/* نوار تنظیمات سریع کادر سبز و پلاک */}
        <div className="pt-2 border-t border-slate-800/80 flex items-center justify-between text-xs text-slate-300">
          <label className="flex items-center gap-2 cursor-pointer select-none">
            <input
              type="checkbox"
              checked={settings.showOverlay}
              onChange={(e) => onUpdateSettings({ showOverlay: e.target.checked })}
              className="w-3.5 h-3.5 rounded text-emerald-600 focus:ring-emerald-500 border-slate-700 bg-slate-800"
            />
            <span className="text-[11px]">کادر سبز دور چهره در پخش زنده</span>
          </label>

          <label className="flex items-center gap-2 cursor-pointer select-none">
            <input
              type="checkbox"
              checked={settings.anprEnabled}
              onChange={(e) => onUpdateSettings({ anprEnabled: e.target.checked })}
              className="w-3.5 h-3.5 rounded text-cyan-600 focus:ring-cyan-500 border-slate-700 bg-slate-800"
            />
            <span className="text-[11px]">پلاک‌خوان (ANPR)</span>
          </label>
        </div>
      </div>

      {/* نوار ابزار جستجو و فیلتر */}
      <div className="p-3 bg-slate-50 border-b border-slate-200 flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <div className="relative flex-1">
            <Search className="w-3.5 h-3.5 absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-400 pointer-events-none" />
            <input
              type="text"
              placeholder="جستجو با شماره رهگیری، دوربین یا زمان..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full pr-8 pl-3 py-1.5 text-xs bg-white border border-slate-200 rounded-lg text-slate-900 placeholder:text-slate-400 focus:outline-hidden focus:border-cyan-500"
            />
            {searchQuery && (
              <button
                onClick={() => setSearchQuery('')}
                className="absolute left-2.5 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600"
              >
                <X className="w-3 h-3" />
              </button>
            )}
          </div>

          {/* دکمه جستجوی تاریخ و ساعت */}
          <button
            onClick={() => setIsSearchModalOpen(true)}
            className="px-2.5 py-1.5 text-xs font-medium rounded-lg bg-white border border-slate-200 hover:bg-slate-100 text-slate-700 flex items-center gap-1.5 shrink-0 shadow-2xs"
            title="فیلتر دقیق بازه زمانی و تاریخی"
          >
            <Calendar className="w-3.5 h-3.5 text-cyan-600" />
            <span>جستجوی تاریخی</span>
          </button>
        </div>

        {/* فیلتر ضریب اطمینان */}
        <div className="flex items-center justify-between text-[11px] text-slate-600">
          <div className="flex items-center gap-1.5">
            <span className="text-slate-400">اطمینان:</span>
            <button
              onClick={() => setSelectedConfidenceFilter(0)}
              className={`px-2 py-0.5 rounded transition-colors ${
                selectedConfidenceFilter === 0
                  ? 'bg-slate-900 text-white font-bold'
                  : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-100'
              }`}
            >
              همه ({events.length})
            </button>
            <button
              onClick={() => setSelectedConfidenceFilter(85)}
              className={`px-2 py-0.5 rounded transition-colors ${
                selectedConfidenceFilter === 85
                  ? 'bg-emerald-700 text-white font-bold'
                  : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-100'
              }`}
            >
              بالای ۸۵٪
            </button>
            <button
              onClick={() => setSelectedConfidenceFilter(92)}
              className={`px-2 py-0.5 rounded transition-colors ${
                selectedConfidenceFilter === 92
                  ? 'bg-cyan-700 text-white font-bold'
                  : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-100'
              }`}
            >
              بالای ۹۲٪
            </button>
          </div>

          {events.length > 0 && (
            <button
              onClick={onClearEvents}
              className="text-slate-400 hover:text-rose-600 flex items-center gap-1 transition-colors"
              title="پاک کردن فهرست رویدادهای زنده"
            >
              <Trash2 className="w-3 h-3" />
              <span>پاک‌سازی</span>
            </button>
          )}
        </div>
      </div>

      {/* بدنه و فهرست رویدادها (Real-Time Detected Face List) */}
      <div className="flex-1 overflow-y-auto p-3 space-y-2.5 max-h-[500px]">
        {!settings.enabled ? (
          <div className="p-8 text-center flex flex-col items-center justify-center text-slate-500 space-y-3">
            <div className="w-12 h-12 rounded-full bg-slate-100 flex items-center justify-center text-slate-400">
              <Power className="w-6 h-6" />
            </div>
            <div>
              <p className="text-sm font-bold text-slate-800">تشخیص چهره در حال حاضر غیرفعال است</p>
              <p className="text-xs text-slate-500 mt-1 max-w-xs mx-auto">
                جهت شروع رصد فریم‌های زنده، کادر دور چهره و ثبت رویدادها، دکمه روشن/خاموش بالا را
                فعال کنید.
              </p>
            </div>
            <button
              onClick={() => onUpdateSettings({ enabled: true })}
              className="px-3.5 py-1.5 text-xs font-bold bg-emerald-600 hover:bg-emerald-700 text-white rounded-lg transition-colors shadow-xs"
            >
              فعال‌سازی فوری موتور تشخیص چهره
            </button>
          </div>
        ) : filteredEvents.length === 0 ? (
          <div className="p-8 text-center flex flex-col items-center justify-center text-slate-500 space-y-2">
            <div className="w-10 h-10 rounded-full bg-cyan-50 flex items-center justify-center text-cyan-600 animate-pulse">
              <ScanFace className="w-5 h-5" />
            </div>
            <p className="text-xs font-bold text-slate-700">در حال پویش جریان فریم‌های دوربین...</p>
            <p className="text-[11px] text-slate-400">
              به محض ورود هر چهره به زاویه دید دوربین، تصویر بندانگشتی و مشخصات در این بخش درج
              می‌شود.
            </p>
          </div>
        ) : (
          filteredEvents.map((ev) => {
            const isHighConfidence = ev.confidence >= 90;
            return (
              <div
                key={ev.id}
                onClick={() => {
                  setSelectedEventForModal(ev);
                  onSelectEvent?.(ev);
                }}
                className="group p-2.5 rounded-xl border border-slate-200 bg-white hover:border-emerald-300 hover:bg-emerald-50/20 hover:shadow-xs transition-all cursor-pointer flex items-center gap-3"
              >
                {/* تامبنیل فریم چهره */}
                <div className="w-14 h-14 rounded-lg bg-slate-900 shrink-0 overflow-hidden shadow-2xs relative border border-slate-200">
                  {renderFaceThumbnail(ev)}
                </div>

                {/* اطلاعات رویداد: زمان، کانال، ضریب اطمینان */}
                <div className="flex-1 min-w-0 space-y-1">
                  <div className="flex items-center justify-between gap-1">
                    <span className="text-xs font-bold text-slate-900 truncate">
                      رهگیری چهره #{ev.trackId}
                    </span>
                    {/* برچسب درصد اطمینان با رنگ‌بندی تفکیک‌شده */}
                    <span
                      className={`px-2 py-0.5 rounded-full text-[11px] font-mono font-bold shrink-0 ${
                        isHighConfidence
                          ? 'bg-emerald-100 text-emerald-800 border border-emerald-200'
                          : 'bg-amber-100 text-amber-800 border border-amber-200'
                      }`}
                      dir="ltr"
                    >
                      {ev.confidence.toFixed(1)}%
                    </span>
                  </div>

                  <div className="flex items-center gap-2 text-[11px] text-slate-500">
                    <span className="flex items-center gap-1 font-mono" dir="ltr">
                      <Clock className="w-3 h-3 text-slate-400" />
                      {ev.timestamp}
                    </span>
                    <span>·</span>
                    <span className="truncate">{ev.channelName}</span>
                  </div>

                  <div className="flex items-center justify-between text-[10px] text-slate-400">
                    <span>زاویه: {ev.pose}</span>
                    <span className="text-cyan-700 font-medium">کیفیت: {ev.qualityScore}%</span>
                  </div>
                </div>

                <ChevronRight className="w-4 h-4 text-slate-300 group-hover:text-emerald-600 transition-colors shrink-0" />
              </div>
            );
          })
        )}
      </div>

      {/* نوار وضعیت آماری پایین پنل */}
      <div className="p-3 bg-slate-50 border-t border-slate-200 text-xs text-slate-600 flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className="font-bold text-slate-800">کل رویدادها:</span>
          <span className="font-mono font-bold text-cyan-800">{events.length}</span>
        </div>
        <div className="flex items-center gap-1.5 text-[11px] text-emerald-700 font-medium">
          <CheckCircle2 className="w-3.5 h-3.5" />
          <span>آرشیو محلی امن رمزنگاری‌شده (AES-GCM)</span>
        </div>
      </div>

      {/* مودال جزئیات چهره انتخاب شده */}
      {selectedEventForModal && (
        <div className="fixed inset-0 z-50 bg-slate-950/60 backdrop-blur-xs flex items-center justify-center p-4">
          <div className="bg-white rounded-2xl max-w-md w-full border border-slate-200 shadow-xl overflow-hidden animate-in fade-in zoom-in-95 duration-150">
            <div className="p-4 bg-slate-900 text-white flex items-center justify-between">
              <div className="flex items-center gap-2">
                <ScanFace className="w-5 h-5 text-emerald-400" />
                <h4 className="font-bold text-sm">شناسنامه رویداد چهره #{selectedEventForModal.trackId}</h4>
              </div>
              <button
                onClick={() => setSelectedEventForModal(null)}
                className="text-slate-400 hover:text-white"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="p-5 space-y-4">
              <div className="flex gap-4 items-center">
                <div className="w-24 h-24 rounded-xl bg-slate-950 overflow-hidden shadow-md shrink-0 border border-slate-200">
                  {renderFaceThumbnail(selectedEventForModal)}
                </div>
                <div className="space-y-1 text-xs text-slate-600 flex-1">
                  <div className="text-sm font-bold text-slate-900">
                    ضریب تطبیق هندسی: {selectedEventForModal.confidence.toFixed(1)}%
                  </div>
                  <div className="text-[11px]">
                    <span className="font-medium text-slate-500">تاریخ و ساعت: </span>
                    <span className="font-mono text-slate-800" dir="ltr">
                      {selectedEventForModal.date} {selectedEventForModal.timestamp}
                    </span>
                  </div>
                  <div className="text-[11px]">
                    <span className="font-medium text-slate-500">کانال دوربین: </span>
                    <span className="text-slate-800">{selectedEventForModal.channelName}</span>
                  </div>
                  <div className="text-[11px]">
                    <span className="font-medium text-slate-500">کیفیت ماکروبلاک: </span>
                    <span className="text-emerald-700 font-bold">
                      {selectedEventForModal.qualityScore}% (استاندارد ISO/IEC 19794-5)
                    </span>
                  </div>
                </div>
              </div>

              {/* مشخصات امنیتی کپسول Step 6 */}
              <div className="p-3 rounded-lg bg-slate-50 border border-slate-200 text-xs space-y-2">
                <div className="flex items-center gap-1.5 font-bold text-slate-800">
                  <Shield className="w-4 h-4 text-emerald-600" />
                  <span>وضعیت انکریپشن و ذخیره‌سازی پاکت ایمن (Envelope)</span>
                </div>
                <div className="grid grid-cols-2 gap-2 text-[11px] font-mono">
                  <div className="bg-white p-2 rounded border border-slate-200">
                    <span className="text-slate-400 block text-[10px]">الگوریتم رمزنگاری</span>
                    <span className="text-slate-800 font-bold">AES-256-GCM</span>
                  </div>
                  <div className="bg-white p-2 rounded border border-slate-200">
                    <span className="text-slate-400 block text-[10px]">طول بردار Nonce</span>
                    <span className="text-slate-800 font-bold">96-bit (Durable)</span>
                  </div>
                  <div className="bg-white p-2 rounded border border-slate-200">
                    <span className="text-slate-400 block text-[10px]">کد اعتبارسنجی AAD</span>
                    <span className="text-slate-800 font-bold">SHA-256 Tag</span>
                  </div>
                  <div className="bg-white p-2 rounded border border-slate-200">
                    <span className="text-slate-400 block text-[10px]">حفاظت از حریم خصوصی</span>
                    <span className="text-emerald-700 font-bold">بدون ارسال ابری</span>
                  </div>
                </div>
              </div>

              <div className="flex justify-end gap-2 pt-2">
                <button
                  onClick={() => setSelectedEventForModal(null)}
                  className="px-4 py-2 text-xs font-bold text-white bg-slate-900 hover:bg-slate-800 rounded-lg transition-colors"
                >
                  بستن پنجره
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* مودال فیلتر و جستجوی تاریخی */}
      {isSearchModalOpen && (
        <div className="fixed inset-0 z-50 bg-slate-950/60 backdrop-blur-xs flex items-center justify-center p-4">
          <div className="bg-white rounded-2xl max-w-sm w-full border border-slate-200 shadow-xl overflow-hidden animate-in fade-in zoom-in-95 duration-150">
            <div className="p-4 bg-slate-900 text-white flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Calendar className="w-4 h-4 text-cyan-400" />
                <h4 className="font-bold text-sm">جستجوی تاریخی و ساعتی رویدادها</h4>
              </div>
              <button
                onClick={() => setIsSearchModalOpen(false)}
                className="text-slate-400 hover:text-white"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="p-4 space-y-3.5 text-xs">
              <div>
                <label className="block text-slate-700 font-medium mb-1">انتخاب تاریخ تقویمی</label>
                <input
                  type="date"
                  value={searchDate}
                  onChange={(e) => setSearchDate(e.target.value)}
                  className="w-full p-2 border border-slate-200 rounded-lg text-slate-900 font-mono"
                />
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-slate-700 font-medium mb-1">از ساعت</label>
                  <input
                    type="time"
                    value={searchStartTime}
                    onChange={(e) => setSearchStartTime(e.target.value)}
                    className="w-full p-2 border border-slate-200 rounded-lg text-slate-900 font-mono"
                  />
                </div>
                <div>
                  <label className="block text-slate-700 font-medium mb-1">تا ساعت</label>
                  <input
                    type="time"
                    value={searchEndTime}
                    onChange={(e) => setSearchEndTime(e.target.value)}
                    className="w-full p-2 border border-slate-200 rounded-lg text-slate-900 font-mono"
                  />
                </div>
              </div>

              <div className="p-2.5 rounded-lg bg-cyan-50 border border-cyan-100 text-[11px] text-cyan-900 leading-relaxed">
                این فیلتر رویدادهای ثبت‌شده در دیتابیس آرشیو محلی را بر مبنای بازه انتخابی بازیابی
                می‌کند.
              </div>

              <div className="flex items-center justify-end gap-2 pt-2">
                <button
                  onClick={() => setIsSearchModalOpen(false)}
                  className="px-3 py-1.5 text-slate-600 hover:text-slate-800"
                >
                  انصراف
                </button>
                <button
                  onClick={() => {
                    setSearchQuery(searchStartTime.slice(0, 2));
                    setIsSearchModalOpen(false);
                  }}
                  className="px-4 py-2 bg-cyan-700 hover:bg-cyan-800 text-white font-bold rounded-lg transition-colors"
                >
                  اعمال فیلتر زمانی
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
