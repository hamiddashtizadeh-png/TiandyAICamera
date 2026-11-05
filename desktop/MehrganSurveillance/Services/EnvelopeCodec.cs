using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MehrganSurveillance.Services;

using MehrganSurveillance.Models;

/// <summary>
/// کدک استاندارد باینری انولوپ رمزگذاری‌شده چهره (Canonical Binary Envelope Codec)
/// تضمین انطباق کامل سربرگ و AAD در کل چرخه رمزگذاری، بازیابی و آزمون‌ها
/// </summary>
public static class EnvelopeCodec
{
    public static readonly ReadOnlyMemory<byte> Magic = new byte[] { 0x4D, 0x46, 0x45, 0x4E, 0x43 }; // "MFENC"
    public const byte Version = 0x01;
    public const int BaseHeaderLength = 48; // 16(Guid)+2(Index)+8(Ticks)+2(CamLen)+4(NormX)+4(NormY)+4(NormW)+4(NormH)+4(Conf) = 48

    public static byte[] SerializeHeader(FaceCropMetadata meta)
    {
        if (meta == null) throw new ArgumentNullException(nameof(meta));
        byte[] camBytes = Encoding.UTF8.GetBytes(meta.CameraId);
        if (camBytes.Length is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(meta.CameraId), "CameraId must be 1..256 bytes.");
        if (meta.EventId == Guid.Empty) throw new ArgumentException("EventId cannot be empty.", nameof(meta.EventId));
        if (meta.TimestampUtcTicks < 638712672000000000L) throw new ArgumentOutOfRangeException(nameof(meta.TimestampUtcTicks), "Timestamp cannot precede 2026-01-01.");

        ValidateGeometry(meta.NormX, meta.NormY, meta.NormW, meta.NormH, meta.Confidence);

        ushort headerLength = (ushort)(BaseHeaderLength + camBytes.Length);
        byte[] header = new byte[headerLength];
        var span = header.AsSpan();

        meta.EventId.TryWriteBytes(span.Slice(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(16, 2), meta.CropIndex);
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(18, 8), meta.TimestampUtcTicks);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(26, 2), (ushort)camBytes.Length);
        camBytes.CopyTo(span.Slice(28, camBytes.Length));

        int offset = 28 + camBytes.Length;
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset, 4), meta.NormX);
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset + 4, 4), meta.NormY);
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset + 8, 4), meta.NormW);
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset + 12, 4), meta.NormH);
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset + 16, 4), meta.Confidence);

        return header;
    }

    public static byte[] ConstructAad(ushort keyId, ushort headerLength, ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length != headerLength)
            throw new ArgumentException("Header length mismatch during AAD construction.");

        byte[] aad = new byte[10 + headerLength];
        Magic.Span.CopyTo(aad.AsSpan(0, 5));
        aad[5] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(6, 2), keyId);
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(8, 2), headerLength);
        headerBytes.CopyTo(aad.AsSpan(10));
        return aad;
    }

    public static FaceCropMetadata ParseHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < BaseHeaderLength + 1)
            throw new InvalidDataException("Header buffer shorter than minimal specification.");

        var eventId = new Guid(headerBytes.Slice(0, 16), bigEndian: true);
        if (eventId == Guid.Empty) throw new InvalidDataException("Empty EventId is invalid.");

        ushort cropIndex = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.Slice(16, 2));
        long ticks = BinaryPrimitives.ReadInt64BigEndian(headerBytes.Slice(18, 8));
        if (ticks < 638712672000000000L) throw new InvalidDataException("Timestamp out of bounds.");

        ushort camLen = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.Slice(26, 2));
        if (camLen is < 1 or > 256) throw new InvalidDataException("CameraId length out of bounds.");

        if (headerBytes.Length != BaseHeaderLength + camLen)
            throw new InvalidDataException($"HeaderLength mismatch: expected {BaseHeaderLength + camLen}, got {headerBytes.Length}.");

        var utf8Decoder = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string cameraId = utf8Decoder.GetString(headerBytes.Slice(28, camLen));

        int offset = 28 + camLen;
        float nx = BinaryPrimitives.ReadSingleBigEndian(headerBytes.Slice(offset, 4));
        float ny = BinaryPrimitives.ReadSingleBigEndian(headerBytes.Slice(offset + 4, 4));
        float nw = BinaryPrimitives.ReadSingleBigEndian(headerBytes.Slice(offset + 8, 4));
        float nh = BinaryPrimitives.ReadSingleBigEndian(headerBytes.Slice(offset + 12, 4));
        float conf = BinaryPrimitives.ReadSingleBigEndian(headerBytes.Slice(offset + 16, 4));

        ValidateGeometry(nx, ny, nw, nh, conf);

        return new FaceCropMetadata(eventId, cropIndex, ticks, cameraId, nx, ny, nw, nh, conf);
    }

    public static void ValidateGeometry(float x, float y, float w, float h, float c)
    {
        if (float.IsNaN(x) || float.IsInfinity(x) || x is < 0.0f or > 1.0f) throw new ArgumentOutOfRangeException(nameof(x), "NormX invalid.");
        if (float.IsNaN(y) || float.IsInfinity(y) || y is < 0.0f or > 1.0f) throw new ArgumentOutOfRangeException(nameof(y), "NormY invalid.");
        if (float.IsNaN(w) || float.IsInfinity(w) || w is <= 0.0f or > 1.0f) throw new ArgumentOutOfRangeException(nameof(w), "NormW invalid.");
        if (float.IsNaN(h) || float.IsInfinity(h) || h is <= 0.0f or > 1.0f) throw new ArgumentOutOfRangeException(nameof(h), "NormH invalid.");
        if (float.IsNaN(c) || float.IsInfinity(c) || c is < 0.0f or > 1.0f) throw new ArgumentOutOfRangeException(nameof(c), "Confidence invalid.");

        if (x + w > 1.0f) throw new ArgumentOutOfRangeException("NormX + NormW exceeds 1.0.");
        if (y + h > 1.0f) throw new ArgumentOutOfRangeException("NormY + NormH exceeds 1.0.");
    }
}
