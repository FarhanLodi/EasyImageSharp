namespace EasyImageSharp.Processing;

/// <summary>
/// How source and backdrop colours are combined before alpha composition (the "blend function" of the
/// W3C Compositing and Blending specification).
/// </summary>
public enum PixelColorBlendingMode
{
    /// <summary>The source colour replaces the backdrop colour.</summary>
    Normal,

    /// <summary>Multiplies the two colours; the result is always at least as dark as either input.</summary>
    Multiply,

    /// <summary>Adds the two colours (linear dodge), clamped to white.</summary>
    Add,

    /// <summary>Subtracts the source from the backdrop, clamped to black.</summary>
    Subtract,

    /// <summary>Complements, multiplies and complements again; the result is always at least as light as either input.</summary>
    Screen,

    /// <summary>Selects the darker of the two colours per channel.</summary>
    Darken,

    /// <summary>Selects the lighter of the two colours per channel.</summary>
    Lighten,

    /// <summary>Multiplies or screens depending on the backdrop colour.</summary>
    Overlay,

    /// <summary>Multiplies or screens depending on the source colour.</summary>
    HardLight,

    /// <summary>Darkens or lightens depending on the source colour, similar to a diffused spotlight.</summary>
    SoftLight,

    /// <summary>Brightens the backdrop to reflect the source colour.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop to reflect the source colour.</summary>
    ColorBurn,

    /// <summary>The absolute difference between the two colours.</summary>
    Difference,

    /// <summary>Similar to <see cref="Difference"/> but with lower contrast.</summary>
    Exclusion,

    /// <summary>The hue of the source with the saturation and luminosity of the backdrop.</summary>
    Hue,

    /// <summary>The saturation of the source with the hue and luminosity of the backdrop.</summary>
    Saturation,

    /// <summary>The hue and saturation of the source with the luminosity of the backdrop.</summary>
    Color,

    /// <summary>The luminosity of the source with the hue and saturation of the backdrop.</summary>
    Luminosity,
}

/// <summary>The Porter-Duff alpha composition operators (W3C Compositing and Blending specification).</summary>
public enum PixelAlphaCompositionMode
{
    /// <summary>The source is placed over the backdrop (the default alpha blend).</summary>
    SrcOver,

    /// <summary>Only the source is kept.</summary>
    Src,

    /// <summary>The source is placed over the backdrop, but only where the backdrop exists.</summary>
    SrcAtop,

    /// <summary>The source is kept only where the backdrop exists.</summary>
    SrcIn,

    /// <summary>The source is kept only where the backdrop does not exist.</summary>
    SrcOut,

    /// <summary>Only the backdrop is kept.</summary>
    Dest,

    /// <summary>The backdrop is placed over the source.</summary>
    DestOver,

    /// <summary>The backdrop is placed over the source, but only where the source exists.</summary>
    DestAtop,

    /// <summary>The backdrop is kept only where the source exists.</summary>
    DestIn,

    /// <summary>The backdrop is kept only where the source does not exist.</summary>
    DestOut,

    /// <summary>Everything is cleared to transparent.</summary>
    Clear,

    /// <summary>The non-overlapping parts of the source and backdrop are kept.</summary>
    Xor,
}
