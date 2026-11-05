using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MehrganSurveillance.Services;

/// <summary>
/// مخزن امن مدیریت کلیدهای AES-256-GCM با ورود اتمیک، بازنشستگی ایمن و ممانعت از نشت منابع نیتیو
/// </summary>
public sealed class ProtectedKeyStore : IAsyncDisposable, IDisposable
{
    private readonly ReaderWriterLockSlim _storeLock = new();
    private readonly Dictionary<ushort, KeyEntry> _keys = new();
    private ushort _activeKeyId;
    private int _isDisposed;

    private sealed class KeyEntry : IAsyncDisposable, IDisposable
    {
        private const int RetiredMask = unchecked((int)0x80000000);
        private int _state; // Composite state: Bit 31 = Retired, Bits 0..30 = In-Flight Operations
        private readonly TaskCompletionSource _zeroInFlightTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposedOnce;

        public ushort KeyId { get; }
        public AesGcm Engine { get; }
        public uint KeyPrefix { get; }
        public DurableNonceAllocator Allocator { get; }

        public KeyEntry(ushort keyId, AesGcm engine, uint keyPrefix, DurableNonceAllocator allocator)
        {
            KeyId = keyId;
            Engine = engine;
            KeyPrefix = keyPrefix;
            Allocator = allocator;
        }

        public bool TryEnter()
        {
            while (true)
            {
                int current = Volatile.Read(ref _state);
                if ((current & RetiredMask) != 0) return false;

                int next = current + 1;
                if (Interlocked.CompareExchange(ref _state, next, current) == current)
                    return true;
            }
        }

        public void Exit()
        {
            int current = Interlocked.Decrement(ref _state);
            if ((current & RetiredMask) != 0 && (current & ~RetiredMask) == 0)
            {
                _zeroInFlightTcs.TrySetResult();
            }
        }

        public async ValueTask RetireAndDisposeAsync()
        {
            while (true)
            {
                int current = Volatile.Read(ref _state);
                if ((current & RetiredMask) != 0) break;

                int next = current | RetiredMask;
                if (Interlocked.CompareExchange(ref _state, next, current) == current)
                {
                    if ((current & ~RetiredMask) == 0)
                    {
                        _zeroInFlightTcs.TrySetResult();
                    }
                    break;
                }
            }

            await _zeroInFlightTcs.Task.ConfigureAwait(false);

            if (Interlocked.Exchange(ref _disposedOnce, 1) == 0)
            {
                Engine.Dispose();
                Allocator.Dispose();
            }
        }

        public void Dispose() => RetireAndDisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() => RetireAndDisposeAsync();
    }

    public void RegisterMasterKey(ushort keyId, ReadOnlySpan<byte> rawKey, uint keyPrefix, string stateDir)
    {
        if (rawKey.Length != 32) throw new ArgumentException("Master key must be exactly 256 bits.", nameof(rawKey));

        _storeLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (_keys.ContainsKey(keyId))
            {
                throw new InvalidOperationException($"KeyId {keyId} is already registered. Duplicate registration is rejected.");
            }

            var engine = new AesGcm(rawKey, tagSizeInBytes: 16);
            var allocator = new DurableNonceAllocator(Path.Combine(stateDir, $"key_{keyId}.nonce"));

            _keys[keyId] = new KeyEntry(keyId, engine, keyPrefix, allocator);
            _activeKeyId = keyId;
        }
        finally
        {
            _storeLock.ExitWriteLock();
        }
    }

    public void RegisterDecryptOnlyKey(ushort keyId, ReadOnlySpan<byte> rawKey, string stateDir)
    {
        if (rawKey.Length != 32) throw new ArgumentException("Master key must be exactly 256 bits.", nameof(rawKey));

        _storeLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (_keys.ContainsKey(keyId)) return;

            var engine = new AesGcm(rawKey, tagSizeInBytes: 16);
            var allocator = new DurableNonceAllocator(Path.Combine(stateDir, $"key_{keyId}.nonce"));

            _keys[keyId] = new KeyEntry(keyId, engine, 0, allocator);
        }
        finally
        {
            _storeLock.ExitWriteLock();
        }
    }

    public void Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, Span<byte> nonce, ReadOnlySpan<byte> aad, out ushort usedKeyId)
    {
        KeyEntry entry;
        _storeLock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            usedKeyId = _activeKeyId;
            if (!_keys.TryGetValue(usedKeyId, out entry!))
                throw new InvalidOperationException($"No active encryption key registered (KeyId {usedKeyId}).");
            if (!entry.TryEnter())
                throw new InvalidOperationException($"Active KeyId {usedKeyId} is retired. Encryption rejected.");
        }
        finally
        {
            _storeLock.ExitReadLock();
        }

        try
        {
            entry.Allocator.AllocateNextNonce(entry.KeyPrefix, nonce);
            entry.Engine.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        finally
        {
            entry.Exit();
        }
    }

    public void Decrypt(ushort keyId, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        KeyEntry entry;
        _storeLock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            if (!_keys.TryGetValue(keyId, out entry!))
                throw new CryptographicException($"KeyId {keyId} not registered in key store.");
            if (!entry.TryEnter())
                throw new InvalidOperationException($"KeyId {keyId} is retired and inaccessible.");
        }
        finally
        {
            _storeLock.ExitReadLock();
        }

        try
        {
            entry.Engine.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        finally
        {
            entry.Exit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        List<KeyEntry> entriesToDispose;
        _storeLock.EnterWriteLock();
        try
        {
            entriesToDispose = new List<KeyEntry>(_keys.Values);
            _keys.Clear();
        }
        finally
        {
            _storeLock.ExitWriteLock();
        }

        foreach (var entry in entriesToDispose)
        {
            await entry.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        _storeLock.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(ProtectedKeyStore));
    }
}
