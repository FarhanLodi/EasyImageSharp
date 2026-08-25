namespace EasyImageSharp.Formats.Png;

/// <summary>Bits per sample (per channel, or per palette index) written to a PNG.</summary>
public enum PngBitDepth : byte
{
    /// <summary>1 bit per sample (grayscale and palette only).</summary>
    Bit1 = 1,

    /// <summary>2 bits per sample (grayscale and palette only).</summary>
    Bit2 = 2,

    /// <summary>4 bits per sample (grayscale and palette only).</summary>
    Bit4 = 4,

    /// <summary>8 bits per sample; valid for every colour type.</summary>
    Bit8 = 8,

    /// <summary>16 bits per sample (not for palette images). 8-bit source samples are widened to 16 bits.</summary>
    Bit16 = 16,
}
