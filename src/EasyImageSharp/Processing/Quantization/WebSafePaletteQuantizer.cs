using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>Maps pixels to the 216-colour "web safe" palette (every channel one of 0x00, 0x33, 0x66, 0x99, 0xCC, 0xFF).</summary>
public sealed class WebSafePaletteQuantizer : IQuantizer
{
    private static readonly Color[] WebSafeColors = BuildPalette();

    private readonly PaletteQuantizer inner;

    /// <summary>Creates a web-safe quantizer with default options.</summary>
    public WebSafePaletteQuantizer()
        : this(new QuantizerOptions())
    {
    }

    /// <summary>Creates a web-safe quantizer with the given options.</summary>
    public WebSafePaletteQuantizer(QuantizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.inner = new PaletteQuantizer(WebSafeColors, options);
    }

    /// <summary>The 216 web-safe colours, ordered red-major.</summary>
    public static ReadOnlyMemory<Color> Palette => WebSafeColors;

    public QuantizerOptions Options => this.inner.Options;

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => this.inner.CreatePixelSpecificQuantizer<TPixel>();

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>(QuantizerOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        => this.inner.CreatePixelSpecificQuantizer<TPixel>(options);

    private static Color[] BuildPalette()
    {
        var colors = new Color[216];
        int i = 0;
        for (int r = 0; r < 6; r++)
        {
            for (int g = 0; g < 6; g++)
            {
                for (int b = 0; b < 6; b++)
                {
                    colors[i++] = new Color((byte)(r * 0x33), (byte)(g * 0x33), (byte)(b * 0x33));
                }
            }
        }

        return colors;
    }
}
