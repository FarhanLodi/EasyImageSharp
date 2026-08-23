using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Drives any image-to-image ONNX model described by an <see cref="ImageModelContract"/>: image to
/// <c>[1,C,H,W]</c> tensor (core bridges), <see cref="InferenceSession"/> run,
/// tensor back to image, with optional fixed-size resizing, size-multiple padding and overlapped tiling.
/// Works on the root frame; the higher-level operations iterate frames themselves.
/// </summary>
public static class ImageModelRunner
{
    /// <summary>Runs <paramref name="image"/> through <paramref name="session"/> and returns the result image (the caller owns it).</summary>
    public static Image<TPixel> Run<TPixel>(
        InferenceSession session, Image<TPixel> image, ImageModelContract contract, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();

        var run = new RunState(
            session,
            OrtRunner.ResolveInputName(session, contract.InputName),
            OrtRunner.ResolveOutputName(session, contract.OutputName),
            contract);

        if (!contract.FixedInputSize.IsEmpty)
        {
            return RunFixedSize(run, image, cancellationToken);
        }

        if (contract.TileSize <= 0 || (image.Width <= contract.TileSize && image.Height <= contract.TileSize))
        {
            Image<TPixel> single = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
            try
            {
                return RunPatch(run, single, requireIntegerScale: true, cancellationToken);
            }
            finally
            {
                if (isCopy)
                {
                    single.Dispose();
                }
            }
        }

        return RunTiled(run, image, cancellationToken);
    }

    // ----- Whole image at a fixed network size -----

    private static Image<TPixel> RunFixedSize<TPixel>(RunState run, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Size fixedSize = run.Contract.FixedInputSize;
        using Image<TPixel> resized = FrameUtil.ResizeCopy(image, fixedSize.Width, fixedSize.Height, KnownResamplers.Bilinear);
        Image<TPixel> output = RunPatch(run, resized, requireIntegerScale: false, cancellationToken);
        try
        {
            int scale = run.Scale > 0 ? run.Scale : 1;
            int targetWidth = checked(image.Width * scale);
            int targetHeight = checked(image.Height * scale);
            if (output.Width != targetWidth || output.Height != targetHeight)
            {
                output.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Bicubic,
                }));
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    // ----- Overlapped tiling -----

    private static Image<TPixel> RunTiled<TPixel>(RunState run, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = image.Width;
        int height = image.Height;
        int tile = run.Contract.TileSize;
        int overlap = run.Contract.TileOverlap;
        ImageFrame<TPixel> source = image.Frames.RootFrame;
        Image<TPixel>? output = null;

        try
        {
            for (int ty = 0; ty < height; ty += tile)
            {
                int regionHeight = Math.Min(tile, height - ty);
                int extTop = Math.Max(0, ty - overlap);
                int extBottom = Math.Min(height, ty + regionHeight + overlap);

                for (int tx = 0; tx < width; tx += tile)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int regionWidth = Math.Min(tile, width - tx);
                    int extLeft = Math.Max(0, tx - overlap);
                    int extRight = Math.Min(width, tx + regionWidth + overlap);

                    using Image<TPixel> patch = FrameUtil.CropCopy(source, new Rectangle(extLeft, extTop, extRight - extLeft, extBottom - extTop));
                    using Image<TPixel> result = RunPatch(run, patch, requireIntegerScale: true, cancellationToken);

                    int scale = run.Scale;
                    output ??= new Image<TPixel>(checked(width * scale), checked(height * scale));
                    FrameUtil.CopyRegion(
                        result.Frames.RootFrame,
                        (tx - extLeft) * scale,
                        (ty - extTop) * scale,
                        output.Frames.RootFrame,
                        tx * scale,
                        ty * scale,
                        regionWidth * scale,
                        regionHeight * scale);
                }
            }

            return output!;
        }
        catch
        {
            output?.Dispose();
            throw;
        }
    }

    // ----- One network pass -----

    /// <summary>
    /// Feeds one single-frame image (padded to the contract's size multiple) through the network and converts
    /// the output back. Infers / validates the spatial scale on <paramref name="run"/>.
    /// </summary>
    private static Image<TPixel> RunPatch<TPixel>(RunState run, Image<TPixel> patch, bool requireIntegerScale, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageModelContract contract = run.Contract;

        int multiple = contract.SizeMultiple;
        int paddedWidth = RoundUp(patch.Width, multiple);
        int paddedHeight = RoundUp(patch.Height, multiple);
        Image<TPixel> networkInput = paddedWidth == patch.Width && paddedHeight == patch.Height
            ? patch
            : FrameUtil.PadReplicate(patch, paddedWidth, paddedHeight);

        try
        {
            TensorNormalization inNorm = contract.InputNormalization;
            float[] input = contract.InputChannels == 3
                ? networkInput.ToChwTensor(inNorm.Mean, inNorm.Std)
                : networkInput.ToGrayscaleTensor(inNorm.Mean[0], inNorm.Std[0]);
            long[] shape = [1, contract.InputChannels, paddedHeight, paddedWidth];

            TensorResult result = OrtRunner.Run(run.Session, run.InputName, input, shape, run.OutputName);
            (int outChannels, int outHeight, int outWidth) = result.AsImageShape();
            if (result.Data.Length < outChannels * outHeight * outWidth)
            {
                throw new ModelContractException("Output tensor is smaller than its declared shape.");
            }

            int scale = ResolveScale(run, paddedWidth, paddedHeight, outWidth, outHeight, requireIntegerScale);

            if (contract.OutputKind == ImageModelOutputKind.Residual)
            {
                if (outChannels != contract.InputChannels || outWidth != paddedWidth || outHeight != paddedHeight)
                {
                    throw new ModelContractException(
                        $"A residual model must return the input shape [1,{contract.InputChannels},{paddedHeight},{paddedWidth}], got [1,{outChannels},{outHeight},{outWidth}].");
                }

                float[] data = result.Data;
                for (int i = 0; i < input.Length; i++)
                {
                    data[i] = input[i] - data[i];
                }
            }

            TensorNormalization outNorm = contract.OutputNormalization;
            Image<TPixel> converted = outChannels switch
            {
                3 => TensorImage.FromChwTensor<TPixel>(result.Data, outWidth, outHeight, outNorm.Mean, outNorm.Std),
                1 => TensorImage.FromGrayscaleTensor<TPixel>(result.Data, outWidth, outHeight, outNorm.Mean[0], outNorm.Std[0]),
                _ => throw new ModelContractException($"Output has {outChannels} channels; only 1 (gray) or 3 (RGB) can be converted to an image."),
            };

            // Drop the replicated padding (scaled) when we added any.
            if (scale > 0 && (paddedWidth != patch.Width || paddedHeight != patch.Height))
            {
                int keepWidth = patch.Width * scale;
                int keepHeight = patch.Height * scale;
                if (keepWidth <= converted.Width && keepHeight <= converted.Height && (keepWidth != converted.Width || keepHeight != converted.Height))
                {
                    Image<TPixel> cropped = FrameUtil.CropCopy(converted, new Rectangle(0, 0, keepWidth, keepHeight));
                    converted.Dispose();
                    return cropped;
                }
            }

            return converted;
        }
        finally
        {
            if (!ReferenceEquals(networkInput, patch))
            {
                networkInput.Dispose();
            }
        }
    }

    private static int ResolveScale(RunState run, int inWidth, int inHeight, int outWidth, int outHeight, bool requireIntegerScale)
    {
        int scale = outWidth / inWidth;
        bool integral = scale >= 1 && outWidth == inWidth * scale && outHeight == inHeight * scale;
        if (!integral)
        {
            if (requireIntegerScale)
            {
                throw new ModelContractException(
                    $"Output size {outWidth}x{outHeight} is not an integer multiple of the input size {inWidth}x{inHeight}; " +
                    "tiled / whole-image runs need a fully convolutional network with an integer scale factor. " +
                    "For fixed-size networks set ImageModelContract.FixedInputSize.");
            }

            scale = 0;
        }

        int expected = run.Contract.ScaleFactor;
        if (expected > 0 && integral && scale != expected)
        {
            throw new ModelContractException(
                $"The contract declares ScaleFactor {expected} but the model scaled {inWidth}x{inHeight} to {outWidth}x{outHeight} (x{scale}).");
        }

        if (run.Scale == 0)
        {
            run.Scale = integral ? scale : (expected > 0 ? expected : 0);
        }
        else if (integral && run.Scale != scale)
        {
            throw new ModelContractException($"The model's scale factor changed between tiles ({run.Scale} then {scale}).");
        }

        return run.Scale;
    }

    private static int RoundUp(int value, int multiple)
        => multiple <= 1 ? value : checked((value + multiple - 1) / multiple * multiple);

    private sealed class RunState(InferenceSession session, string inputName, string outputName, ImageModelContract contract)
    {
        public InferenceSession Session { get; } = session;

        public string InputName { get; } = inputName;

        public string OutputName { get; } = outputName;

        public ImageModelContract Contract { get; } = contract;

        /// <summary>The spatial scale established by the first pass (0 until then / when non-integral).</summary>
        public int Scale { get; set; }
    }
}
