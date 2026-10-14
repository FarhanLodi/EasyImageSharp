using EasyImageSharp.Formats.Tiff;

namespace EasyImageSharp;

public abstract partial class Image
{
    /// <summary>Opens random-access TIFF/BigTIFF region decoding at the stream's current position. The default leaves the caller's stream open.</summary>
    /// <remarks>Requires a seekable source. Supports chunky unsigned 8/16-bit gray/RGB and None/LZW/Deflate/PackBits; other layouts are rejected. Offsets are relative to the initial stream position. Failed opening leaves stream ownership with the caller.</remarks>
    public static TiffRegionReader OpenTiffRegionReader(Stream stream, int pageIndex = 0, TiffRegionOptions? options = null,
        bool leaveOpen = true, CancellationToken cancellationToken = default)
        => new(stream, pageIndex, options ?? new TiffRegionOptions(), leaveOpen, cancellationToken);
}
