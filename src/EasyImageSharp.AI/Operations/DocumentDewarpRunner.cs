using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

internal static class DocumentDewarpRunner
{
    internal static (Image<TPixel> Image, IDocumentCoordinateMap Map) Run<TPixel>(InferenceSession session,
        Image<TPixel> source, DocumentDewarpContract contract, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        contract.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Size work = contract.FixedInputSize.IsEmpty ? new Size(source.Width, source.Height) : contract.FixedInputSize;
        long paddedWidth = ((long)work.Width + contract.SizeMultiple - 1) / contract.SizeMultiple * contract.SizeMultiple;
        long paddedHeight = ((long)work.Height + contract.SizeMultiple - 1) / contract.SizeMultiple * contract.SizeMultiple;
        if (paddedWidth > int.MaxValue || paddedHeight > int.MaxValue || paddedWidth * paddedHeight > Math.Min(contract.MaximumInputPixels, int.MaxValue / 3))
        {
            throw new ModelContractException("Padded dewarp input exceeds the configured pixel budget or tensor buffer limit.");
        }

        int width = (int)paddedWidth, height = (int)paddedHeight;
        if (contract.OutputKind == DocumentDewarpOutputKind.NormalizedSamplingGrid && (width != work.Width || height != work.Height))
        {
            throw new ModelContractException("Normalized-grid exports require unpadded input coordinates. Set SizeMultiple=1 or provide divisible input dimensions.");
        }
        using Image<TPixel> resized = FrameUtil.ResizeCopy(source, work.Width, work.Height, KnownResamplers.Bilinear);
        using Image<TPixel> inputImage = FrameUtil.PadReplicate(resized, width, height);
        float[] input = new float[checked(width * height * 3)];
        inputImage.WriteTensor(input, new TensorWriteOptions { Channels = contract.Channels });
        string inputName = ResolveName(session.InputMetadata.Keys, contract.InputName, "input");
        string outputName = ResolveName(session.OutputMetadata.Keys, contract.OutputName, "output");
        TensorResult prediction = RunCancelable(session, inputName, input, [1, 3, height, width], outputName, cancellationToken);
        if (prediction.Shape.Length != 4 || prediction.Shape[0] != 1 || prediction.Shape[2] < 1 || prediction.Shape[3] < 1
            || prediction.Data.Any(value => !float.IsFinite(value)))
        {
            throw new ModelContractException("Dewarp must return a finite float32 [1,C,H,W] tensor with positive spatial dimensions.");
        }

        if (contract.OutputKind == DocumentDewarpOutputKind.NormalizedSamplingGrid)
        {
            if (prediction.Shape[1] != 2)
            {
                throw new ModelContractException("Sampling-grid dewarp requires exactly two output channels (normalized X,Y).");
            }

            var map = new SamplingGridMap(prediction.Data, checked((int)prediction.Shape[3]), checked((int)prediction.Shape[2]),
                new Size(source.Width, source.Height), contract.GridAlignCorners);
            return (WarpOriginal(source, map, cancellationToken), map);
        }

        if (prediction.Shape[1] != 3 || prediction.Shape[2] != height || prediction.Shape[3] != width)
        {
            throw new ModelContractException("Rectified-image dewarp must return [1,3,H,W] at the padded input resolution. Select an explicit grid contract for grid exports.");
        }

        var output = new Image<TPixel>(work.Width, work.Height);
        try
        {
            int plane = width * height;
            for (int y = 0; y < work.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<TPixel> row = output.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    int i = (y * width) + x;
                    float first = prediction.Data[i], green = prediction.Data[plane + i], third = prediction.Data[(2 * plane) + i];
                    row[x] = TPixel.FromScaledVector4(contract.Channels == TensorChannels.Bgr
                        ? new Vector4(third, green, first, 1) : new Vector4(first, green, third, 1));
                }
            }

            if (output.Width != source.Width || output.Height != source.Height)
            {
                output.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(source.Width, source.Height), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bilinear }));
            }

            return (output, UnavailableDocumentMap.Instance);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Image<TPixel> WarpOriginal<TPixel>(Image<TPixel> source, SamplingGridMap map, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var output = new Image<TPixel>(source.Width, source.Height);
        try
        {
            for (int y = 0; y < output.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<TPixel> row = output.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < output.Width; x++)
                {
                    if (!map.TryMapToSource(new PointF(x + 0.5f, y + 0.5f), out PointF p))
                    {
                        throw new ModelContractException("The sampling grid produced a non-finite coordinate.");
                    }

                    double sx = Math.Clamp(p.X - 0.5, 0, source.Width - 1);
                    double sy = Math.Clamp(p.Y - 0.5, 0, source.Height - 1);
                    int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, source.Width - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
                    Vector4 top = Vector4.Lerp(Premultiply(source[x0, y0].ToScaledVector4()), Premultiply(source[x1, y0].ToScaledVector4()), (float)(sx - x0));
                    Vector4 bottom = Vector4.Lerp(Premultiply(source[x0, y1].ToScaledVector4()), Premultiply(source[x1, y1].ToScaledVector4()), (float)(sx - x0));
                    Vector4 color = Vector4.Lerp(top, bottom, (float)(sy - y0));
                    row[x] = TPixel.FromScaledVector4(color.W > 0 ? new Vector4(color.X / color.W, color.Y / color.W, color.Z / color.W, color.W) : Vector4.Zero);
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Vector4 Premultiply(Vector4 value) => new(value.X * value.W, value.Y * value.W, value.Z * value.W, value.W);

    internal static string ResolveName(IEnumerable<string> names, string? requested, string role)
    {
        string[] available = names.ToArray();
        if (requested is not null)
        {
            return available.Contains(requested, StringComparer.Ordinal) ? requested
                : throw new ModelContractException($"Requested {role} '{requested}' does not exist.");
        }

        return available.Length == 1 ? available[0]
            : throw new ModelContractException($"The model has {available.Length} {role}s; select the required {role} explicitly.");
    }

    internal static TensorResult RunCancelable(InferenceSession session, string inputName, float[] input, long[] shape,
        string outputName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using OrtValue value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(), shape);
        using var options = new RunOptions();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using IDisposableReadOnlyCollection<OrtValue> result = session.Run(options, [inputName], [value], [outputName]);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result[0].IsTensor || result[0].GetTensorTypeAndShape().ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float)
            {
                throw new ModelContractException("Document model output must be a float32 tensor.");
            }

            return new TensorResult(result[0].GetTensorDataAsSpan<float>().ToArray(), result[0].GetTensorTypeAndShape().Shape);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

internal sealed class UnavailableDocumentMap : IDocumentCoordinateMap
{
    internal static UnavailableDocumentMap Instance { get; } = new();
    public bool TryMapToProcessed(PointF source, out PointF processed) { processed = default; return false; }
    public bool TryMapToSource(PointF processed, out PointF source) { source = default; return false; }
}

internal sealed class SamplingGridMap : IDocumentCoordinateMap
{
    private readonly float[] grid;
    private readonly int width, height;
    private readonly Size original;
    private readonly bool alignCorners;

    internal SamplingGridMap(float[] grid, int width, int height, Size original, bool alignCorners)
    {
        this.grid = grid;
        this.width = width;
        this.height = height;
        this.original = original;
        this.alignCorners = alignCorners;
    }

    // A general grid can fold or be non-injective. Do not fabricate a unique forward inverse.
    public bool TryMapToProcessed(PointF source, out PointF processed) { processed = default; return false; }

    public bool TryMapToSource(PointF processed, out PointF source)
    {
        source = default;
        if (!float.IsFinite(processed.X) || !float.IsFinite(processed.Y))
        {
            return false;
        }

        double gx = this.alignCorners ? (processed.X - 0.5) * (this.width - 1) / Math.Max(1, this.original.Width - 1) : (processed.X * this.width / this.original.Width) - 0.5;
        double gy = this.alignCorners ? (processed.Y - 0.5) * (this.height - 1) / Math.Max(1, this.original.Height - 1) : (processed.Y * this.height / this.original.Height) - 0.5;
        gx = Math.Clamp(gx, 0, this.width - 1);
        gy = Math.Clamp(gy, 0, this.height - 1);
        double nx = this.Sample(gx, gy, 0), ny = this.Sample(gx, gy, this.width * this.height);
        double sx = this.alignCorners ? ((nx + 1) * (this.original.Width - 1) / 2) + 0.5 : (nx + 1) * this.original.Width / 2;
        double sy = this.alignCorners ? ((ny + 1) * (this.original.Height - 1) / 2) + 0.5 : (ny + 1) * this.original.Height / 2;
        // Rendering uses border replication. Report that same sampled source position.
        source = new PointF((float)Math.Clamp(sx, 0.5, this.original.Width - 0.5),
            (float)Math.Clamp(sy, 0.5, this.original.Height - 0.5));
        return float.IsFinite(source.X) && float.IsFinite(source.Y);
    }

    private double Sample(double x, double y, int offset)
    {
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, this.width - 1), y1 = Math.Min(y0 + 1, this.height - 1);
        double fx = x - x0, fy = y - y0;
        double top = (this.grid[offset + (y0 * this.width) + x0] * (1 - fx)) + (this.grid[offset + (y0 * this.width) + x1] * fx);
        double bottom = (this.grid[offset + (y1 * this.width) + x0] * (1 - fx)) + (this.grid[offset + (y1 * this.width) + x1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }
}
