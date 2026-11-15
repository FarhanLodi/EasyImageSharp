using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Turns the decoded sample planes into pixel rows: chroma upsampling, the colour transform and packing into the
/// requested pixel format. One output row depends only on a few plane rows, so the full decode converts rows in
/// parallel and the streaming source converts them one at a time with the very same code.
/// </summary>
internal sealed partial class JpegDecoderCore
{
    // The colour model, resolved once by PrepareOutput.
    private bool isRgb;
    private bool isYcck;
    private bool adobeInverted;

    public Image<TPixel> ToImage<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (!this.frameParsed)
        {
            throw new InvalidImageContentException("JPEG image contains no frame.");
        }

        int componentCount = this.components.Length;
        if (componentCount is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"JPEG images with {componentCount} components are not supported.");
        }

        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(this.OutputWidth, this.OutputHeight);
        ImageMetadata metadata = this.BuildMetadata();

        // Rows are independent, so each batch gets its own scratch rows and they run in parallel.
        ParallelRowIterator.IterateRows(this.OutputWidth, this.OutputHeight, (startRow, endRow) =>
        {
            using var scratch = new RowScratch(this);
            for (int y = startRow; y < endRow; y++)
            {
                OperationExecution.Checkpoint();
                this.ConvertRow(y, frame.GetRowSpan(y), scratch);
            }
        });

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, metadata);
    }

    /// <summary>Resolves the colour model and assembles the root metadata. Called once per decode.</summary>
    internal ImageMetadata BuildMetadata()
    {
        this.PrepareOutput(out bool rgbIds);
        int componentCount = this.components.Length;
        this.metadataReader.SetFrame(this.progressive, JpegDecoder.ClassifyColorType(componentCount, this.adobeTransform, rgbIds));
        if (!this.lossless)
        {
            this.metadataReader.SetLuminanceQuantTable(this.quantTables[this.components[0].QuantId & 3]);
        }

        ImageMetadata metadata = this.metadataReader.Finish();
        JpegDecoder.RecordPrecision(metadata, this.precision, this.lossless);
        return metadata;
    }

    private void PrepareOutput(out bool rgbIds)
    {
        int componentCount = this.components.Length;
        rgbIds = componentCount == 3
            && this.components[0].Id == 'R' && this.components[1].Id == 'G' && this.components[2].Id == 'B';
        this.isRgb = componentCount == 3 && (this.adobeTransform == 0 || rgbIds);

        // Four components: Adobe transform 0 is CMYK, 2 is YCCK (any other nonzero value is treated as YCCK,
        // as libjpeg does); without an APP14 marker the data is plain CMYK.
        this.isYcck = componentCount == 4 && this.adobeTransform > 0;

        // Adobe writers store CMYK "inverted" (maximum - ink); the APP14 marker signals that convention.
        this.adobeInverted = this.adobeTransform >= 0;
    }

    /// <summary>Converts output row <paramref name="y"/> into the first <see cref="OutputWidth"/> pixels of the destination.</summary>
    private void ConvertRow<TPixel>(int y, Span<TPixel> destination, RowScratch scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (this.WideSamples)
        {
            this.ConvertWideRow(y, destination, scratch);
        }
        else
        {
            this.ConvertByteRow(y, destination, scratch);
        }
    }

    private void ConvertByteRow<TPixel>(int y, Span<TPixel> destination, RowScratch scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int imageWidth = this.OutputWidth;
        destination = destination[..imageWidth];
        Span<Rgba32> row = scratch.Rgba.AsSpan(0, imageWidth);
        switch (this.components.Length)
        {
            case 1:
            {
                // A single component is luminance: reinterpreting it as L8 makes the write a bulk broadcast
                // straight into the destination row.
                ReadOnlySpan<byte> r0 = this.GetComponentRow(0, y, scratch);
                PixelOps.Convert<L8, TPixel>(MemoryMarshal.Cast<byte, L8>(r0[..imageWidth]), destination);
                return;
            }

            case 3:
            {
                ReadOnlySpan<byte> r0 = this.GetComponentRow(0, y, scratch);
                ReadOnlySpan<byte> r1 = this.GetComponentRow(1, y, scratch);
                ReadOnlySpan<byte> r2 = this.GetComponentRow(2, y, scratch);
                if (this.isRgb)
                {
                    for (int x = 0; x < imageWidth; x++)
                    {
                        row[x] = new Rgba32(r0[x], r1[x], r2[x]);
                    }

                    break;
                }

                // The colour transform packs straight into the destination when its byte layout is one the
                // vector kernel writes, which skips a full pass over the row.
                if (typeof(TPixel) == typeof(Rgba32))
                {
                    YCbCrRowToPacked(r0, r1, r2, MemoryMarshal.Cast<TPixel, uint>(destination), bgra: false);
                    return;
                }

                if (typeof(TPixel) == typeof(Bgra32))
                {
                    YCbCrRowToPacked(r0, r1, r2, MemoryMarshal.Cast<TPixel, uint>(destination), bgra: true);
                    return;
                }

                YCbCrRowToPacked(r0, r1, r2, MemoryMarshal.Cast<Rgba32, uint>(row), bgra: false);
                break;
            }

            default:
            {
                ReadOnlySpan<byte> r0 = this.GetComponentRow(0, y, scratch);
                ReadOnlySpan<byte> r1 = this.GetComponentRow(1, y, scratch);
                ReadOnlySpan<byte> r2 = this.GetComponentRow(2, y, scratch);
                ReadOnlySpan<byte> r3 = this.GetComponentRow(3, y, scratch);
                bool ycck = this.isYcck;
                bool inverted = this.adobeInverted;
                for (int x = 0; x < imageWidth; x++)
                {
                    int s0 = r0[x];
                    int s1 = r1[x];
                    int s2 = r2[x];
                    int s3 = r3[x];
                    if (ycck)
                    {
                        // YCC -> RGB yields the inverted CMY channels of an Adobe CMYK image; K passes through.
                        Rgba32 cmy = YCbCrToRgb(s0, s1, s2);
                        row[x] = InvertedCmykToRgb(255 - cmy.R, 255 - cmy.G, 255 - cmy.B, s3);
                    }
                    else if (inverted)
                    {
                        row[x] = InvertedCmykToRgb(s0, s1, s2, s3);
                    }
                    else
                    {
                        row[x] = InvertedCmykToRgb(255 - s0, 255 - s1, 255 - s2, 255 - s3);
                    }
                }

                break;
            }
        }

        PixelOps.FromRgba32<TPixel>(row, destination);
    }

    /// <summary>
    /// The wide-sample counterpart of <see cref="ConvertByteRow"/>: the colour transform runs at the frame's own
    /// precision and the result is scaled once, to 16 bits for wide pixel formats and to 8 bits for every other one.
    /// </summary>
    private void ConvertWideRow<TPixel>(int y, Span<TPixel> destination, RowScratch scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int imageWidth = this.OutputWidth;
        destination = destination[..imageWidth];
        int max = this.maxSample;
        bool wideTarget = PixelOps.IsHighPrecision<TPixel>();

        if (this.components.Length == 1)
        {
            ReadOnlySpan<ushort> gray = this.GetWideComponentRow(0, y, scratch)[..imageWidth];
            if (typeof(TPixel) == typeof(L16))
            {
                Span<L16> target = MemoryMarshal.Cast<TPixel, L16>(destination);
                for (int x = 0; x < imageWidth; x++)
                {
                    target[x] = new L16(To16(gray[x], max));
                }

                return;
            }

            if (wideTarget)
            {
                Span<Rgba64> wide = scratch.Wide.AsSpan(0, imageWidth);
                for (int x = 0; x < imageWidth; x++)
                {
                    ushort l = To16(gray[x], max);
                    wide[x] = new Rgba64(l, l, l, ushort.MaxValue);
                }

                PixelOps.Convert<Rgba64, TPixel>(wide, destination);
                return;
            }

            Span<byte> narrow = MemoryMarshal.AsBytes(scratch.Rgba.AsSpan())[..imageWidth];
            for (int x = 0; x < imageWidth; x++)
            {
                narrow[x] = To8(gray[x], max);
            }

            PixelOps.Convert<L8, TPixel>(MemoryMarshal.Cast<byte, L8>(narrow), destination);
            return;
        }

        ReadOnlySpan<ushort> c0 = this.GetWideComponentRow(0, y, scratch);
        ReadOnlySpan<ushort> c1 = this.GetWideComponentRow(1, y, scratch);
        ReadOnlySpan<ushort> c2 = this.GetWideComponentRow(2, y, scratch);
        ReadOnlySpan<ushort> c3 = this.components.Length == 4 ? this.GetWideComponentRow(3, y, scratch) : default;
        Span<Rgba64> wideRow = wideTarget ? scratch.Wide.AsSpan(0, imageWidth) : default;
        Span<Rgba32> narrowRow = wideTarget ? default : scratch.Rgba.AsSpan(0, imageWidth);
        int center = (max + 1) >> 1;
        for (int x = 0; x < imageWidth; x++)
        {
            int r;
            int g;
            int b;
            if (this.components.Length == 3)
            {
                if (this.isRgb)
                {
                    (r, g, b) = (c0[x], c1[x], c2[x]);
                }
                else
                {
                    (r, g, b) = WideYCbCrToRgb(c0[x], c1[x], c2[x], center, max);
                }
            }
            else
            {
                int s0 = c0[x];
                int s1 = c1[x];
                int s2 = c2[x];
                int s3 = c3[x];
                if (this.isYcck)
                {
                    (int cr, int cg, int cb) = WideYCbCrToRgb(s0, s1, s2, center, max);
                    (s0, s1, s2) = (max - cr, max - cg, max - cb);
                }
                else if (!this.adobeInverted)
                {
                    (s0, s1, s2, s3) = (max - s0, max - s1, max - s2, max - s3);
                }

                r = MulDivMax(s0, s3, max);
                g = MulDivMax(s1, s3, max);
                b = MulDivMax(s2, s3, max);
            }

            if (wideTarget)
            {
                wideRow[x] = new Rgba64(To16(r, max), To16(g, max), To16(b, max), ushort.MaxValue);
            }
            else
            {
                narrowRow[x] = new Rgba32(To8(r, max), To8(g, max), To8(b, max));
            }
        }

        if (wideTarget)
        {
            PixelOps.Convert<Rgba64, TPixel>(wideRow, destination);
        }
        else
        {
            PixelOps.FromRgba32<TPixel>(narrowRow, destination);
        }
    }

    /// <summary>Scales a sample of the frame's precision to 16 bits, rounding to nearest: the maximum maps to 65535.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort To16(int value, int max) => (ushort)((((ulong)value * 65535) + (ulong)(max >> 1)) / (ulong)max);

    /// <summary>Scales a sample of the frame's precision to 8 bits, rounding to nearest: the maximum maps to 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte To8(int value, int max) => (byte)((((long)value * 255) + (max >> 1)) / max);

    /// <summary>Computes round(a * b / max) for a, b in 0..max.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MulDivMax(int a, int b, int max) => (int)((((long)a * b) + (max >> 1)) / max);

    /// <summary>
    /// The JFIF YCbCr to RGB transform at an arbitrary precision: the same single-precision expression as the 8-bit
    /// <see cref="YCbCrToRgb"/>, with chroma centred on half the range.
    /// </summary>
    private static (int R, int G, int B) WideYCbCrToRgb(int y, int cb, int cr, int center, int max)
    {
        float yf = y;
        float cbf = cb - center;
        float crf = cr - center;
        int r = (int)(yf + (1.402f * crf) + 0.5f);
        int g = (int)(yf - (0.344136f * cbf) - (0.714136f * crf) + 0.5f);
        int b = (int)(yf + (1.772f * cbf) + 0.5f);
        return (Math.Clamp(r, 0, max), Math.Clamp(g, 0, max), Math.Clamp(b, 0, max));
    }

    /// <summary>Returns output row <paramref name="y"/> of byte component <paramref name="index"/> at full image resolution.</summary>
    private ReadOnlySpan<byte> GetComponentRow(int index, int y, RowScratch scratch)
    {
        JpegComponent c = this.components[index];
        byte[]? output = scratch.Upsampled[index];
        if (output is null)
        {
            return c.Plane.AsSpan(c.RowOffset(y), this.OutputWidth);
        }

        if (this.FancyUpsampling && this.maxH == 2 * c.SampleH && c.CompWidth > 2)
        {
            // libjpeg only applies the triangle filter when the component is wider than two samples.
            if (this.maxV == c.SampleV)
            {
                JpegUpsampler.UpsampleH2V1(c.Plane.AsSpan(c.RowOffset(y), c.CompWidth), output);
                return output;
            }

            if (this.maxV == 2 * c.SampleV)
            {
                int row = y >> 1;
                int neighbour = (y & 1) == 0 ? Math.Max(row - 1, 0) : Math.Min(row + 1, c.CompHeight - 1);
                JpegUpsampler.UpsampleH2V2(
                    c.Plane.AsSpan(c.RowOffset(row), c.CompWidth),
                    c.Plane.AsSpan(c.RowOffset(neighbour), c.CompWidth),
                    output);
                return output;
            }
        }

        int source = Math.Min(y * c.SampleV / this.maxV, c.PlaneHeight - 1);
        JpegUpsampler.Replicate(c.Plane.AsSpan(c.RowOffset(source), c.PlaneWidth), c.SampleH, this.maxH, output.AsSpan(0, this.OutputWidth));
        return output;
    }

    /// <summary>The 16-bit counterpart of <see cref="GetComponentRow"/>.</summary>
    private ReadOnlySpan<ushort> GetWideComponentRow(int index, int y, RowScratch scratch)
    {
        JpegComponent c = this.components[index];
        ushort[]? output = scratch.WideUpsampled[index];
        if (output is null)
        {
            return c.WidePlane.AsSpan(c.RowOffset(y), this.OutputWidth);
        }

        if (this.FancyUpsampling && this.maxH == 2 * c.SampleH && c.CompWidth > 2)
        {
            if (this.maxV == c.SampleV)
            {
                JpegUpsampler.UpsampleH2V1(c.WidePlane.AsSpan(c.RowOffset(y), c.CompWidth), output);
                return output;
            }

            if (this.maxV == 2 * c.SampleV)
            {
                int row = y >> 1;
                int neighbour = (y & 1) == 0 ? Math.Max(row - 1, 0) : Math.Min(row + 1, c.CompHeight - 1);
                JpegUpsampler.UpsampleH2V2(
                    c.WidePlane.AsSpan(c.RowOffset(row), c.CompWidth),
                    c.WidePlane.AsSpan(c.RowOffset(neighbour), c.CompWidth),
                    output);
                return output;
            }
        }

        int source = Math.Min(y * c.SampleV / this.maxV, c.PlaneHeight - 1);
        JpegUpsampler.Replicate(c.WidePlane.AsSpan(c.RowOffset(source), c.PlaneWidth), c.SampleH, this.maxH, output.AsSpan(0, this.OutputWidth));
        return output;
    }

    /// <summary>
    /// Converts a row of Y, Cb and Cr samples to opaque <see cref="Rgba32"/> pixels, or <see cref="Bgra32"/> ones when
    /// <paramref name="bgra"/> is set, viewed as one uint per pixel. The vector path (little-endian only) packs
    /// <c>R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24</c>, with R and B swapped for Bgra32. It
    /// evaluates exactly the expression <see cref="YCbCrToRgb"/> uses, in the same order and with the same
    /// truncating conversion, so it produces the same bytes.
    /// </summary>
    private static void YCbCrRowToPacked(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<uint> destination, bool bgra)
    {
        int width = destination.Length;
        int x = 0;
        int redShift = bgra ? 16 : 0;
        int blueShift = bgra ? 0 : 16;

        if (SimdConfig.Vector256Enabled && BitConverter.IsLittleEndian && width >= Vector256<float>.Count)
        {
            ref byte yRef = ref MemoryMarshal.GetReference(y);
            ref byte cbRef = ref MemoryMarshal.GetReference(cb);
            ref byte crRef = ref MemoryMarshal.GetReference(cr);
            ref uint destRef = ref MemoryMarshal.GetReference(destination);

            Vector256<float> half = Vector256.Create(0.5f);
            Vector256<float> offset = Vector256.Create(128f);
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> max = Vector256.Create(255);
            Vector256<uint> opaque = Vector256.Create(0xFF000000u);

            for (; x <= width - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                Vector256<float> luma = WidenEight(ref yRef, x);
                Vector256<float> blue = WidenEight(ref cbRef, x) - offset;
                Vector256<float> red = WidenEight(ref crRef, x) - offset;

                Vector256<float> r = luma + (red * Vector256.Create(1.402f)) + half;
                Vector256<float> g = luma - (blue * Vector256.Create(0.344136f)) - (red * Vector256.Create(0.714136f)) + half;
                Vector256<float> b = luma + (blue * Vector256.Create(1.772f)) + half;

                Vector256<uint> packed = Vector256.ShiftLeft(Clamp(r, zero, max), redShift)
                    | Vector256.ShiftLeft(Clamp(g, zero, max), 8)
                    | Vector256.ShiftLeft(Clamp(b, zero, max), blueShift)
                    | opaque;
                packed.StoreUnsafe(ref destRef, (nuint)x);
            }
        }

        // The tail writes through the pixel structs, so it is right whatever the byte order of a uint.
        if (bgra)
        {
            Span<Bgra32> target = MemoryMarshal.Cast<uint, Bgra32>(destination);
            for (; x < width; x++)
            {
                Rgba32 c = YCbCrToRgb(y[x], cb[x], cr[x]);
                target[x] = new Bgra32(c.R, c.G, c.B, 255);
            }
        }
        else
        {
            Span<Rgba32> target = MemoryMarshal.Cast<uint, Rgba32>(destination);
            for (; x < width; x++)
            {
                target[x] = YCbCrToRgb(y[x], cb[x], cr[x]);
            }
        }

        static Vector256<uint> Clamp(Vector256<float> value, Vector256<int> zero, Vector256<int> max)
            => Vector256.Max(Vector256.Min(Vector256.ConvertToInt32(value), max), zero).AsUInt32();
    }

    /// <summary>Widens eight consecutive bytes starting at <paramref name="index"/> to single precision.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> WidenEight(ref byte source, int index)
    {
        ulong packed = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, (uint)index));
        (Vector128<ushort> low, _) = Vector128.Widen(Vector128.CreateScalar(packed).AsByte());
        (Vector128<uint> first, Vector128<uint> second) = Vector128.Widen(low);
        return Vector256.ConvertToSingle(Vector256.Create(first.AsInt32(), second.AsInt32()));
    }

    private static Rgba32 YCbCrToRgb(int y, int cb, int cr)
    {
        float yf = y;
        float cbf = cb - 128f;
        float crf = cr - 128f;
        int r = (int)(yf + (1.402f * crf) + 0.5f);
        int g = (int)(yf - (0.344136f * cbf) - (0.714136f * crf) + 0.5f);
        int b = (int)(yf + (1.772f * cbf) + 0.5f);
        return new Rgba32(
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255));
    }

    /// <summary>
    /// Converts inverted CMYK samples (each stored as 255 - ink coverage, the Adobe convention) to RGB with
    /// the usual naive model R = (255 - C)(255 - K) / 255, rounded to nearest.
    /// </summary>
    private static Rgba32 InvertedCmykToRgb(int c, int m, int y, int k)
        => new Rgba32(MulDiv255(c, k), MulDiv255(m, k), MulDiv255(y, k));

    /// <summary>Computes round(a * b / 255) for a, b in 0..255.</summary>
    private static byte MulDiv255(int a, int b)
    {
        int t = (a * b) + 128;
        return (byte)((t + (t >> 8)) >> 8);
    }

    /// <summary>
    /// Per-thread rows for <see cref="ConvertRow"/>: one upsampling row per subsampled component and the RGBA rows
    /// the colour transform writes. Everything is rented and returned on dispose.
    /// </summary>
    internal sealed class RowScratch : IDisposable
    {
        public readonly byte[]?[] Upsampled;
        public readonly ushort[]?[] WideUpsampled;
        public readonly Rgba32[] Rgba;
        public readonly Rgba64[] Wide;

        public RowScratch(JpegDecoderCore core)
        {
            int count = core.components.Length;
            bool wide = core.WideSamples;
            this.Upsampled = new byte[count][];
            this.WideUpsampled = new ushort[count][];

            // Upsampling writes up to twice a component's width or the padded MCU width, whichever is larger.
            int fullWidth = Math.Max(core.mcusX * core.maxH * (core.lossless ? 1 : core.outputBlockSize), core.OutputWidth);
            for (int i = 0; i < count; i++)
            {
                JpegComponent c = core.components[i];
                if (c.SampleH == core.maxH && c.SampleV == core.maxV)
                {
                    continue;
                }

                int length = Math.Max(fullWidth, 2 * c.CompWidth);
                if (wide)
                {
                    this.WideUpsampled[i] = ArrayPool<ushort>.Shared.Rent(length);
                }
                else
                {
                    this.Upsampled[i] = ArrayPool<byte>.Shared.Rent(length);
                }
            }

            this.Rgba = ArrayPool<Rgba32>.Shared.Rent(core.OutputWidth);
            this.Wide = wide ? ArrayPool<Rgba64>.Shared.Rent(core.OutputWidth) : Array.Empty<Rgba64>();
        }

        public void Dispose()
        {
            ArrayPool<Rgba32>.Shared.Return(this.Rgba);
            if (this.Wide.Length > 0)
            {
                ArrayPool<Rgba64>.Shared.Return(this.Wide);
            }

            for (int i = 0; i < this.Upsampled.Length; i++)
            {
                if (this.Upsampled[i] is byte[] buffer)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (this.WideUpsampled[i] is ushort[] wideBuffer)
                {
                    ArrayPool<ushort>.Shared.Return(wideBuffer);
                }
            }
        }
    }
}
