import React from 'react';
import {
  FileCheck,
  CheckCircle2,
  AlertTriangle,
  HelpCircle,
  ShieldAlert,
  Terminal,
  Lock,
  Layers,
  Cpu,
  Zap,
  Info,
} from 'lucide-react';
import { EMPIRICAL_COMPARISON_CRITERIA } from '../data/pocTestPlanData';

export const ComparisonReportView: React.FC = () => {
  return (
    <div className="space-y-8">
      {/* بیانیه رسمی ممیزی و سیاست عدم درج امتیازات ساختگی */}
      <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div className="flex items-center gap-2">
            <div className="w-9 h-9 rounded-lg bg-cyan-50 border border-cyan-200 flex items-center justify-center text-cyan-700">
              <FileCheck className="w-5 h-5" />
            </div>
            <div>
              <div className="text-xs font-bold text-cyan-800 uppercase tracking-wider">
                گزارش رسمی ممیزی فنی (Audit Report)
              </div>
              <h2 className="text-lg font-bold text-slate-900 font-display">
                نتیجه ممیزی و ارزیابی معماری استریم دوربین تیاندی
              </h2>
            </div>
          </div>
          <span className="text-xs bg-slate-100 text-slate-700 border border-slate-200 px-3 py-1 rounded-full font-bold">
            بدون امتیازات پیش‌فرض یا ادعای تست فیزیکی بدون مدرک
          </span>
        </div>

        <p className="text-sm text-slate-600 leading-relaxed max-w-4xl">
          مطابق با اصول مهندسی، <strong>هیچ توصیه نهایی تولیدی (Production Recommendation)</strong> پیش از اجرای آزمون‌های فیزیکی بر روی سخت‌افزار واقعی صادر نمی‌گردد. گزارش حاضر تمام داده‌ها را به ۵ دسته تفکیک کرده است: یافته‌های قطعی، علل فرضی خرابی‌های گذشته، موارد تست‌نشده، اقدامات دستی فوری و شواهد لازم برای تصمیم‌گیری.
        </p>
      </div>

      {/* ۵ بخش الزامی ممیزی */}
      <div className="space-y-6">
        {/* ۱. یافته‌های تاییدشده */}
        <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-sm font-bold text-emerald-800 border-b border-slate-100 pb-2">
            <CheckCircle2 className="w-4 h-4 text-emerald-600" />
            <span>۱. یافته‌های تاییدشده و قطعی (Verified Findings)</span>
          </div>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 text-xs text-slate-700">
            <div className="p-3.5 rounded-lg bg-slate-50 border border-slate-200 space-y-1">
              <div className="font-bold text-slate-900">فرمت مستند و قطعی آدرس RTSP تیاندی:</div>
              <div className="font-mono text-cyan-800 font-bold" dir="ltr">
                rtsp://USERNAME:PASSWORD@IP:PORT/CHANNEL/STREAM
              </div>
              <p className="text-slate-500 text-[11px] pt-1">
                مسیرهای معتبر بر اساس شماره کانال و استریم ساخته می‌شوند.
              </p>
            </div>
            <div className="p-3.5 rounded-lg bg-slate-50 border border-slate-200 space-y-1">
              <div className="font-bold text-slate-900">آدرس استریم‌های سه‌گانه:</div>
              <div className="font-mono text-slate-800" dir="ltr">
                /1/1 (Main) · /1/2 (Sub) · /1/3 (Third)
              </div>
              <p className="text-slate-500 text-[11px] pt-1">
                استریم ۱ اصلی، استریم ۲ فرعی وب، و استریم ۳ جریان سوم/موبایل است.
              </p>
            </div>
            <div className="p-3.5 rounded-lg bg-slate-50 border border-slate-200 space-y-1">
              <div className="font-bold text-slate-900">پورت مدیریت وب دوربین: ۳۰۰۰</div>
              <p className="text-slate-600 text-[11px]">
                پورت 3000 سرویس وب (HTTP GUI) دوربین تیاندی است و ارتباطی با پورت RTSP ویدیو ندارد.
              </p>
            </div>
            <div className="p-3.5 rounded-lg bg-slate-50 border border-slate-200 space-y-1">
              <div className="font-bold text-slate-900">الزام پروتکل RTSP روی TCP:</div>
              <p className="text-slate-600 text-[11px]">
                استفاده از انتقال TCP با فلگ <code className="font-mono text-slate-800 font-bold">-rtsp_transport tcp</code> جهت پیشگیری از اتلاف بسته الزامی است.
              </p>
            </div>
          </div>
        </div>

        {/* ۲. مشکلات و آزمون‌های ناموفق */}
        <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-sm font-bold text-amber-800 border-b border-slate-100 pb-2">
            <AlertTriangle className="w-4 h-4 text-amber-600" />
            <span>۲. آزمون‌های ناموفق گذشته و فرضیات تحت بررسی (Failed Tests & Hypotheses)</span>
          </div>
          <div className="space-y-2 text-xs text-slate-600 leading-relaxed">
            <p>
              در آزمون‌های قبلی کاربر، پخش تصویر هم در ویندوز (VLC/DirectShow) و هم در مرورگر وب دچار اختلال و فریز بوده است. در ممیزی حاضر:
            </p>
            <ul className="list-disc list-inside space-y-1.5 pr-2">
              <li>
                <strong>کدک S+265 علت اثبات‌شده نیست:</strong> این کدک اختصاصی تیاندی یک فرضیه محتمل است و نباید بدون بررسی هدرهای اسلایس با FFprobe به عنوان علت نهایی معرفی شود.
              </li>
              <li>
                <strong>اتلاف بسته در UDP:</strong> در سوییچ‌های شلوغ یا پورت‌های PoE داخلی، بسته‌های بزرگ فریم‌های کلیدی (I-frames) مفقود شده و تصویر تا رسیدن فریم کلیدی بعدی دچار افت یا پارگی می‌شود.
              </li>
              <li>
                <strong>رد شدن پروفایل در WebRTC SDP:</strong> عدم تطابق پروفایل H.264 در هندشیک مرورگر می‌تواند منجر به اتصال موفق سیگنالینگ اما بدون رندر فریم (0 fps) شود.
              </li>
            </ul>
          </div>
        </div>

        {/* ۳. موارد تست‌نشده */}
        <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
          <div className="flex items-center gap-2 text-sm font-bold text-cyan-800 border-b border-slate-100 pb-2">
            <ShieldAlert className="w-4 h-4 text-cyan-600" />
            <span>۳. موارد تست‌نشده نیازمند ارزیابی فیزیکی (Untested Items)</span>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs text-slate-700">
            <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
              <span className="font-bold text-slate-900 block mb-1">مدل‌های سخت‌افزاری:</span>
              مدل دقیق دوربین مداربسته تیاندی و دستگاه NVR تا زمان ورود به پنل وب نامشخص است.
            </div>
            <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
              <span className="font-bold text-slate-900 block mb-1">کدک واقعی هر ۳ استریم:</span>
              کدک، رزولوشن، FPS و GOP استریم‌های /1/1، /1/2 و /1/3 باید با FFprobe استخراج شود.
            </div>
            <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
              <span className="font-bold text-slate-900 block mb-1">شمارش فریم‌های دیکود شده:</span>
              اثبات دیکود قطعی حداقل ۵۰ فریم متوالی با دستور FFmpeg در محیط عملیاتی الزامی است.
            </div>
            <div className="p-3 rounded-lg bg-slate-50 border border-slate-200">
              <span className="font-bold text-slate-900 block mb-1">سنجش تاخیر شیشه‌به‌شیشه:</span>
              مقایسه تاخیر WebRTC اتصال مستقیم با تاخیر اتصال از طریق NVR باید در محل اندازه‌گیری شود.
            </div>
          </div>
        </div>

        {/* ۴. اقدامات دستی الزامی توسط کاربر */}
        <div className="p-6 rounded-xl bg-slate-900 text-white shadow-sm space-y-4">
          <div className="flex items-center gap-2 text-sm font-bold text-cyan-300 border-b border-slate-800 pb-2">
            <Terminal className="w-4 h-4 text-cyan-400" />
            <span>۴. اقدامات دستی الزامی توسط کاربر در شبکه محلی (Required Manual Actions)</span>
          </div>

          <div className="space-y-3 text-xs text-slate-300 leading-relaxed">
            <div className="flex items-start gap-2">
              <span className="text-cyan-400 font-mono font-bold">۱.</span>
              <span>
                <strong>بررسی پورت وب دوربین:</strong> مرورگر را باز کرده و به آدرس <code className="font-mono text-cyan-300">http://CAMERA_IP:3000</code> بروید. در منوی تنظیمات ویدیو، وضعیت فشرده‌سازی و خاموش بودن S+265 را بررسی کنید.
              </span>
            </div>
            <div className="flex items-start gap-2">
              <span className="text-cyan-400 font-mono font-bold">۲.</span>
              <span>
                <strong>پروب هر ۳ استریم با FFprobe:</strong> دستور زیر را در ترمینال اجرا کرده و خروجی JSON آن را یادداشت کنید:
                <pre className="p-2.5 rounded bg-black text-emerald-400 font-mono text-[11px] mt-1 select-all" dir="ltr">
                  ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,width,height,r_frame_rate "rtsp://admin:PASSWORD@CAMERA_IP:554/1/1"
                </pre>
              </span>
            </div>
            <div className="flex items-start gap-2">
              <span className="text-cyan-400 font-mono font-bold">۳.</span>
              <span>
                <strong>دیکود ۵۰ فریم با FFmpeg:</strong> دستور زیر را برای اثبات امکان‌پذیری دیکود اجرا کنید:
                <pre className="p-2.5 rounded bg-black text-emerald-400 font-mono text-[11px] mt-1 select-all" dir="ltr">
                  ffmpeg -rtsp_transport tcp -i "rtsp://admin:PASSWORD@CAMERA_IP:554/1/1" -vframes 50 -f null -
                </pre>
              </span>
            </div>
            <div className="flex items-start gap-2">
              <span className="text-cyan-400 font-mono font-bold">۴.</span>
              <span>
                <strong>اجرای درگاه MediaMTX:</strong> فایل پیکربندی ارائه‌شده در تب MediaMTX را ذخیره کرده و با داکر یا باینری اجرا کنید و خروجی WebRTC را در مرورگر مشاهده کنید.
              </span>
            </div>
          </div>
        </div>

        {/* ۵. شواهد الزامی برای توصیه نهایی معماری */}
        <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
          <div className="flex items-center gap-2 text-sm font-bold text-slate-900 border-b border-slate-100 pb-2">
            <Info className="w-4 h-4 text-cyan-700" />
            <span>۵. شواهد مستند برای تصمیم‌گیری معماری (Evidence Supporting Final Recommendations)</span>
          </div>

          <div className="overflow-x-auto">
            <table className="w-full text-right text-xs">
              <thead>
                <tr className="border-b border-slate-200 text-slate-600 bg-slate-50">
                  <th className="py-2.5 px-3">معیار تصمیم‌گیری</th>
                  <th className="py-2.5 px-3">اتصال مستقیم (Method A)</th>
                  <th className="py-2.5 px-3">اتصال از طریق NVR (Method B)</th>
                  <th className="py-2.5 px-3">وضعیت شواهد</th>
                  <th className="py-2.5 px-3">تحلیل فنی مهندسی</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 text-slate-700">
                {EMPIRICAL_COMPARISON_CRITERIA.map((crit, idx) => (
                  <tr key={idx} className="hover:bg-slate-50/80">
                    <td className="py-3 px-3 font-bold text-slate-900">{crit.criterion}</td>
                    <td className="py-3 px-3">{crit.directState}</td>
                    <td className="py-3 px-3">{crit.nvrState}</td>
                    <td className="py-3 px-3">
                      <span
                        className={`px-2 py-0.5 rounded text-[10px] font-bold ${
                          crit.verifiedEvidenceStatus === 'تاییدشده بر اساس آزمون'
                            ? 'bg-emerald-100 text-emerald-800'
                            : 'bg-amber-100 text-amber-800'
                        }`}
                      >
                        {crit.verifiedEvidenceStatus}
                      </span>
                    </td>
                    <td className="py-3 px-3 text-slate-600">{crit.technicalAnalysis}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </div>
  );
};
