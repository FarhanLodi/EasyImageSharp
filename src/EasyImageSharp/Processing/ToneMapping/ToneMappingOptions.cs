namespace EasyImageSharp.Processing;

/// <summary>The tone curve <see cref="IImageProcessingContext.ToneMap(ToneMappingOptions)"/> applies to linear light.</summary>
public enum ToneMapper
{
    /// <summary>Exposure only: the exposed linear value is clipped at 1.</summary>
    Clip = 0,

    /// <summary>
    /// Reinhard's global operator on luminance, <c>L / (1 + L)</c>, with the colour scaled by the same factor so
    /// hue is kept. Never reaches white.
    /// </summary>
    Reinhard = 1,

    /// <summary>
    /// Reinhard's extended operator, <c>L (1 + L / Lw^2) / (1 + L)</c>, which maps the white point <c>Lw</c> to
    /// white. <see cref="ToneMappingOptions.WhitePoint"/> sets <c>Lw</c>; when it is null the largest exposed
    /// luminance of each frame is used, so the brightest pixel just reaches white.
    /// </summary>
    ReinhardExtended = 2,

    /// <summary>
    /// Narkowicz's rational fit of the ACES filmic curve, <c>x (2.51 x + 0.03) / (x (2.43 x + 0.59) + 0.14)</c>,
    /// per channel.
    /// </summary>
    AcesFilmic = 3,

    /// <summary>
    /// Hable's filmic curve from Uncharted 2 (A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30) per
    /// channel, normalised so the white point (<see cref="ToneMappingOptions.WhitePoint"/>, default 11.2) maps
    /// to white. Hable's published shader also pre-multiplies by 2, which is <see cref="ToneMappingOptions.Exposure"/> = 1.
    /// </summary>
    Hable = 4,
}

/// <summary>Settings for <see cref="IImageProcessingContext.ToneMap(ToneMappingOptions)"/>.</summary>
public sealed class ToneMappingOptions
{
    /// <summary>The tone curve. Defaults to <see cref="ToneMapper.AcesFilmic"/>.</summary>
    public ToneMapper ToneMapper { get; init; } = ToneMapper.AcesFilmic;

    /// <summary>Exposure adjustment in stops, applied before the curve: the linear value is multiplied by <c>2^Exposure</c>. Defaults to 0.</summary>
    public float Exposure { get; init; }

    /// <summary>
    /// The linear value (after exposure) that maps to white, for <see cref="ToneMapper.ReinhardExtended"/> and
    /// <see cref="ToneMapper.Hable"/>; ignored by the other curves. <see langword="null"/> selects the curve's
    /// default. Must be positive and finite when set.
    /// </summary>
    public float? WhitePoint { get; init; }
}
