namespace EasyImageSharp.Processing;

/// <summary>Placement and appearance of a watermark (<see cref="IImageProcessingContext.Watermark(Image, WatermarkOptions)"/>).</summary>
public sealed class WatermarkOptions
{
    private readonly int margin = 16;
    private readonly float scale;
    private readonly float opacity = 0.5f;
    private readonly float rotation;
    private readonly Size tileSpacing = new(48, 48);
    private readonly IResampler sampler = KnownResamplers.Bicubic;
    private readonly PixelColorBlendingMode blendMode = PixelColorBlendingMode.Normal;

    /// <summary>The default options: bottom-right, 16 px margin, natural size, 50% opacity, normal blending, no tiling.</summary>
    public static WatermarkOptions Default { get; } = new();

    /// <summary>
    /// Where the watermark sits in the frame. With <see cref="Tile"/> this is where one tile of the grid is anchored.
    /// Defaults to <see cref="AnchorPositionMode.BottomRight"/>.
    /// </summary>
    public AnchorPositionMode Position { get; init; } = AnchorPositionMode.BottomRight;

    /// <summary>Distance in pixels between the watermark and the frame edges it is anchored to, 0-100000. Defaults to 16.</summary>
    public int Margin
    {
        get => this.margin;
        init => this.margin = value is >= 0 and <= 100000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Margin must be between 0 and 100000 pixels.");
    }

    /// <summary>
    /// Size relative to the frame, 0-1: the watermark is scaled (aspect ratio kept) to fit inside
    /// <c>Scale * frame width</c> by <c>Scale * frame height</c>, so it covers the same share of every frame size.
    /// 0 (the default) keeps its natural pixel size.
    /// </summary>
    public float Scale
    {
        get => this.scale;
        init => this.scale = value is >= 0f and <= 1f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Scale must be between 0 and 1.");
    }

    /// <summary>Multiplies the watermark's alpha, 0-1. Defaults to 0.5.</summary>
    public float Opacity
    {
        get => this.opacity;
        init => this.opacity = value is >= 0f and <= 1f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity must be between 0 and 1.");
    }

    /// <summary>How the watermark's colour combines with the image before source-over compositing. Defaults to <see cref="PixelColorBlendingMode.Normal"/>.</summary>
    public PixelColorBlendingMode BlendMode
    {
        get => this.blendMode;
        init => this.blendMode = value is >= PixelColorBlendingMode.Normal and <= PixelColorBlendingMode.Luminosity
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown colour blending mode.");
    }

    /// <summary>Clockwise rotation of the (scaled) watermark in degrees, -360 to 360; the rotated bounds are used for placement.</summary>
    public float Rotation
    {
        get => this.rotation;
        init => this.rotation = value is >= -360f and <= 360f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Rotation must be between -360 and 360 degrees.");
    }

    /// <summary>Repeats the watermark over the whole frame on a grid through the anchored position. Defaults to <see langword="false"/>.</summary>
    public bool Tile { get; init; }

    /// <summary>Gap in pixels between neighbouring tiles horizontally and vertically, each 0-100000. Defaults to 48 x 48.</summary>
    public Size TileSpacing
    {
        get => this.tileSpacing;
        init => this.tileSpacing = value.Width is >= 0 and <= 100000 && value.Height is >= 0 and <= 100000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Tile spacing must be between 0 and 100000 pixels on each axis.");
    }

    /// <summary>The resampler used when <see cref="Scale"/> resizes the watermark. Defaults to bicubic.</summary>
    public IResampler Sampler
    {
        get => this.sampler;
        init => this.sampler = value ?? throw new ArgumentNullException(nameof(value));
    }
}
