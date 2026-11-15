using EasyImageSharp.Formats;

namespace EasyImageSharp.Web;

/// <summary>
/// An output format the middleware can encode to. JPEG, PNG, WebP, GIF, BMP and TIFF are built in; register
/// another (an AVIF or JPEG XL encoder from an add-on package, for example) with <c>AddOutputFormat</c>, allow it
/// in <see cref="EasyImageSharpWebOptions.AllowedFormats"/>, and list it in
/// <see cref="EasyImageSharpWebOptions.NegotiatedFormats"/> to serve it to browsers that accept it.
/// </summary>
/// <remarks>Implementations are singletons and must be thread-safe; <see cref="CreateEncoder"/> is called once per processed image.</remarks>
public interface IImageOutputFormat
{
    /// <summary>
    /// The canonical name, used by the <c>format</c> command and in the allow and negotiation lists: lowercase ASCII
    /// letters and digits, for example <c>avif</c>. A registered format replaces a built-in one of the same name.
    /// </summary>
    string Name { get; }

    /// <summary>Other values the <c>format</c> command accepts for this format, for example <c>jpg</c> for <c>jpeg</c>.</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>The media type sent as <c>Content-Type</c> and matched against the <c>Accept</c> header, for example <c>image/avif</c>.</summary>
    string MimeType { get; }

    /// <summary>
    /// <see langword="true"/> when the format stores an alpha channel. When <see langword="false"/>, transparent
    /// areas are flattened onto the <c>bgcolor</c> command or <see cref="EasyImageSharpWebOptions.DefaultBackgroundColor"/>
    /// before encoding.
    /// </summary>
    bool SupportsTransparency { get; }

    /// <summary>Creates the encoder for one image.</summary>
    /// <param name="quality">
    /// The requested quality from 1 (smallest) to 100 (best), or <see langword="null"/> for the format's default.
    /// Formats without a quality setting ignore it.
    /// </param>
    /// <returns>The encoder.</returns>
    IImageEncoder CreateEncoder(int? quality);
}
