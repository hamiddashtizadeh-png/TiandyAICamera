using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MehrganSurveillance.Services;

/// <summary>
/// تخصیص‌دهنده مقاوم و مانع تکرار Nonce با رزرو بازه‌ای و بازیابی از دیسک
/// </summary>
public sealed class DurableNonceAllocator : IDisposable
{
    private readonly object _lock = new();
    private readonly FileStream _stateStream;
    private readonly string _filePath;
    private ulong _currentCounter;
    private ulong _reservedLimit;
    private const ulong ReservationBlockSize = 1000UL;

    public DurableNonceAllocator(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stateStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64,
            FileOptions.WriteThrough);

        InitializeCounter();
    }

    private void InitializeCounter()
    {
        Span<byte> buffer = stackalloc byte[8];
        if (_stateStream.Length >= 8)
        {
            _stateStream.Position = 0;
            int read = _stateStream.Read(buffer);
            if (read == 8)
            {
                _currentCounter = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }
        }
        else
        {
            _currentCounter = 1UL;
        }

        // پرش به جلو جهت تضمین عدم تکرار Nonce پس از راه‌اندازی مجدد
        _reservedLimit = _currentCounter + ReservationBlockSize;
        CommitReservedLimit();
    }

    private void CommitReservedLimit()
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer, _reservedLimit);
        _stateStream.Position = 0;
        _stateStream.Write(buffer);
        _stateStream.Flush(flushToDisk: true);
    }

    public void AllocateNextNonce(uint keyPrefix, Span<byte> nonceSpan)
    {
        if (nonceSpan.Length != 12)
            throw new ArgumentException("Nonce span must be exactly 12 bytes.", nameof(nonceSpan));

        ulong allocatedCounter;
        lock (_lock)
        {
            if (_currentCounter >= _reservedLimit)
            {
                _reservedLimit += ReservationBlockSize;
                CommitReservedLimit();
            }

            allocatedCounter = _currentCounter++;
        }

        // ۴ بایت پیشوند کلید (Big-Endian)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nonceSpan.Slice(0, 4), keyPrefix);
        // ۸ بایت شمارنده افزایشی یکتا (Big-Endian)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonceSpan.Slice(4, 8), allocatedCounter);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _stateStream.Flush(flushToDisk: true);
                _stateStream.Dispose();
            }
            catch
            {
            }
        }
    }
}
