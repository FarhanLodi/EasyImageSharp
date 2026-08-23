using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Dithering;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Shared machinery for pixel-specific quantizers: alpha thresholding, the lossless small-palette shortcut,
/// lazy palette construction, the palette lookup map and the (optionally dithered) mapping pass. Concrete
/// algorithms only accumulate colour statistics and turn them into an opaque palette.
/// </summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
internal abstract class QuantizerBase<TPixel> : IQuantizer<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly bool fixedPalette;
    private readonly DistinctColorTracker? tracker;
    private bool colorsAdded;
    private bool hasTransparentPixels;
    private bool paletteDirty = true;
    private Rgba32[] paletteRgba = Array.Empty<Rgba32>();
    private TPixel[] palettePixels = Array.Empty<TPixel>();
    private PaletteIndexMap? map;

    protected QuantizerBase(QuantizerOptions options, bool fixedPalette)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Options = options;
        this.fixedPalette = fixedPalette;
        this.AlphaCutoff = options.AlphaCutoff;
        this.tracker = fixedPalette ? null : new DistinctColorTracker(options.MaxColors);
    }

    public QuantizerOptions Options { get; }

    public ReadOnlyMemory<TPixel> Palette
    {
        get
        {
            this.EnsurePalette();
            return this.palettePixels;
        }
    }

    /// <summary>The 0-255 alpha value below which pixels are transparent.</summary>
    protected byte AlphaCutoff { get; }

    /// <summary>The current palette as <see cref="Rgba32"/> values (built on demand).</summary>
    internal ReadOnlySpan<Rgba32> PaletteRgba
    {
        get
        {
            this.EnsurePalette();
            return this.paletteRgba;
        }
    }

    /// <summary>The lookup map for the current palette (built on demand); null while the palette is empty.</summary>
    internal PaletteIndexMap? Map
    {
        get
        {
            this.EnsurePalette();
            return this.map;
        }
    }

    public void AddPaletteColors(ImageFrame<TPixel> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        this.AddPaletteColors(frame, new Rectangle(0, 0, frame.Width, frame.Height));
    }

    public void AddPaletteColors(ImageFrame<TPixel> frame, Rectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateBounds(frame, bounds);
        if (this.fixedPalette)
        {
            return;
        }

        this.colorsAdded = true;
        this.paletteDirty = true;

        if (this.tracker is { Overflowed: false } tracker)
        {
            var row = new Rgba32[bounds.Width];
            for (int y = bounds.Y; y < bounds.Bottom && !tracker.Overflowed; y++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y).Slice(bounds.X, bounds.Width), row);
                tracker.Add(row, this.AlphaCutoff);
            }
        }

        this.hasTransparentPixels |= this.AccumulateColors(frame, bounds);
    }

    public IndexedImageFrame<TPixel> QuantizeFrame(ImageFrame<TPixel> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return this.QuantizeFrame(frame, new Rectangle(0, 0, frame.Width, frame.Height));
    }

    public IndexedImageFrame<TPixel> QuantizeFrame(ImageFrame<TPixel> frame, Rectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateBounds(frame, bounds);
        this.PrepareForFrame(frame, bounds);

        var result = new IndexedImageFrame<TPixel>(bounds.Width, bounds.Height, this.palettePixels);
        PaletteIndexMap map = this.map!;
        IDither? dither = this.Options.Dither;
        if (dither is not null && this.Options.DitherScale > 0f)
        {
            dither.Apply(frame, bounds, map, this.Options.DitherScale, result.IndexMemory, replacePixels: false);
            return result;
        }

        ParallelRowIterator.IterateRows(bounds.Width, bounds.Height, (startRow, endRow) =>
        {
            var row = new Rgba32[bounds.Width];
            for (int y = startRow; y < endRow; y++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(bounds.Y + y).Slice(bounds.X, bounds.Width), row);
                Span<byte> dest = result.GetWritableRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    dest[x] = (byte)map.GetPaletteIndex(row[x], out _);
                }
            }
        });

        return result;
    }

    /// <summary>Replaces every pixel of the region with its (dithered) palette colour without producing indices.</summary>
    internal void QuantizeFrameInPlace(ImageFrame<TPixel> frame, Rectangle bounds)
    {
        ValidateBounds(frame, bounds);
        this.PrepareForFrame(frame, bounds);

        PaletteIndexMap map = this.map!;
        IDither? dither = this.Options.Dither;
        if (dither is not null && this.Options.DitherScale > 0f)
        {
            dither.Apply(frame, bounds, map, this.Options.DitherScale, Memory<byte>.Empty, replacePixels: true);
            return;
        }

        TPixel[] palette = this.palettePixels;
        ParallelRowIterator.IterateRows(bounds.Width, bounds.Height, (startRow, endRow) =>
        {
            var row = new Rgba32[bounds.Width];
            for (int y = startRow; y < endRow; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(bounds.Y + y).Slice(bounds.X, bounds.Width);
                PixelOps.ToRgba32<TPixel>(pixels, row);
                for (int x = 0; x < row.Length; x++)
                {
                    pixels[x] = palette[map.GetPaletteIndex(row[x], out _)];
                }
            }
        });
    }

    public byte GetQuantizedColor(TPixel color, out TPixel match)
    {
        this.EnsurePalette();
        if (this.map is null)
        {
            throw new InvalidOperationException("The quantizer has no palette yet; add colours or quantize a frame first.");
        }

        int index = this.map.GetPaletteIndex(color.ToRgba32(), out _);
        match = this.palettePixels[index];
        return (byte)index;
    }

    // ----- Algorithm hooks -----

    /// <summary>
    /// Adds the pixels of the region to the algorithm's colour statistics, ignoring pixels whose alpha is below
    /// <see cref="AlphaCutoff"/>. Returns true when at least one such transparent pixel was seen.
    /// </summary>
    protected abstract bool AccumulateColors(ImageFrame<TPixel> frame, Rectangle bounds);

    /// <summary>
    /// Builds at most <paramref name="maxColors"/> palette colours from the accumulated statistics. Fixed-palette
    /// quantizers return their palette unchanged (transparent entries included); others return opaque colours
    /// only, the base class appends the transparent entry when it is needed.
    /// </summary>
    protected abstract Rgba32[] BuildPaletteCore(int maxColors);

    /// <summary>Converts a row of pixels to <see cref="Rgba32"/>; a small convenience for subclasses.</summary>
    protected static void ConvertRow(ImageFrame<TPixel> frame, int y, Rectangle bounds, Span<Rgba32> destination)
        => PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y).Slice(bounds.X, bounds.Width), destination);

    // ----- Helpers -----

    private void PrepareForFrame(ImageFrame<TPixel> frame, Rectangle bounds)
    {
        if (!this.colorsAdded && !this.fixedPalette)
        {
            this.AddPaletteColors(frame, bounds);
        }

        this.EnsurePalette();
        if (this.map is null)
        {
            throw new InvalidOperationException("The quantizer produced an empty palette.");
        }
    }

    private void EnsurePalette()
    {
        if (!this.paletteDirty)
        {
            return;
        }

        Rgba32[] palette;
        if (this.fixedPalette)
        {
            palette = this.BuildPaletteCore(this.Options.MaxColors);
        }
        else if (!this.colorsAdded)
        {
            palette = Array.Empty<Rgba32>();
        }
        else
        {
            int budget = this.Options.MaxColors - (this.hasTransparentPixels ? 1 : 0);
            Rgba32[] opaque;
            if (this.tracker is { Overflowed: false } tracker && tracker.Colors.Count > 0 && tracker.Colors.Count <= budget)
            {
                opaque = tracker.Colors.ToArray();
            }
            else
            {
                opaque = this.BuildPaletteCore(budget);
                if (opaque.Length > budget)
                {
                    Array.Resize(ref opaque, budget);
                }
            }

            if (this.hasTransparentPixels)
            {
                palette = new Rgba32[opaque.Length + 1];
                opaque.CopyTo(palette, 0);
                palette[^1] = Rgba32.Transparent;
            }
            else
            {
                palette = opaque;
            }
        }

        this.paletteRgba = palette;
        this.palettePixels = new TPixel[palette.Length];
        PixelOps.FromRgba32<TPixel>(palette, this.palettePixels);
        this.map = palette.Length == 0 ? null : new PaletteIndexMap(palette, this.Options.ColorMatchingMode, this.AlphaCutoff);
        this.paletteDirty = false;
    }

    private static void ValidateBounds(ImageFrame<TPixel> frame, Rectangle bounds)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || bounds.Right > frame.Width || bounds.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds), bounds, $"The region must lie inside the {frame.Width}x{frame.Height} frame.");
        }
    }
}
