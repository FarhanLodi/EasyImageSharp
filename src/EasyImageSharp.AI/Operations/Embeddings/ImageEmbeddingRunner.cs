using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>The synchronous core of <c>GetImageEmbedding</c> over an already-loaded session (CLIP image-tower contract).</summary>
internal static class ImageEmbeddingRunner
{
    private const int DefaultInputSize = 224;
    private const string PreferredOutput = "image_embeds";

    /// <summary>Embeds the root frame and returns the L2-normalised vector.</summary>
    public static float[] Embed<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelDescriptor descriptor = ModelRegistry.ImageEmbedding;
        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, PreferredOutput);
        int size = VisionInference.ResolveSquareInputSize(session, inputName, 3, DefaultInputSize);

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
        long[] shape = result.Shape;
        bool vectorShape = (shape.Length == 2 && shape[0] == 1) || shape.Length == 1;
        if (!vectorShape || result.Data.Length == 0)
        {
            throw new ModelContractException(
                $"The image-embedding model must return one vector ([1,D]); output '{outputName}' has shape [{string.Join(",", shape)}].");
        }

        try
        {
            return ImageEmbeddings.Normalize(result.Data);
        }
        catch (ArgumentException ex)
        {
            throw new ModelContractException($"The image-embedding model returned a vector that cannot be normalised: {ex.Message}");
        }
    }
}
