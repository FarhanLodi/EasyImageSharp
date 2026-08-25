using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;

namespace EasyImageSharp;

/// <summary>Save/encode helpers for images.</summary>
public static partial class ImageExtensions
{
    /// <summary>Saves the image to a file, choosing the encoder from the file extension.</summary>
    public static void Save(this Image image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        using FileStream stream = File.Create(path);
        image.AcceptEncoder(ImageFormatDetector.FormatForPath(path).CreateEncoder(), stream);
    }

    public static void Save(this Image image, Stream stream, IImageEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(encoder);
        image.AcceptEncoder(encoder, stream);
    }

    public static async Task SaveAsync(this Image image, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.Save(path), cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveAsync(this Image image, Stream stream, IImageEncoder encoder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => image.Save(stream, encoder), cancellationToken).ConfigureAwait(false);
    }

    // ----- PNG -----

    public static void SaveAsPng(this Image image, Stream stream) => image.Save(stream, new PngEncoder());

    public static void SaveAsPng(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsPng(stream);
    }

    public static Task SaveAsPngAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new PngEncoder(), cancellationToken);

    // ----- JPEG -----

    public static void SaveAsJpeg(this Image image, Stream stream) => image.Save(stream, new JpegEncoder());

    public static void SaveAsJpeg(this Image image, Stream stream, int quality)
        => image.Save(stream, new JpegEncoder { Quality = quality });

    public static void SaveAsJpeg(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsJpeg(stream);
    }

    public static Task SaveAsJpegAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new JpegEncoder(), cancellationToken);

    // ----- BMP -----

    public static void SaveAsBmp(this Image image, Stream stream) => image.Save(stream, new BmpEncoder());

    public static void SaveAsBmp(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsBmp(stream);
    }

    // ----- TIFF -----

    public static void SaveAsTiff(this Image image, Stream stream) => image.Save(stream, new TiffEncoder());

    public static void SaveAsTiff(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsTiff(stream);
    }
}
