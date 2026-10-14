using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>
/// The non-generic image base type, and the static entry points for loading and identifying images.
/// </summary>
/// <remarks>
/// Images are not thread-safe for concurrent mutation: any number of threads may read one image, but
/// a write must not overlap with any other access to that image. Working on distinct images in
/// parallel - decoding, encoding, cloning, processing - is safe.
/// </remarks>
public abstract partial class Image : IDisposable
{
    internal Image(ImageMetadata? metadata) => this.Metadata = metadata ?? new ImageMetadata();

    /// <summary>The width of the image in pixels.</summary>
    public abstract int Width { get; }

    /// <summary>The height of the image in pixels.</summary>
    public abstract int Height { get; }

    /// <summary>The size of the image in pixels.</summary>
    public Size Size => new(this.Width, this.Height);

    /// <summary>
    /// The image-level metadata: resolution, EXIF/ICC/XMP profiles and format-specific containers. Populated by
    /// decoders, written by encoders, and deep-copied by <c>Clone</c>/<c>CloneAs</c>.
    /// </summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Reads the pixel at the given coordinates as <see cref="Rgba32"/>, regardless of pixel format.</summary>
    internal abstract Rgba32 GetPixelRgba32(int x, int y);

    internal abstract void AcceptEncoder(IImageEncoder encoder, Stream stream);

    /// <summary>
    /// Encodes the image with <paramref name="encoder"/> and returns the result as a base64 string.
    /// </summary>
    /// <param name="encoder">The encoder that produces the bytes to encode.</param>
    /// <returns>The encoded image as base64, without a data URI prefix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public string ToBase64String(IImageEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        using var buffer = new MemoryStream();
        this.AcceptEncoder(encoder, buffer);
        return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Releases the image. Every later operation that touches its pixels throws
    /// <see cref="ObjectDisposedException"/>; memory the caller wrapped is not freed.
    /// </summary>
    public abstract void Dispose();

    // ----- Loading -----

    /// <summary>Loads an image from encoded bytes, decoding into the requested pixel format.</summary>
    public static Image<TPixel> Load<TPixel>(ReadOnlySpan<byte> data)
        where TPixel : unmanaged, IPixel<TPixel>
        => Load<TPixel>(data, DecoderOptions.Default);

    /// <summary>Loads an image from encoded bytes, honouring the limits in <paramref name="options"/>.</summary>
    public static Image<TPixel> Load<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        return ImageFormatDetector.DetectOrThrow(data, options).CreateDecoder().Decode<TPixel>(data, options);
    }

    public static Image<TPixel> Load<TPixel>(Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
        => Load<TPixel>(stream, DecoderOptions.Default);

    public static Image<TPixel> Load<TPixel>(Stream stream, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        => Load<TPixel>(BoundedImageInput.Read(stream, options).Span, options);

    public static Image<TPixel> Load<TPixel>(string path)
        where TPixel : unmanaged, IPixel<TPixel>
        => Load<TPixel>(path, DecoderOptions.Default);

    public static Image<TPixel> Load<TPixel>(string path, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using FileStream stream = File.OpenRead(path);
        return Load<TPixel>(stream, options);
    }

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(Stream stream, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
        => await LoadAsync<TPixel>(stream, DecoderOptions.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(Stream stream, DecoderOptions options, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions, cancellationToken);
        ReadOnlyMemory<byte> bytes = await BoundedImageInput.ReadAsync(stream, options, cancellationToken).ConfigureAwait(false);
        OperationExecution.Checkpoint();
        return Load<TPixel>(bytes.Span, options);
    }

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(string path, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
        => await LoadAsync<TPixel>(path, DecoderOptions.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(string path, DecoderOptions options, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await LoadAsync<TPixel>(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads an image from encoded bytes using <see cref="Rgba32"/> as the pixel format.</summary>
    public static Image Load(ReadOnlySpan<byte> data) => Load<Rgba32>(data);

    public static Image Load(ReadOnlySpan<byte> data, DecoderOptions options) => Load<Rgba32>(data, options);

    public static Image Load(Stream stream) => Load<Rgba32>(stream);

    public static Image Load(Stream stream, DecoderOptions options) => Load<Rgba32>(stream, options);

    public static Image Load(string path) => Load<Rgba32>(path);

    public static Image Load(string path, DecoderOptions options) => Load<Rgba32>(path, options);

    public static async Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        => await LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);

    public static async Task<Image> LoadAsync(Stream stream, DecoderOptions options, CancellationToken cancellationToken = default)
        => await LoadAsync<Rgba32>(stream, options, cancellationToken).ConfigureAwait(false);

    public static async Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default)
        => await LoadAsync<Rgba32>(path, cancellationToken).ConfigureAwait(false);

    public static async Task<Image> LoadAsync(string path, DecoderOptions options, CancellationToken cancellationToken = default)
        => await LoadAsync<Rgba32>(path, options, cancellationToken).ConfigureAwait(false);

    // ----- Raw pixel data -----

    /// <summary>Wraps raw pixel values (row-major, top-left origin) in a new image.</summary>
    public static Image<TPixel> LoadPixelData<TPixel>(ReadOnlySpan<TPixel> data, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        if (data.Length < (long)width * height)
        {
            throw new ArgumentException(
                $"The pixel buffer holds {data.Length} pixels but {width}x{height} requires {(long)width * height}.", nameof(data));
        }

        var image = new Image<TPixel>(width, height);
        data[..(width * height)].CopyTo(image.Frames.RootFrame.PixelSpan);
        return image;
    }

    /// <summary>Reinterprets raw bytes as pixel values and wraps them in a new image.</summary>
    public static Image<TPixel> LoadPixelData<TPixel>(ReadOnlySpan<byte> data, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
        => LoadPixelData(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, TPixel>(data), width, height);

    // ----- Identification -----

    /// <summary>Reads image header information without decoding pixel data. Size limits do not apply.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data)
        => Identify(data, DecoderOptions.Default);

    public static ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        return ImageFormatDetector.DetectOrThrow(data, options).CreateDecoder().Identify(data, options);
    }

    public static ImageInfo Identify(Stream stream) => Identify(stream, DecoderOptions.Default);

    public static ImageInfo Identify(Stream stream, DecoderOptions options) => Identify(BoundedImageInput.Read(stream, options).Span, options);

    public static ImageInfo Identify(string path) => Identify(path, DecoderOptions.Default);

    public static ImageInfo Identify(string path, DecoderOptions options)
    {
        using FileStream stream = File.OpenRead(path);
        return Identify(stream, options);
    }

    public static async Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default)
        => await IdentifyAsync(stream, DecoderOptions.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<ImageInfo> IdentifyAsync(Stream stream, DecoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions, cancellationToken);
        ReadOnlyMemory<byte> bytes = await BoundedImageInput.ReadAsync(stream, options, cancellationToken).ConfigureAwait(false);
        return Identify(bytes.Span, options);
    }

    public static async Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
        => await IdentifyAsync(path, DecoderOptions.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<ImageInfo> IdentifyAsync(string path, DecoderOptions options, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await IdentifyAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Detects the format of encoded image bytes from their magic numbers.</summary>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> data) => ImageFormatDetector.DetectOrThrow(data);

}
