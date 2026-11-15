namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for tone mapping.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Tone maps every frame with the ACES filmic curve at the image's own exposure; see <see cref="IImageProcessingContext.ToneMap(ToneMappingOptions)"/>.</summary>
    public static IImageProcessingContext ToneMap(this IImageProcessingContext context)
        => context.ToneMap(new ToneMappingOptions());

    /// <summary>Tone maps every frame with the given curve and its default white point.</summary>
    public static IImageProcessingContext ToneMap(this IImageProcessingContext context, ToneMapper toneMapper)
        => context.ToneMap(new ToneMappingOptions { ToneMapper = toneMapper });

    /// <summary>Tone maps every frame with the given curve after an exposure adjustment of <paramref name="exposure"/> stops.</summary>
    public static IImageProcessingContext ToneMap(this IImageProcessingContext context, ToneMapper toneMapper, float exposure)
        => context.ToneMap(new ToneMappingOptions { ToneMapper = toneMapper, Exposure = exposure });
}
