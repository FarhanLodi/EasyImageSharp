using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>
/// Sample-format reduction and the colour conversions for the photometric interpretations that are not a
/// straight grey, palette or RGB read: Separated (CMYK), YCbCr and the two Lab encodings.
/// </summary>
/// <remarks>
/// The conversions follow the TIFF 6.0 specification and libtiff's reader behaviour: CMYK is combined
/// multiplicatively with the black ink, YCbCr uses the full-range ITU-R BT.601 transform that matches the
/// default ReferenceBlackWhite, and Lab is taken as CIE L*a*b* on the D50 white point the specification
/// prescribes, converted to sRGB.
/// </remarks>
internal static class TiffColor
{
    /// <summary>PhotometricInterpretation 5: Separated, in practice always CMYK.</summary>
    public const int PhotometricSeparated = 5;

    /// <summary>PhotometricInterpretation 6: YCbCr.</summary>
    public const int PhotometricYCbCr = 6;

    /// <summary>PhotometricInterpretation 8: CIE L*a*b* with signed a and b samples.</summary>
    public const int PhotometricCieLab = 8;

    /// <summary>PhotometricInterpretation 9: ICC L*a*b*, whose a and b samples carry an offset of 128.</summary>
    public const int PhotometricIccLab = 9;

    /// <summary>True for the interpretations <see cref="ConvertRow"/> handles.</summary>
    public static bool IsColorimetric(int photometric)
        => photometric is PhotometricSeparated or PhotometricYCbCr or PhotometricCieLab or PhotometricIccLab;

    /// <summary>
    /// True when a page's samples must be reduced to plain 8-bit values before the photometric conversion:
    /// anything wider than 8 bits that is not the plain unsigned 16-bit case the fast paths already read, and
    /// every signed or floating-point sample format.
    /// </summary>
    public static bool NeedsReduction(int bits, int sampleFormat, int photometric)
        => bits > 8 && (bits == 32 || sampleFormat != 1 || IsColorimetric(photometric));

    /// <summary>
    /// Reduces 16- or 32-bit samples of any sample format to one byte each, producing a chunky
    /// <c>width * samples</c>-byte row layout.
    /// </summary>
    /// <remarks>
    /// Unsigned samples keep their most significant byte, signed samples are shifted into the same unsigned
    /// range first (so the darkest representable value becomes 0), and floating-point samples are read as the
    /// 0..1 range writers use for imaging data and scaled to 0..255.
    /// </remarks>
    public static byte[] ReduceToBytes(
        ReadOnlySpan<byte> raw, int width, int height, int rowBytes, int bits, int sampleFormat, int samples, bool bigEndian)
    {
        int perRow = width * samples;
        int bytesPerSample = bits / 8;
        var output = new byte[(long)perRow * height <= int.MaxValue ? perRow * height : throw new InvalidImageContentException("TIFF page is too large to decode.")];

        // The span cannot cross the lambda, so the reduction runs over a copy-free local array instead.
        byte[] source = raw.ToArray();
        ParallelRowIterator.IterateRows(perRow, height, (startRow, endRow) =>
        {
            for (int y = startRow; y < endRow; y++)
            {
                ReadOnlySpan<byte> src = source.AsSpan(y * rowBytes, rowBytes);
                Span<byte> dst = output.AsSpan(y * perRow, perRow);
                for (int i = 0; i < perRow; i++)
                {
                    int offset = i * bytesPerSample;
                    dst[i] = offset + bytesPerSample <= src.Length
                        ? bits == 16 ? Reduce16(src[offset..], sampleFormat, bigEndian) : Reduce32(src[offset..], sampleFormat, bigEndian)
                        : (byte)0;
                }
            }
        });

        return output;
    }

    private static byte Reduce16(ReadOnlySpan<byte> sample, int sampleFormat, bool bigEndian)
    {
        ushort value = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(sample) : BinaryPrimitives.ReadUInt16LittleEndian(sample);
        return sampleFormat switch
        {
            2 => (byte)((value ^ 0x8000) >> 8),
            3 => Scale((float)BitConverter.UInt16BitsToHalf(value)),
            _ => (byte)(value >> 8),
        };
    }

    private static byte Reduce32(ReadOnlySpan<byte> sample, int sampleFormat, bool bigEndian)
    {
        uint value = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(sample) : BinaryPrimitives.ReadUInt32LittleEndian(sample);
        return sampleFormat switch
        {
            2 => (byte)((value ^ 0x80000000u) >> 24),
            3 => Scale(BitConverter.UInt32BitsToSingle(value)),
            _ => (byte)(value >> 24),
        };
    }

    /// <summary>Maps the 0..1 range floating-point imaging data uses onto 0..255.</summary>
    private static byte Scale(float value)
        => float.IsNaN(value) ? (byte)0 : (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary>
    /// Converts one row of 8-bit samples in a colorimetric photometric interpretation into RGBA.
    /// </summary>
    /// <param name="row">The row's samples, chunky, one byte each.</param>
    /// <param name="dest">Receives <paramref name="width"/> pixels.</param>
    /// <param name="width">The row's pixel count.</param>
    /// <param name="samples">Samples per pixel.</param>
    /// <param name="photometric">The photometric interpretation.</param>
    /// <param name="hasAlpha">True when the last sample is an alpha channel.</param>
    public static void ConvertRow(ReadOnlySpan<byte> row, Span<Rgba32> dest, int width, int samples, int photometric, bool hasAlpha)
    {
        for (int x = 0; x < width; x++)
        {
            int i = x * samples;
            byte alpha = hasAlpha ? row[i + samples - 1] : (byte)255;
            dest[x] = photometric switch
            {
                PhotometricSeparated => FromCmyk(row[i], row[i + 1], row[i + 2], row[i + 3], alpha),
                PhotometricYCbCr => FromYCbCr(row[i], row[i + 1], row[i + 2], alpha),
                _ => FromLab(row[i], row[i + 1], row[i + 2], photometric == PhotometricIccLab, alpha),
            };
        }
    }

    /// <summary>Combines the four inks the way libtiff's RGBA reader does: each ink attenuates the others.</summary>
    public static Rgba32 FromCmyk(byte cyan, byte magenta, byte yellow, byte black, byte alpha)
    {
        int k = 255 - black;
        return new Rgba32(
            (byte)(k * (255 - cyan) / 255),
            (byte)(k * (255 - magenta) / 255),
            (byte)(k * (255 - yellow) / 255),
            alpha);
    }

    /// <summary>Full-range ITU-R BT.601 YCbCr, which is what the default ReferenceBlackWhite describes.</summary>
    public static Rgba32 FromYCbCr(byte luma, byte cb, byte cr, byte alpha)
    {
        float y = luma;
        float u = cb - 128f;
        float v = cr - 128f;
        return new Rgba32(
            Clamp(y + (1.402f * v)),
            Clamp(y - (0.344136f * u) - (0.714136f * v)),
            Clamp(y + (1.772f * u)),
            alpha);
    }

    /// <summary>
    /// CIE L*a*b* (or ICC L*a*b*, whose a and b samples are biased by 128) on the D50 white point, converted
    /// to sRGB.
    /// </summary>
    public static Rgba32 FromLab(byte lightness, byte aSample, byte bSample, bool iccEncoding, byte alpha)
    {
        double l = lightness * 100.0 / 255.0;
        double a = iccEncoding ? aSample - 128.0 : (sbyte)aSample;
        double b = iccEncoding ? bSample - 128.0 : (sbyte)bSample;

        double fy = (l + 16.0) / 116.0;
        double fx = fy + (a / 500.0);
        double fz = fy - (b / 200.0);

        // The D50 white point the TIFF specification prescribes for CIELab data.
        double x = 0.96422 * InverseF(fx);
        double y = InverseF(fy);
        double z = 0.82521 * InverseF(fz);

        return new Rgba32(
            Encode((3.1338561 * x) - (1.6168667 * y) - (0.4906146 * z)),
            Encode((-0.9787684 * x) + (1.9161415 * y) + (0.0334540 * z)),
            Encode((0.0719453 * x) - (0.2289914 * y) + (1.4052427 * z)),
            alpha);
    }

    private static double InverseF(double t)
    {
        const double Delta = 6.0 / 29.0;
        return t > Delta ? t * t * t : 3.0 * Delta * Delta * (t - (4.0 / 29.0));
    }

    /// <summary>Applies the sRGB transfer function and quantises to 8 bits.</summary>
    private static byte Encode(double linear)
    {
        linear = Math.Clamp(linear, 0.0, 1.0);
        double encoded = linear <= 0.0031308 ? 12.92 * linear : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255.0, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
