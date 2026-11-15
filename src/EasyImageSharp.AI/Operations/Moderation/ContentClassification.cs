namespace EasyImageSharp.AI;

/// <summary>
/// The outcome of <see cref="ContentModerationExtensions.ClassifyContent{TPixel}"/>: a probability for every class the
/// moderation model knows, in model output order. The built-in model has two classes, <see cref="UnsafeLabel"/> and
/// <see cref="SafeLabel"/>.
/// </summary>
/// <remarks>
/// The probabilities are the output of a statistical classifier, not a verdict. Thresholds trade false positives for
/// false negatives and should be chosen on data from your own domain; what counts as "not safe for work" is subjective
/// and contextual. Use the result to prioritise or route content for human review, not as the only safeguard.
/// </remarks>
public sealed class ContentClassification
{
    /// <summary>Label of the "not safe for work" class (sexual or explicit content) of the built-in model.</summary>
    public const string UnsafeLabel = "NSFW";

    /// <summary>Label of the "safe for work" class of the built-in model.</summary>
    public const string SafeLabel = "SFW";

    private readonly string[] labels;
    private readonly float[] probabilities;

    internal ContentClassification(string[] labels, float[] probabilities)
    {
        this.labels = labels;
        this.probabilities = probabilities;
        int best = 0;
        for (int i = 1; i < probabilities.Length; i++)
        {
            if (probabilities[i] > probabilities[best])
            {
                best = i;
            }
        }

        this.TopLabel = labels[best];
        this.TopProbability = probabilities[best];
        this.UnsafeProbability = this.GetProbability(UnsafeLabel);
    }

    /// <summary>The class names, in model output order.</summary>
    public IReadOnlyList<string> Labels => this.labels;

    /// <summary>The class probabilities (0-1, summing to 1), parallel to <see cref="Labels"/>.</summary>
    public IReadOnlyList<float> Probabilities => this.probabilities;

    /// <summary>The most probable class.</summary>
    public string TopLabel { get; }

    /// <summary>The probability of <see cref="TopLabel"/>.</summary>
    public float TopProbability { get; }

    /// <summary>The probability of the <see cref="UnsafeLabel"/> class (0-1).</summary>
    public float UnsafeProbability { get; }

    /// <summary>
    /// True when <see cref="UnsafeProbability"/> is at or above <paramref name="threshold"/>. The default of 0.5 is the
    /// model's decision boundary; lower it to catch more (with more false positives), raise it to flag less.
    /// </summary>
    public bool IsUnsafe(float threshold = 0.5f)
    {
        if (!(threshold >= 0f && threshold <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be between 0 and 1.");
        }

        return this.UnsafeProbability >= threshold;
    }

    /// <summary>The probability of the class named <paramref name="label"/> (case-insensitive).</summary>
    /// <exception cref="ArgumentException">The model has no class with that name.</exception>
    public float GetProbability(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        for (int i = 0; i < this.labels.Length; i++)
        {
            if (string.Equals(this.labels[i], label, StringComparison.OrdinalIgnoreCase))
            {
                return this.probabilities[i];
            }
        }

        throw new ArgumentException($"The moderation model has no class '{label}' (classes: {string.Join(", ", this.labels)}).", nameof(label));
    }

    /// <inheritdoc />
    public override string ToString()
        => $"ContentClassification [ {string.Join(", ", this.labels.Select((l, i) => $"{l}={this.probabilities[i]:0.000}"))} ]";
}
