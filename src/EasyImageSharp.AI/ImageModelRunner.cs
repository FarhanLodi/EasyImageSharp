using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Drives any image-to-image ONNX model described by an <see cref="ImageModelContract"/>: image to
/// <c>[N,C,H,W]</c> tensor (core bridges), <see cref="InferenceSession"/> run,
/// tensor back to image, with optional fixed-size resizing, size-multiple padding and overlapped tiling.
/// Works on the root frame; the higher-level operations iterate frames themselves.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunBatch{TPixel}"/> stacks network inputs of identical shape (tiles of the same size, images that the
/// contract resizes or pads to the same size) along the batch axis, up to a bounded batch size, when the model's
/// leading input dimension is dynamic. Models exported with a fixed batch dimension of 1 run one input at a time,
/// with the same results. Inputs are never padded to a common size beyond what the contract itself prescribes
/// (<see cref="ImageModelContract.FixedInputSize"/>, <see cref="ImageModelContract.SizeMultiple"/> or tiling), so a
/// batched result is the same computation as the sequential one; the values agree up to floating-point rounding
/// inside the runtime, whose summation order can depend on the batch size and on buffer alignment.
/// </para>
/// <para>
/// Every method is safe to call concurrently on one shared <see cref="InferenceSession"/>: each call owns its
/// staging buffers, and ONNX Runtime allows concurrent <c>Run</c> calls on a session.
/// </para>
/// </remarks>
public static class ImageModelRunner
{
    /// <summary>The batch size <see cref="RunBatch{TPixel}"/> uses when none is given.</summary>
    public const int DefaultMaxBatchSize = 8;

    /// <summary>Runs <paramref name="image"/> through <paramref name="session"/> and returns the result image (the caller owns it).</summary>
    public static Image<TPixel> Run<TPixel>(
        InferenceSession session, Image<TPixel> image, ImageModelContract contract, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        return RunCore(session, [image], contract, maxBatchSize: 1, cancellationToken)[0];
    }

    /// <summary>
    /// Runs every image of <paramref name="images"/> (root frames) through <paramref name="session"/> and returns
    /// one result per image, in order (the caller owns them). Each result is what <see cref="Run{TPixel}"/> returns
    /// for that image (up to the runtime's float rounding). Network inputs of identical shape are stacked into batches of at most
    /// <paramref name="maxBatchSize"/> when the model has a dynamic batch dimension, and run one at a time otherwise.
    /// </summary>
    /// <param name="session">The loaded model.</param>
    /// <param name="images">The images to run; none may be <c>null</c>. An empty list returns an empty array.</param>
    /// <param name="contract">How to feed the model and read its output.</param>
    /// <param name="maxBatchSize">
    /// Upper bound on the inputs stacked into one inference call (1 disables stacking). A batch is further limited to
    /// 64 Mi input floats, so very large tiles are stacked less.
    /// </param>
    /// <param name="cancellationToken">Cancels between inference calls and terminates a running one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBatchSize"/> is less than 1.</exception>
    /// <exception cref="ModelContractException">The model's input or output does not match the contract.</exception>
    public static Image<TPixel>[] RunBatch<TPixel>(
        InferenceSession session,
        IReadOnlyList<Image<TPixel>> images,
        ImageModelContract contract,
        int maxBatchSize = DefaultMaxBatchSize,
        CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        contract.Validate();
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i] is null)
            {
                throw new ArgumentException($"Image {i} of the batch is null.", nameof(images));
            }
        }

        return images.Count == 0 ? [] : RunCore(session, images, contract, maxBatchSize, cancellationToken);
    }

    // ----- Planning and batching -----

    private static Image<TPixel>[] RunCore<TPixel>(
        InferenceSession session, IReadOnlyList<Image<TPixel>> images, ImageModelContract contract, int maxBatchSize, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var run = new RunState(
            OrtRunner.ResolveInputName(session, contract.InputName),
            OrtRunner.ResolveOutputName(session, contract.OutputName),
            contract);
        var batcher = new OrtBatchRunner(session, run.InputName, run.OutputName, maxBatchSize);
        var jobs = new ImageJob<TPixel>[images.Count];

        // Pending inputs grouped by tensor shape, flushed whenever a group fills a batch and at the end in
        // first-seen order. Insertion-ordered so that the run order (and the scale the first pass establishes)
        // does not depend on hashing.
        var groups = new List<PatchGroup<TPixel>>();
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                jobs[i] = new ImageJob<TPixel>(images[i]);
                foreach (Patch<TPixel> patch in PlanPatches(run, jobs[i]))
                {
                    PatchGroup<TPixel> group = FindGroup(groups, patch.PaddedWidth, patch.PaddedHeight);
                    group.Patches.Add(patch);
                    if (group.Patches.Count >= batcher.BatchCapacity(patch.Input.Length))
                    {
                        Flush(run, batcher, group, cancellationToken);
                    }
                }
            }

            foreach (PatchGroup<TPixel> group in groups)
            {
                Flush(run, batcher, group, cancellationToken);
            }

            var results = new Image<TPixel>[jobs.Length];
            for (int i = 0; i < jobs.Length; i++)
            {
                results[i] = Finish(run, jobs[i]);
            }

            return results;
        }
        catch
        {
            foreach (ImageJob<TPixel>? job in jobs)
            {
                job?.Result?.Dispose();
            }

            throw;
        }
    }

    private static PatchGroup<TPixel> FindGroup<TPixel>(List<PatchGroup<TPixel>> groups, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (PatchGroup<TPixel> candidate in groups)
        {
            if (candidate.Width == width && candidate.Height == height)
            {
                return candidate;
            }
        }

        var group = new PatchGroup<TPixel>(width, height);
        groups.Add(group);
        return group;
    }

    /// <summary>Cuts one image into the network inputs the contract calls for (one, or one per tile).</summary>
    private static IEnumerable<Patch<TPixel>> PlanPatches<TPixel>(RunState run, ImageJob<TPixel> job)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageModelContract contract = run.Contract;
        Image<TPixel> image = job.Source;

        if (!contract.FixedInputSize.IsEmpty)
        {
            // Whole image at a fixed network size; the output is resized back in Finish.
            Size fixedSize = contract.FixedInputSize;
            using Image<TPixel> resized = FrameUtil.ResizeCopy(image, fixedSize.Width, fixedSize.Height, KnownResamplers.Bilinear);
            yield return PreparePatch(run, job, resized, PatchKind.FixedSize, requireIntegerScale: false, default);
            yield break;
        }

        if (contract.TileSize <= 0 || (image.Width <= contract.TileSize && image.Height <= contract.TileSize))
        {
            Image<TPixel> single = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
            try
            {
                yield return PreparePatch(run, job, single, PatchKind.Whole, requireIntegerScale: true, default);
            }
            finally
            {
                if (isCopy)
                {
                    single.Dispose();
                }
            }

            yield break;
        }

        // Overlapped tiling: each tile carries `overlap` pixels of context on every inner side; only its centre
        // region is copied into the output.
        int width = image.Width;
        int height = image.Height;
        int tile = contract.TileSize;
        int overlap = contract.TileOverlap;
        ImageFrame<TPixel> source = image.Frames.RootFrame;
        for (int ty = 0; ty < height; ty += tile)
        {
            int regionHeight = Math.Min(tile, height - ty);
            int extTop = Math.Max(0, ty - overlap);
            int extBottom = Math.Min(height, ty + regionHeight + overlap);

            for (int tx = 0; tx < width; tx += tile)
            {
                int regionWidth = Math.Min(tile, width - tx);
                int extLeft = Math.Max(0, tx - overlap);
                int extRight = Math.Min(width, tx + regionWidth + overlap);

                using Image<TPixel> crop = FrameUtil.CropCopy(source, new Rectangle(extLeft, extTop, extRight - extLeft, extBottom - extTop));
                var placement = new TilePlacement(tx, ty, tx - extLeft, ty - extTop, regionWidth, regionHeight);
                yield return PreparePatch(run, job, crop, PatchKind.Tile, requireIntegerScale: true, placement);
            }
        }
    }

    /// <summary>Pads one single-frame network input to the contract's size multiple and converts it to a tensor.</summary>
    private static Patch<TPixel> PreparePatch<TPixel>(
        RunState run, ImageJob<TPixel> job, Image<TPixel> patch, PatchKind kind, bool requireIntegerScale, TilePlacement placement)
        where TPixel : unmanaged, IPixel<TPixel>
    {
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
            float[] input;
            if (contract.InputChannels == 3 && contract.UseBgrChannelOrder)
            {
                input = new float[checked(paddedWidth * paddedHeight * 3)];
                networkInput.WriteTensor(input, new TensorWriteOptions { Channels = TensorChannels.Bgr }, inNorm.Mean, inNorm.Std);
            }
            else
            {
                input = contract.InputChannels == 3
                    ? networkInput.ToChwTensor(inNorm.Mean, inNorm.Std)
                    : networkInput.ToGrayscaleTensor(inNorm.Mean[0], inNorm.Std[0]);
            }

            return new Patch<TPixel>(job, kind, input, paddedWidth, paddedHeight, patch.Width, patch.Height, requireIntegerScale, placement);
        }
        finally
        {
            if (!ReferenceEquals(networkInput, patch))
            {
                networkInput.Dispose();
            }
        }
    }

    /// <summary>Runs the pending inputs of one group (stacked when possible) and delivers each output to its image.</summary>
    private static void Flush<TPixel>(RunState run, OrtBatchRunner batcher, PatchGroup<TPixel> group, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        List<Patch<TPixel>> patches = group.Patches;
        long[] sampleShape = [run.Contract.InputChannels, group.Height, group.Width];
        int index = 0;
        while (index < patches.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(patches.Count - index, batcher.BatchCapacity(patches[index].Input.Length));
            var inputs = new float[count][];
            for (int i = 0; i < count; i++)
            {
                inputs[i] = patches[index + i].Input;
            }

            BatchOutput output = batcher.Run(inputs, sampleShape, cancellationToken, out bool rejected);
            if (rejected)
            {
                // The graph refused a stacked tensor; the runner now runs one input at a time.
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                Deliver(run, patches[index + i], output.Sample(i), output.SampleShape);
            }

            index += count;
        }

        patches.Clear();
    }

    // ----- One network output -----

    /// <summary>Converts one output sample back to pixels and places it into its image's result.</summary>
    private static void Deliver<TPixel>(RunState run, Patch<TPixel> patch, Span<float> data, long[] sampleShape)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> converted = ConvertOutput(run, patch, data, sampleShape);
        ImageJob<TPixel> job = patch.Job;
        if (patch.Kind != PatchKind.Tile)
        {
            job.Result = converted;
            return;
        }

        try
        {
            int scale = run.Scale;
            Image<TPixel> source = job.Source;
            job.Result ??= new Image<TPixel>(checked(source.Width * scale), checked(source.Height * scale));
            TilePlacement p = patch.Placement;
            FrameUtil.CopyRegion(
                converted.Frames.RootFrame,
                p.OffsetX * scale,
                p.OffsetY * scale,
                job.Result.Frames.RootFrame,
                p.X * scale,
                p.Y * scale,
                p.Width * scale,
                p.Height * scale);
        }
        finally
        {
            converted.Dispose();
        }
    }

    /// <summary>
    /// Turns one output sample into an image: residual subtraction, BGR swap, denormalisation and removal of the
    /// replicated padding. Infers / validates the spatial scale on <paramref name="run"/>.
    /// </summary>
    private static Image<TPixel> ConvertOutput<TPixel>(RunState run, Patch<TPixel> patch, Span<float> data, long[] sampleShape)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageModelContract contract = run.Contract;
        int paddedWidth = patch.PaddedWidth;
        int paddedHeight = patch.PaddedHeight;
        (int outChannels, int outHeight, int outWidth) = ImageShape(sampleShape);
        if (outChannels < 1 || outHeight < 1 || outWidth < 1 || data.Length < (long)outChannels * outHeight * outWidth)
        {
            throw new ModelContractException("Output tensor is smaller than its declared shape.");
        }

        int scale = ResolveScale(run, paddedWidth, paddedHeight, outWidth, outHeight, patch.RequireIntegerScale);

        if (contract.OutputKind == ImageModelOutputKind.Residual)
        {
            if (outChannels != contract.InputChannels || outWidth != paddedWidth || outHeight != paddedHeight)
            {
                throw new ModelContractException(
                    $"A residual model must return the input shape [1,{contract.InputChannels},{paddedHeight},{paddedWidth}], got [1,{outChannels},{outHeight},{outWidth}].");
            }

            float[] input = patch.Input;
            for (int i = 0; i < input.Length; i++)
            {
                data[i] = input[i] - data[i];
            }
        }

        TensorNormalization outNorm = contract.OutputNormalization;
        float[] outputMean = outNorm.Mean, outputStd = outNorm.Std;
        if (outChannels == 3 && contract.UseBgrChannelOrder)
        {
            int plane = checked(outWidth * outHeight);
            for (int i = 0; i < plane; i++)
            {
                (data[i], data[(2 * plane) + i]) = (data[(2 * plane) + i], data[i]);
            }

            outputMean = outNorm.Mean.Length == 1 ? outNorm.Mean : [outNorm.Mean[2], outNorm.Mean[1], outNorm.Mean[0]];
            outputStd = outNorm.Std.Length == 1 ? outNorm.Std : [outNorm.Std[2], outNorm.Std[1], outNorm.Std[0]];
        }

        Image<TPixel> converted = outChannels switch
        {
            3 => TensorImage.FromChwTensor<TPixel>(data, outWidth, outHeight, outputMean, outputStd),
            1 => TensorImage.FromGrayscaleTensor<TPixel>(data, outWidth, outHeight, outNorm.Mean[0], outNorm.Std[0]),
            _ => throw new ModelContractException($"Output has {outChannels} channels; only 1 (gray) or 3 (RGB) can be converted to an image."),
        };

        // Drop the replicated padding (scaled) when we added any.
        if (scale > 0 && (paddedWidth != patch.Width || paddedHeight != patch.Height))
        {
            int keepWidth = patch.Width * scale;
            int keepHeight = patch.Height * scale;
            if (keepWidth <= converted.Width && keepHeight <= converted.Height && (keepWidth != converted.Width || keepHeight != converted.Height))
            {
                try
                {
                    return FrameUtil.CropCopy(converted, new Rectangle(0, 0, keepWidth, keepHeight));
                }
                finally
                {
                    converted.Dispose();
                }
            }
        }

        return converted;
    }

    /// <summary>Completes one image: a fixed-size output is resized back to the source size times the scale.</summary>
    private static Image<TPixel> Finish<TPixel>(RunState run, ImageJob<TPixel> job)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> output = job.Result ?? throw new InvalidOperationException("The model produced no output for an image of the batch.");
        if (run.Contract.FixedInputSize.IsEmpty)
        {
            return output;
        }

        int scale = run.Scale > 0 ? run.Scale : 1;
        int targetWidth = checked(job.Source.Width * scale);
        int targetHeight = checked(job.Source.Height * scale);
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

    /// <summary>Interprets a per-sample output shape [C,H,W] / [H,W] (or an unbatched [1,C,H,W]) as (channels, height, width).</summary>
    private static (int Channels, int Height, int Width) ImageShape(long[] shape) => shape.Length switch
    {
        4 when shape[0] == 1 => (checked((int)shape[1]), checked((int)shape[2]), checked((int)shape[3])),
        3 => (checked((int)shape[0]), checked((int)shape[1]), checked((int)shape[2])),
        2 => (1, checked((int)shape[0]), checked((int)shape[1])),
        _ => throw new ModelContractException(
            $"Expected an image-shaped output tensor ([N,C,H,W]), got a per-sample shape [{string.Join(",", shape)}]."),
    };

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

    private enum PatchKind
    {
        /// <summary>The whole root frame (padded to the size multiple).</summary>
        Whole,

        /// <summary>The whole root frame stretched to the contract's fixed size.</summary>
        FixedSize,

        /// <summary>One overlapped tile.</summary>
        Tile,
    }

    /// <summary>Where a tile's kept centre region goes: target origin, offset inside the tile, and size (source pixels).</summary>
    private readonly record struct TilePlacement(int X, int Y, int OffsetX, int OffsetY, int Width, int Height);

    /// <summary>One network input waiting for its batch.</summary>
    private sealed record Patch<TPixel>(
        ImageJob<TPixel> Job,
        PatchKind Kind,
        float[] Input,
        int PaddedWidth,
        int PaddedHeight,
        int Width,
        int Height,
        bool RequireIntegerScale,
        TilePlacement Placement)
        where TPixel : unmanaged, IPixel<TPixel>;

    /// <summary>Pending inputs that share one tensor shape.</summary>
    private sealed class PatchGroup<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public List<Patch<TPixel>> Patches { get; } = [];
    }

    /// <summary>One image of the batch and its (partial) result.</summary>
    private sealed class ImageJob<TPixel>(Image<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        public Image<TPixel> Source { get; } = source;

        public Image<TPixel>? Result { get; set; }
    }

    private sealed class RunState(string inputName, string outputName, ImageModelContract contract)
    {
        public string InputName { get; } = inputName;

        public string OutputName { get; } = outputName;

        public ImageModelContract Contract { get; } = contract;

        /// <summary>The spatial scale established by the first pass (0 until then / when non-integral).</summary>
        public int Scale { get; set; }
    }
}
