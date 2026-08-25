using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Configuration for resize operations.</summary>
public sealed class ResizeOptions
{
    /// <summary>
    /// The target size. A zero width or height is computed from the aspect ratio. For <see cref="ResizeMode.Manual"/>
    /// this is the canvas size and <see cref="TargetRectangle"/> is where the scaled image lands on it.
    /// </summary>
    public Size Size { get; set; }

    /// <summary>
    /// How the image is fitted into <see cref="Size"/>. Defaults to <see cref="ResizeMode.Stretch"/> (exact target
    /// size, aspect ratio ignored), which existing consumers rely on.
    /// </summary>
    public ResizeMode Mode { get; set; } = ResizeMode.Stretch;

    /// <summary>The resampling kernel. Defaults to <see cref="KnownResamplers.Bicubic"/>.</summary>
    public IResampler Sampler { get; set; } = KnownResamplers.Bicubic;

    /// <summary>
    /// The color used for the letterbox area in the padding modes (<see cref="ResizeMode.Pad"/>,
    /// <see cref="ResizeMode.BoxPad"/>, <see cref="ResizeMode.Manual"/>).
    /// Accepts an <see cref="Rgba32"/> directly through the implicit conversion.
    /// </summary>
    public Color PadColor { get; set; } = Color.Transparent;

    /// <summary>
    /// Where the content is anchored inside the canvas for <see cref="ResizeMode.Pad"/>, <see cref="ResizeMode.BoxPad"/>
    /// and <see cref="ResizeMode.Crop"/>. Defaults to <see cref="AnchorPositionMode.Center"/>.
    /// </summary>
    public AnchorPositionMode Position { get; set; } = AnchorPositionMode.Center;

    /// <summary>
    /// For <see cref="ResizeMode.Crop"/>: the point of the source image, in normalised coordinates
    /// (0..1, where (0.5, 0.5) is the center), that should end up at the center of the cropped result. The crop
    /// window is clamped so it always stays inside the scaled image. When <see langword="null"/>
    /// <see cref="Position"/> is used instead. Ignored by the other modes.
    /// </summary>
    public PointF? CenterCoordinates { get; set; }

    /// <summary>
    /// For <see cref="ResizeMode.Manual"/>: the rectangle, in canvas coordinates, that the source image is scaled
    /// to and placed at. Must have a positive width and height. Ignored by the other modes.
    /// </summary>
    public Rectangle TargetRectangle { get; set; }

    /// <summary>
    /// When <see langword="true"/> the pixels are converted from sRGB to linear light before resampling and back
    /// afterwards, which averages colours the way physical light mixes (no darkening of high-contrast detail).
    /// Slower; defaults to <see langword="false"/>.
    /// </summary>
    public bool Compand { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default) colour channels are weighted by alpha while resampling so fully
    /// transparent pixels do not bleed their (usually black) colour into their opaque neighbours. Only affects pixel
    /// formats with an alpha channel; opaque formats always take the plain path.
    /// </summary>
    public bool PremultiplyAlpha { get; set; } = true;
}
