using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>The synchronous core of <c>ClassifyContent</c> over an already-loaded session (timm ViT classifier contract).</summary>
internal static class ContentModerationRunner
{
    /// <summary>Model metadata key holding the comma-separated class names in output order.</summary>
    internal const string LabelsMetadataKey = "labels";

    private const int DefaultInputSize = 384;
    private const string PreferredOutput = "logits";

    /// <summary>Classifies the root frame.</summary>
    public static ContentClassification Classify<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelDescriptor descriptor = ModelRegistry.ContentModeration;
        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, PreferredOutput);
        int size = VisionInference.ResolveSquareInputSize(session, inputName, 3, DefaultInputSize);
        string[] labels = ResolveLabels(session);

        float[] input;
        Image<TPixel> frame = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
        try
        {
            input = VisionInference.ShortSideCenterCrop(frame, size, KnownResamplers.Bicubic, descriptor.Normalization);
        }
        finally
        {
            if (isCopy)
            {
                frame.Dispose();
            }
        }

        TensorResult result = VisionInference.Run(session, [new ModelInput(inputName, input, [1, 3, size, size])], [outputName], cancellationToken)[0];
        if (result.Data.Length != labels.Length || !(result.Shape.Length == 1 || (result.Shape.Length == 2 && result.Shape[0] == 1)))
        {
            throw new ModelContractException(
                $"The moderation model must return [1,{labels.Length}] scores for the classes {string.Join(", ", labels)}; " +
                $"output '{outputName}' has shape [{string.Join(",", result.Shape)}].");
        }

        // The contract is raw logits (the timm head has no softmax), so the scores are always soft-maxed.
        return new ContentClassification(labels, VisionInference.Softmax(result.Data));
    }

    /// <summary>The class names from the model metadata, or the built-in (NSFW, SFW) pair when the export has none.</summary>
    internal static string[] ResolveLabels(InferenceSession session)
    {
        string[] labels = [ContentClassification.UnsafeLabel, ContentClassification.SafeLabel];
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue(LabelsMetadataKey, out string? declared) && !string.IsNullOrWhiteSpace(declared))
        {
            labels = declared.Split(',', StringSplitOptions.TrimEntries);
        }

        if (labels.Length < 2 || labels.Any(string.IsNullOrEmpty)
            || !labels.Contains(ContentClassification.UnsafeLabel, StringComparer.OrdinalIgnoreCase)
            || labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
        {
            throw new ModelContractException(
                $"The moderation model's '{LabelsMetadataKey}' metadata must list at least two distinct class names including " +
                $"'{ContentClassification.UnsafeLabel}'; got '{string.Join(",", labels)}'.");
        }

        return labels;
    }
}
