using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <content>Colour management: conversion from embedded ICC profiles to sRGB.</content>
internal sealed partial class ImageProcessingContext<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    public IImageProcessingContext ConvertToSrgb(UnsupportedColorProfileHandling unsupportedProfileHandling)
    {
        if (unsupportedProfileHandling is not (UnsupportedColorProfileHandling.Keep or UnsupportedColorProfileHandling.Throw))
        {
            throw new ArgumentOutOfRangeException(
                nameof(unsupportedProfileHandling), unsupportedProfileHandling, "Unknown unsupported-profile handling.");
        }

        return this.ExecuteColorConversion(ColorManagementOps.PlanEmbedded(this.image, unsupportedProfileHandling));
    }

    public IImageProcessingContext ConvertToSrgb(IccMatrixProfile sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        return this.ExecuteColorConversion(ColorManagementOps.PlanExplicit(this.image.Frames.Count, sourceProfile));
    }

    private IImageProcessingContext ExecuteColorConversion(ColorManagementOps.ConversionPlan plan)
    {
        if (plan.WritesPixels)
        {
            // A copy-on-write clone still shares the source's buffers; give it its own before writing in place.
            this.EnsureOwnBuffers();
        }

        ColorManagementOps.Execute(this.image, plan);
        return this;
    }
}
