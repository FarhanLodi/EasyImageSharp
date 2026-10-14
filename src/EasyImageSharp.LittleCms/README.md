# EasyImageSharp.LittleCms

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

## Runtime deployment

- **Windows x64:** run `scripts/build-littlecms.ps1` before building/packing. The script downloads SHA-256-pinned Zig 0.14.1 and upstream Little CMS 2.19.1 sources, then builds the native DLL locally. Generated binaries/toolchains stay under ignored `.artifacts/`. Every package includes `runtimes/win-x64/native/lcms2.dll`; packing on every OS fails if it is missing (CI transfers the Windows-built DLL to the pack job). No opaque binary is checked into source control.
- **Linux:** install `liblcms2-2` (or `liblcms2-dev` when compiling native reference tools) from the distribution. The runtime loads `liblcms2.so.2`. Package consumers must choose a maintained native version appropriate for their distribution.
- **macOS:** install Little CMS through Homebrew (`little-cms2`); standard Intel and Apple Silicon Homebrew locations (including the formula-specific `opt/little-cms2/lib` directories) are searched explicitly, so those installations do not require `DYLD_LIBRARY_PATH`. Custom prefixes must expose the dylib through the platform loader search path.
- **Other Windows architectures/mobile/browser:** not bundled or validated by this package. Supply a compatible native runtime and verify the application for its RID before use.

The native runtime used locally is built from upstream commit `21c582a594fe5279f90c0b93437c398f93bf62b0` (2.19.1). `THIRD_PARTY_NOTICES.txt` includes its MIT license. The small binding uses source-generated P/Invoke directly against the official C API; there is no separate managed wrapper dependency.

## Boundaries

Device-link chains, named-color profiles, soft-proofing/gamut-warning workflows, multichannel ink sets beyond CMYK, arbitrary ICC plugins and custom intents are not exposed. Rendering intent and black-point compensation work within the source/destination profiles and engine capabilities; they do not make out-of-gamut conversion reversible. Invalid/unsupported profiles fail explicitly.

Reference tests use upstream's synthetic test profiles, clearly labeled unsuitable for real production color reproduction. Golden RGB/CMYK/LUT results come from the separately compiled upstream `transicc` command-line application, not this adapter. Run `scripts/build-littlecms.ps1 -BuildReferenceTool` to rebuild that tool.

Primary references: [Little CMS engine](https://www.littlecms.com/color-engine/), [upstream 2.19.1 source and C API](https://github.com/mm2/Little-CMS/tree/lcms2.19.1), [upstream transicc reference tool](https://github.com/mm2/Little-CMS/blob/lcms2.19.1/utils/transicc/transicc.c).
