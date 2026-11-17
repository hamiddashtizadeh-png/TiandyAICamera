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

    public AiImageOptimizationEngine ImageOptimizer => _imageOptimizer;
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
        bool needPreviewJpeg = Volatile.Read(ref _previewSubscriberCount) > 0 && ShouldEncodePreview(frame.ChannelIndex);
        byte[]? previewJpeg = needPreviewJpeg ? EncodeFramePreviewJpeg(frame) : null;

        NormalizedBoundingBox? plateBox;
        NormalizedBoundingBox? vehicleBox = null;
        float detConf;
        bool plateDetected;

        lock (_inferenceLock)
        {
            (plateDetected, plateBox, detConf) = DetectPlateInFrame(frame);

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
    /// تشخیص کادر پلاک در فریم (استنتاج واقعی YOLOv8 ONNX یا کادریاب هندسی-نوری پلاک ملی ایران)
    /// </summary>
    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) DetectPlateInFrame(AcquiredFrame frame)
    {
        if (frame == null || frame.Width < 180 || frame.Height < 120) return (false, null, 0.0f);

        if (_detectorSession != null)
        {
            try
            {
                return RunOnnxPlateDetector(frame);
            }
            catch (Exception ex)
            {
                Log($"خطا در اجرای مدل ONNX دتکتور: {ex.Message}");
            }
        }

        // کادریاب نوری و هندسی پلاک ایران (فقط برای رسم کادر زنده — هرگز پلاک جعلی تولید نمی‌کند)
        try
        {
            var span = frame.PixelData.Span;
            int w = frame.Width;
            int h = frame.Height;
            int stride = frame.Stride;

            const int cols = 12;
            const int rows = 8;
            int cellW = w / cols;
            int cellH = h / rows;

            int bestScore = 0;
            int bestR = -1, bestC = -1;
            bool blueBandConfirmed = false;

            for (int r = 2; r < rows - 1; r++)
            {
                int yStart = r * cellH;
                int yEnd = Math.Min(h - 1, yStart + cellH);

                for (int c = 1; c < cols - 2; c++)
                {
                    int xStart = c * cellW;
                    int xEnd = Math.Min(w - 2, xStart + (cellW * 2));

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

                                if (x < xStart + (cellW / 3) && b > 105 && b > rCol + 30 && b > g + 16)
                                {
                                    blueBandPixels++;
                                }

                                if (lum > 165) brightPlatePixels++;
                                else if (lum < 65) darkCharPixels++;

                                if (prevLum >= 0 && Math.Abs(lum - prevLum) > 60)
                                {
                                    edgeTransitions++;
                                }
                                prevLum = lum;
                            }
                        }
                    }

                    if (brightPlatePixels > 65 && darkCharPixels > 22 && edgeTransitions >= 85 && blueBandPixels >= 4)
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

            if (bestScore >= 85 && blueBandConfirmed && bestR >= 0 && bestC >= 0)
            {
                float normW = 0.22f;
                float normH = 0.058f;
                float centerX = (bestC + 1.0f) / cols;
                float centerY = (bestR + 0.5f) / rows;

                float bx = Math.Clamp(centerX - (normW / 2f), 0.04f, 0.74f);
                float by = Math.Clamp(centerY - (normH / 2f), 0.15f, 0.88f);
                float conf = Math.Min(0.95f, 0.78f + (bestScore / 350f));

                return (true, new NormalizedBoundingBox(bx, by, normW, normH), conf);
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
    private (bool Detected, NormalizedBoundingBox? Box, float Confidence) RunOnnxPlateDetector(AcquiredFrame frame)
    {
        if (_detectorSession == null) return (false, null, 0.0f);

        var inputMeta = _detectorSession.InputMetadata.First();
        var dims = inputMeta.Value.Dimensions;
        int targetH = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : 640;
        int targetW = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : 640;

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
        var src = frame.PixelData.Span;
        float scaleX = (float)frame.Width / targetW;
        float scaleY = (float)frame.Height / targetH;

        for (int y = 0; y < targetH; y++)
        {
            int sy = Math.Min(frame.Height - 1, (int)(y * scaleY));
            int rOff = sy * frame.Stride;
            for (int x = 0; x < targetW; x++)
            {
                int sx = Math.Min(frame.Width - 1, (int)(x * scaleX));
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

    private static byte[]? EncodeFramePreviewJpeg(AcquiredFrame frame)
    {
        try
        {
            int w = frame.Width;
            int h = frame.Height;
            if (w <= 0 || h <= 0) return null;

            byte[] copy = frame.PixelData.ToArray();
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, copy, frame.Stride);
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
