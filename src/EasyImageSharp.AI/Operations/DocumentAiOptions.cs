using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;

namespace EasyImageSharp.AI;

/// <summary>Explicit interpretation of a dewarp model's output. Image and grid exports are not interchangeable.</summary>
public enum DocumentDewarpOutputKind
{
    /// <summary>A rectified [1,3,H,W] image in [0,1]. Output is opaque because the model has no alpha channel. No geometric correspondence is available.</summary>
    RectifiedImage,
    /// <summary>A [1,2,H,W] backward sampling grid, X then Y, normalized to [-1,1]. The original full-resolution image is resampled.</summary>
    NormalizedSamplingGrid,
}

/// <summary>Export-specific document dewarp contract. Defaults match the published dynamic BGR image export.</summary>
public sealed record DocumentDewarpContract
{
    public DocumentDewarpOutputKind OutputKind { get; init; }
    public TensorChannels Channels { get; init; } = TensorChannels.Bgr;
    public string? InputName { get; init; }
    public string? OutputName { get; init; }
    /// <summary>Empty feeds full resolution. Explicit fixed sizes resize model input; image outputs then lose fine detail, while grid outputs still sample original pixels.</summary>
    public Size FixedInputSize { get; init; }
    /// <summary>Edge-replicate input padding to this multiple; default 32. Use 1 for exports that require an exact fixed size. Grid exports require no added padding because their normalized coordinates cover the original image.</summary>
    public int SizeMultiple { get; init; } = 32;
    /// <summary>Grid interpolation and normalized sampling use align_corners=true by default. False uses half-pixel grid resizing and edge-coordinate normalization.</summary>
    public bool GridAlignCorners { get; init; } = true;
    /// <summary>Maximum padded input pixel count, default 16 million. Oversized input is rejected rather than silently downscaled.</summary>
    public long MaximumInputPixels { get; init; } = 16_000_000;

    internal void Validate()
    {
        if (!Enum.IsDefined(this.OutputKind) || this.Channels is not (TensorChannels.Rgb or TensorChannels.Bgr)
            || this.SizeMultiple < 1 || this.MaximumInputPixels < 1
            || ((!this.FixedInputSize.IsEmpty) && (this.FixedInputSize.Width < 1 || this.FixedInputSize.Height < 1)))
        {
            throw new ArgumentException("Invalid dewarp output kind, channel order, dimensions, size multiple or pixel budget.");
        }
    }
}

/// <summary>AI stages run before the classical profile. Requested models are required; failures are never silently replaced by classical estimates.</summary>
public sealed class DocumentAiPreprocessingOptions
{
    public bool AutoOrient { get; set; } = true;
    public bool Dewarp { get; set; }
    /// <summary>Winning class-score threshold in [0,1], default 0.5. Model scores are not calibrated OCR accuracy probabilities.</summary>
    public float MinimumOrientationConfidence { get; set; } = 0.5f;
    public DocumentDewarpContract DewarpContract { get; set; } = new();
    /// <summary>Classical stages after AI geometry; default preserves pixels. Classical AutoOrient is disabled when AI orientation was requested.</summary>
    public DocumentPreprocessingOptions Preprocessing { get; set; } = new();
}
