using EasyImageSharp.Formats.Pbm;

namespace EasyImageSharp.Formats;

public sealed partial class ImageFormat
{
    /// <summary>The Netpbm family: PBM/PGM/PPM (plain P1-P3 and binary P4-P6) plus PAM (P7, decode only).</summary>
    public static ImageFormat Pbm => PbmHolder.Value;

    /// <summary>Nested holder so the instance is created on first use, independent of the static-initializer order across partial files.</summary>
    private static class PbmHolder
    {
        public static readonly ImageFormat Value = new(
            "PBM", "image/x-portable-anymap", new[] { "pbm", "pgm", "ppm", "pnm" },
            static data => data.Length >= 3 && data[0] == (byte)'P' && data[1] is >= (byte)'1' and <= (byte)'7'
                && (data[2] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C or (byte)'#'),
            static () => new PbmDecoder(), static () => new PbmEncoder());
    }
}
