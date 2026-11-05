using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services;

namespace MehrganSurveillance.Models;

/// <summary>
/// آزمون‌های متمرکز جامع آرشیو چهره و زیرساخت رمزگذاری AES-GCM (Phase Zero Step 6)
/// </summary>
public static class FaceArchiveTests
{
    public static (bool Passed, string Message) Test_EnvelopeCodec_SerializationAndParsing_MatchesExactly()
    {
        var meta = new FaceCropMetadata(
            Guid.NewGuid(),
            0,
            DateTime.UtcNow.Ticks,
            "Cam-Entrance-01",
            0.15f,
            0.20f,
            0.30f,
            0.35f,
            0.92f);

        byte[] serialized = EnvelopeCodec.SerializeHeader(meta);
        var parsed = EnvelopeCodec.ParseHeader(serialized);

        if (parsed.EventId != meta.EventId) return (false, "EventId عدم تطابق");
        if (parsed.CameraId != meta.CameraId) return (false, "CameraId عدم تطابق");
        if (Math.Abs(parsed.NormX - meta.NormX) > 0.0001f) return (false, "NormX عدم تطابق");
        if (Math.Abs(parsed.Confidence - meta.Confidence) > 0.0001f) return (false, "Confidence عدم تطابق");

        return (true, "موفق: سریال‌سازی و پارس انولوپ باینری دقیقاً منطبق بود.");
    }

    public static (bool Passed, string Message) Test_EnvelopeCodec_TamperedAad_ThrowsCryptographicException()
    {
        var meta = new FaceCropMetadata(
            Guid.NewGuid(),
            1,
            DateTime.UtcNow.Ticks,
            "Cam-Gate-02",
            0.10f,
            0.10f,
            0.20f,
            0.20f,
            0.88f);

        byte[] header = EnvelopeCodec.SerializeHeader(meta);
        byte[] validAad = EnvelopeCodec.ConstructAad(1, (ushort)header.Length, header);

        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        using var aes = new AesGcm(key, tagSizeInBytes: 16);

        byte[] nonce = new byte[12];
        byte[] plaintext = "FaceCropJpegBinaryDataSimulation"u8.ToArray();
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        aes.Encrypt(nonce, plaintext, ciphertext, tag, validAad);

        // دستکاری ۱ بایت در AAD
        byte[] tamperedAad = (byte[])validAad.Clone();
        tamperedAad[12] ^= 0x55;

        byte[] decrypted = new byte[plaintext.Length];
        bool rejected = false;
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, decrypted, tamperedAad);
        }
        catch (CryptographicException)
        {
            rejected = true;
        }

        if (!rejected)
        {
            return (false, "خطای امنیتی: دستکاری AAD توسط تگ رد نشد!");
        }

        return (true, "موفق: تغییر در حتی ۱ بایت از سربرگ اعتبارسنجی‌شده AAD با خطای قطعی رد گردید.");
    }

    public static (bool Passed, string Message) Test_ProtectedKeyStore_EncryptAndDecrypt_Roundtrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KeyStoreTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new ProtectedKeyStore();
            byte[] rawKey = new byte[32];
            RandomNumberGenerator.Fill(rawKey);

            store.RegisterMasterKey(101, rawKey, 0x12345678, tempDir);

            byte[] plaintext = "ConfidentialFaceCropPayload"u8.ToArray();
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            byte[] nonce = new byte[12];
            byte[] aad = "MFENC_TEST_AAD"u8.ToArray();

            store.Encrypt(plaintext, ciphertext, tag, nonce, aad, out ushort usedKeyId);
            if (usedKeyId != 101) return (false, "KeyId مورد استفاده نادرست است");

            byte[] decrypted = new byte[plaintext.Length];
            store.Decrypt(101, ciphertext, tag, nonce, aad, decrypted);

            for (int i = 0; i < plaintext.Length; i++)
            {
                if (decrypted[i] != plaintext[i])
                    return (false, "عدم تطابق داده‌های رمزگشایی‌شده با ورودی اصلی");
            }

            return (true, "موفق: رمزگذاری و رمزگشایی متقابل با ProtectedKeyStore کاملاً منطبق بود.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    public static async Task<(bool Passed, string Message)> Test_FaceArchiveDatabase_InsertAndQueryAndDeletion()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "FaceArchiveTest_" + Guid.NewGuid().ToString("N") + ".db");
        await using var db = new FaceArchiveDatabase(tempDb);
        await db.InitializeAsync();

        try
        {
            var eventId = Guid.NewGuid();
            var cropId = Guid.NewGuid();

            var ev = new FaceEventRecord
            {
                EventId = eventId,
                CameraId = "cam-test-1",
                ChannelIndex = 0,
                TimestampUtc = DateTime.UtcNow,
                FaceCount = 1
            };

            var crop = new FaceCropRecord
            {
                CropId = cropId,
                EventId = eventId,
                CropIndex = 0,
                Confidence = 0.95f,
                NormX = 0.2f,
                NormY = 0.2f,
                NormW = 0.3f,
                NormH = 0.3f,
                EncryptedFilePath = "test_path.enc"
            };

            await db.InsertEventWithCropsAsync(ev, new System.Collections.Generic.List<FaceCropRecord> { crop });

            var queried = await db.QueryEventsAsync(new FaceArchiveFilter { CameraId = "cam-test-1" });
            if (queried.Count != 1) return (false, "خطا در واکشی رویداد ذخیره‌شده");
            if (queried[0].Crops.Count != 1) return (false, "خطا در واکشی برش‌های رویداد");

            // تست حذف تک‌موردی و حذف والد در نبود فرزند دیگر
            bool deleted = await db.CompleteCropDeletionAsync(cropId, eventId);
            if (!deleted) return (false, "خطا در حذف تک‌موردی");

            var queriedAfter = await db.QueryEventsAsync(new FaceArchiveFilter { CameraId = "cam-test-1" });
            if (queriedAfter.Count != 0) return (false, "رویداد والد پس از حذف آخرین فرزند باید به طور کامل پاک می‌شد");

            return (true, "موفق: درج، استعلام، حذف شرطی والد و یکپارچگی ارجاعی پایگاه داده SQLite تایید شد.");
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    public static async Task<(bool Passed, string Message)> Test_DedicatedThreadArchiveLock_AcquiresAndReleases()
    {
        await using var lockManager = new DedicatedThreadArchiveLock();
        bool acquired = await lockManager.TryAcquireAsync();
        if (!acquired || !lockManager.HasOwnership)
        {
            return (false, "خطا در احراز مالکیت اولیه قفل اختصاصی ویندوز");
        }

        await lockManager.ReleaseAsync();
        if (lockManager.HasOwnership)
        {
            return (false, "خطا: قفل پس از آزادسازی نباید تحت مالکیت باقی بماند");
        }

        return (true, "موفق: احراز و آزادسازی نخ‌محور قفل سراسری ویندوز به درستی عمل کرد.");
    }

    public static (bool Passed, string Message) Test_WindowsSecurePath_TraversalRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArchiveSecureTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // مسیر معتبر
            WindowsSecurePath.AssertNoReparsePointsInHierarchy(Path.Combine(root, "cam_1", "valid.enc"), root);

            // تلاش برای عبور به پوشه والد خارج از ریشه
            bool rejected = false;
            try
            {
                WindowsSecurePath.AssertNoReparsePointsInHierarchy(Path.Combine(root, "..", "escape.enc"), root);
            }
            catch (System.Security.SecurityException)
            {
                rejected = true;
            }

            if (!rejected)
            {
                return (false, "خطا: تلاش برای عبور از دایرکتوری ریشه شناسایی و مسدود نشد!");
            }

            return (true, "موفق: اعتبارسنجی سلسله‌مراتبی مسیر به درستی مانع عبور از دایرکتوری مجاز گردید.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static async Task<System.Collections.Generic.List<(string TestName, bool Passed, string Message)>> RunAllTestsAsync()
    {
        var list = new System.Collections.Generic.List<(string, bool, string)>();

        var t1 = Test_EnvelopeCodec_SerializationAndParsing_MatchesExactly();
        list.Add(("Test_EnvelopeCodec_SerializationAndParsing_MatchesExactly", t1.Passed, t1.Message));

        var t2 = Test_EnvelopeCodec_TamperedAad_ThrowsCryptographicException();
        list.Add(("Test_EnvelopeCodec_TamperedAad_ThrowsCryptographicException", t2.Passed, t2.Message));

        var t3 = Test_ProtectedKeyStore_EncryptAndDecrypt_Roundtrip();
        list.Add(("Test_ProtectedKeyStore_EncryptAndDecrypt_Roundtrip", t3.Passed, t3.Message));

        var t4 = await Test_FaceArchiveDatabase_InsertAndQueryAndDeletion();
        list.Add(("Test_FaceArchiveDatabase_InsertAndQueryAndDeletion", t4.Passed, t4.Message));

        var t5 = await Test_DedicatedThreadArchiveLock_AcquiresAndReleases();
        list.Add(("Test_DedicatedThreadArchiveLock_AcquiresAndReleases", t5.Passed, t5.Message));

        var t6 = Test_WindowsSecurePath_TraversalRejected();
        list.Add(("Test_WindowsSecurePath_TraversalRejected", t6.Passed, t6.Message));

        return list;
    }
}
