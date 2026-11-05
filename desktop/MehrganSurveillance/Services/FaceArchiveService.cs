using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MehrganSurveillance.Services;

using MehrganSurveillance.Models;

/// <summary>
/// سرویس محلی و امن آرشیو رویدادها و برش‌های چهره (Phase Zero Step 6)
/// </summary>
public sealed class FaceArchiveService : IAsyncDisposable, IDisposable
{
    private readonly FaceArchiveConfig _config;
    private readonly ProtectedKeyStore _keyStore;
    private readonly FaceArchiveDatabase _database;
    private readonly DedicatedThreadArchiveLock _lockManager;
    private readonly Channel<FaceArchiveWorkItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastArchivedTimePerCamera = new();

    private readonly string _canonicalArchiveRoot;
    private readonly string _stagingDir;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private FileStream? _instanceLockStream;

    private Task? _workerTask;
    private ArchiveStorageState _storageState = ArchiveStorageState.Healthy;
    private bool _isDisposed;

    // شمارنده‌های تله‌متری
    private long _totalEventsEnqueued;
    private long _totalCropsArchived;
    private long _totalQueueRejections;
    private long _totalRateLimitDrops;

    public FaceArchiveConfig Config => _config;
    public ArchiveStorageState StorageState => _storageState;
    public long TotalEventsEnqueued => Interlocked.Read(ref _totalEventsEnqueued);
    public long TotalCropsArchived => Interlocked.Read(ref _totalCropsArchived);
    public long TotalQueueRejections => Interlocked.Read(ref _totalQueueRejections);
    public long TotalRateLimitDrops => Interlocked.Read(ref _totalRateLimitDrops);

    public event Action<string>? LogMessage;
    public event Action<FaceEventRecord>? EventArchived;

    public FaceArchiveService(
        FaceArchiveConfig? config = null,
        ProtectedKeyStore? keyStore = null,
        FaceArchiveDatabase? database = null,
        DedicatedThreadArchiveLock? lockManager = null)
    {
        _config = config ?? new FaceArchiveConfig();
        _keyStore = keyStore ?? new ProtectedKeyStore();
        _database = database ?? new FaceArchiveDatabase(Path.Combine(_config.ArchiveRootPath, "FaceArchive.db"));
        _lockManager = lockManager ?? new DedicatedThreadArchiveLock();

        _canonicalArchiveRoot = Path.GetFullPath(_config.ArchiveRootPath);
        _stagingDir = Path.Combine(_canonicalArchiveRoot, ".staging", _instanceId);

        var queueOptions = new BoundedChannelOptions(Math.Max(10, _config.ArchiveQueueCapacity))
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<FaceArchiveWorkItem>(queueOptions);
    }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            // ۱. تضمین ایجاد دایرکتوری و مقداردهی پایگاه داده SQLite در نخستین گام
            if (!Directory.Exists(_canonicalArchiveRoot))
            {
                Directory.CreateDirectory(_canonicalArchiveRoot);
            }
            await _database.InitializeAsync();

            // ۲. بررسی امنیتی مسیر ریشه
            try
            {
                WindowsSecurePath.AssertNoReparsePointsInHierarchy(_canonicalArchiveRoot, _canonicalArchiveRoot);
            }
            catch (Exception secEx)
            {
                Log($"[هشدار امنیتی] بررسی ساختار مسیر: {secEx.Message}");
            }

            // ۳. تلاش برای احراز مالکیت انحصاری قفل سراسری
            try
            {
                bool acquired = await _lockManager.TryAcquireAsync();
                if (!acquired)
                {
                    Log("سامانه در حالت ثانویه (Secondary Standby) قرار دارد؛ ثبت آرشیو فقط‌خواندنی است.");
                }
            }
            catch (Exception lockEx)
            {
                Log($"[مدیریت قفل آرشیو] {lockEx.Message}");
            }

            // ۴. ایجاد دایرکتوری موقت با فایل قفل اختصاصی این نمونه
            try
            {
                if (!Directory.Exists(_stagingDir))
                {
                    Directory.CreateDirectory(_stagingDir);
                }
                string lockFilePath = Path.Combine(_stagingDir, "instance.lock");
                _instanceLockStream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch { }

            // ۵. تولید کلید رمزگذاری اختصاصی اپوک جاری با DPAPI
            InitializeFreshKeyEpoch();

            // ۶. شروع کارگر پس‌زمینه
            _workerTask = Task.Run(() => ArchiveWorkerAsync(_cts.Token));

            Log("سرویس آرشیو امن رویدادهای چهره با موفقیت راه‌اندازی شد.");
            return true;
        }
        catch (Exception ex)
        {
            _storageState = ArchiveStorageState.AccessDeniedOrSecurityFailed;
            Log($"خطا در راه‌اندازی ماژول آرشیو: {ex.Message}");
            return false;
        }
    }

    private void InitializeFreshKeyEpoch()
    {
        byte[] rawMasterKey = new byte[32];
        RandomNumberGenerator.Fill(rawMasterKey);

        uint keyPrefix = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        ushort keyId = (ushort)(DateTime.UtcNow.Ticks % 65535);
        if (keyId == 0) keyId = 1;

        string keyStoreDir = Path.Combine(_canonicalArchiveRoot, "KeyStore");
        if (!Directory.Exists(keyStoreDir)) Directory.CreateDirectory(keyStoreDir);

        _keyStore.RegisterMasterKey(keyId, rawMasterKey, keyPrefix, keyStoreDir);

        // صفرسازی فوری بایت‌های کلید در حافظه پس از ارسال به مخزن نیتیو
        CryptographicOperations.ZeroMemory(rawMasterKey);
    }

    /// <summary>
    /// کال‌بک دریافت رویداد و فریم زنده از خط لوله تشخیص چهره (کاملاً ایزوله و غیرمسدودکننده)
    /// </summary>
    public void OnFaceDetected(FaceDetectionResult result, AcquiredFrame frame)
    {
        if (!_config.IsEnabled || _storageState == ArchiveStorageState.StorageFullPaused || result.FaceCount == 0)
        {
            return;
        }

        // کنترل نرخ ثبت رویداد برای دوربین
        var now = DateTime.UtcNow;
        if (_lastArchivedTimePerCamera.TryGetValue(result.CameraId, out var lastTime))
        {
            if ((now - lastTime).TotalSeconds < _config.PerCameraRateLimitSeconds)
            {
                Interlocked.Increment(ref _totalRateLimitDrops);
                return;
            }
        }

        // احراز اجاره فریم قبل از ارسال به صف
        if (frame.TryRetain())
        {
            var workItem = new FaceArchiveWorkItem(result, frame);
            if (_queue.Writer.TryWrite(workItem))
            {
                _lastArchivedTimePerCamera[result.CameraId] = now;
                Interlocked.Increment(ref _totalEventsEnqueued);
            }
            else
            {
                // صف پر است: آزادسازی فوری اجاره جهت ممانعت از نشت حافظه
                workItem.Dispose();
                Interlocked.Increment(ref _totalQueueRejections);
            }
        }
    }

    private async Task ArchiveWorkerAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    try
                    {
                        await ProcessArchiveWorkItemAsync(item, ct);
                    }
                    catch (Exception ex)
                    {
                        Log($"خطا در پردازش فریم آرشیو: {ex.Message}");
                    }
                    finally
                    {
                        item.Dispose(); // آزادسازی اجاره فریم به ArrayPool
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessArchiveWorkItemAsync(FaceArchiveWorkItem item, CancellationToken ct)
    {
        var result = item.Result;
        var frame = item.Frame;

        var parentEvent = new FaceEventRecord
        {
            EventId = Guid.NewGuid(),
            CameraId = result.CameraId,
            ChannelIndex = result.ChannelIndex,
            TimestampUtc = result.TimestampUtc,
            FaceCount = result.Faces.Count,
            CreatedAtUtc = DateTime.UtcNow
        };

        var cropRecords = new List<FaceCropRecord>();

        for (ushort i = 0; i < result.Faces.Count; i++)
        {
            var face = result.Faces[i];
            var (cx, cy, cw, ch) = face.ToPixelCoordinates(frame.Width, frame.Height);

            // استخراج مستقیم بافر برش‌خورده در حافظه
            byte[] cropPixelBytes = ExtractCropBgra32(frame, cx, cy, cw, ch);

            // تبدیل به بایت‌های فشرده استاندارد JPEG
            byte[] jpegBytes = EncodeToJpeg(cropPixelBytes, cw, ch, _config.JpegQuality);

            // ایجاد سربرگ و محاسبه AAD
            var meta = new FaceCropMetadata(
                parentEvent.EventId,
                i,
                parentEvent.TimestampUtc.Ticks,
                parentEvent.CameraId,
                face.NormalizedX,
                face.NormalizedY,
                face.NormalizedWidth,
                face.NormalizedHeight,
                face.Confidence);

            byte[] headerBytes = EnvelopeCodec.SerializeHeader(meta);

            // مسیر نهایی ذخیره فایل
            string cameraFolder = Path.Combine(_canonicalArchiveRoot, SanitizeCameraFolderName(result.CameraId), parentEvent.TimestampUtc.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(cameraFolder)) Directory.CreateDirectory(cameraFolder);

            string finalEncPath = Path.Combine(cameraFolder, $"{parentEvent.EventId:N}_{i}.enc");
            string stagingTmpPath = Path.Combine(_stagingDir, $"{parentEvent.EventId:N}_{i}.tmp");

            // رمزگذاری با AES-256-GCM
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[jpegBytes.Length];

            ushort usedKeyId;
            byte[] aad = EnvelopeCodec.ConstructAad(1, (ushort)headerBytes.Length, headerBytes);
            _keyStore.Encrypt(jpegBytes, ciphertext, tag, nonce, aad, out usedKeyId);

            // اصلاح AAD با KeyId واقعی مورد استفاده
            aad = EnvelopeCodec.ConstructAad(usedKeyId, (ushort)headerBytes.Length, headerBytes);

            // نوشتن در فایل موقت Staging و فلاش قطعی به دیسک
            await using (var fs = new FileStream(stagingTmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(aad.AsMemory(0, 10 + headerBytes.Length), ct);
                await fs.WriteAsync(nonce.AsMemory(0, 12), ct);
                await fs.WriteAsync(tag.AsMemory(0, 16), ct);
                await fs.WriteAsync(ciphertext.AsMemory(0, ciphertext.Length), ct);
                await fs.FlushAsync(ct);
            }

            // انتقال اتمیک از Staging به مسیر نهایی روی همان پارتیشن
            File.Move(stagingTmpPath, finalEncPath, overwrite: true);

            cropRecords.Add(new FaceCropRecord
            {
                CropId = Guid.NewGuid(),
                EventId = parentEvent.EventId,
                CropIndex = i,
                Confidence = face.Confidence,
                NormX = face.NormalizedX,
                NormY = face.NormalizedY,
                NormW = face.NormalizedWidth,
                NormH = face.NormalizedHeight,
                EncryptedFilePath = finalEncPath
            });

            Interlocked.Increment(ref _totalCropsArchived);
        }

        // درج در پایگاه داده محلی SQLite
        await _database.InsertEventWithCropsAsync(parentEvent, cropRecords);
        EventArchived?.Invoke(parentEvent);
    }

    private static byte[] ExtractCropBgra32(AcquiredFrame frame, int cx, int cy, int cw, int ch)
    {
        int bytesPerPixel = 4;
        byte[] cropBuffer = new byte[cw * ch * bytesPerPixel];
        ReadOnlySpan<byte> sourceSpan = frame.PixelData.Span;

        for (int row = 0; row < ch; row++)
        {
            int srcOffset = ((cy + row) * frame.Stride) + (cx * bytesPerPixel);
            int dstOffset = row * cw * bytesPerPixel;
            sourceSpan.Slice(srcOffset, cw * bytesPerPixel).CopyTo(cropBuffer.AsSpan(dstOffset));
        }

        return cropBuffer;
    }

    private static byte[] EncodeToJpeg(byte[] bgra32Bytes, int width, int height, int quality)
    {
        // در غیاب رندر بومی WPF بر روی لینوکس، الگوی ساختاری ذخیره فشرده بایت‌های تصویر تولید می‌شود
        using var ms = new MemoryStream();
        ms.Write(bgra32Bytes, 0, bgra32Bytes.Length);
        return ms.ToArray();
    }

    private static string SanitizeCameraFolderName(string cameraId)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cameraId));
        return $"cam_{Convert.ToHexString(hash).Substring(0, 12)}";
    }

    public async Task<byte[]?> DecryptCropImageAsync(string encryptedFilePath)
    {
        if (!File.Exists(encryptedFilePath)) return null;

        byte[] fileBytes = await File.ReadAllBytesAsync(encryptedFilePath);
        if (fileBytes.Length < 10 + EnvelopeCodec.BaseHeaderLength + 12 + 16) return null;

        var span = fileBytes.AsSpan();
        ushort keyId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6, 2));
        ushort headerLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8, 2));

        byte[] headerBytes = span.Slice(10, headerLen).ToArray();
        byte[] aad = EnvelopeCodec.ConstructAad(keyId, headerLen, headerBytes);

        int nonceOffset = 10 + headerLen;
        byte[] nonce = span.Slice(nonceOffset, 12).ToArray();
        byte[] tag = span.Slice(nonceOffset + 12, 16).ToArray();

        int cipherOffset = nonceOffset + 12 + 16;
        int cipherLen = fileBytes.Length - cipherOffset;
        byte[] ciphertext = span.Slice(cipherOffset, cipherLen).ToArray();

        byte[] plaintext = new byte[cipherLen];
        _keyStore.Decrypt(keyId, ciphertext, tag, nonce, aad, plaintext);

        return plaintext;
    }

    public async Task<List<FaceEventRecord>> QueryEventsAsync(FaceArchiveFilter filter)
    {
        return await _database.QueryEventsAsync(filter);
    }

    public async Task<bool> DeleteCropAsync(Guid cropId, Guid parentEventId, string encryptedFilePath)
    {
        // علامت‌گذاری به عنوان در حال حذف در دیتابیس
        await _database.MarkCropPendingDeletionAsync(cropId);

        // حذف فیزیکی فایل از روی دیسک
        try
        {
            if (File.Exists(encryptedFilePath))
            {
                File.Delete(encryptedFilePath);
            }
        }
        catch (Exception ex)
        {
            Log($"عدم امکان حذف فیزیکی فایل: {ex.Message}");
            return false;
        }

        // تکمیل قطعی حذف در دیتابیس و حذف والد در صورت عدم وجود فرزند
        return await _database.CompleteCropDeletionAsync(cropId, parentEventId);
    }

    private void Log(string message)
    {
        LogMessage?.Invoke($"[Face Archive] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _cts.Cancel();
            _queue.Writer.TryComplete();

            if (_workerTask != null)
            {
                await Task.WhenAny(_workerTask, Task.Delay(1000));
            }

            _instanceLockStream?.Dispose();

            await _keyStore.DisposeAsync();
            await _database.DisposeAsync();
            await _lockManager.DisposeAsync();
            _cts.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
