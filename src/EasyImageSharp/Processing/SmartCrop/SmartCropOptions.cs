namespace EasyImageSharp.Processing;

/// <summary>A region the smart crop should favour (or, with a negative weight, avoid), in source pixel coordinates.</summary>
public readonly struct SmartCropBoost
{
    /// <summary>Initializes a boost region.</summary>
    /// <param name="region">The region in the coordinates of the image being cropped.</param>
    /// <param name="weight">Importance added to every pixel of the region, -10 to 10; 1 roughly equals a strong edge.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is outside -10 to 10 or not finite.</exception>
    public SmartCropBoost(Rectangle region, float weight)
    {
        if (!(weight >= -10f && weight <= 10f))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Boost weight must be between -10 and 10.");
        }

        this.Region = region;
        this.Weight = weight;
    }

    /// <summary>The boosted region in source pixel coordinates.</summary>
    public Rectangle Region { get; }

    /// <summary>The importance added to each pixel of the region.</summary>
    public float Weight { get; }
}

/// <summary>Parameters of the content-aware crop (<see cref="IImageProcessingContext.SmartCrop(int, int, SmartCropOptions)"/>).</summary>
/// <remarks>
/// The image is reduced to an analysis copy of at most <see cref="AnalysisSize"/> pixels per side. Each analysis
/// pixel gets an importance: <see cref="DetailWeight"/> times its luma Laplacian magnitude, plus
/// <see cref="SaturationWeight"/> times its saturation (ignored in near-black and near-white pixels), plus
/// <see cref="SkinWeight"/> times its closeness to a skin chromaticity, plus the boosts, scaled by alpha. Candidate
/// crops of the target aspect ratio and of scales from <see cref="MinScale"/> to 1 slide over a coarse grid; each is
/// scored by the importance inside it, weighted towards the rule-of-thirds lines and away from its borders, minus
/// <see cref="OutsidePenalty"/> times the importance it cuts off, per unit of crop area. The best score wins; scores
/// within a relative 1e-9 count as ties and go to the candidate closest to the image centre (then the larger
/// scale, the topmost and the leftmost), so the result is deterministic and a featureless image is cropped centrally.
/// </remarks>
public sealed class SmartCropOptions
{
    private readonly int analysisSize = 256;
    private readonly float minScale = 1f;
    private readonly float detailWeight = 1f;
    private readonly float saturationWeight = 0.3f;
    private readonly float skinWeight = 1.5f;
    private readonly float outsidePenalty = 0.5f;
    private readonly IReadOnlyList<SmartCropBoost> boosts = Array.Empty<SmartCropBoost>();
    private readonly IResampler sampler = KnownResamplers.Bicubic;

    /// <summary>The longest side of the analysis copy, 32-1024. Defaults to 256.</summary>
    public int AnalysisSize
    {
        get => this.analysisSize;
        init => this.analysisSize = value is >= 32 and <= 1024
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The analysis size must be between 32 and 1024.");
    }

    /// <summary>
    /// The smallest crop considered, as a fraction of the largest crop of the target aspect that fits, 0.1-1.
    /// Defaults to 1: only the largest crop is placed. Lower values let the crop zoom in on dense content.
    /// </summary>
    public float MinScale
    {
        get => this.minScale;
        init => this.minScale = value is >= 0.1f and <= 1f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The minimum scale must be between 0.1 and 1.");
    }

    /// <summary>Whether importance near the third lines of a candidate counts more. Defaults to <see langword="true"/>.</summary>
    public bool RuleOfThirds { get; init; } = true;

    /// <summary>Weight of edge detail (luma Laplacian magnitude), 0-10. Defaults to 1.</summary>
    public float DetailWeight
    {
        get => this.detailWeight;
        init => this.detailWeight = Weight(value);
    }

    /// <summary>Weight of colour saturation, 0-10. Defaults to 0.3.</summary>
    public float SaturationWeight
    {
        get => this.saturationWeight;
        init => this.saturationWeight = Weight(value);
    }

    /// <summary>Weight of skin-like chromaticity, 0-10. Defaults to 1.5. This is a colour heuristic, not face detection.</summary>
    public float SkinWeight
    {
        get => this.skinWeight;
        init => this.skinWeight = Weight(value);
    }

    /// <summary>How much importance cut off by a candidate counts against it, 0-10. Defaults to 0.5.</summary>
    public float OutsidePenalty
    {
        get => this.outsidePenalty;
        init => this.outsidePenalty = Weight(value);
    }

    /// <summary>Regions to favour or avoid, in source pixel coordinates (for example detected faces). Defaults to none.</summary>
    public IReadOnlyList<SmartCropBoost> Boosts
    {
        get => this.boosts;
        init => this.boosts = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The resampler <see cref="IImageProcessingContext.SmartCrop(int, int, SmartCropOptions)"/> resizes the crop with. Defaults to bicubic.</summary>
    public IResampler Sampler
    {
        get => this.sampler;
        init => this.sampler = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static float Weight(float value) => value is >= 0f and <= 10f
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), value, "Weights must be between 0 and 10.");
}
