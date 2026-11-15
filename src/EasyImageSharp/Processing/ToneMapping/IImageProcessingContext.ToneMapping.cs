namespace EasyImageSharp.Processing;

/// <summary>High dynamic range tone mapping.</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Maps scene-linear light to display sRGB on every frame: exposure, then the tone curve, then the sRGB
    /// transfer function, with the result clamped to 0..1 and alpha left unchanged.
    /// </summary>
    /// <remarks>
    /// <see cref="PixelFormats.RgbaVector"/> pixels are read as linear light, which is how the Radiance HDR and
    /// OpenEXR decoders deliver them, and receive sRGB-encoded values afterwards. Every other pixel format is
    /// read as sRGB, linearised, mapped and re-encoded; such images hold no light above 1, so the operation is
    /// most useful on <see cref="PixelFormats.RgbaVector"/>. Negative values and NaN map to 0, positive infinity
    /// to the curve's maximum. The result does not depend on the degree of parallelism.
    /// </remarks>
    /// <param name="options">The tone curve, exposure and white point.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ToneMappingOptions.Exposure"/> is not finite, <see cref="ToneMappingOptions.WhitePoint"/> is
    /// not positive and finite, or <see cref="ToneMappingOptions.ToneMapper"/> is not a defined value.
    /// </exception>
    IImageProcessingContext ToneMap(ToneMappingOptions options)
        => throw new NotSupportedException("This processing context does not support tone mapping.");
}
