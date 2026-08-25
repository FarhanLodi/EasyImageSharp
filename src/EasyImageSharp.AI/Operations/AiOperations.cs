using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// The synchronous cores of every built-in operation, expressed over an already-loaded
/// <see cref="InferenceSession"/> and the core tensor bridges. The public extension methods resolve sessions and
/// dispatch here; tests exercise these directly with tiny stand-in models.
/// </summary>
internal static class AiOperations
{
    private const int OrientationSize = 224;
    private const int SaliencySize = 320;

    // ----- Orientation -----

    /// <summary>Classifies the root frame's orientation (PP-LCNet doc-ori contract).</summary>
    public static OrientationResult DetectOrientation<TPixel>(InferenceSession session, Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> single = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
        try
        {
            return DetectOrientationCore(session, single);
        }
        finally
        {
            if (isCopy)
            {
                single.Dispose();
            }
        }
    }

    private static OrientationResult DetectOrientationCore<TPixel>(InferenceSession session, Image<TPixel> singleFrame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ModelDescriptor descriptor = ModelRegistry.DocumentOrientation;
        using Image<TPixel> resized = FrameUtil.ResizeCopy(singleFrame, OrientationSize, OrientationSize, KnownResamplers.Bicubic);
        float[] input = resized.ToChwTensor(descriptor.Normalization.Mean, descriptor.Normalization.Std);

        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, null);
        TensorResult result = OrtRunner.Run(session, inputName, input, [1, 3, OrientationSize, OrientationSize], outputName);
        if (result.Data.Length < 4)
        {
            throw new ModelContractException(
                $"The orientation classifier returned {result.Data.Length} values (shape [{string.Join(",", result.Shape)}]); expected [1,4].");
        }

        return OrientationResult.FromScores(result.Data.AsSpan(0, 4));
    }

    /// <summary>Uprights every frame in place (each classified individually) and returns the root frame's correction.</summary>
    public static RotateMode AutoOrient<TPixel>(InferenceSession session, Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (image.Frames.Count == 1)
        {
            RotateMode correction = DetectOrientationCore(session, image).Correction;
            if (correction != RotateMode.None)
            {
                image.Mutate(ctx => ctx.Rotate(correction));
            }

            return correction;
        }

        RotateMode rootCorrection = RotateMode.None;
        int index = 0;
        ForEachFrame(image, frame =>
        {
            RotateMode correction = DetectOrientationCore(session, frame).Correction;
            if (index++ == 0)
            {
                rootCorrection = correction;
            }

            return correction == RotateMode.None ? frame.Clone() : frame.Clone(ctx => ctx.Rotate(correction));
        });
        return rootCorrection;
    }

    // ----- Image-to-image operations -----

    /// <summary>Dewarps every frame in place (UVDoc contract; output resized back to the frame size).</summary>
    public static void Dewarp<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
        => ForEachFrame(image, frame => ImageModelRunner.Run(session, frame, ImageModels.DocumentDewarp.Contract, cancellationToken));

    /// <summary>
    /// Upscales every frame in place by <paramref name="factor"/>: the network's native scale is detected from its
    /// output (x4 for Real-ESRGAN) and the result is resampled to exactly <c>factor</c> times the source when the two differ.
    /// </summary>
    public static void Upscale<TPixel>(InferenceSession session, Image<TPixel> image, int factor, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Upscale factor must be at least 1.");
        }

        ImageModelContract contract = ImageModels.SuperResolutionX4.Contract with { ScaleFactor = 0 };
        ForEachFrame(image, frame =>
        {
            Image<TPixel> result = ImageModelRunner.Run(session, frame, contract, cancellationToken);
            int targetWidth = checked(frame.Width * factor);
            int targetHeight = checked(frame.Height * factor);
            if (result.Width == targetWidth && result.Height == targetHeight)
            {
                return result;
            }

            try
            {
                bool downscaling = targetWidth < result.Width;
                result.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = downscaling ? KnownResamplers.Lanczos3 : KnownResamplers.Bicubic,
                }));
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        });
    }

    /// <summary>Denoises every frame in place (DnCNN gray residual contract). The result is grayscale.</summary>
    public static void Denoise<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
        => ForEachFrame(image, frame => ImageModelRunner.Run(session, frame, ImageModels.DenoiseGray.Contract, cancellationToken));

    // ----- Saliency / background -----

    /// <summary>Computes the root frame's saliency mask at the frame's size (U2-Net-p contract; 255 = foreground).</summary>
    public static Image<L8> SaliencyMask<TPixel>(InferenceSession session, Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> single = FrameUtil.FrameAsImage(image, 0, out bool isCopy);
        try
        {
            return SaliencyMaskCore(session, single);
        }
        finally
        {
            if (isCopy)
            {
                single.Dispose();
            }
        }
    }

    private static Image<L8> SaliencyMaskCore<TPixel>(InferenceSession session, Image<TPixel> singleFrame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ModelDescriptor descriptor = ModelRegistry.Saliency;
        using Image<TPixel> resized = FrameUtil.ResizeCopy(singleFrame, SaliencySize, SaliencySize, KnownResamplers.Bilinear);
        float[] input = resized.ToChwTensor(descriptor.Normalization.Mean, descriptor.Normalization.Std);

        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, null);
        TensorResult result = OrtRunner.Run(session, inputName, input, [1, 3, SaliencySize, SaliencySize], outputName);
        (int channels, int height, int width) = result.AsImageShape();
        if (channels < 1 || width <= 0 || height <= 0 || result.Data.Length < width * height)
        {
            throw new ModelContractException($"Unexpected saliency output shape [{string.Join(",", result.Shape)}]; expected [1,1,H,W].");
        }

        // First plane = the fused prediction; squash logits if the export skipped the sigmoid, then stretch to 0-1
        // like the reference post-processing so the mask uses the full range.
        Span<float> plane = result.Data.AsSpan(0, width * height);
        bool needsSigmoid = false;
        foreach (float v in plane)
        {
            if (v < 0f || v > 1f)
            {
                needsSigmoid = true;
                break;
            }
        }

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < plane.Length; i++)
        {
            if (needsSigmoid)
            {
                plane[i] = 1f / (1f + MathF.Exp(-plane[i]));
            }

            min = MathF.Min(min, plane[i]);
            max = MathF.Max(max, plane[i]);
        }

        if (max - min > 1e-6f)
        {
            float range = max - min;
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = (plane[i] - min) / range;
            }
        }

        Image<L8> mask = TensorImage.FromGrayscaleTensor<L8>(plane, width, height);
        try
        {
            if (mask.Width != singleFrame.Width || mask.Height != singleFrame.Height)
            {
                mask.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(singleFrame.Width, singleFrame.Height),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Bilinear,
                }));
            }

            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    /// <summary>Applies a saliency mask to every frame in place (alpha or background colour), optionally cropping to the foreground.</summary>
    public static void RemoveBackground<TPixel>(InferenceSession session, Image<TPixel> image, BackgroundRemovalOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool hasAlpha = TPixel.FromRgba32(Rgba32.Transparent).ToRgba32().A != byte.MaxValue;
        ForEachFrame(image, frame =>
        {
            using Image<L8> mask = SaliencyMaskCore(session, frame);
            Image<TPixel> result = ApplyMask(frame, mask, hasAlpha, options);
            if (!options.CropToForeground)
            {
                return result;
            }

            try
            {
                Rectangle bounds = ForegroundBounds(mask, options.ForegroundThreshold, options.CropPadding);
                if (bounds.IsEmpty || (bounds.Width == result.Width && bounds.Height == result.Height))
                {
                    return result;
                }

                Image<TPixel> cropped = FrameUtil.CropCopy(result, bounds);
                result.Dispose();
                return cropped;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        });
    }

    private static Image<TPixel> ApplyMask<TPixel>(Image<TPixel> frame, Image<L8> mask, bool hasAlpha, BackgroundRemovalOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        var result = new Image<TPixel>(width, height);
        ImageFrame<TPixel> src = frame.Frames.RootFrame;
        ImageFrame<TPixel> dst = result.Frames.RootFrame;
        ImageFrame<L8> maskFrame = mask.Frames.RootFrame;
        bool toAlpha = hasAlpha && options.BackgroundColor is null;
        Rgba32 background = (options.BackgroundColor ?? Color.White).ToRgba32();

        for (int y = 0; y < height; y++)
        {
            Span<TPixel> srcRow = src.GetRowSpan(y);
            Span<TPixel> dstRow = dst.GetRowSpan(y);
            Span<L8> maskRow = maskFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                Rgba32 p = srcRow[x].ToRgba32();
                int m = maskRow[x].PackedValue;
                Rgba32 outPixel;
                if (toAlpha)
                {
                    outPixel = new Rgba32(p.R, p.G, p.B, (byte)((p.A * m + 127) / 255));
                }
                else
                {
                    int inv = 255 - m;
                    outPixel = new Rgba32(
                        (byte)((p.R * m + background.R * inv + 127) / 255),
                        (byte)((p.G * m + background.G * inv + 127) / 255),
                        (byte)((p.B * m + background.B * inv + 127) / 255),
                        hasAlpha ? p.A : byte.MaxValue);
                }

                dstRow[x] = TPixel.FromRgba32(outPixel);
            }
        }

        return result;
    }

    private static Rectangle ForegroundBounds(Image<L8> mask, float threshold, int padding)
    {
        byte cutoff = (byte)Math.Clamp((int)(threshold * 255f + 0.5f), 0, 255);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        ImageFrame<L8> frame = mask.Frames.RootFrame;
        for (int y = 0; y < mask.Height; y++)
        {
            Span<L8> row = frame.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x].PackedValue >= cutoff && row[x].PackedValue > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0)
        {
            return Rectangle.Empty;
        }

        int left = Math.Max(0, minX - padding);
        int top = Math.Max(0, minY - padding);
        int right = Math.Min(mask.Width, maxX + 1 + padding);
        int bottom = Math.Min(mask.Height, maxY + 1 + padding);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    // ----- Binarisation -----

    /// <summary>Binarises every frame in place using a per-pixel threshold map (SauvolaNet contract).</summary>
    public static void Binarize<TPixel>(InferenceSession session, Image<TPixel> image, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ModelDescriptor descriptor = ModelRegistry.Binarization;
        string inputName = OrtRunner.ResolveInputName(session, descriptor.InputName);
        string outputName = OrtRunner.ResolveOutputName(session, null);
        float mean = descriptor.Normalization.Mean[0];
        float std = descriptor.Normalization.Std[0];

        ForEachFrame(image, frame =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int width = frame.Width;
            int height = frame.Height;
            float[] luminance = frame.ToGrayscaleTensor(mean, std);
            TensorResult result = OrtRunner.Run(session, inputName, luminance, [1, 1, height, width], outputName);
            (int channels, int outHeight, int outWidth) = result.AsImageShape();
            if (channels != 1 || outWidth != width || outHeight != height)
            {
                throw new ModelContractException(
                    $"The binarisation model must return a threshold map of the input shape [1,1,{height},{width}], got [{string.Join(",", result.Shape)}].");
            }

            var output = new Image<TPixel>(width, height);
            TPixel white = TPixel.FromRgba32(Rgba32.White);
            TPixel black = TPixel.FromRgba32(Rgba32.Black);
            float[] thresholds = result.Data;
            for (int y = 0; y < height; y++)
            {
                Span<TPixel> row = output.Frames.RootFrame.GetRowSpan(y);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    row[x] = luminance[offset + x] >= thresholds[offset + x] ? white : black;
                }
            }

            return output;
        });
    }

    // ----- Frame plumbing -----

    /// <summary>
    /// Runs <paramref name="transform"/> over every frame (as a single-frame image, read-only) and writes the
    /// returned images back: in place when sizes match, otherwise by swapping the frame set.
    /// </summary>
    internal static void ForEachFrame<TPixel>(Image<TPixel> image, Func<Image<TPixel>, Image<TPixel>> transform)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int count = image.Frames.Count;
        var replacements = new List<Image<TPixel>>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                Image<TPixel> frame = FrameUtil.FrameAsImage(image, i, out bool isCopy);
                try
                {
                    replacements.Add(transform(frame));
                }
                finally
                {
                    if (isCopy)
                    {
                        frame.Dispose();
                    }
                }
            }

            bool sameSizes = true;
            for (int i = 0; i < count; i++)
            {
                if (replacements[i].Width != image.Frames[i].Width || replacements[i].Height != image.Frames[i].Height)
                {
                    sameSizes = false;
                    break;
                }
            }

            if (sameSizes)
            {
                for (int i = 0; i < count; i++)
                {
                    FrameUtil.CopyInto(replacements[i], image.Frames[i]);
                }
            }
            else
            {
                FrameUtil.ReplaceAllFrames(image, replacements);
            }
        }
        finally
        {
            foreach (Image<TPixel> replacement in replacements)
            {
                replacement.Dispose();
            }
        }
    }
}
