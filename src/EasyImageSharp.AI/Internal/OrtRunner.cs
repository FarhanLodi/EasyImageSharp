using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>A float32 tensor read back from a model output.</summary>
internal readonly record struct TensorResult(float[] Data, long[] Shape)
{
    /// <summary>Interprets the shape as [N,C,H,W] / [C,H,W] / [H,W] and returns (channels, height, width).</summary>
    public (int Channels, int Height, int Width) AsImageShape()
    {
        long[] s = this.Shape;
        return s.Length switch
        {
            4 => (checked((int)s[1]), checked((int)s[2]), checked((int)s[3])),
            3 => (checked((int)s[0]), checked((int)s[1]), checked((int)s[2])),
            2 => (1, checked((int)s[0]), checked((int)s[1])),
            _ => throw new ModelContractException(
                $"Expected an image-shaped output tensor ([1,C,H,W]), got rank {s.Length} [{string.Join(",", s)}]."),
        };
    }
}

/// <summary>Thin wrapper over the <see cref="InferenceSession"/> OrtValue run API (zero-copy input, one output copy).</summary>
internal static class OrtRunner
{
    /// <summary>Picks the input to feed: the preferred name when the graph has it, else the single/first input.</summary>
    public static string ResolveInputName(InferenceSession session, string? preferred)
    {
        IReadOnlyDictionary<string, NodeMetadata> inputs = session.InputMetadata;
        if (!string.IsNullOrEmpty(preferred) && inputs.ContainsKey(preferred))
        {
            return preferred;
        }

        foreach (KeyValuePair<string, NodeMetadata> pair in inputs)
        {
            return pair.Key;
        }

        throw new ModelContractException("The model has no inputs.");
    }

    /// <summary>Picks the output to read: the preferred name when the graph has it, else the first output.</summary>
    public static string ResolveOutputName(InferenceSession session, string? preferred)
    {
        IReadOnlyDictionary<string, NodeMetadata> outputs = session.OutputMetadata;
        if (!string.IsNullOrEmpty(preferred) && outputs.ContainsKey(preferred))
        {
            return preferred;
        }

        foreach (KeyValuePair<string, NodeMetadata> pair in outputs)
        {
            return pair.Key;
        }

        throw new ModelContractException("The model has no outputs.");
    }

    /// <summary>
    /// True when the input's leading (batch) axis is dynamic, i.e. several samples can be stacked into one
    /// <c>[N,...]</c> tensor. A fixed leading dimension (typically 1) means samples must be run one at a time.
    /// </summary>
    public static bool HasDynamicBatch(InferenceSession session, string inputName)
        => session.InputMetadata.TryGetValue(inputName, out NodeMetadata? metadata)
            && metadata.IsTensor
            && metadata.Dimensions.Length >= 1
            && metadata.Dimensions[0] <= 0;

    /// <summary>
    /// Runs one float32 tensor and copies the named float32 outputs back. <paramref name="cancellationToken"/> is
    /// wired to <see cref="RunOptions.Terminate"/>, so a long native run stops promptly when it is cancelled.
    /// </summary>
    public static TensorResult[] RunCancelable(
        InferenceSession session, string inputName, Memory<float> data, long[] shape, IReadOnlyList<string> outputNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckShape(data.Length, shape);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data, shape);
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(runOptions, [inputName], [input], outputNames.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            var copies = new TensorResult[results.Count];
            for (int i = 0; i < copies.Length; i++)
            {
                copies[i] = Copy(results[i], outputNames[i]);
            }

            return copies;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Runs one float32 tensor and writes the single named output straight into <paramref name="output"/>, a
    /// caller-owned buffer of exactly <paramref name="outputShape"/>. Used to reuse one output buffer across
    /// batches once the output shape for a given input shape is known.
    /// </summary>
    public static void RunInto(
        InferenceSession session, string inputName, Memory<float> data, long[] shape, string outputName,
        Memory<float> output, long[] outputShape, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckShape(data.Length, shape);
        CheckShape(output.Length, outputShape);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data, shape);
        using OrtValue bound = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, output, outputShape);
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            session.Run(runOptions, [inputName], [input], [outputName], [bound]);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void CheckShape(int length, long[] shape)
    {
        long expected = 1;
        foreach (long dim in shape)
        {
            expected *= dim;
        }

        if (expected != length)
        {
            throw new ArgumentException($"Tensor shape [{string.Join(",", shape)}] does not match {length} values.", nameof(shape));
        }
    }

    private static TensorResult Copy(OrtValue output, string outputName)
    {
        if (!output.IsTensor)
        {
            throw new ModelContractException($"Output '{outputName}' is not a tensor.");
        }

        OrtTensorTypeAndShapeInfo info = output.GetTensorTypeAndShape();
        if (info.ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float)
        {
            throw new ModelContractException(
                $"Output '{outputName}' has element type {info.ElementDataType}; only float32 outputs are supported. Export the model with float32 outputs.");
        }

        return new TensorResult(output.GetTensorDataAsSpan<float>().ToArray(), info.Shape);
    }

    /// <summary>Runs one float32 tensor through the model and copies the named float32 output back.</summary>
    public static TensorResult Run(InferenceSession session, string inputName, float[] data, long[] shape, string outputName)
    {
        long expected = 1;
        foreach (long dim in shape)
        {
            expected *= dim;
        }

        if (expected != data.Length)
        {
            throw new ArgumentException($"Tensor shape [{string.Join(",", shape)}] does not match {data.Length} values.", nameof(shape));
        }

        using OrtValue input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data.AsMemory(), shape);
        using var runOptions = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> results = session.Run(runOptions, [inputName], [input], [outputName]);

        OrtValue output = results[0];
        if (!output.IsTensor)
        {
            throw new ModelContractException($"Output '{outputName}' is not a tensor.");
        }

        OrtTensorTypeAndShapeInfo info = output.GetTensorTypeAndShape();
        if (info.ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float)
        {
            throw new ModelContractException(
                $"Output '{outputName}' has element type {info.ElementDataType}; only float32 outputs are supported. Export the model with float32 outputs.");
        }

        return new TensorResult(output.GetTensorDataAsSpan<float>().ToArray(), info.Shape);
    }
}
