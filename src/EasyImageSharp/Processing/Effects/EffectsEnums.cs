namespace EasyImageSharp.Processing;

/// <summary>The luma coefficients used when converting to grayscale.</summary>
public enum GrayscaleMode
{
    /// <summary>ITU-R BT.709 (HDTV) coefficients: 0.2126 R + 0.7152 G + 0.0722 B.</summary>
    Bt709,

    /// <summary>ITU-R BT.601 (SDTV) coefficients: 0.299 R + 0.587 G + 0.114 B.</summary>
    Bt601,
}

/// <summary>Colour vision deficiencies that can be simulated with <c>ColorBlindness</c>.</summary>
public enum ColorBlindnessMode
{
    /// <summary>Partial colour desensitivity.</summary>
    Achromatomaly,

    /// <summary>Complete colour desensitivity (monochrome vision).</summary>
    Achromatopsia,

    /// <summary>Green-weak vision.</summary>
    Deuteranomaly,

    /// <summary>Green-blind vision.</summary>
    Deuteranopia,

    /// <summary>Red-weak vision.</summary>
    Protanomaly,

    /// <summary>Red-blind vision.</summary>
    Protanopia,

    /// <summary>Blue-weak vision.</summary>
    Tritanomaly,

    /// <summary>Blue-blind vision.</summary>
    Tritanopia,
}

/// <summary>Which per-pixel quantity a binary threshold compares against.</summary>
public enum BinaryThresholdMode
{
    /// <summary>The BT.709 luminance of the pixel (0-1).</summary>
    Luminance,

    /// <summary>The saturation of the pixel in the HSL colour space (0-1).</summary>
    Saturation,

    /// <summary>
    /// The maximum chroma of the pixel: the larger of the absolute Cb and Cr components (BT.601 YCbCr),
    /// scaled so that a fully saturated primary approaches 1.
    /// </summary>
    MaxChroma,
}

/// <summary>Histogram equalization strategies.</summary>
public enum HistogramEqualizationMethod
{
    /// <summary>A single mapping computed from the whole image's luminance histogram.</summary>
    Global,

    /// <summary>
    /// Contrast-limited adaptive histogram equalization (CLAHE): the image is divided into tiles, each tile
    /// gets its own (optionally clipped) mapping and every pixel is remapped by bilinear interpolation between
    /// the mappings of the four nearest tiles.
    /// </summary>
    AdaptiveTileInterpolation,

    /// <summary>
    /// Adaptive equalization with a window sliding over every pixel: each pixel is remapped by the histogram of
    /// its own neighbourhood. Slower but free of tile artefacts.
    /// </summary>
    AdaptiveSlidingWindow,
}
