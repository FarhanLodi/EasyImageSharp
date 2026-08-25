<div align="center">

![EasyImageSharp](https://raw.githubusercontent.com/FarhanLodi/EasyImageSharp/main/src/EasyImageSharp/Assets/icon.png)

# EasyImageSharp.AI

[![NuGet](https://img.shields.io/nuget/v/EasyImageSharp.AI.svg?label=NuGet)](https://www.nuget.org/packages/EasyImageSharp.AI)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE)

</div>

ONNX-powered image operations for [EasyImageSharp](https://www.nuget.org/packages/EasyImageSharp):
document orientation, page dewarping, super-resolution, denoising, background removal and learned
binarisation — with a model hub that downloads, checksum-verifies and caches models for you.

This package is optional. The core `EasyImageSharp` package has no dependencies beyond the framework;
everything here is opt-in, and the ONNX Runtime native binaries live only in this package.

```bash
dotnet add package EasyImageSharp.AI
```

## Quick start

```csharp
using EasyImageSharp;
using EasyImageSharp.AI;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using var ai = new ImageAiSession();

using Image<Rgb24> page = Image.Load<Rgb24>("phone-photo.jpg");

page.AutoOrient(ai);                                  // fix a sideways or upside-down page
page.DewarpDocument(ai);                              // flatten a curled page
page.DenoiseAI(ai);                                   // learned denoise
page.Mutate(ctx => ctx.Deskew().SauvolaThreshold());  // classical finish

page.SaveAsPng("clean.png");
```

`ImageAiSession` caches one inference session per model and is safe to keep for the lifetime of your
application. Dispose it when you are done.

## Operations

| Method | What it does |
|---|---|
| `DetectOrientation(ai)` | Classifies page rotation as 0°, 90°, 180° or 270° and returns the result with per-class probabilities. |
| `AutoOrient(ai)` | Applies that classification with a lossless rotation, and returns the `RotateMode` it used. |
| `DewarpDocument(ai)` | Flattens a photographed or curled page — geometric distortion a four-point perspective correction cannot fix. |
| `Upscale(ai, factor: 4)` | Learned super-resolution. Tiled, so large inputs stay within memory. |
| `DenoiseAI(ai)` | Residual denoiser that removes sensor and scan noise while preserving thin strokes. |
| `GetSaliencyMask(ai)` / `RemoveBackground(ai)` | Segments the subject from the background — useful for documents photographed on a busy surface. |
| `BinarizeAI(ai)` | Learned per-pixel thresholding for degraded documents (stains, bleed-through, uneven light). |

Each has an `...Async` counterpart taking a `CancellationToken`.

### Why these, next to the classical operators

The core library already has projection-profile deskew, Sauvola binarisation and median denoising, and
those remain the right default: they are fast, deterministic and need no model download. These
operations handle what the classical ones cannot:

- **Orientation** — a projection profile is symmetric under 90° and 180°, so it *cannot* detect an
  upside-down page. The classifier can.
- **Dewarping** — a homography maps one plane to another; it cannot straighten a curved book spine.
- **Super-resolution** — recovers stroke topology on small glyphs that bicubic upscaling smears.
- **Learned binarisation** — predicts a per-pixel threshold instead of one global window and constant.

A good pipeline uses both: the model for the quadrant, the classical operator for the residual angle.

## Models

Models are published at
[huggingface.co/EasyImageSharp/EasyImageSharp-models](https://huggingface.co/EasyImageSharp/EasyImageSharp-models),
fetched on first use and cached under `%LOCALAPPDATA%/EasyImageSharp/models` (`~/.local/share` on Linux
and macOS). Every file has its SHA-256 pinned in this package.

| Model | Operation | Size | Licence |
|---|---|---:|---|
| `PP-LCNet_x1_0_doc_ori.onnx` | `DetectOrientation` / `AutoOrient` | 6.7 MB | Apache-2.0 |
| `UVDoc.onnx` | `DewarpDocument` | 31.6 MB | MIT |
| `realesrgan_general_x4v3.onnx` | `Upscale` | 4.9 MB | BSD-3-Clause |
| `dncnn_gray_blind.onnx` | `DenoiseAI` | 2.7 MB | MIT |
| `u2net.onnx` | `GetSaliencyMask` / `RemoveBackground` (default) | 176 MB | Apache-2.0 |
| `u2netp.onnx` | `GetSaliencyMask` / `RemoveBackground` (fast tier, `ModelRegistry.SaliencyFast`) | 4.6 MB | Apache-2.0 |
| `sauvolanet.onnx` | `BinarizeAI` | 0.3 MB | MIT |

Weights keep their upstream authors' licences. To run your own export instead — a re-trained model, an
int8 variant or a file from an internal mirror — point the library at it by model name:

```csharp
var options = new ImageAiOptions();
options.ModelPathOverrides["super-resolution-x4"] = @"C:\models\realesrgan_general_x4v3.onnx";

using var ai = new ImageAiSession(options);
```

Each model's exact input contract — tensor name, shape and normalisation — is documented on the
corresponding `ModelRegistry` property, so an export that matches will work without code changes.

## Configuration

```csharp
var options = new ImageAiOptions
{
    ExecutionProvider = ExecutionProvider.Auto,   // Cpu, Cuda, DirectML, CoreML
    Quantize = true,                              // prefer int8 weights where available
    Offline = false,                              // true: never download, fail if not cached
    CachePath = null,                             // null: use the default cache directory
    AllowUnverifiedModels = false,                // keep checksum verification fail-closed
    IntraOpNumThreads = null,
    Log = Console.WriteLine,
};

using var ai = new ImageAiSession(options);
```

**GPU execution.** `ExecutionProvider.Auto` tries the GPU providers whose native packages are present
and silently falls back to CPU. To enable one, add the matching ONNX Runtime package to *your*
application — for example `Microsoft.ML.OnnxRuntime.Gpu` for CUDA or `Microsoft.ML.OnnxRuntime.DirectML`
for DirectML. This package deliberately does not force those native assets on every consumer.

**Environment overrides.** `EASYIMAGESHARP_CACHE` sets the cache directory and
`EASYIMAGESHARP_MODEL_BASE_URL` redirects downloads to a mirror.

## Air-gapped and offline deployment

The model hub is fail-closed by design: downloads are HTTPS-only, every file is verified against a
pinned SHA-256, and a mismatch deletes the file and throws rather than running an unverified model.

For an environment without internet access, pre-seed the cache and set `Offline = true`:

```csharp
var options = new ImageAiOptions
{
    CachePath = "/opt/myapp/models",
    Offline = true,   // OfflineModelMissingException if a model is not already cached
};
```

Downloads are atomic (written to `.part` and renamed), resume with HTTP range requests, and retry with
exponential backoff. Concurrent requests for the same model collapse into a single download.

## Bring your own model

Any image-to-image ONNX model can be run through the same tiling and normalisation machinery:

```csharp
using Image<Rgb24> output = ImageModelRunner.Run(
    ai,
    modelPath,
    input,
    new ImageModelContract
    {
        InputName = "input",
        Normalization = TensorNormalization.Unit,   // 0-1; also ImageNet mean/std
        ScaleFactor = 2,                            // output size relative to input
        TileSize = 256,
        TileOverlap = 16,
    });
```

## Exceptions

| Situation | Exception |
|---|---|
| Download failed after retries | `ModelDownloadException` |
| SHA-256 did not match the pinned value | `ModelChecksumException` |
| `Offline = true` and the model is not cached | `OfflineModelMissingException` |

## Requirements

Targets **net8.0** and **net10.0**, and depends on `Microsoft.ML.OnnxRuntime`.

## License

[MIT](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE) — Copyright © 2026 Farhan Lodi.
Model weights are covered by their own upstream licences, listed in the table above and on each
`ModelRegistry` entry.
