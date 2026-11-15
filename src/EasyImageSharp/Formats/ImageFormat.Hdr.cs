using EasyImageSharp.Formats.Hdr;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>
    /// Radiance HDR (RGBE), the <c>#?RADIANCE</c> picture format: high dynamic range RGB or XYZ with a shared
    /// 8-bit exponent per pixel.
    /// </summary>
    public static ImageFormat Hdr => HdrHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class HdrHolder
    {
        public static readonly ImageFormat Value = new(
            "HDR", "image/vnd.radiance", new[] { "hdr", "pic", "rgbe", "xyze" },
            static data => HdrHeader.IsMatch(data),
            static () => new HdrDecoder(), static () => new HdrEncoder());
    }
}
