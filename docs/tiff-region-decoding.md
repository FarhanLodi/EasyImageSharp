# TIFF regions and bounded tile processing

`Image.OpenTiffRegionReader` reads classic TIFF and BigTIFF from a readable, seekable stream. It uses 64-bit offsets and dimensions, reads segment tables lazily, and decodes only intersecting strips or storage tiles. It never buffers the complete encoded source or creates a full-page image.

```csharp
using EasyImageSharp;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;

using var stream = File.OpenRead("large-slide.tif");
using var reader = Image.OpenTiffRegionReader(stream, pageIndex: 0,
    options: new TiffRegionOptions
    {
        MaxReadBytes = 128L * 1024 * 1024,
        MaxEncodedSegmentBytes = 32 * 1024 * 1024,
        MaxDecodedSegmentBytes = 64 * 1024 * 1024,
        MaxRegionPixels = 4096L * 4096,
    });

// The origin can exceed Int32.MaxValue; each returned image remains int-sized.
using Image<Rgb24> patch = reader.ReadRegion<Rgb24>(
    new TiffRegion(100_000, 200_000, 512, 512));

// Omit region to enumerate the whole page, including gigapixel pages.
foreach (var tile in reader.ReadTiles<Rgb24>(512, 512))
{
    using (tile)
    {
        Process(tile.Image, tile.Region.X, tile.Region.Y);
    }
}
```

`Process` represents application code. Each result is owned by the caller and must be disposed. Keeping all returned tiles also keeps all their pixel buffers; consume and dispose them incrementally. Early enumeration termination stops further reads. Each tile operation has its own execution scope, so a suspended iterator does not impose its allocation limits on unrelated work.

The reader accepts unsigned 8-bit and 16-bit chunky grayscale (BlackIsZero/WhiteIsZero) and RGB, optionally with explicitly unassociated alpha (`ExtraSamples=2`). Supported compression is None, LZW, Deflate (8/32946), and PackBits. Horizontal prediction is supported for LZW and Deflate. Both byte orders, strips, tiles, and independent page selection are supported. High-precision destination pixel types retain 16-bit samples.

Other layouts fail explicitly: planar data, palette, sub-byte/bilevel, floating point, associated or unspecified alpha, reversed fill order, JPEG-compressed TIFF, and unsupported predictors/codecs. Regions refer to stored pixels. EXIF orientation, ICC profiles, and other page metadata are not applied or imported. Use the regular decoder when these capabilities are required.

Uncompressed predictor-1 segments are read as requested row slices, even when an entire strip would exceed the segment budget. Compressed storage segments must fit both encoded and decoded segment limits; requesting a tiny region cannot avoid decoding the intersecting compressed segment. Processing tiles are independent of storage tiles, so overlapping requests can decode a storage segment repeatedly. There is no implicit segment cache.

`MaxReadBytes` limits actual requested source bytes separately for opening and each region operation, including repeated table reads. `MaxDirectoryEntries`, `MaxPages`, `MaxRegionPixels`, and `MaxTiles` bound directory traversal, page selection, output allocations, and enumeration. Segment limits bound each encoded/decoded scratch array, not total process memory: encoded and decoded buffers, row conversion buffers, pixel storage, and runtime overhead can coexist. `ExecutionOptions` supplies the shared pixel-buffer budget and cancellation scope; it is not a process-memory cap. Cancellation is checked during directory/source reads and between segments and rows; existing decompression internals may finish their current segment before cancellation is observed.

The stream's initial position is the TIFF origin. Keep the stream unchanged and exclusively available until the reader is disposed; concurrent calls are unsupported. `leaveOpen` defaults to `true`. Successful opening transfers stream-disposal responsibility only when `leaveOpen: false`; failed opening leaves ownership with the caller. Stream positions are not restored. Offsets beyond signed 64-bit stream addressing are rejected.

Tests compare regions against independently generated Pillow/hand-built TIFF fixtures and exercise sparse virtual sources: a 100,000 × 100,000 tiled BigTIFF with payload beyond 3 GiB, and a 3,000,000,000-pixel-wide raw strip beyond 4 GiB. Tiny regions require under 2 KiB of actual reads in these tests; no huge file or image allocation is needed. A corrupt unselected compressed tile proves that unused payloads are not read.
