using EasyImageSharp.Formats.Exr;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>OpenEXR, the scene-linear high dynamic range format (half, float and uint channels).</summary>
    public static ImageFormat Exr => ExrHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class ExrHolder
    {
        public static readonly ImageFormat Value = new(
            "EXR", "image/x-exr", new[] { "exr" },
            static data => data.Length >= 4 && data[0] == 0x76 && data[1] == 0x2F && data[2] == 0x31 && data[3] == 0x01,
            static () => new ExrDecoder(), static () => new ExrEncoder());
    }
}
