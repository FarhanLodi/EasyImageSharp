using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Streaming;

/// <summary>
/// A pipeline stage that resamples a frame fed to it one source row at a time and passes every output row on
/// to the next stage as soon as its vertical kernel window is complete, so a resize never holds more than a
/// window's worth of rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly the arithmetic of the in-memory resize.</b> The kernel weights come from the same
/// <see cref="ResizeKernelMap"/>, each source row goes through the same conversion and horizontal pass, and the
/// vertical taps are accumulated with the same <see cref="FrameOps.AccumulateScaled"/> in the same ascending
/// order, first tap assigned rather than added, before the same store. Every output pixel is therefore the very
/// float sum <c>Mutate(ctx =&gt; ctx.Resize(...))</c> computes, rounded the same way: the results are
/// bit-identical for every resampler, pixel format, premultiplied-alpha and companding setting. Nearest-neighbour
/// picks the same source pixel per output pixel as the in-memory path does.
/// </para>
/// <para>
/// <b>Two ways of holding the vertical window</b>, chosen per resize for whichever needs fewer rows:
/// a <em>ring</em> of horizontally resampled source rows, as long as the longest vertical window, from which an
/// output row is computed once its last source row arrives (best when enlarging, where a window spans a few
/// source rows); or a set of <em>open accumulators</em>, one per output row whose window has started but not
/// finished, into which every arriving row is folded at once (best when reducing, where a window spans
/// <c>2 x radius / scale</c> source rows but only about <c>2 x radius</c> output rows are open at a time). Both
/// perform the same additions in the same order, so the choice changes memory, never the result.
/// </para>
/// </remarks>
/// <typeparam name="TPixel">The pixel format of the rows.</typeparam>
internal sealed class ScanlineResizer<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly IScanlineSink<TPixel> next;
    private readonly int width;
    private readonly int height;
    private readonly IResampler sampler;
    private readonly bool premultiplyRequested;
    private readonly bool compand;
    private readonly long workingSetBudget;

    private Mode mode;
    private int sourceWidth;
    private int sourceHeight;
    private int rowsReceived;
    private int nextOutput;

    private ResizeKernelMap? horizontal;
    private ResizeKernelMap? vertical;

    /// <summary><see cref="PixelFormatInfo{TPixel}.HasAlpha"/> and the caller's wish, as the in-memory resize combines them.</summary>
    private bool premultiply;

    /// <summary>Channels carried per pixel while filtering: 1 for <see cref="L8"/>, otherwise 4.</summary>
    private int channels;

    /// <summary>Rows in the ring or open accumulators held in <see cref="window"/>.</summary>
    private int windowRows;

    /// <summary>Ring rows or accumulators, <see cref="windowRows"/> rows of <c>width * channels</c> floats.</summary>
    private float[]? window;

    /// <summary>One more row: the vertical accumulator (ring) or the horizontally resampled current row (accumulators).</summary>
    private float[]? scratch;

    // Accumulator mode: the first output whose window has not ended yet, and the last whose window has started.
    private int firstOpen;
    private int lastStarted = -1;

    // Per-row scratch: the incoming row widened to filter space, and the outgoing row in pixels.
    private Rgba32[]? sourceRgba;
    private float[]? sourceValues;
    private Rgba32[]? outputRgba;
    private TPixel[]? output;

    /// <summary>Nearest-neighbour column map, or <see langword="null"/> when the width does not change.</summary>
    private int[]? columns;

    /// <summary>Creates the stage.</summary>
    /// <param name="next">The stage that receives the resampled rows.</param>
    /// <param name="width">The width the rows are resampled to.</param>
    /// <param name="height">The number of rows produced.</param>
    /// <param name="sampler">The resampling kernel.</param>
    /// <param name="premultiplyAlpha">Whether colour is weighted by alpha while filtering (formats with alpha only).</param>
    /// <param name="compand">Whether to filter in linear light.</param>
    /// <param name="workingSetBudget">The most bytes the stage may hold for its rows; checked before they are allocated.</param>
    public ScanlineResizer(
        IScanlineSink<TPixel> next, int width, int height, IResampler sampler, bool premultiplyAlpha, bool compand, long workingSetBudget)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(sampler);
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.next = next;
        this.width = width;
        this.height = height;
        this.sampler = sampler;
        this.premultiplyRequested = premultiplyAlpha;
        this.compand = compand;
        this.workingSetBudget = workingSetBudget;
    }

    private enum Mode
    {
        NotStarted,
        PassThrough,
        Nearest,
        Ring,
        Accumulate,
        Completed,
        Disposed,
    }

    /// <summary>
    /// The number of rows each strategy would hold for this resize: the ring needs the longest vertical
    /// window, the accumulators the largest number of output windows any one source row falls into.
    /// </summary>
    internal static (int RingRows, int AccumulatorRows) WindowRows(ResizeKernelMap vertical, int sourceHeight)
    {
        int outputs = vertical.Start.Length;
        int first = 0;
        int last = -1;
        int most = 1;
        for (int r = 0; r < sourceHeight; r++)
        {
            while (first < outputs && vertical.Start[first] + vertical.Length[first] <= r)
            {
                first++;
            }

            while (last + 1 < outputs && vertical.Start[last + 1] <= r)
            {
                last++;
            }

            most = Math.Max(most, last - first + 1);
        }

        return (vertical.MaxLength, most);
    }

    /// <summary>The strategy <see cref="Begin"/> picked (PassThrough, Nearest, Ring or Accumulate); for tests.</summary>
    internal string? Strategy { get; private set; }

    /// <summary>For tests: <see langword="true"/> forces the ring, <see langword="false"/> the accumulators.</summary>
    internal bool? ForceRing { get; init; }

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (this.mode != Mode.NotStarted)
        {
            throw new InvalidOperationException("The resize stage has already been started.");
        }

        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.sourceWidth = width;
        this.sourceHeight = height;

        if (width == this.width && height == this.height)
        {
            // FrameOps.Resize returns the frame untouched in this case, so the rows pass through untouched too.
            this.mode = Mode.PassThrough;
        }
        else if (!(this.sampler.Radius > 0f) || !float.IsFinite(this.sampler.Radius) || this.sampler.Radius > 1048576f)
        {
            // FrameOps.Resize's own validation, made before anything is sized from the radius.
            throw new ArgumentException("Resampler radius must be finite, positive, and no greater than 1048576.", nameof(this.sampler));
        }
        else if (this.sampler is NearestNeighborResampler)
        {
            this.CheckBudget(0);
            this.StartNearest();
        }
        else
        {
            this.StartWeighted();
        }

        this.Strategy = this.mode.ToString();
        this.next.Begin(this.width, this.height, metadata);
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        if (this.mode is Mode.NotStarted or Mode.Completed or Mode.Disposed)
        {
            ObjectDisposedException.ThrowIf(this.mode == Mode.Disposed, this);
            throw new InvalidOperationException($"WriteRow is not valid while the resize stage is {this.mode}.");
        }

        if (this.rowsReceived >= this.sourceHeight)
        {
            throw new InvalidOperationException($"All {this.sourceHeight} source rows have already been written.");
        }

        if (row.Length < this.sourceWidth)
        {
            throw new ArgumentException($"The row holds {row.Length} pixels but the source is {this.sourceWidth} wide.", nameof(row));
        }

        OperationExecution.Checkpoint();
        row = row[..this.sourceWidth];
        int r = this.rowsReceived++;
        switch (this.mode)
        {
            case Mode.PassThrough:
                this.next.WriteRow(row);
                this.nextOutput++;
                break;
            case Mode.Nearest:
                this.NearestRow(row, r);
                break;
            case Mode.Ring:
                this.RingRow(row, r);
                break;
            default:
                this.AccumulateRow(row, r);
                break;
        }
    }

    public void Complete()
    {
        if (this.mode is Mode.NotStarted or Mode.Completed or Mode.Disposed)
        {
            ObjectDisposedException.ThrowIf(this.mode == Mode.Disposed, this);
            throw new InvalidOperationException($"Complete is not valid while the resize stage is {this.mode}.");
        }

        if (this.rowsReceived != this.sourceHeight || this.nextOutput != this.height)
        {
            throw new InvalidOperationException(
                $"Only {this.rowsReceived} of {this.sourceHeight} source rows were written to the resize stage.");
        }

        this.mode = Mode.Completed;
        this.ReleaseBuffers();
        this.next.Complete();
    }

    public void Dispose()
    {
        if (this.mode == Mode.Disposed)
        {
            return;
        }

        this.mode = Mode.Disposed;
        this.ReleaseBuffers();
        this.next.Dispose();
    }

    // ----- Nearest neighbour -----

    private void StartNearest()
    {
        this.mode = Mode.Nearest;
        this.output = ArrayPool<TPixel>.Shared.Rent(this.width);
        if (this.width != this.sourceWidth)
        {
            this.columns = ArrayPool<int>.Shared.Rent(this.width);
            for (int x = 0; x < this.width; x++)
            {
                this.columns[x] = Math.Min((int)(((long)x * this.sourceWidth) / this.width), this.sourceWidth - 1);
            }
        }
    }

    /// <summary>Emits every output row whose nearest source row is <paramref name="r"/>, as FrameOps' nearest resize picks them.</summary>
    private void NearestRow(ReadOnlySpan<TPixel> row, int r)
    {
        while (this.nextOutput < this.height
            && Math.Min((int)(((long)this.nextOutput * this.sourceHeight) / this.height), this.sourceHeight - 1) == r)
        {
            if (this.columns is null)
            {
                this.next.WriteRow(row);
            }
            else
            {
                Span<TPixel> destination = this.output.AsSpan(0, this.width);
                for (int x = 0; x < destination.Length; x++)
                {
                    destination[x] = row[this.columns[x]];
                }

                this.next.WriteRow(destination);
            }

            this.nextOutput++;
        }
    }

    // ----- Weighted kernels -----

    private void StartWeighted()
    {
        // The kernel maps are sized by the resize alone, but can still be large (an extreme enlargement), so they
        // are estimated against the budget before they are built.
        long mapBytes = EstimateMapBytes(this.sourceWidth, this.width, this.sampler) + EstimateMapBytes(this.sourceHeight, this.height, this.sampler);
        this.CheckBudget(mapBytes);
        this.horizontal = ResizeKernelMap.Build(this.sourceWidth, this.width, this.sampler);
        this.vertical = ResizeKernelMap.Build(this.sourceHeight, this.height, this.sampler);
        this.premultiply = this.premultiplyRequested && PixelFormatInfo<TPixel>.HasAlpha;
        this.channels = typeof(TPixel) == typeof(L8) ? 1 : 4;

        (int ringRows, int accumulatorRows) = WindowRows(this.vertical, this.sourceHeight);
        bool accumulate = this.ForceRing is { } ring ? !ring : accumulatorRows < ringRows;
        this.windowRows = accumulate ? accumulatorRows : ringRows;
        long rowFloats = (long)this.width * this.channels;
        long windowFloats = this.windowRows * rowFloats;
        this.CheckBudget(mapBytes + ((windowFloats + rowFloats) * sizeof(float)) + ((long)this.sourceWidth * 20));
        if (windowFloats > Array.MaxLength)
        {
            throw new ImageSizeLimitExceededException(
                $"Resizing to {this.width}x{this.height} row by row needs a window larger than the largest addressable buffer.");
        }

        this.window = ArrayPool<float>.Shared.Rent((int)windowFloats);
        this.scratch = ArrayPool<float>.Shared.Rent((int)rowFloats);
        this.sourceValues = ArrayPool<float>.Shared.Rent(this.sourceWidth * this.channels);
        this.output = ArrayPool<TPixel>.Shared.Rent(this.width);
        if (this.channels == 4 && !PixelOps.IsHighPrecision<TPixel>())
        {
            this.sourceRgba = ArrayPool<Rgba32>.Shared.Rent(this.sourceWidth);
            this.outputRgba = ArrayPool<Rgba32>.Shared.Rent(this.width);
        }

        this.mode = accumulate ? Mode.Accumulate : Mode.Ring;
    }

    /// <summary>Ring strategy: filter the row into its slot, then emit every output whose window it completes.</summary>
    private void RingRow(ReadOnlySpan<TPixel> row, int r)
    {
        ResizeKernelMap map = this.vertical!;
        int firstNeeded = map.Start[0];
        int lastNeeded = map.Start[this.height - 1] + map.Length[this.height - 1] - 1;
        if (r < firstNeeded || r > lastNeeded)
        {
            return; // No window reads this row; FrameOps never filters it either.
        }

        int rowFloats = this.width * this.channels;
        this.FilterRow(row, this.RowSpan(r));
        Span<float> accumulator = this.scratch.AsSpan(0, rowFloats);
        while (this.nextOutput < this.height && map.Start[this.nextOutput] + map.Length[this.nextOutput] - 1 <= r)
        {
            // FrameOps.VerticalPass, with the window's rows found in the ring instead of a contiguous chunk.
            int y = this.nextOutput;
            int start = map.Start[y];
            int length = map.Length[y];
            ReadOnlySpan<float> weights = map.Weights.AsSpan(map.Offset[y], length);
            for (int i = 0; i < length; i++)
            {
                FrameOps.AccumulateScaled(this.RowSpan(start + i), accumulator, weights[i], i == 0);
            }

            this.Emit(accumulator);
            this.nextOutput++;
        }
    }

    /// <summary>Accumulator strategy: fold the filtered row into every open output it belongs to, then emit the finished ones.</summary>
    private void AccumulateRow(ReadOnlySpan<TPixel> row, int r)
    {
        ResizeKernelMap map = this.vertical!;
        while (this.firstOpen < this.height && map.Start[this.firstOpen] + map.Length[this.firstOpen] <= r)
        {
            this.firstOpen++;
        }

        while (this.lastStarted + 1 < this.height && map.Start[this.lastStarted + 1] <= r)
        {
            this.lastStarted++;
        }

        if (this.firstOpen > this.lastStarted)
        {
            return; // No window reads this row.
        }

        Span<float> filtered = this.scratch.AsSpan(0, this.width * this.channels);
        this.FilterRow(row, filtered);

        // Output y's taps run over source rows Start[y], Start[y] + 1, ... so this row is its tap r - Start[y],
        // and rows arrive in ascending order: each accumulator sees the same additions in the same order as
        // FrameOps.VerticalPass makes them.
        for (int y = this.firstOpen; y <= this.lastStarted; y++)
        {
            int tap = r - map.Start[y];
            FrameOps.AccumulateScaled(filtered, this.RowSpan(y), map.Weights[map.Offset[y] + tap], tap == 0);
        }

        while (this.nextOutput < this.height && map.Start[this.nextOutput] + map.Length[this.nextOutput] - 1 <= r)
        {
            this.Emit(this.RowSpan(this.nextOutput));
            this.nextOutput++;
        }
    }

    /// <summary>
    /// The window row that holds source row <paramref name="index"/> (ring) or output row <paramref name="index"/>
    /// (accumulators). Indices that are alive at the same time are fewer than <see cref="windowRows"/> apart, so
    /// they never share a slot.
    /// </summary>
    private Span<float> RowSpan(int index)
    {
        int rowFloats = this.width * this.channels;
        return this.window.AsSpan((index % this.windowRows) * rowFloats, rowFloats);
    }

    /// <summary>Converts one source row to filter space and applies the horizontal kernel, exactly as FrameOps does.</summary>
    private void FilterRow(ReadOnlySpan<TPixel> row, Span<float> destination)
    {
        ResizeKernelMap map = this.horizontal!;
        int sourceWidth = this.sourceWidth;
        if (this.channels == 1)
        {
            // FrameOps.ResizeChunkSingleChannel: L8 samples, optionally through the sRGB decode table.
            ReadOnlySpan<byte> samples = MemoryMarshal.AsBytes(row);
            Span<float> values = this.sourceValues.AsSpan(0, sourceWidth);
            if (this.compand)
            {
                float[] toLinear = SrgbCompanding.ToLinear255;
                for (int x = 0; x < sourceWidth; x++)
                {
                    values[x] = toLinear[samples[x]];
                }
            }
            else
            {
                for (int x = 0; x < sourceWidth; x++)
                {
                    values[x] = samples[x];
                }
            }

            HorizontalPassSingleChannel(values, destination, map);
            return;
        }

        Span<Vector4> vectors = MemoryMarshal.Cast<float, Vector4>(this.sourceValues.AsSpan(0, sourceWidth * 4));
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                vectors[x] = HighPrecisionSampling.Load(row[x].ToScaledVector4(), this.premultiply, this.compand);
            }
        }
        else
        {
            Span<Rgba32> rgba = this.sourceRgba.AsSpan(0, sourceWidth);
            PixelOps.ToRgba32<TPixel>(row, rgba);
            FrameOps.LoadRow(rgba, vectors, this.premultiply, this.compand);
        }

        FrameOps.HorizontalPass(vectors, MemoryMarshal.Cast<float, Vector4>(destination), map);
    }

    /// <summary>Stores one accumulated row as pixels, exactly as FrameOps does, and passes it on.</summary>
    private void Emit(Span<float> accumulated)
    {
        Span<TPixel> pixels = this.output.AsSpan(0, this.width);
        if (this.channels == 1)
        {
            // FrameOps.ResizeChunkSingleChannel's store, through the same grey table.
            TPixel[] lut = GrayLut.Table;
            for (int x = 0; x < pixels.Length; x++)
            {
                float value = this.compand ? SrgbCompanding.ToSrgb255(accumulated[x]) : accumulated[x];
                pixels[x] = lut[(byte)Math.Clamp((int)(value + 0.5f), 0, 255)];
            }
        }
        else
        {
            Span<Vector4> vectors = MemoryMarshal.Cast<float, Vector4>(accumulated);
            if (PixelOps.IsHighPrecision<TPixel>())
            {
                for (int x = 0; x < pixels.Length; x++)
                {
                    pixels[x] = TPixel.FromScaledVector4(HighPrecisionSampling.Store(vectors[x], this.premultiply, this.compand));
                }
            }
            else
            {
                Span<Rgba32> rgba = this.outputRgba.AsSpan(0, this.width);
                for (int x = 0; x < rgba.Length; x++)
                {
                    rgba[x] = FrameOps.StorePixel(vectors[x], this.premultiply, this.compand);
                }

                PixelOps.FromRgba32<TPixel>(rgba, pixels);
            }
        }

        this.next.WriteRow(pixels);
    }

    /// <summary>The single-channel horizontal pass, the same loop as FrameOps' private one.</summary>
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
    /// An upper bound on the bytes <see cref="ResizeKernelMap.Build"/> allocates for one axis: three integers per
    /// destination index plus one weight per tap, and no window is longer than the kernel's support or the source.
    /// </summary>
    private static long EstimateMapBytes(int sourceSize, int destinationSize, IResampler sampler)
    {
        double filterScale = Math.Max(1.0, (double)sourceSize / destinationSize);
        double support = (2.0 * sampler.Radius * filterScale) + 2.0;
        long taps = (long)Math.Min(sourceSize, double.IsFinite(support) ? Math.Ceiling(support) : sourceSize);
        return (long)destinationSize * ((3 * sizeof(int)) + (taps * sizeof(float)));
    }

    private void CheckBudget(long windowBytes)
    {
        long rowBytes = (long)this.width * (Unsafe.SizeOf<TPixel>() + Unsafe.SizeOf<Rgba32>());
        if (windowBytes + rowBytes > this.workingSetBudget)
        {
            throw new ImageSizeLimitExceededException(
                $"Resizing {this.sourceWidth}x{this.sourceHeight} to {this.width}x{this.height} row by row needs about "
                + $"{windowBytes + rowBytes:N0} bytes of rows, which exceeds the streaming working-set budget of "
                + $"{this.workingSetBudget:N0} bytes.");
        }
    }

    private void ReleaseBuffers()
    {
        Return(ref this.window);
        Return(ref this.scratch);
        Return(ref this.sourceValues);
        Return(ref this.sourceRgba);
        Return(ref this.outputRgba);
        Return(ref this.output);
        Return(ref this.columns);
    }

    private static void Return<T>(ref T[]? array)
    {
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
            array = null;
        }
    }

    /// <summary>Maps a filtered grey level onto the pixel format exactly as FrameOps' grey table does.</summary>
    private static class GrayLut
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
}
