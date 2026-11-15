namespace EasyImageSharp.Avif;

/// <summary>AVIF save helpers backed by the optional native libavif runtime.</summary>
public static class AvifImageExtensions
{
    /// <summary>Saves the image as AVIF with default encoder settings.</summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded or has no AV1 encoder.</exception>
    public static void SaveAsAvif(this Image image, Stream stream) => image.Save(stream, new AvifEncoder());

    /// <summary>Saves the image as AVIF with the given encoder settings.</summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded or has no AV1 encoder.</exception>
    public static void SaveAsAvif(this Image image, Stream stream, AvifEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as an AVIF file with default encoder settings.</summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded or has no AV1 encoder.</exception>
    public static void SaveAsAvif(this Image image, string path) => image.SaveAsAvif(path, new AvifEncoder());

    /// <summary>
    /// Saves the image as an AVIF file with the given encoder settings. The file is written only after encoding
    /// succeeds, so a failed encode never leaves a truncated file behind.
    /// </summary>
    /// <exception cref="NotSupportedException">libavif cannot be loaded or has no AV1 encoder.</exception>
    public static void SaveAsAvif(this Image image, string path, AvifEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(encoder);
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Saves the image as AVIF asynchronously with default encoder settings.</summary>
    public static Task SaveAsAvifAsync(this Image image, Stream stream) => image.SaveAsAvifAsync(stream, new AvifEncoder(), CancellationToken.None);

    /// <summary>Saves the image as AVIF asynchronously with default encoder settings.</summary>
    public static Task SaveAsAvifAsync(this Image image, Stream stream, CancellationToken cancellationToken)
        => image.SaveAsAvifAsync(stream, new AvifEncoder(), cancellationToken);

    /// <summary>Saves the image as AVIF asynchronously with the given encoder settings.</summary>
    public static Task SaveAsAvifAsync(this Image image, Stream stream, AvifEncoder encoder, CancellationToken cancellationToken)
        => image.SaveAsync(stream, encoder, cancellationToken);

    /// <summary>Saves the image as an AVIF file asynchronously with default encoder settings.</summary>
    public static Task SaveAsAvifAsync(this Image image, string path) => image.SaveAsAvifAsync(path, new AvifEncoder(), CancellationToken.None);

    /// <summary>Saves the image as an AVIF file asynchronously with default encoder settings.</summary>
    public static Task SaveAsAvifAsync(this Image image, string path, CancellationToken cancellationToken)
        => image.SaveAsAvifAsync(path, new AvifEncoder(), cancellationToken);

    /// <summary>Saves the image as an AVIF file asynchronously with the given encoder settings.</summary>
    public static async Task SaveAsAvifAsync(this Image image, string path, AvifEncoder encoder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.SaveAsAvif(path, encoder), cancellationToken).ConfigureAwait(false);
    }
}
