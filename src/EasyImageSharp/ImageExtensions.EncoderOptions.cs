using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Png;

namespace EasyImageSharp;

/// <summary>Save helpers that take configured PNG and BMP encoders.</summary>
public static partial class ImageExtensions
{
    /// <summary>Saves the image as PNG with the given encoder settings.</summary>
    public static void SaveAsPng(this Image image, Stream stream, PngEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a PNG file with the given encoder settings.</summary>
    public static void SaveAsPng(this Image image, string path, PngEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsPng(stream, encoder);
    }

    /// <summary>Saves the image as PNG asynchronously with the given encoder settings.</summary>
    public static Task SaveAsPngAsync(this Image image, Stream stream, PngEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    /// <summary>Saves the image as BMP with the given encoder settings.</summary>
    public static void SaveAsBmp(this Image image, Stream stream, BmpEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a BMP file with the given encoder settings.</summary>
    public static void SaveAsBmp(this Image image, string path, BmpEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsBmp(stream, encoder);
    }

    /// <summary>Saves the image as BMP asynchronously with the given encoder settings.</summary>
    public static Task SaveAsBmpAsync(this Image image, Stream stream, BmpEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);
}
