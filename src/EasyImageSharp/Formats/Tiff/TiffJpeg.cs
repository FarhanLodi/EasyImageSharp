using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>Everything the JPEG-in-TIFF path needs besides the coded segment itself.</summary>
/// <param name="Tables">
/// The JPEGTables tag (347): an abbreviated JPEG stream carrying the quantization and Huffman tables shared
/// by every segment of the page, or <see langword="null"/> when each segment is self-contained.
/// </param>
/// <param name="Options">The decoder options, so the JPEG inside a segment is held to the same limits.</param>
/// <param name="Samples">Samples per pixel of the decoded output: 1 for grey, 3 for colour.</param>
internal sealed record TiffJpegState(byte[]? Tables, DecoderOptions Options, int Samples);

/// <summary>
/// Decodes the JPEG-compressed strips and tiles of TIFF compression 7 through the library's own JPEG decoder.
/// </summary>
/// <remarks>
/// TIFF 6.0 Technical Note 2 stores each strip or tile as its own JPEG stream. The tables those streams need
/// are usually hoisted into the JPEGTables tag as an abbreviated table-only stream, which is prepended here
/// exactly as libtiff does. The JPEG decoder resolves the colour transform itself, so a YCbCr page (with any
/// subsampling) and an Adobe CMYK page both come back as RGB and the page's photometric interpretation plays
/// no further part.
/// </remarks>
internal static class TiffJpeg
{
    /// <summary>
    /// Decodes one JPEG-coded segment into <paramref name="target"/>, laid out as
    /// <paramref name="rows"/> rows of <c>width * samples</c> bytes.
    /// </summary>
    /// <param name="segment">The segment's coded bytes.</param>
    /// <param name="state">The page's shared JPEG state.</param>
    /// <param name="target">Receives the decoded samples; pixels the JPEG does not cover stay zero.</param>
    /// <param name="width">The segment's pixel width.</param>
    /// <param name="rows">The segment's row count.</param>
    public static void DecodeSegment(ReadOnlySpan<byte> segment, TiffJpegState state, Span<byte> target, int width, int rows)
    {
        target.Clear();
        byte[] stream = Assemble(segment, state.Tables);

        var core = new JpegDecoderCore(stream, stream.Length, state.Options);
        core.ParseAndDecode();
        using Image<Rgba32> image = core.ToImage<Rgba32>();

        int samples = state.Samples;
        int rowBytes = width * samples;
        int copyRows = Math.Min(rows, image.Height);
        int copyWidth = Math.Min(width, image.Width);
        ImageFrame<Rgba32> frame = image.Frames.RootFrame;

        for (int y = 0; y < copyRows; y++)
        {
            ReadOnlySpan<Rgba32> source = frame.GetRowSpan(y);
            Span<byte> destination = target.Slice(y * rowBytes, rowBytes);
            if (samples == 1)
            {
                for (int x = 0; x < copyWidth; x++)
                {
                    destination[x] = source[x].R;
                }
            }
            else
            {
                for (int x = 0; x < copyWidth; x++)
                {
                    Rgba32 pixel = source[x];
                    int i = x * samples;
                    destination[i] = pixel.R;
                    destination[i + 1] = pixel.G;
                    destination[i + 2] = pixel.B;
                }
            }
        }
    }

    /// <summary>
    /// Joins the shared table stream and one segment into a complete JPEG: the tables keep their SOI and lose
    /// their EOI, and the segment loses the SOI it usually repeats.
    /// </summary>
    private static byte[] Assemble(ReadOnlySpan<byte> segment, byte[]? tables)
    {
        bool segmentHasSoi = StartsWithSoi(segment);
        if (tables is null || tables.Length < 2 || !StartsWithSoi(tables))
        {
            if (!segmentHasSoi)
            {
                throw new InvalidImageContentException("JPEG-compressed TIFF segment does not start with an SOI marker.");
            }

            return segment.ToArray();
        }

        ReadOnlySpan<byte> prefix = tables.AsSpan();
        if (prefix.Length >= 4 && prefix[^2] == 0xFF && prefix[^1] == 0xD9)
        {
            prefix = prefix[..^2]; // Drop the abbreviated stream's EOI; the segment supplies the real one.
        }

        ReadOnlySpan<byte> body = segmentHasSoi ? segment[2..] : segment;
        var stream = new byte[prefix.Length + body.Length];
        prefix.CopyTo(stream);
        body.CopyTo(stream.AsSpan(prefix.Length));
        return stream;
    }

    private static bool StartsWithSoi(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;
}
