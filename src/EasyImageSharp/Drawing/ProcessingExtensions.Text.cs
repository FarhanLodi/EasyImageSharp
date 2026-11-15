using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for drawing font text (default layout and drawing options).</summary>
public static partial class ProcessingExtensions
{
    /// <summary>
    /// Draws <paramref name="text"/> with <paramref name="font"/>; <paramref name="origin"/> is the top-left of
    /// the first line (the top of its ascent). Kerning is applied and lines break only at line breaks in the text.
    /// </summary>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Font font, Color color, PointF origin)
        => context.DrawText(text, font, color, new TextLayoutOptions { Origin = origin }, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawText(string, Font, Color, TextLayoutOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Font font, Color color, TextLayoutOptions layout)
        => context.DrawText(text, font, color, layout, DrawingOptions.Default);

    /// <summary>
    /// Draws outlined <paramref name="text"/> with <paramref name="font"/>; <paramref name="origin"/> is the top-left
    /// of the first line. See <see cref="IImageProcessingContext.DrawText(string, Font, Color, Pen, TextLayoutOptions, DrawingOptions)"/>.
    /// </summary>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Font font, Color color, Pen outline, PointF origin)
        => context.DrawText(text, font, color, outline, new TextLayoutOptions { Origin = origin }, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawText(string, Font, Color, Pen, TextLayoutOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Font font, Color color, Pen outline, TextLayoutOptions layout)
        => context.DrawText(text, font, color, outline, layout, DrawingOptions.Default);
}
