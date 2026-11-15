# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.1] - Unreleased

New codecs, managed drawing and text, row-streaming transcode, new processing operations and more AI
operations, with the packages consolidated and **.NET 10 as the only target framework**. Version 1.3.0 was
never published; 1.3.1 is the first release after 1.2.0.

No public API was removed, and package validation against the published 1.2.0 packages passes; the only
recorded exception is the dropped `net8.0` target (`CompatibilitySuppressions.xml` in each published
project). New `IImageProcessingContext` members have default implementations that throw
`NotSupportedException`, so third-party implementations of the interface keep compiling and loading.

The release ships five packages: **EasyImageSharp**, **EasyImageSharp.AI**, **EasyImageSharp.SkiaSharp**,
**EasyImageSharp.Codecs** (new) and **EasyImageSharp.Web** (new), plus **EasyImageSharp.LittleCms** as a
forwarding package for existing users.

### Breaking

- **.NET 8 is no longer supported.** Every package targets `net10.0` only. Applications on .NET 8 can stay
  on 1.2.0. The PNG decoder's managed inflate path, which only ran on .NET 8, is now unused; on .NET 10 the
  runtime's zlib-ng `ZLibStream` decodes IDAT data, as it already did.

### Added

- **Formats.** Radiance HDR (`.hdr`) and OpenEXR decoding and encoding (`SaveAsHdr`, `SaveAsExr`). EXR covers
  half, float and uint channels and None, RLE, ZIPS, ZIP, PIZ, DWAA and DWAB compression. The decoder reads
  scanline and tiled parts; the encoder writes scanline parts. `HdrMetadata` and `ExrMetadata` carry the format headers.
- **JPEG.** 12-bit sequential and progressive decoding, and lossless (SOF3) decoding, at full sample
  precision. Sequential arithmetic-coded JPEG (SOF9, with DAC conditioning tables and restart intervals), through
  both the buffered decoder and the row-streaming source; libjpeg-turbo's `testimgari.jpg` decodes at 64.4 dB
  against libjpeg-turbo, the same accuracy as its Huffman-coded twin. Progressive (SOF10) and lossless (SOF11)
  arithmetic coding remain unsupported and say so precisely. Encoder quantization presets
  (`JpegQuantizationPreset`: Annex K, flat and five perceptual tables) and opt-in trellis quantization.
  A row-by-row `JpegScanlineSource` and `JpegScanlineSink`. Separable IDCT and upsampler rewrites.
- **TIFF.** BigTIFF writing (`TiffFormatType`). WebP-compressed pages (compression 50001, libtiff's
  `COMPRESSION_WEBP`): lossy or lossless, 8-bit RGB or RGBA, strips or tiles, each segment decoded by the built-in
  WebP decoder within the request's execution scope and limited to the segment's own size.
- **Metadata.** IPTC-IIM (`IptcProfile`, `IptcTag`) and Photoshop image resources (`PhotoshopResources`,
  `8BIM`), read from and written to JPEG and TIFF.
- **Streaming transcode.** `Image.Transcode`/`TranscodeAsync` and `Image.Thumbnail`/`ThumbnailAsync`
  decode, auto-orient, resize and encode row by row for PNG and JPEG sources and sinks, without
  materializing the full frame. Other formats fall back to a buffered decode or encode transparently, and
  `TranscodeResult` reports which ends streamed. JPEG sources that are resized down decode through the
  reduced IDCT. This is close to, but by design not bit-identical with, decode-then-resize.
- **Managed vector drawing and text (core, no native dependency).** `VectorPath`/`PathBuilder`; solid,
  linear, radial, conic and image brushes; pens with joins, caps and dashes; non-zero and even-odd fills;
  clipping. A managed font stack (`FontCollection`, `SystemFonts`, `Font`, `TextMeasurer`) reading
  TrueType, CFF/OpenType, WOFF and WOFF2 with kerning. `DrawText` supports wrapping and alignment.
- **Processing.** `ToneMap` (Clip, Reinhard, extended Reinhard, Hable, ACES filmic). `ApplyLut` with
  `.cube` 3D LUTs (`ColorLut`). `BilateralFilter` (exact and grid) and `GuidedFilter`. `SmartCrop`,
  `Watermark` and dominant colors. `BlurHash` and `ThumbHash` placeholders. `ImageQuality` full-reference
  and no-reference metrics.
- **Color management on load.** `DecoderOptions` color-profile handling: keep, preserve, convert to sRGB
  or throw. `ConvertToSrgb` processing. Conversion status is recorded in `ColorProfileConversionMetadata`.
- **Memory and diagnostics.** Pluggable `MemoryAllocator` (`PooledMemoryAllocator`,
  `UnmanagedMemoryAllocator`, GC) through `Configuration.MemoryAllocator`. `ImageTelemetry`:
  `ActivitySource` and `Meter` named `EasyImageSharp`, emitting decode/encode/process durations, bytes,
  pixels and rejection counts.
- **Performance.** SIMD paths for VP8/VP8L WebP decode/encode DSP and transforms, YUV conversion,
  integral images and morphology, each with scalar differential tests.
- **EasyImageSharp.AI.** Face detection, landmarks and redaction; mask-based inpainting with tiling;
  content moderation; image embeddings; aesthetic/technical quality assessment. Batched ONNX inference
  and quantized-model resolution in `ModelRegistry`. All need user-supplied or registry models.
- **New packages.**
  - `EasyImageSharp.Codecs`: the optional native codecs and colour management in one package. AVIF
    (libavif with libaom) and JPEG XL (libjxl) decode/encode providers, and the LittleCMS ICC transforms
    previously shipped as `EasyImageSharp.LittleCms`. Namespaces are unchanged (`EasyImageSharp.Avif`,
    `EasyImageSharp.JpegXl`, `EasyImageSharp.LittleCms`). The Windows x64 runtimes of all three libraries are
    bundled, built or fetched from pinned, checksum-verified upstream sources by `scripts/build-libavif.ps1`,
    `scripts/build-libjxl.ps1` and `scripts/build-littlecms.ps1`; Linux and macOS use the system libraries.
    The three bindings share one native library resolver, as the runtime allows only one per assembly.
  - `EasyImageSharp.Web`: ASP.NET Core middleware for on-the-fly processing, signed URLs, format
    negotiation, HTTP and server-side caching, limits, concurrency limiting and a health check, plus the
    dependency-injection integration (`AddEasyImageSharp` with code and `IConfiguration` overloads,
    `ValidateOnStart` and `IImageOptionsFactory`, namespace `EasyImageSharp.Extensions.DependencyInjection`),
    usable without the middleware. `samples/WebSample` shows it in use.
- **Verification.** Third-party conformance suites (PngSuite, libjpeg-turbo, libtiff, plus locally built
  libwebp-test-data) checked against independent decoders. A SharpFuzz/libFuzzer harness with
  dictionaries and an OSS-Fuzz project definition (`fuzz/`). A coverlet coverage collector
  ([docs/COVERAGE.md](docs/COVERAGE.md)).
- **Repository.** CodeQL, OpenSSF Scorecard and a DocFX site build check; a release workflow with
  dry run, CycloneDX SBOMs, build-provenance attestations and optional NuGet author signing; `SUPPORT.md`.
  CI and the release workflow now build the libjxl and libavif runtimes and pack, verify and describe all
  six packages (previously only four were packed).

### Changed

- **PNG chunk CRCs are verified.** A file with a bad CRC on a critical chunk (IHDR, PLTE, IDAT, IEND, or
  APNG acTL/fcTL/fdAT) now throws `InvalidImageContentException` where it previously loaded. An ancillary
  chunk with a bad CRC (text, EXIF, ICC, tRNS, and so on) is discarded rather than trusted, as libpng does
  by default. The buffered decoder and the streaming `Image.Transcode` path apply the same rule. Measured
  cost: about 3% of decode time for a 10 MB 4000x3000 PNG on one machine.
- **TIFF associated alpha is un-premultiplied.** Grey and RGB pages with `ExtraSamples = 1` previously
  returned the premultiplied samples unchanged. They now return straight colour (divided by alpha, rounded
  to nearest), matching libtiff and Pillow and every other alpha source in the library. Semi-transparent
  pixels from such files therefore decode lighter than before.
- **`EasyImageSharp.LittleCms` moved into `EasyImageSharp.Codecs`.** The 1.3.1 `EasyImageSharp.LittleCms`
  package contains no code: it depends on `EasyImageSharp.Codecs` and forwards its five public types there,
  so existing references keep compiling and binaries built against 1.2.0 keep running. Replace the
  reference with `EasyImageSharp.Codecs` at your convenience.
- Package validation now uses 1.2.0 as the baseline for all four previously published packages
  (SkiaSharp and LittleCms had no baseline before).
- CI no longer runs the fixture-determinism job (`check_determinism.py` remains for local use), and the
  docs workflow only checks that the site builds instead of deploying it to GitHub Pages.

### Fixed

- `PngDecoder` validates the PNG signature itself, so calling the decoder directly or through a format
  provider rejects a corrupt signature instead of relying on format detection.
- Pre-TIFF 6.0 "compat" LZW (LSB-first codes, late code-width change) is decoded instead of being reported
  as corrupt. It is detected from the stream's first two bytes, as libtiff detects it.
- All 176 PngSuite files now pass, including the 14 invalid-by-design files. libtiff's `quad-lzw-compat`
  and `32bpp-None` now match their references.
- A still lossless WebP whose header declares no alpha is decoded opaque even where a damaged stream produces
  transparent pixels (an out-of-range colour index), as libwebp-based readers and browsers show it.
- JPEG XL: limiting an animation with `MaxFrames` no longer fails with "decoding failed while
  skipping a frame"; libjxl only accepts the skip once it asks for the frame's output buffer.
- `scripts/build-libavif.ps1` failed with "Unknown argument -" when `-Jobs` was not given: a one-element
  argument array was unwrapped to a string and splatted character by character.

### Verification

- The PngSuite, libjpeg-turbo, libtiff and (locally built) libwebp conformance suites pass with no known failures
  and no skips. JPEG-compressed TIFF pages are held to the same 40 dB PSNR floor as standalone JPEG (lossy
  decoders may round the IDCT and upsampling differently); the tiled libtiff sample decodes at 67.8 dB.
- AVIF has its own test project: round trips (exact lossless, wide bit depths, animation,
  metadata, limits, malformed input) and fixtures written and decoded by an independent libavif build (Pillow's),
  matched within one level at 4:4:4 and above 50 dB when chroma is subsampled.
- The JPEG XL and AVIF tests run against the real native libraries, and a test loads LittleCMS, libavif and
  libjxl in one process to cover the shared resolver. `EasyImageSharp.Codecs` packs with all three win-x64
  runtimes, a README and verbatim third-party licence notices.
- 6,997 tests pass on .NET 10; the only skips are AI tests whose model files are absent.

### Known limits

- JPEG: progressive (SOF10), lossless (SOF11) and hierarchical arithmetic coding are unsupported. No independent
  encoder for them was available to verify a decoder against, so none is claimed.
- The bundled `avif.dll` is built without libaom's assembly SIMD, so AVIF encoding is slower than distribution
  builds of libavif.

## [1.2.0] - 2026-10-14

### Added

- Direct reduced-IDCT JPEG decoding and seekable 64-bit TIFF/BigTIFF region/tile access.
- Optional LittleCMS ICC LUT/CMYK transforms with rendering intents/BPC and reproducible native deployment.
- Isolated image worker with OS process limits; multilingual corpus, orientation calibration and warmed concurrency tools.
- Reusable `WriteTensor` and fused `ResizeToTensor`: CHW/HWC, RGB/BGR/grayscale, caller-owned
  strided buffers, batch offsets, normalization, request execution options and coordinate mapping.
- Independent document-page rotation/cropping; opt-in NeuralOcr, ScannedDocument, PhoneDocument
  and Screenshot profiles with sampled diagnostics, stage decisions/timings and coordinate maps.
- Duplicate-page candidate dHash similarity, with collision and sampling caveats.
- Optional async AI document pipeline with explicit image/grid dewarp contracts and truthful
  mapping availability. Requested model failures propagate; no silent heuristic substitution.
- Request-local parallelism, cancellation and cumulative pixel-buffer allocation budgets; encoded
  input byte limits; incremental root-header inspection for six formats; lazy TIFF page pixel decoding.
- RGB/XYZ/Lab/Lch/HSV/HSL conversions and the documented RGB matrix/TRC ICC v2/v4 subset.
- Per-request optional format providers and a public header-info factory for external decoders.
- Optional `EasyImageSharp.SkiaSharp` package for copied native pixel interop, vector drawing,
  HarfBuzz text-run shaping and explicit RGB color conversion. Core remains managed and dependency-free.
- OCR corpus exporter, CER/WER and convex-quadrilateral detection scoring, pinned local-model smoke
  reports, source integration recipes in both sibling OCR repositories, and tensor benchmarks.
- Broader smoke/precision/resource tests and ARM64/scalar/Native AOT CI coverage; locally verified Linux x64 native and ARM64 QEMU smoke.

### Fixed

- Built-in wide pixel formats retain precision through tensors, resize, rotation, affine/projective
  and page perspective sampling. Wide geometric sampling uses premultiplied alpha; existing byte
  arithmetic is retained.
- Published UVDoc image-output contract now receives dynamic full-resolution BGR input with stride
  padding instead of a fixed low-resolution RGB input. Orientation uses short-side resize plus center crop.
  These intentional AI behavior fixes can change existing outputs and full-resolution inference memory.
- Finite parameter, quadrilateral, tensor shape, resize/kernel and row-batch overflow validation.
- Failed/canceled processing clones dispose their outputs while preserving source pixels.

### Limits

See [the capability matrix](docs/ADVANCED_IMAGING.md). Pixel budgets do not cover all scratch/native
memory; the separate process worker supplies an OS-enforced envelope. The TIFF page API buffers
encoded data; the new seekable region reader supports documented layouts without that buffer. Paragraph text layout, additional bundled codecs/rasterizers and int8 AI variants are not implemented.
Broader real-photo accuracy, deployment calibration and production throughput remain validation gates;
small public-corpus and synthetic-region experiments do not establish universal OCR improvement.

## [1.1.0] - 2026-09-03

Animated PNG, BigTIFF reading, a faster PNG decode on `net8.0`, and the restoration of the verification
this repository had claimed but could not perform. No API was removed and no existing behaviour changed,
with one exception that is called out under **Changed**: `SaveAsPng` on a multi-frame image now writes an
APNG instead of only the first frame.

### Added

- **APNG, decoded and encoded.** `acTL`, `fcTL` and `fdAT` are read and written, with per-frame
  sub-rectangles and offsets, per-frame delays, all three disposal operations and both blend operations,
  loop counts, and a hidden first frame that stays outside the animation. Decoded frames arrive fully
  composited, one image frame per animation frame, exactly as GIF and WebP already delivered them.
  New public surface: `PngMetadata.IsAnimated`, `PngMetadata.RepeatCount`,
  `PngMetadata.AnimateRootFrame`, `PngFrameMetadata` (`FrameDelay`, `DisposalMethod`, `BlendMethod`),
  `PngDisposalMethod`, `PngBlendMethod`, `PngEncoder.FrameDelay` and `PngEncoder.RepeatCount`.
  Animated output is truecolour or grayscale at 8 or 16 bits; `PngColorType.Palette` and sub-8-bit
  grayscale throw `NotSupportedException` for an animated image, because one palette cannot serve frames
  the quantizer processes independently. Verified against 40 fixtures written by Pillow — 25 well-formed
  and 15 deliberately malformed — each with per-frame RGBA ground truth.
- **BigTIFF, decoded.** Version-43 files are read: 8-byte offsets, 64-bit directory entry counts,
  20-byte directory entries, the `LONG8`, `SLONG8` and `IFD8` field types, and strip and tile tables at
  either offset width. Format detection recognises the BigTIFF signature in both byte orders, the EXIF
  and metadata readers were widened to 64 bits, `ExifDataType` gained `Long8`, `SignedLong8` and `Ifd8`,
  and `TiffMetadata.BigTiff` reports which container a file used. **Decode only** — the encoder writes
  classic TIFF as before — and, because a file is buffered whole before decoding, a BigTIFF must be
  under 2 GiB. The point is compatibility with the larger container, not multi-gigabyte imagery.
- **A managed DEFLATE/zlib inflate for PNG, on `net8.0` only.** On .NET 8 the runtime's `ZLibStream`
  sits on native zlib and the managed implementation beats it: 1.27x to 1.49x faster end to end on a
  full `Image.Load`, partly because `IDAT` and `fdAT` payloads are fed to it in place instead of being
  concatenated into one buffer first. On .NET 10 `ZLibStream` is backed by zlib-ng with hand-written
  SIMD, a managed inflate is 1.3x to 2.0x *slower*, and `ZLibStream` is therefore kept. The choice is
  made at compile time per target framework; output is byte-identical on both and there is nothing to
  configure.
- **`DecoderOptions.MaxTotalPixels`**, a budget summed over every decoded frame (default 2^30 pixels),
  so a small file declaring hundreds of large frames cannot exhaust memory even when each frame
  individually fits inside `MaxPixels`.
- **Verification that had been claimed but not performed.** `samples/AotSmoke` publishes under
  `PublishAot` and exercises every codec, metadata path and processing stage in the published binary;
  `samples/Thumbnailer` publishes self-contained and trimmed and fails on a single `IL####` warning;
  `benchmarks/EasyImageSharp.Benchmarks` is a BenchmarkDotNet suite whose corpus is regenerated by a
  script rather than committed, and whose `--readme-table` mode regenerates the README's Performance
  table; and `tests/EasyImageSharp.Tests/Fixtures/check_determinism.py` regenerates the whole fixture
  corpus into a scratch directory and compares **decoded pixels** rather than file bytes.

### Changed

- **`SaveAsPng` on an image with more than one frame now writes an APNG** rather than writing only the
  root frame. This is the one behaviour change in the release. `image.Frames.ExportFrame(0)` produces a
  single-frame image to encode when a still PNG is what you want, and a single-frame image that is not
  marked animated is written exactly as it was in 1.0.1 — byte for byte, with no animation chunks.
- The fuzz harness reads `EASYIMAGESHARP_FUZZ_MUTATIONS` and `EASYIMAGESHARP_FUZZ_SEED` instead of
  requiring an edit to two compile-time constants. The seed is parsed as a `long` and narrowed, so a
  GitHub `run_id` can be passed straight through without silently falling back to the default. Defaults
  are unchanged at 400 mutations and seed 12345, and the seed corpus now includes the APNG fixtures.
- The README's Performance table was replaced with figures the committed benchmark suite produced, and
  the section now states how to regenerate it. The previous figures predated the removal of the
  benchmark project and could not be reproduced from a clean checkout.

### Fixed

- A decoder given a raised `MaxPixels` no longer throws `OverflowException` on a crafted header whose
  declared dimensions multiply beyond `int` range; the size limit is reported as
  `ImageSizeLimitExceededException` as the exception contract requires.
- The TIFF encoder no longer overflows `int` while computing strip, tile and directory sizes for large
  images, and the TIFF decoder validates offsets and counts against the file length before reading.
- The Netpbm encoder writes its ASCII header with the invariant culture, so a culture with non-ASCII
  digits or a different negative sign can no longer produce an unreadable file.

### Security

- A small multi-frame GIF, TIFF, Netpbm or ICO can no longer force unbounded allocation: frame counts
  and the new total-pixel budget are enforced before any pixel buffer is allocated, not per frame only.

## [1.0.1] - 2026-08-26

Packaging and documentation only. **No library code changed**, so there is nothing to gain by upgrading
except a tidier package page; `1.0.0` remains functionally identical.

### Changed

- The README header logo is now constrained to 128×128 with an `<img>` tag. The image itself is unchanged
  at 512×512 and remains the package icon, which nuget.org shows in the page sidebar regardless of the
  README. Note that nuget.org renders READMEs with HTML disabled, so the header image is dropped there;
  the package icon in the sidebar is unaffected.

### Fixed

- The README no longer advertises verification that is not performed. It claimed the public API surface is
  tracked in `PublicAPI.Shipped.txt` (the analyzer and its baselines were removed before 1.0.0), that CI
  regenerates the fixture corpus and fails on a byte change, and that CI runs Native AOT and trimming
  smoke publishes. The corresponding CI jobs have been removed — the fixture job compared zlib-ng
  compression output rather than fixture content, and the AOT, trimming and samples jobs ran against a
  `samples/` directory that no longer exists.
- `CONTRIBUTING.md` no longer documents a `benchmarks/` project, a `samples/` directory or a `docs/` site
  that are not in the repository, and no longer tells contributors that regenerating fixtures must leave
  `git status` clean.

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

[Unreleased]: https://github.com/FarhanLodi/EasyImageSharp/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/FarhanLodi/EasyImageSharp/releases/tag/v1.1.0
[1.0.1]: https://github.com/FarhanLodi/EasyImageSharp/releases/tag/v1.0.1
[1.0.0]: https://github.com/FarhanLodi/EasyImageSharp/releases/tag/v1.0.0
