using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Assembles the RIFF container of a WebP file (RFC 9649 section 2): the simple lossy and lossless forms, the
/// extended 'VP8X' form with its flags and canvas size, and the alpha, animation, colour-profile and metadata
/// chunks. Every chunk is padded to an even length, as the format requires.
/// </summary>
internal sealed class WebpMuxer
{
    /// <summary>The extended-format flag for an animation ('ANIM'/'ANMF' chunks are present).</summary>
    public const byte FlagAnimation = 0x02;

    /// <summary>The extended-format flag for an XMP packet.</summary>
    public const byte FlagXmp = 0x04;

    /// <summary>The extended-format flag for an EXIF profile.</summary>
    public const byte FlagExif = 0x08;

    /// <summary>The extended-format flag for transparency.</summary>
    public const byte FlagAlpha = 0x10;

    /// <summary>The extended-format flag for an embedded ICC profile.</summary>
    public const byte FlagIccProfile = 0x20;

    private readonly MemoryStream body = new();

    /// <summary>Appends one chunk, padding its payload to an even length.</summary>
    public void WriteChunk(ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> payload) => WriteChunkTo(this.body, fourCc, payload);

    /// <summary>Writes one chunk to an arbitrary stream, padding its payload to an even length.</summary>
    public static void WriteChunkTo(Stream stream, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        fourCc.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    /// <summary>Writes the 'VP8X' chunk that opens every extended-format file.</summary>
    public void WriteVp8X(byte flags, int canvasWidth, int canvasHeight)
    {
        Span<byte> payload = stackalloc byte[10];
        payload.Clear();
        payload[0] = flags;
        WriteUInt24(payload[4..], canvasWidth - 1);
        WriteUInt24(payload[7..], canvasHeight - 1);
        this.WriteChunk("VP8X"u8, payload);
    }

    /// <summary>Writes the 'ANIM' chunk with the canvas background colour and the loop count.</summary>
    public void WriteAnim(uint backgroundColor, int loopCount)
    {
        Span<byte> payload = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, backgroundColor);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], (ushort)loopCount);
        this.WriteChunk("ANIM"u8, payload);
    }

    /// <summary>Writes one 'ANMF' chunk: the frame rectangle, its duration and compositing rules, and its image data.</summary>
    public void WriteAnmf(int x, int y, int width, int height, int durationMs, bool disposeToBackground, bool blend, ReadOnlySpan<byte> frameChunks)
    {
        var payload = new byte[16 + frameChunks.Length];
        Span<byte> header = payload.AsSpan(0, 16);
        WriteUInt24(header, x / 2);
        WriteUInt24(header[3..], y / 2);
        WriteUInt24(header[6..], width - 1);
        WriteUInt24(header[9..], height - 1);
        WriteUInt24(header[12..], durationMs);
        header[15] = (byte)((disposeToBackground ? 0x01 : 0x00) | (blend ? 0x00 : 0x02));
        frameChunks.CopyTo(payload.AsSpan(16));
        this.WriteChunk("ANMF"u8, payload);
    }

    /// <summary>Writes the finished file: the RIFF header, the 'WEBP' form type and every chunk written so far.</summary>
    public void WriteTo(Stream stream)
    {
        byte[] payload = this.body.ToArray();
        Span<byte> header = stackalloc byte[12];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(payload.Length + 4));
        "WEBP"u8.CopyTo(header[8..]);
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>Writes a 24-bit little-endian value.</summary>
    public static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    /// <summary>
    /// Converts straight-alpha RGBA pixels to the 4:2:0 planes a VP8 key frame is built from, using the same
    /// fixed-point BT.601 coefficients as the reference encoder; chroma is the average of each 2x2 block.
    /// </summary>
    /// <param name="pixels">The frame in row-major RGBA order.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="y">Receives the luma plane, stride <paramref name="width"/>.</param>
    /// <param name="u">Receives the chroma-blue plane, stride <c>(width + 1) / 2</c>.</param>
    /// <param name="v">Receives the chroma-red plane, stride <c>(width + 1) / 2</c>.</param>
    public static void ToYuv420(ReadOnlySpan<Rgba32> pixels, int width, int height, out byte[] y, out byte[] u, out byte[] v)
    {
        const int fix = 16;
        const int half = 1 << (fix - 1);
        int uvWidth = (width + 1) / 2;
        int uvHeight = (height + 1) / 2;
        y = new byte[width * height];
        u = new byte[uvWidth * uvHeight];
        v = new byte[uvWidth * uvHeight];

        for (int row = 0; row < height; row++)
        {
            int offset = row * width;
            for (int column = 0; column < width; column++)
            {
                Rgba32 pixel = pixels[offset + column];
                int luma = (16839 * pixel.R) + (33059 * pixel.G) + (6420 * pixel.B);
                y[offset + column] = (byte)Math.Clamp((luma + half + (16 << fix)) >> fix, 0, 255);
            }
        }

        for (int row = 0; row < uvHeight; row++)
        {
            int row0 = row * 2;
            int row1 = Math.Min(row0 + 1, height - 1);
            for (int column = 0; column < uvWidth; column++)
            {
                int column0 = column * 2;
                int column1 = Math.Min(column0 + 1, width - 1);
                Rgba32 a = pixels[(row0 * width) + column0];
                Rgba32 b = pixels[(row0 * width) + column1];
                Rgba32 c = pixels[(row1 * width) + column0];
                Rgba32 d = pixels[(row1 * width) + column1];
                int r = a.R + b.R + c.R + d.R;
                int g = a.G + b.G + c.G + d.G;
                int blue = a.B + b.B + c.B + d.B;
                int index = (row * uvWidth) + column;
                u[index] = ClipUv((-9719 * r) - (19081 * g) + (28800 * blue));
                v[index] = ClipUv((28800 * r) - (24116 * g) - (4684 * blue));
            }
        }
    }

    private static byte ClipUv(int value)
    {
        // The chroma sums cover four pixels, so the rounding and the 128 offset are scaled by four as well.
        const int fix = 16;
        int scaled = (value + (1 << (fix + 1)) + (128 << (fix + 2))) >> (fix + 2);
        return (byte)Math.Clamp(scaled, 0, 255);
    }
}
