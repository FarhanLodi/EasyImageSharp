namespace EasyImageSharp.ColorSpaces;

/// <summary>
/// What a decoder does with an embedded ICC colour profile. Set through
/// <see cref="Formats.DecoderOptions.ColorProfileHandling"/>.
/// </summary>
public enum ColorProfileHandling
{
    /// <summary>
    /// Pixels are returned exactly as encoded, in the device space the embedded profile describes, and the
    /// profile is kept on the image metadata. This is the default and the behaviour of every earlier release.
    /// </summary>
    Preserve = 0,

    /// <summary>
    /// Pixels are converted from an embedded RGB matrix/TRC profile (Display P3, Adobe RGB, ProPhoto and the
    /// like) to sRGB right after decoding, and the converted profile is removed from the image and frame
    /// metadata, because untagged pixels are sRGB by convention. Profiles the managed engine cannot apply are
    /// handled according to <see cref="Formats.DecoderOptions.UnsupportedColorProfileHandling"/>. The outcome is
    /// recorded in a <see cref="ColorProfileConversionMetadata"/> on the image metadata and on every frame.
    /// </summary>
    ConvertToSrgb = 1,
}

/// <summary>
/// What a conversion to sRGB does when the embedded profile cannot be applied by the managed engine: a
/// LUT-based (A2B/B2A) profile, a CMYK, grey or Lab profile, an abstract or device-link profile, or a profile
/// whose bytes are malformed.
/// </summary>
public enum UnsupportedColorProfileHandling
{
    /// <summary>
    /// The frame's pixels and its profile are left exactly as decoded, and the reason is recorded as
    /// <see cref="ColorProfileConversionStatus.Unsupported"/> or <see cref="ColorProfileConversionStatus.Invalid"/>
    /// in <see cref="ColorProfileConversionMetadata"/>. This is the default.
    /// </summary>
    Keep = 0,

    /// <summary>
    /// The conversion fails: an unsupported profile throws <see cref="NotSupportedException"/> and a malformed
    /// one throws <see cref="InvalidImageContentException"/>. Nothing is converted, because every frame's profile
    /// is checked before any pixel is written.
    /// </summary>
    Throw = 1,
}

/// <summary>The outcome of converting an image or frame from its embedded ICC profile to sRGB.</summary>
public enum ColorProfileConversionStatus
{
    /// <summary>No ICC profile was embedded, so the pixels were already taken to be sRGB and were left unchanged.</summary>
    NoProfile = 0,

    /// <summary>The pixels were converted from the embedded RGB matrix/TRC profile to sRGB and the profile was removed.</summary>
    Converted = 1,

    /// <summary>
    /// The embedded profile describes sRGB to within half an 8-bit code value everywhere it was sampled, so the
    /// pixels were left unchanged and the redundant profile was removed.
    /// </summary>
    AlreadySrgb = 2,

    /// <summary>
    /// The embedded profile is well formed but outside the managed matrix/TRC subset (for example LUT-based, CMYK,
    /// grey or Lab). The pixels and the profile were left as decoded.
    /// </summary>
    Unsupported = 3,

    /// <summary>The embedded profile is malformed. The pixels and the profile were left as decoded.</summary>
    Invalid = 4,
}
