# EasyImageSharp.Codecs

Optional native codecs and colour management for [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp),
in one package: **AVIF** (libavif with libaom), **JPEG XL** (libjxl) and **ICC colour management** (Little CMS).
The core EasyImageSharp package stays managed and dependency-free; install this one only where those formats or
native ICC transforms are needed.

```
dotnet add package EasyImageSharp.Codecs
```

The namespaces are unchanged from the earlier separate packages (`EasyImageSharp.Avif`, `EasyImageSharp.JpegXl`,
`EasyImageSharp.LittleCms`), so code written against `EasyImageSharp.LittleCms` 1.2.0 only needs the package
reference changed. Windows x64 runtimes for all three libraries are bundled; on Linux and macOS each feature
uses the system library and reports `NotSupportedException` with an explanation when it is missing, without
affecting the others.

## AVIF

Optional AVIF decoder and encoder for [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp), backed by
the native libavif 1.x library with the libaom AV1 codec. The managed core stays dependency-free; install this
package only where AVIF is needed.

### Use

```csharp
using EasyImageSharp;
using EasyImageSharp.Avif;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;

var options = new DecoderOptions { AdditionalFormats = [AvifFormat.Instance] };
using Image<Rgba32> image = Image.Load<Rgba32>("photo.avif", options);

image.SaveAsAvif("out.avif", new AvifEncoder { Quality = 75, Speed = 6 });
image.SaveAsAvif("exact.avif", new AvifEncoder { Lossless = true });
```

- **Decoding:** still images and image sequences, 8-, 10- and 12-bit. Wide pixel types (`Rgba64`,
  `RgbaVector`) keep 10/12-bit precision. Alpha is returned un-premultiplied. ICC, EXIF and XMP fill
  `ImageMetadata`; AV1 colour facts, timing and loop count go to `AvifMetadata` and `AvifFrameMetadata`.
- **Encoding:** quality, alpha quality, speed (0-10), bit depth (8/10/12), chroma subsampling, frame durations
  and repetition count. `Lossless` stores 8-bit RGB exactly (identity matrix, 4:4:4).
- **Limits:** `MaxPixels`, `MaxTotalPixels`, `MaxFrames` and `MaxEncodedBytes` are checked from the container
  before any AV1 data is decoded or pixels are allocated. Cancellation is observed between frames.
- **Not applied:** the rotation, mirror and clean-aperture properties (`irot`, `imir`, `clap`). Rotation and
  mirroring are reported in `AvifMetadata` for the caller to apply.

### Native library

| Platform | What to deploy |
|---|---|
| Windows x64 | Nothing: `avif.dll` ships in the package (`runtimes/win-x64/native`). It is built reproducibly from pinned libavif 1.4.2 and libaom 3.14.1 sources by `scripts/build-libavif.ps1`. libaom is built without assembly SIMD, so encoding is slower than distribution builds. |
| Linux | Install your distribution's libavif 1.x package, built with an AV1 encoder and decoder (it provides `libavif.so.16`, which is what is loaded). |
| macOS | `brew install libavif`. `libavif.16.dylib` is found in the default and Homebrew locations. |

Set `EASYIMAGESHARP_LIBAVIF_PATH` to a library file to load that exact copy instead. Check
`AvifFormat.IsNativeLibraryAvailable`, `NativeVersion` and `NativeCodecVersions` at startup. When the library is
missing, decoding and encoding throw `NotSupportedException` explaining what was not found.

### Licences

The bundled `avif.dll` contains libavif (BSD 2-clause) and libaom (BSD 2-clause, with
the Alliance for Open Media Patent License 1.0). Their full texts are in `THIRD_PARTY_NOTICES.txt`.

## JPEG XL

Optional JPEG XL decoder and encoder for [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp), backed by
the native libjxl library. The managed core stays dependency-free; install this package only where JPEG XL is
needed.

### Use

```csharp
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.JpegXl;
using EasyImageSharp.PixelFormats;

var options = new DecoderOptions { AdditionalFormats = [JpegXlFormat.Instance] };
using Image<Rgba32> image = Image.Load<Rgba32>("photo.jxl", options);

image.SaveAsJxl("out.jxl", new JpegXlEncoder { Distance = 1.0f, Effort = 7 });
image.SaveAsJxl("exact.jxl", new JpegXlEncoder { Lossless = true });
```

- **Decoding:** 8- and 16-bit and floating-point images, alpha, animation (frames, durations, loop count), and
  ICC, EXIF and XMP boxes (including Brotli-compressed `brob` boxes). libjxl applies the codestream orientation.
- **Encoding:** lossless, or lossy by Butteraugli distance; effort; animation; optional compressed metadata boxes.
- **Limits:** `MaxPixels`, `MaxTotalPixels` and `MaxFrames` are enforced before pixel buffers are allocated.
  Frames past `MaxFrames` are skipped without being decoded. Cancellation is observed while decoding.

### Native library

| Platform | What to deploy |
|---|---|
| Windows x64 | Nothing beyond the Microsoft Visual C++ 2015-2022 runtime, which is present on most systems. The package ships the official upstream libjxl 0.12.0 release DLLs (`jxl`, `jxl_cms`, `jxl_threads` and the three Brotli libraries), fetched and checksum-verified by `scripts/build-libjxl.ps1`. |
| Linux | Install your distribution's libjxl package, version 0.9 or newer (`libjxl.so.0.x`). |
| macOS | `brew install jpeg-xl`. |

Set `EASYIMAGESHARP_LIBJXL_PATH` (and `EASYIMAGESHARP_LIBJXL_THREADS_PATH` for the threads library) to load those
exact files instead. Check `JpegXlFormat.IsNativeLibraryAvailable` and `NativeVersion` at startup. When the library
is missing, decoding and encoding throw `NotSupportedException` explaining what was not found.

### Licences

The bundled libjxl DLLs contain libjxl (BSD 3-clause), Brotli (MIT), Highway (Apache 2.0)
and skcms (BSD 3-clause). Their full texts are in `THIRD_PARTY_NOTICES.txt`.

## ICC colour management (Little CMS)

Optional native ICC color management powered by Little CMS 2.19.1. The core EasyImageSharp assembly remains dependency-free.

Supports standard ICC v2/v4 input, display, output, abstract and color-space profiles with **RGB, CMYK, gray, Lab and XYZ** channels. Little CMS evaluates matrix/TRC and LUT transforms, including profile rendering-intent tables and black-point compensation. The four ICC intents are exposed. Missing intent tables follow the engine's standard fallback rules. This is broader than the managed core's matrix/TRC subset; it is not a promise that every malformed or proprietary profile can be used.

```csharp
using EasyImageSharp.LittleCms;
using EasyImageSharp.Metadata.Icc;

var source = new IccProfile(File.ReadAllBytes("input.icc"));
var srgb = IccTransform.CreateSrgbProfile();
var options = new IccTransformOptions
{
    Intent = IccRenderingIntent.RelativeColorimetric,
    BlackPointCompensation = true,
};
using var converted = image.ConvertWithIcc(source, srgb, options);

// For raw CMYK data, floats are C,M,Y,K percentages in 0-100 (not 0-1).
using var rgb = IccImageExtensions.FromCmyk<Rgba32>(cmyk, width, height, cmykProfile, srgb, options);

// Generic reusable buffer transform, including RGB -> CMYK and profile LUT paths.
using var transform = IccTransform.Create(srgb, cmykProfile, options);
transform.Transform(rgbFloats, cmykFloats, pixelCount);
```

`ConvertWithIcc` copies all frames, preserves alpha and attaches the destination profile to the result. Source/destination profiles are explicit; an embedded profile is not silently substituted. General buffer transforms use interleaved floats without alpha: RGB/gray in 0-1, CMYK in 0-100 percent, Lab in natural L*,a*,b* units and XYZ relative to Y=1. Inputs must be finite and buffers must not overlap. Cancellation checks between chunks can leave a partially written caller buffer; image conversion discards incomplete results.

Transforms own native handles and serialize calls/disposal. Dispose them when finished. Native LUT allocation is outside core image-buffer budgets. Profile parsing and transform construction are native operations without mid-call cancellation.

### Runtime deployment

- **Windows x64:** run `scripts/build-littlecms.ps1` before building/packing. The script downloads SHA-256-pinned Zig 0.14.1 and upstream Little CMS 2.19.1 sources, then builds the native DLL locally. Generated binaries/toolchains stay under ignored `.artifacts/`. Every package includes `runtimes/win-x64/native/lcms2.dll`; packing on every OS fails if it is missing (CI transfers the Windows-built DLL to the pack job). No opaque binary is checked into source control.
- **Linux:** install `liblcms2-2` (or `liblcms2-dev` when compiling native reference tools) from the distribution. The runtime loads `liblcms2.so.2`. Package consumers must choose a maintained native version appropriate for their distribution.
- **macOS:** install Little CMS through Homebrew (`little-cms2`); standard Intel and Apple Silicon Homebrew locations (including the formula-specific `opt/little-cms2/lib` directories) are searched explicitly, so those installations do not require `DYLD_LIBRARY_PATH`. Custom prefixes must expose the dylib through the platform loader search path.
- **Other Windows architectures/mobile/browser:** not bundled or validated by this package. Supply a compatible native runtime and verify the application for its RID before use.

The native runtime used locally is built from upstream commit `21c582a594fe5279f90c0b93437c398f93bf62b0` (2.19.1). `THIRD_PARTY_NOTICES.txt` includes its MIT license. The small binding uses source-generated P/Invoke directly against the official C API; there is no separate managed wrapper dependency.

### Boundaries

Device-link chains, named-color profiles, soft-proofing/gamut-warning workflows, multichannel ink sets beyond CMYK, arbitrary ICC plugins and custom intents are not exposed. Rendering intent and black-point compensation work within the source/destination profiles and engine capabilities; they do not make out-of-gamut conversion reversible. Invalid/unsupported profiles fail explicitly.

Reference tests use upstream's synthetic test profiles, clearly labeled unsuitable for real production color reproduction. Golden RGB/CMYK/LUT results come from the separately compiled upstream `transicc` command-line application, not this adapter. Run `scripts/build-littlecms.ps1 -BuildReferenceTool` to rebuild that tool.

Primary references: [Little CMS engine](https://www.littlecms.com/color-engine/), [upstream 2.19.1 source and C API](https://github.com/mm2/Little-CMS/tree/lcms2.19.1), [upstream transicc reference tool](https://github.com/mm2/Little-CMS/blob/lcms2.19.1/utils/transicc/transicc.c).

## Licence

The package is MIT licensed. Every bundled native library's licence text is reproduced verbatim in `THIRD_PARTY_NOTICES.txt`.
