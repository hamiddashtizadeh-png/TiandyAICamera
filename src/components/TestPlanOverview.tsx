import React from 'react';
import {
  Camera,
  Server,
  Network,
  AlertTriangle,
  CheckCircle2,
  ShieldAlert,
  ArrowLeft,
  FileCheck,
  Cpu,
  Layers,
  Terminal,
} from 'lucide-react';

interface TestPlanOverviewProps {
  onSelectMethod: (method: 'direct' | 'nvr') => void;
  onGoToPlayer: () => void;
}

export const TestPlanOverview: React.FC<TestPlanOverviewProps> = ({
  onSelectMethod,
  onGoToPlayer,
}) => {
  return (
    <div className="space-y-8">
      {/* سربرگ عنوان و وضعیت ممیزی */}
      <div className="bg-white p-6 rounded-xl border border-slate-200 shadow-xs space-y-3">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div className="text-xs font-bold text-cyan-700 uppercase tracking-widest flex items-center gap-1.5">
            <FileCheck className="w-4 h-4 text-cyan-600" />
            <span>ممیزی فنی و طرح آزمون استریمینگ تیاندی (Audit v3.0)</span>
          </div>
          <span className="text-xs bg-amber-50 text-amber-800 border border-amber-200 px-2.5 py-0.5 rounded-full font-medium">
            رویکرد شواهد-محور · تفکیک یافته‌های قطعی از فرضیات
          </span>
        </div>

        <h1 className="text-2xl sm:text-3xl font-display font-bold text-slate-900 tracking-tight">
          طرح آزمون جامع و ممیزی استریم دوربین مداربسته تیاندی (Tiandy)
        </h1>
        <p className="text-sm text-slate-600 max-w-4xl leading-relaxed">
          این سند بر مبنای ساختار مستند و تاییدشده آدرس‌های RTSP تیاندی، پروتکل آزمون دو روش مستقل (اتصال مستقیم به دوربین در برابر اتصال از طریق NVR) را تدوین کرده و آزمون مجزای هر سه استریم را بدون فرضیات اثبات‌نشده الزامی می‌سازد.
        </p>
      </div>

      {/* بخش‌های اصلی ممیزی: ۵ ستون کلیدی گزارش */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-5">
        {/* ۱. یافته‌های تاییدشده */}
        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-xs font-bold text-emerald-700">
            <CheckCircle2 className="w-4 h-4 text-emerald-600" />
            <span>۱. یافته‌های تاییدشده و قطعی</span>
          </div>
          <ul className="text-xs text-slate-600 space-y-2 list-disc list-inside leading-relaxed">
            <li>
              <strong>فرمت معتبر RTSP تیاندی:</strong>{' '}
              <code className="bg-slate-100 text-slate-800 px-1 py-0.5 rounded font-mono text-[11px]">
                rtsp://USER:PASS@IP:PORT/CHANNEL/STREAM
              </code>
            </li>
            <li>
              <strong>مسیر استریم‌ها:</strong> استریم اصلی <code className="font-mono">/1/1</code>، استریم فرعی <code className="font-mono">/1/2</code> و استریم سوم <code className="font-mono">/1/3</code> است.
            </li>
            <li>
              <strong>پورت وب دوربین:</strong> پورت <span className="font-mono text-cyan-700 font-semibold">3000</span> مربوط به مدیریت وب و سرویس HTTP است، نه پورت ویدیویی RTSP.
            </li>
            <li>
              <strong>پروتکل لایه انتقال:</strong> اجبار به استفاده از <span className="font-semibold text-slate-800">RTSP روی TCP</span> برای جلوگیری از تخریب فریم‌های کلیدی.
            </li>
          </ul>
        </div>

        {/* ۲. مشکلات گذشته و فرضیات */}
        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-xs font-bold text-amber-700">
            <AlertTriangle className="w-4 h-4 text-amber-600" />
            <span>۲. سابقه خرابی و فرضیات (اثبات‌نشده)</span>
          </div>
          <ul className="text-xs text-slate-600 space-y-2 list-disc list-inside leading-relaxed">
            <li>
              <strong>فرضیه S+265:</strong> فشرده‌سازی هوشمند S+265 یک فرضیه محتمل برای فریز وب است، اما تا قبل از استخراج NAL Units با ffprobe نباید علت قطعی تلقی شود.
            </li>
            <li>
              <strong>اتلاف بسته‌های UDP:</strong> استفاده از UDP در سوییچ‌های شلوغ یا پورت‌های PoE داخلی می‌تواند موجب پارگی تصویر شده باشد.
            </li>
            <li>
              <strong>مذاکره SDP مرورگر:</strong> ناسازگاری احتمالی پروفایل‌های H.264 High Profile در WebRTC مرورگرهای کلاینت.
            </li>
            <li>
              <strong>درایور گرافیک ویندوز:</strong> ناسازگاری شتاب سخت‌افزاری D3D11VA با فواصل نامنظم فریم‌های کلیدی.
            </li>
          </ul>
        </div>

        {/* ۳. موارد تست‌نشده */}
        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-xs font-bold text-cyan-700">
            <ShieldAlert className="w-4 h-4 text-cyan-600" />
            <span>۳. موارد تست‌نشده نیازمند ارزیابی در محل</span>
          </div>
          <ul className="text-xs text-slate-600 space-y-2 list-disc list-inside leading-relaxed">
            <li>مدل سخت‌افزاری دقیق دوربین و NVR و نسخه میان‌افزار (Firmware).</li>
            <li>کدک فعال روی استریم ۱، استریم ۲ و استریم ۳ به تفکیک.</li>
            <li>تایید دریافت فریم‌های دیکود شده حقیقی توسط FFmpeg روی شبکه واقعی.</li>
            <li>سنجش تاخیر شیشه‌به‌شیشه واقعی در مرورگر روی WebRTC و فال‌بک HLS.</li>
            <li>رفتار دوربین در اتصال مستقیم تحت بار چند کلاینت همزمان.</li>
          </ul>
        </div>
      </div>

      {/* دو روش تست مستقل: A (مستقیم) و B (از طریق NVR) */}
      <div className="space-y-4">
        <h2 className="text-lg font-display font-bold text-slate-900 tracking-tight">
          دو روش مستقل آزمون استریم
        </h2>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          {/* متد A */}
          <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div className="w-10 h-10 rounded-lg bg-cyan-50 border border-cyan-200 flex items-center justify-center text-cyan-700">
                  <Camera className="w-5 h-5" />
                </div>
                <div>
                  <div className="text-xs font-bold text-cyan-700">روش اول (Method A)</div>
                  <h3 className="text-base font-bold text-slate-900">
                    اتصال مستقیم به دوربین (Direct Camera)
                  </h3>
                </div>
              </div>
              <button
                onClick={() => onSelectMethod('direct')}
                className="text-xs text-cyan-700 hover:text-cyan-800 font-bold flex items-center gap-1 transition-colors"
              >
                <span>مشاهده مراحل و دستورات</span>
                <ArrowLeft className="w-3.5 h-3.5" />
              </button>
            </div>

            <p className="text-xs text-slate-600 leading-relaxed">
              اتصال مستقیم به دوربین از طریق IP کشف شده با Search Config. استریم‌های <code className="font-mono text-cyan-700">/1/1</code> (اصلی)، <code className="font-mono text-cyan-700">/1/2</code> (فرعی) و <code className="font-mono text-cyan-700">/1/3</code> به طور مجزا پروب و دیکود می‌شوند.
            </p>

            <div className="pt-3 border-t border-slate-100 text-xs space-y-1.5 text-slate-600">
              <div className="font-semibold text-slate-800">مراحل آزمون مستقل:</div>
              <div>۱. تست پورت RTSP (۵۵۴) و پورت مدیریت وب (۳۰۰۰)</div>
              <div>۲. پروب با FFprobe روی هر سه استریم جهت استخراج کدک و رزولوشن</div>
              <div>۳. دیکود حداقل ۵۰ فریم متوالی با FFmpeg به خروجی null</div>
              <div>۴. ارسال استریم به MediaMTX و ارزیابی WebRTC WHEP و HLS در وب و ویندوز</div>
            </div>
          </div>

          {/* متد B */}
          <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div className="w-10 h-10 rounded-lg bg-indigo-50 border border-indigo-200 flex items-center justify-center text-indigo-700">
                  <Server className="w-5 h-5" />
                </div>
                <div>
                  <div className="text-xs font-bold text-indigo-700">روش دوم (Method B)</div>
                  <h3 className="text-base font-bold text-slate-900">
                    اتصال از طریق دستگاه NVR (NVR Connection)
                  </h3>
                </div>
              </div>
              <button
                onClick={() => onSelectMethod('nvr')}
                className="text-xs text-indigo-700 hover:text-indigo-800 font-bold flex items-center gap-1 transition-colors"
              >
                <span>مشاهده مراحل و دستورات</span>
                <ArrowLeft className="w-3.5 h-3.5" />
              </button>
            </div>

            <p className="text-xs text-slate-600 leading-relaxed">
              اتصال به NVR تجمیع‌کننده کانال دوربین. ارزیابی استریم کانال مربوطه روی پورت RTSP با ساختار <code className="font-mono text-indigo-700">/1/1</code> برای سنجش ایزولاسیون شبکه و تداوم ضبط دائمی.
            </p>

            <div className="pt-3 border-t border-slate-100 text-xs space-y-1.5 text-slate-600">
              <div className="font-semibold text-slate-800">مراحل آزمون مستقل:</div>
              <div>۱. تعیین شماره کانال دوربین روی NVR و تایید پورت RTSP</div>
              <div>۲. بررسی عبور مستقیم (Passthrough) بدون ترنسکود سربار</div>
              <div>۳. دیکود ۵۰ فریم از NVR و ارزیابی پایداری تایم‌استمپ‌های RTP</div>
              <div>۴. آزمون توان توزیع همزمان (Fan-out) به چند کلاینت بدون فشار به دوربین</div>
            </div>
          </div>
        </div>
      </div>

      {/* اقدامات دستی الزامی برای کاربر */}
      <div className="p-6 rounded-xl bg-slate-900 text-slate-100 shadow-md space-y-4">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div className="flex items-center gap-2">
            <Terminal className="w-5 h-5 text-cyan-400" />
            <h3 className="text-sm font-bold text-white">
              اقدامات دستی الزامی برای شروع آزمون روی شبکه واقعی
            </h3>
          </div>
          <button
            onClick={onGoToPlayer}
            className="text-xs px-3 py-1.5 bg-cyan-500 hover:bg-cyan-400 text-slate-950 font-bold rounded transition-colors"
          >
            ورود به آزمایشگاه دیکود تصویر &larr;
          </button>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-4 gap-4 text-xs">
          <div className="p-3.5 rounded-lg bg-slate-800/80 border border-slate-700 space-y-1">
            <div className="font-bold text-cyan-300">گام اول: اسکن پورت‌ها</div>
            <div className="text-slate-300">
              با دستور <code className="font-mono text-cyan-200">nc -zv IP 554</code> پورت ویدیویی و پورت 3000 را چک کنید.
            </div>
          </div>
          <div className="p-3.5 rounded-lg bg-slate-800/80 border border-slate-700 space-y-1">
            <div className="font-bold text-cyan-300">گام دوم: بررسی پنل وب</div>
            <div className="text-slate-300">
              به <code className="font-mono text-cyan-200">http://IP:3000</code> رفته و وضعیت S+265 و کدک ویدیو را یادداشت کنید.
            </div>
          </div>
          <div className="p-3.5 rounded-lg bg-slate-800/80 border border-slate-700 space-y-1">
            <div className="font-bold text-cyan-300">گام سوم: دیکود فریم‌ها</div>
            <div className="text-slate-300">
              با FFmpeg ۵۰ فریم از <code className="font-mono text-cyan-200">/1/1</code> دیکود کنید تا خروجی واقعی ثبت شود.
            </div>
          </div>
          <div className="p-3.5 rounded-lg bg-slate-800/80 border border-slate-700 space-y-1">
            <div className="font-bold text-cyan-300">گام چهارم: درگاه MediaMTX</div>
            <div className="text-slate-300">
              درگاه را اجرا کرده و استریم را هم با WebRTC و هم HLS در مرورگر و ویندوز تست کنید.
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};
