using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Hdr;

/// <summary>
/// Decodes Radiance HDR pictures (<c>.hdr</c>, <c>.pic</c>): RGBE and XYZE pixels, new-style run-length
/// encoded, old-style run-length encoded and flat scanlines, and all eight resolution-line orientations.
/// </summary>
/// <remarks>
/// <para>
/// A pixel with mantissas <c>m</c> and exponent byte <c>e</c> decodes to <c>m * 2^(e - 136)</c> per channel,
/// and to zero when <c>e</c> is zero. That is the conversion of the widely used RGBE reference code, which maps
/// exactly representable values (1.0, 0.5, ...) back to themselves; Radiance's own tools add half a mantissa
/// step, a difference of at most 0.2 %.
/// </para>
/// <para>
/// XYZE pixels are taken as CIE XYZ relative to Radiance's equal-energy white, adapted to D65 with the Bradford
/// transform and converted to linear sRGB (Rec. 709 primaries), so XYZ (1, 1, 1) becomes RGB (1, 1, 1). RGBE
/// pixels are passed through; a <c>PRIMARIES</c> line is reported in <see cref="HdrMetadata"/> but does not
/// convert them.
/// </para>
/// <para>
/// <see cref="RgbaVector"/> receives the scene-linear values at full precision with alpha 1, unclamped. Every
/// other pixel format is integer-backed and, by the library's convention, sRGB-encoded, so its colour
/// components receive the linear values clamped to 0..1 and passed through the IEC 61966-2-1 sRGB transfer
/// function, then rounded to the format's precision. That clips everything brighter than 1; tone map an
/// <see cref="RgbaVector"/> image instead for a display rendering that keeps the highlights.
/// </para>
/// </remarks>
public sealed class HdrDecoder : IImageDecoder
{
    internal const string FormatName = "Radiance HDR";

    /// <summary>
    /// Linear sRGB from CIE XYZ relative to an equal-energy white: the Bradford adaptation from E to D65
    /// followed by the IEC 61966-2-1 XYZ-to-linear-sRGB matrix, evaluated in double precision. Each row sums to 1.
    /// </summary>
    private static readonly float[] XyzToLinearSrgb =
    {
        3.146657654151f, -1.666463754524f, -0.480193899627f,
        -0.99552160578f, 1.955756733808f, 0.039764871972f,
        0.063594802939f, -0.214563222225f, 1.150968419286f,
    };

    /// <summary>
    /// When true, pixel values are divided by the cumulative <c>EXPOSURE</c> and <c>COLORCORR</c> factors of the
    /// header, recovering the original radiance. Defaults to false: values are returned as stored, which is what
    /// other readers do.
    /// </summary>
    public bool ApplyExposure { get; init; }

    /// <inheritdoc/>
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            return this.DecodeCore<TPixel>(data, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap(FormatName, ex);
        }
    }

    /// <inheritdoc/>
    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            HdrHeader header = HdrHeader.Parse(data);
            return new ImageInfo(header.Width, header.Height, 32, 1, ImageFormat.Hdr, CreateMetadata(header));
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap(FormatName, ex);
        }
    }

    private static ImageMetadata CreateMetadata(HdrHeader header)
    {
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Hdr };
        metadata.SetFormatMetadata(header.Metadata);
        return metadata;
    }

    private Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        HdrHeader header = HdrHeader.Parse(data);
        int width = header.Width;
        int height = header.Height;
        int scanlines = header.ScanlineCount;
        int length = header.ScanlineLength;

        // Every scanline costs at least one four-byte pixel or run marker, so a header that promises more
        // scanlines than the remaining bytes can hold is rejected before anything is allocated.
        if ((long)scanlines * 4 > data.Length - header.DataOffset)
        {
            throw new InvalidImageContentException("Radiance HDR pixel data is truncated.");
        }

        options.EnsureFrameWithinLimits(width, height, FormatName);

        Vector4 scale = Vector4.One;
        if (this.ApplyExposure)
        {
            Vector3 correction = header.Metadata.ColorCorrection * (float)header.Metadata.Exposure;
            scale = new Vector4(1f / correction.X, 1f / correction.Y, 1f / correction.Z, 1f);
        }

        bool xyz = header.Metadata.ColorFormat == HdrColorFormat.Xyze;
        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(width, height);
        Span<TPixel> pixels = frame.PixelSpan;
        byte[] scanline = ArrayPool<byte>.Shared.Rent(checked(length * 4));
        try
        {
            Span<byte> rgbe = scanline.AsSpan(0, length * 4);
            int pos = header.DataOffset;
            bool havePrevious = false;
            for (int s = 0; s < scanlines; s++)
            {
                OperationExecution.Checkpoint();
                pos = ReadScanline(data, pos, rgbe, length, havePrevious);
                havePrevious = true;
                for (int p = 0; p < length; p++)
                {
                    Vector4 linear = ToLinear(rgbe.Slice(p * 4, 4), xyz) * scale;
                    int x, y;
                    if (header.XMajor)
                    {
                        x = header.FirstDecreasing ? width - 1 - s : s;
                        y = header.SecondDecreasing ? p : height - 1 - p;
                    }
                    else
                    {
                        y = header.FirstDecreasing ? s : height - 1 - s;
                        x = header.SecondDecreasing ? width - 1 - p : p;
                    }

                    pixels[(y * width) + x] = SceneLinearPixels.FromLinear<TPixel>(linear);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanline);
        }

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, CreateMetadata(header));
    }

    /// <summary>Converts one RGBE or XYZE quad into linear RGB with an alpha of 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector4 ToLinear(ReadOnlySpan<byte> quad, bool xyz)
    {
        byte e = quad[3];
        if (e == 0)
        {
            return new Vector4(0f, 0f, 0f, 1f);
        }

        // Exact: an 8-bit mantissa times a power of two is representable even in the subnormal range,
        // because the smallest factor, 2^-135, still leaves eight bits above 2^-149.
        float f = MathF.ScaleB(1f, e - 136);
        float a = quad[0] * f, b = quad[1] * f, c = quad[2] * f;
        if (!xyz)
        {
            return new Vector4(a, b, c, 1f);
        }

        float[] m = XyzToLinearSrgb;
        return new Vector4(
            (m[0] * a) + (m[1] * b) + (m[2] * c),
            (m[3] * a) + (m[4] * b) + (m[5] * c),
            (m[6] * a) + (m[7] * b) + (m[8] * c),
            1f);
    }

    /// <summary>
    /// Reads one scanline of <paramref name="length"/> pixels into <paramref name="rgbe"/> (interleaved quads)
    /// and returns the offset just past it. New-style run-length encoding is recognised exactly as Radiance does
    /// it (a <c>2, 2</c> marker whose length word matches, for scanlines of 8 to 32767 pixels); anything else is
    /// read as old-style data, of which flat pixels are the run-free case.
    /// </summary>
    private static int ReadScanline(ReadOnlySpan<byte> data, int pos, Span<byte> rgbe, int length, bool havePrevious)
    {
        if (length is >= 8 and <= 0x7FFF
            && pos + 4 <= data.Length
            && data[pos] == 2 && data[pos + 1] == 2 && (data[pos + 2] & 0x80) == 0)
        {
            int declared = (data[pos + 2] << 8) | data[pos + 3];
            if (declared != length)
            {
                throw new InvalidImageContentException(
                    $"Radiance HDR run-length scanline declares {declared} pixels, but the image has {length} per scanline.");
            }

            return ReadRunLengthScanline(data, pos + 4, rgbe, length);
        }

        return ReadOldStyleScanline(data, pos, rgbe, length, havePrevious);
    }

    /// <summary>
    /// New-style RLE: the four components are stored as separate planes, each a sequence of
    /// <c>count &gt; 128</c> runs (one byte repeated <c>count - 128</c> times) and <c>count &lt;= 128</c> literal
    /// runs (<c>count</c> bytes copied).
    /// </summary>
    private static int ReadRunLengthScanline(ReadOnlySpan<byte> data, int pos, Span<byte> rgbe, int length)
    {
        for (int channel = 0; channel < 4; channel++)
        {
            int j = 0;
            while (j < length)
            {
                if (pos >= data.Length)
                {
                    throw new InvalidImageContentException("Radiance HDR run-length scanline is truncated.");
                }

                int code = data[pos++];
                if (code > 128)
                {
                    int run = code - 128;
                    if (pos >= data.Length)
                    {
                        throw new InvalidImageContentException("Radiance HDR run-length scanline is truncated.");
                    }

                    if (j + run > length)
                    {
                        throw new InvalidImageContentException("Radiance HDR run overruns its scanline.");
                    }

                    byte value = data[pos++];
                    for (int k = 0; k < run; k++, j++)
                    {
                        rgbe[(j * 4) + channel] = value;
                    }
                }
                else
                {
                    if (j + code > length)
                    {
                        throw new InvalidImageContentException("Radiance HDR literal run overruns its scanline.");
                    }

                    if (pos + code > data.Length)
                    {
                        throw new InvalidImageContentException("Radiance HDR run-length scanline is truncated.");
                    }

                    for (int k = 0; k < code; k++, j++)
                    {
                        rgbe[(j * 4) + channel] = data[pos + k];
                    }

                    pos += code;
                }
            }
        }

        return pos;
    }

    /// <summary>
    /// Old-style data: four-byte pixels, where a pixel <c>(1, 1, 1, n)</c> repeats the previous pixel
    /// <c>n &lt;&lt; shift</c> times and consecutive repeat markers raise the shift by 8. A run may start a
    /// scanline (it then repeats the last pixel of the previous scanline) but may not run past its end.
    /// </summary>
    private static int ReadOldStyleScanline(ReadOnlySpan<byte> data, int pos, Span<byte> rgbe, int length, bool havePrevious)
    {
        int j = 0;
        int shift = 0;
        Span<byte> previous = stackalloc byte[4];
        if (havePrevious)
        {
            rgbe.Slice((length - 1) * 4, 4).CopyTo(previous);
        }

        while (j < length)
        {
            if (pos + 4 > data.Length)
            {
                throw new InvalidImageContentException("Radiance HDR scanline is truncated.");
            }

            ReadOnlySpan<byte> quad = data.Slice(pos, 4);
            pos += 4;
            if (quad[0] == 1 && quad[1] == 1 && quad[2] == 1)
            {
                if (j == 0 && !havePrevious)
                {
                    throw new InvalidImageContentException("Radiance HDR run has no preceding pixel to repeat.");
                }

                long count = shift >= 32 ? (quad[3] == 0 ? 0 : long.MaxValue) : (long)quad[3] << shift;
                if (count > length - j)
                {
                    throw new InvalidImageContentException("Radiance HDR run overruns its scanline.");
                }

                ReadOnlySpan<byte> source = j == 0 ? previous : rgbe.Slice((j - 1) * 4, 4);
                uint repeated = (uint)(source[0] | (source[1] << 8) | (source[2] << 16) | (source[3] << 24));
                for (int k = 0; k < (int)count; k++, j++)
                {
                    Span<byte> target = rgbe.Slice(j * 4, 4);
                    target[0] = (byte)repeated;
                    target[1] = (byte)(repeated >> 8);
                    target[2] = (byte)(repeated >> 16);
                    target[3] = (byte)(repeated >> 24);
                }

                shift = Math.Min(shift + 8, 32);
            }
            else
            {
                quad.CopyTo(rgbe.Slice(j * 4, 4));
                j++;
                shift = 0;
            }
        }

        return pos;
    }
}
