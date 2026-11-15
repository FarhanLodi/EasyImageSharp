using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>
/// WebP-compressed TIFF segments (compression 50001, libtiff's <c>COMPRESSION_WEBP</c>): every strip or tile is a
/// complete WebP file, lossy or lossless, holding 8-bit RGB or RGBA. Each segment is decoded with the built-in
/// <see cref="WebpDecoder"/> and written back as interleaved samples, so the rest of the TIFF pipeline (alpha,
/// tiling, cropping to the image) treats it like any other decompressed segment.
/// </summary>
internal static class TiffWebp
{
    /// <summary>The compression tag value libtiff assigns to WebP.</summary>
    public const int Compression = 50001;

    /// <summary>
    /// Decodes one segment into <paramref name="target"/> (<paramref name="rows"/> rows of <paramref name="width"/>
    /// pixels with <paramref name="samples"/> 8-bit samples each). As for JPEG-in-TIFF, a coded image smaller than the
    /// segment leaves the rest zeroed and a larger one is cropped: edge tiles and the last strip may be coded at the
    /// full nominal segment size or at the size that remains.
    /// </summary>
    public static void DecodeSegment(ReadOnlySpan<byte> segment, Span<byte> target, int width, int rows, int samples)
    {
        target.Clear();

        // The page decode already holds the request's execution scope (cancellation, pixel budget), so the core
        // decoder is called directly. A segment may not declare more pixels than the segment itself holds.
        var options = new DecoderOptions { MaxPixels = Math.Max(1L, (long)width * rows), MaxFrames = 1 };
        using Image<Rgba32> image = WebpDecoder.DecodeCore<Rgba32>(segment.ToArray(), options);

        int rowBytes = width * samples;
        int copyRows = Math.Min(rows, image.Height);
        int copyWidth = Math.Min(width, image.Width);
        for (int y = 0; y < copyRows; y++)
        {
            OperationExecution.Checkpoint();
            Span<Rgba32> source = image.Frames.RootFrame.GetRowSpan(y);
            Span<byte> destination = target.Slice(y * rowBytes, rowBytes);
            for (int x = 0; x < copyWidth; x++)
            {
                Rgba32 p = source[x];
                int o = x * samples;
                destination[o] = p.R;
                destination[o + 1] = p.G;
                destination[o + 2] = p.B;
                if (samples == 4)
                {
                    destination[o + 3] = p.A;
                }
            }
        }
    }
}
