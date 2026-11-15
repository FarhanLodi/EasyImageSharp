using EasyImageSharp.Formats.Exr;
using EasyImageSharp.Formats.Hdr;

namespace EasyImageSharp;

/// <summary>Save helpers for the high dynamic range codecs: Radiance HDR and OpenEXR.</summary>
public static partial class ImageExtensions
{
    // ----- Radiance HDR -----

    /// <summary>
    /// Saves the image as a run-length encoded Radiance HDR (RGBE) file. <see cref="PixelFormats.RgbaVector"/>
    /// pixels are written as linear values; other pixel formats are taken to be sRGB and linearised.
    /// </summary>
    public static void SaveAsHdr(this Image image, Stream stream) => image.Save(stream, new HdrEncoder());

    /// <summary>Saves the image as Radiance HDR using the given encoder settings.</summary>
    public static void SaveAsHdr(this Image image, Stream stream, HdrEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a run-length encoded Radiance HDR file at <paramref name="path"/>.</summary>
    public static void SaveAsHdr(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsHdr(stream);
    }

    /// <summary>Saves the image as Radiance HDR at <paramref name="path"/> using the given encoder settings.</summary>
    public static void SaveAsHdr(this Image image, string path, HdrEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsHdr(stream, encoder);
    }

    /// <summary>Asynchronously saves the image as a run-length encoded Radiance HDR file.</summary>
    public static Task SaveAsHdrAsync(this Image image, Stream stream)
        => image.SaveAsync(stream, new HdrEncoder(), CancellationToken.None);

    /// <summary>Asynchronously saves the image as a run-length encoded Radiance HDR file, observing <paramref name="cancellationToken"/>.</summary>
    public static Task SaveAsHdrAsync(this Image image, Stream stream, CancellationToken cancellationToken)
        => image.SaveAsync(stream, new HdrEncoder(), cancellationToken);

    /// <summary>Asynchronously saves the image as Radiance HDR using the given encoder settings.</summary>
    public static Task SaveAsHdrAsync(this Image image, Stream stream, HdrEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    // ----- OpenEXR -----

    /// <summary>
    /// Saves the image as a ZIP-compressed, half-float OpenEXR file. <see cref="PixelFormats.RgbaVector"/> pixels
    /// are written as linear values; other pixel formats are taken to be sRGB and linearised.
    /// </summary>
    public static void SaveAsExr(this Image image, Stream stream) => image.Save(stream, new ExrEncoder());

    /// <summary>Saves the image as OpenEXR using the given encoder settings.</summary>
    public static void SaveAsExr(this Image image, Stream stream, ExrEncoder encoder) => image.Save(stream, encoder);

    /// <summary>Saves the image as a ZIP-compressed, half-float OpenEXR file at <paramref name="path"/>.</summary>
    public static void SaveAsExr(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsExr(stream);
    }

    /// <summary>Saves the image as OpenEXR at <paramref name="path"/> using the given encoder settings.</summary>
    public static void SaveAsExr(this Image image, string path, ExrEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsExr(stream, encoder);
    }

    /// <summary>Asynchronously saves the image as a ZIP-compressed, half-float OpenEXR file.</summary>
    public static Task SaveAsExrAsync(this Image image, Stream stream)
        => image.SaveAsync(stream, new ExrEncoder(), CancellationToken.None);

    /// <summary>Asynchronously saves the image as a ZIP-compressed, half-float OpenEXR file, observing <paramref name="cancellationToken"/>.</summary>
    public static Task SaveAsExrAsync(this Image image, Stream stream, CancellationToken cancellationToken)
        => image.SaveAsync(stream, new ExrEncoder(), cancellationToken);

    /// <summary>Asynchronously saves the image as OpenEXR using the given encoder settings.</summary>
    public static Task SaveAsExrAsync(this Image image, Stream stream, ExrEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);
}
