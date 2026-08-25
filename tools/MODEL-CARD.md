---
license: other
license_name: mixed-permissive
license_link: LICENSE
tags:
  - onnx
  - image-processing
  - document-image-processing
  - super-resolution
  - denoising
  - image-segmentation
library_name: onnx
---

# EasyImageSharp models

ONNX models used by [EasyImageSharp.AI](https://www.nuget.org/packages/EasyImageSharp.AI), the optional
AI add-on for the [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp) imaging library for .NET.

The library downloads these files at run time, verifies each against a SHA-256 pinned in its source, and
caches them locally. Verification is fail-closed: a file whose hash does not match is deleted rather than
run.

## Licensing

**These weights carry the licences of their original authors, which differ per file.** This repository
redistributes them unmodified in ONNX form; it does not and cannot relicense them. The repository-level
tag is therefore `other` — consult the per-file licence below, and the upstream project for the
authoritative terms and copyright notices.

| File | Task | Size | Licence | Upstream |
|---|---|---|---|---|
| `PP-LCNet_x1_0_doc_ori.onnx` | Document orientation (0/90/180/270) | 6.8 MB | Apache-2.0 | [PaddleX](https://github.com/PaddlePaddle/PaddleX) `doc_orientation_classify` |
| `UVDoc.onnx` | Page dewarping | 31 MB | MIT | [tanguymagne/UVDoc](https://github.com/tanguymagne/UVDoc) |
| `realesrgan_general_x4v3.onnx` | Super-resolution ×4 | 4.9 MB | **BSD-3-Clause** | [xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) `realesr-general-x4v3` |
| `dncnn_gray_blind.onnx` | Grayscale denoising | 2.7 MB | MIT | [cszn/KAIR](https://github.com/cszn/KAIR) `dncnn_gray_blind` |
| `u2net.onnx` | Saliency / background removal (default) | 176 MB | Apache-2.0 | [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) `u2net` |
| `u2netp.onnx` | Saliency, small and fast variant | 4.6 MB | Apache-2.0 | [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) `u2netp` |
| `sauvolanet.onnx` | Learned document binarisation | 0.3 MB | MIT | [Leedeng/SauvolaNet](https://github.com/Leedeng/SauvolaNet) |

`realesrgan_general_x4v3.onnx` is BSD-3-Clause: redistribution must retain the copyright notice and the
list of conditions, and the authors' names may not be used to endorse derived products. See `NOTICE` in
this repository for the retained notices.

## Input and output contracts

The library feeds these tensors exactly and interprets the outputs accordingly. An export that does not
match will run and produce wrong results, so the contract is part of the published artefact.

| File | Input | Normalisation | Output |
|---|---|---|---|
| `PP-LCNet_x1_0_doc_ori.onnx` | `x` `[1,3,224,224]` RGB | ImageNet mean/std | `[1,4]` scores over 0°, 90°, 180°, 270° clockwise |
| `UVDoc.onnx` | `image` `[1,3,712,488]` RGB | 0–1 | `[1,3,712,488]` rectified image in 0–1 |
| `realesrgan_general_x4v3.onnx` | `input` `[1,3,H,W]` RGB, **dynamic H/W** | 0–1 | `[1,3,4H,4W]` in 0–1 |
| `dncnn_gray_blind.onnx` | `input` `[1,1,H,W]` luminance, **dynamic H/W** | 0–1 | `[1,1,H,W]` **noise residual**; clean = input − output |
| `u2net.onnx` | `input.1` `[1,3,320,320]` RGB | ImageNet mean/std | `[1,1,320,320]` saliency mask in 0–1 |
| `u2netp.onnx` | `input` `[1,3,320,320]` RGB | ImageNet mean/std | `[1,1,320,320]` saliency mask in 0–1 |
| `sauvolanet.onnx` | `input` `[1,1,H,W]` luminance, **dynamic H/W** | 0–1 | `[1,1,H,W]` per-pixel **threshold map**; white where luminance ≥ threshold |

## Checksums

Verified by the library against the values compiled into `ModelRegistry.cs`. See `checksums.json`.

```
PP-LCNet_x1_0_doc_ori.onnx     D85B3185075AFCA1A83157F73EAC2E52B598D72E9D47DD19CC4A2F3605E23E3F
UVDoc.onnx                     7E54E917AD9CA8F6CFFE606C7C311AAD3B6EEE457D4D9776F99F175D0CA86835
realesrgan_general_x4v3.onnx   AAA2B465D2258BDCC30D51076BC358DA00D1595D2FA05697979E782F97DE325A
dncnn_gray_blind.onnx          A0A21D0677EA5FB83A66D922EBFB22BC81926C79044B08778F4A6D740FA7864F
u2net.onnx                     8D10D2F3BB75AE3B6D527C77944FC5E7DCD94B29809D47A739A7A728A912B491
u2netp.onnx                    2B5D0563269555FC84FFCA01B24AF5081581D38614F858ECF913331DF0E2ED88
sauvolanet.onnx                948AAEA4882D4D6734C0FEC4739381857BE97F62526AD8BA8CA067A353106160
```

**Published files are never overwritten.** A re-export is published under a new file name with a new
checksum, so a pinned library version always resolves the exact bytes it was tested against.

## Provenance

`realesrgan_general_x4v3.onnx` and `dncnn_gray_blind.onnx` were exported by
[`tools/export_models.py`](https://github.com/FarhanLodi/EasyImageSharp/blob/main/tools/export_models.py),
and `sauvolanet.onnx` by
[`tools/export_sauvolanet.py`](https://github.com/FarhanLodi/EasyImageSharp/blob/main/tools/export_sauvolanet.py),
both at opset 17 from the upstream weights linked above. Each export is validated against the reference
implementation before publication. `u2netp.onnx` is redistributed from an existing ONNX release. `PP-LCNet_x1_0_doc_ori.onnx` and
`UVDoc.onnx` are redistributed unmodified.

## Usage

```csharp
using EasyImageSharp;
using EasyImageSharp.AI;
using EasyImageSharp.PixelFormats;

using var ai = new ImageAiSession();
using Image<Rgb24> page = Image.Load<Rgb24>("photo.jpg");

page.AutoOrient(ai);        // PP-LCNet_x1_0_doc_ori
page.DewarpDocument(ai);    // UVDoc
page.DenoiseAI(ai);         // dncnn_gray_blind
```

Models download on first use into `%LOCALAPPDATA%/EasyImageSharp/models` (`~/.local/share` elsewhere).
For air-gapped deployment, pre-seed that directory and set `ImageAiOptions.Offline = true`.
