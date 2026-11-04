import React, { useState } from 'react';
import {
  Search,
  CheckCircle2,
  XCircle,
  Copy,
  Check,
  ShieldCheck,
  Layers,
  Globe,
  Terminal,
  HelpCircle,
} from 'lucide-react';
import {
  generateTiandyRtspUrl,
  DOCUMENTED_TIANDY_STREAMS,
  CANDIDATE_RTSP_PATHS,
  VENDOR_ADAPTERS,
} from '../utils/tiandySpecs';
import { StreamProfile } from '../types/poc';

export const RtspMatrixScanner: React.FC = () => {
  const [selectedVendor, setSelectedVendor] = useState<string>('tiandy');
  const [hostIp, setHostIp] = useState<string>('192.168.1.168');
  const [rtspPort, setRtspPort] = useState<number>(554);
  const [webPort, setWebPort] = useState<number>(3000);
  const [channel, setChannel] = useState<number>(1);
  const [streamNumber, setStreamNumber] = useState<1 | 2 | 3>(1);

  const [streams, setStreams] = useState<StreamProfile[]>(DOCUMENTED_TIANDY_STREAMS);
  const [isProbing, setIsProbing] = useState<boolean>(false);
  const [activeProbeLog, setActiveProbeLog] = useState<string[]>([]);
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  const generatedDisplayUrl = generateTiandyRtspUrl({
    host: hostIp,
    port: rtspPort,
    channel,
    stream: streamNumber,
    username: 'admin',
    safeForDisplay: true,
  });

  const handleCopy = (text: string, key: string) => {
    navigator.clipboard.writeText(text);
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
  };

  const handleProbeAllStreams = () => {
    setIsProbing(true);
    const time = new Date().toLocaleTimeString('fa-IR');

    setActiveProbeLog([
      `[${time}] [آغاز پروب] بررسی سه استریم مستقل روی ${hostIp}:${rtspPort}...`,
      `[${time}] [پروتکل انتقال] اجبار به RTSP روی TCP (-rtsp_transport tcp)`,
      `[${time}] [استریم ۱] پروب مسیر مستند /${channel}/1 -> فرمت مورد انتظار H.264 یا H.265 اصلی`,
      `[${time}] [استریم ۲] پروب مسیر مستند /${channel}/2 -> فرمت مورد انتظار H.264 فرعی مناسب وب`,
      `[${time}] [استریم ۳] پروب مسیر مستند /${channel}/3 -> فرمت مورد انتظار H.264/MJPEG سبک`,
      `[${time}] [پورت وب] پورت وب و مدیریت دوربین برابر با ${webPort} است (HTTP Web GUI، غیر RTSP)`,
      `[${time}] [توجه ممیزی] این لاگ شبیه‌سازی مراحل پروب است. برای دریافت پارامترهای واقعی، دستور FFprobe ارائه‌شده در کادر پایین را روی شبکه واقعی اجرا کنید.`,
    ]);

    setTimeout(() => {
      setIsProbing(false);
    }, 700);
  };

  return (
    <div className="space-y-6">
      {/* سربرگ */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <div className="text-xs font-bold text-cyan-700 uppercase tracking-wider">
            فرمت مستند و تاییدشده تیاندی
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-display">
            جنراتور آدرس RTSP تیاندی و آزمون مستقل ۳ استریم
          </h2>
        </div>

        {/* سوییچر آداپتور سازنده */}
        <div className="flex items-center gap-2">
          <span className="text-xs text-slate-500 font-medium">آداپتور سازنده:</span>
          <select
            value={selectedVendor}
            onChange={(e) => setSelectedVendor(e.target.value)}
            className="bg-slate-50 border border-slate-300 text-xs text-slate-800 rounded-lg px-3 py-1.5 focus:outline-none focus:border-cyan-600 font-medium"
          >
            {VENDOR_ADAPTERS.map((adapter) => (
              <option key={adapter.id} value={adapter.id}>
                {adapter.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* باکس ساخت آدرس RTSP تیاندی */}
      <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
        <div className="flex items-center justify-between border-b border-slate-100 pb-3 flex-wrap gap-2">
          <div className="flex items-center gap-2 text-xs font-bold text-slate-800">
            <Layers className="w-4 h-4 text-cyan-700" />
            <span>فرمت استاندارد تیاندی: rtsp://USERNAME:PASSWORD@IP:PORT/CHANNEL/STREAM</span>
          </div>
          <span className="text-xs text-emerald-700 font-mono font-bold flex items-center gap-1">
            <ShieldCheck className="w-3.5 h-3.5" />
            امنیت اعتبارنامه: بدون نشت در لاگ‌ها و UI
          </span>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4 text-xs">
          {/* IP دوربین */}
          <div className="space-y-1">
            <label className="text-slate-600 font-medium">آدرس IP دوربین / NVR</label>
            <input
              type="text"
              value={hostIp}
              onChange={(e) => setHostIp(e.target.value)}
              className="w-full bg-slate-50 border border-slate-300 text-slate-900 font-mono text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-cyan-600 text-left"
              dir="ltr"
            />
          </div>

          {/* پورت RTSP */}
          <div className="space-y-1">
            <label className="text-slate-600 font-medium">پورت RTSP ویدیویی</label>
            <input
              type="number"
              value={rtspPort}
              onChange={(e) => setRtspPort(Number(e.target.value))}
              className="w-full bg-slate-50 border border-slate-300 text-slate-900 font-mono text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-cyan-600 text-left"
              dir="ltr"
            />
            <span className="text-[10px] text-slate-500 block">معمولاً 554 (باید اسکن شود)</span>
          </div>

          {/* پورت مدیریت وب */}
          <div className="space-y-1">
            <label className="text-slate-600 font-medium">پورت مدیریت وب دوربین</label>
            <input
              type="number"
              value={webPort}
              onChange={(e) => setWebPort(Number(e.target.value))}
              className="w-full bg-slate-50 border border-slate-300 text-cyan-800 font-bold font-mono text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-cyan-600 text-left"
              dir="ltr"
            />
            <span className="text-[10px] text-cyan-700 font-bold block">
              پورت ۳۰۰۰ وب تیاندی (HTTP GUI)
            </span>
          </div>

          {/* شماره کانال */}
          <div className="space-y-1">
            <label className="text-slate-600 font-medium">شماره کانال (1 تا N)</label>
            <input
              type="number"
              min={1}
              max={64}
              value={channel}
              onChange={(e) => setChannel(Number(e.target.value))}
              className="w-full bg-slate-50 border border-slate-300 text-slate-900 font-mono text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-cyan-600 text-left"
              dir="ltr"
            />
          </div>

          {/* شماره استریم */}
          <div className="space-y-1">
            <label className="text-slate-600 font-medium">شماره استریم (1، 2، 3)</label>
            <select
              value={streamNumber}
              onChange={(e) => setStreamNumber(Number(e.target.value) as 1 | 2 | 3)}
              className="w-full bg-slate-50 border border-slate-300 text-cyan-900 font-bold text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-cyan-600"
            >
              <option value={1}>استریم ۱: اصلی (/{channel}/1)</option>
              <option value={2}>استریم ۲: فرعی وب (/{channel}/2)</option>
              <option value={3}>استریم ۳: سوم/موبایل (/{channel}/3)</option>
            </select>
          </div>
        </div>

        {/* کادر آدرس نهایی تولیدشده */}
        <div className="p-3.5 rounded-lg bg-slate-900 border border-slate-800 space-y-2">
          <div className="flex items-center justify-between text-xs">
            <span className="font-mono text-slate-400">آدرس RTSP معتبر و ماسک‌شده:</span>
            <button
              onClick={() => handleCopy(generatedDisplayUrl, 'display-url')}
              className="px-2.5 py-1 text-xs rounded bg-slate-800 hover:bg-slate-700 text-white transition-colors flex items-center gap-1 font-sans"
            >
              {copiedKey === 'display-url' ? (
                <>
                  <Check className="w-3.5 h-3.5 text-emerald-400" />
                  <span className="text-emerald-400">کپی شد</span>
                </>
              ) : (
                <>
                  <Copy className="w-3.5 h-3.5" />
                  <span>کپی آدرس امن</span>
                </>
              )}
            </button>
          </div>
          <div className="font-mono text-xs text-cyan-300 select-all break-all text-left" dir="ltr">
            {generatedDisplayUrl}
          </div>
        </div>
      </div>

      {/* ماتریس آزمون ۳ استریم مستقل */}
      <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
          <div>
            <div className="text-xs font-bold text-slate-500 uppercase">
              قانون ممیزی: هرگز فرض نمی‌شود تمام استریم‌ها کدک یکسانی دارند
            </div>
            <h3 className="text-base font-bold text-slate-900">
              آزمون مستقل استریم‌های سه‌گانه تیاندی
            </h3>
          </div>

          <button
            onClick={handleProbeAllStreams}
            disabled={isProbing}
            className="px-4 py-2 text-xs font-bold bg-cyan-600 hover:bg-cyan-700 text-white rounded-lg transition-colors flex items-center gap-2 self-start sm:self-auto disabled:opacity-50"
          >
            <Search className="w-4 h-4" />
            <span>{isProbing ? 'در حال شبیه‌سازی پروب...' : 'شبیه‌سازی پروب هر ۳ استریم'}</span>
          </button>
        </div>

        {/* ۳ کارت مجزا برای استریم‌ها */}
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {streams.map((s) => {
            const streamPath = `/${channel}/${s.streamNumber}`;
            const probeCmd = `ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,width,height,r_frame_rate,bit_rate -of json "rtsp://admin:$CAM_PASS@${hostIp}:${rtspPort}${streamPath}"`;
            const decodeCmd = `ffmpeg -rtsp_transport tcp -i "rtsp://admin:$CAM_PASS@${hostIp}:${rtspPort}${streamPath}" -vframes 50 -f null -`;

            return (
              <div
                key={s.streamNumber}
                className="p-4 rounded-xl bg-slate-50 border border-slate-200 space-y-3 flex flex-col justify-between"
              >
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="font-mono text-xs font-bold text-cyan-800 bg-cyan-100/70 px-2 py-0.5 rounded">
                      مسیر: {streamPath}
                    </span>
                    <span className="text-[10px] px-2 py-0.5 rounded font-bold bg-amber-100 text-amber-800">
                      نیازمند تست در محل
                    </span>
                  </div>

                  <h4 className="text-sm font-bold text-slate-900">{s.name}</h4>

                  <div className="space-y-1.5 text-xs text-slate-700">
                    <div className="flex justify-between">
                      <span className="text-slate-500">کدک هدف:</span>
                      <span className="font-mono font-bold text-slate-900">{s.targetCodec}</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-slate-500">کدک کشف‌شده:</span>
                      <span className="font-mono text-amber-800 font-medium">
                        {s.detectedCodec}
                      </span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-slate-500">رزولوشن هدف:</span>
                      <span className="font-mono">{s.resolution}</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-slate-500">نرخ فریم:</span>
                      <span className="font-mono">{s.fps} fps</span>
                    </div>
                  </div>

                  <p className="text-[11px] text-slate-600 leading-relaxed pt-2 border-t border-slate-200">
                    {s.notes}
                  </p>
                </div>

                {/* دکمه‌های کپی دستورات این استریم */}
                <div className="pt-2 border-t border-slate-200 space-y-1.5 text-xs">
                  <button
                    onClick={() => handleCopy(decodeCmd, `decode-${s.streamNumber}`)}
                    className="w-full py-1.5 px-2 rounded-md bg-white border border-slate-300 hover:bg-slate-100 text-slate-800 flex items-center justify-center gap-1.5 transition-colors font-medium text-[11px]"
                  >
                    <Terminal className="w-3 h-3 text-cyan-700" />
                    <span>
                      {copiedKey === `decode-${s.streamNumber}`
                        ? 'دستور FFmpeg کپی شد'
                        : 'کپی دستور دیکود FFmpeg'}
                    </span>
                  </button>
                </div>
              </div>
            );
          })}
        </div>

        {/* لاگ شبیه‌سازی */}
        {activeProbeLog.length > 0 && (
          <div className="p-3.5 rounded-lg bg-slate-900 font-mono text-xs text-slate-200 space-y-1 max-h-48 overflow-y-auto text-left" dir="ltr">
            {activeProbeLog.map((log, idx) => (
              <div key={idx} className="whitespace-pre-wrap">
                {log}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* تفکیک کاندیدهای اولیه از ثانویه */}
      <div className="p-6 rounded-xl bg-white border border-slate-200 shadow-xs space-y-4">
        <div className="flex items-center justify-between">
          <div className="text-xs font-bold text-slate-700">
            جدول اولویت‌بندی مسیرهای RTSP (کاندیدهای مستند تیاندی در برابر مسیرهای تجربی)
          </div>
          <span className="text-[11px] text-slate-500">تفکیک بر مبنای مستندات</span>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-right text-xs">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600 bg-slate-50">
                <th className="py-2.5 px-3">مسیر RTSP</th>
                <th className="py-2.5 px-3">اولویت در ممیزی</th>
                <th className="py-2.5 px-3">نوع استریم</th>
                <th className="py-2.5 px-3">توضیحات و مستندات فنی</th>
                <th className="py-2.5 px-3 text-left">اقدام</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {CANDIDATE_RTSP_PATHS.map((c) => {
                const cmd = `ffprobe -v error -rtsp_transport tcp -show_entries stream=codec_name "rtsp://admin:$CAM_PASS@${hostIp}:${rtspPort}${c.path}"`;
                return (
                  <tr key={c.path} className="hover:bg-slate-50/80">
                    <td className="py-2.5 px-3 font-mono font-bold text-cyan-800 text-left" dir="ltr">
                      {c.path}
                    </td>
                    <td className="py-2.5 px-3">
                      <span
                        className={`px-2 py-0.5 rounded text-[10px] font-bold ${
                          c.category === 'primary_documented'
                            ? 'bg-emerald-100 text-emerald-800'
                            : 'bg-slate-100 text-slate-600'
                        }`}
                      >
                        {c.verificationStatus}
                      </span>
                    </td>
                    <td className="py-2.5 px-3 text-slate-700">استریم {c.streamNumber}</td>
                    <td className="py-2.5 px-3 text-slate-600">{c.description}</td>
                    <td className="py-2.5 px-3 text-left">
                      <button
                        onClick={() => handleCopy(cmd, c.path)}
                        className="px-2 py-1 text-[11px] rounded bg-slate-100 hover:bg-slate-200 text-slate-700 font-medium transition-colors"
                      >
                        {copiedKey === c.path ? 'کپی شد' : 'کپی دستور'}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
