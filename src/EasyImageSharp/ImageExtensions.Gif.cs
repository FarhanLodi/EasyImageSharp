using EasyImageSharp.Formats.Gif;

namespace EasyImageSharp;

/// <summary>GIF save helpers.</summary>
public static partial class ImageExtensions
{
    /// <summary>Saves the image as GIF with default encoder settings (Wu quantization, global colour table).</summary>
    public static void SaveAsGif(this Image image, Stream stream) => image.Save(stream, new GifEncoder());

    /// <summary>Saves the image as GIF with the given encoder settings.</summary>
    public static void SaveAsGif(this Image image, Stream stream, GifEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a GIF file with default encoder settings.</summary>
    public static void SaveAsGif(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsGif(stream);
    }

    /// <summary>Saves the image as a GIF file with the given encoder settings.</summary>
    public static void SaveAsGif(this Image image, string path, GifEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsGif(stream, encoder);
    }

    /// <summary>Saves the image as GIF asynchronously with default encoder settings.</summary>
    public static Task SaveAsGifAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new GifEncoder(), cancellationToken);

    /// <summary>Saves the image as GIF asynchronously with the given encoder settings.</summary>
    public static Task SaveAsGifAsync(this Image image, Stream stream, GifEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    /// <summary>Saves the image as a GIF file asynchronously with default encoder settings.</summary>
    public static async Task SaveAsGifAsync(this Image image, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.SaveAsGif(path), cancellationToken).ConfigureAwait(false);
    }
}
