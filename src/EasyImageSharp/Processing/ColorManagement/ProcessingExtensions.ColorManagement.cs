using EasyImageSharp.ColorSpaces;

namespace EasyImageSharp.Processing;

/// <content>Convenience overloads for colour management.</content>
public static partial class ProcessingExtensions
{
    /// <summary>
    /// Converts every frame from its embedded ICC profile to sRGB, leaving frames whose profile the managed engine
    /// cannot apply unconverted; see <see cref="IImageProcessingContext.ConvertToSrgb(UnsupportedColorProfileHandling)"/>.
    /// </summary>
    public static IImageProcessingContext ConvertToSrgb(this IImageProcessingContext context)
        => context.ConvertToSrgb(UnsupportedColorProfileHandling.Keep);
}
