using EasyImageSharp.Formats.Ico;
using EasyImageSharp.Formats.Pbm;
using EasyImageSharp.Formats.Qoi;
using EasyImageSharp.Formats.Tga;

namespace EasyImageSharp;

/// <summary>Save helpers for the TGA, Netpbm, QOI and ICO/CUR codecs.</summary>
public static partial class ImageExtensions
{
    // ----- TGA -----

    /// <summary>Saves the image as TGA (run-length encoded, depth chosen from the pixel format).</summary>
    public static void SaveAsTga(this Image image, Stream stream) => image.Save(stream, new TgaEncoder());

    /// <summary>Saves the image as TGA using the given encoder settings.</summary>
    public static void SaveAsTga(this Image image, Stream stream, TgaEncoder encoder) => image.Save(stream, encoder);

    public static void SaveAsTga(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsTga(stream);
    }

    public static void SaveAsTga(this Image image, string path, TgaEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsTga(stream, encoder);
    }

    public static Task SaveAsTgaAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new TgaEncoder(), cancellationToken);

    public static Task SaveAsTgaAsync(this Image image, Stream stream, TgaEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    // ----- Netpbm (PBM/PGM/PPM) -----

    /// <summary>Saves the image in the binary Netpbm format matching its pixel type (P5 graymap for <see cref="PixelFormats.L8"/>, P6 pixmap otherwise).</summary>
    public static void SaveAsPbm(this Image image, Stream stream) => image.Save(stream, new PbmEncoder());

    /// <summary>Saves the image as PBM/PGM/PPM using the given encoder settings.</summary>
    public static void SaveAsPbm(this Image image, Stream stream, PbmEncoder encoder) => image.Save(stream, encoder);

    public static void SaveAsPbm(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsPbm(stream);
    }

    public static void SaveAsPbm(this Image image, string path, PbmEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsPbm(stream, encoder);
    }

    public static Task SaveAsPbmAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new PbmEncoder(), cancellationToken);

    public static Task SaveAsPbmAsync(this Image image, Stream stream, PbmEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    // ----- QOI -----

    /// <summary>Saves the image as QOI (channel count chosen from the pixel format).</summary>
    public static void SaveAsQoi(this Image image, Stream stream) => image.Save(stream, new QoiEncoder());

    /// <summary>Saves the image as QOI using the given encoder settings.</summary>
    public static void SaveAsQoi(this Image image, Stream stream, QoiEncoder encoder) => image.Save(stream, encoder);

    public static void SaveAsQoi(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsQoi(stream);
    }

    public static void SaveAsQoi(this Image image, string path, QoiEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsQoi(stream, encoder);
    }

    public static Task SaveAsQoiAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new QoiEncoder(), cancellationToken);

    public static Task SaveAsQoiAsync(this Image image, Stream stream, QoiEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);

    // ----- ICO / CUR -----

    /// <summary>Saves every frame of the image as an entry of a Windows icon.</summary>
    public static void SaveAsIco(this Image image, Stream stream) => image.Save(stream, new IcoEncoder());

    /// <summary>Saves the image as ICO or CUR using the given encoder settings.</summary>
    public static void SaveAsIco(this Image image, Stream stream, IcoEncoder encoder) => image.Save(stream, encoder);

    public static void SaveAsIco(this Image image, string path)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsIco(stream);
    }

    public static void SaveAsIco(this Image image, string path, IcoEncoder encoder)
    {
        using FileStream stream = File.Create(path);
        image.SaveAsIco(stream, encoder);
    }

    public static Task SaveAsIcoAsync(this Image image, Stream stream, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, new IcoEncoder(), cancellationToken);

    public static Task SaveAsIcoAsync(this Image image, Stream stream, IcoEncoder encoder, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, encoder, cancellationToken);
}
