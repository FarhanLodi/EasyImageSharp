using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Tensors;

public static partial class ImageTensorExtensions
{
    /// <summary>Computes tensor canvas dimensions and coordinate mapping without resizing or allocating pixels.</summary>
    public static TensorResizeMapping GetTensorResizeMapping<TPixel>(this Image<TPixel> image, ResizeOptions resizeOptions)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(resizeOptions);
        var plan = ImageProcessingContext<TPixel>.ComputeResizeTargets(resizeOptions, image.Width, image.Height);
        return new TensorResizeMapping(new Size(image.Width, image.Height), new Size(plan.CanvasWidth, plan.CanvasHeight),
            new Rectangle(plan.OffsetX, plan.OffsetY, plan.ContentWidth, plan.ContentHeight));
    }

    /// <summary>
    /// Resizes the root frame into caller-owned tensor storage, using a rolling cache of filtered rows
    /// instead of a full resized image. Sampling, integer pixel quantization, alpha handling, companding,
    /// and all resize modes match Resize followed by WriteTensor. Canvas padding is normalized like image
    /// pixels; destination stride gaps remain untouched. Returns the actual rounded resize geometry.
    /// </summary>
    public static TensorResizeMapping ResizeToTensor<TPixel>(this Image<TPixel> image, Span<float> destination,
        ResizeOptions resizeOptions, TensorWriteOptions? tensorOptions = null,
        ReadOnlySpan<float> channelMean = default, ReadOnlySpan<float> channelStd = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(resizeOptions);
        ArgumentNullException.ThrowIfNull(resizeOptions.Sampler);
        using var execution = tensorOptions?.ExecutionOptions is { } executionOptions ? OperationExecution.Enter(executionOptions) : null;
        OperationExecution.Checkpoint();
        TensorResizeMapping mapping = image.GetTensorResizeMapping(resizeOptions);
        int width = mapping.OutputSize.Width;
        int height = mapping.OutputSize.Height;
        (TensorLayout layout, TensorChannels channels, int offset, long rowStride, long planeStride) =
            ValidateTensorDestination(width, height, destination.Length, tensorOptions, channelMean, channelStd);
        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);
        Span<float> normalization = stackalloc float[768];
        if (!PixelOps.IsHighPrecision<TPixel>())
        {
            for (int i = 0; i < 256; i++)
            {
                float value = i / 255f;
                normalization[i] = (value - mr) / sr;
                normalization[256 + i] = (value - mg) / sg;
                normalization[512 + i] = (value - mb) / sb;
            }
        }

        Rectangle bounds = mapping.ContentBounds;
        bool identity = bounds.Width == image.Width && bounds.Height == image.Height;
        bool nearest = resizeOptions.Sampler is NearestNeighborResampler;
        bool weighted = !identity && !nearest;
        if (!identity && (!(resizeOptions.Sampler.Radius > 0f) || !float.IsFinite(resizeOptions.Sampler.Radius)
            || resizeOptions.Sampler.Radius > 1048576f))
        {
            throw new ArgumentException("Resampler radius must be finite, positive, and no greater than 1048576.", nameof(resizeOptions));
        }

        bool premultiply = resizeOptions.PremultiplyAlpha && PixelFormatInfo<TPixel>.HasAlpha;
        bool compand = resizeOptions.Compand;
        ResizeKernelMap? horizontal = weighted ? ResizeKernelMap.Build(image.Width, bounds.Width, resizeOptions.Sampler) : null;
        ResizeKernelMap? vertical = weighted ? ResizeKernelMap.Build(image.Height, bounds.Height, resizeOptions.Sampler) : null;
        int slots = vertical?.MaxLength ?? 1;
        // Only the source rows needed by one vertical kernel window are retained.
        int cacheLength = weighted ? checked(slots * bounds.Width) : 1;
        using var buffers = new ResizeTensorBuffers<TPixel>(width, weighted ? image.Width : 1, weighted ? bounds.Width : 1, cacheLength, slots);
        TPixel pad = TPixel.FromRgba32(resizeOptions.PadColor.ToRgba32());
        int firstX = (int)Math.Max(0L, bounds.X);
        int lastX = (int)Math.Min(width, (long)bounds.X + bounds.Width);
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
            Span<TPixel> pixels = buffers.Output.AsSpan(0, width);
            long contentY = (long)y - bounds.Y;
            bool hasContent = contentY >= 0 && contentY < bounds.Height && firstX < lastX;
            if (!hasContent || firstX != 0 || lastX != width)
            {
                pixels.Fill(pad);
            }

            if (hasContent)
            {
                int cy = (int)contentY;
                if (!weighted)
                {
                    int sourceY = identity ? cy : (int)((long)cy * image.Height / bounds.Height);
                    Span<TPixel> source = frame.GetRowSpan(sourceY);
                    for (int x = firstX; x < lastX; x++)
                    {
                        int cx = x - bounds.X;
                        int sourceX = identity ? cx : (int)((long)cx * image.Width / bounds.Width);
                        pixels[x] = source[sourceX];
                    }
                }
                else
                {
                    int start = vertical!.Start[cy];
                    int length = vertical.Length[cy];
                    Span<Vector4> accumulator = buffers.Accumulator.AsSpan(0, bounds.Width);
                    for (int tap = 0; tap < length; tap++)
                    {
                        int sourceY = start + tap;
                        int slot = sourceY % slots;
                        Span<Vector4> filtered = buffers.Filtered.AsSpan(slot * bounds.Width, bounds.Width);
                        if (buffers.RowKeys[slot] != sourceY)
                        {
                            OperationExecution.Checkpoint();
                            Span<TPixel> source = frame.GetRowSpan(sourceY);
                            Span<Vector4> vectors = buffers.SourceVectors.AsSpan(0, image.Width);
                            if (PixelOps.IsHighPrecision<TPixel>())
                            {
                                for (int x = 0; x < image.Width; x++)
                                {
                                    vectors[x] = HighPrecisionSampling.Load(source[x].ToScaledVector4(), premultiply, compand);
                                }
                            }
                            else
                            {
                                Span<Rgba32> rgba = buffers.SourceRgba.AsSpan(0, image.Width);
                                PixelOps.ToRgba32<TPixel>(source, rgba);
                                FrameOps.LoadRow(rgba, vectors, premultiply, compand);
                            }

                            FrameOps.HorizontalPass(vectors, filtered, horizontal!);
                            buffers.RowKeys[slot] = sourceY;
                        }

                        FrameOps.AccumulateScaled(MemoryMarshal.Cast<Vector4, float>(filtered),
                            MemoryMarshal.Cast<Vector4, float>(accumulator), vertical.Weights[vertical.Offset[cy] + tap], tap == 0);
                    }

                    for (int x = firstX; x < lastX; x++)
                    {
                        Vector4 value = accumulator[x - bounds.X];
                        pixels[x] = PixelOps.IsHighPrecision<TPixel>()
                            ? TPixel.FromScaledVector4(HighPrecisionSampling.Store(value, premultiply, compand))
                            : TPixel.FromRgba32(FrameOps.StorePixel(value, premultiply, compand));
                    }
                }
            }

            int rowOffset = (int)(offset + (y * rowStride));
            if (!PixelOps.IsHighPrecision<TPixel>())
            {
                WriteResizedByteRow<TPixel>(pixels, destination, rowOffset, (int)planeStride, layout, channels, normalization);
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                Vector4 value = PixelOps.IsHighPrecision<TPixel>()
                    ? pixels[x].ToScaledVector4() : pixels[x].ToRgba32().ToScaledVector4();
                if (channels == TensorChannels.Grayscale)
                {
                    float gray = PixelOps.IsHighPrecision<TPixel>()
                        ? (value.X * 0.2126f) + (value.Y * 0.7152f) + (value.Z * 0.0722f)
                        : L8.FromRgba32(pixels[x].ToRgba32()).PackedValue / 255f;
                    destination[rowOffset + x] = (gray - mr) / sr;
                }
                else
                {
                    int i = rowOffset + (x * (layout == TensorLayout.Hwc ? 3 : 1));
                    int step = layout == TensorLayout.Hwc ? 1 : (int)planeStride;
                    destination[i] = ((channels == TensorChannels.Bgr ? value.Z : value.X) - mr) / sr;
                    destination[i + step] = (value.Y - mg) / sg;
                    destination[i + (2 * step)] = ((channels == TensorChannels.Bgr ? value.X : value.Z) - mb) / sb;
                }
            }
        }

        return mapping;
    }

    private static void WriteResizedByteRow<TPixel>(ReadOnlySpan<TPixel> pixels, Span<float> destination,
        int rowOffset, int planeStride, TensorLayout layout, TensorChannels channels, ReadOnlySpan<float> normalization)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ReadOnlySpan<float> first = normalization[..256];
        if (channels == TensorChannels.Grayscale)
        {
            Span<float> target = destination.Slice(rowOffset, pixels.Length);
            for (int x = 0; x < pixels.Length; x++)
            {
                target[x] = first[L8.FromRgba32(pixels[x].ToRgba32()).PackedValue];
            }

            return;
        }

        ReadOnlySpan<float> green = normalization.Slice(256, 256);
        ReadOnlySpan<float> third = normalization.Slice(512, 256);
        ReadOnlySpan<float> red = channels == TensorChannels.Rgb ? first : third;
        ReadOnlySpan<float> blue = channels == TensorChannels.Rgb ? third : first;
        if (layout == TensorLayout.Chw)
        {
            int redOffset = channels == TensorChannels.Rgb ? rowOffset : rowOffset + (2 * planeStride);
            int blueOffset = channels == TensorChannels.Rgb ? rowOffset + (2 * planeStride) : rowOffset;
            Span<float> r = destination.Slice(redOffset, pixels.Length);
            Span<float> g = destination.Slice(rowOffset + planeStride, pixels.Length);
            Span<float> b = destination.Slice(blueOffset, pixels.Length);
            for (int x = 0; x < pixels.Length; x++)
            {
                Rgba32 pixel = pixels[x].ToRgba32();
                r[x] = red[pixel.R];
                g[x] = green[pixel.G];
                b[x] = blue[pixel.B];
            }
        }
        else
        {
            Span<float> target = destination.Slice(rowOffset, pixels.Length * 3);
            int redIndex = channels == TensorChannels.Rgb ? 0 : 2;
            int blueIndex = channels == TensorChannels.Rgb ? 2 : 0;
            for (int x = 0; x < pixels.Length; x++)
            {
                Rgba32 pixel = pixels[x].ToRgba32();
                int i = x * 3;
                target[i + redIndex] = red[pixel.R];
                target[i + 1] = green[pixel.G];
                target[i + blueIndex] = blue[pixel.B];
            }
        }
    }

    private sealed class ResizeTensorBuffers<TPixel> : IDisposable where TPixel : unmanaged, IPixel<TPixel>
    {
        internal readonly TPixel[] Output;
        internal readonly Rgba32[] SourceRgba;
        internal readonly Vector4[] SourceVectors;
        internal readonly Vector4[] Accumulator;
        internal readonly Vector4[] Filtered;
        internal readonly int[] RowKeys;

        internal ResizeTensorBuffers(int width, int sourceWidth, int contentWidth, int cacheLength, int slots)
        {
            try
            {
                this.Output = ArrayPool<TPixel>.Shared.Rent(width);
                this.SourceRgba = ArrayPool<Rgba32>.Shared.Rent(sourceWidth);
                this.SourceVectors = ArrayPool<Vector4>.Shared.Rent(sourceWidth);
                this.Accumulator = ArrayPool<Vector4>.Shared.Rent(contentWidth);
                this.Filtered = ArrayPool<Vector4>.Shared.Rent(cacheLength);
                this.RowKeys = ArrayPool<int>.Shared.Rent(slots);
                this.RowKeys.AsSpan(0, slots).Fill(-1);
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (this.Output is not null) ArrayPool<TPixel>.Shared.Return(this.Output);
            if (this.SourceRgba is not null) ArrayPool<Rgba32>.Shared.Return(this.SourceRgba);
            if (this.SourceVectors is not null) ArrayPool<Vector4>.Shared.Return(this.SourceVectors);
            if (this.Accumulator is not null) ArrayPool<Vector4>.Shared.Return(this.Accumulator);
            if (this.Filtered is not null) ArrayPool<Vector4>.Shared.Return(this.Filtered);
            if (this.RowKeys is not null) ArrayPool<int>.Shared.Return(this.RowKeys);
        }
    }
}
