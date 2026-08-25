namespace EasyImageSharp.Drawing;

/// <summary>
/// Options shared by every drawing operation: whether shape edges are anti-aliased and how strongly the
/// drawn colour is blended over the existing pixels.
/// </summary>
public sealed class DrawingOptions
{
    private readonly float blendPercentage = 1f;

    /// <summary>The default options: anti-aliased, fully opaque.</summary>
    public static DrawingOptions Default { get; } = new();

    /// <summary>
    /// Whether shape edges receive fractional coverage (anti-aliasing). When <see langword="false"/> each
    /// pixel is either fully covered or untouched, decided by whether its centre lies inside the shape.
    /// Bitmap text is never anti-aliased. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Antialias { get; init; } = true;

    /// <summary>
    /// A global opacity multiplier in the range 0-1 applied on top of the colour's own alpha; 1 (the
    /// default) draws the colour as given, 0.5 draws it half-transparent. Values outside 0-1 are clamped.
    /// </summary>
    public float BlendPercentage
    {
        get => this.blendPercentage;
        init => this.blendPercentage = float.IsNaN(value) ? 1f : Math.Clamp(value, 0f, 1f);
    }
}
