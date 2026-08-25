namespace EasyImageSharp.Processing;

/// <summary>Pixel neighbourhood used when labelling connected components.</summary>
public enum Connectivity
{
    /// <summary>Pixels connect through their four edge neighbours.</summary>
    Four = 4,

    /// <summary>Pixels connect through their eight edge and corner neighbours.</summary>
    Eight = 8,
}

/// <summary>Which rules <see cref="IImageProcessingContext.RemoveLines(int, LineOrientation)"/> targets.</summary>
public enum LineOrientation
{
    /// <summary>Long horizontal rules only.</summary>
    Horizontal,

    /// <summary>Long vertical rules only.</summary>
    Vertical,

    /// <summary>Both horizontal and vertical rules.</summary>
    Both,
}

/// <summary>Skew estimation algorithm used by <see cref="DeskewOptions"/>.</summary>
public enum DeskewMethod
{
    /// <summary>
    /// Projection profiling of dark pixels (the original <c>Deskew(float)</c> algorithm): fast and robust for
    /// dense text, searches at 0.1 degree resolution.
    /// </summary>
    Projection,

    /// <summary>
    /// Hough voting of text-edge pixels on a background-normalised downscale with sub-degree (0.05 degree,
    /// parabolic-refined) resolution; better for sparse or unevenly lit pages.
    /// </summary>
    Hough,
}

/// <summary>Binarisation algorithm selected by <see cref="BinarizeOptions"/>.</summary>
public enum BinarizeMethod
{
    /// <summary>Otsu for high-contrast, evenly lit pages; Sauvola otherwise (see <see cref="BinarizeOptions"/>).</summary>
    Auto,

    /// <summary>Global Otsu threshold.</summary>
    Otsu,

    /// <summary>Sauvola local threshold.</summary>
    Sauvola,

    /// <summary>Niblack local threshold.</summary>
    Niblack,

    /// <summary>Wolf-Jolion local threshold.</summary>
    WolfJolion,

    /// <summary>Phansalkar local threshold.</summary>
    Phansalkar,

    /// <summary>NICK local threshold.</summary>
    Nick,
}

/// <summary>Options for <see cref="IImageProcessingContext.Deskew(DeskewOptions)"/>.</summary>
public sealed class DeskewOptions
{
    /// <summary>The estimator to use. Defaults to <see cref="DeskewMethod.Hough"/>.</summary>
    public DeskewMethod Method { get; set; } = DeskewMethod.Hough;

    /// <summary>The largest absolute skew searched, in degrees (0.5-45). Defaults to 15.</summary>
    public float MaxAngle { get; set; } = 15f;

    /// <summary>The colour revealed in the corners after rotation. Defaults to white.</summary>
    public Color FillColor { get; set; } = Color.White;

    /// <summary>
    /// Skews smaller than this (in degrees) are ignored so already-straight pages are left untouched.
    /// Defaults to 0.1.
    /// </summary>
    public float MinAngle { get; set; } = 0.1f;
}

/// <summary>Options for <see cref="IImageProcessingContext.Binarize(BinarizeOptions)"/>.</summary>
/// <remarks>
/// With <see cref="BinarizeMethod.Auto"/> the page is analysed once: the Otsu separability of the luminance
/// histogram (between-class variance divided by total variance) is computed, and the background brightness
/// (mean of pixels above the Otsu threshold) is sampled in a coarse 8x8 grid of blocks. When the separability
/// is at least <see cref="MinSeparability"/> and the darkest and brightest block backgrounds differ by no more
/// than <see cref="IlluminationTolerance"/> grey levels the page is treated as high-contrast and evenly lit and
/// the global Otsu threshold is used; otherwise Sauvola with <see cref="WindowSize"/> and <see cref="K"/> is used.
/// </remarks>
public sealed class BinarizeOptions
{
    private const float DefaultK = 0.2f;

    private float? k;

    /// <summary>The algorithm to use. Defaults to <see cref="BinarizeMethod.Auto"/>.</summary>
    public BinarizeMethod Method { get; set; } = BinarizeMethod.Auto;

    /// <summary>Local window size (odd, in pixels) for the local methods. Defaults to 25.</summary>
    public int WindowSize { get; set; } = 25;

    /// <summary>
    /// The <c>k</c> parameter of the local methods. When it is never assigned, each method uses its own usual
    /// value (Sauvola 0.2, Niblack -0.2, Wolf-Jolion 0.5, Phansalkar 0.25, NICK -0.1) exactly like the
    /// convenience overloads on <see cref="ProcessingExtensions"/>; those values have different signs and
    /// magnitudes, so one shared default cannot suit them all. Assigning a value applies it to whichever method
    /// runs. Reading it before it is assigned reports Sauvola's 0.2.
    /// </summary>
    public float K
    {
        get => this.k ?? DefaultK;
        set => this.k = value;
    }

    /// <summary>Largest spread of block background brightness (grey levels) still considered evenly lit. Defaults to 40.</summary>
    public int IlluminationTolerance { get; set; } = 40;

    /// <summary>Smallest Otsu separability (0-1) still considered high contrast. Defaults to 0.7.</summary>
    public float MinSeparability { get; set; } = 0.7f;

    /// <summary>The <c>k</c> to use for <paramref name="method"/>: the assigned value, else that method's usual one.</summary>
    internal float KFor(BinarizeMethod method) => this.k ?? method switch
    {
        BinarizeMethod.Niblack => -0.2f,
        BinarizeMethod.WolfJolion => 0.5f,
        BinarizeMethod.Phansalkar => 0.25f,
        BinarizeMethod.Nick => -0.1f,
        _ => DefaultK,
    };
}

/// <summary>Result of <see cref="IImageProcessingContext.DetectOrientation"/>.</summary>
public readonly struct OrientationEstimate
{
    /// <summary>Initializes a new estimate.</summary>
    public OrientationEstimate(RotateMode rotation, float confidence)
    {
        this.Rotation = rotation;
        this.Confidence = confidence;
    }

    /// <summary>The clockwise rotation that makes the page upright (apply it with <c>Rotate</c>).</summary>
    public RotateMode Rotation { get; }

    /// <summary>
    /// A heuristic confidence in [0, 1]. Values above roughly 0.5 mean the text-line cues (horizontal vs vertical
    /// line structure, ascender/descender asymmetry and left alignment) agreed clearly; low values mean the page
    /// carries too little text or the cues conflicted, in which case <see cref="Rotation"/> is a weak guess.
    /// </summary>
    public float Confidence { get; }

    /// <inheritdoc/>
    public override string ToString()
        => FormattableString.Invariant($"OrientationEstimate [ Rotation={this.Rotation}, Confidence={this.Confidence:0.00} ]");
}

/// <summary>Per-component statistics produced by <see cref="ConnectedComponents"/>.</summary>
public readonly struct ComponentStats
{
    /// <summary>Initializes a new statistics record.</summary>
    public ComponentStats(int label, int area, Rectangle bounds, PointF centroid)
    {
        this.Label = label;
        this.Area = area;
        this.Bounds = bounds;
        this.Centroid = centroid;
    }

    /// <summary>The label value (1-based) stored in the label map for this component.</summary>
    public int Label { get; }

    /// <summary>The number of pixels in the component.</summary>
    public int Area { get; }

    /// <summary>The tight bounding box of the component.</summary>
    public Rectangle Bounds { get; }

    /// <summary>The mean pixel position of the component.</summary>
    public PointF Centroid { get; }

    /// <inheritdoc/>
    public override string ToString() => $"ComponentStats [ Label={this.Label}, Area={this.Area}, Bounds={this.Bounds} ]";
}

/// <summary>A text line found by <see cref="IImageProcessingContext.SegmentText"/>: its bounds and the words inside it.</summary>
public sealed class TextLine
{
    internal TextLine(Rectangle bounds, IReadOnlyList<Rectangle> words)
    {
        this.Bounds = bounds;
        this.Words = words;
    }

    /// <summary>The bounding box of the line's ink.</summary>
    public Rectangle Bounds { get; }

    /// <summary>Word bounding boxes, ordered left to right.</summary>
    public IReadOnlyList<Rectangle> Words { get; }
}

/// <summary>Text lines and words found by projection-profile segmentation.</summary>
public sealed class TextRegions
{
    internal TextRegions(IReadOnlyList<TextLine> lines)
    {
        this.Lines = lines;
        var words = new List<Rectangle>();
        foreach (TextLine line in lines)
        {
            words.AddRange(line.Words);
        }

        this.Words = words;
    }

    /// <summary>The text lines, ordered top to bottom.</summary>
    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>All words in reading order (line by line, left to right).</summary>
    public IReadOnlyList<Rectangle> Words { get; }
}

/// <summary>Options for <see cref="IImageProcessingContext.PrepareForOcr(OcrPreprocessOptions)"/>.</summary>
public sealed class OcrPreprocessOptions
{
    /// <summary>Divide out the estimated background (uneven illumination, shadows). Defaults to true.</summary>
    public bool NormalizeBackground { get; set; } = true;

    /// <summary>
    /// Radius of the background estimate for <see cref="NormalizeBackground"/>; 0 picks one from the image size
    /// (about 1/25 of the smaller dimension, at least 8). Defaults to 0.
    /// </summary>
    public int BackgroundRadius { get; set; }

    /// <summary>
    /// Apply a 3x3 median filter before deskewing. Removes impulse noise on grey scans but rounds the corners of
    /// thin strokes at low resolution, so it is off by default; component-based despeckling
    /// (<see cref="DespeckleMaxArea"/>) is the default denoiser.
    /// </summary>
    public bool MedianDenoise { get; set; }

    /// <summary>Estimate and correct the skew. Defaults to true.</summary>
    public bool Deskew { get; set; } = true;

    /// <summary>The largest skew searched, in degrees. Defaults to 15.</summary>
    public float DeskewMaxAngle { get; set; } = 15f;

    /// <summary>Binarise the result after deskewing. Defaults to true.</summary>
    public bool Binarize { get; set; } = true;

    /// <summary>Binarisation settings used when <see cref="Binarize"/> is set; <see langword="null"/> uses the defaults.</summary>
    public BinarizeOptions? BinarizeOptions { get; set; }

    /// <summary>Remove isolated specks (both polarities) up to this many pixels after binarising; 0 disables. Defaults to 3.</summary>
    public int DespeckleMaxArea { get; set; } = 3;
}
