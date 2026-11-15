using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for watermarking.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Places <paramref name="watermark"/> bottom-right with a 16 px margin at 50% opacity (<see cref="WatermarkOptions.Default"/>).</summary>
    public static IImageProcessingContext Watermark(this IImageProcessingContext context, Image watermark)
        => context.Watermark(watermark, WatermarkOptions.Default);

    /// <summary>Places <paramref name="watermark"/> at <paramref name="position"/> with a 16 px margin and the given opacity.</summary>
    public static IImageProcessingContext Watermark(this IImageProcessingContext context, Image watermark, AnchorPositionMode position, float opacity)
        => context.Watermark(watermark, new WatermarkOptions { Position = position, Opacity = opacity });

    /// <summary>Places white bitmap-font text at twice the glyph size bottom-right with the default options.</summary>
    public static IImageProcessingContext Watermark(this IImageProcessingContext context, string text)
        => context.Watermark(text, Color.White, new TextOptions { Scale = 2 }, WatermarkOptions.Default);

    /// <summary>Places bitmap-font text of the given colour at twice the glyph size with the given options.</summary>
    public static IImageProcessingContext Watermark(this IImageProcessingContext context, string text, Color color, WatermarkOptions options)
        => context.Watermark(text, color, new TextOptions { Scale = 2 }, options);

    /// <summary>Places TrueType/OpenType text of the given colour with the default options (<see cref="WatermarkOptions.Default"/>).</summary>
    public static IImageProcessingContext Watermark(this IImageProcessingContext context, string text, Font font, Color color)
        => context.Watermark(text, font, color, WatermarkOptions.Default);
}
