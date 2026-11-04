import React, { useState } from 'react';
import { X, Download, Copy, Check, FileText, Terminal, Layers } from 'lucide-react';
import { AuditStep, StreamEndpointConfig } from '../types/poc';
import { sanitizeRtspUrl, sanitizeCommandString } from '../utils/security';

interface ExportModalProps {
  isOpen: boolean;
  onClose: () => void;
  directConfig: StreamEndpointConfig;
  nvrConfig: StreamEndpointConfig;
  directSteps: AuditStep[];
  nvrSteps: AuditStep[];
}

export const ExportModal: React.FC<ExportModalProps> = ({
  isOpen,
  onClose,
  directConfig,
  nvrConfig,
  directSteps,
  nvrSteps,
}) => {
  const [activeTab, setActiveTab] = useState<'markdown' | 'bash' | 'powershell'>('markdown');
  const [copied, setCopied] = useState<boolean>(false);

  if (!isOpen) return null;

  // گزارش ممیزی مستند به زبان فارسی
  const markdownContent = `# گزارش ممیزی فنی و طرح آزمون استریم دوربین‌های تیاندی (Tiandy POC)
**تاریخ ممیزی:** ۲۰۲۶-۱۱-۰۳  
**وضعیت:** ممیزی فنی انجام شد · نیازمند اجرای آزمون‌های فیزیکی در محل  

---

## ۱. یافته‌های تاییدشده و قطعی (Verified Findings)
- **فرمت استاندارد آدرس RTSP تیاندی:**  
  \`rtsp://USERNAME:PASSWORD@IP:PORT/CHANNEL/STREAM\`
- **ساختار استریم‌های سه‌گانه:**
  - استریم ۱ (جریان اصلی با کیفیت بالا): \`/1/1\`
  - استریم ۲ (جریان فرعی مناسب وب): \`/1/2\`
  - استریم ۳ (جریان سبک/موبایل): \`/1/3\`
- **پورت مدیریت وب دوربین:** پورت \`3000\` (HTTP Web GUI است و نباید با RTSP اشتباه شود).
- **پورت RTSP:** پیش‌فرض ۵۵۴ است و باید با ابزار اسکن پورت تایید شود.
- **پروتکل انتقال الزامی:** اجبار به استفاده از \`-rtsp_transport tcp\` و در درگاه \`protocols: [tcp]\` جهت جلوگیری از افت بسته‌ها.

---

## ۲. مشکلات و آزمون‌های ناموفق گذشته (Failed Tests & Hypotheses)
- سابقه اختلال و فریز تصویر در ویندوز (VLC / DirectShow) و مرورگر وب (WebRTC / HTML5).
- **فرضیه S+265:** فشرده‌سازی هوشمند S+265 به عنوان یک فرضیه محتمل تحت بررسی است و نباید بدون بررسی NAL Units علت قطعی فرض شود.
- **فرضیه اتلاف بسته در UDP:** اتلاف بسته‌های ویدیویی روی سوییچ‌ها موجب تخریب فریم‌های کلیدی می‌گردد.
- **فرضیه مذاکره SDP در مرورگر:** عدم پذیرش پروفایل‌های غیر Baseline در WebRTC.

---

## ۳. موارد تست‌نشده نیازمند ارزیابی در محل (Untested Items)
1. مدل دقیق دوربین و دستگاه NVR.
2. کدک واقعی فعال روی هر سه استریم (H.264 یا H.265 یا S+265).
3. اثبات دیکود قطعی ۵۰ فریم متوالی با FFmpeg به خروجی null sink.
4. سنجش دقیق تاخیر شیشه‌به‌شیشه در کلاینت‌های وب و ویندوز.

---

## ۴. اقدامات دستی الزامی توسط کاربر (Required Manual Actions)
1. **اسکن پورت‌ها:**
   \`\`\`bash
   nc -zv ${directConfig.host} ${directConfig.rtspPort}
   curl -I http://${directConfig.host}:${directConfig.webManagementPort}
   \`\`\`
2. **پروب استریم‌های ۱، ۲ و ۳ با FFprobe:**
   \`\`\`bash
   ffprobe -v error -rtsp_transport tcp -select_streams v:0 -show_entries stream=codec_name,width,height,r_frame_rate "rtsp://admin:$CAM_PASS@${directConfig.host}:${directConfig.rtspPort}/1/1"
   ffprobe -v error -rtsp_transport tcp -select_streams v:0 "rtsp://admin:$CAM_PASS@${directConfig.host}:${directConfig.rtspPort}/1/2"
   ffprobe -v error -rtsp_transport tcp -select_streams v:0 "rtsp://admin:$CAM_PASS@${directConfig.host}:${directConfig.rtspPort}/1/3"
   \`\`\`
3. **دیکود ۵۰ فریم با FFmpeg:**
   \`\`\`bash
   ffmpeg -rtsp_transport tcp -i "rtsp://admin:$CAM_PASS@${directConfig.host}:${directConfig.rtspPort}/1/1" -vframes 50 -f null -
   \`\`\`
4. **پخش در ویندوز:**
   \`\`\`bash
   vlc --rtsp-tcp --network-caching=300 "rtsp://admin:$CAM_PASS@${directConfig.host}:${directConfig.rtspPort}/1/1"
   \`\`\`

---

## ۵. شواهد لازم پیش از اتخاذ تصمیم معماری نهایی (Evidence for Final Decision)
تصمیم قطعی بین اتصال مستقیم (Method A) و اتصال از طریق NVR (Method B) منوط به ثبت شواهد زیر خواهد بود:
- تاخیر واقعی شیشه‌به‌شیشه اندازه‌گیری‌شده در WebRTC
- مصرف پردازنده دوربین در مواجهه با بیش از ۳ سشن همزمان
- پایداری ضبط مداوم روی هارددیسک NVR
`;

  // اسکریپت بش برای لینوکس و مک
  const bashScript = `#!/usr/bin/env bash
# اسکریپت آزمون و اعتبارسنجی استریم دوربین تیاندی
# فرمت تاییدشده: rtsp://USER:PASS@HOST:PORT/CHANNEL/STREAM
# پورت وب: 3000

set -euo pipefail

CAM_HOST="${directConfig.host}"
CAM_RTSP_PORT="${directConfig.rtspPort}"
CAM_WEB_PORT="${directConfig.webManagementPort}"
NVR_HOST="${nvrConfig.host}"
NVR_PORT="${nvrConfig.rtspPort}"

echo "=================================================="
echo " گام ۱: تست ارتباط به پورت RTSP ($CAM_RTSP_PORT) و پورت وب ($CAM_WEB_PORT)"
echo "=================================================="
nc -zv "$CAM_HOST" "$CAM_RTSP_PORT"
curl -I -s "http://$CAM_HOST:$CAM_WEB_PORT" | head -n 3 || echo "پورت وب ۳۰۰۰ بررسی شد"

read -s -p "رمز عبور دوربین را وارد کنید (در ترمینال نمایش داده نمی‌شود): " CAM_PASS
echo ""

echo "=================================================="
echo " گام ۲: پروب مستقل استریم‌های ۱، ۲ و ۳ با FFprobe"
echo "=================================================="
echo "--- استریم ۱ (جریان اصلی /1/1) ---"
ffprobe -v error -rtsp_transport tcp \\
  -select_streams v:0 \\
  -show_entries stream=codec_name,profile,width,height,r_frame_rate,bit_rate \\
  -of json "rtsp://admin:\${CAM_PASS}@\${CAM_HOST}:\${CAM_RTSP_PORT}/1/1"

echo "--- استریم ۲ (جریان فرعی /1/2) ---"
ffprobe -v error -rtsp_transport tcp \\
  -select_streams v:0 \\
  -show_entries stream=codec_name,width,height,r_frame_rate \\
  -of json "rtsp://admin:\${CAM_PASS}@\${CAM_HOST}:\${CAM_RTSP_PORT}/1/2"

echo "--- استریم ۳ (جریان سوم /1/3) ---"
ffprobe -v error -rtsp_transport tcp \\
  -select_streams v:0 \\
  -show_entries stream=codec_name,width,height,r_frame_rate \\
  -of json "rtsp://admin:\${CAM_PASS}@\${CAM_HOST}:\${CAM_RTSP_PORT}/1/3"

echo "=================================================="
echo " گام ۳: دیکود ۵۰ فریم واقعی با FFmpeg روی TCP"
echo "=================================================="
ffmpeg -rtsp_transport tcp \\
  -i "rtsp://admin:\${CAM_PASS}@\${CAM_HOST}:\${CAM_RTSP_PORT}/1/1" \\
  -vframes 50 -f null -

echo "=================================================="
echo " گام ۴: آزمون استریم کانال ۱ از طریق NVR (/1/1)"
echo "=================================================="
ffmpeg -rtsp_transport tcp \\
  -i "rtsp://admin:\${CAM_PASS}@\${NVR_HOST}:\${NVR_PORT}/1/1" \\
  -vframes 50 -f null -

echo ">>> آزمون اعتبارسنجی با موفقیت کامل شد. <<<"
`;

  // اسکریپت پاورشل برای ویندوز
  const psScript = `# اسکریپت اعتبارسنجی استریم دوربین تیاندی در ویندوز (PowerShell)
# فرمت مستند: rtsp://USER:PASS@HOST:PORT/CHANNEL/STREAM
# پورت وب: 3000

$CamHost = "${directConfig.host}"
$CamRtspPort = ${directConfig.rtspPort}
$CamWebPort = ${directConfig.webManagementPort}
$NvrHost = "${nvrConfig.host}"
$NvrPort = ${nvrConfig.rtspPort}

Write-Host ">>> ۱. بررسی باز بودن پورت ویدیویی RTSP..." -ForegroundColor Cyan
Test-NetConnection -ComputerName $CamHost -Port $CamRtspPort

Write-Host ">>> ۲. بررسی پورت وب مدیریت دوربین (3000)..." -ForegroundColor Cyan
Test-NetConnection -ComputerName $CamHost -Port $CamWebPort

$Password = Read-Host -Prompt "رمز عبور دوربین را وارد کنید" -AsSecureString
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
$PlainPass = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

Write-Host ">>> ۳. دیکود ۵۰ فریم از استریم ۱ دوربین (/1/1) با FFmpeg روی پروتکل TCP..." -ForegroundColor Green
ffmpeg -rtsp_transport tcp -i "rtsp://admin:$PlainPass@$CamHost:$CamRtspPort/1/1" -vframes 50 -f null -

Write-Host ">>> ۴. دیکود ۵۰ فریم از استریم کانال ۱ دستگاه NVR (/1/1)..." -ForegroundColor Green
ffmpeg -rtsp_transport tcp -i "rtsp://admin:$PlainPass@$NvrHost:$NvrPort/1/1" -vframes 50 -f null -

Write-Host ">>> ۵. اجرای VLC با اجبار به پروتکل TCP و بافر ۳۰۰ میلی‌ثانیه:" -ForegroundColor Yellow
Write-Host "vlc --rtsp-tcp --network-caching=300 rtsp://admin:****@$CamHost:$CamRtspPort/1/1"
`;

  const activeContent =
    activeTab === 'markdown'
      ? markdownContent
      : activeTab === 'bash'
      ? bashScript
      : psScript;

  const handleCopy = () => {
    navigator.clipboard.writeText(activeContent);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleDownload = () => {
    const filename =
      activeTab === 'markdown'
        ? 'TIANDY_AUDIT_REPORT_FA.md'
        : activeTab === 'bash'
        ? 'verify_tiandy_fa.sh'
        : 'verify_tiandy_fa.ps1';

    const blob = new Blob([activeContent], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs animate-in fade-in duration-150">
      <div className="w-full max-w-4xl bg-white border border-slate-200 rounded-xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]">
        {/* سربرگ مودال */}
        <div className="p-4 bg-slate-50 border-b border-slate-200 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <FileText className="w-5 h-5 text-cyan-700" />
            <h3 className="text-sm font-bold text-slate-900">
              خروجی گزارش ممیزی و اسکریپت‌های تست در محیط کاربر
            </h3>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded-md text-slate-400 hover:text-slate-800 hover:bg-slate-200 transition-colors"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* سوییچر تب‌ها */}
        <div className="flex items-center gap-2 px-4 pt-3 border-b border-slate-200 bg-white">
          <button
            onClick={() => setActiveTab('markdown')}
            className={`pb-2.5 text-xs font-bold border-b-2 transition-colors ${
              activeTab === 'markdown'
                ? 'border-cyan-600 text-cyan-800'
                : 'border-transparent text-slate-500 hover:text-slate-800'
            }`}
          >
            گزارش رسمی ممیزی (Markdown)
          </button>
          <button
            onClick={() => setActiveTab('bash')}
            className={`pb-2.5 text-xs font-bold border-b-2 transition-colors ${
              activeTab === 'bash'
                ? 'border-cyan-600 text-cyan-800'
                : 'border-transparent text-slate-500 hover:text-slate-800'
            }`}
          >
            اسکریپت لینوکس و مک (verify_tiandy_fa.sh)
          </button>
          <button
            onClick={() => setActiveTab('powershell')}
            className={`pb-2.5 text-xs font-bold border-b-2 transition-colors ${
              activeTab === 'powershell'
                ? 'border-cyan-600 text-cyan-800'
                : 'border-transparent text-slate-500 hover:text-slate-800'
            }`}
          >
            اسکریپت ویندوز (verify_tiandy_fa.ps1)
          </button>
        </div>

        {/* محتوای متنی کد */}
        <div className="p-4 overflow-y-auto flex-1 font-mono text-xs text-slate-800 bg-slate-900 text-slate-200">
          <pre className="whitespace-pre-wrap leading-relaxed text-left" dir="ltr">
            {activeContent}
          </pre>
        </div>

        {/* پاورقی مودال */}
        <div className="p-4 bg-slate-50 border-t border-slate-200 flex items-center justify-between gap-3 text-xs">
          <span className="text-slate-600">
            تمام اسکریپت‌ها بر مبنای فرمت مستند تیاندی (<code className="font-mono">/1/1</code>، <code className="font-mono">/1/2</code>، <code className="font-mono">/1/3</code>) و پورت وب ۳۰۰۰ تدوین شده‌اند.
          </span>
          <div className="flex items-center gap-2 shrink-0">
            <button
              onClick={handleCopy}
              className="px-3 py-1.5 rounded-lg bg-white border border-slate-300 hover:bg-slate-100 text-slate-800 font-bold transition-colors flex items-center gap-1.5"
            >
              {copied ? <Check className="w-3.5 h-3.5 text-emerald-600" /> : <Copy className="w-3.5 h-3.5" />}
              <span>{copied ? 'کپی شد' : 'کپی متن'}</span>
            </button>
            <button
              onClick={handleDownload}
              className="px-3.5 py-1.5 rounded-lg bg-slate-900 hover:bg-slate-800 text-white font-bold transition-colors flex items-center gap-1.5 shadow-xs"
            >
              <Download className="w-3.5 h-3.5" />
              <span>دانلود فایل</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};
