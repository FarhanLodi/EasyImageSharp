namespace EasyImageSharp.AI;

/// <summary>Tuning for <see cref="InpaintingExtensions"/>.</summary>
public sealed class InpaintingOptions
{
    /// <summary>The defaults: threshold 128, no dilation, a context margin of half the region size, 512 px working size.</summary>
    public static InpaintingOptions Default { get; } = new();

    /// <summary>
    /// A mask pixel at or above this value marks a hole to fill; lower values are kept. Default 128, so anti-aliased
    /// edges below half intensity are kept; use 1 to treat every non-zero mask pixel as a hole. Must be 1-255.
    /// </summary>
    public byte MaskThreshold { get; init; } = 128;

    /// <summary>
    /// Grows every hole by this many pixels (a square neighbourhood) before inpainting, so a mask drawn tightly around an
    /// object also covers its soft edge and halo. Default 0. Must be 0-256.
    /// </summary>
    public int MaskDilation { get; init; }

    /// <summary>
    /// Context the model sees around each hole, as a fraction of the hole's longer side added on every side (0.5 gives a
    /// crop about twice the hole). Crops are never smaller than the model's working size while the image allows it, so
    /// small holes are filled at full resolution. Larger values give the model more surroundings at the cost of a lower
    /// working resolution for big holes. Default 0.5. Must be 0-10.
    /// </summary>
    public float ContextMargin { get; init; } = 0.5f;

    /// <summary>
    /// The working resolution for exports with dynamic spatial axes (a crop larger than this is downscaled first; the
    /// model input is padded to a multiple of 8). Exports with a fixed input size, like the built-in 512x512 model,
    /// always run at their declared size and ignore this. Default 512. Must be 64-4096.
    /// </summary>
    public int WorkingSize { get; init; } = 512;

    internal void Validate()
    {
        if (this.MaskThreshold == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.MaskThreshold), this.MaskThreshold, "The mask threshold must be 1-255; 0 would mark every pixel as a hole.");
        }

        if (this.MaskDilation is < 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(this.MaskDilation), this.MaskDilation, "The mask dilation must be 0-256 pixels.");
        }

        if (!(this.ContextMargin >= 0f && this.ContextMargin <= 10f))
        {
            throw new ArgumentOutOfRangeException(nameof(this.ContextMargin), this.ContextMargin, "The context margin must be a finite value between 0 and 10.");
        }

        if (this.WorkingSize is < 64 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(this.WorkingSize), this.WorkingSize, "The working size must be 64-4096 pixels.");
        }
    }
}
