using EasyImageSharp.Processing.Dithering;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for quantization and dithering.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Reduces the image to at most 256 colours with the Wu quantizer and Floyd–Steinberg dithering.</summary>
    public static IImageProcessingContext Quantize(this IImageProcessingContext context)
        => context.Quantize(KnownQuantizers.Wu);

    /// <summary>Dithers the image to the 216-colour web-safe palette at full strength.</summary>
    public static IImageProcessingContext Dither(this IImageProcessingContext context, IDither dither)
        => context.Dither(dither, 1f, WebSafePaletteQuantizer.Palette);

    /// <summary>Dithers the image to the 216-colour web-safe palette with the given strength (0-1).</summary>
    public static IImageProcessingContext Dither(this IImageProcessingContext context, IDither dither, float ditherScale)
        => context.Dither(dither, ditherScale, WebSafePaletteQuantizer.Palette);

    /// <summary>Dithers the image to <paramref name="palette"/> at full strength.</summary>
    public static IImageProcessingContext Dither(this IImageProcessingContext context, IDither dither, ReadOnlyMemory<Color> palette)
        => context.Dither(dither, 1f, palette);

    /// <summary>Dithers the image to black and white by luminance.</summary>
    public static IImageProcessingContext BinaryDither(this IImageProcessingContext context, IDither dither)
        => context.BinaryDither(dither, Color.White, Color.Black);
}
