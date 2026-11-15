namespace EasyImageSharp.Streaming;

/// <summary>
/// Chooses how far a decoder that can reduce while it decodes (the JPEG reduced IDCT, at 1/2, 1/4 or 1/8 scale)
/// may shrink a source that is about to be resized anyway.
/// </summary>
/// <remarks>
/// The reduced image is kept at least <see cref="Margin"/> times the resize target in both axes, so the final
/// resample is still a real reduction with a proper kernel rather than a near-1:1 step from a block average. The
/// same gap of two is what common thumbnailers use for their draft-mode decodes. A reduced decode does not give
/// bit-identical results to decoding at full size and resizing, which is why it is only ever used when a
/// resize follows.
/// </remarks>
internal static class ReducedDecode
{
    /// <summary>How many times larger than the resize target the reduced image must stay.</summary>
    internal const int Margin = 2;

    /// <summary>
    /// The largest denominator of 8, 4 and 2 that keeps <c>ceil(source / d)</c> at least <see cref="Margin"/> times
    /// the target in both axes; 1 when none does (including when the target is not smaller than the source).
    /// </summary>
    public static int ChooseDenominator(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        Guard.MustBePositive(sourceWidth, nameof(sourceWidth));
        Guard.MustBePositive(sourceHeight, nameof(sourceHeight));
        Guard.MustBePositive(targetWidth, nameof(targetWidth));
        Guard.MustBePositive(targetHeight, nameof(targetHeight));
        for (int denominator = 8; denominator > 1; denominator /= 2)
        {
            long width = ((long)sourceWidth + denominator - 1) / denominator;
            long height = ((long)sourceHeight + denominator - 1) / denominator;
            if (width >= (long)targetWidth * Margin && height >= (long)targetHeight * Margin)
            {
                return denominator;
            }
        }

        return 1;
    }
}
