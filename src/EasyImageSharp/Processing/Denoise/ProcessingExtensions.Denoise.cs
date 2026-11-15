namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the edge-preserving filters.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Bilateral filter with the default <see cref="BilateralFilterOptions"/> (sigma 3 px, range sigma 0.1, exact).</summary>
    public static IImageProcessingContext BilateralFilter(this IImageProcessingContext context)
        => context.BilateralFilter(new BilateralFilterOptions());

    /// <summary>Bilateral filter with the given sigmas and an automatic radius and method.</summary>
    public static IImageProcessingContext BilateralFilter(this IImageProcessingContext context, float sigmaSpatial, float sigmaRange)
        => context.BilateralFilter(new BilateralFilterOptions { SigmaSpatial = sigmaSpatial, SigmaRange = sigmaRange });

    /// <summary>Self-guided filter with radius 4 and epsilon 0.01 (a 0.1 standard deviation).</summary>
    public static IImageProcessingContext GuidedFilter(this IImageProcessingContext context)
        => context.GuidedFilter(4, 0.01f);
}
