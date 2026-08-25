namespace EasyImageSharp.AI;

/// <summary>What a model is for. Informational; the operations know their own contracts.</summary>
public enum ImageModelTask
{
    /// <summary>Whole-image classification (for example page orientation).</summary>
    Classification,

    /// <summary>Document orientation: 0 / 90 / 180 / 270 degree page rotation.</summary>
    DocumentOrientation,

    /// <summary>Document dewarp / unwarp: a rectified page image.</summary>
    Dewarp,

    /// <summary>Single-image super-resolution.</summary>
    SuperResolution,

    /// <summary>Denoising (residual or direct).</summary>
    Denoise,

    /// <summary>Salient-object segmentation / background removal.</summary>
    Saliency,

    /// <summary>Learned document binarisation (per-pixel threshold map).</summary>
    Binarization,

    /// <summary>Any other image-to-image network.</summary>
    ImageToImage,
}

/// <summary>
/// How pixels are mapped to tensor values: <c>value = (byte / 255 - Mean[c]) / Std[c]</c>, matching the
/// core <c>ToChwTensor</c> / <c>FromChwTensor</c> bridges. Use <see cref="Unit"/> for plain 0-1 inputs,
/// <see cref="ImageNet"/> for torchvision-style networks and <see cref="Byte"/> for raw 0-255 inputs.
/// </summary>
public sealed record TensorNormalization
{
    /// <summary>Per-channel mean subtracted after scaling to 0-1 (one value is broadcast to all channels).</summary>
    public required float[] Mean { get; init; }

    /// <summary>Per-channel standard deviation divided by after mean subtraction.</summary>
    public required float[] Std { get; init; }

    /// <summary>Plain 0-1 range: <c>byte / 255</c>.</summary>
    public static TensorNormalization Unit { get; } = new() { Mean = [0f], Std = [1f] };

    /// <summary>ImageNet statistics on RGB in 0-1: mean (0.485, 0.456, 0.406), std (0.229, 0.224, 0.225).</summary>
    public static TensorNormalization ImageNet { get; } = new()
    {
        Mean = [0.485f, 0.456f, 0.406f],
        Std = [0.229f, 0.224f, 0.225f],
    };

    /// <summary>Raw 0-255 range (no scaling): mean 0, std 1/255.</summary>
    public static TensorNormalization Byte { get; } = new() { Mean = [0f], Std = [1f / 255f] };

    /// <summary>Symmetric -1..1 range: <c>(byte / 255 - 0.5) / 0.5</c>.</summary>
    public static TensorNormalization Symmetric { get; } = new() { Mean = [0.5f], Std = [0.5f] };
}

/// <summary>
/// Describes one downloadable ONNX model: where it lives, how to verify it and how to feed it. Instances for
/// the built-in models are exposed by <see cref="ModelRegistry"/>; create your own for models on a private
/// mirror (see the package README for the flat-file layout and immutability rules).
/// </summary>
public sealed record ModelDescriptor
{
    /// <summary>Stable identifier used for <see cref="ImageAiOptions.ModelPathOverrides"/> (for example <c>doc-orientation</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The fp32 file name (single path segment, no directories), e.g. <c>UVDoc.onnx</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Upper-case hex SHA-256 of the exact published fp32 file, or <c>null</c> when the file is not yet published
    /// (a placeholder entry: supply your own export through <see cref="ImageAiOptions.ModelPathOverrides"/>, or
    /// opt into <see cref="ImageAiOptions.AllowUnverifiedModels"/> against a mirror you trust).
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>File name of the int8-quantised variant (<c>{name}.int8.onnx</c>), or <c>null</c> when none exists.</summary>
    public string? Int8FileName { get; init; }

    /// <summary>Upper-case hex SHA-256 of the int8 file, or <c>null</c> when not published.</summary>
    public string? Int8Sha256 { get; init; }

    /// <summary>Base URL the file names are appended to (no trailing slash needed).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>License of the weights (SPDX identifier where possible).</summary>
    public required string License { get; init; }

    /// <summary>Name of the image input; <c>null</c> or empty means "the graph's first input".</summary>
    public string? InputName { get; init; }

    /// <summary>Expected input shape in NCHW; <c>-1</c> marks a dynamic axis. Informational and used for validation.</summary>
    public int[] InputShape { get; init; } = [1, 3, -1, -1];

    /// <summary>How input pixels are normalised.</summary>
    public TensorNormalization Normalization { get; init; } = TensorNormalization.Unit;

    /// <summary>What the model does.</summary>
    public ImageModelTask Task { get; init; } = ImageModelTask.ImageToImage;

    /// <summary>Approximate size of the fp32 file in bytes when known (for UIs; not used for verification).</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Human-readable provenance (upstream project, commit, exporter).</summary>
    public string? Provenance { get; init; }

    /// <summary>True when the fp32 file has a pinned checksum, i.e. it is published and downloadable by default.</summary>
    public bool IsPublished => !string.IsNullOrEmpty(this.Sha256);

    /// <summary>True when an int8 variant with a pinned checksum is published.</summary>
    public bool HasPublishedInt8 => !string.IsNullOrEmpty(this.Int8FileName) && !string.IsNullOrEmpty(this.Int8Sha256);

    /// <summary>The default download URL of the fp32 file.</summary>
    public string Url => Combine(this.BaseUrl, this.FileName);

    /// <summary>The default download URL of the int8 file, or <c>null</c>.</summary>
    public string? Int8Url => this.Int8FileName is null ? null : Combine(this.BaseUrl, this.Int8FileName);

    internal static string Combine(string baseUrl, string fileName) => $"{baseUrl.TrimEnd('/')}/{fileName}";
}
