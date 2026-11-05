using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MehrganSurveillance.Services;

/// <summary>
/// قفل سراسری نام‌دار با رزرو نخ اختصاصی جهت انطباق کامل با قوانین Thread-Affinity ویندوز
/// </summary>
public sealed class DedicatedThreadArchiveLock : IAsyncDisposable, IDisposable
{
    private const string MutexName = @"Global\MehrganSurveillance_FaceArchive_SingleWriter";
    private readonly BlockingCollection<LockRequest> _queue = new();
    private readonly Thread _dedicatedThread;
    private readonly TaskCompletionSource<bool> _threadReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _state; // 0 = Running, 1 = ShutdownRequested, 2 = Disposed
    private int _hasOwnership; // 1 = Owned on dedicated thread, 0 = Not owned

    private enum RequestType { Acquire, Release, Shutdown }
    private record LockRequest(RequestType Type, TaskCompletionSource<bool> Completion, CancellationToken Ct);

    public bool HasOwnership => Volatile.Read(ref _hasOwnership) == 1;

    public DedicatedThreadArchiveLock()
    {
        _dedicatedThread = new Thread(ThreadLoop)
        {
            Name = "FaceArchive-MutexDedicatedThread",
            IsBackground = true
        };
        _dedicatedThread.SetApartmentState(ApartmentState.MTA);
        _dedicatedThread.Start();
    }

    private void ThreadLoop()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            _threadReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _threadReady.TrySetException(ex);
            _shutdownCompletedTcs.TrySetResult();
            DrainRemainingRequestsWithException(new InvalidOperationException("Mutex initialization failed.", ex));
            return;
        }

        try
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                if (req.Type == RequestType.Shutdown)
                {
                    if (Volatile.Read(ref _hasOwnership) == 1)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch { /* Suppress on shutdown */ }
                        Volatile.Write(ref _hasOwnership, 0);
                    }
                    req.Completion.TrySetResult(true);
                    break;
                }

                if (req.Ct.IsCancellationRequested)
                {
                    req.Completion.TrySetCanceled(req.Ct);
                    continue;
                }

                if (req.Type == RequestType.Acquire)
                {
                    if (Volatile.Read(ref _hasOwnership) == 1)
                    {
                        req.Completion.TrySetResult(true);
                        continue;
                    }

                    bool acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    catch (Exception ex)
                    {
                        req.Completion.TrySetException(ex);
                        continue;
                    }

                    if (acquired)
                    {
                        if (req.Ct.IsCancellationRequested)
                        {
                            try { mutex.ReleaseMutex(); }
                            catch { /* Suppress release errors during rollback */ }
                            Volatile.Write(ref _hasOwnership, 0);
                            req.Completion.TrySetCanceled(req.Ct);
                        }
                        else
                        {
                            Volatile.Write(ref _hasOwnership, 1);
                            req.Completion.TrySetResult(true);
                        }
                    }
                    else
                    {
                        req.Completion.TrySetResult(false);
                    }
                }
                else if (req.Type == RequestType.Release)
                {
                    if (Volatile.Read(ref _hasOwnership) == 1)
                    {
                        try
                        {
                            mutex.ReleaseMutex();
                            Volatile.Write(ref _hasOwnership, 0);
                            req.Completion.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            req.Completion.TrySetException(ex);
                        }
                    }
                    else
                    {
                        req.Completion.TrySetResult(true);
                    }
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref _hasOwnership) == 1 && mutex != null)
            {
                try { mutex.ReleaseMutex(); }
                catch { /* Ensure clean release */ }
                Volatile.Write(ref _hasOwnership, 0);
            }
            mutex?.Dispose();
            DrainRemainingRequestsWithException(new ObjectDisposedException(nameof(DedicatedThreadArchiveLock)));
            _shutdownCompletedTcs.TrySetResult();
        }
    }

    private void DrainRemainingRequestsWithException(Exception ex)
    {
        while (_queue.TryTake(out var orphan))
        {
            orphan.Completion.TrySetException(ex);
        }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        EnsureAdmissible();
        await _threadReady.Task.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var req = new LockRequest(RequestType.Acquire, tcs, ct);
        if (!TryEnqueue(req))
        {
            throw new ObjectDisposedException(nameof(DedicatedThreadArchiveLock), "Shutdown is already in progress.");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        EnsureAdmissible();
        await _threadReady.Task.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var req = new LockRequest(RequestType.Release, tcs, ct);
        if (!TryEnqueue(req))
        {
            throw new ObjectDisposedException(nameof(DedicatedThreadArchiveLock), "Shutdown is already in progress.");
        }

        await tcs.Task.ConfigureAwait(false);
    }

    private bool TryEnqueue(LockRequest req)
    {
        if (Volatile.Read(ref _state) != 0) return false;
        try
        {
            return _queue.TryAdd(req);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void EnsureAdmissible()
    {
        if (Volatile.Read(ref _state) != 0)
            throw new ObjectDisposedException(nameof(DedicatedThreadArchiveLock), "Lock is shutting down or disposed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            await _shutdownCompletedTcs.Task.ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(new LockRequest(RequestType.Shutdown, tcs, CancellationToken.None));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch { /* Fallback to thread termination wait */ }

        _queue.CompleteAdding();

        var threadExited = await Task.Run(() => _dedicatedThread.Join(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        if (!threadExited)
        {
            _shutdownCompletedTcs.TrySetResult();
        }

        await _shutdownCompletedTcs.Task.ConfigureAwait(false);
        _queue.Dispose();
        Volatile.Write(ref _state, 2);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
