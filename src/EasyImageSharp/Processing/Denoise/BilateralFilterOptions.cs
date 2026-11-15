namespace EasyImageSharp.Processing;

/// <summary>How <see cref="IImageProcessingContext.BilateralFilter(BilateralFilterOptions)"/> evaluates the filter.</summary>
public enum BilateralFilterMethod
{
    /// <summary>
    /// <see cref="Exact"/> when the window radius is at most <see cref="BilateralFilterOptions.MaxExactRadius"/>,
    /// <see cref="Grid"/> otherwise.
    /// </summary>
    Auto,

    /// <summary>
    /// The brute-force filter: every pixel within the circular window is weighted by a spatial Gaussian times a
    /// range Gaussian of the Euclidean RGB distance. Cost grows with the square of the radius.
    /// </summary>
    Exact,

    /// <summary>
    /// The bilateral grid approximation (Paris and Durand 2006; Chen, Paris and Durand 2007): each colour channel is
    /// splatted into a coarse (x, y, value) grid with cells of <see cref="BilateralFilterOptions.SigmaSpatial"/>
    /// pixels by <see cref="BilateralFilterOptions.SigmaRange"/>, blurred with a [1 4 6 4 1] / 16 kernel along each
    /// axis and sliced back with trilinear interpolation. Cost is independent of the radius; the range term
    /// compares each channel separately rather than the colour distance, and the spatial falloff is Gaussian
    /// without a hard window, so results differ from <see cref="Exact"/> by a few 8-bit levels.
    /// </summary>
    Grid,
}

/// <summary>Parameters of <see cref="IImageProcessingContext.BilateralFilter(BilateralFilterOptions)"/>.</summary>
public sealed class BilateralFilterOptions
{
    /// <summary>The largest radius <see cref="BilateralFilterMethod.Auto"/> still evaluates exactly.</summary>
    public const int MaxExactRadius = 8;

    /// <summary>The largest radius the exact filter accepts.</summary>
    public const int MaxRadius = 64;

    private readonly float sigmaSpatial = 3f;
    private readonly float sigmaRange = 0.1f;
    private readonly int radius;

    /// <summary>Standard deviation of the spatial Gaussian in pixels, 0.1-256. Defaults to 3.</summary>
    public float SigmaSpatial
    {
        get => this.sigmaSpatial;
        init => this.sigmaSpatial = value is >= 0.1f and <= 256f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Spatial sigma must be between 0.1 and 256 pixels.");
    }

    /// <summary>
    /// Standard deviation of the range Gaussian in normalised channel units (0-1 spans the channel range),
    /// 0.001-10. Smaller values preserve weaker edges. Defaults to 0.1 (about 25 8-bit levels).
    /// </summary>
    public float SigmaRange
    {
        get => this.sigmaRange;
        init => this.sigmaRange = value is >= 0.001f and <= 10f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Range sigma must be between 0.001 and 10.");
    }

    /// <summary>
    /// Window radius of the exact filter in pixels, 0-64; 0 (the default) uses <c>ceil(2 SigmaSpatial)</c>. The
    /// grid method has no window and ignores it except to choose the method under <see cref="BilateralFilterMethod.Auto"/>.
    /// </summary>
    public int Radius
    {
        get => this.radius;
        init => this.radius = value is >= 0 and <= MaxRadius
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Radius must be between 0 and 64.");
    }

    /// <summary>Exact, grid or automatic evaluation. Defaults to <see cref="BilateralFilterMethod.Auto"/>.</summary>
    public BilateralFilterMethod Method { get; init; } = BilateralFilterMethod.Auto;

    /// <summary>The radius the exact filter uses: <see cref="Radius"/>, or <c>ceil(2 SigmaSpatial)</c> capped at 64 when it is 0.</summary>
    public int EffectiveRadius => this.radius > 0 ? this.radius : Math.Clamp((int)MathF.Ceiling(2 * this.sigmaSpatial), 1, MaxRadius);
}
