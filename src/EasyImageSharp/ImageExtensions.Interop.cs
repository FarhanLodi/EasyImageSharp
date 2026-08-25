using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <content>
/// Entry points for callers that already hold raw pixel bytes - a native buffer, a bitmap from
/// another stack, a frame from a capture API. Such buffers usually pad each row out to an alignment
/// boundary, so these overloads take an explicit stride instead of assuming tightly packed rows.
/// </content>
public abstract partial class Image
{
    /// <summary>
    /// Copies raw pixel bytes with an explicit row stride into a new image, dropping the padding.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format the bytes are laid out in.</typeparam>
    /// <param name="data">The source bytes, one row every <paramref name="stride"/> bytes, top row first.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="stride">
    /// The distance in bytes between the start of one row and the start of the next. It must be at
    /// least <c>width * sizeof(TPixel)</c>; anything beyond that is padding and is ignored.
    /// </param>
    /// <returns>A new image owning its own tightly packed copy of the pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width, height or stride is out of range.</exception>
    /// <exception cref="ArgumentException">The buffer is too small for the given size and stride.</exception>
    public static Image<TPixel> LoadPixelData<TPixel>(ReadOnlyMemory<byte> data, int width, int height, int stride)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));

        int rowBytes = width * Unsafe.SizeOf<TPixel>();
        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride), stride, $"The stride must be at least {rowBytes} bytes for a {width} pixel wide row.");
        }

        long required = ((long)(height - 1) * stride) + rowBytes;
        if (data.Length < required)
        {
            throw new ArgumentException(
                $"The buffer holds {data.Length} bytes but {width}x{height} at a stride of {stride} requires {required}.",
                nameof(data));
        }

        var image = new Image<TPixel>(width, height);
        ReadOnlySpan<byte> source = data.Span;
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * stride, rowBytes).CopyTo(MemoryMarshal.AsBytes(image.Frames.RootFrame.GetRowSpan(y)));
        }

        return image;
    }

    /// <summary>
    /// Copies raw pixel bytes with an explicit row stride into a new <see cref="Rgba32"/> image,
    /// dropping the padding.
    /// </summary>
    /// <param name="data">The source bytes, one row every <paramref name="stride"/> bytes, top row first.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="stride">The distance in bytes between the start of one row and the start of the next.</param>
    /// <returns>A new image owning its own tightly packed copy of the pixels.</returns>
    public static Image<Rgba32> LoadPixelData(ReadOnlyMemory<byte> data, int width, int height, int stride)
        => LoadPixelData<Rgba32>(data, width, height, stride);
}

/// <content>Interop conveniences for moving pixels between an image and a caller-owned buffer.</content>
public static partial class ImageExtensions
{
    /// <summary>Saves the image to a stream in the given format, using that format's default encoder.</summary>
    /// <param name="image">The image to save.</param>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="format">The format to encode as.</param>
    /// <exception cref="NotSupportedException">The library cannot encode that format.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static void Save(this Image image, Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(format);
        image.Save(stream, format.CreateEncoder());
    }

    /// <summary>Saves the image to a stream in the given format, using that format's default encoder.</summary>
    /// <param name="image">The image to save.</param>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once the image has been written.</returns>
    /// <exception cref="NotSupportedException">The library cannot encode that format.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static async Task SaveAsync(
        this Image image, Stream stream, ImageFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(format);
        await image.SaveAsync(stream, format.CreateEncoder(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the image in the given format and returns the result as a base64 string.</summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="format">The format to encode as.</param>
    /// <returns>The encoded image as base64, without a data URI prefix.</returns>
    /// <exception cref="NotSupportedException">The library cannot encode that format.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static string ToBase64String(this Image image, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(format);
        return image.ToBase64String(format.CreateEncoder());
    }

    /// <summary>
    /// Copies the root frame's pixels into <paramref name="destination"/>, starting each row at a
    /// multiple of <paramref name="stride"/> bytes and leaving the padding between rows untouched.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    /// <param name="image">The image to read.</param>
    /// <param name="destination">The buffer to fill, typically one owned by native code.</param>
    /// <param name="stride">
    /// The distance in bytes between the start of one row and the start of the next; at least
    /// <c>Width * sizeof(TPixel)</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The stride is smaller than one row of pixels.</exception>
    /// <exception cref="ArgumentException">The destination is too small for the image at that stride.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static void CopyPixelDataTo<TPixel>(this Image<TPixel> image, Span<byte> destination, int stride)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Frames.RootFrame.CopyPixelDataTo(destination, stride);
    }

    /// <summary>
    /// Copies the frame's pixels into <paramref name="destination"/>, starting each row at a multiple
    /// of <paramref name="stride"/> bytes and leaving the padding between rows untouched.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    /// <param name="frame">The frame to read.</param>
    /// <param name="destination">The buffer to fill, typically one owned by native code.</param>
    /// <param name="stride">
    /// The distance in bytes between the start of one row and the start of the next; at least
    /// <c>Width * sizeof(TPixel)</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The stride is smaller than one row of pixels.</exception>
    /// <exception cref="ArgumentException">The destination is too small for the frame at that stride.</exception>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public static void CopyPixelDataTo<TPixel>(this ImageFrame<TPixel> frame, Span<byte> destination, int stride)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(frame);

        int rowBytes = frame.Width * Unsafe.SizeOf<TPixel>();
        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride), stride, $"The stride must be at least {rowBytes} bytes for a {frame.Width} pixel wide row.");
        }

        long required = ((long)(frame.Height - 1) * stride) + rowBytes;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} bytes but the frame needs {required} at a stride of {stride}.",
                nameof(destination));
        }

        for (int y = 0; y < frame.Height; y++)
        {
            MemoryMarshal.AsBytes(frame.GetRowSpan(y)).CopyTo(destination.Slice(y * stride, rowBytes));
        }
    }

    /// <summary>
    /// Returns the root frame's pixels as a new byte array in the memory layout of
    /// <typeparamref name="TPixel"/>, with tightly packed rows.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    /// <param name="image">The image to read.</param>
    /// <returns>A new array holding <c>Width * Height * sizeof(TPixel)</c> bytes.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public static byte[] GetPixelBytes<TPixel>(this Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        var bytes = new byte[(long)image.Width * image.Height * Unsafe.SizeOf<TPixel>()];
        image.CopyPixelDataTo(bytes);
        return bytes;
    }
}
