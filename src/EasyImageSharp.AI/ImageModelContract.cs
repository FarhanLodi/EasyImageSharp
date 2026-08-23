namespace EasyImageSharp.AI;

/// <summary>How to interpret an image-to-image model's output tensor.</summary>
public enum ImageModelOutputKind
{
    /// <summary>The output is the result image itself (super-resolution, dewarp, colourisation...).</summary>
    Image,

    /// <summary>
    /// The output is a residual to subtract from the input in tensor space (<c>result = input - output</c>),
    /// as DnCNN-style denoisers predict the noise rather than the clean image.
    /// </summary>
    Residual,
}

/// <summary>
/// Describes how to feed an image-to-image ONNX model and read its result, so that
/// <see cref="ImageModelRunner"/> can drive any fully convolutional network (yours or one from the registry):
/// tensor layout is always <c>[1, C, H, W]</c> float32 with C = 1 (luminance) or 3 (RGB).
/// </summary>
public sealed record ImageModelContract
{
    /// <summary>Name of the image input; <c>null</c> uses the graph's first input.</summary>
    public string? InputName { get; init; }

    /// <summary>Name of the image output; <c>null</c> uses the graph's first output.</summary>
    public string? OutputName { get; init; }

    /// <summary>1 for a single luminance plane, 3 for RGB planes. Default 3.</summary>
    public int InputChannels { get; init; } = 3;

    /// <summary>How input pixels are normalised. Default 0-1.</summary>
    public TensorNormalization InputNormalization { get; init; } = TensorNormalization.Unit;

    /// <summary>How output values map back to pixels (inverse of the input mapping). Default 0-1.</summary>
    public TensorNormalization OutputNormalization { get; init; } = TensorNormalization.Unit;

    /// <summary>Whether the output is the image or a residual to subtract. Default image.</summary>
    public ImageModelOutputKind OutputKind { get; init; } = ImageModelOutputKind.Image;

    /// <summary>
    /// Expected spatial scale of the output relative to the input (4 for a x4 super-resolution net). 0 (default)
    /// infers the scale from the first run; a non-zero value is validated against the actual output.
    /// </summary>
    public int ScaleFactor { get; init; }

    /// <summary>
    /// When non-empty, the input is stretched to exactly this size (width x height) before inference and the
    /// output is resized back to the source size times <see cref="ScaleFactor"/>. Use for networks with fixed
    /// input dimensions (UVDoc at 488x712, U2-Net at 320x320). Disables tiling.
    /// </summary>
    public Size FixedInputSize { get; init; }

    /// <summary>
    /// Tile edge in source pixels; 0 (default) runs the whole image in one pass. Tiles are cut with
    /// <see cref="TileOverlap"/> pixels of context on every inner side and only the centre of each tile's
    /// output is kept, so seams are invisible for networks whose receptive field is smaller than the overlap.
    /// </summary>
    public int TileSize { get; init; }

    /// <summary>Context added around each tile in source pixels. Default 16.</summary>
    public int TileOverlap { get; init; } = 16;

    /// <summary>
    /// Pads each network input (edge replication) so its width and height are multiples of this value, and crops
    /// the corresponding output. Needed by U-Net style networks with strided downsampling (typically 8, 16 or 32).
    /// Default 1 (no padding).
    /// </summary>
    public int SizeMultiple { get; init; } = 1;

    /// <summary>Validates the contract; throws <see cref="ArgumentException"/> when a field is out of range.</summary>
    public void Validate()
    {
        if (this.InputChannels is not (1 or 3))
        {
            throw new ArgumentException("InputChannels must be 1 or 3.", nameof(this.InputChannels));
        }

        if (this.ScaleFactor < 0)
        {
            throw new ArgumentException("ScaleFactor must be zero (auto) or positive.", nameof(this.ScaleFactor));
        }

        if (this.TileSize < 0 || (this.TileSize > 0 && this.TileSize < 8))
        {
            throw new ArgumentException("TileSize must be 0 (no tiling) or at least 8.", nameof(this.TileSize));
        }

        if (this.TileOverlap < 0)
        {
            throw new ArgumentException("TileOverlap must be non-negative.", nameof(this.TileOverlap));
        }

        if (this.SizeMultiple < 1)
        {
            throw new ArgumentException("SizeMultiple must be at least 1.", nameof(this.SizeMultiple));
        }

        if (!this.FixedInputSize.IsEmpty && (this.FixedInputSize.Width <= 0 || this.FixedInputSize.Height <= 0))
        {
            throw new ArgumentException("FixedInputSize must be empty or have positive width and height.", nameof(this.FixedInputSize));
        }

        ArgumentNullException.ThrowIfNull(this.InputNormalization);
        ArgumentNullException.ThrowIfNull(this.OutputNormalization);
    }
}
