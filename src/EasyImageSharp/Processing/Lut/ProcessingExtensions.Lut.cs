namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for colour lookup tables.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Applies <paramref name="lut"/> fully with tetrahedral interpolation.</summary>
    public static IImageProcessingContext ApplyLut(this IImageProcessingContext context, ColorLut lut)
        => context.ApplyLut(lut, LutInterpolation.Tetrahedral, 1f);

    /// <summary>Applies <paramref name="lut"/> fully with the given interpolation.</summary>
    public static IImageProcessingContext ApplyLut(this IImageProcessingContext context, ColorLut lut, LutInterpolation interpolation)
        => context.ApplyLut(lut, interpolation, 1f);
}
