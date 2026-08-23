namespace EasyImageSharp.Formats.Png;

/// <summary>The PNG interlace method (IHDR interlace field).</summary>
public enum PngInterlaceMethod : byte
{
    /// <summary>Scanlines are stored top to bottom.</summary>
    None = 0,

    /// <summary>Adam7: seven passes of increasing resolution so a partial download can show a coarse preview.</summary>
    Adam7 = 1,
}
