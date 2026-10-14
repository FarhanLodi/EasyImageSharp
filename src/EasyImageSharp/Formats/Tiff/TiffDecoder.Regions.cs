using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

public sealed partial class TiffDecoder
{
    internal static void DecompressRegionSegment(ReadOnlySpan<byte> encoded, Span<byte> decoded, int width, int height,
        int bits, int samples, int compression, int predictor, bool bigEndian)
    {
        var layout = new Layout(bits, samples, false, compression, predictor == 2, null, false, null);
        Decompress(layout, encoded, decoded, width, height);
        if (predictor == 2)
        {
            UndoPredictor(decoded, width, layout, bigEndian);
        }
    }

    internal static void ConvertRegionRow<TPixel>(ReadOnlySpan<byte> source, Span<TPixel> target, int bits, int samples,
        int photometric, bool bigEndian, Span<Rgba32> byteRow, Span<Rgba64> wideRow)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool alpha = samples == (photometric == 2 ? 4 : 2);
        var layout = new Layout(bits, samples, alpha, CompressionNone, false, null, false, null);
        if (bits == 16 && PixelOps.IsHighPrecision<TPixel>())
        {
            ConvertRow16(source, wideRow, target.Length, layout, photometric, bigEndian);
            PixelOps.Convert<Rgba64, TPixel>(wideRow, target);
        }
        else
        {
            ConvertRow(source, byteRow, target.Length, layout, photometric, null, bigEndian);
            PixelOps.FromRgba32<TPixel>(byteRow, target);
        }
    }
}
