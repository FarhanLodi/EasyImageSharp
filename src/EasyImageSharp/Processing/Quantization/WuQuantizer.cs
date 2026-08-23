using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Xiaolin Wu's colour quantizer (v2): a 3-D moment histogram over 5 bits per channel is split into boxes by
/// repeatedly cutting the box whose split reduces the total variance the most; every box becomes one palette
/// colour at its mean. Fast, deterministic and the default choice for photographic content. Alpha is handled by
/// thresholding: pixels below <see cref="QuantizerOptions.TransparencyThreshold"/> share one transparent entry.
/// </summary>
public sealed class WuQuantizer : IQuantizer
{
    /// <summary>Creates a Wu quantizer with default options (256 colours, Floyd–Steinberg dithering).</summary>
    public WuQuantizer()
        : this(new QuantizerOptions())
    {
    }

    /// <summary>Creates a Wu quantizer with the given options.</summary>
    public WuQuantizer(QuantizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Options = options;
    }

    public QuantizerOptions Options { get; }

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => new WuFrameQuantizer<TPixel>(this.Options);

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>(QuantizerOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        return new WuFrameQuantizer<TPixel>(options);
    }

    private sealed class WuFrameQuantizer<TPixel> : QuantizerBase<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly WuHistogram histogram = new();
        private readonly object mergeLock = new();

        public WuFrameQuantizer(QuantizerOptions options)
            : base(options, fixedPalette: false)
        {
        }

        protected override bool AccumulateColors(ImageFrame<TPixel> frame, Rectangle bounds)
        {
            byte cutoff = this.AlphaCutoff;
            Configuration configuration = Configuration.Default;
            long pixels = (long)bounds.Width * bounds.Height;
            bool parallel = configuration.MaxDegreeOfParallelism > 1 && bounds.Height >= 2
                && pixels >= configuration.MinimumPixelsPerTask * 2L;

            if (!parallel)
            {
                var row = new Rgba32[bounds.Width];
                bool transparent = false;
                for (int y = bounds.Y; y < bounds.Bottom; y++)
                {
                    ConvertRow(frame, y, bounds, row);
                    transparent |= this.histogram.Add(row, cutoff);
                }

                return transparent;
            }

            // Each row batch fills its own histogram; the partial histograms are summed under a lock.
            bool anyTransparent = false;
            ParallelRowIterator.IterateRows(bounds.Width, bounds.Height, (startRow, endRow) =>
            {
                var local = new WuHistogram();
                var row = new Rgba32[bounds.Width];
                bool transparent = false;
                for (int y = startRow; y < endRow; y++)
                {
                    ConvertRow(frame, bounds.Y + y, bounds, row);
                    transparent |= local.Add(row, cutoff);
                }

                lock (this.mergeLock)
                {
                    this.histogram.Merge(local);
                    anyTransparent |= transparent;
                }
            });

            return anyTransparent;
        }

        protected override Rgba32[] BuildPaletteCore(int maxColors) => this.histogram.BuildPalette(maxColors);
    }
}
