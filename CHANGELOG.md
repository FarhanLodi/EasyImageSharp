# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-23

First public release. `EasyImageSharp` is a fully managed 2D imaging library for .NET; `EasyImageSharp.AI`
is an optional add-on adding ONNX-powered operations. Both target `net8.0` and `net10.0`, and both are
AOT- and trimming-compatible.

### Codecs

| Format | Decode | Encode |
|---|---|---|
| PNG | All colour types, bit depths 1/2/4/8/16, Adam7 interlacing, palette and colour-key transparency | All colour types and bit depths, palette output via quantisation, Adam7, selectable filtering |
| JPEG | Baseline, extended sequential and progressive; every chroma subsampling with triangle upsampling for 4:2:2 and 4:2:0; restart markers; grayscale, YCbCr, RGB, Adobe CMYK and YCCK | Baseline and progressive, quality 1–100, 4:4:4 / 4:2:2 / 4:2:0 / 4:1:1 / 4:1:0, grayscale, RGB, CMYK, YCCK, optimised Huffman tables, restart intervals |
| WebP | Lossy (VP8), lossless (VP8L), alpha (ALPH), animation with offsets, blending and disposal | Lossy and lossless, near-lossless, alpha, animation, quality and effort levels |
| GIF | GIF87a/89a, global and local palettes, interlacing, transparency, animation with disposal | LZW, global or per-frame palettes, transparency, animation with delays and loop count |
| BMP | 1/4/8-bit palette, 16/24/32-bit, `BI_BITFIELDS` and alpha bitfields, RLE8/RLE4, OS/2 headers, both row orders | 1/4/8-bit palette, 16-bit, 24-bit, 32-bit with alpha |
| TIFF | Multi-page, both byte orders, strips and tiles, chunky and planar, None / LZW / Deflate / PackBits / CCITT G3 & G4 / JPEG, horizontal predictor, 1–32-bit samples in unsigned, signed and floating-point formats, WhiteIsZero / BlackIsZero / palette / RGB(A) / CMYK / YCbCr / CIELab | Multi-page, None / LZW / Deflate / PackBits / CCITT G3 & G4, selectable bit depth, photometric and predictor |
| TGA | Types 1/2/3 and their RLE variants, 8/15/16/24/32-bit, colour maps, either origin | 8/16/24/32-bit, raw or run-length |
| Netpbm | P1–P6 (ASCII and binary) and P7 PAM, 8- and 16-bit | PBM / PGM / PPM, plain or binary |
| QOI | Full specification | Byte-identical to the reference encoder |
| ICO / CUR | Multi-image icons with embedded BMP or PNG entries | PNG or 32-bit BMP entries, cursors with hotspots |

Not implemented, and reported as `NotSupportedException`: arithmetic-coded, lossless and 12-bit JPEG;
old-style JPEG-in-TIFF (compression 6); JBIG.

### Core

- `Image<TPixel>` with `Rgb24`, `Rgba32`, `Bgr24`, `Bgra32` and `L8`, plus the high-precision `Rgb48`,
  `Rgba64`, `L16`, `La16`, `La32`, `A8`, `Argb32`, `Abgr32` and `RgbaVector`. Conversions between
  high-precision formats keep full precision; 16-bit PNG and TIFF samples decode at full width.
- Multi-frame images through `image.Frames`, with `CloneFrame`, `AddFrame`, `InsertFrame` and `MoveFrame`.
- `Load` / `LoadAsync` / `Identify` / `IdentifyAsync` / `LoadPixelData` / `WrapMemory`, the `image[x, y]`
  indexer, `ProcessPixelRows` (one, two or three images), `CopyPixelDataTo`, `ToBase64String`,
  `Clone` / `CloneAs`, and `Save` / `SaveAs*` with async counterparts.
- `Color` with the CSS named colours and hex parsing; `Point`, `Size` and `Rectangle` with `PointF`,
  `SizeF` and `RectangleF` counterparts.
- `Configuration.MaxDegreeOfParallelism` for row-parallel execution, set to 1 for determinism.

### Processing

Geometry (resize with every mode, anchor positions and 15 resamplers, optional linear-light and
premultiplied-alpha resampling; crop, entropy crop, pad, rotate, flip, skew, affine and projective
transforms with taper and quad distortion); colour (grayscale modes, hue, saturation, lightness,
opacity, colour matrices with presets including eight colour-blindness simulations); filters (Gaussian,
box and bokeh blur, sharpen, median, ten edge-detector kernels, arbitrary and separable convolution,
oil paint, pixelate, vignette, glow, histogram equalisation including CLAHE); thresholding (binary,
Otsu, Sauvola, Bradley, Niblack, Wolf-Jolion, Phansalkar, NICK, and an auto-selecting `Binarize`);
quantisation (Wu, Octree, WebSafe, fixed palette) with 15 dither kernels; compositing with 20 blend
modes and 12 Porter-Duff alpha composition modes; and annotation drawing with an embedded bitmap font.

### Document imaging

`Deskew` and `DetectSkew` (projection profile or Hough), `DetectOrientation`, `AutoRotateDocument`,
`DetectPage`, `CorrectPerspective`, `AutoCropPage`, `BackgroundNormalize`, `RemoveShadows`,
`ContrastStretch`, `AutoLevels`, `Gamma`, morphology (erode, dilate, open, close, top-hat, black-hat,
thin, despeckle), connected components (`RemoveSmallObjects`, `KeepLargestComponent`, `FillHoles`),
`RemoveLines`, `RemoveBorders`, `RemoveHolePunches`, `SegmentTextLines`, `SegmentWords`, `NormalizeDpi`
and the `PrepareForOcr` preset.

### Metadata

`ImageMetadata` with resolution and units, EXIF read and write for JPEG, PNG and TIFF (typed access to
about 60 well-known tags, unknown tags round-tripped), ICC and XMP passthrough, per-format metadata
(`JpegMetadata`, `PngMetadata`, `TiffMetadata`, `BmpMetadata`, `GifMetadata`, `WebpMetadata`) and
per-frame metadata for GIF, WebP and TIFF. `AutoOrient` applies EXIF orientation and resets the tag;
orientation is never applied silently.

### Safety with untrusted input

- `DecoderOptions` with `MaxPixels` (256 megapixels per frame by default) and `MaxFrames`, enforced
  immediately after the header is parsed and before any pixel memory is allocated. `Identify` is never
  limited, so callers can inspect a declared size before committing to a decode.
- A closed exception contract: `UnknownImageFormatException`, `InvalidImageContentException`,
  `ImageSizeLimitExceededException` (all deriving from `ImageFormatException`) and
  `NotSupportedException`. Framework exceptions never escape a decoder for malformed input.
- Every pixel-touching member throws `ObjectDisposedException` after `Dispose()`.

### Tensors and AI

Core tensor bridges (`ToChwTensor`, `ToHwcTensor`, `ToGrayscaleTensor`, `FromChwTensor`,
`FromGrayscaleTensor`) with mean/standard-deviation normalisation. The optional `EasyImageSharp.AI`
package adds `DetectOrientation`, `AutoOrient`, `DewarpDocument`, `Upscale`, `DenoiseAI`,
`GetSaliencyMask`, `RemoveBackground` and `BinarizeAI`, plus a generic tiled image-to-image runner for
your own ONNX models. Its model hub downloads over HTTPS only, verifies each file against a pinned
SHA-256 fail-closed, caches locally, resumes interrupted downloads and supports fully offline operation.

### Performance

Row-parallel execution, SIMD pixel kernels, pooled buffers and copy-on-write cloning. Measured with
BenchmarkDotNet on a 6-core Ryzen 5 4600H: a 3032×2008 JPEG decodes in 85 ms, a bicubic half-resize of
the same image takes 15 ms (5 ms for `L8`), Otsu thresholding an A4 page at 300 DPI takes 8 ms, and a
load-resize-save pipeline sustains 51 images per second. Full tables in `benchmarks/results/`.

### Verification

2387 tests for the core library and 160 for the AI package, on both target frameworks. Codecs are
checked against a corpus of independently-encoded fixtures with pixel-exact ground truth: JPEG decoding
matches a reference decoder at ≥61 dB PSNR, WebP output — lossy included — decodes byte-identically in
the reference decoder, and QOI output is byte-identical to the reference encoder. About 150 crafted
corrupt-input cases and a seeded byte-mutation fuzz pass run on every build. Every code sample in the
documentation is compiled by the test suite so it cannot drift from the API.

[1.0.0]: https://github.com/FarhanLodi/EasyImageSharp/releases/tag/v1.0.0
