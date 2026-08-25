using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Maps pixels to a fixed, caller-supplied palette of 1 to 256 colours (nearest colour in RGBA space, dithered as
/// configured). Pixels below the transparency threshold use the palette's first fully transparent entry when it
/// has one; otherwise they are matched like any other colour.
/// </summary>
public sealed class PaletteQuantizer : IQuantizer
{
    private readonly Rgba32[] palette;

    /// <summary>Creates a quantizer for the given palette with default options.</summary>
    public PaletteQuantizer(ReadOnlyMemory<Color> palette)
        : this(palette, new QuantizerOptions())
    {
    }

    /// <summary>Creates a quantizer for the given palette. <see cref="QuantizerOptions.MaxColors"/> is ignored; the palette is used as supplied.</summary>
    public PaletteQuantizer(ReadOnlyMemory<Color> palette, QuantizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (palette.Length is 0 or > 256)
        {
            throw new ArgumentException("A palette must contain between 1 and 256 colours.", nameof(palette));
        }

        this.Palette = palette;
        this.Options = options;
        this.palette = new Rgba32[palette.Length];
        ReadOnlySpan<Color> colors = palette.Span;
        for (int i = 0; i < colors.Length; i++)
        {
            this.palette[i] = colors[i].ToRgba32();
        }
    }

    /// <summary>The palette pixels are mapped to.</summary>
    public ReadOnlyMemory<Color> Palette { get; }

    public QuantizerOptions Options { get; }

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => new PaletteFrameQuantizer<TPixel>(this.Options, this.palette);

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>(QuantizerOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PaletteFrameQuantizer<TPixel>(options, this.palette);
    }

    private sealed class PaletteFrameQuantizer<TPixel> : QuantizerBase<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly Rgba32[] palette;

        public PaletteFrameQuantizer(QuantizerOptions options, Rgba32[] palette)
            : base(options, fixedPalette: true)
            => this.palette = palette;

        protected override bool AccumulateColors(ImageFrame<TPixel> frame, Rectangle bounds) => false;

        protected override Rgba32[] BuildPaletteCore(int maxColors) => this.palette;
    }
}
