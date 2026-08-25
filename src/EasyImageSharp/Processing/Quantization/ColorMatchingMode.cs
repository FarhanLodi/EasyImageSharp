namespace EasyImageSharp.Processing.Quantization;

/// <summary>Controls how source colours are matched to palette entries during quantization.</summary>
public enum ColorMatchingMode
{
    /// <summary>
    /// Every colour is matched to its true nearest palette entry (Euclidean distance in RGBA). Lookups are
    /// accelerated by a per-bucket candidate cache, so this is only marginally slower than <see cref="Coarse"/>.
    /// </summary>
    Exact,

    /// <summary>
    /// Colours are bucketed to 5/6/5 bits of red/green/blue and every colour in a bucket receives the palette
    /// entry nearest to the bucket centre. Slightly less accurate at bucket boundaries; the difference is
    /// invisible when dithering is enabled.
    /// </summary>
    Coarse,
}
