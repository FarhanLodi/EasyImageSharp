using System.Globalization;

namespace EasyImageSharp.Processing;

/// <summary>One colour cluster found by <see cref="ProcessingExtensions.GetDominantColors{TPixel}(Image{TPixel}, int, DominantColorOptions?)"/>.</summary>
public readonly struct DominantColor
{
    /// <summary>Initializes a cluster result.</summary>
    public DominantColor(Color color, float fraction)
    {
        this.Color = color;
        this.Fraction = fraction;
    }

    /// <summary>The cluster's mean colour (averaged in OKLab and converted back to opaque sRGB).</summary>
    public Color Color { get; }

    /// <summary>The share of the measured pixels assigned to this cluster, 0-1; the shares of one result sum to 1.</summary>
    public float Fraction { get; }

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"DominantColor [ #{this.Color.R:X2}{this.Color.G:X2}{this.Color.B:X2}, {this.Fraction * 100:F1}% ]");
}

/// <summary>Options of <see cref="ProcessingExtensions.GetDominantColors{TPixel}(Image{TPixel}, int, DominantColorOptions?)"/>.</summary>
public sealed class DominantColorOptions
{
    private readonly int maxSampleDimension = 256;
    private readonly byte minimumAlpha = 26;
    private readonly int maxIterations = 32;

    /// <summary>
    /// The image is point-sampled on a regular grid of at most this many samples along its longer side, 16-4096,
    /// or 0 to use every pixel. Defaults to 256.
    /// </summary>
    public int MaxSampleDimension
    {
        get => this.maxSampleDimension;
        init => this.maxSampleDimension = value == 0 || value is >= 16 and <= 4096
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The sample dimension must be 0 or between 16 and 4096.");
    }

    /// <summary>Pixels with an alpha below this value are ignored (near-transparent). Defaults to 26 (about 10%).</summary>
    public byte MinimumAlpha
    {
        get => this.minimumAlpha;
        init => this.minimumAlpha = value;
    }

    /// <summary>The most k-means refinement rounds, 1-1000. Defaults to 32; clustering usually converges sooner.</summary>
    public int MaxIterations
    {
        get => this.maxIterations;
        init => this.maxIterations = value is >= 1 and <= 1000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Iterations must be between 1 and 1000.");
    }
}
