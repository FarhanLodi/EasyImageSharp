namespace EasyImageSharp.AI;

/// <summary>
/// The outcome of <see cref="QualityAssessmentExtensions.AssessQuality{TPixel}"/>: two learned, no-reference opinion
/// scores on a 1-10 scale (NIMA), each the mean of a predicted distribution over the ratings 1..10.
/// </summary>
/// <remarks>
/// The technical score was trained on TID2013 (synthetic distortions such as noise, blur, compression and contrast
/// change) and the aesthetic score on AVA (photography-community ratings). Both predict a mean human rating and are most
/// meaningful for ranking images of similar content against each other; predictions rarely reach the ends of the scale,
/// and the absolute values are not comparable with other metrics.
/// </remarks>
public sealed class QualityAssessment
{
    private readonly float[] technical;
    private readonly float[] aesthetic;

    internal QualityAssessment(float[] technicalDistribution, float[] aestheticDistribution)
    {
        this.technical = technicalDistribution;
        this.aesthetic = aestheticDistribution;
        (this.TechnicalScore, this.TechnicalStandardDeviation) = Moments(technicalDistribution);
        (this.AestheticScore, this.AestheticStandardDeviation) = Moments(aestheticDistribution);
    }

    /// <summary>Predicted mean technical-quality rating, 1 (worst) to 10 (best).</summary>
    public float TechnicalScore { get; }

    /// <summary>Standard deviation of the predicted technical-rating distribution (how much raters would disagree).</summary>
    public float TechnicalStandardDeviation { get; }

    /// <summary>Predicted probability of each technical rating 1..10 (index 0 = rating 1), summing to 1.</summary>
    public IReadOnlyList<float> TechnicalDistribution => this.technical;

    /// <summary>Predicted mean aesthetic rating, 1 (worst) to 10 (best).</summary>
    public float AestheticScore { get; }

    /// <summary>Standard deviation of the predicted aesthetic-rating distribution.</summary>
    public float AestheticStandardDeviation { get; }

    /// <summary>Predicted probability of each aesthetic rating 1..10 (index 0 = rating 1), summing to 1.</summary>
    public IReadOnlyList<float> AestheticDistribution => this.aesthetic;

    /// <inheritdoc />
    public override string ToString()
        => $"QualityAssessment [ Technical={this.TechnicalScore:0.00} (sd {this.TechnicalStandardDeviation:0.00}), Aesthetic={this.AestheticScore:0.00} (sd {this.AestheticStandardDeviation:0.00}) ]";

    private static (float Mean, float StandardDeviation) Moments(float[] distribution)
    {
        double total = 0, mean = 0;
        for (int i = 0; i < distribution.Length; i++)
        {
            total += distribution[i];
            mean += (i + 1) * (double)distribution[i];
        }

        mean /= total;
        double variance = 0;
        for (int i = 0; i < distribution.Length; i++)
        {
            double d = (i + 1) - mean;
            variance += d * d * distribution[i];
        }

        return ((float)mean, (float)Math.Sqrt(variance / total));
    }
}
