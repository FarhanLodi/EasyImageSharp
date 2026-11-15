using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>One named float32 input tensor of a model call.</summary>
internal readonly record struct ModelInput(string Name, float[] Data, long[] Shape);

/// <summary>
/// Shared plumbing of the whole-image vision models (embeddings, content moderation, learned quality) and of the
/// inpainting runner: the evaluation transforms their reference pipelines use, input-metadata validation and a
/// cancellable multi-input ONNX Runtime call.
/// </summary>
internal static class VisionInference
{
    // Largest intermediate the exact resize-then-crop transform may allocate. Beyond it (only extreme aspect ratios
    // reach it) the centred square is cropped from the source first, which differs only by filter edge effects.
    private const long MaxIntermediatePixels = 16_000_000;

    /// <summary>
    /// The CLIP / timm evaluation transform of the root frame: shorter side to <paramref name="size"/> (longer side
    /// truncated, <c>floor(size * long / short)</c>), centred <paramref name="size"/> x <paramref name="size"/> crop
    /// with a floor offset, then a planar RGB tensor normalised as <c>(byte / 255 - mean[c]) / std[c]</c>.
    /// </summary>
    public static float[] ShortSideCenterCrop<TPixel>(Image<TPixel> image, int size, IResampler sampler, TensorNormalization normalization)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = image.Width;
        int height = image.Height;
        long shortSide = Math.Min(width, height);
        long longSide = Math.Max(width, height);
        long scaledLong = size * longSide / shortSide;
        if (scaledLong * size > MaxIntermediatePixels)
        {
            int side = (int)shortSide;
            Rectangle square = width >= height
                ? new Rectangle((width - side) / 2, 0, side, side)
                : new Rectangle(0, (height - side) / 2, side, side);
            using Image<TPixel> center = FrameUtil.CropCopy(image.Frames.RootFrame, square);
            using Image<TPixel> scaled = FrameUtil.ResizeCopy(center, size, size, sampler);
            return scaled.ToChwTensor(normalization.Mean, normalization.Std);
        }

        int resizedWidth = width <= height ? size : (int)scaledLong;
        int resizedHeight = width <= height ? (int)scaledLong : size;
        using Image<TPixel> resized = FrameUtil.ResizeCopy(image, resizedWidth, resizedHeight, sampler);
        if (resizedWidth == size && resizedHeight == size)
        {
            return resized.ToChwTensor(normalization.Mean, normalization.Std);
        }

        var crop = new Rectangle((resizedWidth - size) / 2, (resizedHeight - size) / 2, size, size);
        using Image<TPixel> cropped = FrameUtil.CropCopy(resized, crop);
        return cropped.ToChwTensor(normalization.Mean, normalization.Std);
    }

    /// <summary>
    /// Validates a float32 <c>[N,C,H,W]</c> image input and returns its square spatial size: the fixed size the graph
    /// declares, or <paramref name="defaultSize"/> when its spatial axes are dynamic.
    /// </summary>
    public static int ResolveSquareInputSize(InferenceSession session, string inputName, int channels, int defaultSize)
    {
        NodeMetadata metadata = session.InputMetadata[inputName];
        int[] dims = metadata.Dimensions;
        if (metadata.ElementType != typeof(float) || dims.Length != 4 || (dims[1] > 0 && dims[1] != channels))
        {
            throw new ModelContractException(
                $"Input '{inputName}' must be a float32 [N,{channels},H,W] tensor; the model declares {metadata.ElementType.Name} [{string.Join(",", dims)}].");
        }

        int height = dims[2];
        int width = dims[3];
        if (height <= 0 && width <= 0)
        {
            return defaultSize;
        }

        if (height != width || height <= 0)
        {
            throw new ModelContractException(
                $"Input '{inputName}' must be square with both spatial axes fixed or both dynamic; the model declares [{string.Join(",", dims)}].");
        }

        return height;
    }

    /// <summary>
    /// Runs the model with the given float32 inputs and copies the requested float32 outputs back, in order.
    /// Cancellation terminates a run in progress and surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static TensorResult[] Run(InferenceSession session, IReadOnlyList<ModelInput> inputs, IReadOnlyList<string> outputNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = new string[inputs.Count];
        var values = new OrtValue[inputs.Count];
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                ModelInput input = inputs[i];
                long expected = 1;
                foreach (long dim in input.Shape)
                {
                    expected *= dim;
                }

                if (expected != input.Data.Length)
                {
                    throw new ArgumentException($"Tensor shape [{string.Join(",", input.Shape)}] does not match {input.Data.Length} values.", nameof(inputs));
                }

                names[i] = input.Name;
                values[i] = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.Data.AsMemory(), input.Shape);
            }

            using var options = new RunOptions();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => options.Terminate = true);
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(options, names, values, outputNames.ToArray());
            cancellationToken.ThrowIfCancellationRequested();

            var tensors = new TensorResult[outputNames.Count];
            for (int i = 0; i < tensors.Length; i++)
            {
                OrtValue output = results[i];
                if (!output.IsTensor)
                {
                    throw new ModelContractException($"Output '{outputNames[i]}' is not a tensor.");
                }

                OrtTensorTypeAndShapeInfo info = output.GetTensorTypeAndShape();
                if (info.ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float)
                {
                    throw new ModelContractException(
                        $"Output '{outputNames[i]}' has element type {info.ElementDataType}; only float32 outputs are supported.");
                }

                float[] data = output.GetTensorDataAsSpan<float>().ToArray();
                foreach (float value in data)
                {
                    if (!float.IsFinite(value))
                    {
                        throw new ModelContractException($"Output '{outputNames[i]}' contains a non-finite value.");
                    }
                }

                tensors[i] = new TensorResult(data, info.Shape);
            }

            return tensors;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            foreach (OrtValue? value in values)
            {
                value?.Dispose();
            }
        }
    }

    /// <summary>Numerically stable softmax in double precision.</summary>
    public static float[] Softmax(ReadOnlySpan<float> logits)
    {
        double max = double.NegativeInfinity;
        foreach (float value in logits)
        {
            max = Math.Max(max, value);
        }

        var exp = new double[logits.Length];
        double sum = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            exp[i] = Math.Exp(logits[i] - max);
            sum += exp[i];
        }

        var result = new float[logits.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (float)(exp[i] / sum);
        }

        return result;
    }
}
