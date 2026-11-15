using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Batched document-orientation classification (PP-LCNet doc-ori contract): every page is preprocessed exactly as the
/// single-image <c>DetectOrientation</c> operation does, and the <c>[N,3,224,224]</c> inputs are stacked up to the
/// batch bound when the model's batch axis is dynamic (the published export's is).
/// </summary>
internal static class OrientationBatch
{
    /// <summary>The square classifier input edge.</summary>
    public const int InputSize = 224;

    /// <summary>The short side the page is scaled to before the centre crop.</summary>
    private const int ResizeShortSide = 256;

    /// <summary>
    /// The network input of one page (root frame): aspect-preserving bilinear resize of the short side to 256, a
    /// centred 224x224 crop, ImageNet mean/std on 0-1 RGB values, CHW.
    /// </summary>
    public static float[] Preprocess<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ModelDescriptor descriptor = ModelRegistry.DocumentOrientation;
        double scale = (double)ResizeShortSide / Math.Min(image.Width, image.Height);
        double scaledWidth = Math.Round(image.Width * scale), scaledHeight = Math.Round(image.Height * scale);
        if (scaledWidth > int.MaxValue || scaledHeight > int.MaxValue || scaledWidth * scaledHeight > 16_000_000)
        {
            throw new ModelContractException("Orientation aspect-ratio resize exceeds the supported working pixel budget.");
        }

        int width = (int)scaledWidth, height = (int)scaledHeight;
        using Image<TPixel> resized = FrameUtil.ResizeCopy(image, width, height, KnownResamplers.Bilinear);
        using Image<TPixel> cropped = FrameUtil.CropCopy(resized, new Rectangle((width - InputSize) / 2, (height - InputSize) / 2, InputSize, InputSize));
        return cropped.ToChwTensor(descriptor.Normalization.Mean, descriptor.Normalization.Std);
    }

    /// <summary>Classifies the root frame of every image; results are in input order.</summary>
    public static OrientationResult[] Detect<TPixel>(
        InferenceSession session, IReadOnlyList<Image<TPixel>> images, int maxBatchSize, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var results = new OrientationResult[images.Count];
        if (images.Count == 0)
        {
            return results;
        }

        string inputName = OrtRunner.ResolveInputName(session, ModelRegistry.DocumentOrientation.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, null);
        var batcher = new OrtBatchRunner(session, inputName, outputName, maxBatchSize);
        long[] sampleShape = [3, InputSize, InputSize];
        var pending = new List<float[]>();
        int next = 0;
        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Add(Preprocess(images[i]));
            if (pending.Count >= batcher.BatchCapacity(pending[0].Length) || i == images.Count - 1)
            {
                next = Flush(batcher, pending, sampleShape, results, next, cancellationToken);
            }
        }

        return results;
    }

    private static int Flush(
        OrtBatchRunner batcher, List<float[]> pending, long[] sampleShape, OrientationResult[] results, int next, CancellationToken cancellationToken)
    {
        int index = 0;
        while (index < pending.Count)
        {
            int count = Math.Min(pending.Count - index, batcher.BatchCapacity(pending[index].Length));
            BatchOutput output = batcher.Run(pending.GetRange(index, count), sampleShape, cancellationToken, out bool rejected);
            if (rejected)
            {
                continue;
            }

            if (output.SampleShape.Length != 1 || output.SampleShape[0] != 4)
            {
                throw new ModelContractException(
                    $"The orientation classifier returned shape [{string.Join(",", output.Shape)}] for {count} page(s); expected [{count},4].");
            }

            for (int i = 0; i < count; i++)
            {
                Span<float> scores = output.Sample(i);
                foreach (float value in scores)
                {
                    if (!float.IsFinite(value))
                    {
                        throw new ModelContractException("The orientation classifier returned a non-finite score.");
                    }
                }

                results[next++] = OrientationResult.FromScores(scores);
            }

            index += count;
        }

        pending.Clear();
        return next;
    }
}
