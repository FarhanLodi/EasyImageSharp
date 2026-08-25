using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>
/// The outcome of document-orientation detection: how far the page is currently rotated, the lossless rotation
/// that uprights it, and the class probabilities.
/// </summary>
public sealed class OrientationResult
{
    /// <summary>The four classes in model output order: how many degrees clockwise the page is currently rotated.</summary>
    public static readonly int[] ClassAngles = [0, 90, 180, 270];

    private OrientationResult(int detectedAngle, RotateMode correction, float confidence, float[] probabilities)
    {
        this.DetectedAngle = detectedAngle;
        this.Correction = correction;
        this.Confidence = confidence;
        this.Probabilities = probabilities;
    }

    /// <summary>Degrees clockwise the page is currently rotated away from upright: 0, 90, 180 or 270.</summary>
    public int DetectedAngle { get; }

    /// <summary>The clockwise rotation that uprights the page (<c>(360 - DetectedAngle) % 360</c>); <see cref="RotateMode.None"/> when already upright.</summary>
    public RotateMode Correction { get; }

    /// <summary>Probability of the winning class (0-1).</summary>
    public float Confidence { get; }

    /// <summary>Probabilities over the classes {0, 90, 180, 270} degrees, summing to 1.</summary>
    public IReadOnlyList<float> Probabilities { get; }

    /// <summary>True when no rotation is needed.</summary>
    public bool IsUpright => this.DetectedAngle == 0;

    /// <summary>
    /// Interprets four classifier scores over {0, 90, 180, 270} degrees. Raw logits are soft-maxed; scores that
    /// already form a probability distribution (all in 0-1, summing to 1) are used as they are.
    /// </summary>
    public static OrientationResult FromScores(ReadOnlySpan<float> scores)
    {
        if (scores.Length < 4)
        {
            throw new ModelContractException($"An orientation classifier must output 4 scores ({{0,90,180,270}}); got {scores.Length}.");
        }

        float[] probabilities = ToProbabilities(scores[..4]);
        int best = 0;
        for (int i = 1; i < probabilities.Length; i++)
        {
            if (probabilities[i] > probabilities[best])
            {
                best = i;
            }
        }

        return new OrientationResult(ClassAngles[best], CorrectionFor(best), probabilities[best], probabilities);
    }

    /// <summary>Maps a class index (0..3 = page rotated 0/90/180/270 degrees clockwise) to the rotation that uprights it.</summary>
    public static RotateMode CorrectionFor(int classIndex) => classIndex switch
    {
        0 => RotateMode.None,
        1 => RotateMode.Rotate270,
        2 => RotateMode.Rotate180,
        3 => RotateMode.Rotate90,
        _ => throw new ArgumentOutOfRangeException(nameof(classIndex), classIndex, "Class index must be 0..3."),
    };

    private static float[] ToProbabilities(ReadOnlySpan<float> scores)
    {
        bool alreadyProbabilities = true;
        float sum = 0f;
        foreach (float s in scores)
        {
            if (s < 0f || s > 1f || float.IsNaN(s))
            {
                alreadyProbabilities = false;
                break;
            }

            sum += s;
        }

        if (alreadyProbabilities && MathF.Abs(sum - 1f) < 1e-3f)
        {
            return scores.ToArray();
        }

        float max = float.NegativeInfinity;
        foreach (float s in scores)
        {
            max = MathF.Max(max, s);
        }

        var result = new float[scores.Length];
        float total = 0f;
        for (int i = 0; i < scores.Length; i++)
        {
            result[i] = MathF.Exp(scores[i] - max);
            total += result[i];
        }

        for (int i = 0; i < result.Length; i++)
        {
            result[i] /= total;
        }

        return result;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"OrientationResult [ DetectedAngle={this.DetectedAngle}, Correction={this.Correction}, Confidence={this.Confidence:0.000} ]";
}
