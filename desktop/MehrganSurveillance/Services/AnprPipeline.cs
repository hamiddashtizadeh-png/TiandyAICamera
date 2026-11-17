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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance.Services;

/// <summary>
/// خط لوله هوش مصنوعی پلاک‌خوان هوشمند خودرو (ANPR Pipeline)
/// سیاست سخت‌گیرانه عدم جعل داده: بدون تایید واقعی OCR هیچ پلاک تصادفی یا فرضی در دیتابیس ثبت نمی‌شود.
/// در حین پایش زنده، رویداد کادر پلاک و تصویر زنده جهت نمایش روی دوربین و پنجره ANPR ارسال می‌گردد.
/// </summary>
public sealed class AnprPipeline : IFrameConsumer, IAsyncDisposable
{
    // واژگان استاندارد کاراکترهای پلاک ملی ایران برای دیکودینگ خروجی CTC مدل‌های OCR
    private static readonly string[] OcrVocabulary =
    {
        "", // CTC Blank (index 0)
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "الف", "ب", "پ", "ت", "ث", "ج", "د", "س", "ص", "ط",
        "ع", "ق", "ل", "م", "ن", "و", "ه", "ی", "ژ", "ک", "ش", "ز"
    };

    private readonly object _inferenceLock = new();
    private readonly AnprConfig _config;
    private readonly AiImageOptimizationEngine _imageOptimizer = new();
    private readonly AutoSceneImageTuner _autoTuner = new();
    private byte[]? _tuningScratchBuffer;

    public AiImageOptimizationEngine ImageOptimizer => _imageOptimizer;
    public AutoSceneImageTuner AutoTuner => _autoTuner;
    public bool IsAutoImageOptimizationEnabled { get; set; } = true;

    private readonly ExecutionProviderVerifier? _epVerifier;
    private readonly Channel<AcquiredFrame> _queue;
    private readonly CancellationTokenSource _cts = new();

    // کش جلوگیری از ثبت مکرر پلاک‌های یکسان در فریم‌های متوالی
    private readonly ConcurrentDictionary<string, DateTime> _recentDetections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, DateTime> _lastPreviewEncodePerChannel = new();
    private int _previewSubscriberCount;

    private InferenceSession? _detectorSession;
    private InferenceSession? _ocrSession;
    private AnprModelStatus _modelStatus = new();
    private bool _isInitialized;
    private bool _isDisposed;
    private Task? _workerTask;

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
    public event Action<AnprLiveObservation>? LiveObservationAvailable;
    public event Action<AutoImageTuningRecommendation>? AutoSceneTuningUpdated;
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

    public void RegisterPreviewSubscriber() => Interlocked.Increment(ref _previewSubscriberCount);
    public void UnregisterPreviewSubscriber()
    {
        int val = Interlocked.Decrement(ref _previewSubscriberCount);
        if (val < 0) Interlocked.Exchange(ref _previewSubscriberCount, 0);
    }

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

            string statusMsg;
            if (_detectorSession != null && _ocrSession != null)
                statusMsg = $"آماده به کار با {activeProvider} (ONNX کامل)";
            else if (_detectorSession != null)
                statusMsg = $"دتکتور ONNX فعال ({activeProvider}) — فایل iranian-plate-ocr.onnx یافت نشد";
            else
                statusMsg = $"کادریاب زنده فعال ({activeProvider}) — بدون مدل ONNX OCR (عدم ثبت پلاک جعلی)";

            _modelStatus = new AnprModelStatus
            {
                DetectorLoaded = _detectorSession != null,
                OcrLoaded = _ocrSession != null,
                DetectorPath = detPath,
                OcrPath = ocrPth,
                ExecutionProvider = activeProvider,
                StatusMessage = statusMsg
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

    public ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken)
    {
        if (!_config.IsEnabled || !_isInitialized || _isDisposed)
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        if (_config.ActiveCameraIds.Count > 0 && !_config.ActiveCameraIds.Contains(frame.CameraId))
        {
            frame.Release();
            return ValueTask.CompletedTask;
        }

        if (!_queue.Writer.TryWrite(frame))
        {
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
                        var (result, observation) = ProcessFrameWithObservation(frame);
                        if (observation != null)
                        {
                            LiveObservationAvailable?.Invoke(observation);
                        }

                        if (result != null)
                        {
                            Interlocked.Increment(ref _totalFramesProcessed);

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
    /// پردازش دو مرحله‌ای فریم: تنها در صورتی نتیجه نهایی بازمی‌گرداند که OCR واقعی پلاک معتبر را تایید کند
    /// </summary>
    public AnprResult? ProcessFrame(AcquiredFrame frame)
    {
        var (result, _) = ProcessFrameWithObservation(frame);
        return result;
    }

    private (AnprResult? Result, AnprLiveObservation? Observation) ProcessFrameWithObservation(AcquiredFrame frame)
    {
        if (frame == null || frame.IsDisposed) return (null, null);

        var sw = Stopwatch.StartNew();

        AutoImageTuningRecommendation? activeTuning = null;
        if (IsAutoImageOptimizationEnabled)
        {
            var (tuningChanged, rec) = _autoTuner.EvaluateFrameIfDue(frame);
            activeTuning = rec;
            if (rec != null)
            {
                _imageOptimizer.ProfileRegistry.ApplyAutoSceneProfile(rec.OptimizedProfile, persistToDisk: false);
                if (tuningChanged)
                {
                    AutoSceneTuningUpdated?.Invoke(rec);
                    Log($"تنظیم خودکار تصویر ({rec.SceneLabelFa}) روی دوربین {frame.CameraId} اعمال شد.");
                }
            }
        }

        NormalizedBoundingBox? plateBox;
        NormalizedBoundingBox? vehicleBox = null;
        float detConf;
        bool plateDetected;
        byte[]? previewJpeg = null;
        bool needPreviewJpeg = Volatile.Read(ref _previewSubscriberCount) > 0 && ShouldEncodePreview(frame.ChannelIndex);

        lock (_inferenceLock)
        {
            ReadOnlySpan<byte> detectionSpan = frame.PixelData.Span;
            byte[]? tunedWorkingBuffer = null;

            if (IsAutoImageOptimizationEnabled && activeTuning != null && activeTuning.RequiresWorkingBufferEnhancement)
            {
                tunedWorkingBuffer = AutoSceneImageTuner.CreateIsolatedBalancedBuffer(
                    frame.PixelData.Span,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    activeTuning,
                    ref _tuningScratchBuffer);
                detectionSpan = tunedWorkingBuffer.AsSpan(0, frame.Stride * frame.Height);
            }

            if (needPreviewJpeg)
            {
                previewJpeg = EncodeFramePreviewJpeg(detectionSpan, frame.Width, frame.Height, frame.Stride);
            }

            (plateDetected, plateBox, detConf) = DetectPlateInFrame(detectionSpan, frame.Width, frame.Height, frame.Stride);

            if (!plateDetected || plateBox == null || detConf < _config.MinDetectionConfidence)
            {
                var emptyObs = previewJpeg != null
                    ? new AnprLiveObservation
                    {
                        CameraId = frame.CameraId,
                        ChannelIndex = frame.ChannelIndex,
                        TimestampUtc = frame.TimestampUtc,
                        PreviewFrameJpeg = previewJpeg
                    }
                    : null;
                return (null, emptyObs);
            }

            vehicleBox = BuildVehicleBoxFromPlate(plateBox);

            // اگر مدل OCR بارگذاری نشده است، فقط کادر پلاک روی تصویر زنده نمایش داده می‌شود و هرگز پلاک جعلی ساخته نمی‌شود
            if (_ocrSession == null)
            {
                var candidateObs = new AnprLiveObservation
                {
                    CameraId = frame.CameraId,
                    ChannelIndex = frame.ChannelIndex,
                    TimestampUtc = frame.TimestampUtc,
                    PlateBox = plateBox,
                    VehicleBox = vehicleBox,
                    PlateConfidence = detConf,
                    LabelText = "کادر پلاک (نیازمند مدل OCR)",
                    IsOcrVerified = false,
                    PreviewFrameJpeg = previewJpeg
                };
                return (null, candidateObs);
            }

            // برش کاملاً ایزوله کادر پلاک بدون دستکاری بافر اصلی
            int px = Math.Clamp((int)(plateBox.X * frame.Width), 0, frame.Width - 10);
            int py = Math.Clamp((int)(plateBox.Y * frame.Height), 0, frame.Height - 10);
            int pw = Math.Clamp((int)(plateBox.Width * frame.Width), 20, frame.Width - px);
            int ph = Math.Clamp((int)(plateBox.Height * frame.Height), 8, frame.Height - py);

            byte[] rawCropBytes = ExtractCropBuffer(frame.PixelData.Span, frame.Width, frame.Height, frame.Stride, px, py, pw, ph);
            int cropStride = pw * 4;

            // اجرای موتور بهینه‌سازی تصویر همراه با OCR واقعی ONNX روی نامزدها
            var optResult = _imageOptimizer.OptimizePlateCropAsync(
                frame.CameraId,
                rawCropBytes,
                pw,
                ph,
                cropStride,
                4,
                (buf, w, h, s) => EvaluateOcrOnBuffer(buf, w, h, s)).GetAwaiter().GetResult();

            var winningCandidate = optResult.BestCandidate;
            var plate = winningCandidate.Plate;
            float ocrConf = winningCandidate.OcrConfidence;

            if (!plate.IsValid || ocrConf < _config.MinOcrConfidence)
            {
                var partialObs = new AnprLiveObservation
                {
                    CameraId = frame.CameraId,
                    ChannelIndex = frame.ChannelIndex,
                    TimestampUtc = frame.TimestampUtc,
                    PlateBox = plateBox,
                    VehicleBox = vehicleBox,
                    PlateConfidence = detConf,
                    LabelText = plate.ValidationStatus == AnprValidationStatus.Partial
                        ? $"ناخوانا: {plate.CompactText}"
                        : "کادر پلاک شناسایی شد",
                    IsOcrVerified = false,
                    PreviewFrameJpeg = previewJpeg
                };
                return (null, partialObs);
            }

            sw.Stop();

            var verifiedObs = new AnprLiveObservation
            {
                CameraId = frame.CameraId,
                ChannelIndex = frame.ChannelIndex,
                TimestampUtc = frame.TimestampUtc,
                PlateBox = plateBox,
                VehicleBox = vehicleBox,
                PlateConfidence = ocrConf,
                LabelText = plate.NormalizedText,
                IsOcrVerified = true,
                PreviewFrameJpeg = previewJpeg
            };

            var res = new AnprResult
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
                PlateCropJpeg = winningCandidate.JpegBytes,
                OriginalPlateCropJpeg = optResult.OriginalCropJpeg,
                OptimizationMethod = optResult.WinningMethod,
                OptimizationConfidenceGain = optResult.ConfidenceImprovementPercentage,
                InferenceDurationMs = sw.Elapsed.TotalMilliseconds,
                ModelInfo = "YOLOv8-ANPR-IR (ONNX)",
                ExecutionProvider = _modelStatus.ExecutionProvider
            };

            return (res, verifiedObs);
        }
    }

    private bool ShouldEncodePreview(int channelIndex)
    {
        var now = DateTime.UtcNow;
        if (_lastPreviewEncodePerChannel.TryGetValue(channelIndex, out var last) &&
            (now - last).TotalMilliseconds < 150)
        {
            return false;
        }
        _lastPreviewEncodePerChannel[channelIndex] = now;
        return true;
    }

    private static NormalizedBoundingBox BuildVehicleBoxFromPlate(NormalizedBoundingBox plateBox)
    {
        float vx = Math.Clamp(plateBox.X - 0.16f, 0.02f, 0.70f);
        float vy = Math.Clamp(plateBox.Y - 0.34f, 0.02f, 0.60f);
        float vw = Math.Clamp(plateBox.Width + 0.32f, 0.25f, 0.96f - vx);
        float vh = Math.Clamp(plateBox.Height + 0.44f, 0.30f, 0.96f - vy);
        return new NormalizedBoundingBox(vx, vy, vw, vh);
    }

    /// <summary>
    /// تشخیص کادر پلاک در فریم (استنتاج واقعی YOLOv8 ONNX یا کادریاب هندسی-نوری چندمقیاسه با حذف سطح آسفالت/سایه)
    /// </summary>
    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) DetectPlateInFrame(
        ReadOnlySpan<byte> span, int w, int h, int stride)
    {
        if (w < 180 || h < 120 || span.IsEmpty) return (false, null, 0.0f);

        if (_detectorSession != null)
        {
            try
            {
                return RunOnnxPlateDetector(span, w, h, stride);
            }
            catch (Exception ex)
            {
                Log($"خطا در اجرای مدل ONNX دتکتور: {ex.Message}");
            }
        }

        // کادریاب هندسی-نوری چندمقیاسه پلاک ملی ایران با فیلتر حذف بافت زمین/آسفالت/سایه
        try
        {
            float bestScore = 0f;
            NormalizedBoundingBox? bestBox = null;

            // دو مقیاس واقعی پلاک خودرو: نمای نزدیک/متوسط (14%) و نمای خیابان (9%) با نسبت ابعاد 4.3:1
            ReadOnlySpan<float> widthFractions = stackalloc float[] { 0.145f, 0.095f };

            foreach (float wf in widthFractions)
            {
                int boxW = Math.Clamp((int)(w * wf), 48, w / 2);
                int boxH = Math.Clamp((int)(boxW / 4.25f), 14, h / 5);
                int blueBandW = Math.Max(6, (int)(boxW * 0.14f));
                int stepX = Math.Max(6, boxW / 3);
                int stepY = Math.Max(4, boxH / 2);
                int marginH = Math.Max(6, boxH / 2);

                int yMin = Math.Max(marginH + 2, (int)(h * 0.18f));
                int yMax = Math.Min(h - boxH - marginH - 2, (int)(h * 0.88f) - boxH);
                int xMin = (int)(w * 0.03f);
                int xMax = Math.Min(w - boxW - 2, (int)(w * 0.96f) - boxW);

                for (int y = yMin; y <= yMax; y += stepY)
                {
                    for (int x = xMin; x <= xMax; x += stepX)
                    {
                        // حذف ناحیه نمایشگر تاریخ/ساعت دوربین در گوشه‌های بالا
                        if (y < (int)(h * 0.18f) && (x < (int)(w * 0.42f) || x > (int)(w * 0.78f)))
                        {
                            continue;
                        }

                        // ۱. بررسی نوار آبی واقعی سمت چپ پلاک ایران
                        int bluePixels = 0;
                        int blueSamples = 0;
                        int xBlueEnd = x + blueBandW;
                        int stepP = Math.Max(2, boxH / 7);

                        for (int py = y + 1; py < y + boxH - 1; py += stepP)
                        {
                            int rOff = py * stride;
                            for (int px = x; px < xBlueEnd; px += 2)
                            {
                                int idx = rOff + (px * 4);
                                if (idx + 2 >= span.Length) continue;
                                byte b = span[idx];
                                byte g = span[idx + 1];
                                byte r = span[idx + 2];
                                blueSamples++;

                                if (b >= 115 && r <= 105 && (b - r) >= 48 && (b - g) >= 22)
                                {
                                    bluePixels++;
                                }
                            }
                        }

                        float blueRatio = blueSamples > 0 ? (float)bluePixels / blueSamples : 0f;
                        if (bluePixels < 4 || blueRatio < 0.28f)
                        {
                            continue;
                        }

                        // ۲. بررسی کنتراست ارقام و زمینه روشن در بدنه پلاک
                        int innerSamples = 0;
                        int edgeTransitions = 0;
                        long lumSum = 0;
                        int brightCount = 0;
                        int darkCount = 0;
                        int xInnerStart = xBlueEnd;
                        int xInnerEnd = x + boxW - 2;

                        for (int py = y + 2; py < y + boxH - 2; py += stepP)
                        {
                            int rOff = py * stride;
                            int prevLum = -1;
                            for (int px = xInnerStart; px < xInnerEnd; px += 2)
                            {
                                int idx = rOff + (px * 4);
                                if (idx + 2 >= span.Length) continue;
                                int lum = (span[idx + 2] * 299 + span[idx + 1] * 587 + span[idx] * 114) / 1000;
                                lumSum += lum;
                                innerSamples++;

                                if (lum >= 145) brightCount++;
                                else if (lum <= 88) darkCount++;

                                if (prevLum >= 0 && Math.Abs(lum - prevLum) >= 44)
                                {
                                    edgeTransitions++;
                                }
                                prevLum = lum;
                            }
                        }

                        if (innerSamples < 24) continue;
                        float brightRatio = (float)brightCount / innerSamples;
                        float darkRatio = (float)darkCount / innerSamples;
                        float innerEdgeDensity = (float)edgeTransitions / innerSamples;

                        if (brightRatio < 0.28f || darkRatio < 0.12f || innerEdgeDensity < 0.13f)
                        {
                            continue;
                        }

                        // ۳. فیلتر حیاتی حذف آسفالت و پیاده‌رو (مقایسه لبه‌های داخل پلاک با حاشیه بالا و پایین)
                        int surroundSamples = 0;
                        int surroundEdges = 0;
                        for (int band = 0; band < 2; band++)
                        {
                            int sy0 = band == 0 ? y - marginH : y + boxH + 1;
                            int sy1 = band == 0 ? y - 1 : y + boxH + marginH;
                            for (int py = sy0; py < sy1; py += stepP)
                            {
                                int rOff = py * stride;
                                int prevLum = -1;
                                for (int px = xInnerStart; px < xInnerEnd; px += 2)
                                {
                                    int idx = rOff + (px * 4);
                                    if (idx + 2 >= span.Length) continue;
                                    int lum = (span[idx + 2] * 299 + span[idx + 1] * 587 + span[idx] * 114) / 1000;
                                    surroundSamples++;
                                    if (prevLum >= 0 && Math.Abs(lum - prevLum) >= 44)
                                    {
                                        surroundEdges++;
                                    }
                                    prevLum = lum;
                                }
                            }
                        }

                        float surroundDensity = surroundSamples > 0 ? (float)surroundEdges / surroundSamples : 0f;
                        // روی پیاده‌رو/سنگفرش یا شاخ‌وبرگ، لبه داخل کادر با اطراف تقریباً یکسان است (< 1.55x)
                        if (surroundDensity > 0.02f && innerEdgeDensity < surroundDensity * 1.55f)
                        {
                            continue;
                        }

                        float score = (blueRatio * 35f) + (innerEdgeDensity * 90f) + (brightRatio * 20f);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestBox = new NormalizedBoundingBox(
                                (float)x / w,
                                (float)y / h,
                                (float)boxW / w,
                                (float)boxH / h);
                        }
                    }
                }
            }

            if (bestBox != null && bestScore >= 26f)
            {
                float conf = Math.Clamp(0.68f + (bestScore / 140f), 0.68f, 0.94f);
                return (true, bestBox, conf);
            }
        }
        catch
        {
        }

        return (false, null, 0.0f);
    }

    /// <summary>
    /// استنتاج واقعی YOLOv8 ONNX دتکتور پلاک خودرو
    /// </summary>
    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) RunOnnxPlateDetector(
        ReadOnlySpan<byte> src, int frameWidth, int frameHeight, int frameStride)
    {
        if (_detectorSession == null) return (false, null, 0.0f);

        var inputMeta = _detectorSession.InputMetadata.First();
        var dims = inputMeta.Value.Dimensions;
        int targetH = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : 640;
        int targetW = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : 640;

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
        float scaleX = (float)frameWidth / targetW;
        float scaleY = (float)frameHeight / targetH;

        for (int y = 0; y < targetH; y++)
        {
            int sy = Math.Min(frameHeight - 1, (int)(y * scaleY));
            int rOff = sy * frameStride;
            for (int x = 0; x < targetW; x++)
            {
                int sx = Math.Min(frameWidth - 1, (int)(x * scaleX));
                int idx = rOff + (sx * 4);
                if (idx + 2 < src.Length)
                {
                    tensor[0, 0, y, x] = src[idx + 2] / 255.0f; // R
                    tensor[0, 1, y, x] = src[idx + 1] / 255.0f; // G
                    tensor[0, 2, y, x] = src[idx] / 255.0f;     // B
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputMeta.Key, tensor)
        };

        using var outputs = _detectorSession.Run(inputs);
        var outTensor = outputs.FirstOrDefault()?.AsTensor<float>();
        if (outTensor == null || outTensor.Dimensions.Length < 3) return (false, null, 0.0f);

        int d1 = outTensor.Dimensions[1];
        int d2 = outTensor.Dimensions[2];

        float bestConf = 0.0f;
        NormalizedBoundingBox? bestBox = null;

        // فرمت استاندارد YOLOv8: [1, 4+C, N] یا [1, N, 4+C]
        bool channelFirst = d1 < d2 && d1 >= 5;
        int numBoxes = channelFirst ? d2 : d1;
        int numAttrs = channelFirst ? d1 : d2;
        if (numAttrs < 5) return (false, null, 0.0f);

        for (int i = 0; i < numBoxes; i++)
        {
            float maxClassConf = 0.0f;
            for (int c = 4; c < numAttrs; c++)
            {
                float sc = channelFirst ? outTensor[0, c, i] : outTensor[0, i, c];
                if (sc > maxClassConf) maxClassConf = sc;
            }

            if (maxClassConf >= _config.MinDetectionConfidence && maxClassConf > bestConf)
            {
                float cx = channelFirst ? outTensor[0, 0, i] : outTensor[0, i, 0];
                float cy = channelFirst ? outTensor[0, 1, i] : outTensor[0, i, 1];
                float bw = channelFirst ? outTensor[0, 2, i] : outTensor[0, i, 2];
                float bh = channelFirst ? outTensor[0, 3, i] : outTensor[0, i, 3];

                // نرمال‌سازی در صورتی که خروجی بر حسب پیکسل ورودی 640 باشد
                if (cx > 1.5f || bw > 1.5f)
                {
                    cx /= targetW;
                    cy /= targetH;
                    bw /= targetW;
                    bh /= targetH;
                }

                float nx = Math.Clamp(cx - (bw / 2f), 0.0f, 0.98f);
                float ny = Math.Clamp(cy - (bh / 2f), 0.0f, 0.98f);
                float nw = Math.Clamp(bw, 0.02f, 1.0f - nx);
                float nh = Math.Clamp(bh, 0.02f, 1.0f - ny);

                bestConf = maxClassConf;
                bestBox = new NormalizedBoundingBox(nx, ny, nw, nh);
            }
        }

        return bestBox != null ? (true, bestBox, bestConf) : (false, null, 0.0f);
    }

    /// <summary>
    /// برش کاملاً ایزوله بافر پلاک از فریم اصلی بدون کوچک‌ترین تغییر در بافر اصلی
    /// </summary>
    public static byte[] ExtractCropBuffer(ReadOnlySpan<byte> src, int fullW, int fullH, int fullStride, int x, int y, int w, int h)
    {
        byte[] crop = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int srcOff = ((y + row) * fullStride) + (x * 4);
            int dstOff = row * (w * 4);
            int copyBytes = Math.Min(w * 4, src.Length - srcOff);
            if (copyBytes > 0 && srcOff >= 0 && srcOff < src.Length)
            {
                src.Slice(srcOff, copyBytes).CopyTo(crop.AsSpan(dstOff));
            }
        }
        return crop;
    }

    /// <summary>
    /// ارزیابی واقعی متن پلاک از روی بافر برش‌خورده با مدل ONNX OCR
    /// در نبود مدل OCR هرگز پلاک جعلی تولید نمی‌کند و خروجی نامعتبر برمی‌گرداند.
    /// </summary>
    public (string RawText, float Confidence, IranianPlate Plate) EvaluateOcrOnBuffer(byte[] cropBuffer, int w, int h, int stride)
    {
        if (cropBuffer == null || w < 16 || h < 6 || _ocrSession == null)
        {
            return (string.Empty, 0.0f, IranianPlate.CreateInvalid(string.Empty));
        }

        try
        {
            int bpp = Math.Clamp(stride / Math.Max(1, w), 3, 4);
            var inputMeta = _ocrSession.InputMetadata.First();
            var dims = inputMeta.Value.Dimensions;
            int inChannels = (dims.Length >= 4 && dims[1] > 0) ? dims[1] : 1;
            int inH = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : 40;
            int inW = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : 160;

            var tensor = new DenseTensor<float>(new[] { 1, inChannels, inH, inW });
            float scaleX = (float)w / inW;
            float scaleY = (float)h / inH;

            for (int y = 0; y < inH; y++)
            {
                int sy = Math.Min(h - 1, (int)(y * scaleY));
                int rOff = sy * stride;
                for (int x = 0; x < inW; x++)
                {
                    int sx = Math.Min(w - 1, (int)(x * scaleX));
                    int idx = rOff + (sx * bpp);
                    if (idx + 2 < cropBuffer.Length)
                    {
                        byte b = cropBuffer[idx];
                        byte g = cropBuffer[idx + 1];
                        byte r = cropBuffer[idx + 2];

                        if (inChannels == 1)
                        {
                            float gray = (0.299f * r + 0.587f * g + 0.114f * b) / 255.0f;
                            tensor[0, 0, y, x] = gray;
                        }
                        else
                        {
                            tensor[0, 0, y, x] = r / 255.0f;
                            tensor[0, 1, y, x] = g / 255.0f;
                            tensor[0, 2, y, x] = b / 255.0f;
                        }
                    }
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputMeta.Key, tensor)
            };

            using var outputs = _ocrSession.Run(inputs);
            var logits = outputs.FirstOrDefault()?.AsTensor<float>();
            if (logits == null || logits.Dimensions.Length < 3)
            {
                return (string.Empty, 0.0f, IranianPlate.CreateInvalid(string.Empty));
            }

            // پشتیبانی از شکل [1, T, C] یا [T, 1, C]
            bool batchFirst = logits.Dimensions[0] == 1;
            int timeSteps = batchFirst ? logits.Dimensions[1] : logits.Dimensions[0];
            int vocabCount = Math.Min(logits.Dimensions[2], OcrVocabulary.Length);

            var sb = new System.Text.StringBuilder();
            int prevClass = -1;
            float probSum = 0f;
            int charCount = 0;

            for (int t = 0; t < timeSteps; t++)
            {
                int bestClass = 0;
                float maxLogit = float.MinValue;

                for (int c = 0; c < vocabCount; c++)
                {
                    float val = batchFirst ? logits[0, t, c] : logits[t, 0, c];
                    if (val > maxLogit)
                    {
                        maxLogit = val;
                        bestClass = c;
                    }
                }

                // Softmax approximation for confidence of winning timestep
                double expSum = 0;
                for (int c = 0; c < vocabCount; c++)
                {
                    float val = batchFirst ? logits[0, t, c] : logits[t, 0, c];
                    expSum += Math.Exp(val - maxLogit);
                }
                float prob = (float)(1.0 / expSum);

                if (bestClass != 0 && bestClass != prevClass)
                {
                    sb.Append(OcrVocabulary[bestClass]);
                    probSum += prob;
                    charCount++;
                }
                prevClass = bestClass;
            }

            string rawText = sb.ToString().Trim();
            float avgConf = charCount > 0 ? Math.Clamp(probSum / charCount, 0.0f, 1.0f) : 0.0f;
            var plate = IranianPlateNormalizer.ParseAndNormalize(rawText, avgConf);
            return (rawText, avgConf, plate);
        }
        catch (Exception ex)
        {
            Log($"خطا در اجرای OCR: {ex.Message}");
            return (string.Empty, 0.0f, IranianPlate.CreateInvalid(string.Empty));
        }
    }

    private static byte[]? EncodeFramePreviewJpeg(ReadOnlySpan<byte> span, int w, int h, int stride)
    {
        try
        {
            if (w <= 0 || h <= 0 || span.IsEmpty) return null;

            byte[] copy = span.ToArray();
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, copy, stride);
            var enc = new JpegBitmapEncoder { QualityLevel = 72 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
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
