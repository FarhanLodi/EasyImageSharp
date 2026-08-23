using EasyImageSharp.Formats.Webp;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>Google's WebP format (RFC 9649): lossy VP8, lossless VP8L, alpha and animation.</summary>
    public static ImageFormat Webp => WebpHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class WebpHolder
    {
        public static readonly ImageFormat Value = new(
            "WEBP", "image/webp", new[] { "webp" },
            static data => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8),
            static () => new WebpDecoder(), static () => new WebpEncoder());
    }
}
