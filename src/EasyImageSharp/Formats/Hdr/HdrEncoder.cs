using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Hdr;

/// <summary>How <see cref="HdrEncoder"/> stores scanlines.</summary>
public enum HdrCompression
{
    /// <summary>Flat RGBE pixels, four bytes each.</summary>
    None = 0,

    /// <summary>
    /// Radiance's new-style run-length encoding, one plane per component. Scanlines narrower than 8 or wider
    /// than 32767 pixels cannot use it and are written flat.
    /// </summary>
    RunLength = 1,
}

/// <summary>
/// Encodes images as Radiance HDR (<c>#?RADIANCE</c>, <c>FORMAT=32-bit_rle_rgbe</c>, standard <c>-Y +X</c>
/// orientation). Only the first frame is written, and alpha is discarded because the format has none.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RgbaVector"/> pixels are written as linear values; every other pixel format is taken to be
/// sRGB-encoded and is linearised with the inverse sRGB transfer function first. Negative components and NaN are
/// written as zero, and values beyond the format's range (about 1.7e38) saturate.
/// </para>
/// <para>
/// Each pixel shares one exponent between its three components, chosen from the largest; mantissas are rounded
/// to nearest, so the brightest component of every pixel is stored to within half a mantissa step (at most
/// 0.4 % of its value) and darker components of the same pixel to the same absolute step, which is coarse
/// relative to a component far darker than its neighbours in the pixel.
/// </para>
/// </remarks>
public sealed class HdrEncoder : IImageEncoder
{
    /// <summary>The scanline encoding. Defaults to <see cref="HdrCompression.RunLength"/>.</summary>
    public HdrCompression Compression { get; init; } = HdrCompression.RunLength;

    /// <inheritdoc/>
    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        if (this.Compression is not (HdrCompression.None or HdrCompression.RunLength))
        {
            throw new ArgumentOutOfRangeException(nameof(this.Compression), this.Compression, "Unknown Radiance HDR compression.");
        }

        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        int width = frame.Width;
        int height = frame.Height;
        string header = string.Create(CultureInfo.InvariantCulture, $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n");
        stream.Write(Encoding.ASCII.GetBytes(header));

        bool runLength = this.Compression == HdrCompression.RunLength && width is >= 8 and <= 0x7FFF;
        Vector4[] linear = ArrayPool<Vector4>.Shared.Rent(width);
        byte[] rgbe = ArrayPool<byte>.Shared.Rent(width * 4);

        // Worst case for one RLE plane is a literal marker per 128 bytes on top of the bytes themselves.
        int outputCapacity = runLength ? 4 + (4 * (width + (width / 128) + 1)) : width * 4;
        byte[] output = ArrayPool<byte>.Shared.Rent(outputCapacity);
        try
        {
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                SceneLinearPixels.ToLinear<TPixel>(frame.GetRowSpan(y), linear.AsSpan(0, width));
                for (int x = 0; x < width; x++)
                {
                    WriteRgbe(linear[x], rgbe.AsSpan(x * 4, 4));
                }

                int count = runLength
                    ? EncodeRunLength(rgbe.AsSpan(0, width * 4), width, output)
                    : EncodeFlat(rgbe.AsSpan(0, width * 4), width, output);
                stream.Write(output, 0, count);
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(linear);
            ArrayPool<byte>.Shared.Return(rgbe);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>Packs a linear colour into a shared-exponent RGBE quad with round-to-nearest mantissas.</summary>
    internal static void WriteRgbe(Vector4 color, Span<byte> quad)
    {
        double r = Sanitize(color.X), g = Sanitize(color.Y), b = Sanitize(color.Z);
        double max = Math.Max(r, Math.Max(g, b));
        if (max < 1e-32)
        {
            quad[0] = quad[1] = quad[2] = quad[3] = 0;
            return;
        }

        // max = f * 2^exponent with f in [0.5, 1), as frexp would report it.
        int exponent = Math.ILogB(max) + 1;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            double scale = Math.ScaleB(1.0, 8 - exponent);
            int mr = (int)((r * scale) + 0.5), mg = (int)((g * scale) + 0.5), mb = (int)((b * scale) + 0.5);
            if (Math.Max(mr, Math.Max(mg, mb)) > 255)
            {
                // Rounding carried the largest mantissa to 256; one exponent step up makes it 128.
                exponent++;
                continue;
            }

            int biased = exponent + 128;
            if (biased > 255)
            {
                quad[0] = quad[1] = quad[2] = quad[3] = 255;
                return;
            }

            if (biased < 1)
            {
                quad[0] = quad[1] = quad[2] = quad[3] = 0;
                return;
            }

            quad[0] = (byte)mr;
            quad[1] = (byte)mg;
            quad[2] = (byte)mb;
            quad[3] = (byte)biased;
            return;
        }

        quad[0] = quad[1] = quad[2] = quad[3] = 255;
    }

    private static double Sanitize(float value)
        => value > 0f ? (float.IsPositiveInfinity(value) ? float.MaxValue : value) : 0.0;

    /// <summary>
    /// Writes flat quads. Radiance readers take a <c>(1, 1, 1, e)</c> pixel for an old-style run marker and a
    /// leading <c>(2, 2, b &lt; 128, e)</c> for a new-style one, but neither shape can occur here: the largest
    /// mantissa of every non-zero quad <see cref="WriteRgbe"/> produces is at least 128.
    /// </summary>
    private static int EncodeFlat(ReadOnlySpan<byte> rgbe, int width, Span<byte> output)
    {
        rgbe[..(width * 4)].CopyTo(output);
        return width * 4;
    }

    /// <summary>
    /// New-style RLE: a <c>2, 2, width</c> marker, then each component plane as runs of at least four equal bytes
    /// (<c>128 + count</c>, value; at most 127) and literal stretches (<c>count</c>, bytes; at most 128).
    /// </summary>
    private static int EncodeRunLength(ReadOnlySpan<byte> rgbe, int width, Span<byte> output)
    {
        output[0] = 2;
        output[1] = 2;
        output[2] = (byte)(width >> 8);
        output[3] = (byte)width;
        int p = 4;
        Span<byte> plane = width <= 1024 ? stackalloc byte[width] : new byte[width];
        for (int channel = 0; channel < 4; channel++)
        {
            for (int x = 0; x < width; x++)
            {
                plane[x] = rgbe[(x * 4) + channel];
            }

            int i = 0;
            while (i < width)
            {
                // Find the next run worth encoding as a run; shorter repeats stay inside literals.
                int runStart = i;
                int runLength = 0;
                while (runStart < width)
                {
                    runLength = 1;
                    while (runStart + runLength < width && runLength < 127 && plane[runStart + runLength] == plane[runStart])
                    {
                        runLength++;
                    }

                    if (runLength >= 4)
                    {
                        break;
                    }

                    runStart += runLength;
                }

                while (i < runStart)
                {
                    int count = Math.Min(128, runStart - i);
                    output[p++] = (byte)count;
                    plane.Slice(i, count).CopyTo(output[p..]);
                    p += count;
                    i += count;
                }

                if (runStart < width)
                {
                    output[p++] = (byte)(128 + runLength);
                    output[p++] = plane[runStart];
                    i = runStart + runLength;
                }
            }
        }

        return p;
    }
}
