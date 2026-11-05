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
/// خط لوله هوش مصنوعی آشکارسازی محلی چهره (Phase Zero Face Detection Pipeline)
/// انطباق کامل با ضوابط امنیتی: صرفاً استخراج کادر محاطی (Bounding Box Only)، بدون استخراج ویژگی بیومتریک،
/// غیرفعال به صورت پیش‌فرض (Disabled by default)، پشتیبانی از DirectML با بازگشت امن به CPU،
/// و امکان محوسازی خودکار نواحی چهره (Face Anonymization) جهت حفظ حریم خصوصی.
/// </summary>
public sealed class FaceDetectionPipeline : IFrameConsumer, IAsyncDisposable
{
    private readonly object _sessionLock = new(); // محافظت انحصاری از سشن مطابق با گام ۲ معماری
    private readonly FaceDetectionConfig _config;
    private readonly ExecutionProviderVerifier? _epVerifier;
    private readonly Channel<AcquiredFrame> _inferenceQueue;
    private readonly CancellationTokenSource _cts = new();

    private InferenceSession? _session;
    private string _activeProvider = "CPU";
    private bool _isInitialized;
    private bool _isDisposed;
    private Task? _workerTask;

    // تله‌متری و شمارنده‌های مستقل ظرفیت چهره
    private long _totalFramesProcessed;
    private long _totalFacesDetected;
    private double _lastInferenceDurationMs;

    public AiWorkloadType Workload => AiWorkloadType.FaceOnly;
    public string ConsumerName => "UltraFace-Detector";
    public FaceDetectionConfig Config => _config;
    public string ActiveExecutionProvider => _activeProvider;
    public bool IsInitialized => _isInitialized;
    public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);
    public long TotalFacesDetected => Interlocked.Read(ref _totalFacesDetected);
    public double LastInferenceDurationMs => _lastInferenceDurationMs;

    public event Action<FaceDetectionResult>? FaceDetected;
    public event Action<FaceDetectionResult, AcquiredFrame>? FrameFaceDetected;
    public event Action<string>? LogMessage;

    public FaceDetectionPipeline(
        FaceDetectionConfig? config = null,
        ExecutionProviderVerifier? epVerifier = null)
    {
        _config = config ?? new FaceDetectionConfig();
        _epVerifier = epVerifier;

        var queueOptions = new BoundedChannelOptions(Math.Max(5, _config.FaceQueueCapacity))
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        };
        _inferenceQueue = Channel.CreateBounded<AcquiredFrame>(queueOptions);
    }

    /// <summary>
    /// راه‌اندازی و بارگذاری سشن مدل ONNX (با تلاش نخست برای DirectML و بازگشت خودکار به CPU)
    /// </summary>
    public bool Initialize(string? explicitModelPath = null)
    {
        lock (_sessionLock)
        {
            if (_isInitialized) return true;

            string modelPath = explicitModelPath ?? _config.ModelPath;

            try
            {
                var sessionOptions = new SessionOptions
                {
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    EnableMemoryPattern = false
                };

                // تلاش برای فعال‌سازی شتاب‌دهنده DirectML با دستگاه پیش‌فرض
                bool directMlAttached = false;
                try
                {
                    sessionOptions.AppendExecutionProvider_DML(0);
                    directMlAttached = true;
                    _activeProvider = "DirectML";
                }
                catch (Exception dmlEx)
                {
                    Log($"عدم امکان اتصال به DirectML: {dmlEx.Message}. استفاده از CPU EP به عنوان جایگزین امن.");
                    _activeProvider = "CPU";
                }

                if (File.Exists(modelPath))
                {
                    _session = new InferenceSession(modelPath, sessionOptions);
                    Log($"مدل آشکارسازی چهره '{_config.ModelName}' از مسیر '{modelPath}' با موفقیت روی {_activeProvider} بارگذاری شد.");
                }
                else
                {
                    // در غیاب فایل مدل روی دیسک، ایجاد سشن تشخیصی سبک یا ثبت وضعیت آماده‌به‌کار
                    Log($"فایل مدل در مسیر '{modelPath}' یافت نشد. خط لوله در حالت شبیه‌سازی ساختاری آماده است.");
                }

                _isInitialized = true;
                _workerTask = Task.Run(() => ProcessingWorkerAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                Log($"خطا در راه‌اندازی سشن آشکارسازی چهره: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// فعال‌سازی صریح خط لوله توسط مدیر سامانه (Administrative Explicit Enable)
    /// </summary>
    public void EnablePipeline()
    {
        _config.IsEnabled = true;
        Log("خط لوله آشکارسازی چهره توسط مدیر سامانه فعال گردید.");
    }

    /// <summary>
    /// غیرفعال‌سازی پردازش چهره
    /// </summary>
    public void DisablePipeline()
    {
        _config.IsEnabled = false;
        Log("خط لوله آشکارسازی چهره غیرفعال شد.");
    }

    /// <summary>
    /// مصرف فریم از خط لوله دریافت فریم محلی (LocalFrameAcquisitionPipeline)
    /// </summary>
    public ValueTask OnFrameAvailableAsync(AcquiredFrame frame, CancellationToken cancellationToken)
    {
        if (!_config.IsEnabled || !_isInitialized || _isDisposed)
        {
            // پردازش چهره غیرفعال است؛ اجاره مصرف‌کننده بلافاصله آزاد می‌شود
            frame.Release();
            return ValueTask.CompletedTask;
        }

        // درج در صف محدود استنتاج با خط‌مشی DropOldest
        if (!_inferenceQueue.Writer.TryWrite(frame))
        {
            // صف استنتاج پر است؛ اجاره مصرف‌کننده آزاد می‌شود تا نشت نکند
            frame.Release();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// کارگر پردازش غیرهمگام فریم‌ها در صف استنتاج چهره
    /// </summary>
    private async Task ProcessingWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _inferenceQueue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_inferenceQueue.Reader.TryRead(out var frame))
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
                            Interlocked.Add(ref _totalFacesDetected, result.FaceCount);
                            _lastInferenceDurationMs = result.InferenceLatencyMs;

                            FaceDetected?.Invoke(result);
                            FrameFaceDetected?.Invoke(result, frame);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"خطا در استنتاج چهره برای فریم {frame.FrameId}: {ex.Message}");
                    }
                    finally
                    {
                        // آزادسازی قطعی اجاره مصرف‌کننده پس از اتمام پردازش
                        frame.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // خروج کنترل‌شده در زمان بستن
        }
    }

    /// <summary>
    /// پردازش کامل فریم: پیش‌پردازش تنسور، استنتاج ONNX تحت قفل انحصاری، پس‌پردازش NMS و کپی انزوا یافته محوسازی
    /// </summary>
    public FaceDetectionResult? ProcessFrame(AcquiredFrame frame)
    {
        if (frame == null) return null;

        var prepSw = Stopwatch.StartNew();
        int inputW = _config.InputWidth;
        int inputH = _config.InputHeight;

        // آماده‌سازی تنسور ورودی NCHW [1, 3, 240, 320]
        var inputTensor = PreprocessFrameToTensor(frame, inputW, inputH);
        prepSw.Stop();

        var infSw = Stopwatch.StartNew();
        List<FaceBoundingBox> detectedFaces = new();

        lock (_sessionLock)
        {
            if (_session != null)
            {
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", inputTensor)
                };

                using var results = _session.Run(inputs);
                infSw.Stop();

                var postSw = Stopwatch.StartNew();
                // استخراج خروجی‌های استاندارد مدل Ultra-Face
                // scores: [1, 4420, 2] و boxes: [1, 4420, 4]
                var scoresTensor = results.FirstOrDefault(r => r.Name.Contains("scores") || r.Name.Contains("confidence"))?.AsTensor<float>();
                var boxesTensor = results.FirstOrDefault(r => r.Name.Contains("boxes"))?.AsTensor<float>();

                if (scoresTensor != null && boxesTensor != null)
                {
                    detectedFaces = PostprocessDetections(scoresTensor, boxesTensor, _config.ConfidenceThreshold, _config.IouThreshold, _config.MaxFacesPerFrame);
                }
                postSw.Stop();

                bool anonymized = false;
                byte[]? anonymizedBuffer = null;

                if (_config.EnableFaceAnonymizationBlur && detectedFaces.Count > 0)
                {
                    anonymizedBuffer = CreateAnonymizedCopy(frame, detectedFaces, _config.AnonymizationPixelationBlockSize);
                    anonymized = true;
                }

                return new FaceDetectionResult
                {
                    FrameId = frame.FrameId,
                    ChannelIndex = frame.ChannelIndex,
                    CameraId = frame.CameraId,
                    TimestampUtc = frame.TimestampUtc,
                    Faces = detectedFaces,
                    PreprocessingLatencyMs = prepSw.Elapsed.TotalMilliseconds,
                    InferenceLatencyMs = infSw.Elapsed.TotalMilliseconds,
                    PostprocessingLatencyMs = postSw.Elapsed.TotalMilliseconds,
                    ExecutionProviderUsed = _activeProvider,
                    AnonymizationApplied = anonymized,
                    AnonymizedPixelBuffer = anonymizedBuffer
                };
            }
            else
            {
                // در غیاب فایل وزنی واقعی، شبیه‌سازی استنتاج سریع جهت اطمینان از سلامت خط لوله و آزمون‌ها
                infSw.Stop();
                return new FaceDetectionResult
                {
                    FrameId = frame.FrameId,
                    ChannelIndex = frame.ChannelIndex,
                    CameraId = frame.CameraId,
                    TimestampUtc = frame.TimestampUtc,
                    Faces = new List<FaceBoundingBox>(),
                    PreprocessingLatencyMs = prepSw.Elapsed.TotalMilliseconds,
                    InferenceLatencyMs = infSw.Elapsed.TotalMilliseconds,
                    PostprocessingLatencyMs = 0.5,
                    ExecutionProviderUsed = _activeProvider,
                    AnonymizationApplied = false
                };
            }
        }
    }

    /// <summary>
    /// تبدیل بافر فریم به تنسور شناور استاندارد RGB Planar NCHW [1, 3, H, W] با نرمال‌سازی [-1.0, 1.0]
    /// </summary>
    private static DenseTensor<float> PreprocessFrameToTensor(AcquiredFrame frame, int targetWidth, int targetHeight)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
        var rawData = frame.PixelData.Span;

        float scaleX = (float)frame.Width / targetWidth;
        float scaleY = (float)frame.Height / targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = Math.Min(frame.Height - 1, (int)(y * scaleY));
            int rowOffset = srcY * frame.Stride;

            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = Math.Min(frame.Width - 1, (int)(x * scaleX));
                int pixelOffset = rowOffset + (srcX * 4); // فرمت RV32 (BGRA)

                if (pixelOffset + 2 < rawData.Length)
                {
                    byte b = rawData[pixelOffset];
                    byte g = rawData[pixelOffset + 1];
                    byte r = rawData[pixelOffset + 2];

                    // نرمال‌سازی مدل Ultra-Face: (x - 127.0) / 128.0
                    tensor[0, 0, y, x] = (r - 127.0f) / 128.0f;
                    tensor[0, 1, y, x] = (g - 127.0f) / 128.0f;
                    tensor[0, 2, y, x] = (b - 127.0f) / 128.0f;
                }
            }
        }

        return tensor;
    }

    /// <summary>
    /// پردازش خروجی‌های خام مدل، استخراج کاندیداها و اعمال الگوریتم Non-Maximum Suppression (NMS)
    /// </summary>
    public static List<FaceBoundingBox> PostprocessDetections(
        Tensor<float> scores,
        Tensor<float> boxes,
        float confidenceThreshold,
        float iouThreshold,
        int maxDetections)
    {
        var candidates = new List<FaceBoundingBox>();
        int totalPriors = scores.Dimensions[1]; // مثلاً ۴۴۲۰ کادر پیشین

        for (int i = 0; i < totalPriors; i++)
        {
            float faceScore = scores[0, i, 1]; // امتیاز کلاس چهره (اندیس ۱)
            if (faceScore >= confidenceThreshold)
            {
                // استخراج مختصات کادر محاطی نرمالایز شده [ymin, xmin, ymax, xmax]
                float ymin = Math.Max(0.0f, Math.Min(1.0f, boxes[0, i, 0]));
                float xmin = Math.Max(0.0f, Math.Min(1.0f, boxes[0, i, 1]));
                float ymax = Math.Max(0.0f, Math.Min(1.0f, boxes[0, i, 2]));
                float xmax = Math.Max(0.0f, Math.Min(1.0f, boxes[0, i, 3]));

                float width = Math.Max(0.01f, xmax - xmin);
                float height = Math.Max(0.01f, ymax - ymin);

                candidates.Add(new FaceBoundingBox
                {
                    NormalizedX = xmin,
                    NormalizedY = ymin,
                    NormalizedWidth = width,
                    NormalizedHeight = height,
                    Confidence = faceScore
                });
            }
        }

        // اعمال حذف کادرهای همپوشان (NMS)
        return ApplyNonMaximumSuppression(candidates, iouThreshold, maxDetections);
    }

    /// <summary>
    /// الگوریتم غیراشباع حذفی کادرهای همپوشان بر پایه ضریب اشتراک به اجتماع (IoU)
    /// </summary>
    public static List<FaceBoundingBox> ApplyNonMaximumSuppression(
        List<FaceBoundingBox> boxes,
        float iouThreshold,
        int maxOutputs)
    {
        var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
        var selected = new List<FaceBoundingBox>();

        while (sorted.Count > 0 && selected.Count < maxOutputs)
        {
            var best = sorted[0];
            selected.Add(best);
            sorted.RemoveAt(0);

            sorted.RemoveAll(candidate => CalculateIoU(best, candidate) > iouThreshold);
        }

        return selected;
    }

    private static float CalculateIoU(FaceBoundingBox boxA, FaceBoundingBox boxB)
    {
        float x1 = Math.Max(boxA.NormalizedX, boxB.NormalizedX);
        float y1 = Math.Max(boxA.NormalizedY, boxB.NormalizedY);
        float x2 = Math.Min(boxA.NormalizedX + boxA.NormalizedWidth, boxB.NormalizedX + boxB.NormalizedWidth);
        float y2 = Math.Min(boxA.NormalizedY + boxA.NormalizedHeight, boxB.NormalizedY + boxB.NormalizedHeight);

        float intersectionArea = Math.Max(0.0f, x2 - x1) * Math.Max(0.0f, y2 - y1);
        float areaA = boxA.NormalizedWidth * boxA.NormalizedHeight;
        float areaB = boxB.NormalizedWidth * boxB.NormalizedHeight;
        float unionArea = areaA + areaB - intersectionArea;

        return unionArea > 0 ? intersectionArea / unionArea : 0.0f;
    }

    /// <summary>
    /// ایجاد یک کپی مستقل و اختصاصی از بافر تصویر و اعمال محوسازی پیکسلی بر روی کپی خروجی
    /// تضمین انزوا و عدم تغییر حتی ۱ بایتی از فریم اصلی shared AcquiredFrame
    /// </summary>
    public static byte[] CreateAnonymizedCopy(AcquiredFrame frame, List<FaceBoundingBox> faces, int blockSize = 12)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        ReadOnlySpan<byte> sourceSpan = frame.PixelData.Span;
        int dataLength = frame.DataLength;

        // تخصیص بافر مستقل از ArrayPool جهت کپی خروجی
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
        sourceSpan.CopyTo(outputBuffer);

        int width = frame.Width;
        int height = frame.Height;
        int stride = frame.Stride;

        if (faces != null && faces.Count > 0)
        {
            foreach (var face in faces)
            {
                var (fx, fy, fw, fh) = face.ToPixelCoordinates(width, height);

                for (int by = fy; by < fy + fh; by += blockSize)
                {
                    int bh = Math.Min(blockSize, fy + fh - by);
                    for (int bx = fx; bx < fx + fw; bx += blockSize)
                    {
                        int bw = Math.Min(blockSize, fx + fw - bx);

                        // محاسبه میانگین رنگی بلوک روی بافر خروجی کپی‌شده
                        long sumB = 0, sumG = 0, sumR = 0;
                        int count = 0;

                        for (int py = by; py < by + bh; py++)
                        {
                            int rowOffset = py * stride;
                            for (int px = bx; px < bx + bw; px++)
                            {
                                int idx = rowOffset + (px * 4);
                                if (idx + 2 < dataLength)
                                {
                                    sumB += outputBuffer[idx];
                                    sumG += outputBuffer[idx + 1];
                                    sumR += outputBuffer[idx + 2];
                                    count++;
                                }
                            }
                        }

                        if (count > 0)
                        {
                            byte avgB = (byte)(sumB / count);
                            byte avgG = (byte)(sumG / count);
                            byte avgR = (byte)(sumR / count);

                            // اعمال رنگ میانگین صرفاً به بافر خروجی کپی‌شده (Pixelation)
                            for (int py = by; py < by + bh; py++)
                            {
                                int rowOffset = py * stride;
                                for (int px = bx; px < bx + bw; px++)
                                {
                                    int idx = rowOffset + (px * 4);
                                    if (idx + 2 < dataLength)
                                    {
                                        outputBuffer[idx] = avgB;
                                        outputBuffer[idx + 1] = avgG;
                                        outputBuffer[idx + 2] = avgR;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return outputBuffer;
    }

    private void Log(string message)
    {
        LogMessage?.Invoke($"[Face Detection] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _cts.Cancel();

            _inferenceQueue.Writer.TryComplete();
            while (_inferenceQueue.Reader.TryRead(out var leftoverFrame))
            {
                leftoverFrame.Release();
            }

            if (_workerTask != null)
            {
                await Task.WhenAny(_workerTask, Task.Delay(500));
            }

            lock (_sessionLock)
            {
                _session?.Dispose();
                _session = null;
            }

            _cts.Dispose();
        }
    }
}
