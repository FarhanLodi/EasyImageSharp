namespace EasyImageSharp.Web;

/// <summary>
/// The query-string commands the image middleware understands. Names are matched case-insensitively; each may
/// appear at most once (a repeated command is answered with 400). Any other query parameter is ignored and does not
/// affect the output or the cache key, but it is covered by the URL signature when signing is enabled.
/// </summary>
/// <remarks>
/// The commands run in a fixed order whatever their order in the URL: <see cref="AutoOrient"/>, <see cref="Rotate"/>,
/// <see cref="Flip"/>, the resize (<see cref="Width"/>, <see cref="Height"/>, <see cref="Mode"/>,
/// <see cref="Anchor"/>, <see cref="Sampler"/>), the background (<see cref="BackgroundColor"/>), then encoding
/// (<see cref="Format"/>, <see cref="Quality"/>).
/// </remarks>
public static class ImageCommandNames
{
    /// <summary><c>width</c>: the target width in pixels, 1 to <see cref="EasyImageSharpWebOptions.MaxWidth"/>. With only one side given, the other follows the aspect ratio.</summary>
    public const string Width = "width";

    /// <summary><c>height</c>: the target height in pixels, 1 to <see cref="EasyImageSharpWebOptions.MaxHeight"/>.</summary>
    public const string Height = "height";

    /// <summary>
    /// <c>mode</c>: how the image is fitted into the <c>width</c> × <c>height</c> box. <c>max</c> (the default) fits
    /// inside the box keeping the aspect ratio; <c>crop</c> covers the box and crops the overflow; <c>pad</c> fits
    /// inside and pads to the exact box; <c>boxpad</c> pads without ever scaling up; <c>min</c> covers the box without
    /// cropping (the result may be larger than the box); <c>stretch</c> fills the box exactly, ignoring the aspect ratio.
    /// </summary>
    public const string Mode = "mode";

    /// <summary>
    /// <c>anchor</c>: which part is kept by <c>crop</c> and where the image sits for <c>pad</c>/<c>boxpad</c>:
    /// <c>center</c> (the default), <c>top</c>, <c>bottom</c>, <c>left</c>, <c>right</c>, <c>topleft</c>,
    /// <c>topright</c>, <c>bottomleft</c> or <c>bottomright</c>.
    /// </summary>
    public const string Anchor = "anchor";

    /// <summary>
    /// <c>format</c>: the output format, <c>jpeg</c> (or <c>jpg</c>), <c>png</c>, <c>webp</c>, <c>gif</c>,
    /// <c>bmp</c>, <c>tiff</c> (or <c>tif</c>) or an added format, subject to
    /// <see cref="EasyImageSharpWebOptions.AllowedFormats"/>. <c>auto</c>, like omitting the command, keeps the source
    /// format unless format negotiation picks a better one the client accepts.
    /// </summary>
    public const string Format = "format";

    /// <summary><c>quality</c>: the encoder quality from 1 (smallest) to 100 (best), for JPEG, WebP and added formats that support it.</summary>
    public const string Quality = "quality";

    /// <summary>
    /// <c>bgcolor</c>: a CSS colour name or a hex colour (<c>RGB</c>, <c>RGBA</c>, <c>RRGGBB</c> or <c>RRGGBBAA</c>,
    /// without <c>#</c> or with it URL-encoded as <c>%23</c>). Transparent areas are composited over it, and the
    /// padding of <c>pad</c>/<c>boxpad</c> uses it.
    /// </summary>
    public const string BackgroundColor = "bgcolor";

    /// <summary><c>autoorient</c>: <c>true</c> or <c>false</c> (also <c>1</c>/<c>0</c>); whether to rotate the image upright from its EXIF orientation. Defaults to <see cref="EasyImageSharpWebOptions.AutoOrient"/>.</summary>
    public const string AutoOrient = "autoorient";

    /// <summary>
    /// <c>sampler</c>: the resampling filter for resizing: <c>bicubic</c> (the default), <c>lanczos3</c>,
    /// <c>bilinear</c>, <c>nearest</c>, <c>box</c>, <c>catmullrom</c> or <c>mitchell</c>.
    /// </summary>
    public const string Sampler = "sampler";

    /// <summary><c>rotate</c>: a clockwise lossless rotation of <c>0</c>, <c>90</c>, <c>180</c> or <c>270</c> degrees, applied after <c>autoorient</c>.</summary>
    public const string Rotate = "rotate";

    /// <summary><c>flip</c>: <c>h</c> (mirror left to right), <c>v</c> (upside down) or <c>hv</c> (both).</summary>
    public const string Flip = "flip";

    /// <summary>
    /// <c>expires</c>: for signed URLs, the Unix time in seconds (UTC) after which the URL is answered with 403. It is
    /// covered by the signature. Ignored when signing is disabled.
    /// </summary>
    public const string Expires = "expires";

    /// <summary>
    /// <c>sig</c>: the URL signature, base64url without padding, of the HMAC-SHA256 described on
    /// <see cref="ImageUrlSigner"/>. Ignored when signing is disabled.
    /// </summary>
    public const string Signature = "sig";
}
