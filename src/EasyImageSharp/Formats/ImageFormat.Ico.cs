using EasyImageSharp.Formats.Ico;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>Windows icons (ICO) and cursors (CUR); every directory entry decodes to a frame.</summary>
    public static ImageFormat Ico => IcoHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class IcoHolder
    {
        public static readonly ImageFormat Value = new(
            "ICO", "image/x-icon", new[] { "ico", "cur" },
            static data => IcoDecoder.Matches(data),
            static () => new IcoDecoder(), static () => new IcoEncoder());
    }
}
