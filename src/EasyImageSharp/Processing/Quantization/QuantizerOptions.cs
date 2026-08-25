using EasyImageSharp.Processing.Dithering;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>Settings shared by every <see cref="IQuantizer"/>.</summary>
public sealed class QuantizerOptions
{
    /// <summary>The default alpha threshold: pixels with alpha below 64/255 become fully transparent.</summary>
    public const float DefaultTransparencyThreshold = 64f / 255f;

    private int maxColors = 256;
    private float ditherScale = 1f;
    private float transparencyThreshold = DefaultTransparencyThreshold;

    /// <summary>
    /// The maximum number of palette entries, from 2 to 256. When transparent pixels are present one entry is
    /// reserved for the fully transparent colour.
    /// </summary>
    public int MaxColors
    {
        get => this.maxColors;
        init
        {
            if (value is < 2 or > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxColors must be between 2 and 256.");
            }

            this.maxColors = value;
        }
    }

    /// <summary>The dither applied while mapping pixels to the palette; <see langword="null"/> disables dithering. Defaults to Floyd–Steinberg.</summary>
    public IDither? Dither { get; init; } = KnownDitherings.FloydSteinberg;

    /// <summary>Scales the dither strength from 0 (no dithering) to 1 (full strength). Defaults to 1.</summary>
    public float DitherScale
    {
        get => this.ditherScale;
        init
        {
            if (!(value >= 0f && value <= 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DitherScale must be between 0 and 1.");
            }

            this.ditherScale = value;
        }
    }

    /// <summary>How pixels are matched to palette entries. Defaults to <see cref="ColorMatchingMode.Exact"/>.</summary>
    public ColorMatchingMode ColorMatchingMode { get; init; } = ColorMatchingMode.Exact;

    /// <summary>
    /// Pixels whose alpha (0-1) is below this threshold are mapped to a single fully transparent palette entry;
    /// all other pixels are treated as opaque when the palette is built. Defaults to 64/255.
    /// </summary>
    public float TransparencyThreshold
    {
        get => this.transparencyThreshold;
        init
        {
            if (!(value >= 0f && value <= 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "TransparencyThreshold must be between 0 and 1.");
            }

            this.transparencyThreshold = value;
        }
    }

    /// <summary>The 0-255 alpha value below which a pixel counts as transparent.</summary>
    internal byte AlphaCutoff => (byte)Math.Clamp((int)MathF.Round(this.transparencyThreshold * 255f), 0, 255);

    /// <summary>Returns a copy of these options with a different <see cref="MaxColors"/>.</summary>
    internal QuantizerOptions WithMaxColors(int maxColors) => new()
    {
        MaxColors = maxColors,
        Dither = this.Dither,
        DitherScale = this.DitherScale,
        ColorMatchingMode = this.ColorMatchingMode,
        TransparencyThreshold = this.TransparencyThreshold,
    };
}
