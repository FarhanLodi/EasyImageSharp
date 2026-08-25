using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Static facts about a pixel format that processing operations branch on.</summary>
internal static class PixelFormatInfo<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>Whether <typeparamref name="TPixel"/> stores an alpha channel (a transparent pixel survives a round trip).</summary>
    public static readonly bool HasAlpha = TPixel.FromRgba32(Rgba32.Transparent).ToRgba32().A != byte.MaxValue;
}

/// <summary>sRGB transfer-function tables shared by the companding resize and transform paths.</summary>
internal static class SrgbCompanding
{
    /// <summary>Linear-light value (scaled to 0..255) of each 8-bit sRGB code.</summary>
    public static readonly float[] ToLinear255 = BuildDecodeTable();

    /// <summary>Encodes a linear-light value in 0..255 back to an sRGB value in 0..255 (unclamped, unrounded).</summary>
    public static float ToSrgb255(float linear255)
    {
        float v = linear255 / 255f;
        if (v <= 0f)
        {
            return 0f;
        }

        if (v >= 1f)
        {
            return 255f;
        }

        float s = v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
        return s * 255f;
    }

    private static float[] BuildDecodeTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            float linear = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            table[i] = linear * 255f;
        }

        return table;
    }
}

/// <summary>Geometric operations on single frames.</summary>
internal static class FrameOps
{
    // ----- Resize -----

    /// <summary>
    /// Bytes of horizontally filtered rows one resize chunk may hold. Chunks are sized so this working set
    /// stays cache resident; the only cost of splitting is that the few rows shared with the next chunk are
    /// filtered twice.
    /// </summary>
    private const int ResizeChunkBudgetBytes = 1 << 21;

    /// <summary>
    /// Resizes with the given kernel. <paramref name="premultiplyAlpha"/> weights colours by alpha while filtering
    /// (only when the format has alpha); <paramref name="compand"/> filters in linear light. Opaque formats without
    /// companding take the historical straight-RGB path, so their output is unchanged.
    /// </summary>
    public static ImageFrame<TPixel> Resize<TPixel>(
        ImageFrame<TPixel> source, int width, int height, IResampler sampler, bool premultiplyAlpha = true, bool compand = false)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        ArgumentNullException.ThrowIfNull(sampler);
        if (width == source.Width && height == source.Height)
        {
            return source;
        }

        return sampler is NearestNeighborResampler
            ? NearestResize(source, width, height)
            : WeightedResize(source, width, height, sampler, premultiplyAlpha && PixelFormatInfo<TPixel>.HasAlpha, compand);
    }

    private static ImageFrame<TPixel> NearestResize<TPixel>(ImageFrame<TPixel> source, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> dest = FrameFactory.CreateUninitialized<TPixel>(width, height);
        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        bool sameWidth = width == sourceWidth;
        int[] columns = ArrayPool<int>.Shared.Rent(width);
        try
        {
            if (!sameWidth)
            {
                for (int x = 0; x < width; x++)
                {
                    columns[x] = Math.Min((int)(((long)x * sourceWidth) / width), sourceWidth - 1);
                }
            }

            ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
            {
                for (int y = startRow; y < endRow; y++)
                {
                    Span<TPixel> sourceRow = source.GetRowSpan(Math.Min((int)(((long)y * sourceHeight) / height), sourceHeight - 1));
                    Span<TPixel> destRow = dest.GetRowSpan(y);
                    if (sameWidth)
                    {
                        sourceRow.CopyTo(destRow);
                        continue;
                    }

                    for (int x = 0; x < width; x++)
                    {
                        destRow[x] = sourceRow[columns[x]];
                    }
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(columns);
        }

        return dest;
    }

    private static ImageFrame<TPixel> WeightedResize<TPixel>(
        ImageFrame<TPixel> source, int width, int height, IResampler sampler, bool premultiply, bool compand)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var horizontal = ResizeKernelMap.Build(source.Width, width, sampler);
        var vertical = ResizeKernelMap.Build(source.Height, height, sampler);
        ImageFrame<TPixel> dest = FrameFactory.CreateUninitialized<TPixel>(width, height);

        // L8 carries a single channel, so filtering it as four is three quarters wasted work.
        bool singleChannel = typeof(TPixel) == typeof(L8);
        int channels = singleChannel ? 1 : 4;
        int budgetRows = Math.Clamp(
            ResizeChunkBudgetBytes / Math.Max(1, width * channels * sizeof(float)),
            vertical.MaxLength,
            source.Height);
        (int Y0, int Y1)[] chunks = PlanResizeChunks(vertical, height, budgetRows);

        // Every chunk filters the source rows it needs and writes only its own destination rows, so the
        // chunks are independent and the arithmetic per output pixel is unchanged by the split.
        int pixelsPerChunk = (int)Math.Min(int.MaxValue, (long)width * height / chunks.Length);
        ParallelRowIterator.IterateRows(Math.Max(1, pixelsPerChunk), chunks.Length, (startChunk, endChunk) =>
        {
            for (int c = startChunk; c < endChunk; c++)
            {
                (int y0, int y1) = chunks[c];
                if (singleChannel)
                {
                    ResizeChunkSingleChannel(source, dest, horizontal, vertical, y0, y1, compand);
                }
                else
                {
                    ResizeChunkColor(source, dest, horizontal, vertical, y0, y1, premultiply, compand);
                }
            }
        });

        return dest;
    }

    /// <summary>Splits the destination rows into runs whose source windows fit inside the row budget.</summary>
    private static (int Y0, int Y1)[] PlanResizeChunks(ResizeKernelMap vertical, int height, int budgetRows)
    {
        var chunks = new List<(int, int)>();
        int y = 0;
        while (y < height)
        {
            int end = y + 1;

            // Start is non-decreasing, so the rows a run needs are Start[end] + Length[end] - Start[y].
            while (end < height && vertical.Start[end] + vertical.Length[end] - vertical.Start[y] <= budgetRows)
            {
                end++;
            }

            chunks.Add((y, end));
            y = end;
        }

        return chunks.ToArray();
    }

    /// <summary>Filters destination rows <c>[y0, y1)</c> of an RGBA-shaped format.</summary>
    private static void ResizeChunkColor<TPixel>(
        ImageFrame<TPixel> source, ImageFrame<TPixel> dest, ResizeKernelMap horizontal, ResizeKernelMap vertical,
        int y0, int y1, bool premultiply, bool compand)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = dest.Width;
        int sourceWidth = source.Width;
        int firstRow = vertical.Start[y0];
        int rowCount = vertical.Start[y1 - 1] + vertical.Length[y1 - 1] - firstRow;

        Vector4[] filtered = ArrayPool<Vector4>.Shared.Rent(rowCount * width);
        Rgba32[] sourceRgba = ArrayPool<Rgba32>.Shared.Rent(sourceWidth);
        Vector4[] sourceVectors = ArrayPool<Vector4>.Shared.Rent(sourceWidth);
        Vector4[] accumulator = ArrayPool<Vector4>.Shared.Rent(width);
        Rgba32[] destRgba = ArrayPool<Rgba32>.Shared.Rent(width);
        try
        {
            Span<Rgba32> sourceRow = sourceRgba.AsSpan(0, sourceWidth);
            Span<Vector4> sourceVectorRow = sourceVectors.AsSpan(0, sourceWidth);
            for (int r = 0; r < rowCount; r++)
            {
                PixelOps.ToRgba32<TPixel>(source.GetRowSpan(firstRow + r), sourceRow);
                LoadRow(sourceRow, sourceVectorRow, premultiply, compand);
                HorizontalPass(sourceVectorRow, filtered.AsSpan(r * width, width), horizontal);
            }

            Span<Vector4> accumulate = accumulator.AsSpan(0, width);
            Span<Rgba32> destRow = destRgba.AsSpan(0, width);
            for (int y = y0; y < y1; y++)
            {
                VerticalPass(filtered, firstRow, width, vertical, y, accumulate);
                for (int x = 0; x < width; x++)
                {
                    destRow[x] = StorePixel(accumulate[x], premultiply, compand);
                }

                PixelOps.FromRgba32<TPixel>(destRow, dest.GetRowSpan(y));
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(destRgba);
            ArrayPool<Vector4>.Shared.Return(accumulator);
            ArrayPool<Vector4>.Shared.Return(sourceVectors);
            ArrayPool<Rgba32>.Shared.Return(sourceRgba);
            ArrayPool<Vector4>.Shared.Return(filtered);
        }
    }

    /// <summary>
    /// Filters destination rows <c>[y0, y1)</c> of <see cref="L8"/>. Because the three colour channels of a
    /// grayscale pixel hold the same value they also filter to the same value, so one channel is carried and
    /// the result is expanded through the same <c>Rgba32</c> round trip the colour path uses, via a table.
    /// </summary>
    private static void ResizeChunkSingleChannel<TPixel>(
        ImageFrame<TPixel> source, ImageFrame<TPixel> dest, ResizeKernelMap horizontal, ResizeKernelMap vertical,
        int y0, int y1, bool compand)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = dest.Width;
        int sourceWidth = source.Width;
        int firstRow = vertical.Start[y0];
        int rowCount = vertical.Start[y1 - 1] + vertical.Length[y1 - 1] - firstRow;

        float[] filtered = ArrayPool<float>.Shared.Rent(rowCount * width);
        float[] sourceValues = ArrayPool<float>.Shared.Rent(sourceWidth);
        float[] accumulator = ArrayPool<float>.Shared.Rent(width);
        try
        {
            Span<float> sourceRow = sourceValues.AsSpan(0, sourceWidth);
            float[] toLinear = SrgbCompanding.ToLinear255;
            for (int r = 0; r < rowCount; r++)
            {
                ReadOnlySpan<byte> samples = MemoryMarshal.AsBytes(source.GetRowSpan(firstRow + r));
                if (compand)
                {
                    for (int x = 0; x < sourceWidth; x++)
                    {
                        sourceRow[x] = toLinear[samples[x]];
                    }
                }
                else
                {
                    for (int x = 0; x < sourceWidth; x++)
                    {
                        sourceRow[x] = samples[x];
                    }
                }

                HorizontalPassSingleChannel(sourceRow, filtered.AsSpan(r * width, width), horizontal);
            }

            TPixel[] lut = GrayLut<TPixel>.Table;
            Span<float> accumulate = accumulator.AsSpan(0, width);
            for (int y = y0; y < y1; y++)
            {
                int start = vertical.Start[y] - firstRow;
                int length = vertical.Length[y];
                ReadOnlySpan<float> weights = vertical.Weights.AsSpan(vertical.Offset[y], length);
                for (int i = 0; i < length; i++)
                {
                    AccumulateScaled(filtered.AsSpan((start + i) * width, width), accumulate, weights[i], i == 0);
                }

                Span<TPixel> destRow = dest.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    float value = compand ? SrgbCompanding.ToSrgb255(accumulate[x]) : accumulate[x];
                    destRow[x] = lut[(byte)Math.Clamp((int)(value + 0.5f), 0, 255)];
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(accumulator);
            ArrayPool<float>.Shared.Return(sourceValues);
            ArrayPool<float>.Shared.Return(filtered);
        }
    }

    /// <summary>Maps a filtered grey level onto the pixel format exactly as the colour path's round trip does.</summary>
    private static class GrayLut<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        public static readonly TPixel[] Table = Build();

        private static TPixel[] Build()
        {
            var table = new TPixel[256];
            for (int v = 0; v < 256; v++)
            {
                table[v] = TPixel.FromRgba32(new Rgba32((byte)v, (byte)v, (byte)v, byte.MaxValue));
            }

            return table;
        }
    }

    /// <summary>Applies the horizontal kernel to one source row.</summary>
    private static void HorizontalPass(ReadOnlySpan<Vector4> source, Span<Vector4> destination, ResizeKernelMap map)
    {
        ref Vector4 samples = ref MemoryMarshal.GetReference(source);
        ref float weights = ref MemoryMarshal.GetReference(map.Weights.AsSpan());
        for (int x = 0; x < destination.Length; x++)
        {
            ref Vector4 window = ref Unsafe.Add(ref samples, (uint)map.Start[x]);
            ref float tap = ref Unsafe.Add(ref weights, (uint)map.Offset[x]);
            int length = map.Length[x];
            Vector4 sum = Vector4.Zero;
            for (int i = 0; i < length; i++)
            {
                sum += Unsafe.Add(ref window, (uint)i) * Unsafe.Add(ref tap, (uint)i);
            }

            destination[x] = sum;
        }
    }

    /// <summary>Applies the horizontal kernel to one single-channel source row.</summary>
    private static void HorizontalPassSingleChannel(ReadOnlySpan<float> source, Span<float> destination, ResizeKernelMap map)
    {
        ref float samples = ref MemoryMarshal.GetReference(source);
        ref float weights = ref MemoryMarshal.GetReference(map.Weights.AsSpan());
        for (int x = 0; x < destination.Length; x++)
        {
            ref float window = ref Unsafe.Add(ref samples, (uint)map.Start[x]);
            ref float tap = ref Unsafe.Add(ref weights, (uint)map.Offset[x]);
            int length = map.Length[x];
            float sum = 0f;
            for (int i = 0; i < length; i++)
            {
                sum += Unsafe.Add(ref window, (uint)i) * Unsafe.Add(ref tap, (uint)i);
            }

            destination[x] = sum;
        }
    }

    /// <summary>
    /// Applies the vertical kernel to one destination row. Taps are accumulated whole-row at a time, in the
    /// same ascending order the per-pixel loop used, which keeps the sum bit-identical while letting each
    /// tap run as a straight-line vector loop over contiguous memory.
    /// </summary>
    private static void VerticalPass(
        Vector4[] filtered, int firstRow, int width, ResizeKernelMap map, int y, Span<Vector4> accumulator)
    {
        int start = map.Start[y] - firstRow;
        int length = map.Length[y];
        ReadOnlySpan<float> weights = map.Weights.AsSpan(map.Offset[y], length);
        Span<float> accumulate = MemoryMarshal.Cast<Vector4, float>(accumulator);
        for (int i = 0; i < length; i++)
        {
            ReadOnlySpan<float> row = MemoryMarshal.Cast<Vector4, float>(filtered.AsSpan((start + i) * width, width));
            AccumulateScaled(row, accumulate, weights[i], i == 0);
        }
    }

    /// <summary>
    /// <c>accumulator += source * weight</c>, or <c>accumulator = source * weight</c> for the first tap.
    /// Float addition is commutative, so folding the running sum in from the right leaves the result exact.
    /// </summary>
    private static void AccumulateScaled(ReadOnlySpan<float> source, Span<float> accumulator, float weight, bool first)
    {
        int count = source.Length;
        ref float src = ref MemoryMarshal.GetReference(source);
        ref float acc = ref MemoryMarshal.GetReference(accumulator);
        int i = 0;

        if (SimdConfig.Vector256Enabled && count >= Vector256<float>.Count)
        {
            Vector256<float> scale = Vector256.Create(weight);
            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> value = Vector256.LoadUnsafe(ref src, (nuint)i) * scale;
                if (!first)
                {
                    value += Vector256.LoadUnsafe(ref acc, (nuint)i);
                }

                value.StoreUnsafe(ref acc, (nuint)i);
            }
        }

        if (SimdConfig.Vector128Enabled)
        {
            Vector128<float> scale = Vector128.Create(weight);
            for (; i <= count - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref src, (nuint)i) * scale;
                if (!first)
                {
                    value += Vector128.LoadUnsafe(ref acc, (nuint)i);
                }

                value.StoreUnsafe(ref acc, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            float value = Unsafe.Add(ref src, (uint)i) * weight;
            Unsafe.Add(ref acc, (uint)i) = first ? value : Unsafe.Add(ref acc, (uint)i) + value;
        }
    }

    /// <summary>Expands a row of pixels into filter-space vectors (0..255 range, optionally linear light and/or premultiplied).</summary>
    internal static void LoadRow(ReadOnlySpan<Rgba32> source, Span<Vector4> destination, bool premultiply, bool compand)
    {
        if (!premultiply && !compand)
        {
            PixelOps.WidenToSingle(source, MemoryMarshal.Cast<Vector4, float>(destination));
            return;
        }

        float[] toLinear = SrgbCompanding.ToLinear255;
        for (int i = 0; i < source.Length; i++)
        {
            Rgba32 p = source[i];
            Vector4 v = compand
                ? new Vector4(toLinear[p.R], toLinear[p.G], toLinear[p.B], p.A)
                : new Vector4(p.R, p.G, p.B, p.A);
            if (premultiply)
            {
                float a = p.A / 255f;
                v = new Vector4(v.X * a, v.Y * a, v.Z * a, v.W);
            }

            destination[i] = v;
        }
    }

    /// <summary>Converts a filtered vector back to a pixel, undoing premultiplication and companding as requested.</summary>
    internal static Rgba32 StorePixel(Vector4 value, bool premultiply, bool compand)
    {
        if (premultiply)
        {
            // An alpha that rounds to zero carries no usable colour: emit canonical transparent black.
            if (value.W < 0.5f)
            {
                return Rgba32.Transparent;
            }

            float scale = 255f / value.W;
            value = new Vector4(value.X * scale, value.Y * scale, value.Z * scale, value.W);
        }

        if (compand)
        {
            value = new Vector4(
                SrgbCompanding.ToSrgb255(value.X),
                SrgbCompanding.ToSrgb255(value.Y),
                SrgbCompanding.ToSrgb255(value.Z),
                value.W);
        }

        return ToRgba32(value);
    }

    internal static Rgba32 ToRgba32(Vector4 value) => new(
        (byte)Math.Clamp((int)(value.X + 0.5f), 0, 255),
        (byte)Math.Clamp((int)(value.Y + 0.5f), 0, 255),
        (byte)Math.Clamp((int)(value.Z + 0.5f), 0, 255),
        (byte)Math.Clamp((int)(value.W + 0.5f), 0, 255));

    // ----- Rotation / flipping -----

    /// <summary>Side of the square block a transposing rotation works on, so both images stay in cache.</summary>
    private const int TransposeBlock = 32;

    public static ImageFrame<TPixel> Rotate90<TPixel>(ImageFrame<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
        => Transpose(source, flipRows: true);

    public static ImageFrame<TPixel> Rotate270<TPixel>(ImageFrame<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
        => Transpose(source, flipRows: false);

    /// <summary>
    /// Transposes the frame, reversing either the source rows (a quarter turn clockwise) or the source
    /// columns (counter-clockwise). Both images are walked in square blocks: a naive double loop strides the
    /// full width of one of them on every step, which misses the cache on all but the narrowest images.
    /// </summary>
    private static ImageFrame<TPixel> Transpose<TPixel>(ImageFrame<TPixel> source, bool flipRows)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = source.Height;
        int height = source.Width;
        ImageFrame<TPixel> dest = FrameFactory.CreateUninitialized<TPixel>(width, height);
        int sourceHeight = source.Height;
        int sourceWidth = source.Width;

        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            for (int y0 = startRow; y0 < endRow; y0 += TransposeBlock)
            {
                int yEnd = Math.Min(y0 + TransposeBlock, endRow);
                for (int x0 = 0; x0 < width; x0 += TransposeBlock)
                {
                    int xEnd = Math.Min(x0 + TransposeBlock, width);
                    for (int y = y0; y < yEnd; y++)
                    {
                        Span<TPixel> destRow = dest.GetRowSpan(y);
                        int sourceX = flipRows ? y : sourceWidth - 1 - y;
                        for (int x = x0; x < xEnd; x++)
                        {
                            Span<TPixel> sourceRow = source.GetRowSpan(flipRows ? sourceHeight - 1 - x : x);
                            destRow[x] = sourceRow[sourceX];
                        }
                    }
                }
            }
        });

        return dest;
    }

    public static ImageFrame<TPixel> Rotate180<TPixel>(ImageFrame<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = source.Width;
        int height = source.Height;
        ImageFrame<TPixel> dest = FrameFactory.CreateUninitialized<TPixel>(width, height);
        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            for (int y = startRow; y < endRow; y++)
            {
                Span<TPixel> destRow = dest.GetRowSpan(y);
                source.GetRowSpan(height - 1 - y).CopyTo(destRow);
                destRow.Reverse();
            }
        });

        return dest;
    }

    /// <summary>Rotates clockwise by an arbitrary angle with bilinear sampling, expanding the canvas.</summary>
    public static ImageFrame<TPixel> RotateArbitrary<TPixel>(ImageFrame<TPixel> source, float degrees, Rgba32 background)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int w = source.Width;
        int h = source.Height;
        int newW = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin)));
        int newH = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos)));

        // Pre-convert the source to Rgba32 for fast repeated sampling.
        var src = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            PixelOps.ToRgba32<TPixel>(source.GetRowSpan(y), src.AsSpan(y * w, w));
        }

        var dest = new ImageFrame<TPixel>(newW, newH);
        var destRow = new Rgba32[newW];
        double halfDestW = newW / 2.0;
        double halfDestH = newH / 2.0;
        double halfSrcW = w / 2.0;
        double halfSrcH = h / 2.0;

        for (int dy = 0; dy < newH; dy++)
        {
            double ty = dy + 0.5 - halfDestH;
            for (int dx = 0; dx < newW; dx++)
            {
                double tx = dx + 0.5 - halfDestW;
                double sx = (cos * tx) + (sin * ty) + halfSrcW - 0.5;
                double sy = (-sin * tx) + (cos * ty) + halfSrcH - 0.5;
                destRow[dx] = BilinearSample(src, w, h, sx, sy, background);
            }

            PixelOps.FromRgba32<TPixel>(destRow, dest.GetRowSpan(dy));
        }

        return dest;
    }

    private static Rgba32 BilinearSample(Rgba32[] src, int width, int height, double x, double y, Rgba32 background)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        if (x0 < -1 || y0 < -1 || x0 >= width || y0 >= height)
        {
            return background;
        }

        float fx = (float)(x - x0);
        float fy = (float)(y - y0);

        Vector4 p00 = SampleOrBackground(src, width, height, x0, y0, background);
        Vector4 p10 = SampleOrBackground(src, width, height, x0 + 1, y0, background);
        Vector4 p01 = SampleOrBackground(src, width, height, x0, y0 + 1, background);
        Vector4 p11 = SampleOrBackground(src, width, height, x0 + 1, y0 + 1, background);

        Vector4 top = Vector4.Lerp(p00, p10, fx);
        Vector4 bottom = Vector4.Lerp(p01, p11, fx);
        return ToRgba32(Vector4.Lerp(top, bottom, fy));
    }

    private static Vector4 SampleOrBackground(Rgba32[] src, int width, int height, int x, int y, Rgba32 background)
    {
        Rgba32 p = (uint)x < (uint)width && (uint)y < (uint)height ? src[(y * width) + x] : background;
        return new Vector4(p.R, p.G, p.B, p.A);
    }

    public static ImageFrame<TPixel> Flip<TPixel>(ImageFrame<TPixel> source, FlipMode mode)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = source.Width;
        int height = source.Height;
        ImageFrame<TPixel> dest = FrameFactory.CreateUninitialized<TPixel>(width, height);
        bool horizontal = mode == FlipMode.Horizontal;
        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            for (int y = startRow; y < endRow; y++)
            {
                Span<TPixel> destRow = dest.GetRowSpan(y);
                source.GetRowSpan(horizontal ? y : height - 1 - y).CopyTo(destRow);
                if (horizontal)
                {
                    destRow.Reverse();
                }
            }
        });

        return dest;
    }

    // ----- Crop / pad / composite -----

    public static ImageFrame<TPixel> Crop<TPixel>(ImageFrame<TPixel> source, Rectangle bounds)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (bounds.Width <= 0 || bounds.Height <= 0
            || bounds.X < 0 || bounds.Y < 0
            || bounds.Right > source.Width || bounds.Bottom > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds), bounds, $"Crop rectangle must lie inside the image bounds {source.Width}x{source.Height}.");
        }

        var dest = new ImageFrame<TPixel>(bounds.Width, bounds.Height);
        for (int y = 0; y < bounds.Height; y++)
        {
            source.GetRowSpan(bounds.Y + y).Slice(bounds.X, bounds.Width).CopyTo(dest.GetRowSpan(y));
        }

        return dest;
    }

    /// <summary>Centers the source on a canvas of the given size; crops when the canvas is smaller.</summary>
    /// <summary>Centers the source on a canvas of the given size; crops when the canvas is smaller.</summary>
    public static ImageFrame<TPixel> PadToCanvas<TPixel>(ImageFrame<TPixel> source, int width, int height, Rgba32 background)
        where TPixel : unmanaged, IPixel<TPixel>
        => PlaceOnCanvas(source, width, height, (width - source.Width) / 2, (height - source.Height) / 2, background);

    /// <summary>
    /// Places the source on a canvas of the given size with its top-left corner at (<paramref name="offsetX"/>,
    /// <paramref name="offsetY"/>); parts outside the canvas are clipped and the rest is filled with
    /// <paramref name="background"/>. Negative offsets therefore crop.
    /// </summary>
    public static ImageFrame<TPixel> PlaceOnCanvas<TPixel>(
        ImageFrame<TPixel> source, int width, int height, int offsetX, int offsetY, Rgba32 background)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));

        var dest = new ImageFrame<TPixel>(width, height);
        int srcStartX = Math.Max(0, -offsetX);
        int srcStartY = Math.Max(0, -offsetY);
        int destStartX = Math.Max(0, offsetX);
        int destStartY = Math.Max(0, offsetY);
        int copyWidth = Math.Min(source.Width - srcStartX, width - destStartX);
        int copyHeight = Math.Min(source.Height - srcStartY, height - destStartY);

        // Only paint the background where the source does not fully cover the canvas.
        if (copyWidth < width || copyHeight < height)
        {
            dest.PixelSpan.Fill(TPixel.FromRgba32(background));
        }

        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return dest; // The source lies entirely outside the canvas.
        }

        for (int y = 0; y < copyHeight; y++)
        {
            source.GetRowSpan(srcStartY + y).Slice(srcStartX, copyWidth)
                .CopyTo(dest.GetRowSpan(destStartY + y)[destStartX..]);
        }

        return dest;
    }

    /// <summary>Offset of <paramref name="content"/> inside <paramref name="canvas"/> along one axis for an anchor.</summary>
    public static int AnchorOffset(AnchorPositionMode anchor, bool horizontal, int canvas, int content)
    {
        bool start = horizontal
            ? anchor is AnchorPositionMode.Left or AnchorPositionMode.TopLeft or AnchorPositionMode.BottomLeft
            : anchor is AnchorPositionMode.Top or AnchorPositionMode.TopLeft or AnchorPositionMode.TopRight;
        bool end = horizontal
            ? anchor is AnchorPositionMode.Right or AnchorPositionMode.TopRight or AnchorPositionMode.BottomRight
            : anchor is AnchorPositionMode.Bottom or AnchorPositionMode.BottomLeft or AnchorPositionMode.BottomRight;
        if (start)
        {
            return 0;
        }

        if (end)
        {
            return canvas - content;
        }

        return (canvas - content) / 2;
    }

    // ----- Entropy crop -----

    /// <summary>
    /// Finds the bounding box of "interesting" content: the Sobel gradient magnitude of the luminance is normalised
    /// so a full-contrast step edge scores 1.0, and every pixel scoring at least <paramref name="threshold"/> is
    /// kept. Returns the full frame when nothing exceeds the threshold.
    /// </summary>
    public static Rectangle EntropyCropBounds<TPixel>(ImageFrame<TPixel> source, float threshold)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = source.Width;
        int height = source.Height;
        Rectangle full = new(0, 0, width, height);
        if (width < 3 || height < 3)
        {
            return full;
        }

        var gray = new byte[width * height];
        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            var row = new Rgba32[width];
            for (int y = startRow; y < endRow; y++)
            {
                PixelOps.ToRgba32<TPixel>(source.GetRowSpan(y), row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    gray[offset + x] = PixelOps.Luminance8(row[x]);
                }
            }
        });

        // A black-to-white step gives |gx| = 4 * 255, so dividing by 4 * 255 maps a full-contrast edge to 1.
        float cutoff = Math.Clamp(threshold, 0f, 1f) * 4f * 255f;
        float cutoffSquared = cutoff * cutoff;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;
        object gate = new();
        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            int localMinX = int.MaxValue;
            int localMinY = int.MaxValue;
            int localMaxX = -1;
            int localMaxY = -1;
            for (int y = startRow; y < endRow; y++)
            {
                int up = Math.Max(0, y - 1) * width;
                int mid = y * width;
                int down = Math.Min(height - 1, y + 1) * width;
                for (int x = 0; x < width; x++)
                {
                    int left = Math.Max(0, x - 1);
                    int right = Math.Min(width - 1, x + 1);
                    int gx = (gray[up + right] + (2 * gray[mid + right]) + gray[down + right])
                        - (gray[up + left] + (2 * gray[mid + left]) + gray[down + left]);
                    int gy = (gray[down + left] + (2 * gray[down + x]) + gray[down + right])
                        - (gray[up + left] + (2 * gray[up + x]) + gray[up + right]);
                    if (((float)gx * gx) + ((float)gy * gy) >= cutoffSquared)
                    {
                        if (x < localMinX)
                        {
                            localMinX = x;
                        }

                        if (x > localMaxX)
                        {
                            localMaxX = x;
                        }

                        if (y < localMinY)
                        {
                            localMinY = y;
                        }

                        localMaxY = y;
                    }
                }
            }

            if (localMaxX >= 0)
            {
                lock (gate)
                {
                    minX = Math.Min(minX, localMinX);
                    minY = Math.Min(minY, localMinY);
                    maxX = Math.Max(maxX, localMaxX);
                    maxY = Math.Max(maxY, localMaxY);
                }
            }
        });

        if (maxX < 0)
        {
            return full;
        }

        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Source-over compositing of <paramref name="source"/> onto <paramref name="background"/> using
    /// straight (non-premultiplied) alpha. With an opaque background this is
    /// <c>src * a + bg * (1 - a)</c> per channel with alpha 255.
    /// </summary>
    public static Rgba32 SourceOver(Rgba32 source, Rgba32 background)
    {
        if (source.A == byte.MaxValue || background.A == 0)
        {
            return source;
        }

        float sourceAlpha = source.A / 255f;
        float backgroundWeight = (background.A / 255f) * (1f - sourceAlpha);
        float outAlpha = sourceAlpha + backgroundWeight;
        if (outAlpha <= 0f)
        {
            return Rgba32.Transparent;
        }

        Vector4 blended = ((new Vector4(source.R, source.G, source.B, 0f) * sourceAlpha)
            + (new Vector4(background.R, background.G, background.B, 0f) * backgroundWeight)) / outAlpha;
        return ToRgba32(new Vector4(blended.X, blended.Y, blended.Z, outAlpha * 255f));
    }

    public static void DrawImage<TPixel>(ImageFrame<TPixel> destination, Image source, Point location, float opacity)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int startX = Math.Max(0, location.X);
        int startY = Math.Max(0, location.Y);
        int endX = Math.Min(destination.Width, location.X + source.Width);
        int endY = Math.Min(destination.Height, location.Y + source.Height);

        for (int y = startY; y < endY; y++)
        {
            Span<TPixel> destRow = destination.GetRowSpan(y);
            for (int x = startX; x < endX; x++)
            {
                Rgba32 src = source.GetPixelRgba32(x - location.X, y - location.Y);
                float alpha = src.A / 255f * opacity;
                if (alpha <= 0f)
                {
                    continue;
                }

                Rgba32 dst = destRow[x].ToRgba32();
                float inverse = 1f - alpha;
                destRow[x] = TPixel.FromRgba32(new Rgba32(
                    (byte)Math.Clamp((int)((src.R * alpha) + (dst.R * inverse) + 0.5f), 0, 255),
                    (byte)Math.Clamp((int)((src.G * alpha) + (dst.G * inverse) + 0.5f), 0, 255),
                    (byte)Math.Clamp((int)((src.B * alpha) + (dst.B * inverse) + 0.5f), 0, 255),
                    (byte)Math.Clamp((int)((alpha * 255f) + (dst.A * inverse) + 0.5f), 0, 255)));
            }
        }
    }
}
