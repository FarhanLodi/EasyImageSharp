using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;

namespace EasyImageSharp.Processing;

/// <summary>
/// Text drawn with TrueType/OpenType fonts by the managed font engine: anti-aliased glyph outlines, kerning,
/// wrapping and alignment, composited source-over onto every frame like the other drawing operations.
/// </summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Draws <paramref name="text"/> with <paramref name="font"/>, laid out by <paramref name="layout"/> (origin,
    /// alignment, wrapping, spacing, kerning, fallback faces). Glyphs are anti-aliased unless
    /// <see cref="DrawingOptions.Antialias"/> is off, in which case each pixel is either covered or not.
    /// Overlapping glyphs blend once, so translucent colours stay even. Text is laid out left to right one
    /// glyph per character without complex-script shaping; see <see cref="FontFace"/>.
    /// </summary>
    IImageProcessingContext DrawText(string text, Font font, Color color, TextLayoutOptions layout, DrawingOptions drawingOptions)
        => throw new NotSupportedException("This processing context does not support text drawing.");

    /// <summary>
    /// Draws <paramref name="text"/> with an outline: the glyphs grown outwards by <paramref name="outline"/>'s
    /// thickness in pixels (at most the font's em size) are first filled with its colour, then the glyphs
    /// themselves with <paramref name="color"/> on top. Otherwise identical to
    /// <see cref="DrawText(string, Font, Color, TextLayoutOptions, DrawingOptions)"/>.
    /// </summary>
    IImageProcessingContext DrawText(string text, Font font, Color color, Pen outline, TextLayoutOptions layout, DrawingOptions drawingOptions)
        => throw new NotSupportedException("This processing context does not support text drawing.");
}
