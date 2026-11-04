import React, { useState } from 'react';
import {
  Layers,
  Copy,
  Check,
  Download,
  Terminal,
  ShieldCheck,
  CheckCircle2,
  Globe,
  Radio,
  FileCode,
} from 'lucide-react';

export const MediaMTXConfigurator: React.FC = () => {
  const [enableTranscodeFallback, setEnableTranscodeFallback] = useState<boolean>(false);
  const [copied, setCopied] = useState<boolean>(false);

  const mediaMtxYaml = `# پیکربندی یکپارچه درگاه MediaMTX برای آزمون استریم دوربین‌های تیاندی
# الزامات: اجبار به RTSP روی TCP، پشتیبانی از WebRTC (WHEP) و HLS (fMP4)

logLevel: info
logDestinations: [stdout]

# لایه انتقال عمومی: اجبار به TCP برای پیشگیری از اتلاف بسته در UDP
protocols: [tcp]

# سرور RTSP داخلی جهت بازپخش به کلاینت‌های ویندوز (VLC / FFplay)
rtsp: yes
rtspAddress: :8554
protocols: [tcp]

# سرور WebRTC (WHEP) برای پخش مستقیم در مرورگر وب با حداقل تاخیر (<250ms)
webrtc: yes
webrtcAddress: :8889
webrtcEncryption: no
webrtcLocalUDPAddress: :8189
webrtcIPsFromInterfaces: yes
webrtcHandshakeTimeout: 10s

# سرور HLS به عنوان فال‌بک سگمنتی برای شبکه‌هایی که WebRTC را مسدود کرده‌اند
hls: yes
hlsAddress: :8888
hlsVariant: fmp4
hlsSegmentCount: 5
hlsSegmentDuration: 1s
hlsPartDuration: 200ms

# تعریف مسیرهای استریم (مستقل از برند و سازنده دوربین)
paths:
  # ========================================================
  # استریم ۱ دوربین مستقیم تیاندی (جریان اصلی /1/1)
  # ========================================================
  tiandy_stream1:
    source: rtsp://$CAM_USER:$CAM_PASS@192.168.1.168:554/1/1
    sourceProtocol: tcp
    sourceOnDemand: no
    maxReaders: 0

  # ========================================================
  # استریم ۲ دوربین مستقیم تیاندی (جریان فرعی وب /1/2)
  # ========================================================
  tiandy_stream2:
    source: rtsp://$CAM_USER:$CAM_PASS@192.168.1.168:554/1/2
    sourceProtocol: tcp
    sourceOnDemand: no
    maxReaders: 0

  # ========================================================
  # استریم ۳ دوربین مستقیم تیاندی (جریان سبک/موبایل /1/3)
  # ========================================================
  tiandy_stream3:
    source: rtsp://$CAM_USER:$CAM_PASS@192.168.1.168:554/1/3
    sourceProtocol: tcp
    sourceOnDemand: no
    maxReaders: 0

  # ========================================================
  # استریم کانال ۱ از طریق دستگاه NVR (/1/1)
  # ========================================================
  tiandy_nvr_ch1:
    source: rtsp://$NVR_USER:$NVR_PASS@192.168.1.200:554/1/1
    sourceProtocol: tcp
    sourceOnDemand: no
    maxReaders: 0
${
  enableTranscodeFallback
    ? `
  # ========================================================
  # خط لوله ترنسکود احتیاطی (تنها در صورت تایید خروجی H.265/S+265)
  # تبدیل به H.264 Baseline برای مرورگرهایی که فاقد شتاب‌دهنده HEVC هستند
  # ========================================================
  tiandy_stream1_transcode:
    runOnInit: >
      ffmpeg -rtsp_transport tcp -i rtsp://$CAM_USER:$CAM_PASS@192.168.1.168:554/1/1
      -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p
      -b:v 3500k -g 50 -c:a aac -ar 44100
      -f rtsp rtsp://localhost:8554/tiandy_stream1_transcode
    runOnInitRestart: yes
`
    : ''
}`;

  const handleCopy = () => {
    navigator.clipboard.writeText(mediaMtxYaml);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleDownload = () => {
    const blob = new Blob([mediaMtxYaml], { type: 'text/yaml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'mediamtx.yml';
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="space-y-6">
      {/* سربرگ */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <div className="text-xs font-bold text-cyan-700 uppercase tracking-wider">
            درگاه یکپارچه استریمینگ
          </div>
          <h2 className="text-lg font-bold text-slate-900 font-display">
            پیکربندی عملیاتی MediaMTX با ۳ استریم مستقل
          </h2>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={handleCopy}
            className="px-3 py-1.5 text-xs font-medium rounded-lg bg-slate-100 hover:bg-slate-200 text-slate-800 transition-colors flex items-center gap-1.5 border border-slate-200"
          >
            {copied ? <Check className="w-3.5 h-3.5 text-emerald-600" /> : <Copy className="w-3.5 h-3.5" />}
            <span>{copied ? 'کپی شد' : 'کپی mediamtx.yml'}</span>
          </button>
          <button
            onClick={handleDownload}
            className="px-3.5 py-1.5 text-xs font-bold rounded-lg bg-slate-900 hover:bg-slate-800 text-white transition-colors flex items-center gap-1.5 shadow-xs"
          >
            <Download className="w-3.5 h-3.5" />
            <span>دانلود فایل YAML</span>
          </button>
        </div>
      </div>

      {/* ۳ ستون ویژگی‌های فنی درگاه */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs">
        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-2">
          <div className="flex items-center gap-2 font-bold text-emerald-700">
            <CheckCircle2 className="w-4 h-4" />
            <span>اجبار به انتقال روی TCP</span>
          </div>
          <p className="text-slate-600 leading-relaxed">
            درگاه MediaMTX با تنظیم <code className="font-mono text-emerald-800">protocols: [tcp]</code> تمام بسته‌های RTP را روی کانال‌های اینترلیود TCP دریافت کرده و اتلاف بسته‌های شبکه را به صفر می‌رساند.
          </p>
        </div>

        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-2">
          <div className="flex items-center gap-2 font-bold text-cyan-700">
            <Globe className="w-4 h-4" />
            <span>WebRTC WHEP (تاخیر کمتر از ۲۵۰ms)</span>
          </div>
          <p className="text-slate-600 leading-relaxed">
            پروتکل مدرن WHEP روی پورت <code className="font-mono text-cyan-800">:8889</code> هندشیک مستقیم با مرورگرها برقرار کرده و تاخیر شیشه‌به‌شیشه وب را به کمترین حد ممکن می‌رساند.
          </p>
        </div>

        <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-2">
          <div className="flex items-center gap-2 font-bold text-indigo-700">
            <Radio className="w-4 h-4" />
            <span>فال‌بک HLS با سگمنت‌های fMP4</span>
          </div>
          <p className="text-slate-600 leading-relaxed">
            در صورت مسدود بودن ترافیک UDP/STUN کلاینت‌ها، استریم با تکه‌های ۱ ثانیه‌ای fMP4 به صورت استریم پیوسته HLS تحویل مرورگر می‌شود.
          </p>
        </div>
      </div>

      {/* سوییچ ترنسکود اختیاری */}
      <div className="p-4 rounded-xl bg-white border border-slate-200 shadow-xs flex items-center justify-between gap-4 text-xs">
        <div className="space-y-0.5">
          <span className="font-bold text-slate-800">
            افزودن خط لوله ترنسکود FFmpeg در صورت نیاز (کدک‌های HEVC / S+265)
          </span>
          <p className="text-slate-500">
            تنها در صورتی که تست با ffprobe نشان دهد دوربین روی کدک H.265 یا S+265 است و مرورگر کلاینت توان دیکود سخت‌افزاری آن را ندارد فعال شود.
          </p>
        </div>
        <button
          onClick={() => setEnableTranscodeFallback(!enableTranscodeFallback)}
          className={`px-3 py-1.5 rounded-lg text-xs font-bold transition-colors whitespace-nowrap border ${
            enableTranscodeFallback
              ? 'bg-amber-100 text-amber-900 border-amber-300'
              : 'bg-slate-100 text-slate-600 border-slate-300 hover:text-slate-900'
          }`}
        >
          {enableTranscodeFallback ? 'ترنسکود فعال شد' : 'ترنسکود غیرفعال'}
        </button>
      </div>

      {/* کادر مشاهده کد پیکربندی */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-3">
        <div className="flex items-center justify-between">
          <div className="text-xs font-bold text-slate-700 font-mono flex items-center gap-1.5">
            <FileCode className="w-4 h-4 text-cyan-700" />
            <span>mediamtx.yml</span>
          </div>
          <div className="text-[11px] text-slate-500 flex items-center gap-1 font-mono">
            <ShieldCheck className="w-3.5 h-3.5 text-emerald-600" />
            <span>متغیرهای محیطی جهت حفظ امنیت رمز عبور</span>
          </div>
        </div>

        <pre className="p-4 rounded-lg bg-slate-900 font-mono text-xs text-slate-200 overflow-x-auto border border-slate-800 leading-relaxed text-left" dir="ltr">
          {mediaMtxYaml}
        </pre>
      </div>

      {/* اجرای سریع با داکر */}
      <div className="p-5 rounded-xl bg-white border border-slate-200 shadow-xs space-y-2 text-xs">
        <div className="font-bold text-slate-800 flex items-center gap-1.5">
          <Terminal className="w-4 h-4 text-cyan-700" />
          <span>دستور اجرای یک‌خطی درگاه MediaMTX با داکر (Docker Host Mode)</span>
        </div>
        <div className="p-3 rounded-lg bg-slate-900 font-mono text-emerald-400 select-all border border-slate-800 text-left" dir="ltr">
          docker run --rm -it --network=host -v $(pwd)/mediamtx.yml:/mediamtx.yml bluenviron/mediamtx:latest
        </div>
      </div>
    </div>
  );
};
