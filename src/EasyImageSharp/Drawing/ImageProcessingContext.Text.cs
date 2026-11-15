using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Layout;
using EasyImageSharp.Fonts.Rendering;

namespace EasyImageSharp.Processing;

/// <summary>Font text drawing; see <c>IImageProcessingContext.Text.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext DrawText(string text, Font font, Color color, TextLayoutOptions layout, DrawingOptions drawingOptions)
        => this.DrawFontText(text, font, color, null, layout, drawingOptions);

    public IImageProcessingContext DrawText(string text, Font font, Color color, Pen outline, TextLayoutOptions layout, DrawingOptions drawingOptions)
        => this.DrawFontText(text, font, color, outline, layout, drawingOptions);

    private IImageProcessingContext DrawFontText(string text, Font font, Color color, Pen? outline, TextLayoutOptions layout, DrawingOptions drawingOptions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(drawingOptions);
        if (!float.IsFinite(layout.Origin.X) || !float.IsFinite(layout.Origin.Y))
        {
            throw new ArgumentException("The text origin must have finite coordinates.", nameof(layout));
        }

        if (text.Length == 0)
        {
            return this;
        }

        // The layout does not depend on the frame, so it is computed once for all frames.
        LaidOutText laidOut = TextLayout.Layout(text, font, layout);
        return this.PerFrame(frame =>
        {
            TextRenderer.Draw(frame, laidOut, color, outline, drawingOptions);
            return frame;
        });
    }
}
