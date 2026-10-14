using System.Buffers.Binary;
using EasyImageSharp.Formats;

namespace EasyImageSharp;

/// <summary>Root dimensions only. Unlike ImageInfo, this does not claim frame counts or complete metadata.</summary>
public sealed class ImageHeaderInfo
{
    internal ImageHeaderInfo(int width, int height, ImageFormat format)
    {
        if (width <= 0 || height <= 0) throw new InvalidImageContentException("Invalid header dimensions.");
        this.Width = width;
        this.Height = height;
        this.Format = format;
    }

    public int Width { get; }
    public int Height { get; }
    public Size Size => new(this.Width, this.Height);
    public ImageFormat Format { get; }
}

public abstract partial class Image
{
    /// <summary>Reads only enough bytes to obtain PNG, JPEG, GIF, BMP, QOI or WebP root dimensions.</summary>
    /// <remarks>Consumes the stream through the relevant header and leaves it open. Does not validate pixel payloads,
    /// restore stream position, read metadata or count frames. Other formats throw NotSupportedException;
    /// use Identify for full format support. Header bytes, including skipped JPEG application segments,
    /// are bounded by maxHeaderBytes.</remarks>
    public static ImageHeaderInfo IdentifyHeader(Stream stream, int maxHeaderBytes = 1048576, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeaderBytes);
        byte[] buffer = new byte[Math.Min(64, maxHeaderBytes)];
        int count = 0, required = 12, jpegOffset = 2;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHeaderCapacity(ref buffer, required, maxHeaderBytes);
            while (count < required)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer.AsSpan(count, required - count));
                if (read == 0) throw new InvalidImageContentException("Truncated image header.");
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ImageHeaderInfo? result = ParseHeader(buffer.AsSpan(0, count), ref jpegOffset, out required);
            if (result is not null) return result;
        }
    }

    /// <summary>Asynchronously reads bounded root dimensions; supports the same formats and semantics as IdentifyHeader.</summary>
    public static async Task<ImageHeaderInfo> IdentifyHeaderAsync(Stream stream, int maxHeaderBytes = 1048576, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeaderBytes);
        byte[] buffer = new byte[Math.Min(64, maxHeaderBytes)];
        int count = 0, required = 12, jpegOffset = 2;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHeaderCapacity(ref buffer, required, maxHeaderBytes);
            while (count < required)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count, required - count), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new InvalidImageContentException("Truncated image header.");
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ImageHeaderInfo? result = ParseHeader(buffer.AsSpan(0, count), ref jpegOffset, out required);
            if (result is not null) return result;
        }
    }

    private static void EnsureHeaderCapacity(ref byte[] buffer, int required, int maximum)
    {
        if (required > maximum) throw new ImageSizeLimitExceededException("Image header exceeds maxHeaderBytes.");
        if (required > buffer.Length) Array.Resize(ref buffer, (int)Math.Min(maximum, Math.Max((long)required, buffer.Length * 2L)));
    }

    private static ImageHeaderInfo? ParseHeader(ReadOnlySpan<byte> data, ref int jpegOffset, out int required)
    {
        required = data.Length;
        if (data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            required = 24;
            if (data.Length < required) return null;
            if (BinaryPrimitives.ReadInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidImageContentException("PNG must begin with IHDR.");
            return new(BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]), ImageFormat.Png);
        }
        if (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))
            return new(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), ImageFormat.Gif);
        if (data[..4].SequenceEqual("qoif"u8))
            return new(BinaryPrimitives.ReadInt32BigEndian(data[4..]), BinaryPrimitives.ReadInt32BigEndian(data[8..]), ImageFormat.Qoi);
        if (data[..2].SequenceEqual("BM"u8))
        {
            required = 18;
            if (data.Length < required) return null;
            int dib = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
            required = dib == 12 ? 22 : 26;
            if (dib != 12 && dib < 40) throw new NotSupportedException("Unsupported BMP header variant.");
            if (data.Length < required) return null;
            if (dib == 12) return new(BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]), ImageFormat.Bmp);
            int height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
            if (height == int.MinValue) throw new InvalidImageContentException("Invalid BMP height.");
            return new(BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs(height), ImageFormat.Bmp);
        }
        if (data[0] == 255 && data[1] == 216)
        {
            int offset = jpegOffset;
            while (true)
            {
                required = checked(offset + 2);
                if (data.Length < required) return null;
                if (data[offset++] != 255) throw new InvalidImageContentException("Invalid JPEG marker.");
                while (data[offset] == 255)
                {
                    offset++;
                    jpegOffset = offset - 1;
                    required = checked(offset + 1);
                    if (data.Length < required) return null;
                }
                int marker = data[offset++];
                if (marker is 217 or 218 or 0) throw new InvalidImageContentException("JPEG has no supported size header before image data.");
                if (marker is 1 or >= 208 and <= 215) { jpegOffset = offset; continue; }
                required = checked(offset + 2);
                if (data.Length < required) return null;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                if (length < 2) throw new InvalidImageContentException("Invalid JPEG segment length.");
                if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204))
                {
                    if (length < 8) throw new InvalidImageContentException("Truncated JPEG size segment.");
                    required = checked(offset + 8);
                    if (data.Length < required) return null;
                    return new(BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]), ImageFormat.Jpeg);
                }
                offset = checked(offset + length);
                jpegOffset = offset;
            }
        }
        if (data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            required = 20;
            if (data.Length < required) return null;
            required = data.Slice(12, 4).SequenceEqual("VP8L"u8) ? 25 : 30;
            if (data.Length < required) return null;
            if (data.Slice(12, 4).SequenceEqual("VP8X"u8))
                return new(Read24(data[24..]) + 1, Read24(data[27..]) + 1, ImageFormat.Webp);
            if (data.Slice(12, 4).SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
                return new((int)(packed & 0x3fff) + 1, (int)((packed >> 14) & 0x3fff) + 1, ImageFormat.Webp);
            }
            if (data.Slice(12, 4).SequenceEqual("VP8 "u8) && data.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return new(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3fff, ImageFormat.Webp);
            throw new InvalidImageContentException("Unsupported or malformed WebP header.");
        }
        throw new NotSupportedException("Incremental header identification supports PNG, JPEG, GIF, BMP, QOI and WebP. Use Image.Identify for other formats.");
    }

    private static int Read24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);
}
