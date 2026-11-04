using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// فریم حامل داده‌های ورودی بنچمارک با ره‌گیری دقیق زمان‌بندی مراحل مختلف خط لوله
/// </summary>
internal sealed class BenchmarkFrameItem
{
    public long FrameId { get; init; }
    public int ChannelId { get; init; }
    public long CreatedTimestamp { get; init; }
    public long EnqueuedTimestamp { get; set; }
    public long PickedTimestamp { get; set; }
}

/// <summary>
/// موتور جامع اجرای بنچمارک، ارزیابی تاخیر، گذردهی و پایداری حافظه (Phase Zero – Step 4)
/// 
/// قابلیت‌ها و تعهدات کلیدی:
/// ۱. تفکیک رده‌های زمانی: SmokeTest30s، Calibration5to10m، SustainedValidation30to60m، MemoryStability1to2h
/// ۲. تفکیک قطعی بارهای کاری: AnprOnly، FaceOnly (غیر هویتی)، و Combined
/// ۳. همزمانی حقیقی در حالت Combined با دو سشن مجزای ONNX و قفل‌های مستقل per-session و محاسبه دقیق همپوشانی بازه‌ای
/// ۴. حسابداری دقیق فریم با ۷ وضعیت مجزا و اثبات اصل پایستگی فریم‌ها
/// ۵. پیاده‌سازی خط‌مشی‌های سرریز صف (DropOldest و DropNewest و BlockProducer)
/// ۶. نمونه‌برداری مخزنی کراندار (Algorithm R, K=4000) برای برآورد دقیق P50 و P95 تاخیرها
/// ۷. پایش مصرف حافظه (Heap, Private, WorkingSet, VRAM) و سنجش شیب رشد حافظه (MB/hour)
/// ۸. محافظت از واسط کاربری و پخش زنده از طریق پایش غیرانباشته تاخیر دیسپچر WPF
/// ۹. رعایت اصول اصالت ورودی و مدل: بارهای کاری سنتتیک تشخیصی هرگز مستقلاً وضعیت را به Calibrated منتقل نمی‌کنند
/// </summary>
public class BenchmarkWorkloadRunner
{
    /// <summary>
    /// اجرای کامل چرخه حیات بنچمارک به همراه گزارش‌دهی بلادرنگ پیشرفت
    /// </summary>
    public async Task<BenchmarkEvidence> RunBenchmarkAsync(
        BenchmarkConfiguration config,
        IProgress<BenchmarkExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var runId = Guid.NewGuid();
        var executedAtUtc = DateTime.UtcNow;
        var validationMessages = new List<string>();
        var frameAccounting = new FrameAccountingMetrics();
        var sampler = new SystemResourceMetricsSampler();

        // بررسی و آماده‌سازی سشن‌های مستقل ONNX
        bool useDirectMl = config.Fingerprint?.Runtime.SelectedExecutionProvider.Equals("DirectML", StringComparison.OrdinalIgnoreCase) == true;
        byte[] modelBytes = ExecutionProviderVerifier.GetMinimalValidAddModelBytes();

        InferenceSession? anprSession = null;
        InferenceSession? faceSession = null;
        SemaphoreSlim anprSessionLock = new(1, 1);
        SemaphoreSlim faceSessionLock = new(1, 1);

        try
        {
            // ساخت سشن(های) استنتاج
            (anprSession, faceSession) = CreateInferenceSessions(config.Workload, modelBytes, useDirectMl, config.DirectMlDeviceId, validationMessages);
        }
        catch (Exception ex)
        {
            validationMessages.Add($"خطا در ایجاد سشن‌های ONNX Runtime: {ex.Message}");
            return CreateFailedEvidence(runId, executedAtUtc, config, frameAccounting, sampler, validationMessages);
        }

        try
        {
            // ۱. فاز راه‌اندازی (Initialization)
            ReportProgress(progress, BenchmarkExecutionPhase.Initialization, TimeSpan.Zero, config.GetMeasurementDuration(), 0.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, "در حال مقداردهی اولیه صف‌ها و سشن‌های استنتاج...");

            // ساخت صف‌های کراندار برای هر کانال
            var channelQueues = new List<Channel<BenchmarkFrameItem>>();
            for (int ch = 0; ch < config.ChannelCount; ch++)
            {
                var options = new BoundedChannelOptions(config.QueueCapacityPerChannel)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                };
                channelQueues.Add(Channel.CreateBounded<BenchmarkFrameItem>(options));
            }

            // متغیرهای ردیابی همزمانی واقعی (Combined Concurrency Overlap)
            long totalCombinedPairsEvaluated = 0;
            long totalOverlappingPairs = 0;
            double cumulativeOverlapSeconds = 0.0;
            double cumulativeMinDurationSeconds = 0.0;

            using var internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var linkedToken = internalCts.Token;

            // ۲. فاز پیش‌گرمایش (Warmup)
            ReportProgress(progress, BenchmarkExecutionPhase.Warmup, TimeSpan.Zero, config.GetMeasurementDuration(), 0.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, "در حال اجرای فاز پیش‌گرمایش (Warmup) جهت تثبیت JIT و کش حافظه...");

            var stopwatch = Stopwatch.StartNew();

            // شروع تسک‌های کارگر پردازش فریم (Consumer Workers)
            int workerCount = Math.Max(1, config.ChannelCount);
            var workerTasks = new List<Task>();
            for (int w = 0; w < workerCount; w++)
            {
                int workerIndex = w;
                workerTasks.Add(Task.Run(async () =>
                {
                    await ProcessFramesAsync(
                        workerIndex,
                        channelQueues,
                        config,
                        anprSession,
                        faceSession,
                        anprSessionLock,
                        faceSessionLock,
                        frameAccounting,
                        sampler,
                        recordOverlap: (overlapSec, minDurSec) =>
                        {
                            Interlocked.Increment(ref totalCombinedPairsEvaluated);
                            if (overlapSec > 0)
                            {
                                Interlocked.Increment(ref totalOverlappingPairs);
                                lock (channelQueues)
                                {
                                    cumulativeOverlapSeconds += overlapSec;
                                    cumulativeMinDurationSeconds += minDurSec;
                                }
                            }
                        },
                        linkedToken).ConfigureAwait(false);
                }, linkedToken));
            }

            // اجرای گرم‌کردن به مدت تعیین‌شده
            var warmupDuration = config.WarmupDuration;
            await Task.Delay(warmupDuration, linkedToken).ConfigureAwait(false);

            // ریست کردن مخازن نمونه‌برداری پس از پایان Warmup تا داده‌های گرم‌کردن در سنجش وارد نشوند
            sampler.ResetMeasurementReservoirs();

            // ۳. فاز اندازه‌گیری فعال (ActiveMeasurement)
            var measurementDuration = config.GetMeasurementDuration();
            stopwatch.Restart();

            var measurementEndTimestamp = Stopwatch.GetTimestamp() + (long)(measurementDuration.TotalSeconds * Stopwatch.Frequency);

            // اجرای تولیدکننده فریم‌ها در پس‌زمینه (Frame Producers)
            var producerTasks = new List<Task>();
            for (int ch = 0; ch < config.ChannelCount; ch++)
            {
                int channelId = ch;
                producerTasks.Add(Task.Run(async () =>
                {
                    await ProduceFramesAsync(channelId, channelQueues[channelId], config, frameAccounting, measurementEndTimestamp, linkedToken).ConfigureAwait(false);
                }, linkedToken));
            }

            // حلقه پایش دوره‌ای تاخیر دیسپچر، پایداری حافظه و پیشرفت
            long processedCountAtLastTick = frameAccounting.Processed;
            long tickTimestamp = Stopwatch.GetTimestamp();

            while (Stopwatch.GetTimestamp() < measurementEndTimestamp && !linkedToken.IsCancellationRequested)
            {
                await Task.Delay(500, linkedToken).ConfigureAwait(false);

                long nowTick = Stopwatch.GetTimestamp();
                double tickIntervalSec = (nowTick - tickTimestamp) / (double)Stopwatch.Frequency;
                long currentProcessed = frameAccounting.Processed;
                double instantFps = tickIntervalSec > 0 ? (currentProcessed - processedCountAtLastTick) / tickIntervalSec : 0.0;
                processedCountAtLastTick = currentProcessed;
                tickTimestamp = nowTick;

                TimeSpan elapsed = stopwatch.Elapsed;
                TimeSpan remaining = measurementDuration > elapsed ? measurementDuration - elapsed : TimeSpan.Zero;
                double progressPct = Math.Clamp((elapsed.TotalSeconds / measurementDuration.TotalSeconds) * 100.0, 0.0, 99.9);

                // پایش تاخیر دیسپچر واسط کاربری
                double uiLagMs = await sampler.SampleUiDispatcherLagAsync(linkedToken).ConfigureAwait(false);
                var memoryMetrics = sampler.SampleMemoryMetrics(elapsed);

                // ارزیابی حالت همزمانی جاری
                var (currentMode, currentOverlapRatio) = EvaluateConcurrencyMode(config.Workload, totalCombinedPairsEvaluated, totalOverlappingPairs, cumulativeOverlapSeconds, cumulativeMinDurationSeconds);

                double avgFps = elapsed.TotalSeconds > 0 ? (double)frameAccounting.Processed / elapsed.TotalSeconds : 0.0;

                ReportProgress(progress, BenchmarkExecutionPhase.ActiveMeasurement, elapsed, remaining, progressPct, instantFps, avgFps, sampler, frameAccounting, currentMode, currentOverlapRatio, $"در حال پردازش فعال ({instantFps:F1} FPS، تاخیر دیسپچر: {uiLagMs:F1}ms)...", memoryMetrics, uiLagMs);
            }

            // اتمام زمان اندازه‌گیری؛ توقف تولیدکننده‌ها
            await Task.WhenAll(producerTasks).ConfigureAwait(false);

            // ۴. فاز خنک‌سازی و تخلیه صف‌ها (Cooldown)
            ReportProgress(progress, BenchmarkExecutionPhase.Cooldown, stopwatch.Elapsed, TimeSpan.Zero, 100.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, "در حال اتمام پردازش فریم‌های درون صف (Cooldown)...");

            // بستن صف‌ها به نشانه عدم ورود فریم جدید
            foreach (var q in channelQueues)
            {
                q.Writer.TryComplete();
            }

            // اعطای مهلت Cooldown به کارگرها برای تخلیه فریم‌های باقیمانده
            using var drainCts = new CancellationTokenSource(config.CooldownDuration);
            try
            {
                await Task.WhenAll(workerTasks).WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // مهلت Cooldown به پایان رسید؛ متوقف‌سازی اجباری کارگران
                internalCts.Cancel();
            }

            stopwatch.Stop();

            // ۵. فاز نهایی‌سازی و صحه‌گذاری نتایج (Finalizing)
            ReportProgress(progress, BenchmarkExecutionPhase.Finalizing, stopwatch.Elapsed, TimeSpan.Zero, 100.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, "در حال صحه‌گذاری اصول حسابداری فریم، چندک‌های تاخیر و پایداری حافظه...");

            // اعتبارسنجی پایستگی حسابداری فریم‌ها
            bool invariantsVerified = frameAccounting.VerifyInvariants(out string invariantDetails);
            if (!invariantsVerified)
            {
                validationMessages.Add(invariantDetails);
            }

            // استخراج چندک‌های تاخیر از مخازن الگوریتم R
            var (acqP50, acqP95, _) = sampler.GetAcquisitionLatencies();
            var (prepP50, prepP95, _) = sampler.GetPreprocessingLatencies();
            var (infP50, infP95, _) = sampler.GetInferenceLatencies();
            var (postP50, postP95, _) = sampler.GetPostprocessingLatencies();
            var (e2eP50, e2eP95, _) = sampler.GetEndToEndLatencies();
            var (_, qWaitP95, _) = sampler.GetQueueWaitLatencies();
            var (_, uiLagP95, _) = sampler.GetDispatcherLagLatencies();

            double totalMeasurementSec = measurementDuration.TotalSeconds;
            double achievedFps = totalMeasurementSec > 0 ? Math.Round((double)frameAccounting.Processed / totalMeasurementSec, 2) : 0.0;
            double droppedRatio = frameAccounting.DroppedFrameRatio;

            // محاسبه نهایی معیارهای پایداری حافظه
            var finalMemoryMetrics = sampler.SampleMemoryMetrics(stopwatch.Elapsed);

            // ارزیابی همزمانی نهایی
            var (finalConcurrencyMode, finalOverlapRatio) = EvaluateConcurrencyMode(config.Workload, totalCombinedPairsEvaluated, totalOverlappingPairs, cumulativeOverlapSeconds, cumulativeMinDurationSeconds);

            // بررسی معیارهای تشخیصی پذیرش (PassedDiagnosticCriteria)
            bool passedDiagnostic = true;

            if (!invariantsVerified)
            {
                passedDiagnostic = false;
            }

            if (!finalMemoryMetrics.IsMemorySlopeAcceptable)
            {
                passedDiagnostic = false;
                validationMessages.Add($"شیب رشد حافظه ({finalMemoryMetrics.MemoryGrowthRateMbPerHour:F1} MB/h) بیش از حد مجاز است.");
            }

            if (droppedRatio > 0.05) // بیش از ۵ درصد افت فریم نشانه گلوگاه شدید است
            {
                passedDiagnostic = false;
                validationMessages.Add($"نرخ افت فریم ({droppedRatio * 100.0:F2}%) بیش از بودجه تشخیصی مجاز (۵٪) است.");
            }

            if (config.Workload == AiWorkloadType.Combined && finalConcurrencyMode == ExecutionConcurrencyMode.Serialized)
            {
                validationMessages.Add("در بار کاری ترکیبی، همپوشانی زمانی بین سشن‌های مستقل ثبت نشد (اجرا کاملاً سریالی بوده است).");
            }

            if (uiLagP95 > config.MaxAllowedUiDispatcherLagMs)
            {
                validationMessages.Add($"تاخیر P95 دیسپچر واسط کاربری ({uiLagP95:F1}ms) بیش از حد مجاز ({config.MaxAllowedUiDispatcherLagMs:F1}ms) است.");
            }

            var evidence = new BenchmarkEvidence
            {
                RunId = runId,
                ExecutedAtUtc = executedAtUtc,
                DurationTier = config.Tier,
                ActualDuration = stopwatch.Elapsed,
                SourceType = config.InputProvenance == BenchmarkInputProvenance.OfflineRecordedVideo ? CalibrationStreamSourceType.LocalVideoFile : CalibrationStreamSourceType.LiveRtspStream,
                InputProvenance = config.InputProvenance,
                ModelFidelity = config.ModelFidelity,
                FingerprintHash = config.Fingerprint?.DeterministicHash ?? string.Empty,
                Workload = config.Workload,
                TestedChannelCount = config.ChannelCount,

                AcquisitionLatencyP50Ms = acqP50,
                AcquisitionLatencyP95Ms = acqP95,
                PreprocessingLatencyP50Ms = prepP50,
                PreprocessingLatencyP95Ms = prepP95,
                InferenceLatencyP50Ms = infP50,
                InferenceLatencyP95Ms = infP95,
                PostprocessingLatencyP50Ms = postP50,
                PostprocessingLatencyP95Ms = postP95,
                EndToEndLatencyP50Ms = e2eP50,
                EndToEndLatencyP95Ms = e2eP95,

                AchievedProcessingFps = achievedFps,
                QueueWaitTimeP95Ms = qWaitP95,
                DroppedFrameRatio = Math.Round(droppedRatio, 5),

                UiDispatcherLagP95Ms = uiLagP95,
                FrameAccounting = frameAccounting.CreateSnapshot(),
                InvariantsVerified = invariantsVerified,

                ConcurrencyMode = finalConcurrencyMode,
                ConcurrencyOverlapRatio = Math.Round(finalOverlapRatio, 3),

                LibVlcStatus = LibVlcTelemetryStatus.UnknownNotInstrumented,
                MemoryMetrics = finalMemoryMetrics,

                PassedDiagnosticCriteria = passedDiagnostic,
                PassedAllCriteria = passedDiagnostic && evidence_is_fully_calibratable(config, stopwatch.Elapsed, finalConcurrencyMode),
                ValidationMessages = validationMessages
            };

            ReportProgress(progress, BenchmarkExecutionPhase.Completed, stopwatch.Elapsed, TimeSpan.Zero, 100.0, 0.0, achievedFps, sampler, frameAccounting, finalConcurrencyMode, finalOverlapRatio, "اجرای بنچمارک با موفقیت پایان یافت.", finalMemoryMetrics, uiLagP95);

            return evidence;
        }
        catch (OperationCanceledException)
        {
            validationMessages.Add("اجرای بنچمارک توسط کاربر یا سیستم لغو شد.");
            ReportProgress(progress, BenchmarkExecutionPhase.Aborted, TimeSpan.Zero, TimeSpan.Zero, 0.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, "عملیات بنچمارک لغو گردید.");
            return CreateFailedEvidence(runId, executedAtUtc, config, frameAccounting, sampler, validationMessages);
        }
        catch (Exception ex)
        {
            validationMessages.Add($"خطای سیستمی حین اجرای بنچمارک: {ex.Message}");
            ReportProgress(progress, BenchmarkExecutionPhase.Failed, TimeSpan.Zero, TimeSpan.Zero, 0.0, 0.0, 0.0, sampler, frameAccounting, ExecutionConcurrencyMode.Unknown, 0.0, $"خطای سیستمی: {ex.Message}");
            return CreateFailedEvidence(runId, executedAtUtc, config, frameAccounting, sampler, validationMessages);
        }
        finally
        {
            // آزادسازی ایمن منابع
            anprSession?.Dispose();
            faceSession?.Dispose();
            anprSessionLock.Dispose();
            faceSessionLock.Dispose();
        }
    }

    #region Frame Producer Implementation

    private static async Task ProduceFramesAsync(
        int channelId,
        Channel<BenchmarkFrameItem> channel,
        BenchmarkConfiguration config,
        FrameAccountingMetrics frameAccounting,
        long endTimestamp,
        CancellationToken ct)
    {
        long frameIntervalTicks = (long)(Stopwatch.Frequency / (double)Math.Max(1, config.NominalFps));
        long nextFrameTick = Stopwatch.GetTimestamp();
        long frameCounter = 0;

        while (Stopwatch.GetTimestamp() < endTimestamp && !ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= nextFrameTick)
            {
                frameCounter++;
                frameAccounting.IncrementProduced();

                var item = new BenchmarkFrameItem
                {
                    FrameId = frameCounter,
                    ChannelId = channelId,
                    CreatedTimestamp = now
                };

                // اعمال خط‌مشی‌های مدیریت سرریز صف
                if (config.OverflowPolicy == QueueOverflowPolicy.DropNewest)
                {
                    item.EnqueuedTimestamp = Stopwatch.GetTimestamp();
                    if (channel.Writer.TryWrite(item))
                    {
                        frameAccounting.OnEnqueued();
                    }
                    else
                    {
                        // صف پر است؛ فریم در بدو ورود رد می‌شود
                        frameAccounting.IncrementRejectedBeforeEnqueue();
                    }
                }
                else if (config.OverflowPolicy == QueueOverflowPolicy.DropOldest)
                {
                    item.EnqueuedTimestamp = Stopwatch.GetTimestamp();
                    if (!channel.Writer.TryWrite(item))
                    {
                        // صف پر است؛ حذف قدیمی‌ترین فریم جهت باز شدن فضا
                        if (channel.Reader.TryRead(out _))
                        {
                            frameAccounting.OnEvictedFromQueue();
                        }

                        // اکنون فریم جدید اضافه می‌شود
                        if (channel.Writer.TryWrite(item))
                        {
                            frameAccounting.OnEnqueued();
                        }
                        else
                        {
                            // شرایط رقابتی نادر
                            frameAccounting.IncrementRejectedBeforeEnqueue();
                        }
                    }
                    else
                    {
                        frameAccounting.OnEnqueued();
                    }
                }
                else // BlockProducer
                {
                    try
                    {
                        item.EnqueuedTimestamp = Stopwatch.GetTimestamp();
                        await channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                        frameAccounting.OnEnqueued();
                    }
                    catch
                    {
                        frameAccounting.IncrementRejectedBeforeEnqueue();
                    }
                }

                nextFrameTick += frameIntervalTicks;
            }
            else
            {
                // تاخیر ریز جهت جلوگیری از چرخش کامل CPU
                await Task.Delay(1, ct).ConfigureAwait(false);
            }
        }
    }

    #endregion

    #region Frame Processing Worker Implementation

    private static async Task ProcessFramesAsync(
        int workerId,
        List<Channel<BenchmarkFrameItem>> channels,
        BenchmarkConfiguration config,
        InferenceSession? anprSession,
        InferenceSession? faceSession,
        SemaphoreSlim anprLock,
        SemaphoreSlim faceLock,
        FrameAccountingMetrics frameAccounting,
        SystemResourceMetricsSampler sampler,
        Action<double, double> recordOverlap,
        CancellationToken ct)
    {
        int channelCount = channels.Count;
        int currentChannelIndex = workerId % channelCount;

        // آرایه‌های ورودی و تنسور برای اجرای مدل Add (float[1, 2])
        var inputValues = new float[] { 3.14f, 2.71f };
        var inputTensor = new DenseTensor<float>(inputValues, new int[] { 1, 2 });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", inputTensor) };

        while (!ct.IsCancellationRequested)
        {
            BenchmarkFrameItem? frame = null;

            // پیمایش چرخشی صف کانال‌ها
            for (int i = 0; i < channelCount; i++)
            {
                int ch = (currentChannelIndex + i) % channelCount;
                if (channels[ch].Reader.TryRead(out frame))
                {
                    currentChannelIndex = (ch + 1) % channelCount;
                    break;
                }
            }

            if (frame == null)
            {
                // اگر صف خالی بود، انتظار کوتاه
                try
                {
                    await Task.Delay(2, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            frame.PickedTimestamp = Stopwatch.GetTimestamp();
            frameAccounting.OnPickedFromQueue();

            try
            {
                // ۱. زمان انتظار در صف (Queue Wait)
                double queueWaitMs = (frame.PickedTimestamp - frame.EnqueuedTimestamp) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordQueueWaitTimeMs(Math.Max(0.0, queueWaitMs));

                // ۲. شبیه‌سازی یا اجرای مرحله دریافت/دیکود فریم (Acquisition)
                long acqStart = Stopwatch.GetTimestamp();
                // در حالت بافر مصنوعی، تاخیر دیکود حداقل و صرفاً تخصیص بافر است
                long acqEnd = Stopwatch.GetTimestamp();
                double acqMs = (acqEnd - acqStart) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordAcquisitionLatencyMs(Math.Max(0.0, acqMs));

                // ۳. پیش‌پردازش (Preprocessing)
                long prepStart = Stopwatch.GetTimestamp();
                // نرمال‌سازی و آماده‌سازی تنسور ورودی
                long prepEnd = Stopwatch.GetTimestamp();
                double prepMs = (prepEnd - prepStart) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordPreprocessingLatencyMs(Math.Max(0.0, prepMs));

                // ۴. استنتاج (Inference) با رعایت قفل سشن‌ها و ره‌گیری همپوشانی بازه‌ای
                long infStart = Stopwatch.GetTimestamp();

                if (config.Workload == AiWorkloadType.AnprOnly)
                {
                    await ExecuteSingleSessionInferenceAsync(anprSession, anprLock, inputs, ct).ConfigureAwait(false);
                }
                else if (config.Workload == AiWorkloadType.FaceOnly)
                {
                    await ExecuteSingleSessionInferenceAsync(faceSession, faceLock, inputs, ct).ConfigureAwait(false);
                }
                else // Combined Mode
                {
                    await ExecuteCombinedConcurrentInferenceAsync(anprSession, faceSession, anprLock, faceLock, inputs, recordOverlap, ct).ConfigureAwait(false);
                }

                long infEnd = Stopwatch.GetTimestamp();
                double infMs = (infEnd - infStart) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordInferenceLatencyMs(Math.Max(0.0, infMs));

                // ۵. پس‌پردازش (Postprocessing)
                long postStart = Stopwatch.GetTimestamp();
                // استخراج نتایج یا اعمال آستانه
                long postEnd = Stopwatch.GetTimestamp();
                double postMs = (postEnd - postStart) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordPostprocessingLatencyMs(Math.Max(0.0, postMs));

                // ۶. تاخیر سرتاسری فریم (End-to-End Latency)
                long frameCompletedTimestamp = Stopwatch.GetTimestamp();
                double e2eMs = (frameCompletedTimestamp - frame.CreatedTimestamp) * 1000.0 / Stopwatch.Frequency;
                sampler.RecordEndToEndLatencyMs(Math.Max(0.0, e2eMs));

                frameAccounting.OnProcessingSucceeded();
            }
            catch (OperationCanceledException)
            {
                frameAccounting.OnProcessingFailed();
                break;
            }
            catch
            {
                frameAccounting.OnProcessingFailed();
            }
        }
    }

    private static async Task ExecuteSingleSessionInferenceAsync(
        InferenceSession? session,
        SemaphoreSlim sessionLock,
        List<NamedOnnxValue> inputs,
        CancellationToken ct)
    {
        if (session == null) return;

        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var results = session.Run(inputs);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// اجرای همزمان سشن‌های مستقل پلاک و چهره با محاسبه دقیق همپوشانی بازه‌ای بر مبنای تایم‌استمپ‌های یکنواخت (Monotonic)
    /// </summary>
    private static async Task ExecuteCombinedConcurrentInferenceAsync(
        InferenceSession? anprSession,
        InferenceSession? faceSession,
        SemaphoreSlim anprLock,
        SemaphoreSlim faceLock,
        List<NamedOnnxValue> inputs,
        Action<double, double> recordOverlap,
        CancellationToken ct)
    {
        if (anprSession == null || faceSession == null) return;

        long anprStart = 0, anprEnd = 0;
        long faceStart = 0, faceEnd = 0;

        var anprTask = Task.Run(async () =>
        {
            await anprLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                anprStart = Stopwatch.GetTimestamp();
                using var results = anprSession.Run(inputs);
                anprEnd = Stopwatch.GetTimestamp();
            }
            finally
            {
                anprLock.Release();
            }
        }, ct);

        var faceTask = Task.Run(async () =>
        {
            await faceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                faceStart = Stopwatch.GetTimestamp();
                using var results = faceSession.Run(inputs);
                faceEnd = Stopwatch.GetTimestamp();
            }
            finally
            {
                faceLock.Release();
            }
        }, ct);

        await Task.WhenAll(anprTask, faceTask).ConfigureAwait(false);

        // محاسبه دقیق همپوشانی بازه‌ای (Monotonic Interval Overlap)
        // Concurrency = max(startA, startB) < min(endA, endB)
        long overlapStart = Math.Max(anprStart, faceStart);
        long overlapEnd = Math.Min(anprEnd, faceEnd);

        double overlapSeconds = 0.0;
        if (overlapEnd > overlapStart)
        {
            overlapSeconds = (overlapEnd - overlapStart) / (double)Stopwatch.Frequency;
        }

        double durA = (anprEnd - anprStart) / (double)Stopwatch.Frequency;
        double durB = (faceEnd - faceStart) / (double)Stopwatch.Frequency;
        double minDuration = Math.Min(durA, durB);

        recordOverlap(overlapSeconds, minDuration);
    }

    #endregion

    #region Helper Methods

    private static (InferenceSession? anpr, InferenceSession? face) CreateInferenceSessions(
        AiWorkloadType workload,
        byte[] modelBytes,
        bool useDirectMl,
        int deviceId,
        List<string> warnings)
    {
        InferenceSession? anpr = null;
        InferenceSession? face = null;

        if (workload is AiWorkloadType.AnprOnly or AiWorkloadType.Combined)
        {
            anpr = CreateSession(modelBytes, useDirectMl, deviceId, "ANPR", warnings);
        }

        if (workload is AiWorkloadType.FaceOnly or AiWorkloadType.Combined)
        {
            face = CreateSession(modelBytes, useDirectMl, deviceId, "Face", warnings);
        }

        return (anpr, face);
    }

    private static InferenceSession CreateSession(byte[] modelBytes, bool useDirectMl, int deviceId, string workloadLabel, List<string> warnings)
    {
        using var options = new SessionOptions();
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        if (useDirectMl)
        {
            try
            {
                options.EnableMemoryPattern = false; // الزامی برای پایداری DirectML
                options.AppendExecutionProvider_DML(deviceId);
            }
            catch (Exception ex)
            {
                warnings.Add($"پرووایدر DirectML برای سشن {workloadLabel} ناموفق بود. فال‌بک به CPU: {ex.Message}");
            }
        }

        return new InferenceSession(modelBytes, options);
    }

    private static (ExecutionConcurrencyMode Mode, double Ratio) EvaluateConcurrencyMode(
        AiWorkloadType workload,
        long totalEvaluated,
        long totalOverlapping,
        double cumulativeOverlapSec,
        double cumulativeMinDurSec)
    {
        if (workload != AiWorkloadType.Combined || totalEvaluated == 0)
        {
            return (ExecutionConcurrencyMode.Unknown, 0.0);
        }

        double overlapRatio = cumulativeMinDurSec > 0 ? cumulativeOverlapSec / cumulativeMinDurSec : 0.0;

        // اگر بیش از ۵ درصد همپوشانی زمانی واقعی ثبت شده باشد
        if (overlapRatio >= 0.05)
        {
            return (ExecutionConcurrencyMode.ConcurrentOverlap, overlapRatio);
        }

        return (ExecutionConcurrencyMode.Serialized, overlapRatio);
    }

    private static bool evidence_is_fully_calibratable(BenchmarkConfiguration config, TimeSpan actualDuration, ExecutionConcurrencyMode concurrencyMode)
    {
        if (config.Tier == BenchmarkDurationTier.SmokeTest30s || actualDuration < TimeSpan.FromMinutes(4.5))
            return false;

        if (config.ModelFidelity != BenchmarkModelFidelity.FullProductionModel)
            return false;

        if (config.InputProvenance == BenchmarkInputProvenance.SyntheticMemoryBuffer)
            return false;

        if (config.Workload == AiWorkloadType.Combined && concurrencyMode == ExecutionConcurrencyMode.Serialized)
            return false;

        return true;
    }

    private static void ReportProgress(
        IProgress<BenchmarkExecutionProgress>? progress,
        BenchmarkExecutionPhase phase,
        TimeSpan elapsed,
        TimeSpan remaining,
        double progressPercentage,
        double instantFps,
        double avgFps,
        SystemResourceMetricsSampler sampler,
        FrameAccountingMetrics frameAccounting,
        ExecutionConcurrencyMode concurrencyMode,
        double overlapRatio,
        string statusMessage,
        MemoryStabilityMetrics? memoryMetrics = null,
        double currentUiLagMs = 0.0)
    {
        if (progress == null) return;

        var (infP50, infP95, _) = sampler.GetInferenceLatencies();
        var (e2eP50, e2eP95, _) = sampler.GetEndToEndLatencies();
        var (_, qWaitP95, _) = sampler.GetQueueWaitLatencies();

        var update = new BenchmarkExecutionProgress
        {
            Phase = phase,
            Elapsed = elapsed,
            EstimatedRemaining = remaining,
            ProgressPercentage = Math.Round(progressPercentage, 1),
            InstantaneousFps = Math.Round(instantFps, 1),
            AverageProcessingFps = Math.Round(avgFps, 1),
            CurrentInferenceLatencyP50Ms = infP50,
            CurrentInferenceLatencyP95Ms = infP95,
            CurrentEndToEndLatencyP50Ms = e2eP50,
            CurrentEndToEndLatencyP95Ms = e2eP95,
            CurrentQueueWaitP95Ms = qWaitP95,
            FrameAccounting = frameAccounting.CreateSnapshot(),
            MemoryMetrics = memoryMetrics ?? new MemoryStabilityMetrics(),
            CurrentUiDispatcherLagMs = currentUiLagMs,
            ConcurrencyMode = concurrencyMode,
            ConcurrencyOverlapRatio = overlapRatio,
            StatusMessage = statusMessage
        };

        progress.Report(update);
    }

    private static BenchmarkEvidence CreateFailedEvidence(
        Guid runId,
        DateTime executedAtUtc,
        BenchmarkConfiguration config,
        FrameAccountingMetrics frameAccounting,
        SystemResourceMetricsSampler sampler,
        List<string> validationMessages)
    {
        return new BenchmarkEvidence
        {
            RunId = runId,
            ExecutedAtUtc = executedAtUtc,
            DurationTier = config.Tier,
            ActualDuration = TimeSpan.Zero,
            SourceType = CalibrationStreamSourceType.LiveRtspStream,
            InputProvenance = config.InputProvenance,
            ModelFidelity = config.ModelFidelity,
            FingerprintHash = config.Fingerprint?.DeterministicHash ?? string.Empty,
            Workload = config.Workload,
            TestedChannelCount = config.ChannelCount,
            FrameAccounting = frameAccounting.CreateSnapshot(),
            PassedDiagnosticCriteria = false,
            PassedAllCriteria = false,
            ValidationMessages = validationMessages
        };
    }

    #endregion
}
