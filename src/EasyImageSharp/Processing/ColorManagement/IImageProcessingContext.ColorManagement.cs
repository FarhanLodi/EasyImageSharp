using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Metadata.Icc;

namespace EasyImageSharp.Processing;

/// <content>Colour management: conversion from embedded ICC profiles to sRGB.</content>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Converts every frame from its embedded ICC profile (the frame's own, else the image's) to sRGB and removes the
    /// profiles that no longer describe the pixels. The managed engine applies RGB matrix/TRC profiles - Display P3,
    /// Adobe RGB, ProPhoto and the like - relative-colorimetrically through D50 with Bradford adaptation, in
    /// single-precision float. Frames without a profile, and frames whose profile already describes sRGB, keep their
    /// pixels. The outcome is recorded in a <see cref="ColorProfileConversionMetadata"/> on the image and on every frame.
    /// </summary>
    /// <param name="unsupportedProfileHandling">
    /// What to do with a profile outside the matrix/TRC subset (LUT-based, CMYK, grey, Lab) or a malformed one:
    /// leave that frame unconverted, or throw before any pixel is written.
    /// </param>
    /// <remarks>
    /// Integer pixel formats clamp out-of-gamut colours to the sRGB range; <see cref="PixelFormats.RgbaVector"/> keeps
    /// them as values outside 0-1. Alpha is copied unchanged. Pixel formats without colour channels hold only luminance
    /// and are converted as neutral greys; load as a colour format and convert to grey afterwards when the colour
    /// conversion has to be exact. Running the operation again is a no-op, because the converted profiles are gone.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unsupportedProfileHandling"/> is not a defined value.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UnsupportedColorProfileHandling.Throw"/> and a frame's profile is outside the matrix/TRC subset.
    /// </exception>
    /// <exception cref="InvalidImageContentException">
    /// <see cref="UnsupportedColorProfileHandling.Throw"/> and a frame's profile is malformed.
    /// </exception>
    IImageProcessingContext ConvertToSrgb(UnsupportedColorProfileHandling unsupportedProfileHandling)
        => throw new NotSupportedException("This processing context does not support color profile conversion.");

    /// <summary>
    /// Converts every frame to sRGB from <paramref name="sourceProfile"/>, ignoring any embedded profile, and removes the
    /// embedded profiles. Use it for untagged images whose colour space is known from elsewhere, for example a Display P3
    /// capture pipeline. The outcome is recorded in a <see cref="ColorProfileConversionMetadata"/> as for
    /// <see cref="ConvertToSrgb(UnsupportedColorProfileHandling)"/>.
    /// </summary>
    /// <param name="sourceProfile">The colour space the pixels are encoded in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceProfile"/> is <see langword="null"/>.</exception>
    IImageProcessingContext ConvertToSrgb(IccMatrixProfile sourceProfile)
        => throw new NotSupportedException("This processing context does not support color profile conversion.");
}
