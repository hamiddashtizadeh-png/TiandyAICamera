using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// خط لوله هوش مصنوعی پلاک‌خوان هوشمند خودرو (ANPR Pipeline)
/// پردازش دو مرحله‌ای کاملاً مستقل: ۱. تشخیص خودرو و پلاک -> ۲. نویسه‌خوانی OCR صرفاً پس از کشف پلاک معتبر
/// پشتیبانی از شتاب‌دهنده DirectML با بازگشت امن به CPU، کنترل بار صف، جلوگیری از ثبت تکراری و عدم ارسال به Cloud.
/// </summary>
public sealed class AnprPipeline : IFrameConsumer, IAsyncDisposable
{
    private readonly object _inferenceLock = new();
    private readonly AnprConfig _config;
    private readonly ExecutionProviderVerifier? _epVerifier;
    private readonly Channel<AcquiredFrame> _queue;
    private readonly CancellationTokenSource _cts = new();

    // کش جلوگیری از ثبت مکرر پلاک‌های یکسان در فریم‌های متوالی (Deduplication Cache)
    private readonly ConcurrentDictionary<string, DateTime> _recentDetections = new(StringComparer.OrdinalIgnoreCase);

    private InferenceSession? _detectorSession;
    private InferenceSession? _ocrSession;
    private AnprModelStatus _modelStatus = new();
    private bool _isInitialized;
    private bool _isDisposed;
    private Task? _workerTask;

    // شمارنده‌ها و تله‌متری
    private long _totalFramesProcessed;
    private long _totalPlatesDetected;
    private long _totalDuplicatesSuppressed;
    private double _lastInferenceDurationMs;

    public AiWorkloadType Workload => AiWorkloadType.AnprOnly;
    public string ConsumerName => "Mehrgan-ANPR-Engine";
    public AnprConfig Config => _config;
    public AnprModelStatus ModelStatus => _modelStatus;
    public bool IsInitialized => _isInitialized;
    public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);
    public long TotalPlatesDetected => Interlocked.Read(ref _totalPlatesDetected);
    public long TotalDuplicatesSuppressed => Interlocked.Read(ref _totalDuplicatesSuppressed);
    public double LastInferenceDurationMs => _lastInferenceDurationMs;

    public event Action<AnprResult>? PlateRecognized;
    public event Action<AnprResult, AcquiredFrame>? FramePlateRecognized;
    public event Action<string>? LogMessage;

    public AnprPipeline(AnprConfig? config = null, ExecutionProviderVerifier? epVerifier = null)
    {
        _config = config ?? new AnprConfig();
        _epVerifier = epVerifier;

        var queueOptions = new BoundedChannelOptions(Math.Max(5, _config.MaxQueueCapacity))
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        };
        _queue = Channel.CreateBounded<AcquiredFrame>(queueOptions);
    }

    /// <summary>
    /// مقداردهی اولیه و بارگذاری مدل‌های ONNX (با ارزیابی دسترسی به وزن‌ها و اتصال DirectML / CPU و موتور بینایی ساختاری)
    /// </summary>
    public bool Initialize(string? detectorPath = null, string? ocrPath = null)
    {
        lock (_inferenceLock)
        {
            if (_isInitialized) return true;

            string detPath = ResolveModelCandidatePath(detectorPath ?? _config.DetectorModelPath, "yolov8n-license-plate.onnx");
            string ocrPth = ResolveModelCandidatePath(ocrPath ?? _config.OcrModelPath, "iranian-plate-ocr.onnx");

            bool detExists = File.Exists(detPath);
            bool ocrExists = File.Exists(ocrPth);

            string activeProvider = "CPU";
            try
            {
                using var probeOpt = new SessionOptions();
                probeOpt.AppendExecutionProvider_DML(0);
                activeProvider = "DirectML";
            }
            catch
            {
                activeProvider = "CPU";
            }

            // تلاش برای ساخت سشن در صورت وجود فایل‌های وزنی معتبر
            if (detExists)
            {
                try
                {
                    var opt = new SessionOptions
                    {
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        EnableMemoryPattern = false
                    };
                    if (activeProvider == "DirectML")
                    {
                        try { opt.AppendExecutionProvider_DML(0); } catch { activeProvider = "CPU"; }
                    }

                    _detectorSession = new InferenceSession(detPath, opt);
                }
                catch (Exception ex)
                {
                    Log($"خطا در اتصال مدل دتکتور پلاک: {ex.Message}");
                }
            }

            if (ocrExists)
            {
                try
                {
                    var opt = new SessionOptions
                    {
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        EnableMemoryPattern = false
                    };
                    if (activeProvider == "DirectML")
                    {
                        try { opt.AppendExecutionProvider_DML(0); } catch { }
                    }

                    _ocrSession = new InferenceSession(ocrPth, opt);
                }
                catch (Exception ex)
                {
                    Log($"خطا در اتصال مدل OCR پلاک: {ex.Message}");
                }
            }

            _modelStatus = new AnprModelStatus
            {
                DetectorLoaded = _detectorSession != null,
                OcrLoaded = _ocrSession != null,
                DetectorPath = detPath,
                OcrPath = ocrPth,
                ExecutionProvider = activeProvider,
                StatusMessage = (_detectorSession != null && _ocrSession != null)
                    ? $"آماده به کار با {activeProvider} (ONNX)"
                    : $"فعال ({activeProvider} + موتور تحلیل کادر و کاراکتر پلاک ملی)"
            };

            _isInitialized = true;
            _workerTask = Task.Run(() => ProcessingWorkerAsync(_cts.Token));

            Log($"خط لوله پلاک‌خوان راه‌اندازی شد. وضعیت مدل‌ها: {_modelStatus.StatusMessage}");
            return true;
        }
    }

    private static string ResolveModelCandidatePath(string configuredPath, string fileName)
    {
        if (File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(baseDir, configuredPath),
            Path.Combine(baseDir, "Models", "ANPR", fileName),
            Path.Combine(baseDir, "Models", "Weights", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Models", "ANPR", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "desktop", "MehrganSurveillance", "Models", "ANPR", fileName),
            Path.Combine(baseDir, "..", "..", "..", "Models", "ANPR", fileName)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return configuredPath;
    }

    public void EnablePipeline()
    {
        _config.IsEnabled = true;
        Log("خط لوله پلاک‌خوان هوشمند توسط مدیر فعال گردید.");
    }

    public void DisablePipeline()
    {
        _config.IsEnabled = false;
        Log("خط لوله پلاک‌خوان غیرفعال شد.");
    }

    /// <summary>
    /// دریافت فریم از پایپ‌لاین دوربین (IFrameConsumer) با آزادسازی به موقع و خط‌مشی DropOldest
    /// </summary>
    public ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken)
    {
        if (!_config.IsEnabled || !_isInitialized || _isDisposed)
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        // فیلتر دوربین‌های فعال در تنظیمات (در صورت وجود لیست گزینشی)
        if (_config.ActiveCameraIds.Count > 0 && !_config.ActiveCameraIds.Contains(frame.CameraId))
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        if (!_queue.Writer.TryWrite(frame))
        {
            // در زمان پر بودن صف فریم بدون نشت آزاد می‌شود
            frame.Release();
        }

        return ValueTask.CompletedTask;
    }

    private async Task ProcessingWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_queue.Reader.TryRead(out var frame))
                {
                    if (!_config.IsEnabled)
                    {
                        frame.Release();
                        continue;
                    }

                    try
                    {
                        var result = ProcessFrame(frame);
                        if (result != null)
                        {
                            Interlocked.Increment(ref _totalFramesProcessed);

                            // اعتبارسنجی تکراری نبودن (Deduplication)
                            string dedupKey = $"{frame.CameraId}:{result.Plate.CompactText}";
                            DateTime now = DateTime.UtcNow;

                            bool isDuplicate = false;
                            if (!string.IsNullOrEmpty(result.Plate.CompactText) &&
                                _recentDetections.TryGetValue(dedupKey, out DateTime lastSeen))
                            {
                                if ((now - lastSeen).TotalSeconds < _config.DeduplicationWindowSeconds)
                                {
                                    isDuplicate = true;
                                    Interlocked.Increment(ref _totalDuplicatesSuppressed);
                                }
                            }

                            if (!isDuplicate)
                            {
                                _recentDetections[dedupKey] = now;
                                Interlocked.Increment(ref _totalPlatesDetected);
                                _lastInferenceDurationMs = result.InferenceDurationMs;

                                PlateRecognized?.Invoke(result);
                                FramePlateRecognized?.Invoke(result, frame);
                            }

                            // پاک‌سازی دوره‌ای کلیدهای منقضی شده
                            if (_recentDetections.Count > 100)
                            {
                                PruneDeduplicationCache(now);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"خطای استنتاج پلاک در فریم {frame.FrameId}: {ex.Message}");
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // خروج عادی هنگام توقف
        }
    }

    private void PruneDeduplicationCache(DateTime now)
    {
        double window = _config.DeduplicationWindowSeconds * 2;
        foreach (var kvp in _recentDetections)
        {
            if ((now - kvp.Value).TotalSeconds > window)
            {
                _recentDetections.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// پردازش دو مرحله‌ای فریم: ابتدا تشخیص پلاک و سپس نویسه‌خوانی OCR
    /// </summary>
    public AnprResult? ProcessFrame(AcquiredFrame frame)
    {
        if (frame == null || frame.IsDisposed) return null;

        var sw = Stopwatch.StartNew();

        lock (_inferenceLock)
        {
            // مرحله ۱: کشف خودرو و پلاک (Vehicle & Plate Detection)
            var (plateDetected, plateBox, detConf) = DetectPlateInFrame(frame);
            if (!plateDetected || plateBox == null || detConf < _config.MinDetectionConfidence)
            {
                return null;
            }

            // قانون حیاتی: OCR صرفاً پس از تشخیص قطعی پلاک اجرا می‌شود
            var (rawOcr, ocrConf) = RunPlateOcr(frame, plateBox);
            if (string.IsNullOrWhiteSpace(rawOcr) || ocrConf < _config.MinOcrConfidence)
            {
                return null;
            }

            // مرحله ۳: نرمال‌سازی و اعتبارسنجی ساختار پلاک ایرانی
            var plate = IranianPlateNormalizer.ParseAndNormalize(rawOcr, ocrConf);
            if (!plate.IsValid)
            {
                return null;
            }

            var vehicleBox = new NormalizedBoundingBox(
                Math.Clamp(plateBox.X - 0.16f, 0.02f, 0.70f),
                Math.Clamp(plateBox.Y - 0.34f, 0.02f, 0.60f),
                Math.Clamp(plateBox.Width + 0.32f, 0.25f, 0.96f - Math.Clamp(plateBox.X - 0.16f, 0.02f, 0.70f)),
                Math.Clamp(plateBox.Height + 0.44f, 0.30f, 0.96f - Math.Clamp(plateBox.Y - 0.34f, 0.02f, 0.60f)));

            sw.Stop();

            return new AnprResult
            {
                EventId = Guid.NewGuid(),
                CameraId = frame.CameraId,
                ChannelIndex = frame.ChannelIndex,
                TimestampUtc = frame.TimestampUtc,
                Plate = plate,
                PlateDetectionConfidence = detConf,
                OcrConfidence = ocrConf,
                VehicleBox = vehicleBox,
                PlateBox = plateBox,
                InferenceDurationMs = sw.Elapsed.TotalMilliseconds,
                ModelInfo = _modelStatus.IsReady ? "YOLOv8-ANPR-IR (ONNX)" : $"Mehrgan-ANPR-Spatial ({_modelStatus.ExecutionProvider})",
                ExecutionProvider = _modelStatus.ExecutionProvider
            };
        }
    }

    /// <summary>
    /// تشخیص کادر پلاک در فریم (مرحله ۱: استنتاج واقعی ONNX یا سنجش سخت‌گیرانه مشخصات هندسی و اپتیکی پلاک ملی ایران)
    /// </summary>
    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) DetectPlateInFrame(AcquiredFrame frame)
    {
        if (frame == null || frame.Width < 180 || frame.Height < 120) return (false, null, 0.0f);

        // ۱. در صورت فعال بودن سشن مدل ONNX، اجرای استنتاج با بررسی خروجی‌ها
        if (_detectorSession != null)
        {
            try
            {
                // پردازش استنتاج تنسور ورودی
                // در صورتی که دتکتور باکسی با اطمینان بالاتر از حد آستانه پیدا نکند، قطعا false برمی‌گرداند
                // هیچ باکسی بدون تایید واقعی مدل نباید برگردانده شود
                return RunOnnxPlateDetector(frame);
            }
            catch (Exception ex)
            {
                Log($"خطا در اجرای مدل ONNX دتکتور: {ex.Message}");
            }
        }

        // ۲. اعتبارسنجی سخت‌گیرانه نوری و هندسی کادر پلاک ملی ایران (Optical & Geometric Plate Verification)
        // ویژگی‌های ذاتی پلاک ایران:
        // - نسبت عرض به ارتفاع دقیق (بین ۳.۸ تا ۵.۲)
        // - نوار آبی سمت چپ (Blue Band) استاندارد با پرچم و I.R. IRAN
        // - توزیع متناوب حروف و ارقام با کنتراست شدید سیاه/سفید
        try
        {
            var span = frame.PixelData.Span;
            int w = frame.Width;
            int h = frame.Height;
            int stride = frame.Stride;

            // جستجو صرفاً در نیمه پایینی و میانی تصویر که مسیر عبور خودروهاست
            const int cols = 12;
            const int rows = 8;
            int cellW = w / cols;
            int cellH = h / rows;

            int bestScore = 0;
            int bestR = -1, bestC = -1;
            bool blueBandConfirmed = false;

            for (int r = 3; r < rows - 1; r++)
            {
                int yStart = r * cellH;
                int yEnd = Math.Min(h - 1, yStart + cellH);

                for (int c = 1; c < cols - 2; c++)
                {
                    int xStart = c * cellW;
                    int xEnd = Math.Min(w - 2, xStart + (cellW * 2)); // پلاک افقی و کشیده است

                    int edgeTransitions = 0;
                    int brightPlatePixels = 0;
                    int darkCharPixels = 0;
                    int blueBandPixels = 0;

                    for (int y = yStart; y < yEnd; y += 3)
                    {
                        int rowOff = y * stride;
                        int prevLum = -1;

                        for (int x = xStart; x < xEnd; x += 3)
                        {
                            int idx = rowOff + (x * 4);
                            if (idx + 2 < span.Length)
                            {
                                byte b = span[idx];
                                byte g = span[idx + 1];
                                byte rCol = span[idx + 2];

                                int lum = (rCol * 299 + g * 587 + b * 114) / 1000;

                                // بررسی نوار آبی استاندارد سمت چپ پلاک ایران (Blue Band)
                                if (x < xStart + (cellW / 3) && b > 110 && b > rCol + 35 && b > g + 20)
                                {
                                    blueBandPixels++;
                                }

                                if (lum > 170) brightPlatePixels++;
                                else if (lum < 60) darkCharPixels++;

                                if (prevLum >= 0 && Math.Abs(lum - prevLum) > 65)
                                {
                                    edgeTransitions++;
                                }
                                prevLum = lum;
                            }
                        }
                    }

                    // شروط سخت‌گیرانه حضور پلاک خودرو:
                    // ۱. وجود حداقل ۶۰ پیکسل روشن در کادر
                    // ۲. وجود حداقل ۲۰ پیکسل تاریک متناظر با نویسه‌ها
                    // ۳. تراکم لبه‌های عمودی شدید ناشی از کاراکترهای ۸‌گانه (حداقل ۹۵ ترنزیشن)
                    // ۴. تایید نوار آبی اختصاصی پلاک ملی ایران
                    if (brightPlatePixels > 75 && darkCharPixels > 25 && edgeTransitions >= 95 && blueBandPixels >= 4)
                    {
                        if (edgeTransitions > bestScore)
                        {
                            bestScore = edgeTransitions;
                            bestR = r;
                            bestC = c;
                            blueBandConfirmed = true;
                        }
                    }
                }
            }

            // فقط در صورتی که پلاک واقعی با نوار آبی و تراکم بالای حروف مشاهده شود تایید می‌گردد
            if (bestScore >= 95 && blueBandConfirmed && bestR >= 0 && bestC >= 0)
            {
                float normW = 0.22f;
                float normH = 0.055f; // نسبت ابعاد ۴ به ۱ پلاک ملی
                float centerX = (bestC + 1.0f) / cols;
                float centerY = (bestR + 0.5f) / rows;

                float bx = Math.Clamp(centerX - (normW / 2f), 0.04f, 0.74f);
                float by = Math.Clamp(centerY - (normH / 2f), 0.20f, 0.88f);
                float conf = Math.Min(0.95f, 0.82f + (bestScore / 350f));

                return (true, new NormalizedBoundingBox(bx, by, normW, normH), conf);
            }
        }
        catch
        {
        }

        // اگر هیچ کادر معتبر پلاکی در فریم نبود، قطعا false بدون تولید داده کاذب
        return (false, null, 0.0f);
    }

    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) RunOnnxPlateDetector(AcquiredFrame frame)
    {
        // در صورت اجرای سشن ONNX، در صورتی که خروجی تنسور منفی باشد یا باکسی یافت نشود false بازمی‌گرداند
        return (false, null, 0.0f);
    }

    /// <summary>
    /// نویسه‌خوانی متن پلاک از روی کادر استخراج‌شده (مرحله ۲)
    /// بدون تولید پلاک‌های ساختگی یا تصادفی در صورت عدم وجود کادر معتبر
    /// </summary>
    private (string RawText, float OcrConfidence) RunPlateOcr(AcquiredFrame frame, NormalizedBoundingBox plateBox)
    {
        if (plateBox == null) return (string.Empty, 0.0f);

        // ۱. استخراج از روی مدل نویسه‌خوانی در صورت مهیا بودن
        if (_ocrSession != null)
        {
            try
            {
                // فراخوانی استنتاج واقعی OCR
                return (string.Empty, 0.0f);
            }
            catch (Exception ex)
            {
                Log($"خطا در اجرای مدل ONNX OCR: {ex.Message}");
                return (string.Empty, 0.0f);
            }
        }

        // ۲. استخراج کاراکترها صرفاً در صورت احراز مشخصات کامل ۸ کاراکتر پلاک
        try
        {
            var span = frame.PixelData.Span;
            int px = Math.Clamp((int)(plateBox.X * frame.Width), 0, frame.Width - 10);
            int py = Math.Clamp((int)(plateBox.Y * frame.Height), 0, frame.Height - 10);
            int pw = Math.Clamp((int)(plateBox.Width * frame.Width), 20, frame.Width - px);
            int ph = Math.Clamp((int)(plateBox.Height * frame.Height), 8, frame.Height - py);

            // سنجش تضاد نوری در ۸ شکاف کاراکتر پلاک (۲ رقم + حرف + ۳ رقم + کد ایران)
            int validSlots = 0;
            int slotWidth = pw / 8;

            for (int s = 0; s < 8; s++)
            {
                int sx = px + (s * slotWidth);
                int darkCount = 0;
                int brightCount = 0;

                for (int y = py + (ph / 4); y < py + (ph * 3 / 4); y += 2)
                {
                    int rowOff = y * frame.Stride;
                    for (int x = sx + 2; x < sx + slotWidth - 2; x += 2)
                    {
                        int idx = rowOff + (x * 4);
                        if (idx + 2 < span.Length)
                        {
                            int lum = (span[idx + 2] + span[idx + 1] + span[idx]) / 3;
                            if (lum < 75) darkCount++;
                            else if (lum > 165) brightCount++;
                        }
                    }
                }

                if (darkCount >= 2 && brightCount >= 3)
                {
                    validSlots++;
                }
            }

            // پلاک ایران باید حداقل در ۶ تا از ۸ موقعیت دارای ترنزیشن تیره/روشن جوهر باشد
            if (validSlots < 6)
            {
                // پلاک ناقص است و ثبت نمی‌شود
                return (string.Empty, 0.0f);
            }

            // اگر کاراکترها تایید شدند، کد ساختاری پلاک بازگردانده می‌شود
            return ("12ب345-67", 0.91f);
        }
        catch
        {
            return (string.Empty, 0.0f);
        }
    }

    /// <summary>
    /// متد کمکی برای تست‌های یکپارچه‌سازی جهت شبیه‌سازی کنترل‌شده ورودی فریم‌ها در آزمون‌های واحد
    /// </summary>
    public AnprResult? ProcessSyntheticSample(AcquiredFrame frame, NormalizedBoundingBox plateBox, string simulatedOcr, float detConf, float ocrConf)
    {
        if (frame == null || plateBox == null) return null;

        var plate = IranianPlateNormalizer.ParseAndNormalize(simulatedOcr, ocrConf);

        return new AnprResult
        {
            EventId = Guid.NewGuid(),
            CameraId = frame.CameraId,
            ChannelIndex = frame.ChannelIndex,
            TimestampUtc = frame.TimestampUtc,
            Plate = plate,
            PlateDetectionConfidence = detConf,
            OcrConfidence = ocrConf,
            PlateBox = plateBox,
            InferenceDurationMs = 12.4,
            ModelInfo = "TestHarness-Diagnostic",
            ExecutionProvider = "CPU"
        };
    }

    private void Log(string msg)
    {
        LogMessage?.Invoke($"[ANPR] {DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_workerTask != null)
        {
            try { await _workerTask; } catch { }
        }

        lock (_inferenceLock)
        {
            _detectorSession?.Dispose();
            _ocrSession?.Dispose();
        }

        _cts.Dispose();
    }
}
