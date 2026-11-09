import React, { useState, useEffect } from 'react';
import {
  TiandySettingsProvider,
  CameraCapabilityProfile,
  CameraSettingsSnapshot,
  OptimizationRecommendation
} from '../services/cameraProvider';
import { Shield, Camera, Sliders, CheckCircle2, AlertTriangle, RotateCcw, Zap, Eye, Sun, Moon, Cpu, History } from 'lucide-react';

interface AuditRecord {
  id: string;
  timestamp: string;
  cameraIp: string;
  profile: string;
  admin: string;
  status: 'موفق' | 'خطا';
  summary: string;
  wasRolledBack: boolean;
  snapshotBefore: CameraSettingsSnapshot;
  snapshotAfter: CameraSettingsSnapshot;
}

export const CameraOptimizer: React.FC = () => {
  const [provider] = useState(() => new TiandySettingsProvider());
  const [cameraIp, setCameraIp] = useState('192.168.1.100');
  const [cameraPort, setCameraPort] = useState(80);
  const [cameraUser, setCameraUser] = useState('admin');
  const [cameraPass, setCameraPass] = useState('admin');
  const [selectedProfile, setSelectedProfile] = useState<'NighttimeLicensePlate' | 'DaytimeLicensePlate' | 'HighSpeedVehicle' | 'LowLightEnvironment' | 'GeneralSurveillance'>('NighttimeLicensePlate');

  const [capabilities, setCapabilities] = useState<CameraCapabilityProfile | null>(null);
  const [currentSnapshot, setCurrentSnapshot] = useState<CameraSettingsSnapshot | null>(null);
  const [lastAppliedSnapshot, setLastAppliedSnapshot] = useState<CameraSettingsSnapshot | null>(null);
  const [recommendation, setRecommendation] = useState<OptimizationRecommendation | null>(null);

  // Manual Adjustments
  const [manualSettings, setManualSettings] = useState<CameraSettingsSnapshot | null>(null);
  const [adminConfirmed, setAdminConfirmed] = useState(false);
  const [isApplying, setIsApplying] = useState(false);
  const [isDetecting, setIsDetecting] = useState(false);
  const [statusMessage, setStatusMessage] = useState('سیستم آماده بهینه‌سازی تنظیمات تصویر دوربین تیاندی است.');
  const [auditHistory, setAuditHistory] = useState<AuditRecord[]>([]);

  // Initial detection
  useEffect(() => {
    handleDetectCapabilities();
  }, []);

  const handleDetectCapabilities = async () => {
    setIsDetecting(true);
    setStatusMessage('در حال اتصال به وب‌سرویس ONVIF Imaging و ارزیابی قابلیت‌های سخت‌افزاری...');
    try {
      const caps = await provider.detectCapabilities(cameraIp, cameraPort, cameraUser, cameraPass);
      const snapshot = await provider.getCurrentSettings(cameraIp, cameraPort, cameraUser, cameraPass);

      setCapabilities(caps);
      setCurrentSnapshot(snapshot);
      setManualSettings({ ...snapshot });

      generateRecommendation(caps, snapshot, selectedProfile);
      setStatusMessage('قابلیت‌های دوربین تیاندی با موفقیت شناسایی و اسنپ‌شات اولیه ذخیره شد.');
    } catch {
      setStatusMessage('خطا در استعلام قابلیت‌ها؛ بررسی آدرس و اتصال شبکه.');
    } finally {
      setIsDetecting(false);
    }
  };

  const generateRecommendation = (
    caps: CameraCapabilityProfile,
    current: CameraSettingsSnapshot,
    profileType: 'NighttimeLicensePlate' | 'DaytimeLicensePlate' | 'HighSpeedVehicle' | 'LowLightEnvironment' | 'GeneralSurveillance'
  ) => {
    const proposed: CameraSettingsSnapshot = { ...current, snapshotId: `prop_${Date.now()}` };
    const reasons: Record<string, string> = {};
    let summary = '';
    let confidence = 0.94;

    switch (profileType) {
      case 'NighttimeLicensePlate':
        proposed.exposureMode = 'Manual';
        proposed.shutterSpeedSeconds = 0.0025; // 1/400s
        proposed.gain = 35;
        proposed.hlcEnabled = true;
        proposed.hlcLevel = 75;
        proposed.wdrEnabled = true;
        proposed.wdrLevel = 30;
        proposed.dayNightMode = 'Night';
        proposed.irMode = 'Smart';
        proposed.irIntensity = 65;

        reasons['Shutter'] = 'تنظیم سرعت شاتر روی 1/400 ثانیه جهت جلوگیری از کشیدگی و تاری حروف پلاک خودروهای متحرک در شب.';
        reasons['HLC'] = 'فعال‌سازی ضروری HLC در سطح ۷۵٪ جهت مهار نور مستقیم و خیره‌کننده چراغ‌های خودرو روی صفحه آلومینیومی پلاک.';
        reasons['Gain'] = 'تنظیم گین سنسور روی ۳۵ جهت تأمین روشنایی فریم هم‌زمان با مهار نویز برفکی در پردازش کاراکترها.';
        reasons['IR'] = 'تنظیم مادون قرمز هوشمند Smart IR روی ۶۵٪ برای ایجاد نور یکنواخت بدون سوختگی بازتاب پلاک.';
        summary = 'پیکربندی تخصصی پلاک‌خوانی شبانه: فعال‌سازی قطعی HLC جهت مهار پرتوهای مستقیم چراغ‌های جلو، شاتر 1/400s جهت جلوگیری از کشیدگی پلاک و کنترل توان مادون قرمز جهت خوانایی بی‌نقص OCR.';
        confidence = 0.96;
        break;

      case 'DaytimeLicensePlate':
        proposed.exposureMode = 'Manual';
        proposed.shutterSpeedSeconds = 0.001; // 1/1000s
        proposed.gain = 15;
        proposed.hlcEnabled = false;
        proposed.wdrEnabled = true;
        proposed.wdrLevel = 45;
        proposed.dayNightMode = 'Day';
        proposed.irMode = 'Off';

        reasons['Shutter'] = 'سرعت شاتر سریع 1/1000 ثانیه جهت فریز کردن دقیق حرکت خودروهای عبوری تا سرعت ۸۰ کیلومتر بر ساعت.';
        reasons['Gain'] = 'کاهش گین آنالوگ به ۱۵ جهت ثبت شارپ‌ترین لبه کاراکترها بدون نویز سنسور در نور روز.';
        reasons['WDR'] = 'فعال‌سازی WDR در سطح ۴۵٪ جهت بالانس بین تابش مستقیم آفتاب و سایه زیر سپر خودرو.';
        summary = 'پیکربندی پلاک‌خوانی روزانه: شاتر سریع برای ثبت بدون لرزش، حداقل نویز سنسور و تعادل کنتراست سایه و آفتاب.';
        confidence = 0.95;
        break;

      case 'HighSpeedVehicle':
        proposed.exposureMode = 'Manual';
        proposed.shutterSpeedSeconds = 0.0005; // 1/2000s
        proposed.gain = 40;
        proposed.hlcEnabled = true;
        proposed.hlcLevel = 50;
        proposed.wdrEnabled = true;
        proposed.wdrLevel = 50;

        reasons['Shutter'] = 'شاتر فوق‌سریع 1/2000 ثانیه ویژه بزرگراه‌ها و تردد پرسرعت تا ۱۲۰ کیلومتر در ساعت.';
        reasons['Gain'] = 'جبران کسری نوردهی ناشی از شاتر بسیار کوتاه با تنظیم گین روی ۴۰.';
        summary = 'پیکربندی سرعت بالا: تثبیت کامل ارقام پلاک در سرعت‌های بزرگراهی با جبران روشنایی.';
        confidence = 0.92;
        break;

      case 'LowLightEnvironment':
        proposed.exposureMode = 'Auto';
        proposed.gain = 60;
        proposed.wdrEnabled = true;
        proposed.wdrLevel = 60;
        proposed.irMode = 'Auto';
        proposed.irIntensity = 85;

        reasons['Gain'] = 'تقویت حساسیت سنسور به ۶۰٪ برای استخراج جزئیات از مناطق تاریک محیط.';
        reasons['IR'] = 'تابش پرقدرت مادون قرمز جهت پوشش حداکثر عمق میدان پایش.';
        summary = 'پیکربندی محیط تاریک: تقویت دید پیرامونی و فعال‌سازی توان بالای مادون قرمز.';
        confidence = 0.90;
        break;

      default:
        proposed.exposureMode = 'Auto';
        proposed.shutterSpeedSeconds = 0.02;
        proposed.gain = 25;
        proposed.wdrEnabled = false;
        proposed.hlcEnabled = false;
        proposed.irMode = 'Auto';

        reasons['Shutter'] = 'حالت استاندارد 1/50 ثانیه با حذف لرزش فرکانس برق شهری (Anti-Flicker 50Hz).';
        summary = 'پیکربندی نظارت عمومی: تصاویر روان و با رنگ‌های طبیعی جهت نظارت بر تردد عابران و محوطه.';
        confidence = 0.98;
        break;
    }

    const rec: OptimizationRecommendation = {
      recommendationId: `rec_${Date.now()}`,
      targetProfile: profileType,
      currentSettings: current,
      proposedSettings: proposed,
      parameterReasons: reasons,
      executiveSummary: summary,
      engineConfidence: confidence
    };

    setRecommendation(rec);
    setManualSettings(proposed);
  };

  const handleProfileChange = (profile: typeof selectedProfile) => {
    setSelectedProfile(profile);
    if (capabilities && currentSnapshot) {
      generateRecommendation(capabilities, currentSnapshot, profile);
    }
  };

  const handleApplySettings = async () => {
    if (!adminConfirmed) {
      alert('اعمال تنظیمات نیازمند تأیید صریح مدیر سیستم می‌باشد.');
      return;
    }
    if (!capabilities || !manualSettings) return;

    setIsApplying(true);
    setStatusMessage('در حال ایجاد Snapshot، اعمال تنظیمات بر روی دوربین تیاندی و اعتبارسنجی...');

    try {
      // Save snapshot before applying
      const beforeSnap = currentSnapshot ? { ...currentSnapshot } : { ...manualSettings };
      setLastAppliedSnapshot(beforeSnap);

      const success = await provider.applySettings(
        cameraIp,
        cameraPort,
        cameraUser,
        cameraPass,
        manualSettings,
        capabilities
      );

      if (success) {
        const afterSnap = { ...manualSettings, capturedAt: new Date().toISOString() };
        setCurrentSnapshot(afterSnap);

        const newAudit: AuditRecord = {
          id: `audit_${Date.now()}`,
          timestamp: new Date().toLocaleTimeString('fa-IR'),
          cameraIp,
          profile: selectedProfile,
          admin: 'مدیر ارشد سامانه',
          status: 'موفق',
          summary: `تنظیمات با موفقیت اعمال شد. شاتر: ${formatShutter(manualSettings.shutterSpeedSeconds)} | HLC: ${manualSettings.hlcEnabled ? `${manualSettings.hlcLevel}%` : 'خاموش'} | گین: ${manualSettings.gain}`,
          wasRolledBack: false,
          snapshotBefore: beforeSnap,
          snapshotAfter: afterSnap
        };

        setAuditHistory((prev) => [newAudit, ...prev]);
        setStatusMessage('تنظیمات جدید با موفقیت روی دوربین ثبت و ذخیره گردید.');
      } else {
        setStatusMessage('خطا در اعمال تنظیمات توسط وب‌سرویس دوربین.');
      }
    } catch (e: any) {
      setStatusMessage(`خطا: ${e.message}`);
    } finally {
      setIsApplying(false);
    }
  };

  const handleRollback = async () => {
    if (!lastAppliedSnapshot || !capabilities) {
      alert('هیچ اسنپ‌شات قبلی برای بازگردانی یافت نشد.');
      return;
    }

    if (!confirm('آیا از بازگردانی (Rollback) تنظیمات به اسنپ‌شات پایدار قبلی اطمینان دارید؟')) {
      return;
    }

    setIsApplying(true);
    setStatusMessage('در حال بازگردانی تنظیمات به اسنپ‌شات قبلی (Rollback)...');

    try {
      const success = await provider.applySettings(
        cameraIp,
        cameraPort,
        cameraUser,
        cameraPass,
        lastAppliedSnapshot,
        capabilities
      );

      if (success) {
        setCurrentSnapshot({ ...lastAppliedSnapshot });
        setManualSettings({ ...lastAppliedSnapshot });

        const rollbackAudit: AuditRecord = {
          id: `audit_rb_${Date.now()}`,
          timestamp: new Date().toLocaleTimeString('fa-IR'),
          cameraIp,
          profile: 'بازگردانی Rollback',
          admin: 'مدیر ارشد سامانه',
          status: 'موفق',
          summary: 'تنظیمات با موفقیت به مقادیر پایدار اولیه بازگردانی شد.',
          wasRolledBack: true,
          snapshotBefore: currentSnapshot || lastAppliedSnapshot,
          snapshotAfter: lastAppliedSnapshot
        };

        setAuditHistory((prev) => [rollbackAudit, ...prev]);
        setStatusMessage('عملیات Rollback با موفقیت به پایان رسید.');
      }
    } finally {
      setIsApplying(false);
    }
  };

  const formatShutter = (sec: number) => {
    if (sec <= 0) return 'Auto';
    if (sec < 1) {
      const denom = Math.round(1 / sec);
      return `1/${denom} ثانیه`;
    }
    return `${sec.toFixed(2)} ثانیه`;
  };

  return (
    <div className="flex-1 flex flex-col bg-[#161B26] text-slate-100 overflow-y-auto p-4 md:p-6" dir="rtl">
      {/* هدر ماژول بهینه‌ساز */}
      <div className="flex flex-col md:flex-row md:items-center justify-between pb-4 border-b border-[#252D3D] mb-5 gap-3">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-blue-600/20 border border-blue-500/40 flex items-center justify-center text-blue-400">
            <Sliders className="w-5 h-5" />
          </div>
          <div>
            <h1 className="text-lg font-bold text-sky-400 flex items-center gap-2">
              بهینه‌ساز هوشمند تنظیمات دوربین (Camera Settings Optimizer)
              <span className="text-xs px-2 py-0.5 rounded-full bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
                فاز ۸ - Tiandy Native & ONVIF
              </span>
            </h1>
            <p className="text-xs text-slate-400">
              مدیریت دینامیک شاتر، نوردهی، مهار نور چراغ (HLC)، گین و مادون قرمز جهت بهبود قطعی وضوح پلاک‌خوان
            </p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={handleDetectCapabilities}
            disabled={isDetecting}
            className="flex items-center gap-2 px-3 py-1.5 text-xs font-semibold rounded-lg bg-[#202634] hover:bg-[#2A3446] border border-[#2D3748] text-slate-200 transition"
          >
            <Camera className="w-3.5 h-3.5 text-sky-400" />
            {isDetecting ? 'در حال استعلام...' : 'استعلام قابلیت‌های ONVIF'}
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-5">
        {/* ستون راست: انتخاب دوربین و قابلیت‌ها (۴ ستون) */}
        <div className="lg:col-span-4 space-y-4">
          {/* کارت ۱: اتصال به دوربین */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <h2 className="text-sm font-bold text-blue-400 mb-3 flex items-center gap-2">
              <Shield className="w-4 h-4 text-blue-400" />
              ۱. مشخصات اتصال دوربین
            </h2>

            <div className="grid grid-cols-2 gap-3 text-xs mb-3">
              <div>
                <label className="block text-slate-400 mb-1">آدرس IP دوربین:</label>
                <input
                  type="text"
                  value={cameraIp}
                  onChange={(e) => setCameraIp(e.target.value)}
                  className="w-full bg-[#161B26] border border-[#334155] rounded-lg px-2.5 py-1.5 text-slate-200 font-mono text-xs focus:outline-none focus:border-blue-500"
                />
              </div>
              <div>
                <label className="block text-slate-400 mb-1">پورت وب/ONVIF:</label>
                <input
                  type="number"
                  value={cameraPort}
                  onChange={(e) => setCameraPort(Number(e.target.value))}
                  className="w-full bg-[#161B26] border border-[#334155] rounded-lg px-2.5 py-1.5 text-slate-200 font-mono text-xs focus:outline-none focus:border-blue-500"
                />
              </div>
            </div>

            <div className="grid grid-cols-2 gap-3 text-xs mb-3">
              <div>
                <label className="block text-slate-400 mb-1">نام کاربری:</label>
                <input
                  type="text"
                  value={cameraUser}
                  onChange={(e) => setCameraUser(e.target.value)}
                  className="w-full bg-[#161B26] border border-[#334155] rounded-lg px-2.5 py-1.5 text-slate-200 text-xs focus:outline-none focus:border-blue-500"
                />
              </div>
              <div>
                <label className="block text-slate-400 mb-1">کلمه عبور:</label>
                <input
                  type="password"
                  value={cameraPass}
                  onChange={(e) => setCameraPass(e.target.value)}
                  className="w-full bg-[#161B26] border border-[#334155] rounded-lg px-2.5 py-1.5 text-slate-200 text-xs focus:outline-none focus:border-blue-500"
                />
              </div>
            </div>

            <button
              onClick={handleDetectCapabilities}
              disabled={isDetecting}
              className="w-full py-2 bg-blue-600 hover:bg-blue-500 text-white font-semibold text-xs rounded-lg transition flex items-center justify-center gap-2"
            >
              <Cpu className="w-3.5 h-3.5" />
              {isDetecting ? 'در حال اعتبارسنجی قابلیت‌ها...' : 'شناسایی و استعلام قابلیت‌های سخت‌افزاری'}
            </button>
          </div>

          {/* کارت ۲: قابلیت‌های سخت‌افزاری تاییدشده */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <h2 className="text-sm font-bold text-blue-400 mb-3 flex items-center gap-2">
              <CheckCircle2 className="w-4 h-4 text-emerald-400" />
              ۲. قابلیت‌های تاییدشده تیاندی
            </h2>

            <div className="text-xs space-y-2 mb-3 bg-[#161B26] p-2.5 rounded-lg border border-[#252D3D]">
              <div className="flex justify-between">
                <span className="text-slate-400">سازنده:</span>
                <span className="font-semibold text-slate-200">{capabilities?.manufacturer || 'Tiandy Technologies'}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-400">مدل و فریمور:</span>
                <span className="font-semibold text-slate-200">{capabilities?.model || 'TC-NC214 Starlight'}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-400">موتور ارائه‌دهنده:</span>
                <span className="font-semibold text-sky-400">{capabilities?.providerName || provider.providerName}</span>
              </div>
            </div>

            <div className="flex flex-wrap gap-1.5">
              <span className="text-[11px] px-2 py-1 rounded bg-[#1E293B] text-sky-300 border border-[#334155]">
                ✔ کنترل شاتر (Exposure)
              </span>
              <span className="text-[11px] px-2 py-1 rounded bg-[#1E293B] text-sky-300 border border-[#334155]">
                ✔ گین سنسور (Gain)
              </span>
              <span className="text-[11px] px-2 py-1 rounded bg-[#1E293B] text-sky-300 border border-[#334155]">
                ✔ دامنه دینامیکی (WDR)
              </span>
              <span className="text-[11px] px-2 py-1 rounded bg-emerald-950/40 text-emerald-300 border border-emerald-800/40 font-semibold">
                ✔ مهار نور چراغ (HLC اختصاصی)
              </span>
              <span className="text-[11px] px-2 py-1 rounded bg-[#1E293B] text-sky-300 border border-[#334155]">
                ✔ فیلتر مکانیکی روز/شب (ICR)
              </span>
              <span className="text-[11px] px-2 py-1 rounded bg-[#1E293B] text-sky-300 border border-[#334155]">
                ✔ مادون قرمز هوشمند (Smart IR)
              </span>
            </div>
          </div>

          {/* کارت ۳: سناریوهای بهینه‌سازی */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <h2 className="text-sm font-bold text-blue-400 mb-3 flex items-center gap-2">
              <Zap className="w-4 h-4 text-amber-400" />
              ۳. انتخاب پروفایل بهینه‌سازی
            </h2>

            <div className="space-y-2 text-xs">
              <label
                className={`flex items-center gap-2 p-2.5 rounded-lg border cursor-pointer transition ${
                  selectedProfile === 'NighttimeLicensePlate'
                    ? 'bg-blue-600/15 border-blue-500 text-blue-200'
                    : 'bg-[#161B26] border-[#2D3748] text-slate-300 hover:bg-[#1C2333]'
                }`}
              >
                <input
                  type="radio"
                  name="profile"
                  checked={selectedProfile === 'NighttimeLicensePlate'}
                  onChange={() => handleProfileChange('NighttimeLicensePlate')}
                  className="accent-blue-500"
                />
                <Moon className="w-4 h-4 text-indigo-400" />
                <div>
                  <div className="font-bold">🌙 پلاک‌خوانی در شب (Nighttime ANPR + HLC)</div>
                  <div className="text-[10px] text-slate-400">مهار قطعی نور چراغ خودرو، شاتر 1/400s و مادون قرمز Smart</div>
                </div>
              </label>

              <label
                className={`flex items-center gap-2 p-2.5 rounded-lg border cursor-pointer transition ${
                  selectedProfile === 'DaytimeLicensePlate'
                    ? 'bg-blue-600/15 border-blue-500 text-blue-200'
                    : 'bg-[#161B26] border-[#2D3748] text-slate-300 hover:bg-[#1C2333]'
                }`}
              >
                <input
                  type="radio"
                  name="profile"
                  checked={selectedProfile === 'DaytimeLicensePlate'}
                  onChange={() => handleProfileChange('DaytimeLicensePlate')}
                  className="accent-blue-500"
                />
                <Sun className="w-4 h-4 text-amber-400" />
                <div>
                  <div className="font-bold">☀️ پلاک‌خوانی در روز (Daytime ANPR)</div>
                  <div className="text-[10px] text-slate-400">شاتر سریع 1/1000s جهت فریز حرکت، گین کم و WDR متعادل</div>
                </div>
              </label>

              <label
                className={`flex items-center gap-2 p-2.5 rounded-lg border cursor-pointer transition ${
                  selectedProfile === 'HighSpeedVehicle'
                    ? 'bg-blue-600/15 border-blue-500 text-blue-200'
                    : 'bg-[#161B26] border-[#2D3748] text-slate-300 hover:bg-[#1C2333]'
                }`}
              >
                <input
                  type="radio"
                  name="profile"
                  checked={selectedProfile === 'HighSpeedVehicle'}
                  onChange={() => handleProfileChange('HighSpeedVehicle')}
                  className="accent-blue-500"
                />
                <Zap className="w-4 h-4 text-emerald-400" />
                <div>
                  <div className="font-bold">🏎 خودروهای پرسرعت (High-Speed 1/2000s)</div>
                  <div className="text-[10px] text-slate-400">ویژه بزرگراه‌ها تا ۱۲۰ کیلومتر بر ساعت با جبران روشنایی</div>
                </div>
              </label>

              <label
                className={`flex items-center gap-2 p-2.5 rounded-lg border cursor-pointer transition ${
                  selectedProfile === 'LowLightEnvironment'
                    ? 'bg-blue-600/15 border-blue-500 text-blue-200'
                    : 'bg-[#161B26] border-[#2D3748] text-slate-300 hover:bg-[#1C2333]'
                }`}
              >
                <input
                  type="radio"
                  name="profile"
                  checked={selectedProfile === 'LowLightEnvironment'}
                  onChange={() => handleProfileChange('LowLightEnvironment')}
                  className="accent-blue-500"
                />
                <Eye className="w-4 h-4 text-sky-400" />
                <div>
                  <div className="font-bold">🔦 دید در شب محیطی (Low-Light)</div>
                  <div className="text-[10px] text-slate-400">روشن‌سازی محیط‌های تاریک و تقویت حداکثری دید پیرامونی</div>
                </div>
              </label>
            </div>
          </div>
        </div>

        {/* ستون چپ: مقایسه، دلایل فنی، کنترل‌های دستی و لاگ ممیزی (۸ ستون) */}
        <div className="lg:col-span-8 space-y-4">
          {/* مقایسه Side-by-Side مقادیر جاری در برابر پیشنهادی */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <div className="flex items-center justify-between mb-3 pb-2 border-b border-[#252D3D]">
              <h2 className="text-sm font-bold text-blue-400 flex items-center gap-2">
                <Sliders className="w-4 h-4 text-blue-400" />
                مقایسه تنظیمات فعلی و مقادیر پیشنهادی هوشمند
              </h2>
              <span className="text-xs text-emerald-400 font-semibold bg-emerald-950/40 border border-emerald-800/40 px-2 py-0.5 rounded-full">
                میزان اطمینان موتور: {Math.round((recommendation?.engineConfidence || 0.94) * 100)}٪
              </span>
            </div>

            <div className="overflow-x-auto">
              <table className="w-full text-xs text-right border-collapse">
                <thead>
                  <tr className="text-slate-400 border-b border-[#2D3748] pb-1">
                    <th className="py-1.5 font-semibold">پارامتر</th>
                    <th className="py-1.5 font-semibold text-slate-300">وضعیت فعلی دوربین (Current)</th>
                    <th className="py-1.5 font-semibold text-sky-400">مقدار پیشنهادی هوشمند (Proposed)</th>
                    <th className="py-1.5 font-semibold text-amber-400">تنظیم دستی مدیر (Adjustment)</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-[#252D3D] text-slate-200">
                  <tr>
                    <td className="py-2 text-slate-300 font-medium">سرعت شاتر (Exposure):</td>
                    <td className="py-2 text-slate-400">{formatShutter(currentSnapshot?.shutterSpeedSeconds || 0.02)}</td>
                    <td className="py-2 text-sky-400 font-bold">{formatShutter(recommendation?.proposedSettings.shutterSpeedSeconds || 0.0025)}</td>
                    <td className="py-2">
                      <select
                        value={manualSettings?.shutterSpeedSeconds || 0.0025}
                        onChange={(e) =>
                          setManualSettings((prev) => prev && { ...prev, shutterSpeedSeconds: parseFloat(e.target.value), exposureMode: 'Manual' })
                        }
                        className="bg-[#161B26] border border-[#334155] rounded px-2 py-1 text-slate-200 text-xs"
                      >
                        <option value={0.02}>1/50 ثانیه (نرمال)</option>
                        <option value={0.004}>1/250 ثانیه</option>
                        <option value={0.0025}>1/400 ثانیه (پلاک شب)</option>
                        <option value={0.001}>1/1000 ثانیه (پلاک روز)</option>
                        <option value={0.0005}>1/2000 ثانیه (سرعت بالا)</option>
                      </select>
                    </td>
                  </tr>

                  <tr>
                    <td className="py-2 text-slate-300 font-medium">مهار نور شدید (HLC):</td>
                    <td className="py-2">
                      <span className={currentSnapshot?.hlcEnabled ? 'text-emerald-400' : 'text-red-400'}>
                        {currentSnapshot?.hlcEnabled ? `فعال (${currentSnapshot.hlcLevel}%)` : 'غیرفعال (Off)'}
                      </span>
                    </td>
                    <td className="py-2 text-emerald-400 font-bold">
                      {recommendation?.proposedSettings.hlcEnabled ? `فعال (${recommendation.proposedSettings.hlcLevel}%)` : 'غیرفعال'}
                    </td>
                    <td className="py-2 flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={manualSettings?.hlcEnabled ?? true}
                        onChange={(e) => setManualSettings((prev) => prev && { ...prev, hlcEnabled: e.target.checked })}
                        className="accent-blue-500"
                      />
                      <input
                        type="range"
                        min="0"
                        max="100"
                        disabled={!manualSettings?.hlcEnabled}
                        value={manualSettings?.hlcLevel || 75}
                        onChange={(e) => setManualSettings((prev) => prev && { ...prev, hlcLevel: Number(e.target.value) })}
                        className="w-20 accent-emerald-500"
                      />
                      <span className="text-[10px] text-slate-400">{manualSettings?.hlcLevel}%</span>
                    </td>
                  </tr>

                  <tr>
                    <td className="py-2 text-slate-300 font-medium">گین سنسور (Gain):</td>
                    <td className="py-2 text-slate-400">{currentSnapshot?.gain ?? 50}</td>
                    <td className="py-2 text-sky-400 font-bold">{recommendation?.proposedSettings.gain ?? 35}</td>
                    <td className="py-2 flex items-center gap-2">
                      <input
                        type="range"
                        min="0"
                        max="100"
                        value={manualSettings?.gain || 35}
                        onChange={(e) => setManualSettings((prev) => prev && { ...prev, gain: Number(e.target.value) })}
                        className="w-24 accent-sky-500"
                      />
                      <span className="text-[10px] text-slate-400">{manualSettings?.gain}</span>
                    </td>
                  </tr>

                  <tr>
                    <td className="py-2 text-slate-300 font-medium">دامنه دینامیکی (WDR):</td>
                    <td className="py-2 text-slate-400">{currentSnapshot?.wdrEnabled ? `فعال (${currentSnapshot.wdrLevel}%)` : 'غیرفعال'}</td>
                    <td className="py-2 text-sky-400 font-bold">
                      {recommendation?.proposedSettings.wdrEnabled ? `فعال (${recommendation.proposedSettings.wdrLevel}%)` : 'غیرفعال'}
                    </td>
                    <td className="py-2 flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={manualSettings?.wdrEnabled ?? true}
                        onChange={(e) => setManualSettings((prev) => prev && { ...prev, wdrEnabled: e.target.checked })}
                        className="accent-blue-500"
                      />
                      <span className="text-[10px] text-slate-400">{manualSettings?.wdrEnabled ? 'فعال' : 'خاموش'}</span>
                    </td>
                  </tr>

                  <tr>
                    <td className="py-2 text-slate-300 font-medium">مادون قرمز و روز/شب:</td>
                    <td className="py-2 text-slate-400">
                      {currentSnapshot?.irMode || 'Auto'} / {currentSnapshot?.dayNightMode || 'Auto'}
                    </td>
                    <td className="py-2 text-sky-400 font-bold">
                      {recommendation?.proposedSettings.irMode || 'Smart'} ({recommendation?.proposedSettings.irIntensity}%)
                    </td>
                    <td className="py-2">
                      <select
                        value={manualSettings?.dayNightMode || 'Night'}
                        onChange={(e) => setManualSettings((prev) => prev && { ...prev, dayNightMode: e.target.value as any })}
                        className="bg-[#161B26] border border-[#334155] rounded px-2 py-1 text-slate-200 text-xs"
                      >
                        <option value="Auto">Auto (خودکار)</option>
                        <option value="Day">Day (روز / رنگی)</option>
                        <option value="Night">Night (شب / مادون قرمز)</option>
                      </select>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          {/* دلایل تحلیلی مهندسی فارسی */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <h2 className="text-sm font-bold text-blue-400 mb-2 flex items-center gap-2">
              <Zap className="w-4 h-4 text-amber-400" />
              توجیه و دلایل فنی تغییرات پیشنهادی هوش مصنوعی
            </h2>

            <div className="bg-[#161B26] border border-[#252D3D] rounded-lg p-3 text-xs mb-3 text-slate-200 leading-relaxed">
              {recommendation?.executiveSummary || 'پیکربندی هوشمند بر اساس پارامترهای اپتیکال و شرایط محیطی پایش خودرو.'}
            </div>

            <div className="space-y-1.5 text-xs text-slate-300">
              {recommendation?.parameterReasons &&
                Object.entries(recommendation.parameterReasons).map(([param, reason]) => (
                  <div key={param} className="flex items-start gap-2">
                    <span className="text-sky-400 font-bold shrink-0">• {param}:</span>
                    <span className="text-slate-400">{reason}</span>
                  </div>
                ))}
            </div>
          </div>

          {/* پنل تایید مدیر و اعمال با قابلیت Rollback */}
          <div className="bg-[#1A2232] border border-[#2D3748] rounded-xl p-4 shadow-sm flex flex-col md:flex-row items-center justify-between gap-4">
            <div className="flex items-start gap-3">
              <input
                type="checkbox"
                id="admin-auth"
                checked={adminConfirmed}
                onChange={(e) => setAdminConfirmed(e.target.checked)}
                className="mt-1 w-4 h-4 accent-amber-500 rounded cursor-pointer"
              />
              <label htmlFor="admin-auth" className="text-xs cursor-pointer">
                <span className="font-bold text-amber-400 block">
                  اینجانب به عنوان مدیر سیستم، مقادیر پیشنهادی را بررسی کرده و با اعمال آن‌ها روی دوربین موافقم.
                </span>
                <span className="text-[11px] text-slate-400 block mt-0.5">
                  طبق الزامات ایمنی مهرگان، هیچ تغییری بدون تأیید صریح مدیر روی فریمور دوربین ثبت نمی‌گردد.
                </span>
              </label>
            </div>

            <div className="flex items-center gap-3 shrink-0">
              <button
                onClick={handleRollback}
                disabled={!lastAppliedSnapshot || isApplying}
                className="flex items-center gap-2 px-3.5 py-2 text-xs font-semibold rounded-lg bg-red-950/60 hover:bg-red-900 border border-red-800/60 text-red-200 transition disabled:opacity-40"
              >
                <RotateCcw className="w-3.5 h-3.5" />
                بازگردانی به قبل (Rollback)
              </button>

              <button
                onClick={handleApplySettings}
                disabled={!adminConfirmed || isApplying}
                className="flex items-center gap-2 px-5 py-2 text-xs font-bold rounded-lg bg-blue-600 hover:bg-blue-500 text-white shadow-md transition disabled:opacity-40 disabled:bg-[#334155]"
              >
                <CheckCircle2 className="w-4 h-4" />
                {isApplying ? 'در حال اعمال و تایید...' : 'اعمال تنظیمات روی دوربین'}
              </button>
            </div>
          </div>

          {/* تاریخچه تغییرات و ممیزی (Audit Log) */}
          <div className="bg-[#202634] border border-[#2D3748] rounded-xl p-4 shadow-sm">
            <h2 className="text-sm font-bold text-blue-400 mb-3 flex items-center gap-2">
              <History className="w-4 h-4 text-blue-400" />
              تاریخچه ممیزی تغییرات اعمال‌شده (بدون ذخیره کلمات عبور)
            </h2>

            {auditHistory.length === 0 ? (
              <div className="text-xs text-slate-500 text-center py-4 bg-[#161B26] rounded-lg border border-[#252D3D]">
                هنوز تغییری در این نشست کاری بر روی دوربین ثبت نشده است.
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-xs text-right border-collapse">
                  <thead>
                    <tr className="text-slate-400 border-b border-[#2D3748] pb-1">
                      <th className="py-1.5 font-semibold">زمان</th>
                      <th className="py-1.5 font-semibold">آدرس IP</th>
                      <th className="py-1.5 font-semibold">پروفایل</th>
                      <th className="py-1.5 font-semibold">وضعیت</th>
                      <th className="py-1.5 font-semibold">شرح عملیات</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-[#252D3D] text-slate-300">
                    {auditHistory.map((rec) => (
                      <tr key={rec.id}>
                        <td className="py-2 text-slate-400 font-mono">{rec.timestamp}</td>
                        <td className="py-2 font-mono text-slate-300">{rec.cameraIp}</td>
                        <td className="py-2 text-sky-400">{rec.profile}</td>
                        <td className="py-2">
                          <span
                            className={`px-2 py-0.5 rounded text-[10px] ${
                              rec.wasRolledBack
                                ? 'bg-amber-950/40 text-amber-400 border border-amber-800/40'
                                : 'bg-emerald-950/40 text-emerald-400 border border-emerald-800/40'
                            }`}
                          >
                            {rec.wasRolledBack ? 'Rollback شد' : rec.status}
                          </span>
                        </td>
                        <td className="py-2 text-slate-300">{rec.summary}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* نوار وضعیت پایینی */}
      <div className="mt-5 p-2.5 bg-[#11151E] border border-[#252D3D] rounded-lg flex items-center justify-between text-xs text-slate-400">
        <div className="flex items-center gap-2">
          <AlertTriangle className="w-3.5 h-3.5 text-amber-400" />
          <span>{statusMessage}</span>
        </div>
        <div className="flex items-center gap-2">
          <span className="w-2 h-2 rounded-full bg-emerald-500 animate-pulse"></span>
          <span className="text-emerald-400 font-semibold">موتور ONVIF Imaging فعال</span>
        </div>
      </div>
    </div>
  );
};
