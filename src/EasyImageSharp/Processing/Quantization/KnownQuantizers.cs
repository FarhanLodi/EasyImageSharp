namespace EasyImageSharp.Processing.Quantization;

/// <summary>Ready-made quantizers with default options (256 colours, Floyd–Steinberg dithering).</summary>
public static class KnownQuantizers
{
    /// <summary>Xiaolin Wu's moment-histogram quantizer; the default for <c>Quantize()</c> and the palette encoders.</summary>
    public static IQuantizer Wu { get; } = new WuQuantizer();

    /// <summary>The octree quantizer.</summary>
    public static IQuantizer Octree { get; } = new OctreeQuantizer();

    /// <summary>The fixed 216-colour web-safe palette.</summary>
    public static IQuantizer WebSafe { get; } = new WebSafePaletteQuantizer();
}
