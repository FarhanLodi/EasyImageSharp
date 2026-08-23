using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Accumulates the metadata segments of a JPEG stream (APP0 JFIF, APP1 EXIF/XMP, APP2 ICC, COM, DQT quality)
/// into an <see cref="ImageMetadata"/>. Shared by <see cref="JpegDecoder"/>'s decode and identify paths.
/// Segments that are malformed are ignored; only the size caps raise <see cref="InvalidImageContentException"/>.
/// </summary>
internal sealed class JpegMetadataReader
{
    private static readonly byte[] JfifIdentifier = { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0 };
    private static readonly byte[] ExifIdentifier = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
    private static readonly byte[] XmpIdentifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] IccIdentifier = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    private readonly ImageMetadata metadata = new();
    private readonly JpegMetadata jpeg;
    private readonly SortedDictionary<int, byte[]> iccChunks = new();
    private int iccChunkCount = -1;
    private long iccTotal;
    private bool exifSeen;
    private bool xmpSeen;

    public JpegMetadataReader()
    {
        this.jpeg = this.metadata.GetJpegMetadata();
        this.metadata.DecodedImageFormat = ImageFormat.Jpeg;
    }

    /// <summary>Records a marker segment's payload (the bytes after the two-byte length).</summary>
    public void ProcessSegment(byte marker, ReadOnlySpan<byte> payload)
    {
        switch (marker)
        {
            case 0xE0:
                this.ReadJfif(payload);
                break;
            case 0xE1:
                if (StartsWith(payload, ExifIdentifier))
                {
                    this.ReadExif(payload[ExifIdentifier.Length..]);
                }
                else if (StartsWith(payload, XmpIdentifier))
                {
                    this.ReadXmp(payload[XmpIdentifier.Length..]);
                }

                break;
            case 0xE2:
                if (StartsWith(payload, IccIdentifier))
                {
                    this.ReadIccChunk(payload[IccIdentifier.Length..]);
                }

                break;
            case 0xFE:
                this.jpeg.Comments.Add(ExifReader.DecodeUtf8OrLatin1(payload));
                break;
        }
    }

    /// <summary>Records the frame facts once the SOF segment has been parsed.</summary>
    public void SetFrame(bool progressive, JpegColorType colorType)
    {
        this.jpeg.Progressive = progressive;
        this.jpeg.ColorType = colorType;
    }

    /// <summary>Estimates the encoding quality from the first (luminance) quantization table in natural order.</summary>
    public void SetLuminanceQuantTable(ushort[]? table)
    {
        if (table is not null)
        {
            this.jpeg.Quality = EstimateQuality(table);
        }
    }

    /// <summary>Assembles the accumulated segments and returns the metadata.</summary>
    public ImageMetadata Finish()
    {
        if (this.iccChunkCount > 0 && this.iccChunks.Count == this.iccChunkCount)
        {
            bool complete = true;
            for (int i = 1; i <= this.iccChunkCount; i++)
            {
                if (!this.iccChunks.ContainsKey(i))
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                var icc = new byte[this.iccTotal];
                int pos = 0;
                foreach (byte[] chunk in this.iccChunks.Values)
                {
                    chunk.CopyTo(icc, pos);
                    pos += chunk.Length;
                }

                this.metadata.IccProfile = new IccProfile(icc);
            }
        }

        // The EXIF resolution tags win over the JFIF density when both are present.
        if (this.metadata.ExifProfile is not null)
        {
            this.metadata.ApplyExifResolution(this.metadata.ExifProfile);
        }

        return this.metadata;
    }

    /// <summary>
    /// Inverts the ITU-T T.81 Annex K quality scaling used by this library, libjpeg and most encoders: the quality
    /// whose scaled standard luminance table reproduces <paramref name="table"/> exactly, or the closest quality by
    /// the table's overall scale when no exact match exists.
    /// </summary>
    internal static int EstimateQuality(ushort[] table)
    {
        for (int q = 100; q >= 1; q--)
        {
            int scale = q < 50 ? 5000 / q : 200 - (q * 2);
            bool match = true;
            for (int i = 0; i < 64; i++)
            {
                int expected = Math.Clamp(((JpegTables.StdLuminanceQuant[i] * scale) + 50) / 100, 1, 255);
                if (expected != table[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return q;
            }
        }

        long sum = 0;
        long stdSum = 0;
        for (int i = 0; i < 64; i++)
        {
            sum += table[i];
            stdSum += JpegTables.StdLuminanceQuant[i];
        }

        double scaleEstimate = sum * 100.0 / stdSum;
        double quality = scaleEstimate <= 100 ? (200 - scaleEstimate) / 2 : 5000 / scaleEstimate;
        return Math.Clamp((int)Math.Round(quality), 1, 100);
    }

    private void ReadJfif(ReadOnlySpan<byte> payload)
    {
        // JFIF APP0: "JFIF\0" ver(2) units(1) xdensity(2) ydensity(2) xthumb(1) ythumb(1) ...
        if (!StartsWith(payload, JfifIdentifier) || payload.Length < 12)
        {
            return;
        }

        byte units = payload[7];
        int x = BinaryPrimitives.ReadUInt16BigEndian(payload[8..]);
        int y = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
        if (x <= 0 || y <= 0)
        {
            return;
        }

        this.metadata.ResolutionUnits = units switch
        {
            1 => PixelResolutionUnit.PixelsPerInch,
            2 => PixelResolutionUnit.PixelsPerCentimeter,
            _ => PixelResolutionUnit.AspectRatio,
        };
        this.metadata.HorizontalResolution = x;
        this.metadata.VerticalResolution = y;
    }

    private void ReadExif(ReadOnlySpan<byte> payload)
    {
        if (this.exifSeen)
        {
            return; // Only the first APP1 EXIF segment counts.
        }

        this.exifSeen = true;
        this.metadata.ExifProfile = ExifProfile.TryParse(payload);
    }

    private void ReadXmp(ReadOnlySpan<byte> payload)
    {
        if (this.xmpSeen)
        {
            return;
        }

        this.xmpSeen = true;
        this.metadata.XmpProfile = new XmpProfile(payload.ToArray());
    }

    private void ReadIccChunk(ReadOnlySpan<byte> payload)
    {
        // seq(1, 1-based) count(1) data
        if (payload.Length < 2)
        {
            return;
        }

        int sequence = payload[0];
        int count = payload[1];
        if (sequence == 0 || count == 0 || sequence > count)
        {
            return;
        }

        if (this.iccChunkCount == -1)
        {
            this.iccChunkCount = count;
        }
        else if (this.iccChunkCount != count)
        {
            return; // Inconsistent chunk counts: ignore the stray segment.
        }

        if (this.iccChunks.ContainsKey(sequence))
        {
            return;
        }

        this.iccTotal += payload.Length - 2;
        if (this.iccTotal > ExifReader.MaxIccBytes)
        {
            throw new InvalidImageContentException($"Embedded ICC profile exceeds the {ExifReader.MaxIccBytes:N0} byte limit.");
        }

        this.iccChunks[sequence] = payload[2..].ToArray();
    }

    private static bool StartsWith(ReadOnlySpan<byte> payload, byte[] identifier)
        => payload.Length >= identifier.Length && payload[..identifier.Length].SequenceEqual(identifier);
}
