namespace EasyImageSharp.Processing;

/// <summary>Edge-preserving smoothing (bilateral and guided filters).</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Bilateral filter: smooths noise and texture while keeping edges whose contrast is large compared with
    /// <see cref="BilateralFilterOptions.SigmaRange"/>. Colour channels are filtered; alpha is kept. See
    /// <see cref="BilateralFilterMethod"/> for the exact and the grid evaluation.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement the filter.</exception>
    IImageProcessingContext BilateralFilter(BilateralFilterOptions options)
        => throw new NotSupportedException("This processing context does not support the bilateral filter.");

    /// <summary>
    /// Self-guided filter (He, Sun and Tang 2013) on each colour channel: within every
    /// <c>(2 radius + 1)</c>-square window the output is the linear fit <c>a I + b</c> of the channel to itself with
    /// <c>a = var / (var + epsilon)</c>, averaged over the windows covering the pixel (windows are clipped at the
    /// border). Regions whose variance is well below <paramref name="epsilon"/> are smoothed; edges with larger
    /// variance are kept. Values are normalised to 0-1, so <paramref name="epsilon"/> = 0.01 corresponds to a
    /// standard deviation of 0.1 (about 25 8-bit levels). Alpha is kept.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is outside 1-256 or <paramref name="epsilon"/> is not positive and finite.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement the filter.</exception>
    IImageProcessingContext GuidedFilter(int radius, float epsilon)
        => throw new NotSupportedException("This processing context does not support the guided filter.");

    /// <summary>
    /// Joint guided filter: like <see cref="GuidedFilter(int, float)"/>, but every colour channel is fitted to the
    /// BT.601 luma of <paramref name="guide"/>'s root frame, so the guide's edges are transferred (for example to
    /// refine a mask or a depth map with the photo it belongs to). The guide must have the frame's size.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="guide"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The guide's size differs from a frame's size.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is outside 1-256 or <paramref name="epsilon"/> is not positive and finite.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement the filter.</exception>
    IImageProcessingContext GuidedFilter(Image guide, int radius, float epsilon)
        => throw new NotSupportedException("This processing context does not support the guided filter.");
}
