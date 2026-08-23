using EasyImageSharp.Formats.Tga;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>
    /// Truevision TGA. The format has no magic number: files are recognised by a strict header consistency check
    /// (see <see cref="TgaHeader.IsPlausible"/>) or by the TGA 2.0 footer, so this format is tried last.
    /// </summary>
    public static ImageFormat Tga => TgaHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class TgaHolder
    {
        public static readonly ImageFormat Value = new(
            "TGA", "image/x-targa", new[] { "tga", "targa", "icb", "vda", "vst" },
            static data => TgaHeader.IsPlausible(data),
            static () => new TgaDecoder(), static () => new TgaEncoder());
    }
}
