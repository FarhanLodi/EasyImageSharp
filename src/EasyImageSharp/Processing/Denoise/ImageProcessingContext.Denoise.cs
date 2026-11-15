namespace EasyImageSharp.Processing;

/// <summary>Edge-preserving smoothing; see <c>IImageProcessingContext.Denoise.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext BilateralFilter(BilateralFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        BilateralFilterMethod method = options.Method switch
        {
            BilateralFilterMethod.Auto => options.EffectiveRadius <= BilateralFilterOptions.MaxExactRadius
                ? BilateralFilterMethod.Exact
                : BilateralFilterMethod.Grid,
            BilateralFilterMethod.Exact or BilateralFilterMethod.Grid => options.Method,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Method, "Unknown bilateral filter method."),
        };

        return this.PerFrame(frame =>
        {
            if (method == BilateralFilterMethod.Exact)
            {
                BilateralOps.Exact(frame, options.EffectiveRadius, options.SigmaSpatial, options.SigmaRange);
            }
            else
            {
                BilateralOps.Grid(frame, options.SigmaSpatial, options.SigmaRange);
            }

            return frame;
        });
    }

    public IImageProcessingContext GuidedFilter(int radius, float epsilon)
    {
        GuidedFilterOps.Validate(radius, epsilon);
        return this.PerFrame(frame =>
        {
            GuidedFilterOps.Apply(frame, null, radius, epsilon);
            return frame;
        });
    }

    public IImageProcessingContext GuidedFilter(Image guide, int radius, float epsilon)
    {
        ArgumentNullException.ThrowIfNull(guide);
        GuidedFilterOps.Validate(radius, epsilon);
        float[] luma = GuidedFilterOps.GuideLuma(guide);
        return this.PerFrame(frame =>
        {
            if (frame.Width != guide.Width || frame.Height != guide.Height)
            {
                throw new ArgumentException(FormattableString.Invariant(
                    $"The guide is {guide.Width}x{guide.Height} but the frame is {frame.Width}x{frame.Height}."), nameof(guide));
            }

            GuidedFilterOps.Apply(frame, luma, radius, epsilon);
            return frame;
        });
    }
}
