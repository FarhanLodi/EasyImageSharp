using System.Collections.Frozen;

namespace EasyImageSharp.AI;

/// <summary>
/// The catalogue of models the built-in operations use. Two entries (document orientation and dewarp) are
/// published today with pinned checksums; the remaining entries are placeholders that document the exact
/// file name and I/O contract the operations expect, so an export you produce yourself (supplied through
/// <see cref="ImageAiOptions.ModelPathOverrides"/>) or a future first-party publication drops straight in.
/// Files are flat (single path segment) under the base URL; a published file is never overwritten.
/// </summary>
public static class ModelRegistry
{
    /// <summary>
    /// Base URL of the first-party model repository. Every model this library uses is expected here, so the
    /// whole supply chain is under one owner: files are flat (a single path segment), a published file is
    /// never overwritten, and each one is pinned by the SHA-256 in <c>Checksums</c>.
    /// </summary>
    /// <remarks>
    /// Point this elsewhere without recompiling through <see cref="ImageAiOptions.BaseUrlOverride"/> or the
    /// <c>EASYIMAGESHARP_MODEL_BASE_URL</c> environment variable — useful for an internal mirror. Because
    /// verification is by content hash, a mirror serving identical bytes validates identically.
    /// </remarks>
    public const string DefaultBaseUrl = "https://huggingface.co/EasyImageSharp/EasyImageSharp-models/resolve/main";

    /// <summary>
    /// Upstream repository for the two PaddleOCR-derived document-preprocessing models, used until they are
    /// mirrored into the first-party repository. Both are pinned by checksum, so re-uploading the identical
    /// bytes to <see cref="DefaultBaseUrl"/> and switching these two descriptors over needs no other change.
    /// </summary>
    public const string PaddleOcrNetBaseUrl = "https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models/resolve/main";

    /// <summary>
    /// Upper-case hex SHA-256 of every published file, verified after download by <see cref="ModelHub"/>.
    /// Values are taken from the exact uploaded bytes; a re-export always gets a new file name and a new entry.
    /// </summary>
    private static readonly FrozenDictionary<string, string> Checksums = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PP-LCNet_x1_0_doc_ori.onnx"] = "D85B3185075AFCA1A83157F73EAC2E52B598D72E9D47DD19CC4A2F3605E23E3F",
        ["UVDoc.onnx"] = "7E54E917AD9CA8F6CFFE606C7C311AAD3B6EEE457D4D9776F99F175D0CA86835",
        ["realesrgan_general_x4v3.onnx"] = "AAA2B465D2258BDCC30D51076BC358DA00D1595D2FA05697979E782F97DE325A",
        ["dncnn_gray_blind.onnx"] = "A0A21D0677EA5FB83A66D922EBFB22BC81926C79044B08778F4A6D740FA7864F",
        ["u2netp.onnx"] = "2B5D0563269555FC84FFCA01B24AF5081581D38614F858ECF913331DF0E2ED88",
        ["u2net.onnx"] = "8D10D2F3BB75AE3B6D527C77944FC5E7DCD94B29809D47A739A7A728A912B491",
        ["sauvolanet.onnx"] = "948AAEA4882D4D6734C0FEC4739381857BE97F62526AD8BA8CA067A353106160",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// PP-LCNet_x1_0 document-orientation classifier (PaddleX <c>doc_orientation_classify</c>): input <c>x</c>
    /// <c>[1,3,224,224]</c>, RGB, ImageNet mean/std on 0-1 values, page stretched to 224x224 with bicubic
    /// resampling; output <c>[1,4]</c> scores over {0, 90, 180, 270} degrees clockwise (index i means the page
    /// is currently rotated i*90 degrees clockwise from upright). Apache-2.0.
    /// </summary>
    public static ModelDescriptor DocumentOrientation { get; } = new()
    {
        Name = "doc-orientation",
        FileName = "PP-LCNet_x1_0_doc_ori.onnx",
        Sha256 = Checksums["PP-LCNet_x1_0_doc_ori.onnx"],
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "x",
        InputShape = [1, 3, 224, 224],
        Normalization = TensorNormalization.ImageNet,
        Task = ImageModelTask.DocumentOrientation,
        SizeBytes = 6_800_000,
        Provenance = "PaddleX PP-LCNet_x1_0_doc_ori inference model exported to ONNX (PaddleOcrNet-models repository).",
    };

    /// <summary>
    /// UVDoc page dewarp network: input <c>image</c> <c>[1,3,H,W]</c> RGB in 0-1 (no mean/std), fed at the
    /// canonical 488x712 (width x height); output <c>[1,3,H,W]</c> is a rectified RGB image in 0-1 at the
    /// same size, which the operation resizes back to the source dimensions. MIT.
    /// </summary>
    public static ModelDescriptor DocumentDewarp { get; } = new()
    {
        Name = "doc-dewarp",
        FileName = "UVDoc.onnx",
        Sha256 = Checksums["UVDoc.onnx"],
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "image",
        InputShape = [1, 3, 712, 488],
        Normalization = TensorNormalization.Unit,
        Task = ImageModelTask.Dewarp,
        SizeBytes = 31_000_000,
        Provenance = "UVDoc (tanguymagne/UVDoc) exported to ONNX (PaddleOcrNet-models repository).",
    };

    /// <summary>
    /// Real-ESRGAN general x4 v3 (SRVGGNet compact) super-resolution. Contract: input <c>[1,3,H,W]</c> RGB in
    /// 0-1, dynamic H/W; output <c>[1,3,4H,4W]</c> in 0-1. Tiled at 256 px with 16 px overlap. BSD-3-Clause.
    /// <b>Not yet published</b> (no pinned checksum): supply your own export via
    /// <see cref="ImageAiOptions.ModelPathOverrides"/> under the name <c>super-resolution-x4</c>.
    /// </summary>
    public static ModelDescriptor SuperResolutionX4 { get; } = new()
    {
        Name = "super-resolution-x4",
        FileName = "realesrgan_general_x4v3.onnx",
        Sha256 = Checksums.GetValueOrDefault("realesrgan_general_x4v3.onnx"),
        Int8FileName = "realesrgan_general_x4v3.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("realesrgan_general_x4v3.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "BSD-3-Clause",
        InputName = "input",
        InputShape = [1, 3, -1, -1],
        Normalization = TensorNormalization.Unit,
        Task = ImageModelTask.SuperResolution,
        SizeBytes = 4_900_000,
        Provenance = "xinntao/Real-ESRGAN realesr-general-x4v3 (SRVGGNetCompact) exported with dynamic H/W, opset 17.",
    };

    /// <summary>
    /// DnCNN blind grayscale denoiser (17-layer residual CNN). Contract: input <c>[1,1,H,W]</c> luminance in 0-1,
    /// dynamic H/W; output <c>[1,1,H,W]</c> is the predicted <i>noise residual</i>, so <c>clean = input - output</c>.
    /// Tiled at 256 px with 16 px overlap. Check the upstream license terms before redistribution.
    /// <b>Not yet published</b>: override under the name <c>denoise-gray</c>.
    /// </summary>
    public static ModelDescriptor DenoiseGray { get; } = new()
    {
        Name = "denoise-gray",
        FileName = "dncnn_gray_blind.onnx",
        Sha256 = Checksums.GetValueOrDefault("dncnn_gray_blind.onnx"),
        Int8FileName = "dncnn_gray_blind.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("dncnn_gray_blind.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "input",
        InputShape = [1, 1, -1, -1],
        Normalization = TensorNormalization.Unit,
        Task = ImageModelTask.Denoise,
        SizeBytes = 2_300_000,
        Provenance = "cszn/DnCNN dncnn_gray_blind exported with dynamic H/W, opset 17.",
    };

    /// <summary>
    /// U2-Net-p salient object detector. Contract: input <c>[1,3,320,320]</c> RGB, ImageNet mean/std on 0-1
    /// values, image stretched to 320x320; output <c>[1,1,320,320]</c> saliency in 0-1 (the fused <c>d0</c>
    /// map after sigmoid; raw logits are also accepted). Apache-2.0. <b>Not yet published</b>: override under the
    /// name <c>saliency</c>.
    /// </summary>
    public static ModelDescriptor Saliency { get; } = new()
    {
        Name = "saliency",
        FileName = "u2net.onnx",
        Sha256 = Checksums.GetValueOrDefault("u2net.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "input.1",
        InputShape = [1, 3, 320, 320],
        Normalization = TensorNormalization.ImageNet,
        Task = ImageModelTask.Saliency,
        SizeBytes = 176_000_000,
        Provenance = "xuebinqin/U-2-Net u2net (full) at 320x320, opset 17.",
    };

    /// <summary>
    /// The small U-2-Net variant, for callers who would rather have a 4.7 MB download and roughly 2.5x the
    /// speed than the accuracy of <see cref="Saliency"/>. On clear, high-contrast subjects the two agree;
    /// the small one loses feathered edges and thin structures. Select it with
    /// <c>ImageAiOptions.ModelPathOverrides</c> or by passing this descriptor explicitly. Apache-2.0.
    /// </summary>
    public static ModelDescriptor SaliencyFast { get; } = new()
    {
        Name = "saliency-fast",
        FileName = "u2netp.onnx",
        Sha256 = Checksums.GetValueOrDefault("u2netp.onnx"),
        Int8FileName = "u2netp.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("u2netp.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "input",
        InputShape = [1, 3, 320, 320],
        Normalization = TensorNormalization.ImageNet,
        Task = ImageModelTask.Saliency,
        SizeBytes = 4_700_000,
        Provenance = "xuebinqin/U-2-Net u2netp exported at 320x320, opset 17.",
    };

    /// <summary>
    /// SauvolaNet learned document binarisation. Contract: input <c>[1,1,H,W]</c> luminance in 0-1, dynamic H/W;
    /// output <c>[1,1,H,W]</c> per-pixel threshold in the same 0-1 scale; a pixel is white when
    /// <c>luminance &gt;= threshold</c>. Run whole-image (no tiling). Check the upstream license terms.
    /// <b>Not yet published</b>: override under the name <c>binarization</c>.
    /// </summary>
    public static ModelDescriptor Binarization { get; } = new()
    {
        Name = "binarization",
        FileName = "sauvolanet.onnx",
        Sha256 = Checksums.GetValueOrDefault("sauvolanet.onnx"),
        Int8FileName = "sauvolanet.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("sauvolanet.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "input",
        InputShape = [1, 1, -1, -1],
        Normalization = TensorNormalization.Unit,
        Task = ImageModelTask.Binarization,
        SizeBytes = 200_000,
        Provenance = "Leedeng/SauvolaNet exported with dynamic H/W and a /255 input scale, opset 17.",
    };

    /// <summary>Every built-in descriptor, published or placeholder.</summary>
    public static IReadOnlyList<ModelDescriptor> All { get; } =
    [
        DocumentOrientation,
        DocumentDewarp,
        SuperResolutionX4,
        DenoiseGray,
        Saliency,
        SaliencyFast,
        Binarization,
    ];

    /// <summary>Only the descriptors whose fp32 file has a pinned checksum today.</summary>
    public static IReadOnlyList<ModelDescriptor> Published { get; } = All.Where(d => d.IsPublished).ToArray();

    /// <summary>Finds a descriptor by <see cref="ModelDescriptor.Name"/> or file name (case-insensitive), or <c>null</c>.</summary>
    public static ModelDescriptor? Find(string nameOrFileName)
    {
        ArgumentNullException.ThrowIfNull(nameOrFileName);
        foreach (ModelDescriptor descriptor in All)
        {
            if (string.Equals(descriptor.Name, nameOrFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(descriptor.FileName, nameOrFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(descriptor.Int8FileName, nameOrFileName, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }
        }

        return null;
    }

    /// <summary>The pinned SHA-256 (upper-case hex) of a published file name, or <c>null</c>.</summary>
    public static string? GetChecksum(string fileName)
        => Checksums.TryGetValue(fileName, out string? value) ? value : null;
}
