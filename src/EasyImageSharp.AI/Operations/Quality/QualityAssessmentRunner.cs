using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>The synchronous core of <c>AssessQuality</c> over an already-loaded session (two-head NIMA contract).</summary>
internal static class QualityAssessmentRunner
{
    private const int DefaultInputSize = 224;
    private const int Ratings = 10;
    private const string TechnicalOutput = "technical";
    private const string AestheticOutput = "aesthetic";

    /// <summary>Scores the root frame.</summary>
    public static QualityAssessment Assess<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelDescriptor descriptor = ModelRegistry.QualityAssessment;
        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        foreach (string required in new[] { TechnicalOutput, AestheticOutput })
        {
            if (!session.OutputMetadata.ContainsKey(required))
            {
                throw new ModelContractException(
                    $"The quality model must have the outputs '{TechnicalOutput}' and '{AestheticOutput}'; it has {string.Join(", ", session.OutputMetadata.Keys)}.");
            }
        }

        int size = VisionInference.ResolveSquareInputSize(session, inputName, 3, DefaultInputSize);
        float[] input = NearestStretchTensor(image.Frames.RootFrame, size, descriptor.Normalization);
        TensorResult[] results = VisionInference.Run(
            session, [new ModelInput(inputName, input, [1, 3, size, size])], [TechnicalOutput, AestheticOutput], cancellationToken);
        return new QualityAssessment(ToDistribution(results[0], TechnicalOutput), ToDistribution(results[1], AestheticOutput));
    }

    /// <summary>
    /// The upstream evaluation resize (Keras <c>load_img</c> with PIL nearest-neighbour): the frame is stretched to
    /// <paramref name="size"/> squared, sampling source pixel <c>floor((x + 0.5) * width / size)</c>, and written as a
    /// planar RGB tensor normalised as <c>(byte / 255 - mean) / std</c>.
    /// </summary>
    internal static float[] NearestStretchTensor<TPixel>(ImageFrame<TPixel> frame, int size, TensorNormalization normalization)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        float[] mean = Expand(normalization.Mean);
        float[] std = Expand(normalization.Std);
        int plane = size * size;
        var tensor = new float[3 * plane];
        var columns = new int[size];
        for (int x = 0; x < size; x++)
        {
            columns[x] = Math.Min((int)(((2L * x) + 1) * frame.Width / (2L * size)), frame.Width - 1);
        }

        for (int y = 0; y < size; y++)
        {
            int sourceY = Math.Min((int)(((2L * y) + 1) * frame.Height / (2L * size)), frame.Height - 1);
            Span<TPixel> row = frame.GetRowSpan(sourceY);
            for (int x = 0; x < size; x++)
            {
                Rgba32 pixel = row[columns[x]].ToRgba32();
                int i = (y * size) + x;
                tensor[i] = ((pixel.R / 255f) - mean[0]) / std[0];
                tensor[plane + i] = ((pixel.G / 255f) - mean[1]) / std[1];
                tensor[(2 * plane) + i] = ((pixel.B / 255f) - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    private static float[] Expand(float[] values) => values.Length == 1 ? [values[0], values[0], values[0]] : values;

    private static float[] ToDistribution(TensorResult result, string name)
    {
        if (result.Data.Length != Ratings || !(result.Shape.Length == 1 || (result.Shape.Length == 2 && result.Shape[0] == 1)))
        {
            throw new ModelContractException(
                $"Quality output '{name}' must be a [1,{Ratings}] distribution over the ratings 1..{Ratings}; got shape [{string.Join(",", result.Shape)}].");
        }

        double sum = 0;
        foreach (float p in result.Data)
        {
            if (p < 0f)
            {
                throw new ModelContractException($"Quality output '{name}' must be a softmax distribution; it contains a negative value.");
            }

            sum += p;
        }

        if (Math.Abs(sum - 1.0) > 0.01)
        {
            throw new ModelContractException($"Quality output '{name}' must be a softmax distribution; its values sum to {sum:0.###}.");
        }

        float[] distribution = result.Data;
        for (int i = 0; i < distribution.Length; i++)
        {
            distribution[i] = (float)(distribution[i] / sum);
        }

        return distribution;
    }
}
