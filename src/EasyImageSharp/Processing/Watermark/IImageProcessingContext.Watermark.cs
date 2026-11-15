using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;

namespace EasyImageSharp.Processing;

/// <summary>Watermarking with an image, bitmap-font text or TrueType/OpenType text.</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Composites <paramref name="watermark"/> (its root frame) onto every frame: scaled relative to the frame, rotated,
    /// anchored with a margin, optionally tiled, blended with <see cref="WatermarkOptions.BlendMode"/> and source-over
    /// at <see cref="WatermarkOptions.Opacity"/>. Parts outside the frame are clipped.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="watermark"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement watermarking.</exception>
    IImageProcessingContext Watermark(Image watermark, WatermarkOptions options)
        => throw new NotSupportedException("This processing context does not support watermarks.");

    /// <summary>
    /// Renders <paramref name="text"/> in <paramref name="color"/> with the bitmap font, scale, background and alignment
    /// of <paramref name="textOptions"/>, and places the rendered block like an image watermark. An empty text draws
    /// nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement watermarking.</exception>
    IImageProcessingContext Watermark(string text, Color color, TextOptions textOptions, WatermarkOptions options)
        => throw new NotSupportedException("This processing context does not support watermarks.");

    /// <summary>
    /// Renders <paramref name="text"/> in <paramref name="color"/> with the TrueType/OpenType <paramref name="font"/>
    /// (anti-aliased, left aligned, kerning on, hard line breaks honoured), trims it to its ink, and places it like an
    /// image watermark. A text that draws nothing, such as an empty one or only spaces, leaves the image unchanged.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ImageSizeLimitExceededException">The rendered text is too large for a single stamp.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement watermarking.</exception>
    IImageProcessingContext Watermark(string text, Font font, Color color, WatermarkOptions options)
        => throw new NotSupportedException("This processing context does not support watermarks.");
}
