<div align="center">

![EasyImageSharp](https://raw.githubusercontent.com/FarhanLodi/EasyImageSharp/main/src/EasyImageSharp/Assets/icon.png)

# EasyImageSharp

**A complete 2D imaging library for .NET, written entirely in managed C#.**

Ten codecs, a fluent processing pipeline, EXIF metadata, a document-imaging toolkit and ONNX tensor
bridges — in one assembly, with no native dependencies and no licence key.

[![NuGet](https://img.shields.io/nuget/v/EasyImageSharp.svg?label=NuGet)](https://www.nuget.org/packages/EasyImageSharp)
[![NuGet downloads](https://img.shields.io/nuget/dt/EasyImageSharp.svg?label=downloads)](https://www.nuget.org/packages/EasyImageSharp)
[![CI](https://github.com/FarhanLodi/EasyImageSharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/FarhanLodi/EasyImageSharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

</div>

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

## Highlights

- **Ten codecs, decode and encode.** PNG, JPEG (baseline, progressive, CMYK), WebP (lossy, lossless,
  animated), GIF, BMP, TIFF (multi-page, CCITT G3/G4, JPEG-in-TIFF), TGA, Netpbm, QOI and ICO/CUR.
- **One managed assembly.** No native binaries and no per-architecture packages, so the same package
  publishes to Native AOT, trimmed, single-file, Alpine and ARM64 targets without a RID matrix.
- **A fluent pipeline.** Resize with 16 resamplers, crop, rotate, affine and projective transforms,
  colour matrices, convolution, blur and sharpen, edge detection, histogram equalisation and CLAHE,
  quantisation and dithering, 18 blend modes, and annotation drawing.
- **Document imaging built in.** Otsu, Sauvola, Niblack, Wolf-Jolion, Phansalkar, NICK and adaptive
  thresholding; deskew; page detection and perspective correction; illumination correction; morphology;
  connected components; text-line and word segmentation; and a one-call `PrepareForOcr()` preset.
- **Metadata that survives.** EXIF read and write with typed access, ICC and XMP passthrough, and
  `AutoOrient()`.
- **Hardened against untrusted input.** Size limits enforced before allocation, a closed exception
  contract, around 150 corrupt-input tests and a fuzz pass on every build.
- **Fast.** SIMD kernels, pooled buffers, copy-on-write clones and row-parallel execution: a 3032×2008
  JPEG decodes in 85 ms and half-resizes in 15 ms.
- **ONNX-ready.** Image-to-tensor bridges in the core, plus an optional `EasyImageSharp.AI` package with
  six pre-wired models and a checksum-verified model hub.
- **MIT, permanently.** No revenue threshold, no commercial tier, no licence key.

## Contents

[Why](#why-easyimagesharp) · [Install](#install) · [Getting started](#getting-started) ·
[Recipes](#recipes) · [Formats](#format-support) · [Processing](#processing) · [Metadata](#metadata) ·
[Untrusted input](#working-with-untrusted-input) · [Performance](#performance) ·
[AI](#ai-operations) · [Packages](#packages) · [Deployment](#deployment-notes) ·
[Verification](#how-it-is-verified) · [Building](#building-from-source) · [Community](#community)

## Why EasyImageSharp

**Everything in one managed assembly.** Ten codecs, the processing pipeline, EXIF, document imaging
and drawing live in a single DLL under 1 MB with no dependency beyond the framework. There are no native
binaries, no per-architecture asset packages and no platform-specific build steps. A container image
does not grow by tens of megabytes of native payload, and a deployment does not fail at run time because
a shared object was missing for one architecture.

**MIT, with no conditions attached.** No revenue threshold, no build-time licence key, no separate
commercial tier, no distinction between open- and closed-source use. The licence that applies to a hobby
project is the licence that applies to a company, permanently. Compliance review is one line.

**Document imaging is a first-class citizen.** Most imaging libraries stop at resize, crop and filters,
so a document pipeline ends up gluing a general imaging library to a computer-vision toolkit. Here,
`SauvolaThreshold`, `Deskew`, `DetectPage`, `CorrectPerspective`, morphology, connected components,
illumination correction and text-line segmentation are ordinary operations on the same `Mutate` pipeline
as `Resize` — one dependency, one pixel type, no conversion layer.

```csharp
page.Mutate(ctx => ctx.BackgroundNormalize(40).Deskew().SauvolaThreshold());
```

**Designed for ONNX from the start.** `ToChwTensor` and `FromChwTensor` handle layout and normalisation
for any model you bring, and the optional [`EasyImageSharp.AI`](#ai-operations) package adds six
ready-made operations backed by a checksum-verified model hub. Classical and learned methods compose in
the same pipeline.

## Install

```bash
dotnet add package EasyImageSharp          # core library
dotnet add package EasyImageSharp.AI       # optional ONNX-powered operations
```

Targets **.NET 8.0** and **.NET 10.0**.

## Getting started

```csharp
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

// The format is detected from the bytes, never from the file extension.
using Image<Rgb24> image = Image.Load<Rgb24>("input.png");
Console.WriteLine($"{image.Width}x{image.Height} {image.Metadata.DecodedImageFormat?.Name}");

// Mutate edits in place; Clone returns a new image and leaves the source untouched.
image.Mutate(ctx => ctx.Resize(400, 0).Grayscale());
using Image<Rgb24> small = image.Clone(ctx => ctx.Resize(100, 0));

image.SaveAsJpeg("output.jpg");
await small.SaveAsync("small.png");   // format chosen from the extension
```

- `Image.Load(...)` without a type argument decodes to `Rgba32`.
- Load from a path, stream or byte span; save to a path or stream. `LoadAsync`, `SaveAsync` and the
  `SaveAs…Async` family take a `CancellationToken`.
- A zero width or height in `Resize` preserves the aspect ratio.
- Images own their pixel buffers and implement `IDisposable` — always use `using`.

## Recipes

### Thumbnails with resource limits

```csharp
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;

var options = new DecoderOptions { MaxPixels = 50_000_000 };
using Image<Rgba32> image = Image.Load<Rgba32>(uploadedBytes, options);

using Image<Rgba32> thumb = image.Clone(ctx => ctx.Resize(new ResizeOptions
{
    Size = new Size(320, 320),
    Mode = ResizeMode.Crop,
    Sampler = KnownResamplers.Lanczos3,
}));

thumb.SaveAsWebp("thumb.webp", new WebpEncoder { Quality = 82 });
```

### Validating an untrusted upload

```csharp
// Identify parses only the header and is never size-limited, so check the declared
// dimensions before committing to a decode.
ImageInfo info = await Image.IdentifyAsync(stream);
if ((long)info.Width * info.Height > 40_000_000)
{
    throw new InvalidDataException($"{info.Width}x{info.Height} exceeds the supported size.");
}

stream.Position = 0;
try
{
    using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(stream);
    image.Mutate(ctx => ctx.AutoOrient());
    image.SaveAsJpeg("normalised.jpg");
}
catch (ImageFormatException ex)   // unknown format, malformed data, or a size limit exceeded
{
    Console.Error.WriteLine(ex.Message);
}
```

### Re-encoding with options

```csharp
using System.IO.Compression;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;

using Image<Rgba32> image = Image.Load<Rgba32>("input.tif");

image.SaveAsJpeg("out.jpg", new JpegEncoder { Quality = 90, Progressive = true });
image.SaveAsPng("out.png", new PngEncoder { CompressionLevel = CompressionLevel.SmallestSize });
```

### Preparing a scan for OCR

```csharp
using Image<Rgb24> page = Image.Load<Rgb24>("scan.jpg");

page.Mutate(ctx => ctx
    .BackgroundNormalize(40)   // flatten uneven illumination
    .Deskew()                  // projection-profile straightening
    .MedianBlur(1)             // remove speckle
    .SauvolaThreshold());      // document-grade binarisation

page.SaveAsPng("clean.png");

// The same steps as a single preset:
page.Mutate(ctx => ctx.PrepareForOcr());
```

### Rectifying a photographed document

```csharp
using Image<Rgb24> photo = Image.Load<Rgb24>("desk-photo.jpg");

if (photo.DetectPage() is { } quad)
{
    photo.Mutate(ctx => ctx.CorrectPerspective(quad));
}
```

### Annotating detection results

```csharp
image.Mutate(ctx =>
{
    foreach (var (box, label) in detections)
    {
        ctx.DrawRectangle(Color.Lime, 2f, box);
        ctx.DrawLabel(label, Color.Black, Color.Lime, box);
    }
});
```

### Pages and frames

```csharp
using Image<Rgb24> document = Image.Load<Rgb24>("fax.tif");

for (int i = 0; i < document.Frames.Count; i++)
{
    using Image<Rgb24> page = document.Frames.CloneFrame(i);
    page.SaveAsPng($"page-{i:D3}.png");
}
```

Animated GIF and WebP frames are delivered fully composited, with disposal and blending applied.

### Fast pixel access

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

The `image[x, y]` indexer bounds-checks every access; prefer `ProcessPixelRows` in hot paths.

## Format support

| | PNG | JPEG | WebP | GIF | BMP | TIFF | TGA | PNM | QOI | ICO |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| Decode | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Encode | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Animation | | | ✔ | ✔ | | | | | | |
| Multi-page | | | | | | ✔ | | | | ✔ |

| Format | Decode | Encode |
|---|---|---|
| **PNG** | All colour types, bit depths 1/2/4/8/16, Adam7 interlacing, palette and colour-key transparency | All colour types and bit depths, palette output via quantisation, Adam7, selectable filtering |
| **JPEG** | Baseline, extended sequential and progressive; all chroma subsampling with triangle upsampling for 4:2:2 and 4:2:0; restart markers; grayscale, YCbCr, RGB, Adobe CMYK and YCCK | Baseline and progressive, quality 1–100, 4:4:4 / 4:2:2 / 4:2:0 / 4:1:1 / 4:1:0, grayscale, RGB, CMYK, YCCK, optimised Huffman tables, restart intervals |
| **WebP** | Lossy (VP8), lossless (VP8L), alpha, animation with offsets, blending and disposal | Lossy and lossless, near-lossless, alpha, animation, quality and effort levels |
| **GIF** | GIF87a/89a, global and local palettes, interlacing, transparency, animation with disposal | LZW, global or per-frame palettes, transparency, delays, loop count |
| **BMP** | 1/4/8-bit palette, 16/24/32-bit, bitfields and alpha bitfields, RLE8/RLE4, OS/2 headers, both row orders | 1/4/8-bit palette, 16-bit, 24-bit, 32-bit with alpha |
| **TIFF** | Multi-page, both byte orders, strips and tiles, chunky and planar, None / LZW / Deflate / PackBits / CCITT G3 & G4 / JPEG, horizontal predictor, 1–32-bit samples (unsigned, signed, floating point), WhiteIsZero / BlackIsZero / palette / RGB(A) / CMYK / YCbCr / CIELab | Multi-page, None / LZW / Deflate / PackBits / CCITT G3 & G4, selectable bit depth, photometric and predictor |
| **TGA** | Types 1/2/3 and RLE variants, 8/15/16/24/32-bit, colour maps, either origin | 8/16/24/32-bit, raw or run-length |
| **PNM** | P1–P6 (ASCII and binary) and P7 PAM, 8- and 16-bit | PBM / PGM / PPM, plain or binary |
| **QOI** | Full specification | Byte-identical to the reference encoder |
| **ICO / CUR** | Multi-image icons with embedded BMP or PNG entries | PNG or 32-bit BMP entries, cursors with hotspots |

**Not implemented**, and reported as `NotSupportedException` with a message naming the feature:
arithmetic-coded, lossless and 12-bit JPEG; old-style JPEG-in-TIFF (compression 6); JBIG. HEIC/HEIF is
not planned (patent-encumbered), and AVIF would only ever ship as an opt-in add-on.

## Processing

Every operation is available on the `IImageProcessingContext` passed to `Mutate` and `Clone`.

| Category | Operations |
|---|---|
| **Geometry** | `Resize` (Stretch / Max / Min / Pad / Crop / BoxPad / Manual, anchor positions, 16 resamplers, optional linear-light and premultiplied-alpha resampling), `Crop`, `EntropyCrop`, `Pad`, `Rotate`, `Flip`, `RotateFlip`, `Skew`, `Transform` (affine and projective builders, taper, quad distortion) |
| **Colour** | `Grayscale`, `BlackWhite`, `Invert`, `Brightness`, `Contrast`, `Hue`, `Saturate`, `Lightness`, `Opacity`, `Filter(ColorMatrix)`, `KnownFilterMatrices` (including eight colour-blindness simulations), `BackgroundColor` |
| **Filters** | `GaussianBlur`, `GaussianSharpen`, `BoxBlur`, `BokehBlur`, `MedianBlur`, `DetectEdges` (10 kernels), `Convolve`, `OilPaint`, `Pixelate`, `Vignette`, `Glow`, `Swizzle`, `HistogramEqualization` (global, CLAHE, sliding window) |
| **Thresholding** | `BinaryThreshold`, `OtsuThreshold`, `SauvolaThreshold`, `AdaptiveThreshold`, `NiblackThreshold`, `WolfJolionThreshold`, `PhansalkarThreshold`, `NickThreshold`, and an auto-selecting `Binarize` |
| **Document** | `Deskew`, `DetectSkew`, `DetectOrientation`, `AutoRotateDocument`, `DetectPage`, `CorrectPerspective`, `AutoCropPage`, `BackgroundNormalize`, `RemoveShadows`, `ContrastStretch`, `AutoLevels`, `Gamma`, morphology (erode, dilate, open, close, top-hat, black-hat, thin, despeckle), connected components (`RemoveSmallObjects`, `KeepLargestComponent`, `FillHoles`), `RemoveLines`, `RemoveBorders`, `RemoveHolePunches`, `SegmentTextLines`, `SegmentWords`, `NormalizeDpi`, `PrepareForOcr` |
| **Quantisation** | `Quantize` (Wu, Octree, WebSafe, fixed palette), `Dither` and `BinaryDither` with 14 kernels |
| **Compositing** | `DrawImage` with 18 blend modes and 12 Porter-Duff alpha composition modes |
| **Drawing** | Rectangles, lines, polygons, ellipses, circles, `DrawText` and `DrawLabel` with an embedded bitmap font, `DrawBoundingBoxes` |

**Pixel formats.** `Rgb24`, `Rgba32`, `Bgr24`, `Bgra32` and `L8`, plus the high-precision `Rgb48`,
`Rgba64`, `L16`, `La16`, `La32`, `A8`, `Argb32`, `Abgr32` and `RgbaVector`. Conversions between
high-precision formats keep full precision, and 16-bit PNG and TIFF samples decode at full width.

**Parallelism.** Operations run row-parallel by default. For deterministic single-threaded execution:

```csharp
Configuration.Default.MaxDegreeOfParallelism = 1;
```

## Metadata

```csharp
using EasyImageSharp.Metadata.Exif;

using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");

if (image.Metadata.ExifProfile is { } exif &&
    exif.TryGetValue(ExifTag.DateTimeOriginal, out var taken))
{
    Console.WriteLine(taken.Value);
}

Console.WriteLine($"{image.Metadata.HorizontalResolution} DPI");

image.Mutate(ctx => ctx.AutoOrient());   // apply EXIF orientation and reset the tag
image.SaveAsJpeg("out.jpg");             // EXIF, ICC and XMP are preserved
```

EXIF is read and written for JPEG, PNG and TIFF, with typed access to around 60 well-known tags and
lossless round-tripping of the rest. ICC and XMP profiles pass through unmodified. Resolution and
per-frame metadata are preserved. EXIF orientation is never applied implicitly.

## Working with untrusted input

Decoding attacker-supplied bytes is the primary attack surface of any imaging library.

```csharp
var options = new DecoderOptions
{
    MaxPixels = 50_000_000,   // per frame; default 256 MP
    MaxFrames = 32,           // e.g. TIFF pages; default unlimited
};

using Image<Rgb24> image = Image.Load<Rgb24>(bytes, options);
```

**Limits are enforced before allocation.** The header is parsed and the declared size validated before
any pixel buffer is allocated, so a small file declaring enormous dimensions is rejected in microseconds.
`Identify` is never limited, so callers can inspect dimensions before committing to a decode.

**The exception contract is closed.** Framework exceptions never escape a decoder on malformed input.

| Condition | Exception |
|---|---|
| Bytes match no known format | `UnknownImageFormatException` |
| Malformed, truncated or internally inconsistent data | `InvalidImageContentException` |
| Declared size exceeds `DecoderOptions` | `ImageSizeLimitExceededException` |
| Recognised feature that is not implemented | `NotSupportedException` |
| Format cannot represent the image being encoded | `NotSupportedException` |

The first three derive from `ImageFormatException`, so one catch covers every kind of invalid input.

Vulnerability reports: see [SECURITY.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/SECURITY.md).

## Performance

BenchmarkDotNet, 6-core Ryzen 5 4600H, .NET 10, Release.

| Operation | Input | Time | Allocated |
|---|---|---:|---:|
| JPEG decode | 3032×2008 → Rgba32 | 85.2 ms | 41.4 MB |
| PNG decode | 3032×2008 → Rgba32 | 89.4 ms | 25.9 MB |
| Resize, bicubic ×0.5 | 3032×2008 Rgba32 | 14.9 ms | 7.4 MB |
| Resize, bicubic ×0.5 | 3032×2008 L8 | 5.1 ms | 1.9 MB |
| Grayscale, in place | A4 at 300 DPI, L8 | 3.2 ms | 4.8 KB |
| Otsu threshold, in place | A4 at 300 DPI, L8 | 8.0 ms | 268 KB |
| Load → resize → save | 20 JPEGs | 19.6 ms each (51 img/s) | 9.4 MB |

Hot paths use SIMD pixel kernels, pooled buffers and copy-on-write cloning. PNG decode is dominated by
the runtime's `ZLibStream` inflating IDAT data rather than by this library's code, which is why it
benefits less from the surrounding optimisation than JPEG does.

## AI operations

Two levels, depending on how much you want to bring yourself.

### Tensor bridges — in the core package

Convert between images and tensors for **any** ONNX model, with no extra dependency:

```csharp
using EasyImageSharp.Tensors;

// Planar [3, H, W] float tensor with ImageNet normalisation, ready for your inference session.
float[] chw = image.ToChwTensor(
    channelMean: [0.485f, 0.456f, 0.406f],
    channelStd:  [0.229f, 0.224f, 0.225f]);

// ...and back again from a model's [3, H, W] output.
using Image<Rgb24> result = TensorImage.FromChwTensor<Rgb24>(output, width, height);
```

`ToHwcTensor` produces interleaved `[H, W, 3]`, `ToGrayscaleTensor` produces `[H, W]` luminance, and
`FromGrayscaleTensor` builds an image from single-channel output. You supply the inference session; the
library handles normalisation and layout.

### EasyImageSharp.AI — pre-wired models

```bash
dotnet add package EasyImageSharp.AI
```

```csharp
using EasyImageSharp.AI;

using var ai = new ImageAiSession();
using Image<Rgb24> page = Image.Load<Rgb24>("phone-photo.jpg");

page.AutoOrient(ai);                                  // upright the page
page.DewarpDocument(ai);                              // flatten curl and keystone
page.DenoiseAI(ai);                                   // remove sensor noise
page.Mutate(ctx => ctx.Deskew().SauvolaThreshold());  // classical finish

page.SaveAsPng("clean.png");
```

| Operation | What it does | Why a model rather than an algorithm |
|---|---|---|
| `DetectOrientation` / `AutoOrient` | Classifies page rotation as 0°, 90°, 180° or 270° and applies a lossless correction | A projection profile is symmetric under rotation, so it cannot tell an upright page from an upside-down one. This can. |
| `DewarpDocument` | Flattens a photographed or curled page | A four-point perspective transform maps one plane to another; it cannot straighten a curved book spine. |
| `Upscale` | Learned super-resolution, tiled for large inputs | Recovers stroke topology on small glyphs that bicubic interpolation smears. |
| `DenoiseAI` | Residual denoiser for sensor and scan noise | Separates noise from ink, where a median filter of the same strength erodes thin strokes and serifs. |
| `GetSaliencyMask` / `RemoveBackground` | Segments the subject from its surroundings | Lets thresholding see only the document, so a cluttered desk does not pollute the statistics. |
| `BinarizeAI` | Learned per-pixel thresholding | Predicts a threshold per pixel instead of one window and constant, for stained or bleed-through documents. |

Each has an `...Async` counterpart taking a `CancellationToken`, and any image-to-image ONNX model of
your own can run through the same tiling and normalisation machinery via `ImageModelRunner`.

### Models

Published at
[huggingface.co/EasyImageSharp/EasyImageSharp-models](https://huggingface.co/EasyImageSharp/EasyImageSharp-models),
downloaded on first use and cached locally.

| Model | Operation | Size | Licence |
|---|---|---:|---|
| PP-LCNet x1.0 doc-ori | `AutoOrient` | 6.7 MB | Apache-2.0 |
| UVDoc | `DewarpDocument` | 31.6 MB | MIT |
| Real-ESRGAN general x4v3 | `Upscale` | 4.9 MB | BSD-3-Clause |
| DnCNN blind (grayscale) | `DenoiseAI` | 2.7 MB | MIT |
| U²-Net | `RemoveBackground` (default) | 176 MB | Apache-2.0 |
| U²-Net-p | `RemoveBackground` (fast tier) | 4.6 MB | Apache-2.0 |
| SauvolaNet | `BinarizeAI` | 0.3 MB | MIT |

Weights carry their original authors' licences, which differ per file; the model repository documents
each one with its input and output contract.

### Supply chain

Downloading executable weights at run time is a security surface, so it is bounded:

- **HTTPS only**, unless explicitly overridden for a local mirror.
- **SHA-256 pinned in source and verified fail-closed.** A file whose hash does not match is deleted and
  the load throws, rather than running unverified weights. A compromised host cannot substitute a model.
- **Published files are immutable.** A re-export is published under a new name, so a pinned library
  version always resolves the exact bytes it was tested against.
- **Downloads are atomic and resumable**, and concurrent requests for the same model collapse into one.
- **Offline mode** raises `OfflineModelMissingException` rather than touching the network, for
  air-gapped deployment against a pre-seeded cache.

```csharp
using var ai = new ImageAiSession(new ImageAiOptions
{
    ExecutionProvider = ExecutionProvider.Auto,   // CPU, CUDA, DirectML or CoreML
    CachePath = "/opt/myapp/models",              // default: %LOCALAPPDATA%/EasyImageSharp/models
    Offline = true,
});
```

GPU execution requires the matching ONNX Runtime package in your application; `Auto` falls back to CPU
when none is present. Full details in the
[package documentation](https://github.com/FarhanLodi/EasyImageSharp/blob/main/src/EasyImageSharp.AI/README.md).

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| **EasyImageSharp** | Codecs, `Image<TPixel>`, processing pipeline, document operators, drawing, metadata, pixel formats, tensor bridges | None beyond the framework |
| **EasyImageSharp.AI** | ONNX-powered orientation, dewarping, super-resolution, denoising, background removal and binarisation; model hub | `Microsoft.ML.OnnxRuntime` |

**Dependency policy.** The core package uses framework APIs only, and CI fails if it ever gains a
package dependency. Free, managed, permissively-licensed dependencies may be added where they provide
clear value; native binaries are confined to optional add-on packages; paid, split-licensed and copyleft
dependencies are never taken.

## Deployment notes

**Target frameworks.** .NET 8.0 and .NET 10.0. There is deliberately no `netstandard` target: the pixel
abstraction uses static abstract interface members, which require .NET 7 or later. Both targets are
AOT- and trimming-compatible with no conditional compilation.

**Thread safety.** A single `Image<TPixel>` instance is not thread-safe and must not be mutated
concurrently. Decoding, encoding and processing distinct images in parallel is fully supported.

**Memory.** `Image<TPixel>` owns its pixel buffer and must be disposed. After disposal, every
pixel-accessing member throws `ObjectDisposedException`.

**Versioning.** Semantic versioning. Breaking changes are confined to major releases and documented in
[CHANGELOG.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CHANGELOG.md).

## How it is verified

- **Independent fixtures.** Codecs are tested against a corpus of over 1,100 files encoded by other
  tools, with pixel-exact ground truth, so decode paths this library's own encoders never produce are
  still exercised.
- **Reference comparisons.** JPEG decoding matches a reference decoder at ≥ 61 dB PSNR; WebP output —
  lossy included — decodes byte-identically in the reference decoder; QOI output is byte-identical to the
  reference encoder.
- **Hostile input.** Around 150 crafted corrupt-input cases and a seeded byte-mutation fuzz pass run on
  every build, with a deeper nightly fuzz run across three operating systems and both frameworks.
- **Documentation that cannot drift.** Every code sample in this file is transcribed into the test suite
  and compiled, so a rename breaks the build rather than the docs.
- **Scale.** 2,387 tests for the core library and 162 for the AI package, run on Ubuntu, Windows and
  macOS on both target frameworks, plus pack validation for both packages.

## Building from source

Requires the .NET 10 SDK; the `net8.0` test leg additionally requires the .NET 8 runtime.

```bash
git clone https://github.com/FarhanLodi/EasyImageSharp.git
cd EasyImageSharp

dotnet build EasyImageSharp.slnx -c Release
dotnet test  EasyImageSharp.slnx -c Release
dotnet pack  src/EasyImageSharp  -c Release -o artifacts
```

Tagging `vX.Y.Z` runs the full suite on every OS and publishes both packages to NuGet. See
[CONTRIBUTING.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CONTRIBUTING.md) for the
repository layout, coding style, fixture regeneration and how to add a codec or an operation.

## Community

- **Bugs and feature requests:** [GitHub Issues](https://github.com/FarhanLodi/EasyImageSharp/issues)
- **Security reports:** [SECURITY.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/SECURITY.md)
- **Contributing:** [CONTRIBUTING.md](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CONTRIBUTING.md)
  and the [Code of Conduct](https://github.com/FarhanLodi/EasyImageSharp/blob/main/CODE_OF_CONDUCT.md)

## 💖 Support

If EasyImageSharp saves you time, consider supporting its development:

- 💳 **PayPal** — [paypal.me/FarhanLodi](https://www.paypal.com/paypalme/FarhanLodi)
- 📱 **UPI (India)** — `farhanlodi5@oksbi`
- 🏦 **Bank transfer (USD)** — details below

### USD bank transfer details (Wise)

USD account details for Farhan Lodi on Wise. Sending from a bank in the US? Use these details for a
domestic transfer. Sending from anywhere else? Make an international SWIFT transfer.

| Field | Value |
|---|---|
| Name | Farhan Lodi |
| Account type | Deposit |
| Routing number (wire and ACH) | `084009519` |
| Account number | `420927686563885` |
| SWIFT/BIC | `TRWIUS35XXX` |
| Bank address | Wise US Inc, 108 W 13th St, Wilmington, DE, 19801, United States |

Use the routing and account numbers when sending from the US, and the SWIFT/BIC when sending from
outside the US.

📧 Need more details, a different payment method, or have a question? Email
[farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com).

## 📬 Contact

For work inquiries, collaboration, feature requests, or any questions, reach out to:

**Farhan Lodi** — [farhanlodi31@gmail.com](mailto:farhanlodi31@gmail.com)

## 📄 License

[MIT](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE) — Copyright © 2026 Farhan Lodi.
