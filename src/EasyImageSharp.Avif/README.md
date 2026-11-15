# EasyImageSharp.Avif

Optional AVIF decoder and encoder for [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp), backed by
the native libavif 1.x library with the libaom AV1 codec. The managed core stays dependency-free; install this
package only where AVIF is needed.

## Use

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

## Native library

| Platform | What to deploy |
|---|---|
| Windows x64 | Nothing: `avif.dll` ships in the package (`runtimes/win-x64/native`). It is built reproducibly from pinned libavif 1.4.2 and libaom 3.14.1 sources by `scripts/build-libavif.ps1`. libaom is built without assembly SIMD, so encoding is slower than distribution builds. |
| Linux | Install your distribution's libavif 1.x package, built with an AV1 encoder and decoder (it provides `libavif.so.16`, which is what is loaded). |
| macOS | `brew install libavif`. `libavif.16.dylib` is found in the default and Homebrew locations. |

Set `EASYIMAGESHARP_LIBAVIF_PATH` to a library file to load that exact copy instead. Check
`AvifFormat.IsNativeLibraryAvailable`, `NativeVersion` and `NativeCodecVersions` at startup. When the library is
missing, decoding and encoding throw `NotSupportedException` explaining what was not found.

## Licences

The package is MIT licensed. The bundled `avif.dll` contains libavif (BSD 2-clause) and libaom (BSD 2-clause, with
the Alliance for Open Media Patent License 1.0). Their full texts are in `THIRD_PARTY_NOTICES.txt`.
