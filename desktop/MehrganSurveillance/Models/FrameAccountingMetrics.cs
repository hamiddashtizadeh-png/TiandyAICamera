using System;
using System.Threading;

namespace MehrganSurveillance.Models;

/// <summary>
/// ثبت دقیق و تفکیک‌شده سرنوشت فریم‌ها در طول خط لوله بنچمارک با ۷ وضعیت مجزا
/// رعایت اصول حسابداری فریم بدون ایجاد ابهام در فریم‌های افتاده و با تضمین برابری پایستگی
/// </summary>
public class FrameAccountingMetrics
{
    private long _produced;
    private long _rejectedBeforeEnqueue;
    private long _evictedAfterEnqueue;
    private long _processed;
    private long _failed;
    private long _pendingInQueue;
    private long _inFlight;

    /// <summary>
    /// ۱. کل فریم‌های تولید یا دریافت شده توسط ورودی
    /// </summary>
    public long Produced => Interlocked.Read(ref _produced);

    /// <summary>
    /// ۲. فریم‌های رد شده پیش از ورود به صف (مثلاً در خط‌مشی DropNewest زمان پر بودن صف یا گلوگاه اولیه)
    /// </summary>
    public long RejectedBeforeEnqueue => Interlocked.Read(ref _rejectedBeforeEnqueue);

    /// <summary>
    /// ۳. فریم‌های اخراج‌شده از صف پس از ورود (در خط‌مشی DropOldest هنگام ورود فریم جدیدتر به صف پر)
    /// </summary>
    public long EvictedAfterEnqueue => Interlocked.Read(ref _evictedAfterEnqueue);

    /// <summary>
    /// ۴. فریم‌های با موفقیت پردازش‌شده در تمام مراحل خط لوله
    /// </summary>
    public long Processed => Interlocked.Read(ref _processed);

    /// <summary>
    /// ۵. فریم‌های شکست‌خورده حین پردازش به دلیل بروز استثنا، خطای تنسور یا ناهماهنگی
    /// </summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>
    /// ۶. فریم‌های فعلی منتظر در صف ورودی کانال‌ها
    /// </summary>
    public long PendingInQueue => Interlocked.Read(ref _pendingInQueue);

    /// <summary>
    /// ۷. فریم‌های در حال پردازش در خط لوله توسط کارگران استنتاج
    /// </summary>
    public long InFlight => Interlocked.Read(ref _inFlight);

    /// <summary>
    /// مجموع فریم‌های وارد شده به صف (شامل تمام موارد اخراج‌شده، پردازش‌شده، خطا داده و در حال انتظار)
    /// </summary>
    public long TotalEnqueued => EvictedAfterEnqueue + Processed + Failed + PendingInQueue + InFlight;

    /// <summary>
    /// کل فریم‌های افتاده (مجموع فریم‌های ردشده قبل از صف و فریم‌های اخراج‌شده پس از صف)
    /// </summary>
    public long TotalDroppedFrames => RejectedBeforeEnqueue + EvictedAfterEnqueue;

    /// <summary>
    /// نسبت فریم‌های افتاده به کل فریم‌های تولیدشده (۰.۰۱ به معنای ۱ درصد افت فریم)
    /// </summary>
    public double DroppedFrameRatio
    {
        get
        {
            long prod = Produced;
            return prod > 0 ? (double)TotalDroppedFrames / prod : 0.0;
        }
    }

    /// <summary>
    /// نرخ پردازش موفق نسبت به فریم‌های تولید شده
    /// </summary>
    public double SuccessRatio
    {
        get
        {
            long prod = Produced;
            return prod > 0 ? (double)Processed / prod : 0.0;
        }
    }

    // متدهای اتمیک و نخ‌ایمن برای مدیریت وضعیت‌ها
    public void IncrementProduced() => Interlocked.Increment(ref _produced);
    public void IncrementRejectedBeforeEnqueue() => Interlocked.Increment(ref _rejectedBeforeEnqueue);

    public void OnEnqueued()
    {
        Interlocked.Increment(ref _pendingInQueue);
    }

    public void OnEvictedFromQueue()
    {
        Interlocked.Decrement(ref _pendingInQueue);
        Interlocked.Increment(ref _evictedAfterEnqueue);
    }

    public void OnPickedFromQueue()
    {
        Interlocked.Decrement(ref _pendingInQueue);
        Interlocked.Increment(ref _inFlight);
    }

    public void OnDequeued() => Interlocked.Decrement(ref _pendingInQueue);
    public void OnInFlightStarted() => Interlocked.Increment(ref _inFlight);

    public void OnProcessingSucceeded()
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _processed);
    }

    public void OnProcessingFailed()
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _failed);
    }

    public void OnInFlightCompleted(bool success)
    {
        if (success)
            OnProcessingSucceeded();
        else
            OnProcessingFailed();
    }

    /// <summary>
    /// اعتبارسنجی اصل پایستگی و تغییرناپذیری فریم‌ها:
    /// Produced == RejectedBeforeEnqueue + EvictedAfterEnqueue + Processed + Failed + PendingInQueue + InFlight
    /// </summary>
    public bool VerifyInvariants(out string details)
    {
        long prod = Produced;
        long rej = RejectedBeforeEnqueue;
        long evict = EvictedAfterEnqueue;
        long proc = Processed;
        long fail = Failed;
        long pending = PendingInQueue;
        long inFlight = InFlight;

        long accountedTotal = rej + evict + proc + fail + pending + inFlight;

        if (prod == accountedTotal)
        {
            details = $"پایستگی حسابداری فریم برقرار است. کل فریم‌ها ({prod}) = رد شده ({rej}) + اخراج از صف ({evict}) + پردازش موفق ({proc}) + خطا ({fail}) + در صف ({pending}) + در حال اجرا ({inFlight})";
            return true;
        }

        long discrepancy = prod - accountedTotal;
        details = $"نقض پایستگی حسابداری فریم: اختلاف {discrepancy} فریم بین کل تولیدشده ({prod}) و مجموع اجزا ({accountedTotal}). [Rejected={rej}, Evicted={evict}, Processed={proc}, Failed={fail}, Pending={pending}, InFlight={inFlight}]";
        return false;
    }

    /// <summary>
    /// ایجاد یک تصویر لحظه‌ای (Snapshot) مجزا و غیروابسته برای گزارش‌گیری یا الصاق به شواهد
    /// </summary>
    public FrameAccountingMetrics CreateSnapshot()
    {
        return new FrameAccountingMetrics
        {
            _produced = this.Produced,
            _rejectedBeforeEnqueue = this.RejectedBeforeEnqueue,
            _evictedAfterEnqueue = this.EvictedAfterEnqueue,
            _processed = this.Processed,
            _failed = this.Failed,
            _pendingInQueue = this.PendingInQueue,
            _inFlight = this.InFlight
        };
    }
}
