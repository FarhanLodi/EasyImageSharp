namespace EasyImageSharp.Drawing;

/// <summary>
/// Options shared by every drawing operation: whether shape edges are anti-aliased, how strongly the
/// drawn colour is blended over the existing pixels, which fill rule decides the inside of a shape, how
/// finely curves are approximated, and an optional clip path that limits where anything is drawn.
/// </summary>
public sealed class DrawingOptions
{
    /// <summary>The curve tolerance used unless told otherwise, in pixels.</summary>
    public const float DefaultCurveTolerance = 0.1f;

    private readonly float blendPercentage = 1f;
    private readonly float curveTolerance = DefaultCurveTolerance;
    private CoverageMask? clipMaskCache;

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

    /// <summary>
    /// The rule that decides which points are inside a filled path or polygon whose figures overlap or cross
    /// themselves; defaults to <see cref="FillRule.NonZero"/>. Strokes are unaffected: a stroke always covers
    /// every point its pen sweeps over.
    /// </summary>
    public FillRule FillRule { get; init; }

    /// <summary>
    /// The largest distance, in pixels, allowed between a Bezier curve or arc of a <see cref="VectorPath"/> and
    /// the line segments that approximate it when it is drawn; defaults to <see cref="DefaultCurveTolerance"/>.
    /// Smaller values give smoother curves at the cost of more segments. Values are clamped to 0.001-10, and
    /// NaN gives the default. The shape helpers (<c>FillEllipse</c>, <c>DrawCircle</c>, ...) keep their own
    /// fixed accuracy.
    /// </summary>
    public float CurveTolerance
    {
        get => this.curveTolerance;
        init => this.curveTolerance = float.IsNaN(value) ? DefaultCurveTolerance : Math.Clamp(value, 0.001f, 10f);
    }

    /// <summary>
    /// When set, drawing only changes pixels inside this path: coverage is multiplied by the path's own coverage
    /// mask (anti-aliased when <see cref="Antialias"/> is), so a clipped edge is as smooth as a drawn one. The
    /// path is filled with <see cref="ClipFillRule"/>. Defaults to <see langword="null"/> (no clipping).
    /// </summary>
    public VectorPath? ClipPath { get; init; }

    /// <summary>The fill rule that decides the inside of <see cref="ClipPath"/>; defaults to <see cref="FillRule.NonZero"/>.</summary>
    public FillRule ClipFillRule { get; init; }

    /// <summary>
    /// The coverage mask of <see cref="ClipPath"/> for a frame of the given size, or <see langword="null"/> without a
    /// clip path. The options are immutable, so the last mask is cached and reused by every operation drawn with
    /// them on frames of that size; masks are never modified after creation, so sharing one between threads is safe.
    /// </summary>
    internal CoverageMask? GetClipMask(int width, int height)
    {
        if (this.ClipPath is not VectorPath clip)
        {
            return null;
        }

        CoverageMask? cached = Volatile.Read(ref this.clipMaskCache);
        if (cached is not null && cached.FrameWidth == width && cached.FrameHeight == height)
        {
            return cached;
        }

        CoverageMask mask = CoverageMask.Create(clip, this.ClipFillRule, this.Antialias, this.CurveTolerance, width, height);
        Volatile.Write(ref this.clipMaskCache, mask);
        return mask;
    }
}
