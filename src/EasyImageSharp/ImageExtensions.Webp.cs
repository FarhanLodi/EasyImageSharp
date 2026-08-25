using EasyImageSharp.Formats.Webp;

namespace EasyImageSharp;

/// <summary>WebP save helpers.</summary>
public static partial class ImageExtensions
{
    /// <summary>Saves the image as WebP with default encoder settings.</summary>
    public static void SaveAsWebp(this Image image, Stream stream) => image.Save(stream, new WebpEncoder());

    /// <summary>Saves the image as WebP with the given encoder settings.</summary>
    public static void SaveAsWebp(this Image image, Stream stream, WebpEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a WebP file with default encoder settings.</summary>
    public static void SaveAsWebp(this Image image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        using FileStream stream = File.Create(path);
        image.SaveAsWebp(stream);
    }

    /// <summary>Saves the image as a WebP file with the given encoder settings.</summary>
    public static void SaveAsWebp(this Image image, string path, WebpEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(image);
        using FileStream stream = File.Create(path);
        image.SaveAsWebp(stream, encoder);
    }

    /// <summary>Saves the image as WebP asynchronously with default encoder settings.</summary>
    public static Task SaveAsWebpAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new WebpEncoder(), cancellationToken);

    /// <summary>Saves the image as WebP asynchronously with the given encoder settings.</summary>
    public static Task SaveAsWebpAsync(this Image image, Stream stream, WebpEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    /// <summary>Saves the image as a WebP file asynchronously with default encoder settings.</summary>
    public static async Task SaveAsWebpAsync(this Image image, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.SaveAsWebp(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the image as a WebP file asynchronously with the given encoder settings.</summary>
    public static async Task SaveAsWebpAsync(this Image image, string path, WebpEncoder encoder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.SaveAsWebp(path, encoder), cancellationToken).ConfigureAwait(false);
    }
}
