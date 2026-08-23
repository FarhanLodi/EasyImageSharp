using EasyImageSharp.Formats.Jpeg;

namespace EasyImageSharp;

/// <summary>JPEG save helpers that take a configured <see cref="JpegEncoder"/>.</summary>
public static partial class ImageExtensions
{
    /// <summary>Saves the image as JPEG to <paramref name="stream"/> using the options of <paramref name="encoder"/>.</summary>
    public static void SaveAsJpeg(this Image image, Stream stream, JpegEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        image.Save(stream, encoder);
    }

    /// <summary>Saves the image as JPEG to the file at <paramref name="path"/> using the options of <paramref name="encoder"/>.</summary>
    public static void SaveAsJpeg(this Image image, string path, JpegEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(encoder);
        using FileStream stream = File.Create(path);
        image.Save(stream, encoder);
    }

    /// <summary>Saves the image as JPEG to the file at <paramref name="path"/> at the given quality (1..100).</summary>
    public static void SaveAsJpeg(this Image image, string path, int quality)
        => image.SaveAsJpeg(path, new JpegEncoder { Quality = quality });

    /// <summary>Asynchronously saves the image as JPEG to <paramref name="stream"/> using the options of <paramref name="encoder"/>.</summary>
    public static Task SaveAsJpegAsync(this Image image, Stream stream, JpegEncoder encoder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return image.SaveAsync(stream, encoder, cancellationToken);
    }
}
