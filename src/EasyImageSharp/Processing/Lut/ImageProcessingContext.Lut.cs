namespace EasyImageSharp.Processing;

/// <summary>Colour lookup tables; see <c>IImageProcessingContext.Lut.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext ApplyLut(ColorLut lut, LutInterpolation interpolation, float amount)
    {
        ArgumentNullException.ThrowIfNull(lut);
        if (interpolation is not (LutInterpolation.Tetrahedral or LutInterpolation.Trilinear))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "Unknown interpolation.");
        }

        if (!(amount >= 0f && amount <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Amount must be between 0 and 1.");
        }

        if (amount == 0f)
        {
            return this;
        }

        return this.PerFrame(frame =>
        {
            LutOps.Apply(frame, lut, interpolation == LutInterpolation.Tetrahedral, amount);
            return frame;
        });
    }
}
