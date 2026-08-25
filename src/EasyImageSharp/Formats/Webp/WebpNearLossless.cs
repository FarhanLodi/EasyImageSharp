namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// The near-lossless preprocessing the WebP encoder applies before the lossless bitstream is built: pixels
/// whose four-connected neighbourhood is already flat have their low bits discarded, which leaves edges and
/// detail untouched while giving the entropy coder long runs of identical values to work with.
/// </summary>
/// <remarks>
/// The reduction runs in several passes, from the coarsest bit depth down to one bit, and each pass only
/// touches pixels whose neighbours are within the pass's own tolerance. Quality 100 changes nothing; quality 0
/// discards five bits. The worst-case per-channel error is <c>2^bits - 1</c> for the bit depth
/// <see cref="BitsForQuality"/> reports.
/// </remarks>
internal static class WebpNearLossless
{
    /// <summary>Maps a near-lossless quality (0..100) onto the number of low bits the coarsest pass discards.</summary>
    public static int BitsForQuality(int quality) => 5 - (Math.Clamp(quality, 0, 100) / 20);

    /// <summary>The largest amount any channel can move at the given quality.</summary>
    public static int MaxErrorForQuality(int quality)
    {
        int bits = BitsForQuality(quality);
        return bits <= 0 ? 0 : (1 << bits) - 1;
    }

    /// <summary>Returns the preprocessed pixels, or the input itself when the quality asks for no reduction.</summary>
    public static uint[] Apply(uint[] argb, int width, int height, int quality)
    {
        int bits = BitsForQuality(quality);
        if (bits <= 0 || width <= 4 || height <= 4)
        {
            // Tiny images have too few interior pixels for the smoothness test to mean anything.
            return argb;
        }

        uint[] source = argb;
        uint[] destination = new uint[argb.Length];
        for (int pass = bits; pass > 0; pass--)
        {
            Reduce(source, destination, width, height, pass);
            if (ReferenceEquals(source, argb))
            {
                source = new uint[argb.Length];
            }

            (source, destination) = (destination, source);
        }

        return source;
    }

    private static void Reduce(uint[] source, uint[] destination, int width, int height, int bits)
    {
        int limit = 1 << bits;
        source.CopyTo(destination, 0);
        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            int above = row - width;
            int below = row + width;
            for (int x = 1; x < width - 1; x++)
            {
                uint value = source[row + x];
                if (IsNear(value, source[row + x - 1], limit)
                    && IsNear(value, source[row + x + 1], limit)
                    && IsNear(value, source[above + x], limit)
                    && IsNear(value, source[below + x], limit))
                {
                    destination[row + x] = Discretize(value, bits);
                }
            }
        }
    }

    private static bool IsNear(uint value, uint reference, int limit)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            int delta = (int)((value >> shift) & 0xff) - (int)((reference >> shift) & 0xff);
            if (delta >= limit || delta <= -limit)
            {
                return false;
            }
        }

        return true;
    }

    private static uint Discretize(uint argb, int bits)
        => (Closest(argb >> 24, bits) << 24)
            | (Closest((argb >> 16) & 0xff, bits) << 16)
            | (Closest((argb >> 8) & 0xff, bits) << 8)
            | Closest(argb & 0xff, bits);

    private static uint Closest(uint value, int bits)
    {
        uint mask = (1u << bits) - 1;
        uint biased = value + (mask >> 1) + ((value >> bits) & 1);
        return biased > 0xff ? 0xff : biased & ~mask;
    }
}
