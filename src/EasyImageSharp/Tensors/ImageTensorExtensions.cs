using System.Buffers;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Tensors;

/// <summary>
/// Zero-dependency bridges between images and the float tensors consumed and produced by
/// neural networks (ONNX Runtime, TorchSharp, etc.). No inference dependency is taken here;
/// pair these with any runtime.
/// </summary>
public static class ImageTensorExtensions
{
    /// <summary>
    /// Extracts a planar CHW float tensor (shape [3, height, width], RGB channel order) from the
    /// root frame. Values are scaled to 0-1 and then normalized as (value - mean[c]) / std[c].
    /// Pass empty spans to skip normalization (mean 0, std 1).
    /// </summary>
    public static float[] ToChwTensor<TPixel>(
        this Image<TPixel> image, ReadOnlySpan<float> channelMean = default, ReadOnlySpan<float> channelStd = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);

        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 3));
        int planeSize = width * height;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        // Rows are independent; each batch keeps its own scratch. The channel is gathered and scaled with
        // the same arithmetic as the scalar formulation, eight values at a time.
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            Rgba32[] pixels = ArrayPool<Rgba32>.Shared.Rent(width);
            float[] channel = ArrayPool<float>.Shared.Rent(width);
            try
            {
                Span<Rgba32> row = pixels.AsSpan(0, width);
                Span<float> values = channel.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
                    int offset = y * width;
                    PixelOps.ExtractChannel(row, 0, values);
                    PixelOps.Normalize(values, tensor.AsSpan(offset, width), mr, sr);
                    PixelOps.ExtractChannel(row, 1, values);
                    PixelOps.Normalize(values, tensor.AsSpan(planeSize + offset, width), mg, sg);
                    PixelOps.ExtractChannel(row, 2, values);
                    PixelOps.Normalize(values, tensor.AsSpan((2 * planeSize) + offset, width), mb, sb);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(channel);
                ArrayPool<Rgba32>.Shared.Return(pixels);
            }
        });

        return tensor;
    }

    /// <summary>
    /// Extracts an interleaved HWC float tensor (shape [height, width, 3], RGB order),
    /// normalized like <see cref="ToChwTensor{TPixel}"/>.
    /// </summary>
    public static float[] ToHwcTensor<TPixel>(
        this Image<TPixel> image, ReadOnlySpan<float> channelMean = default, ReadOnlySpan<float> channelStd = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);

        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 3));
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            Rgba32[] pixels = ArrayPool<Rgba32>.Shared.Rent(width);
            try
            {
                Span<Rgba32> row = pixels.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
                    int offset = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 p = row[x];
                        int i = offset + (x * 3);
                        tensor[i] = ((p.R / 255f) - mr) / sr;
                        tensor[i + 1] = ((p.G / 255f) - mg) / sg;
                        tensor[i + 2] = ((p.B / 255f) - mb) / sb;
                    }
                }
            }
            finally
            {
                ArrayPool<Rgba32>.Shared.Return(pixels);
            }
        });

        return tensor;
    }

    /// <summary>
    /// Extracts a single-channel luminance tensor (shape [height, width]), scaled to 0-1 and
    /// normalized as (value - mean) / std.
    /// </summary>
    public static float[] ToGrayscaleTensor<TPixel>(this Image<TPixel> image, float mean = 0f, float std = 1f)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 1));
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            byte[] luminance = ArrayPool<byte>.Shared.Rent(width);
            float[] widened = ArrayPool<float>.Shared.Rent(width);
            try
            {
                Span<byte> samples = luminance.AsSpan(0, width);
                Span<float> values = widened.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelOps.ToLuminance<TPixel>(frame.GetRowSpan(y), samples);
                    for (int x = 0; x < width; x++)
                    {
                        values[x] = samples[x];
                    }

                    PixelOps.Normalize(values, tensor.AsSpan(y * width, width), mean, std);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(widened);
                ArrayPool<byte>.Shared.Return(luminance);
            }
        });

        return tensor;
    }

    private static (float R, float G, float B) ReadTriplet(ReadOnlySpan<float> values, float fallback)
        => TensorImage.ReadTripletInternal(values, fallback);

    /// <summary>
    /// A single float[] can hold at most <see cref="Array.MaxLength"/> elements (~2.1 billion), so
    /// tensors are limited to roughly 715 megapixels for 3 channels. Larger images must be tiled.
    /// </summary>
    private static int CheckedTensorLength(int width, int height, int channels)
    {
        long length = (long)width * height * channels;
        if (length > Array.MaxLength)
        {
            throw new NotSupportedException(
                $"A {width}x{height} image with {channels} channels needs {length:N0} floats, more than a single array can hold "
                + $"({Array.MaxLength:N0}). Tile the image before converting it to a tensor.");
        }

        return (int)length;
    }
}

/// <summary>Builds images from model output tensors (super-resolution, denoising, segmentation masks).</summary>
public static class TensorImage
{
    /// <summary>
    /// Creates an image from a planar CHW float tensor (shape [3, height, width], RGB order).
    /// Values are denormalized as ((value * std[c]) + mean[c]) * 255 and clamped.
    /// </summary>
    public static Image<TPixel> FromChwTensor<TPixel>(
        ReadOnlySpan<float> tensor,
        int width,
        int height,
        ReadOnlySpan<float> channelMean = default,
        ReadOnlySpan<float> channelStd = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        int planeSize = width * height;
        if (tensor.Length < 3 * planeSize)
        {
            throw new ArgumentException(
                $"Tensor holds {tensor.Length} values but [3, {height}, {width}] requires {3 * planeSize}.", nameof(tensor));
        }

        (float mr, float mg, float mb) = ReadTripletInternal(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTripletInternal(channelStd, 1f);

        var image = new Image<TPixel>(width, height);
        var row = new Rgba32[width];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba32(
                    Denormalize(tensor[offset + x], mr, sr),
                    Denormalize(tensor[planeSize + offset + x], mg, sg),
                    Denormalize(tensor[(2 * planeSize) + offset + x], mb, sb));
            }

            PixelOps.FromRgba32<TPixel>(row, image.Frames.RootFrame.GetRowSpan(y));
        }

        return image;
    }

    /// <summary>
    /// Creates a grayscale image from a single-channel tensor (shape [height, width]);
    /// useful for visualizing model masks and heatmaps.
    /// </summary>
    public static Image<TPixel> FromGrayscaleTensor<TPixel>(
        ReadOnlySpan<float> tensor, int width, int height, float mean = 0f, float std = 1f)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        if (tensor.Length < width * height)
        {
            throw new ArgumentException(
                $"Tensor holds {tensor.Length} values but [{height}, {width}] requires {width * height}.", nameof(tensor));
        }

        var image = new Image<TPixel>(width, height);
        var row = new Rgba32[width];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                byte v = Denormalize(tensor[offset + x], mean, std);
                row[x] = new Rgba32(v, v, v);
            }

            PixelOps.FromRgba32<TPixel>(row, image.Frames.RootFrame.GetRowSpan(y));
        }

        return image;
    }

    private static byte Denormalize(float value, float mean, float std)
        => (byte)Math.Clamp((int)((((value * std) + mean) * 255f) + 0.5f), 0, 255);

    internal static (float R, float G, float B) ReadTripletInternal(ReadOnlySpan<float> values, float fallback)
        => values.IsEmpty
            ? (fallback, fallback, fallback)
            : values.Length >= 3
                ? (values[0], values[1], values[2])
                : (values[0], values[0], values[0]);
}
