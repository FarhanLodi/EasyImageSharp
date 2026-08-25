namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Builds the ALPH chunk that carries the alpha plane of a lossy WebP frame (RFC 9649 section 2.4): the plane
/// is optionally pre-filtered with one of the three spatial predictors and then stored either raw or as the
/// green channel of a VP8L image. Every filter that the effort level allows is tried and the smallest result
/// wins, so the choice is measured rather than guessed.
/// </summary>
internal static class WebpAlphaEncoder
{
    /// <summary>The four filtering methods the format defines.</summary>
    public const int FilterCount = 4;

    private const int FilterNone = 0;
    private const int FilterHorizontal = 1;
    private const int FilterVertical = 2;
    private const int FilterGradient = 3;

    /// <summary>Encodes an alpha plane into a complete ALPH chunk payload (its header byte included).</summary>
    /// <param name="alpha">The full-resolution alpha plane, row-major.</param>
    /// <param name="width">Plane width.</param>
    /// <param name="height">Plane height.</param>
    /// <param name="compression">Whether the filtered plane may be compressed with VP8L.</param>
    /// <param name="quality">1..100, passed through to the lossless encoder.</param>
    /// <param name="method">0..6 effort level; below 2 only one filter is tried.</param>
    public static byte[] Encode(byte[] alpha, int width, int height, WebpAlphaCompression compression, int quality, int method)
    {
        byte[]? best = null;
        int firstFilter = method >= 2 ? 0 : PredictFilter(alpha, width, height);
        int lastFilter = method >= 2 ? FilterCount - 1 : firstFilter;

        for (int filter = firstFilter; filter <= lastFilter; filter++)
        {
            byte[] filtered = ApplyFilter(alpha, width, height, filter);
            byte[] candidate = Assemble(filtered, width, height, filter, compression, quality, method);
            if (best is null || candidate.Length < best.Length)
            {
                best = candidate;
            }

            if (compression == WebpAlphaCompression.None)
            {
                // Raw storage is the same size whatever the filter does, so there is nothing left to compare.
                break;
            }
        }

        return best!;
    }

    private static byte[] Assemble(byte[] filtered, int width, int height, int filter, WebpAlphaCompression compression, int quality, int method)
    {
        byte[]? compressed = null;
        if (compression == WebpAlphaCompression.Lossless)
        {
            var green = new uint[filtered.Length];
            for (int i = 0; i < filtered.Length; i++)
            {
                green[i] = (uint)filtered[i] << 8;
            }

            compressed = Vp8LEncoder.EncodeStreamOnly(green, width, height, quality, method);
        }

        // The reference encoder falls back to raw storage for the rare plane that "compresses" to more bytes.
        if (compressed is not null && compressed.Length < filtered.Length)
        {
            var payload = new byte[compressed.Length + 1];
            payload[0] = (byte)(1 | (filter << 2));
            compressed.CopyTo(payload, 1);
            return payload;
        }

        var raw = new byte[filtered.Length + 1];
        raw[0] = (byte)(filter << 2);
        filtered.CopyTo(raw, 1);
        return raw;
    }

    /// <summary>Picks a filter from the summed absolute residuals, for effort levels that cannot afford to try all four.</summary>
    private static int PredictFilter(byte[] alpha, int width, int height)
    {
        long none = 0;
        long horizontal = 0;
        long vertical = 0;
        long gradient = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int above = row - width;
            for (int x = 0; x < width; x++)
            {
                int value = alpha[row + x];
                int left = x > 0 ? alpha[row + x - 1] : y > 0 ? alpha[above] : 0;
                int top = y > 0 ? alpha[above + x] : left;
                int topLeft = x > 0 && y > 0 ? alpha[above + x - 1] : top;
                none += value;
                horizontal += Math.Abs(value - left);
                vertical += Math.Abs(value - top);
                gradient += Math.Abs(value - GradientPredictor(left, top, topLeft));
            }
        }

        long best = Math.Min(Math.Min(none, horizontal), Math.Min(vertical, gradient));
        return best == none ? FilterNone : best == horizontal ? FilterHorizontal : best == vertical ? FilterVertical : FilterGradient;
    }

    /// <summary>Produces the residual plane for one filter; the exact inverse of what the decoder undoes.</summary>
    public static byte[] ApplyFilter(byte[] alpha, int width, int height, int filter)
    {
        var output = new byte[alpha.Length];
        if (filter == FilterNone)
        {
            alpha.CopyTo(output, 0);
            return output;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int above = row - width;
            if (y == 0 || filter == FilterHorizontal)
            {
                // The first row of every filter, and every row of the horizontal filter, predict from the left;
                // the very first pixel of a row predicts from the pixel above it, or from zero on the first row.
                int previous = y > 0 ? alpha[above] : 0;
                for (int x = 0; x < width; x++)
                {
                    output[row + x] = (byte)(alpha[row + x] - previous);
                    previous = alpha[row + x];
                }

                continue;
            }

            if (filter == FilterVertical)
            {
                for (int x = 0; x < width; x++)
                {
                    output[row + x] = (byte)(alpha[row + x] - alpha[above + x]);
                }

                continue;
            }

            int left = alpha[above];
            int topLeft = left;
            for (int x = 0; x < width; x++)
            {
                int top = alpha[above + x];
                output[row + x] = (byte)(alpha[row + x] - GradientPredictor(left, top, topLeft));
                topLeft = top;
                left = alpha[row + x];
            }
        }

        return output;
    }

    private static int GradientPredictor(int a, int b, int c)
    {
        int g = a + b - c;
        return (g & ~0xff) == 0 ? g : g < 0 ? 0 : 255;
    }
}
