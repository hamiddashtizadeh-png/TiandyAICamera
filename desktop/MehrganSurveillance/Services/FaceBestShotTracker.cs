using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance.Services;

/// <summary>
/// خروجی نهایی ترک یک شخص از لحظه ورود به تصویر تا زمان خروج:
/// شامل دقیقاً یک تصویر برگزیده (Best Shot) با بالاترین وضوح، اطمینان و کیفیت نوری
/// </summary>
public sealed class CompletedFaceTrack
{
    public Guid TrackId { get; init; }
    public string CameraId { get; init; } = string.Empty;
    public int ChannelIndex { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public DateTime BestShotTimestampUtc { get; init; }
    public int FramesObserved { get; init; }
    public double BestQualityScore { get; init; }
    public float BestConfidence { get; init; }
    public FaceBoundingBox BestBox { get; init; } = new();
    public byte[] BestCropBgra32 { get; init; } = Array.Empty<byte>();
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public int CropStride { get; init; }
}

/// <summary>
/// ردیاب هوشمند انتخاب بهترین شات چهره (Entry-to-Exit Best-Shot Face Tracker)
/// برای جلوگیری از پرشدن بیهوده دیتابیس: تا زمانی که شخص در کادر دوربین حضور دارد، فریم‌ها را در حافظه مقایسه کرده
/// و پس از خروج فرد از صحنه، تنها «بهترین و واضح‌ترین» تصویر آن شخص را یک‌بار ذخیره و ارسال می‌کند.
/// </summary>
public sealed class FaceBestShotTracker : IDisposable
{
    private sealed class ActiveTrack
    {
        public Guid TrackId { get; } = Guid.NewGuid();
        public string CameraId { get; init; } = string.Empty;
        public int ChannelIndex { get; set; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public FaceBoundingBox LastBox { get; set; } = new();
        public int FramesObserved { get; set; }

        public double BestQualityScore { get; set; } = double.MinValue;
        public float BestConfidence { get; set; }
        public FaceBoundingBox BestBox { get; set; } = new();
        public DateTime BestTimestampUtc { get; set; }
        public byte[] BestCropBgra32 { get; set; } = Array.Empty<byte>();
        public int CropWidth { get; set; }
        public int CropHeight { get; set; }
        public int CropStride { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, List<ActiveTrack>> _tracksByCamera = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _sweepTimer;
    private bool _disposed;

    public double ExitTimeoutSeconds { get; set; } = 2.0;

    public event Action<CompletedFaceTrack>? TrackCompleted;

    public FaceBestShotTracker(double exitTimeoutSeconds = 2.0)
    {
        ExitTimeoutSeconds = Math.Max(0.5, exitTimeoutSeconds);
        _sweepTimer = new Timer(_ => SweepExpiredTracks(), null, 500, 500);
    }

    public int ActiveTrackCount
    {
        get
        {
            lock (_sync)
            {
                return _tracksByCamera.Values.Sum(list => list.Count);
            }
        }
    }

    /// <summary>
    /// دریافت فریم جدید از پایپ‌لاین چهره و ارزیابی کیفیت کادرهای چهره درون آن
    /// </summary>
    public void ObserveFrame(FaceDetectionResult result, AcquiredFrame frame)
    {
        if (_disposed || result == null || frame == null || frame.IsDisposed) return;
        if (result.Faces == null || result.Faces.Count == 0) return;

        DateTime now = result.TimestampUtc == default ? DateTime.UtcNow : result.TimestampUtc;
        if (now > DateTime.UtcNow.AddSeconds(5)) now = DateTime.UtcNow;

        lock (_sync)
        {
            if (!_tracksByCamera.TryGetValue(result.CameraId, out var camTracks))
            {
                camTracks = new List<ActiveTrack>();
                _tracksByCamera[result.CameraId] = camTracks;
            }

            var matchedTracks = new HashSet<ActiveTrack>();

            foreach (var face in result.Faces)
            {
                ActiveTrack? matched = FindBestMatchingTrack(camTracks, matchedTracks, face);

                if (matched == null)
                {
                    matched = new ActiveTrack
                    {
                        CameraId = result.CameraId,
                        ChannelIndex = result.ChannelIndex,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                        LastBox = face,
                        FramesObserved = 1
                    };
                    camTracks.Add(matched);
                }
                else
                {
                    matched.LastSeenUtc = now;
                    matched.LastBox = face;
                    matched.ChannelIndex = result.ChannelIndex;
                    matched.FramesObserved++;
                }

                matchedTracks.Add(matched);

                // استخراج و امتیازدهی به شات پرتره برای این فریم
                var (crop, cw, ch, cStride, score) = EvaluateFaceShot(frame, face);
                if (crop.Length > 0 && score > matched.BestQualityScore)
                {
                    matched.BestQualityScore = score;
                    matched.BestConfidence = face.Confidence;
                    matched.BestBox = face;
                    matched.BestTimestampUtc = now;
                    matched.BestCropBgra32 = crop;
                    matched.CropWidth = cw;
                    matched.CropHeight = ch;
                    matched.CropStride = cStride;
                }
            }
        }
    }

    /// <summary>
    /// تسویه فوری ترک‌های فعال یک دوربین (مثلاً هنگام قطع پخش زنده)
    /// </summary>
    public void FlushCamera(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return;

        List<CompletedFaceTrack>? toEmit = null;
        lock (_sync)
        {
            if (_tracksByCamera.TryGetValue(cameraId, out var list) && list.Count > 0)
            {
                toEmit = list.Select(ToCompleted).Where(c => c.BestCropBgra32.Length > 0).ToList();
                list.Clear();
            }
        }

        if (toEmit != null)
        {
            foreach (var item in toEmit)
            {
                TrackCompleted?.Invoke(item);
            }
        }
    }

    /// <summary>
    /// تسویه فوری کلیه ترک‌های فعال (هنگام بستن برنامه)
    /// </summary>
    public void FlushAll()
    {
        List<CompletedFaceTrack> toEmit = new();
        lock (_sync)
        {
            foreach (var kvp in _tracksByCamera)
            {
                foreach (var tr in kvp.Value)
                {
                    var comp = ToCompleted(tr);
                    if (comp.BestCropBgra32.Length > 0)
                    {
                        toEmit.Add(comp);
                    }
                }
                kvp.Value.Clear();
            }
        }

        foreach (var item in toEmit)
        {
            TrackCompleted?.Invoke(item);
        }
    }

    private void SweepExpiredTracks()
    {
        if (_disposed) return;

        DateTime now = DateTime.UtcNow;
        List<CompletedFaceTrack>? completed = null;

        lock (_sync)
        {
            foreach (var kvp in _tracksByCamera)
            {
                var list = kvp.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if ((now - list[i].LastSeenUtc).TotalSeconds >= ExitTimeoutSeconds)
                    {
                        var finished = ToCompleted(list[i]);
                        list.RemoveAt(i);
                        if (finished.BestCropBgra32.Length > 0)
                        {
                            completed ??= new List<CompletedFaceTrack>();
                            completed.Add(finished);
                        }
                    }
                }
            }
        }

        if (completed != null)
        {
            foreach (var item in completed)
            {
                try
                {
                    TrackCompleted?.Invoke(item);
                }
                catch
                {
                }
            }
        }
    }

    private static ActiveTrack? FindBestMatchingTrack(
        List<ActiveTrack> tracks,
        HashSet<ActiveTrack> alreadyMatched,
        FaceBoundingBox face)
    {
        float cx = face.NormalizedX + (face.NormalizedWidth * 0.5f);
        float cy = face.NormalizedY + (face.NormalizedHeight * 0.5f);

        ActiveTrack? bestTrack = null;
        float bestScore = float.MinValue;

        foreach (var tr in tracks)
        {
            if (alreadyMatched.Contains(tr)) continue;

            float iou = CalculateIoU(tr.LastBox, face);
            float tcx = tr.LastBox.NormalizedX + (tr.LastBox.NormalizedWidth * 0.5f);
            float tcy = tr.LastBox.NormalizedY + (tr.LastBox.NormalizedHeight * 0.5f);
            float dist = (float)Math.Sqrt((cx - tcx) * (cx - tcx) + (cy - tcy) * (cy - tcy));

            if (iou >= 0.12f || dist <= 0.24f)
            {
                float matchScore = iou * 2.0f - dist;
                if (matchScore > bestScore)
                {
                    bestScore = matchScore;
                    bestTrack = tr;
                }
            }
        }

        return bestTrack;
    }

    private static float CalculateIoU(FaceBoundingBox a, FaceBoundingBox b)
    {
        float x1 = Math.Max(a.NormalizedX, b.NormalizedX);
        float y1 = Math.Max(a.NormalizedY, b.NormalizedY);
        float x2 = Math.Min(a.NormalizedX + a.NormalizedWidth, b.NormalizedX + b.NormalizedWidth);
        float y2 = Math.Min(a.NormalizedY + a.NormalizedHeight, b.NormalizedY + b.NormalizedHeight);

        float inter = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
        float areaA = a.NormalizedWidth * a.NormalizedHeight;
        float areaB = b.NormalizedWidth * b.NormalizedHeight;
        float union = areaA + areaB - inter;
        return union > 0f ? inter / union : 0f;
    }

    private static (byte[] Crop, int Width, int Height, int Stride, double Score) EvaluateFaceShot(AcquiredFrame frame, FaceBoundingBox face)
    {
        try
        {
            var (rawX, rawY, rawW, rawH) = face.ToPixelCoordinates(frame.Width, frame.Height);
            if (rawW <= 6 || rawH <= 6)
            {
                return (Array.Empty<byte>(), 0, 0, 0, double.MinValue);
            }

            int padX = Math.Max((int)(rawW * 0.40), 20);
            int padTop = Math.Max((int)(rawH * 0.35), 18);
            int padBottom = Math.Max((int)(rawH * 0.52), 28);

            int cx = Math.Max(0, rawX - padX);
            int cy = Math.Max(0, rawY - padTop);
            int cw = Math.Min(frame.Width - cx, rawW + (padX * 2));
            int ch = Math.Min(frame.Height - cy, rawH + padTop + padBottom);

            if (cw <= 8 || ch <= 8)
            {
                return (Array.Empty<byte>(), 0, 0, 0, double.MinValue);
            }

            int stride = cw * 4;
            byte[] crop = new byte[stride * ch];
            ReadOnlySpan<byte> src = frame.PixelData.Span;

            for (int row = 0; row < ch; row++)
            {
                int srcOff = ((cy + row) * frame.Stride) + (cx * 4);
                int dstOff = row * stride;
                if (srcOff >= 0 && srcOff + stride <= src.Length && dstOff + stride <= crop.Length)
                {
                    src.Slice(srcOff, stride).CopyTo(crop.AsSpan(dstOff, stride));
                }
            }

            var metrics = ImageProcessingFilterSet.CalculateQualityMetrics(crop.AsSpan(), cw, ch, stride, 4);
            double normArea = face.NormalizedWidth * face.NormalizedHeight;

            // ترکیب اطمینان مدل، وضوح لاپلاسیان تصویر، اندازه چهره و جریمه تاری/نور شدید
            double score =
                (face.Confidence * 100.0) +
                (Math.Min(metrics.LaplacianSharpness, 120f) * 0.45) +
                Math.Min(normArea * 350.0, 30.0) -
                (metrics.OverexposedPercentage * 0.7) -
                (metrics.MotionBlurScore * 0.25);

            return (crop, cw, ch, stride, score);
        }
        catch
        {
            return (Array.Empty<byte>(), 0, 0, 0, double.MinValue);
        }
    }

    private static CompletedFaceTrack ToCompleted(ActiveTrack tr) => new()
    {
        TrackId = tr.TrackId,
        CameraId = tr.CameraId,
        ChannelIndex = tr.ChannelIndex,
        FirstSeenUtc = tr.FirstSeenUtc,
        LastSeenUtc = tr.LastSeenUtc,
        BestShotTimestampUtc = tr.BestTimestampUtc,
        FramesObserved = tr.FramesObserved,
        BestQualityScore = tr.BestQualityScore,
        BestConfidence = tr.BestConfidence,
        BestBox = tr.BestBox,
        BestCropBgra32 = tr.BestCropBgra32,
        CropWidth = tr.CropWidth,
        CropHeight = tr.CropHeight,
        CropStride = tr.CropStride
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweepTimer.Dispose();
    }
}
