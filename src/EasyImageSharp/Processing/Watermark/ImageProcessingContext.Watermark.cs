using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Watermarking; see <c>IImageProcessingContext.Watermark.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext Watermark(Image watermark, WatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        ArgumentNullException.ThrowIfNull(options);
        return this.ApplyWatermark(WatermarkOps.ReadStamp(watermark), options);
    }

    public IImageProcessingContext Watermark(string text, Color color, TextOptions textOptions, WatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(textOptions);
        ArgumentNullException.ThrowIfNull(options);
        ImageFrame<Rgba32>? stamp = WatermarkOps.RenderText(text, color, textOptions);
        return stamp is null ? this : this.ApplyWatermark(stamp, options);
    }

    public IImageProcessingContext Watermark(string text, Font font, Color color, WatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(options);
        ImageFrame<Rgba32>? stamp = WatermarkOps.RenderFontText(text, font, color);
        return stamp is null ? this : this.ApplyWatermark(stamp, options);
    }

    private IImageProcessingContext ApplyWatermark(ImageFrame<Rgba32> source, WatermarkOptions options)
    {
        if (options.Opacity == 0f)
        {
            return this;
        }

        // Frames of one image usually share a size, so the scaled and rotated stamp is prepared once per size.
        var prepared = new Dictionary<Size, ImageFrame<Rgba32>>();
        return this.PerFrame(frame =>
        {
            var size = new Size(frame.Width, frame.Height);
            if (!prepared.TryGetValue(size, out ImageFrame<Rgba32>? stamp))
            {
                stamp = WatermarkOps.Prepare(source, frame.Width, frame.Height, options);
                prepared[size] = stamp;
            }

            WatermarkOps.Apply(frame, stamp, options);
            return frame;
        });
    }
}
