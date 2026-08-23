namespace EasyImageSharp.Processing;

/// <summary>How an image is fitted into the target size during a resize.</summary>
public enum ResizeMode
{
    /// <summary>Stretches to the exact target size, ignoring aspect ratio. This is the default.</summary>
    Stretch,

    /// <summary>Scales to fit inside the target box, preserving aspect ratio; the result may be smaller than the box.</summary>
    Max,

    /// <summary>Scales to cover the target box, preserving aspect ratio; the result may be larger than the box.</summary>
    Min,

    /// <summary>
    /// Scales to fit inside the target box, preserving aspect ratio, and pads the remainder with
    /// <see cref="ResizeOptions.PadColor"/>; the content is placed according to <see cref="ResizeOptions.Position"/>.
    /// </summary>
    Pad,

    /// <summary>
    /// Scales to cover the target box, preserving aspect ratio, then crops to the exact target size. The retained
    /// region is chosen by <see cref="ResizeOptions.CenterCoordinates"/> when set, otherwise by
    /// <see cref="ResizeOptions.Position"/>.
    /// </summary>
    Crop,

    /// <summary>
    /// Pads the image to the target size without scaling when it already fits inside the box; otherwise behaves
    /// like <see cref="Pad"/>. Never enlarges the source.
    /// </summary>
    BoxPad,

    /// <summary>
    /// Scales the image to <see cref="ResizeOptions.TargetRectangle"/>'s size and places it at that rectangle's
    /// location on a canvas of <see cref="ResizeOptions.Size"/> filled with <see cref="ResizeOptions.PadColor"/>;
    /// parts outside the canvas are clipped.
    /// </summary>
    Manual,
}

/// <summary>Where the content is anchored inside the canvas for the padding and cropping resize modes.</summary>
public enum AnchorPositionMode
{
    /// <summary>Centered horizontally and vertically.</summary>
    Center,

    /// <summary>Centered horizontally, aligned to the top edge.</summary>
    Top,

    /// <summary>Centered horizontally, aligned to the bottom edge.</summary>
    Bottom,

    /// <summary>Aligned to the left edge, centered vertically.</summary>
    Left,

    /// <summary>Aligned to the right edge, centered vertically.</summary>
    Right,

    /// <summary>Aligned to the top-left corner.</summary>
    TopLeft,

    /// <summary>Aligned to the top-right corner.</summary>
    TopRight,

    /// <summary>Aligned to the bottom-right corner.</summary>
    BottomRight,

    /// <summary>Aligned to the bottom-left corner.</summary>
    BottomLeft,
}

/// <summary>Lossless rotation amounts (clockwise).</summary>
public enum RotateMode
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270,
}

/// <summary>Mirroring operations.</summary>
public enum FlipMode
{
    None,
    Horizontal,
    Vertical,
}
