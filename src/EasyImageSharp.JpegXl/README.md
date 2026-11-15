# EasyImageSharp.JpegXl

Optional JPEG XL decoder and encoder for [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp), backed by
the native libjxl library. The managed core stays dependency-free; install this package only where JPEG XL is
needed.

## Use

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

## Native library

| Platform | What to deploy |
|---|---|
| Windows x64 | Nothing beyond the Microsoft Visual C++ 2015-2022 runtime, which is present on most systems. The package ships the official upstream libjxl 0.12.0 release DLLs (`jxl`, `jxl_cms`, `jxl_threads` and the three Brotli libraries), fetched and checksum-verified by `scripts/build-libjxl.ps1`. |
| Linux | Install your distribution's libjxl package, version 0.9 or newer (`libjxl.so.0.x`). |
| macOS | `brew install jpeg-xl`. |

Set `EASYIMAGESHARP_LIBJXL_PATH` (and `EASYIMAGESHARP_LIBJXL_THREADS_PATH` for the threads library) to load those
exact files instead. Check `JpegXlFormat.IsNativeLibraryAvailable` and `NativeVersion` at startup. When the library
is missing, decoding and encoding throw `NotSupportedException` explaining what was not found.

## Licences

The package is MIT licensed. The bundled DLLs contain libjxl (BSD 3-clause), Brotli (MIT), Highway (Apache 2.0)
and skcms (BSD 3-clause). Their full texts are in `THIRD_PARTY_NOTICES.txt`.
