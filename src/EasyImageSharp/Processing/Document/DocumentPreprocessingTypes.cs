using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Conservative starting points; quality must be evaluated against the target OCR model and corpus.</summary>
public enum DocumentPreprocessingProfile
{
    /// <summary>Preserve color, alpha, precision and geometry; corrections must be explicitly enabled for the target model.</summary>
    NeuralOcr,
    /// <summary>Enable heuristic orientation, deskew and adaptive binarization.</summary>
    ScannedDocument,
    /// <summary>Enable heuristic orientation, page perspective correction and conditional background normalization.</summary>
    PhoneDocument,
    /// <summary>Preserve small text and color; smoothing and binarization are disabled by default.</summary>
    Screenshot,
}

/// <summary>Opt-in preprocessing. Null switches select the profile defaults; explicit switches override them.</summary>
public sealed class DocumentPreprocessingOptions
{
    public DocumentPreprocessingProfile Profile { get; set; } = DocumentPreprocessingProfile.NeuralOcr;
    /// <summary>Request-local parallelism, cancellation and cumulative pixel allocation budget; null inherits ambient defaults.</summary>
    public ImageExecutionOptions? ExecutionOptions { get; set; }
    /// <summary>Enable quarter-turn orientation detection, with abstention below the confidence floor.</summary>
    public bool? AutoOrient { get; set; }
    /// <summary>Enable page-outline detection and perspective rectification; no correction when no convincing outline exists.</summary>
    public bool? CorrectPerspective { get; set; }
    /// <summary>Enable Hough text-edge deskew; weak estimates leave the page unchanged.</summary>
    public bool? Deskew { get; set; }
    /// <summary>True forces normalization; null uses the profile and applies it only when sampled background variation exceeds 40/255.</summary>
    public bool? NormalizeBackground { get; set; }
    /// <summary>Enable adaptive Otsu/Sauvola binary luminance output. This intentionally discards color and tonal detail.</summary>
    public bool? Binarize { get; set; }
    /// <summary>Uncalibrated orientation threshold in [0,1], default 0.5.</summary>
    public float MinimumOrientationConfidence { get; set; } = 0.5f;
    /// <summary>Maximum absolute skew search angle in [0.5,45] degrees, default 15.</summary>
    public float MaximumSkewAngle { get; set; } = 15f;
    /// <summary>Skip skew corrections below this angle; must be in [0,MaximumSkewAngle]. Default 0.1.</summary>
    public float MinimumSkewAngle { get; set; } = 0.1f;
    /// <summary>Longest diagnostic sample-grid dimension, in [8,4096]. Default 512. Sparse small content can be missed by subsampling.</summary>
    public int MaximumSampleDimension { get; set; } = 512;

    internal void Validate()
    {
        if (!Enum.IsDefined(this.Profile) || !(this.MinimumOrientationConfidence >= 0 && this.MinimumOrientationConfidence <= 1)
            || !(this.MaximumSkewAngle >= 0.5f && this.MaximumSkewAngle <= 45)
            || !(this.MinimumSkewAngle >= 0 && this.MinimumSkewAngle <= this.MaximumSkewAngle)
            || this.MaximumSampleDimension < 8 || this.MaximumSampleDimension > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(DocumentPreprocessingOptions), "Invalid profile, orientation confidence, skew bounds or diagnostic sample dimension.");
        }
    }
}

/// <summary>Maps continuous pixel coordinates (pixel centers at x+0.5,y+0.5). Implementations may be projective or nonlinear.</summary>
public interface IDocumentCoordinateMap
{
    /// <summary>Maps source coordinates to processed coordinates; false when no reliable finite mapping is available. Mapping does not imply the point lies inside the output canvas.</summary>
    bool TryMapToProcessed(PointF source, out PointF processed);

    /// <summary>Maps processed coordinates to source coordinates; false when no reliable finite mapping is available. Mapping does not imply the point lies inside the source canvas.</summary>
    bool TryMapToSource(PointF processed, out PointF source);
}

/// <summary>A stage decision and its wall time, including any analysis used for the decision.</summary>
public sealed class DocumentPreprocessingStage
{
    internal DocumentPreprocessingStage(string name, bool applied, string reason, double? confidence, TimeSpan elapsed)
    {
        this.Name = name;
        this.Applied = applied;
        this.Reason = reason;
        this.Confidence = confidence;
        this.Elapsed = elapsed;
    }

    public string Name { get; }
    public bool Applied { get; }
    public string Reason { get; }

    /// <summary>Uncalibrated heuristic score, when the detector provides one; null means unavailable, not zero confidence.</summary>
    public double? Confidence { get; }
    public TimeSpan Elapsed { get; }
}

/// <summary>Diagnostics and coordinate mapping for one independently processed frame.</summary>
public sealed class DocumentPagePreprocessingResult
{
    internal DocumentPagePreprocessingResult(int frameIndex, DocumentAnalysis analysis, Size outputSize,
        IDocumentCoordinateMap coordinates, IReadOnlyList<DocumentPreprocessingStage> stages)
    {
        this.FrameIndex = frameIndex;
        this.Analysis = analysis;
        this.OutputSize = outputSize;
        this.Coordinates = coordinates;
        this.Stages = stages;
    }

    public int FrameIndex { get; }
    /// <summary>Measurements of the original page, before preprocessing.</summary>
    public DocumentAnalysis Analysis { get; }
    public Size OutputSize { get; }
    public IDocumentCoordinateMap Coordinates { get; }
    public IReadOnlyList<DocumentPreprocessingStage> Stages { get; }
}

/// <summary>Owns the output image. Dispose the result when finished; the source image is never owned or changed.</summary>
public sealed class DocumentPreprocessingResult<TPixel> : IDisposable
    where TPixel : unmanaged, IPixel<TPixel>
{
    internal DocumentPreprocessingResult(Image<TPixel> image, IReadOnlyList<DocumentPagePreprocessingResult> pages)
    {
        this.Image = image;
        this.Pages = pages;
    }

    /// <summary>Owned processed image. Subsequent caller mutations do not update recorded geometry or diagnostics.</summary>
    public Image<TPixel> Image { get; }
    public IReadOnlyList<DocumentPagePreprocessingResult> Pages { get; }
    public void Dispose() => this.Image.Dispose();
}
