namespace EasyImageSharp.JpegXl;

/// <summary>JPEG XL save helpers backed by the optional native libjxl runtime.</summary>
public static class JpegXlImageExtensions
{
    /// <summary>Saves the image as JPEG XL with default encoder settings (lossy, distance 1, effort 7).</summary>
    /// <exception cref="NotSupportedException">libjxl cannot be loaded.</exception>
    public static void SaveAsJxl(this Image image, Stream stream) => image.Save(stream, new JpegXlEncoder());

    /// <summary>Saves the image as JPEG XL with the given encoder settings.</summary>
    /// <exception cref="NotSupportedException">libjxl cannot be loaded.</exception>
    public static void SaveAsJxl(this Image image, Stream stream, JpegXlEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a JPEG XL file with default encoder settings.</summary>
    /// <exception cref="NotSupportedException">libjxl cannot be loaded.</exception>
    public static void SaveAsJxl(this Image image, string path) => image.SaveAsJxl(path, new JpegXlEncoder());

    /// <summary>
    /// Saves the image as a JPEG XL file with the given encoder settings. The file is written only after encoding
    /// succeeds, so a failed encode never leaves a truncated file behind.
    /// </summary>
    /// <exception cref="NotSupportedException">libjxl cannot be loaded.</exception>
    public static void SaveAsJxl(this Image image, string path, JpegXlEncoder encoder)
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

    /// <summary>Saves the image as JPEG XL asynchronously with default encoder settings.</summary>
    public static Task SaveAsJxlAsync(this Image image, Stream stream) => image.SaveAsJxlAsync(stream, new JpegXlEncoder(), CancellationToken.None);

    /// <summary>Saves the image as JPEG XL asynchronously with default encoder settings.</summary>
    public static Task SaveAsJxlAsync(this Image image, Stream stream, CancellationToken cancellationToken)
        => image.SaveAsJxlAsync(stream, new JpegXlEncoder(), cancellationToken);

    /// <summary>Saves the image as JPEG XL asynchronously with the given encoder settings.</summary>
    public static Task SaveAsJxlAsync(this Image image, Stream stream, JpegXlEncoder encoder, CancellationToken cancellationToken)
        => image.SaveAsync(stream, encoder, cancellationToken);

    /// <summary>Saves the image as a JPEG XL file asynchronously with default encoder settings.</summary>
    public static Task SaveAsJxlAsync(this Image image, string path) => image.SaveAsJxlAsync(path, new JpegXlEncoder(), CancellationToken.None);

    /// <summary>Saves the image as a JPEG XL file asynchronously with default encoder settings.</summary>
    public static Task SaveAsJxlAsync(this Image image, string path, CancellationToken cancellationToken)
        => image.SaveAsJxlAsync(path, new JpegXlEncoder(), cancellationToken);

    /// <summary>Saves the image as a JPEG XL file asynchronously with the given encoder settings.</summary>
    public static async Task SaveAsJxlAsync(this Image image, string path, JpegXlEncoder encoder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.SaveAsJxl(path, encoder), cancellationToken).ConfigureAwait(false);
    }
}
