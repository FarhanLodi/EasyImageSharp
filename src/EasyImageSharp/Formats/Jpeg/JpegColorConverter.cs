using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Row-wise conversion of source pixels into JPEG component samples. RGB to YCbCr uses the JFIF (ITU-R BT.601
/// full-range) matrix evaluated in 16-bit fixed point through precomputed per-channel tables, so a pixel costs
/// three table lookups, two adds and a shift per component and the result is bit-exact and platform-independent.
/// Pixel formats with a known byte layout are read in place; everything else goes through <see cref="Rgba32"/>.
/// </summary>
/// <remarks>
/// Samples are emitted already level-shifted (sample - 128, as the DCT wants them) and scaled by
/// <see cref="SampleScale"/>, i.e. with <see cref="FractionBits"/> fractional bits. Keeping four fractional bits
/// costs one extra byte per sample and makes the colour transform and the chroma box filter effectively exact:
/// rounding the matrix output to whole samples first, as an 8-bit pipeline must, adds about 1/12 of a squared
/// sample unit of noise to every component, which is a measurable dent in the PSNR of a high-quality encode.
/// </remarks>
internal static class JpegColorConverter
{
    /// <summary>Fractional bits carried by an emitted sample.</summary>
    public const int FractionBits = 4;

    /// <summary>The scale factor a sample is stored with: <c>1 &lt;&lt; FractionBits</c>.</summary>
    public const int SampleScale = 1 << FractionBits;

    /// <summary>Fractional bits of the fixed-point matrix coefficients.</summary>
    private const int ScaleBits = 16;

    /// <summary>Right shift that takes a matrix sum from <see cref="ScaleBits"/> down to <see cref="FractionBits"/>.</summary>
    private const int OutputShift = ScaleBits - FractionBits;

    /// <summary>Rounding bias for <see cref="OutputShift"/>: adding it makes the shift round to nearest.</summary>
    private const int RoundBias = 1 << (OutputShift - 1);

    /// <summary>The -128 level shift, expressed at <see cref="ScaleBits"/> precision.</summary>
    private const int LevelShift = 128 << ScaleBits;

    // Section offsets into Table: nine products share eight sections because the Cb-from-blue and Cr-from-red
    // coefficients are both exactly 0.5 and carry the same bias.
    private const int RY = 0;
    private const int GY = 256;
    private const int BY = 512;
    private const int RCb = 768;
    private const int GCb = 1024;
    private const int BCb = 1280;
    private const int RCr = BCb;
    private const int GCr = 1536;
    private const int BCr = 1792;

    /// <summary>Per-channel products of the BT.601 matrix, indexed by <c>section + sample</c>.</summary>
    private static readonly int[] Table = CreateTable();

    /// <summary>Converts a row to level-shifted Y, Cb and Cr samples.</summary>
    public static void ToYCbCr<TPixel>(ReadOnlySpan<TPixel> row, Span<short> y, Span<short> cb, Span<short> cr, Span<Rgba32> scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = row.Length;
        ReadOnlySpan<byte> src = AsRgbBytes(row, scratch, out RgbLayout layout);
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref short yRef = ref MemoryMarshal.GetReference(y);
        ref short cbRef = ref MemoryMarshal.GetReference(cb);
        ref short crRef = ref MemoryMarshal.GetReference(cr);
        ref int t = ref MemoryMarshal.GetArrayDataReference(Table);
        int stride = layout.Stride;
        int rOff = layout.R;
        int gOff = layout.G;
        int bOff = layout.B;
        int i = 0;
        for (int x = 0; x < width; x++, i += stride)
        {
            int r = Unsafe.Add(ref s, i + rOff);
            int g = Unsafe.Add(ref s, i + gOff);
            int b = Unsafe.Add(ref s, i + bOff);
            Unsafe.Add(ref yRef, x) =
                (short)((Unsafe.Add(ref t, RY + r) + Unsafe.Add(ref t, GY + g) + Unsafe.Add(ref t, BY + b)) >> OutputShift);
            Unsafe.Add(ref cbRef, x) =
                (short)((Unsafe.Add(ref t, RCb + r) + Unsafe.Add(ref t, GCb + g) + Unsafe.Add(ref t, BCb + b)) >> OutputShift);
            Unsafe.Add(ref crRef, x) =
                (short)((Unsafe.Add(ref t, RCr + r) + Unsafe.Add(ref t, GCr + g) + Unsafe.Add(ref t, BCr + b)) >> OutputShift);
        }
    }

    /// <summary>Converts a row to one level-shifted luma sample per pixel; <see cref="L8"/> rows are taken as-is.</summary>
    public static void ToLuminance<TPixel>(ReadOnlySpan<TPixel> row, Span<short> y, Span<Rgba32> scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = row.Length;
        ref short yRef = ref MemoryMarshal.GetReference(y);
        if (typeof(TPixel) == typeof(L8))
        {
            ref byte gray = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(row));
            for (int x = 0; x < width; x++)
            {
                Unsafe.Add(ref yRef, x) = Shift(Unsafe.Add(ref gray, x));
            }

            return;
        }

        ReadOnlySpan<byte> src = AsRgbBytes(row, scratch, out RgbLayout layout);
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref int t = ref MemoryMarshal.GetArrayDataReference(Table);
        int stride = layout.Stride;
        int rOff = layout.R;
        int gOff = layout.G;
        int bOff = layout.B;
        int i = 0;
        for (int x = 0; x < width; x++, i += stride)
        {
            int r = Unsafe.Add(ref s, i + rOff);
            int g = Unsafe.Add(ref s, i + gOff);
            int b = Unsafe.Add(ref s, i + bOff);
            Unsafe.Add(ref yRef, x) =
                (short)((Unsafe.Add(ref t, RY + r) + Unsafe.Add(ref t, GY + g) + Unsafe.Add(ref t, BY + b)) >> OutputShift);
        }
    }

    /// <summary>Copies a row's R, G and B channels into three planes, level-shifted but otherwise untransformed.</summary>
    public static void ToRgb<TPixel>(ReadOnlySpan<TPixel> row, Span<short> r, Span<short> g, Span<short> b, Span<Rgba32> scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = row.Length;
        ReadOnlySpan<byte> src = AsRgbBytes(row, scratch, out RgbLayout layout);
        int stride = layout.Stride;
        int i = 0;
        for (int x = 0; x < width; x++, i += stride)
        {
            r[x] = Shift(src[i + layout.R]);
            g[x] = Shift(src[i + layout.G]);
            b[x] = Shift(src[i + layout.B]);
        }
    }

    /// <summary>
    /// Separates a row into Adobe-style inverted CMYK (each sample is 255 minus the ink coverage): the black ink
    /// is K = 255 - max(R, G, B) and the chromatic inks are scaled by the remaining density, so that a decoder
    /// computing (255 - C)(255 - K) / 255 reproduces R.
    /// </summary>
    public static void ToInvertedCmyk<TPixel>(
        ReadOnlySpan<TPixel> row, Span<short> c, Span<short> m, Span<short> y, Span<short> k, Span<Rgba32> scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = row.Length;
        ReadOnlySpan<byte> src = AsRgbBytes(row, scratch, out RgbLayout layout);
        int stride = layout.Stride;
        int i = 0;
        for (int x = 0; x < width; x++, i += stride)
        {
            Separate(src, i, layout, out int white, out int ic, out int im, out int iy);
            k[x] = Shift(white);
            c[x] = Shift(ic);
            m[x] = Shift(im);
            y[x] = Shift(iy);
        }
    }

    /// <summary>
    /// Produces YCCK samples (Adobe transform 2): the inverted CMY channels put through the same YCbCr matrix as
    /// RGB, with K passed through unchanged. This is the exact inverse of what a YCCK-aware decoder does, which
    /// recovers the inverted CMY samples as 255 minus the YCbCr-to-RGB result.
    /// </summary>
    public static void ToYcck<TPixel>(
        ReadOnlySpan<TPixel> row, Span<short> y, Span<short> cb, Span<short> cr, Span<short> k, Span<Rgba32> scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = row.Length;
        ReadOnlySpan<byte> src = AsRgbBytes(row, scratch, out RgbLayout layout);
        ref int t = ref MemoryMarshal.GetArrayDataReference(Table);
        int stride = layout.Stride;
        int i = 0;
        for (int x = 0; x < width; x++, i += stride)
        {
            Separate(src, i, layout, out int white, out int ic, out int im, out int iy);
            k[x] = Shift(white);

            // The decoder's inverse takes 255 minus each transformed channel, so feed it the complements.
            int r = 255 - ic;
            int g = 255 - im;
            int b = 255 - iy;
            y[x] = (short)((Unsafe.Add(ref t, RY + r) + Unsafe.Add(ref t, GY + g) + Unsafe.Add(ref t, BY + b)) >> OutputShift);
            cb[x] = (short)((Unsafe.Add(ref t, RCb + r) + Unsafe.Add(ref t, GCb + g) + Unsafe.Add(ref t, BCb + b)) >> OutputShift);
            cr[x] = (short)((Unsafe.Add(ref t, RCr + r) + Unsafe.Add(ref t, GCr + g) + Unsafe.Add(ref t, BCr + b)) >> OutputShift);
        }
    }

    /// <summary>Splits one pixel into the inverted CMYK inks a decoder multiplies back together.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Separate(ReadOnlySpan<byte> src, int i, RgbLayout layout, out int white, out int c, out int m, out int y)
    {
        int r = src[i + layout.R];
        int g = src[i + layout.G];
        int b = src[i + layout.B];
        white = Math.Max(r, Math.Max(g, b)); // 255 - K ink.
        if (white == 0)
        {
            // Pure black: the K ink alone reproduces the pixel, so leave the chromatic inks empty.
            c = 255;
            m = 255;
            y = 255;
            return;
        }

        int half = white >> 1;
        c = ((r * 255) + half) / white;
        m = ((g * 255) + half) / white;
        y = ((b * 255) + half) / white;
    }

    /// <summary>Level-shifts an 8-bit sample and scales it to <see cref="FractionBits"/> fractional bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Shift(int sample) => (short)((sample - 128) << FractionBits);

    private static int[] CreateTable()
    {
        var table = new int[2048];
        for (int i = 0; i < 256; i++)
        {
            table[RY + i] = Fix(0.29900) * i;
            table[GY + i] = Fix(0.58700) * i;

            // The level shift and the rounding bias ride along in the last term of each sum.
            table[BY + i] = (Fix(0.11400) * i) - LevelShift + RoundBias;
            table[RCb + i] = -Fix(0.16874) * i;
            table[GCb + i] = -Fix(0.33126) * i;

            // Chroma is centred on 128, so level shifting it leaves nothing but the rounding bias.
            table[BCb + i] = (Fix(0.50000) * i) + RoundBias;
            table[GCr + i] = -Fix(0.41869) * i;
            table[BCr + i] = -Fix(0.08131) * i;
        }

        return table;
    }

    private static int Fix(double value) => (int)((value * (1 << ScaleBits)) + 0.5);

    /// <summary>Views the row as raw bytes with a known RGB layout, converting through the scratch row when needed.</summary>
    private static ReadOnlySpan<byte> AsRgbBytes<TPixel>(ReadOnlySpan<TPixel> row, Span<Rgba32> scratch, out RgbLayout layout)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        RgbLayout? known = LayoutOf<TPixel>();
        if (known.HasValue)
        {
            layout = known.Value;
            return MemoryMarshal.AsBytes(row);
        }

        PixelOps.ToRgba32(row, scratch[..row.Length]);
        layout = new RgbLayout(4, 0, 1, 2);
        return MemoryMarshal.AsBytes<Rgba32>(scratch[..row.Length]);
    }

    /// <summary>Returns the byte layout for pixel formats that can be read in place, or null.</summary>
    private static RgbLayout? LayoutOf<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgb24))
        {
            return new RgbLayout(3, 0, 1, 2);
        }

        if (typeof(TPixel) == typeof(Rgba32))
        {
            return new RgbLayout(4, 0, 1, 2);
        }

        if (typeof(TPixel) == typeof(Bgr24))
        {
            return new RgbLayout(3, 2, 1, 0);
        }

        if (typeof(TPixel) == typeof(Bgra32))
        {
            return new RgbLayout(4, 2, 1, 0);
        }

        return null;
    }

    /// <summary>Describes where R, G and B live inside one pixel of a byte-addressable pixel format.</summary>
    private readonly record struct RgbLayout(int Stride, int R, int G, int B);
}
