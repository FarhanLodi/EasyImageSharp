using System.Collections.Frozen;

namespace EasyImageSharp.AI;

/// <summary>
/// The catalogue of models the built-in operations use. Entries with a pinned checksum are published (see
/// <see cref="Published"/>); the remaining entries are placeholders that document the exact file name and I/O
/// contract the operations expect, so an export you produce yourself (supplied through
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

        // YuNet 2026may: byte-identical to opencv/opencv_zoo models/face_detection_yunet at commit
        // 26cc381e4d2594bb9f47a26eb8fd96c94a13660d (the Git LFS pointer there pins the same SHA-256).
        ["face_detection_yunet_2026may.onnx"] = "EBAFCE4E3C118D6554634BE5C27AB333B4C047A9A8C3FAF1D7CF93101C22F0F0",

        // int8 static QDQ variants produced and evaluated by tools/ModelExport/quantize_int8.py (onnxruntime 1.29);
        // each met its pre-declared accuracy tolerance against the fp32 file (metrics in the model card).
        ["face_detection_yunet_2026may.int8.onnx"] = "BAFA17FB45FF43BD99C55654EFFC89724E4A78068ABE2C5630116B0158436578",
        ["sauvolanet.int8.onnx"] = "9368A0B1C8B25DEA4643F6D11688A1A61AA92A94BDEA3D9B9E5DF414F8FBDAA4",
        ["PP-LCNet_x1_0_doc_ori.int8.onnx"] = "7DBFAEA1CD5D11FA4E35C99B7312D5A3AE1F482654B1F424071C0C00194DB836",
        ["clip-vit-base-patch32-vision.onnx"] = "FD6E1402A588279D1723C7534D4BCBA5BC0B14B47DFAB0E46F8C47B8270D7D40",
        ["clip-vit-base-patch32-vision.int8.onnx"] = "583FD1110A514667812FEE7D684952AAF82A99B959760C8D7DCA7E0AB9839299",
        ["big-lama-512.onnx"] = "1FAEF5301D78DB7DDA502FE59966957EC4B79DD64E16F03ED96913C7A4EB68D6",
        ["nsfw-vit-tiny-384.onnx"] = "03D3AAB4390315F122E2A1827349C4818655776499CC36C1034AF69314BF56E2",
        ["nima-mobilenet-quality.onnx"] = "F311E55DDC1115BBE32306CA6F511787B4913B60E1F788CCDC704B83D548667B",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// PP-LCNet_x1_0 document-orientation classifier (PaddleX <c>doc_orientation_classify</c>): input <c>x</c>
    /// <c>[1,3,224,224]</c>, RGB, ImageNet mean/std on 0-1 values, aspect-preserving bilinear resize to short side 256
    /// followed by a 224x224 center crop; output <c>[1,4]</c> scores over {0, 90, 180, 270} degrees clockwise (index i means the page
    /// is currently rotated i*90 degrees clockwise from upright). Apache-2.0.
    /// </summary>
    public static ModelDescriptor DocumentOrientation { get; } = new()
    {
        Name = "doc-orientation",
        FileName = "PP-LCNet_x1_0_doc_ori.onnx",
        Sha256 = Checksums["PP-LCNet_x1_0_doc_ori.onnx"],
        Int8FileName = "PP-LCNet_x1_0_doc_ori.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("PP-LCNet_x1_0_doc_ori.int8.onnx"),
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
    /// UVDoc page dewarp network: input <c>image</c> <c>[1,3,H,W]</c> BGR in 0-1 (no mean/std), fed at full
    /// resolution with stride-32 edge padding; output <c>[1,3,H,W]</c> is a rectified BGR image in 0-1 at the
    /// same size, with padding removed after inference. It does not expose a geometric sampling grid. MIT.
    /// </summary>
    public static ModelDescriptor DocumentDewarp { get; } = new()
    {
        Name = "doc-dewarp",
        FileName = "UVDoc.onnx",
        Sha256 = Checksums["UVDoc.onnx"],
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "image",
        InputShape = [1, 3, -1, -1],
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

    /// <summary>
    /// YuNet face detector (OpenCV Zoo <c>face_detection_yunet_2026may</c>, the dynamic-shape re-export of the 2023mar
    /// weights). Contract: input <c>input</c> <c>[1,3,H,W]</c> BGR raw 0-255 (no mean/std), H and W zero-padded at the
    /// bottom / right to multiples of 32; outputs <c>cls_s</c> / <c>obj_s</c> <c>[1,N,1]</c>, <c>bbox_s</c>
    /// <c>[1,N,4]</c> and <c>kps_s</c> <c>[1,N,10]</c> for strides s = 8, 16, 32 over the row-major anchor grid
    /// (N = H/s * W/s). Finds faces of roughly 10-300 pixels at the network resolution. MIT (Shiqi Yu).
    /// </summary>
    public static ModelDescriptor FaceDetection { get; } = new()
    {
        Name = "face-detection",
        FileName = "face_detection_yunet_2026may.onnx",
        Sha256 = Checksums.GetValueOrDefault("face_detection_yunet_2026may.onnx"),
        Int8FileName = "face_detection_yunet_2026may.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("face_detection_yunet_2026may.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "input",
        InputShape = [1, 3, -1, -1],
        Normalization = TensorNormalization.Byte,
        Task = ImageModelTask.FaceDetection,
        SizeBytes = 229_738,
        Provenance = "opencv/opencv_zoo models/face_detection_yunet/face_detection_yunet_2026may.onnx at commit 26cc381e4d2594bb9f47a26eb8fd96c94a13660d (MIT, Shiqi Yu), redistributed unchanged.",
    };

    /// <summary>
    /// OpenAI CLIP ViT-B/32 image tower with its visual projection, used by <c>GetImageEmbedding</c>. Contract: input
    /// <c>pixel_values</c> <c>[N,3,224,224]</c> RGB, shorter side resized to 224 with bicubic sampling (longer side
    /// truncated), centred 224x224 crop, CLIP mean (0.48145466, 0.4578275, 0.40821073) / std (0.26862954, 0.26130258,
    /// 0.27577711) on 0-1 values; output <c>image_embeds</c> <c>[N,512]</c> (not normalised; the operation
    /// L2-normalises it). The int8 variant is a dynamic quantisation whose embeddings keep a cosine similarity of about
    /// 0.98 with the fp32 ones. MIT.
    /// </summary>
    public static ModelDescriptor ImageEmbedding { get; } = new()
    {
        Name = "image-embedding",
        FileName = "clip-vit-base-patch32-vision.onnx",
        Sha256 = Checksums.GetValueOrDefault("clip-vit-base-patch32-vision.onnx"),
        Int8FileName = "clip-vit-base-patch32-vision.int8.onnx",
        Int8Sha256 = Checksums.GetValueOrDefault("clip-vit-base-patch32-vision.int8.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "MIT",
        InputName = "pixel_values",
        InputShape = [1, 3, 224, 224],
        Normalization = new TensorNormalization
        {
            Mean = [0.48145466f, 0.4578275f, 0.40821073f],
            Std = [0.26862954f, 0.26130258f, 0.27577711f],
        },
        Task = ImageModelTask.Embedding,
        SizeBytes = 351_685_709,
        Provenance = "openai/clip-vit-base-patch32 vision tower + visual projection; byte-identical to the ONNX export " +
            "Xenova/clip-vit-base-patch32 onnx/vision_model.onnx (int8: onnx/vision_model_quantized.onnx) at revision " +
            "6b7c523703ae26e39be4b03774966f8571c7f523, verified against the official weights (tools/ModelExport/VisionModels/verify_clip.py).",
    };

    /// <summary>
    /// big-lama (LaMa, resolution-robust large-mask inpainting with Fourier convolutions), used by <c>Inpaint</c>.
    /// Contract: inputs <c>image</c> <c>[1,3,512,512]</c> RGB in 0-1 and <c>mask</c> <c>[1,1,512,512]</c> with 1 = hole,
    /// 0 = keep; output <c>output</c> <c>[1,3,512,512]</c> RGB in <b>0-255</b>, already composited over the input. The
    /// operation crops around each masked region, scales or pads the crop to 512x512 and blends only the hole pixels back
    /// at full resolution. Apache-2.0.
    /// </summary>
    public static ModelDescriptor Inpainting { get; } = new()
    {
        Name = "inpainting",
        FileName = "big-lama-512.onnx",
        Sha256 = Checksums.GetValueOrDefault("big-lama-512.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "image",
        InputShape = [1, 3, 512, 512],
        Normalization = TensorNormalization.Unit,
        Task = ImageModelTask.Inpainting,
        SizeBytes = 208_044_816,
        Provenance = "advimman/lama big-lama; byte-identical to Carve/LaMa-ONNX lama_fp32.onnx (torch.onnx.export, opset 17) " +
            "at revision c3c0c9e468934d62e79c329e35d82dd09ff8c444 (tools/ModelExport/VisionModels/verify_lama.py).",
    };

    /// <summary>
    /// A small NSFW / SFW image classifier (ViT-Tiny, patch 16, 384 px), used by <c>ClassifyContent</c>. Contract: input
    /// <c>pixel_values</c> <c>[N,3,384,384]</c> RGB, shorter side resized to 384 with bicubic sampling (longer side
    /// truncated), centred 384x384 crop, <c>(byte / 255 - 0.5) / 0.5</c>; output <c>logits</c> <c>[N,2]</c> over
    /// (NSFW, SFW), with the class names also stored in the model metadata under <c>labels</c>. A statistical
    /// classifier, not a guarantee: see the content-moderation remarks. Apache-2.0.
    /// </summary>
    public static ModelDescriptor ContentModeration { get; } = new()
    {
        Name = "content-moderation",
        FileName = "nsfw-vit-tiny-384.onnx",
        Sha256 = Checksums.GetValueOrDefault("nsfw-vit-tiny-384.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "pixel_values",
        InputShape = [1, 3, 384, 384],
        Normalization = TensorNormalization.Symmetric,
        Task = ImageModelTask.ContentModeration,
        SizeBytes = 22_509_796,
        Provenance = "Marqo/nsfw-image-detection-384 (timm vit_tiny_patch16_384) at revision 0c26ec22111b83f106d72a55f611ec35962bcb65, " +
            "exported from model.safetensors with torch.onnx.export, opset 17 (tools/ModelExport/VisionModels/export_moderation.py).",
    };

    /// <summary>
    /// NIMA (neural image assessment) on MobileNet v1, two heads in one graph, used by <c>AssessQuality</c>. Contract:
    /// input <c>pixel_values</c> <c>[N,3,224,224]</c> RGB, the image stretched to 224x224 with nearest-neighbour
    /// sampling (the upstream evaluation pipeline), <c>(byte / 255 - 0.5) / 0.5</c>; outputs <c>technical</c> and
    /// <c>aesthetic</c> <c>[N,10]</c>, softmax distributions over the ratings 1..10 (technical quality trained on
    /// TID2013, aesthetics on AVA). Apache-2.0.
    /// </summary>
    public static ModelDescriptor QualityAssessment { get; } = new()
    {
        Name = "quality-assessment",
        FileName = "nima-mobilenet-quality.onnx",
        Sha256 = Checksums.GetValueOrDefault("nima-mobilenet-quality.onnx"),
        BaseUrl = DefaultBaseUrl,
        License = "Apache-2.0",
        InputName = "pixel_values",
        InputShape = [1, 3, 224, 224],
        Normalization = TensorNormalization.Symmetric,
        Task = ImageModelTask.QualityAssessment,
        SizeBytes = 25_702_841,
        Provenance = "idealo/image-quality-assessment weights_mobilenet_technical_0.11.hdf5 + weights_mobilenet_aesthetic_0.07.hdf5 " +
            "(Keras 2.1.6 MobileNet layout) converted with tf2onnx, opset 17 (tools/ModelExport/VisionModels/export_nima.py).",
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
        FaceDetection,
        ImageEmbedding,
        Inpainting,
        ContentModeration,
        QualityAssessment,
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
