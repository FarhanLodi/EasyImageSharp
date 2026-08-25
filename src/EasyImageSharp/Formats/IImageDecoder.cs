using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats;

/// <summary>Decodes a specific encoded image format into an <see cref="Image{TPixel}"/>.</summary>
public interface IImageDecoder
{
    /// <summary>Decodes the image, honouring the limits in <paramref name="options"/>.</summary>
    Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>;

    /// <summary>Reads header-level information without decoding the pixel data. Never subject to size limits.</summary>
    ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options);
}

/// <summary>Encodes an <see cref="Image{TPixel}"/> into a specific image format.</summary>
public interface IImageEncoder
{
    void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>;
}

/// <summary>Convenience overloads that use <see cref="DecoderOptions.Default"/>.</summary>
public static class ImageDecoderExtensions
{
    public static Image<TPixel> Decode<TPixel>(this IImageDecoder decoder, ReadOnlySpan<byte> data)
        where TPixel : unmanaged, IPixel<TPixel>
        => decoder.Decode<TPixel>(data, DecoderOptions.Default);

    public static ImageInfo Identify(this IImageDecoder decoder, ReadOnlySpan<byte> data)
        => decoder.Identify(data, DecoderOptions.Default);
}

/// <summary>
/// Shared guard that turns the framework exceptions a span-based parser raises on truncated or
/// inconsistent input into the documented <see cref="InvalidImageContentException"/>.
/// </summary>
internal static class DecoderGuard
{
    /// <summary>True for exception types that only occur when the input violates the format's own invariants.</summary>
    /// <remarks>
    /// Decoders only ever parse in-memory buffers, so an <see cref="IOException"/> can only originate from the
    /// deflate implementation, which reports some corrupt streams through its internal <c>ZLibException</c>
    /// (an <see cref="IOException"/>) rather than <see cref="System.IO.InvalidDataException"/>.
    /// </remarks>
    public static bool IsMalformedInputSymptom(Exception ex)
        => ex is IndexOutOfRangeException
            or ArgumentException
            or ArithmeticException
            or System.IO.InvalidDataException
            or IOException;

    public static InvalidImageContentException Wrap(string formatName, Exception inner)
        => new($"Malformed {formatName} data: {inner.Message}", inner);
}
