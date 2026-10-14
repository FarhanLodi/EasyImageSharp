# Advanced imaging and OCR recipes

These APIs are implemented for the next 1.2.0 release. Package files built locally are not a published NuGet release.

## Capability matrix

| Capability | Implementation | Boundaries |
| --- | --- | --- |
| Wide precision | Built-in 16-bit/float tensors, resize, rotation, affine/projective and page perspective sampling | Thresholding intentionally produces binary luminance; individual legacy effects may still use 8-bit intermediates. |
| Alpha | Premultiplied geometric sampling for wide pixels; existing byte arithmetic retained | Hidden RGB at zero alpha is not recoverable after premultiplication. |
| Tensor bridge | Reusable CHW/HWC RGB/BGR/grayscale; offsets/strides; fused resize/normalization; coordinate mapping | Float tensors, root frame; caller initializes unused padding; no implicit batch allocator. |
| Documents | Independent page processing, four opt-in profiles, sampled diagnostics, dHash, timings, abstention reasons | Heuristic scores are not calibrated accuracy probabilities. Sparse content can evade subsampling; hash matches are candidates, not identity proofs. |
| AI documents | Optional orientation and full-resolution BGR image-output UVDoc; explicit custom grid contracts | Models required; failures propagate. Image-output models supply no coordinate correspondence and output opaque pixels. Grid mapping supports processed-to-source only. |
| Execution | Request-local parallelism, cooperative cancellation, cumulative pixel-buffer allocation budget | In-place cancellation may leave partial changes. Codec scratch, tensors, metadata and native allocations are not covered by the pixel budget. |
| Encoded input | Stream/span byte limits, cancellation-aware async reads | Buffered decoders require encoded files below 2 GiB; a byte limit does not bound MemoryStream capacity or total process memory. |
| Incremental identify | PNG/JPEG/GIF/BMP/QOI/WebP root dimensions | Does not validate payloads or report frame counts/metadata. Other formats use full `Identify`. Stream position advances. |
| TIFF pages | Lazy page pixel decoding with aggregate limits and cycle checks | Encoded bytes still buffered in the page API. Dispose pages as consumed; use the separate region reader for large seekable files. |
| Reduced JPEG | Direct reduced IDCT at 1/2, 1/4 or 1/8 dimensions, baseline and progressive | Smaller component planes; progressive coefficient grids remain full-size. [Contract and references](reduced-jpeg-decoding.md). |
| TIFF regions | Seekable classic/BigTIFF, 64-bit offsets, selective strips/tiles and lazy ROI tiles | Chunky unsigned8/16 gray/RGB with supported compression; decoded compressed segments have explicit limits. [Supported layouts](tiff-region-decoding.md). |
| Process memory | Optional isolated worker with OS-enforced limits and deadlines | Windows committed-memory cap; Linux virtual-address cap, not RSS. [Deployment recipe](../tools/IsolatedImageWorker/README.md). |
| Color | RGB/XYZ/Lab/Lch/HSV/HSL; Bradford adaptation; explicit ICC matrix/TRC conversion | ICC v2/v4 RGB input/display profiles, relative colorimetric only. No arbitrary LUT, CMYK, perceptual intent or black-point compensation. |
| Native ICC | Optional LittleCMS RGB/CMYK/Gray/Lab/XYZ, LUT profiles, four rendering intents and BPC | Native dependency; standard profiles, not device-link/named-color workflows. [Deployment and validation](../src/EasyImageSharp.LittleCms/README.md). |
| Drawing and interop | Optional `EasyImageSharp.SkiaSharp`: copied strided float bitmap interop, paths/brushes/gradients, HarfBuzz shaping | Native deployment required. Single shaped text runs; paragraph bidi, font fallback and layout remain caller-owned. |
| Format extensions | Per-request custom decoder/encoder providers, separate from built-ins | Providers must enforce their own format-specific limits. No new bundled PDF/SVG/HEIF/AVIF rasterizer is implied. |

The managed core remains independent of ONNX, Skia and LittleCMS. See the [Skia adapter](../src/EasyImageSharp.SkiaSharp/README.md) and [AI package](../src/EasyImageSharp.AI/README.md) for native/model requirements.

## Model-compatible tensors

```csharp
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;

using var image = Image.Load<Rgb24>("receipt.png");
var resize = new ResizeOptions { Size = new Size(960, 960), Mode = ResizeMode.Pad };
float[] buffer = new float[3 * 960 * 960]; // retain per worker or rent with explicit ownership
TensorResizeMapping mapping = image.ResizeToTensor(buffer, resize,
    new TensorWriteOptions { Channels = TensorChannels.Bgr });
PointF original = mapping.ToSource(new PointF(100, 200));
```

Match a model's resampler, channel order, intermediate rounding, mean/std and padding exactly. Fusing preprocessing changes neither a model's expected input nor its coordinate convention. The byte path retains the existing resize quantization. The fused path avoids the full resized image; it still allocates kernel maps and row scratch.

EasyOCRSharp and PaddleOCRNet migrations are opt-in source integrations until the new core is released. Each sibling repository documents `EasyImageSharpSourceProject`. The detector/recognizer parity tests compare exact tensors. TrOCR's local model was unavailable; its tensor contract was tested, not real inference.

## Conservative preprocessing with diagnostics

```csharp
using var prepared = image.PreprocessDocument(new DocumentPreprocessingOptions
{
    Profile = DocumentPreprocessingProfile.NeuralOcr,
    ExecutionOptions = new ImageExecutionOptions
    {
        Configuration = new Configuration { MaxDegreeOfParallelism = 1 },
        MaxPixelBufferBytes = 256L * 1024 * 1024,
        CancellationToken = cancellationToken,
    },
});
// prepared.Image belongs to prepared; dispose the result after OCR.
foreach (var page in prepared.Pages)
    foreach (var stage in page.Stages)
        Console.WriteLine($"{stage.Name}: {stage.Reason}");
```

`NeuralOcr` and `Screenshot` preserve pixels by default. `ScannedDocument` enables orientation, deskew and binarization. `PhoneDocument` enables orientation, perspective and conditional illumination normalization. Enable destructive stages only after measuring your model on representative labeled data. Mappings use continuous coordinates with centers at `(x + 0.5, y + 0.5)`; check the `TryMap...` return value.

## Bounded uploads and independent TIFF pages

```csharp
using EasyImageSharp.Formats;

var limits = new DecoderOptions
{
    MaxEncodedBytes = 32 * 1024 * 1024,
    MaxPixels = 20_000_000,
    MaxTotalPixels = 80_000_000,
    MaxFrames = 20,
    ExecutionOptions = new ImageExecutionOptions
    {
        CancellationToken = cancellationToken,
        MaxPixelBufferBytes = 256L * 1024 * 1024,
    },
};
using var decoded = await Image.LoadAsync<Rgba32>(uploadStream, limits, cancellationToken);

foreach (var page in Image.LoadTiffPages<Rgba32>("document.tif", limits))
    using (page) { /* process and release one page before requesting the next */ }
```

For a cheap dimension check on a separate stream, call `Image.IdentifyHeaderAsync`. It consumes a bounded prefix and leaves the stream open. Rewind seekable streams or reopen before full decode. `MaxFrames` truncates page enumeration intentionally; aggregate pixel budgets reject work. The pixel-buffer budget is cumulative across allocations, so repeated resizes can exceed it even when peak live memory is smaller.

Use request-local image parallelism of one when OCR/inference already parallelizes across requests or batches, then benchmark actual concurrency. Do not change `Configuration.Default` per request.

## Color and providers

Color-space vector APIs state white points explicitly. `IccMatrixProfile.Create(profile)` validates the supported subset and rejects unsupported profiles. Conversion is opt-in; retaining an ICC tag alone never transforms pixels. Conversions to sRGB clear stale source ICC tags; attach a suitable destination profile when required.

`ImageFormat.Create` defines a provider and `DecoderOptions.AdditionalFormats` registers it for one request. A provider returns public `ImageInfo.Create(...)` header data and implements `IImageDecoder`; supplying an encoder also enables `image.Save(stream, format)`. Registration is copied and never changes a process-wide format registry. Use a separate optional assembly for native decoders or document rasterizers.

## Evidence and deployment scope

The [implementation plan](../IMPLEMENTATION_PLAN.md) records current test and benchmark results. The
[OCR evaluation artifacts](../benchmarks/ocr/results) distinguish local smoke, public-corpus diagnostics,
annotated text-region comparisons and synthetic challenges. Their provenance, script/category splits,
model fingerprints and annotation limitations are part of the result; a single aggregate is insufficient.
The [export tool](../tools/OcrCorpusExport/README.md) supports reproducible source/release comparisons.

[Platform verification](platform-verification.md) covers Windows tests/trimming, Linux x64 Native AOT,
ARM64 Native AOT under QEMU, and Linux/Windows native color and process-limit checks. Physical ARM64 and
macOS runner execution remain separate CI evidence. No production workload or arbitrary-image accuracy
guarantee follows from these checks.

Reduced JPEG and seekable TIFF region access are separate implemented APIs, with independent fixtures
and explicit format limits. They avoid full-size pixel planes/full-file buffering respectively; neither
removes every codec scratch allocation or supports every TIFF layout. Measure the
[concurrent workloads](../tools/CoreThroughput/README.md) with your own images before selecting a path.
