# EasyImageSharp

[![NuGet](https://img.shields.io/nuget/v/EasyImageSharp.svg?label=NuGet)](https://www.nuget.org/packages/EasyImageSharp)
[![CI](https://github.com/FarhanLodi/EasyImageSharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/FarhanLodi/EasyImageSharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE)

**A complete 2D imaging library for .NET, written entirely in C#.** Ten codecs, a fluent processing
pipeline, EXIF metadata, document/OCR operators, and image-to-tensor bridges for ONNX — with no native
binaries, no licence key, and no commercial tier. MIT, forever.

```bash
dotnet add package EasyImageSharp
```

```csharp
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");

image.Mutate(ctx => ctx.AutoOrient().Resize(800, 0));

image.SaveAsWebp("thumbnail.webp");
```

## Why EasyImageSharp

| | |
|---|---|
| **Fully managed** | No native dependencies. Ships in Native AOT, trimmed, single-file, Alpine, ARM64 and WebAssembly builds without per-platform asset packages. |
| **MIT, permanently** | No revenue threshold, no build-time licence key, no separate commercial tier. It passes corporate OSS policy scans as-is. |
| **Fast** | A 6-megapixel JPEG decodes in 85 ms and half-resizes in 15 ms, with pooled buffers and row-parallel execution throughout. See [Performance](#performance). |
| **Safe with untrusted input** | Per-frame pixel and frame limits enforced *before* allocation, a documented exception contract, ~150 crafted corrupt-input tests and a seeded fuzz pass on every build. |
| **Document- and AI-oriented** | Sauvola/Niblack binarisation, deskew, page detection, morphology and tensor bridges are in the box — not glued together from three libraries. |

## Contents

[Install](#install) · [Quick start](#quick-start) · [Common tasks](#common-tasks) ·
[Format support](#format-support) · [Processing](#processing-operations) · [Metadata](#metadata) ·
[Untrusted input](#untrusted-input) · [Performance](#performance) · [AI and ONNX](#ai-and-onnx) ·
[Packages](#packages) · [Production notes](#production-notes)

## Install

```bash
dotnet add package EasyImageSharp          # the core library
dotnet add package EasyImageSharp.AI       # optional: ONNX-powered operations
```

Targets **net8.0** and **net10.0**.

## Quick start

```csharp
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

// The format is detected from the bytes; the file extension is not consulted.
using Image<Rgb24> image = Image.Load<Rgb24>("input.png");

Console.WriteLine($"{image.Width}x{image.Height}");

// Mutate edits in place. Clone returns a new image and leaves the source untouched.
image.Mutate(ctx => ctx.Resize(400, 0).Grayscale());

image.SaveAsJpeg("output.jpg");
```

`Image.Load(...)` without a type argument decodes to `Rgba32`. Every load and save has async and
stream overloads: `Image.LoadAsync`, `image.SaveAsPngAsync(stream)`, and so on.

## Common tasks

<details open>
<summary><b>Make a thumbnail, safely</b></summary>

```csharp
using EasyImageSharp.Formats;

// Reject absurd images up front: the header is parsed, nothing is allocated.
var options = new DecoderOptions { MaxPixels = 50_000_000 };

using Image<Rgba32> image = Image.Load<Rgba32>(uploadedBytes, options);

using Image<Rgba32> thumb = image.Clone(ctx => ctx.Resize(new ResizeOptions
{
    Size = new Size(320, 320),
    Mode = ResizeMode.Crop,          // fill the box and centre-crop the overflow
    Sampler = KnownResamplers.Lanczos3,
}));

thumb.SaveAsWebp("thumb.webp", new WebpEncoder { Quality = 82 });
```
</details>

<details>
<summary><b>Handle a web upload</b></summary>

```csharp
// Look before you decode: Identify parses only the header and is never size-limited.
ImageInfo info = await Image.IdentifyAsync(stream);
if ((long)info.Width * info.Height > 40_000_000)
{
    return Results.BadRequest($"{info.Width}x{info.Height} is too large.");
}

stream.Position = 0;
try
{
    using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(stream);
    image.Mutate(ctx => ctx.AutoOrient());   // phone photos arrive rotated
    // ...
}
catch (ImageFormatException ex)              // unknown, malformed and oversized
{
    return Results.BadRequest(ex.Message);
}
```
</details>

<details>
<summary><b>Prepare a scanned page for OCR</b></summary>

```csharp
using Image<Rgb24> page = Image.Load<Rgb24>("scan.jpg");

page.Mutate(ctx => ctx
    .BackgroundNormalize(40)     // flatten uneven lighting
    .Deskew()                    // straighten (projection profile, ±15°)
    .MedianBlur(1)               // remove speckle
    .SauvolaThreshold());        // document-grade binarisation

page.SaveAsPng("clean.png");

// Or the whole pipeline as one preset:
page.Mutate(ctx => ctx.PrepareForOcr());
```
</details>

<details>
<summary><b>Straighten a photographed document</b></summary>

```csharp
using Image<Rgb24> photo = Image.Load<Rgb24>("desk-photo.jpg");

PointF[]? quad = photo.DetectPage();
if (quad is not null)
{
    photo.Mutate(ctx => ctx.CorrectPerspective(quad));
}
```
</details>

<details>
<summary><b>Draw OCR bounding boxes</b></summary>

```csharp
image.Mutate(ctx =>
{
    foreach (var (box, text) in results)
    {
        ctx.DrawRectangle(Color.Lime, 2f, box);
        ctx.DrawLabel(text, Color.Black, Color.Lime, box);
    }
});
```
</details>

<details>
<summary><b>Read every page of a TIFF or frame of a GIF</b></summary>

```csharp
using Image<Rgb24> document = Image.Load<Rgb24>("fax.tif");

for (int i = 0; i < document.Frames.Count; i++)
{
    using Image<Rgb24> page = document.Frames.CloneFrame(i);
    page.SaveAsPng($"page-{i}.png");
}
```

Animated GIF and WebP frames arrive fully composited, with disposal and blending already applied.
</details>

<details>
<summary><b>Work with raw pixels efficiently</b></summary>

```csharp
image.ProcessPixelRows(accessor =>
{
    for (int y = 0; y < accessor.Height; y++)
    {
        Span<Rgb24> row = accessor.GetRowSpan(y);
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = new Rgb24(row[x].B, row[x].G, row[x].R);
        }
    }
});
```

The `image[x, y]` indexer is convenient but bounds-checks every access; use `ProcessPixelRows` in hot loops.
</details>

## Format support

| | PNG | JPEG | WebP | GIF | BMP | TIFF | TGA | Netpbm | QOI | ICO |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| **Decode** | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| **Encode** | ● | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| **Animation** | | | ● | ● | | | | | | |
| **Multi-page** | | | | | | ● | | | | ● |

<details>
<summary><b>Full per-format detail</b></summary>

| Format | Decode | Encode |
|--------|--------|--------|
| **PNG** | All colour types, bit depths 1/2/4/8/16, Adam7 interlacing, palette and colour-key (`tRNS`) transparency | All colour types, bit depths 1/2/4/8/16, palette output via quantisation, Adam7 interlacing, selectable filtering |
| **JPEG** | Baseline, extended sequential and progressive (SOF0/1/2 with successive approximation), all chroma subsampling with triangle upsampling for 4:2:2 and 4:2:0, restart markers, grayscale / YCbCr / RGB / Adobe CMYK / YCCK | Baseline and progressive, quality 1–100, 4:4:4 / 4:2:2 / 4:2:0 / 4:1:1 / 4:1:0, grayscale, RGB, CMYK, YCCK, optimised Huffman tables, restart intervals |
| **WebP** | Lossy (VP8), lossless (VP8L), alpha (ALPH), animation (ANIM/ANMF) with offsets, blending and disposal | Lossy and lossless, near-lossless, alpha, animation, quality and effort levels |
| **GIF** | GIF87a/89a, global and local palettes, interlacing, transparency, animation with disposal methods | LZW, global or per-frame palettes, transparency, animation with delays and loop count |
| **BMP** | 1/4/8-bit palette, 16/24/32-bit, `BI_BITFIELDS` and alpha bitfields, RLE8/RLE4, OS/2 `BITMAPCOREHEADER`, both row orders | 1/4/8-bit palette, 16-bit, 24-bit, 32-bit with alpha |
| **TIFF** | Multi-page, both byte orders, strips and tiles, chunky and planar, None / LZW / Deflate / PackBits / CCITT G3 & G4 / JPEG, horizontal predictor, 1–32-bit samples (unsigned, signed, floating point), WhiteIsZero / BlackIsZero / palette / RGB(A) / CMYK / YCbCr / CIELab | Multi-page, None / LZW / Deflate / PackBits / CCITT G3 & G4, selectable bit depth, photometric and predictor |
| **TGA** | Types 1/2/3 and their RLE variants, 8/15/16/24/32-bit, colour maps, either origin | 8/16/24/32-bit, raw or run-length |
| **Netpbm** | P1–P6 (ASCII and binary) plus P7 PAM, 8- and 16-bit | PBM / PGM / PPM, plain or binary |
| **QOI** | Full specification | Byte-identical to the reference encoder |
| **ICO / CUR** | Multi-image icons with embedded BMP or PNG entries | PNG or 32-bit BMP entries, cursors with hotspots |

Not implemented: arithmetic-coded, lossless and 12-bit JPEG; old-style JPEG-in-TIFF (compression 6);
JBIG. These raise `NotSupportedException` with a clear message rather than failing obscurely.
</details>

**How the codecs are verified.** Round-trip tests, plus a corpus of independently-encoded fixtures with
pixel-exact ground truth, so decode paths this library's own encoders never produce are still exercised.
JPEG decoding is checked against a reference decoder at ≥61 dB PSNR; WebP output — lossy included —
decodes to *byte-identical* pixels in the reference decoder; QOI output is byte-identical to the
reference encoder.

## Processing operations

Every operation is available on the `IImageProcessingContext` passed to `Mutate` and `Clone`.

| Group | Operations |
|-------|------------|
| **Geometry** | `Resize` (Stretch / Max / Min / Pad / Crop / BoxPad / Manual, anchor positions, 15 samplers, optional linear-light and premultiplied-alpha resampling), `Crop`, `EntropyCrop`, `Pad`, `Rotate`, `Flip`, `RotateFlip`, `Skew`, `Transform` (affine and projective builders, taper, quad distortion) |
| **Colour** | `Grayscale` (Bt709/Bt601), `BlackWhite`, `Invert`, `Brightness`, `Contrast`, `Hue`, `Saturate`, `Lightness`, `Opacity`, `Filter(ColorMatrix)`, `KnownFilterMatrices` (Sepia, Kodachrome, Lomograph, Polaroid, eight colour-blindness simulations), `BackgroundColor` |
| **Filters** | `GaussianBlur`, `GaussianSharpen`, `BoxBlur`, `BokehBlur`, `MedianBlur`, `DetectEdges` (Sobel, Scharr, Prewitt, Kirsch, Robinson, Laplacian, LoG, RobertsCross, Kayyali), `Convolve`, `OilPaint`, `Pixelate`, `Vignette`, `Glow`, `Swizzle`, `HistogramEqualization` (global, CLAHE, sliding window) |
| **Thresholding** | `BinaryThreshold`, `OtsuThreshold`, `SauvolaThreshold`, `AdaptiveThreshold` (Bradley), `NiblackThreshold`, `WolfJolionThreshold`, `PhansalkarThreshold`, `NickThreshold`, `Binarize` (auto-selecting) |
| **Document** | `Deskew` / `DetectSkew` (projection profile or Hough), `DetectOrientation`, `AutoRotateDocument`, `DetectPage`, `CorrectPerspective`, `AutoCropPage`, `BackgroundNormalize`, `RemoveShadows`, `ContrastStretch`, `AutoLevels`, `Gamma`, morphology (`Erode`, `Dilate`, `Open`, `Close`, `TopHat`, `BlackHat`, `Thin`, `Despeckle`), connected components (`RemoveSmallObjects`, `KeepLargestComponent`, `FillHoles`), `RemoveLines`, `RemoveBorders`, `RemoveHolePunches`, `SegmentTextLines`, `SegmentWords`, `NormalizeDpi`, `PrepareForOcr` |
| **Quantisation** | `Quantize` (Wu, Octree, WebSafe, fixed palette), `Dither` and `BinaryDither` with 15 error-diffusion and ordered kernels |
| **Compositing** | `DrawImage` with 20 blend modes and 12 Porter-Duff alpha composition modes, source rectangles, opacity |
| **Drawing** | `FillRectangle`, `DrawRectangle`, `DrawLine`, `DrawPolygon`, `FillPolygon`, `DrawEllipse`, `FillEllipse`, `DrawCircle`, `FillCircle`, `DrawText`, `DrawLabel`, `DrawBoundingBoxes` (embedded bitmap font — no font engine required) |
| **Metadata** | `AutoOrient` |

**Pixel formats.** `Rgb24`, `Rgba32`, `Bgr24`, `Bgra32`, `L8`, plus high-precision `Rgb48`, `Rgba64`,
`L16`, `La16`, `La32`, `A8`, `Argb32`, `Abgr32` and `RgbaVector`. Conversions between high-precision
formats keep full precision, and 16-bit PNG and TIFF samples decode at full width into a format that
can hold them.

**Primitives.** `Color` (CSS named colours, `Color.Parse("#336699")`), `Point`, `Size`, `Rectangle`
and their `PointF` / `SizeF` / `RectangleF` counterparts.

## Metadata

```csharp
using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");

if (image.Metadata.ExifProfile is { } exif &&
    exif.TryGetValue(ExifTag.DateTimeOriginal, out var taken))
{
    Console.WriteLine(taken.Value);
}

Console.WriteLine($"{image.Metadata.HorizontalResolution} DPI");

image.Mutate(ctx => ctx.AutoOrient());   // apply EXIF orientation, then reset the tag
image.SaveAsJpeg("out.jpg");             // EXIF, ICC and XMP are carried across
```

EXIF is read and written for JPEG, PNG and TIFF, with typed access to about 60 well-known tags and
lossless round-tripping of everything else. ICC and XMP profiles pass through untouched. Resolution
and per-frame metadata (GIF delays and disposal, TIFF page tags) are preserved.

EXIF orientation is never applied silently — call `AutoOrient()` when you want it.

## Untrusted input

Decoding attacker-controlled bytes is the risky part of any imaging library. Three things protect you:

```csharp
var options = new DecoderOptions
{
    MaxPixels = 50_000_000,   // per frame; default 256 MP
    MaxFrames = 32,           // e.g. TIFF pages; default unlimited
};

using Image<Rgb24> image = Image.Load<Rgb24>(bytes, options);
```

1. **Limits apply before allocation.** The header is parsed, the declared size is checked, and only
   then is pixel memory allocated. A 200-byte file claiming 65535×65535 is rejected in microseconds.
2. **`Identify` is never limited**, so you can always inspect dimensions before committing to a decode.
3. **A closed exception contract:**

| Situation | Exception |
|---|---|
| Bytes match no known format | `UnknownImageFormatException` |
| Malformed, truncated or internally inconsistent data | `InvalidImageContentException` |
| A recognised feature this library does not implement | `NotSupportedException` |
| Declared size exceeds `DecoderOptions` | `ImageSizeLimitExceededException` |
| Encoding something the format cannot represent | `NotSupportedException` |

The first, second and fourth derive from `ImageFormatException`, so `catch (ImageFormatException)`
covers all bad input. Framework exceptions such as `IndexOutOfRangeException` never escape a decoder —
enforced by ~150 crafted corrupt-input tests and a seeded byte-mutation fuzz pass that runs on every build.

## Performance

Measured with BenchmarkDotNet on a 6-core Ryzen 5 4600H, .NET 10. Full tables in
[`benchmarks/results/`](https://github.com/FarhanLodi/EasyImageSharp/tree/main/benchmarks/results).

| Operation | Input | Time | Allocated |
|---|---|---:|---:|
| JPEG decode | 3032×2008 → Rgba32 | **85.2 ms** | 41.4 MB |
| PNG decode | 3032×2008 → Rgba32 | **89.4 ms** | 25.9 MB |
| Resize, bicubic ×0.5 | 3032×2008 Rgba32 | **14.9 ms** | 7.4 MB |
| Resize, bicubic ×0.5 | 3032×2008 L8 | **5.1 ms** | 1.9 MB |
| Grayscale, in place | A4 at 300 DPI, L8 | **3.2 ms** | 4.8 KB |
| Otsu threshold, in place | A4 at 300 DPI, L8 | **8.0 ms** | 268 KB |
| Load → resize → save | 20 JPEGs | **19.6 ms each** = 51 img/s | 9.4 MB |

Hot paths use SIMD pixel kernels, pooled scratch buffers and copy-on-write cloning, so a resize
allocates the destination and little else. Operations parallelise across rows by default; for
deterministic single-threaded behaviour:

```csharp
Configuration.Default.MaxDegreeOfParallelism = 1;
```

Honest caveat: PNG decode is dominated by the runtime's own `ZLibStream` inflating the IDAT data, not
by this library's code, so it benefits far less from the surrounding optimisation than JPEG does.
Going faster there needs a managed inflate implementation, which is on the roadmap.

## AI and ONNX

The core package includes tensor bridges, so any ONNX Runtime model is a few lines away:

```csharp
using EasyImageSharp.Tensors;

float[] chw = image.ToChwTensor(
    channelMean: [0.485f, 0.456f, 0.406f],
    channelStd:  [0.229f, 0.224f, 0.225f]);

var input = new DenseTensor<float>(chw, [1, 3, image.Height, image.Width]);
// ... session.Run(...) ...
using Image<Rgb24> result = TensorImage.FromChwTensor<Rgb24>(output, width, height);
```

`ToHwcTensor` gives interleaved `[H, W, 3]`, `ToGrayscaleTensor` gives `[H, W]` luminance, and
`FromGrayscaleTensor` builds an image from single-channel output.

The optional **`EasyImageSharp.AI`** package adds ready-made operations backed by a checksum-verified
model hub:

```csharp
using EasyImageSharp.AI;

using var ai = new ImageAiSession(new ImageAiOptions { ExecutionProvider = ExecutionProvider.Auto });

using Image<Rgb24> page = Image.Load<Rgb24>("phone-photo.jpg");

page.AutoOrient(ai);         // 0/90/180/270 classifier
page.DewarpDocument(ai);     // flatten a curled page
page.DenoiseAI(ai);          // learned denoiser
page.Mutate(ctx => ctx.SauvolaThreshold());
```

Models download on first use, are verified against pinned SHA-256 checksums, cached locally, and can
be pre-seeded for air-gapped deployment. See the
[package README](https://github.com/FarhanLodi/EasyImageSharp/blob/main/src/EasyImageSharp.AI/README.md).

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| **`EasyImageSharp`** | Codecs, `Image<TPixel>`, processing pipeline, document operators, drawing, metadata, pixel formats, tensor bridges | None beyond the framework |
| **`EasyImageSharp.AI`** | ONNX-powered orientation, dewarp, super-resolution, denoise, background removal, learned binarisation; model hub | `Microsoft.ML.OnnxRuntime` |

**Dependency policy.** The core package uses framework APIs only. It may take free, managed,
permissively-licensed dependencies where they add real value; native binaries are confined to optional
add-on packages; paid, split-licensed and copyleft dependencies are never taken.

## Production notes

**Target frameworks.** `net8.0` and `net10.0`. There is deliberately no `netstandard` target: the pixel
abstraction uses static abstract interface members, which require .NET 7 or later. Both targets are
AOT- and trimming-compatible (`IsAotCompatible=true`) with no conditional compilation.

**Thread safety.** A single `Image<TPixel>` is **not** thread-safe — do not mutate one from several
threads without synchronising. Decoding, encoding and processing *different* images concurrently is
fully supported, and is what the parallel row execution is built for.

**Memory.** `Image<TPixel>` owns its pixel buffer and must be disposed — use `using`. After disposal,
every pixel-touching member throws `ObjectDisposedException` rather than reading freed state.

**Versioning.** Semantic versioning. Breaking changes are confined to major releases and listed in
[CHANGELOG.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CHANGELOG.md).

## Building from source

Requires the .NET 10 SDK; running the `net8.0` test leg also needs the .NET 8 runtime.

```bash
git clone https://github.com/FarhanLodi/EasyImageSharp.git
cd EasyImageSharp

dotnet build EasyImageSharp.slnx -c Release
dotnet test  EasyImageSharp.slnx -c Release
dotnet pack  src/EasyImageSharp  -c Release -o artifacts
```

CI builds and tests on Ubuntu, Windows and macOS across both target frameworks, validates the packages,
and runs a Native AOT publish. Tagging `vX.Y.Z` packs and publishes to NuGet.

Contributions are welcome — see
[CONTRIBUTING.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CONTRIBUTING.md).

## License

[MIT](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE) — Copyright © 2026 Farhan Lodi.
