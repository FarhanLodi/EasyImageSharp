using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

public abstract partial class Image
{
    /// <summary>Buffers bounded TIFF input, then decodes pages lazily. The input stream remains open; dispose each yielded page.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(Stream stream)
        where TPixel : unmanaged, IPixel<TPixel> => LoadTiffPages<TPixel>(stream, DecoderOptions.Default);

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily using the specified policy.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(Stream stream, DecoderOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options ??= DecoderOptions.Default;
        ReadOnlyMemory<byte> bytes = BoundedImageInput.Read(stream, options);
        foreach (Image<TPixel> page in new TiffDecoder().DecodePages<TPixel>(bytes, options)) yield return page;
    }

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily. Dispose each yielded page.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(string path)
        where TPixel : unmanaged, IPixel<TPixel> => LoadTiffPages<TPixel>(path, DecoderOptions.Default);

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily using the specified policy.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(string path, DecoderOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using FileStream stream = File.OpenRead(path);
        foreach (Image<TPixel> page in LoadTiffPages<TPixel>(stream, options)) yield return page;
    }
}
