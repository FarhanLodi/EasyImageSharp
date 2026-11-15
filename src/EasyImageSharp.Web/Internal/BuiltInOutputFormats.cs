using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Formats.Webp;

namespace EasyImageSharp.Web.Internal;

/// <summary>The output formats encoded by the core library itself.</summary>
internal static class BuiltInOutputFormats
{
    public static readonly IImageOutputFormat Jpeg = new Format(
        "jpeg", new[] { "jpg" }, "image/jpeg", supportsTransparency: false,
        static quality => quality is int q ? new JpegEncoder { Quality = q } : new JpegEncoder());

    public static readonly IImageOutputFormat Png = new Format(
        "png", Array.Empty<string>(), "image/png", supportsTransparency: true, static _ => new PngEncoder());

    // WebpFileFormat.Auto writes graphics (at most 256 colours) losslessly and photographs as lossy VP8.
    public static readonly IImageOutputFormat Webp = new Format(
        "webp", Array.Empty<string>(), "image/webp", supportsTransparency: true,
        static quality => quality is int q ? new WebpEncoder { Quality = q } : new WebpEncoder());

    public static readonly IImageOutputFormat Gif = new Format(
        "gif", Array.Empty<string>(), "image/gif", supportsTransparency: true, static _ => new GifEncoder());

    // Browser support for BMP alpha is inconsistent, so transparent areas are flattened.
    public static readonly IImageOutputFormat Bmp = new Format(
        "bmp", Array.Empty<string>(), "image/bmp", supportsTransparency: false, static _ => new BmpEncoder());

    public static readonly IImageOutputFormat Tiff = new Format(
        "tiff", new[] { "tif" }, "image/tiff", supportsTransparency: true, static _ => new TiffEncoder());

    public static IReadOnlyList<IImageOutputFormat> All { get; } = new[] { Jpeg, Png, Webp, Gif, Bmp, Tiff };

    /// <summary>The default of <see cref="EasyImageSharpWebOptions.AllowedFormats"/>.</summary>
    public static IReadOnlyList<string> DefaultAllowed { get; } = new[] { "jpeg", "png", "webp", "gif" };

    /// <summary>The default of <see cref="EasyImageSharpWebOptions.NegotiatedFormats"/>.</summary>
    public static IReadOnlyList<string> DefaultNegotiated { get; } = new[] { "webp" };

    private sealed class Format : IImageOutputFormat
    {
        private readonly Func<int?, IImageEncoder> factory;

        public Format(string name, string[] aliases, string mimeType, bool supportsTransparency, Func<int?, IImageEncoder> factory)
        {
            this.Name = name;
            this.Aliases = aliases;
            this.MimeType = mimeType;
            this.SupportsTransparency = supportsTransparency;
            this.factory = factory;
        }

        public string Name { get; }

        public IReadOnlyList<string> Aliases { get; }

        public string MimeType { get; }

        public bool SupportsTransparency { get; }

        public IImageEncoder CreateEncoder(int? quality) => this.factory(quality);

        public override string ToString() => this.Name;
    }
}
