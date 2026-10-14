using System.Buffers;
using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Tensors;

/// <summary>
/// Zero-dependency bridges between images and the float tensors consumed and produced by
/// neural networks (ONNX Runtime, TorchSharp, etc.). No inference dependency is taken here;
/// pair these with any runtime.
/// </summary>
public static partial class ImageTensorExtensions
{
    /// <summary>
    /// Writes root-frame samples into caller-owned storage without allocating a tensor or scratch arrays.
    /// Samples are normalized as (value - mean) / std, with normalization in output channel order.
    /// Mean/std may be empty (zero/one), one value (broadcast), or one value per output channel.
    /// Eight-bit grayscale uses rounded BT.709 luminance; built-in wider formats retain floating-point luminance.
    /// Invalid arguments are rejected before any output is written. Padding is left untouched.
    /// </summary>
    public static void WriteTensor<TPixel>(
        this Image<TPixel> image, Span<float> destination, TensorWriteOptions? options = null,
        ReadOnlySpan<float> channelMean = default, ReadOnlySpan<float> channelStd = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        using var execution = options?.ExecutionOptions is { } executionOptions ? OperationExecution.Enter(executionOptions) : null;
        int width = image.Width;
        int height = image.Height;
        (TensorLayout layout, TensorChannels channels, int offset, long rowStride, long planeStride) =
            ValidateTensorDestination(width, height, destination.Length, options, channelMean, channelStd);

        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);
        if (!PixelOps.IsHighPrecision<TPixel>())
        {
            WriteByteTensor(image, destination, layout, channels, offset, (int)rowStride, planeStride,
                mr, mg, mb, sr, sg, sb);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
            Span<TPixel> row = image.Frames.RootFrame.GetRowSpan(y);
            int rowOffset = (int)(offset + (y * rowStride));
            for (int x = 0; x < width; x++)
            {
                Vector4 value = PixelOps.IsHighPrecision<TPixel>()
                    ? row[x].ToScaledVector4() : row[x].ToRgba32().ToScaledVector4();
                if (channels == TensorChannels.Grayscale)
                {
                    float luma = PixelOps.IsHighPrecision<TPixel>()
                        ? (value.X * 0.2126f) + (value.Y * 0.7152f) + (value.Z * 0.0722f)
                        : L8.FromRgba32(row[x].ToRgba32()).PackedValue / 255f;
                    destination[rowOffset + x] = (luma - mr) / sr;
                    continue;
                }

                float first = channels == TensorChannels.Bgr ? value.Z : value.X;
                float third = channels == TensorChannels.Bgr ? value.X : value.Z;
                int i = rowOffset + (x * (layout == TensorLayout.Hwc ? 3 : 1));
                int step = layout == TensorLayout.Hwc ? 1 : (int)planeStride;
                destination[i] = (first - mr) / sr;
                destination[i + step] = (value.Y - mg) / sg;
                destination[i + (2 * step)] = (third - mb) / sb;
            }
        }
    }

    private static (TensorLayout Layout, TensorChannels Channels, int Offset, long RowStride, long PlaneStride)
        ValidateTensorDestination(int width, int height, int destinationLength, TensorWriteOptions? options,
            ReadOnlySpan<float> channelMean, ReadOnlySpan<float> channelStd)
    {
        TensorLayout layout = options?.Layout ?? TensorLayout.Chw;
        TensorChannels channels = options?.Channels ?? TensorChannels.Rgb;
        if (!Enum.IsDefined(layout) || !Enum.IsDefined(channels))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        int count = channels == TensorChannels.Grayscale ? 1 : 3;
        ValidateNormalization(channelMean, channelStd, count);
        long packedRow = (long)width * (layout == TensorLayout.Hwc ? count : 1);
        long rowStride = options?.RowStride ?? 0;
        long planeStride = options?.PlaneStride ?? 0;
        int offset = options?.DestinationOffset ?? 0;
        if (offset < 0 || rowStride < 0 || planeStride < 0 || (layout == TensorLayout.Hwc && planeStride != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (packedRow > destinationLength - (long)offset)
        {
            throw new ArgumentException("Destination cannot hold one image row at the requested offset.", "destination");
        }

        if (rowStride == 0)
        {
            rowStride = packedRow;
        }

        if (rowStride < packedRow)
        {
            throw new ArgumentException("Row stride overlaps image samples.", nameof(options));
        }

        long planeExtent = ((height - 1L) * rowStride) + packedRow;
        // Bound a single plane before multiplying its stride by the channel count.
        // This also prevents aggregate arithmetic overflow for extreme dimensions/strides.
        if (planeExtent > destinationLength - (long)offset)
        {
            throw new ArgumentException("Destination cannot hold one image plane at the requested offset and stride.", "destination");
        }

        if (planeStride == 0)
        {
            planeStride = rowStride * height;
        }

        if (layout == TensorLayout.Chw && planeStride < planeExtent)
        {
            throw new ArgumentException("Plane stride overlaps image samples.", nameof(options));
        }

        long required = offset + planeExtent + (layout == TensorLayout.Chw ? (count - 1L) * planeStride : 0);
        if (required > destinationLength)
        {
            throw new ArgumentException($"Destination requires at least {required} floats.", "destination");
        }

        return (layout, channels, offset, rowStride, planeStride);
    }

    private static void WriteByteTensor<TPixel>(
        Image<TPixel> image, Span<float> destination, TensorLayout layout, TensorChannels channels,
        int offset, int rowStride, long planeStride,
        float mr, float mg, float mb, float sr, float sg, float sb)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        // Normalization is evaluated once per possible byte value, retaining the same arithmetic
        // as the scalar reference while avoiding three divisions per pixel. Scratch stays on stack.
        Span<float> first = stackalloc float[256];
        FillTable(first, mr, sr);
        int width = image.Width;
        int height = image.Height;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        if (channels == TensorChannels.Grayscale)
        {
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                Span<TPixel> source = frame.GetRowSpan(y);
                Span<float> target = destination.Slice(offset + (y * rowStride), width);
                for (int x = 0; x < width; x++)
                {
                    target[x] = first[L8.FromRgba32(source[x].ToRgba32()).PackedValue];
                }
            }

            return;
        }

        Span<float> green = stackalloc float[256];
        Span<float> third = stackalloc float[256];
        FillTable(green, mg, sg);
        FillTable(third, mb, sb);
        ReadOnlySpan<float> red = channels == TensorChannels.Rgb ? first : third;
        ReadOnlySpan<float> blue = channels == TensorChannels.Rgb ? third : first;
        if (layout == TensorLayout.Chw)
        {
            int redOffset = channels == TensorChannels.Rgb ? offset : offset + (int)(2 * planeStride);
            int blueOffset = channels == TensorChannels.Rgb ? offset + (int)(2 * planeStride) : offset;
            int greenOffset = offset + (int)planeStride;
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                Span<TPixel> source = frame.GetRowSpan(y);
                int row = y * rowStride;
                Span<float> r = destination.Slice(redOffset + row, width);
                Span<float> g = destination.Slice(greenOffset + row, width);
                Span<float> b = destination.Slice(blueOffset + row, width);
                for (int x = 0; x < width; x++)
                {
                    Rgba32 pixel = source[x].ToRgba32();
                    r[x] = red[pixel.R];
                    g[x] = green[pixel.G];
                    b[x] = blue[pixel.B];
                }
            }
        }
        else
        {
            int redIndex = channels == TensorChannels.Rgb ? 0 : 2;
            int blueIndex = channels == TensorChannels.Rgb ? 2 : 0;
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                Span<TPixel> source = frame.GetRowSpan(y);
                Span<float> target = destination.Slice(offset + (y * rowStride), width * 3);
                for (int x = 0; x < width; x++)
                {
                    Rgba32 pixel = source[x].ToRgba32();
                    int i = x * 3;
                    target[i + redIndex] = red[pixel.R];
                    target[i + 1] = green[pixel.G];
                    target[i + blueIndex] = blue[pixel.B];
                }
            }
        }

        static void FillTable(Span<float> table, float mean, float std)
        {
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = ((i / 255f) - mean) / std;
            }
        }
    }

    internal static void ValidateNormalization(ReadOnlySpan<float> mean, ReadOnlySpan<float> std, int channels)
    {
        Validate(mean, channels, false, nameof(mean));
        Validate(std, channels, true, nameof(std));
        static void Validate(ReadOnlySpan<float> values, int channels, bool divisor, string name)
        {
            if (values.Length != 0 && values.Length != 1 && values.Length != channels)
            {
                throw new ArgumentException("Supply zero, one, or one value per channel.", name);
            }

            foreach (float value in values)
            {
                if (!float.IsFinite(value) || (divisor && value <= 0))
                {
                    throw new ArgumentOutOfRangeException(name, "Normalization must be finite and standard deviations positive.");
                }
            }
        }
    }

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
        ValidateNormalization(channelMean, channelStd, 3);
        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);

        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 3));
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            image.WriteTensor(tensor, channelMean: channelMean, channelStd: channelStd);
            return tensor;
        }
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
        ValidateNormalization(channelMean, channelStd, 3);
        (float mr, float mg, float mb) = ReadTriplet(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTriplet(channelStd, 1f);

        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 3));
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            image.WriteTensor(tensor, new TensorWriteOptions { Layout = TensorLayout.Hwc }, channelMean, channelStd);
            return tensor;
        }
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
        ValidateNormalization([mean], [std], 1);
        int width = image.Width;
        int height = image.Height;
        var tensor = GC.AllocateUninitializedArray<float>(CheckedTensorLength(width, height, 1));
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            image.WriteTensor(tensor, new TensorWriteOptions { Channels = TensorChannels.Grayscale }, [mean], [std]);
            return tensor;
        }
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
    /// Values are denormalized as (value * std[c]) + mean[c]. Integer formats clamp and
    /// quantize to their native range; floating-point formats preserve values outside 0-1.
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
        ulong required = (ulong)width * (ulong)height * 3;
        if ((ulong)tensor.Length < required)
        {
            throw new ArgumentException(
                $"Tensor holds {tensor.Length} values but [3, {height}, {width}] requires {required}.", nameof(tensor));
        }

        int planeSize = width * height;
        ImageTensorExtensions.ValidateNormalization(channelMean, channelStd, 3);
        (float mr, float mg, float mb) = ReadTripletInternal(channelMean, 0f);
        (float sr, float sg, float sb) = ReadTripletInternal(channelStd, 1f);

        var image = new Image<TPixel>(width, height);
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                Span<TPixel> output = image.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    output[x] = TPixel.FromScaledVector4(new Vector4(
                        (tensor[i] * sr) + mr, (tensor[planeSize + i] * sg) + mg,
                        (tensor[(2 * planeSize) + i] * sb) + mb, 1f));
                }
            }

            return image;
        }
        var row = new Rgba32[width];
        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
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
        long required = (long)width * height;
        if (tensor.Length < required)
        {
            throw new ArgumentException(
                $"Tensor holds {tensor.Length} values but [{height}, {width}] requires {required}.", nameof(tensor));
        }

        ImageTensorExtensions.ValidateNormalization([mean], [std], 1);
        var image = new Image<TPixel>(width, height);
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            for (int y = 0; y < height; y++)
            {
                OperationExecution.Checkpoint();
                Span<TPixel> output = image.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    float v = (tensor[(y * width) + x] * std) + mean;
                    output[x] = TPixel.FromScaledVector4(new Vector4(v, v, v, 1f));
                }
            }

            return image;
        }
        var row = new Rgba32[width];
        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
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
