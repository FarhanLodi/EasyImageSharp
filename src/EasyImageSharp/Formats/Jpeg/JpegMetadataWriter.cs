using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Writes the metadata segments of a JPEG file: APP0 JFIF (with the density taken from the image resolution),
/// APP1 EXIF, APP1 XMP, APP2 ICC (chunked at 65 519 bytes) and COM comments.
/// </summary>
internal static class JpegMetadataWriter
{
    /// <summary>The largest APP segment payload: 65 535 minus the two length bytes.</summary>
    private const int MaxSegmentPayload = 65533;

    /// <summary>The largest ICC chunk payload after the 12-byte "ICC_PROFILE\0" identifier and the two counter bytes.</summary>
    private const int MaxIccChunk = 65519;

    private static readonly byte[] ExifIdentifier = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
    private static readonly byte[] XmpIdentifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] IccIdentifier = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    /// <summary>Writes APP0 (JFIF), APP1 (EXIF, XMP), APP2 (ICC) and COM segments right after the SOI marker.</summary>
    /// <param name="stream">The stream positioned just after the SOI marker.</param>
    /// <param name="metadata">The metadata to write.</param>
    /// <param name="writeJfif">
    /// Whether to lead with the JFIF APP0 segment. False for frames whose colour model JFIF does not describe
    /// (RGB, CMYK and YCCK), which carry an Adobe APP14 segment instead.
    /// </param>
    public static void Write(Stream stream, ImageMetadata metadata, bool writeJfif = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(metadata);

        if (writeJfif)
        {
            WriteSegment(stream, 0xE0, JfifPayload(metadata));
        }

        ExifProfile? exif = metadata.PrepareExifForWrite();
        if (exif is not null)
        {
            byte[] tiff = exif.ToByteArray();
            if (tiff.Length + ExifIdentifier.Length <= MaxSegmentPayload)
            {
                WriteSegment(stream, 0xE1, Concat(ExifIdentifier, tiff));
            }
            else if (exif.Thumbnail is not null)
            {
                // Try again without the thumbnail, which is by far the largest optional part.
                ExifProfile slim = exif.DeepClone();
                slim.Thumbnail = null;
                byte[] slimTiff = slim.ToByteArray();
                if (slimTiff.Length + ExifIdentifier.Length <= MaxSegmentPayload)
                {
                    WriteSegment(stream, 0xE1, Concat(ExifIdentifier, slimTiff));
                }
            }
        }

        if (metadata.XmpProfile is not null)
        {
            ReadOnlySpan<byte> xmp = metadata.XmpProfile.RawData;
            if (xmp.Length + XmpIdentifier.Length <= MaxSegmentPayload)
            {
                WriteSegment(stream, 0xE1, Concat(XmpIdentifier, xmp));
            }
        }

        if (metadata.IccProfile is not null)
        {
            ReadOnlySpan<byte> icc = metadata.IccProfile.RawData;
            int chunkCount = Math.Max(1, (icc.Length + MaxIccChunk - 1) / MaxIccChunk);
            if (chunkCount <= 255)
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    int start = i * MaxIccChunk;
                    ReadOnlySpan<byte> chunk = icc.Slice(start, Math.Min(MaxIccChunk, icc.Length - start));
                    var payload = new byte[IccIdentifier.Length + 2 + chunk.Length];
                    IccIdentifier.CopyTo(payload, 0);
                    payload[IccIdentifier.Length] = (byte)(i + 1);
                    payload[IccIdentifier.Length + 1] = (byte)chunkCount;
                    chunk.CopyTo(payload.AsSpan(IccIdentifier.Length + 2));
                    WriteSegment(stream, 0xE2, payload);
                }
            }
        }

        if (metadata.TryGetFormatMetadata(out JpegMetadata? jpeg))
        {
            foreach (string comment in jpeg.Comments)
            {
                byte[] text = Encoding.UTF8.GetBytes(comment ?? string.Empty);
                if (text.Length <= MaxSegmentPayload)
                {
                    WriteSegment(stream, 0xFE, text);
                }
            }
        }
    }

    /// <summary>The 14-byte JFIF APP0 payload with density units and values derived from the metadata resolution.</summary>
    internal static byte[] JfifPayload(ImageMetadata metadata)
    {
        (byte units, double x, double y) = metadata.ResolutionUnits switch
        {
            PixelResolutionUnit.AspectRatio => ((byte)0, metadata.HorizontalResolution, metadata.VerticalResolution),
            PixelResolutionUnit.PixelsPerCentimeter => ((byte)2, metadata.HorizontalResolution, metadata.VerticalResolution),
            _ => ((byte)1, metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch)),
        };

        ushort xd = (ushort)Math.Clamp((int)Math.Round(x), 1, ushort.MaxValue);
        ushort yd = (ushort)Math.Clamp((int)Math.Round(y), 1, ushort.MaxValue);
        var payload = new byte[14];
        payload[0] = (byte)'J';
        payload[1] = (byte)'F';
        payload[2] = (byte)'I';
        payload[3] = (byte)'F';
        payload[4] = 0;
        payload[5] = 1; // Version 1.01
        payload[6] = 1;
        payload[7] = units;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), xd);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10), yd);
        return payload;
    }

    private static byte[] Concat(byte[] prefix, ReadOnlySpan<byte> data)
    {
        var result = new byte[prefix.Length + data.Length];
        prefix.CopyTo(result, 0);
        data.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private static void WriteSegment(Stream stream, byte marker, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
        int length = payload.Length + 2;
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(payload);
    }
}
