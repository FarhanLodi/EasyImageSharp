using EasyImageSharp.Formats.Qoi;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>The "Quite OK Image" format (qoiformat.org).</summary>
    public static ImageFormat Qoi => QoiHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class QoiHolder
    {
        public static readonly ImageFormat Value = new(
            "QOI", "image/qoi", new[] { "qoi" },
            static data => data.Length >= 4 && data[0] == (byte)'q' && data[1] == (byte)'o' && data[2] == (byte)'i' && data[3] == (byte)'f',
            static () => new QoiDecoder(), static () => new QoiEncoder());
    }
}
