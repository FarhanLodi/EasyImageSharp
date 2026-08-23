using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Histogram equalization of the BT.709 luminance channel. Chroma is preserved by scaling each pixel's RGB
/// components by the ratio of new to old luminance. Mappings use the classic "step" formulation:
/// <c>lut[i] = floor((cdf_excl(i) + step / 2) / step)</c> with <c>step = (pixels - count(last occupied bin)) /
/// (levels - 1)</c>, so the darkest occupied level maps to 0 and the lightest to the top level.
/// </summary>
internal static class HistogramEqualizationOps
{
    public static void Equalize<TPixel>(ImageFrame<TPixel> frame, HistogramEqualizationOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int levels = options.LuminanceLevels;
        int width = frame.Width;
        int height = frame.Height;
        Rgba32[] pixels = RowProcessor.ReadRegion(frame, RowProcessor.Bounds(frame));
        var levelPlane = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            levelPlane[i] = (byte)ToLevel(PixelOps.Luminance8(pixels[i]), levels);
        }

        switch (options.Method)
        {
            case HistogramEqualizationMethod.Global:
                EqualizeGlobal(pixels, levelPlane, width, height, options);
                break;
            case HistogramEqualizationMethod.AdaptiveTileInterpolation:
                EqualizeTiles(pixels, levelPlane, width, height, options);
                break;
            case HistogramEqualizationMethod.AdaptiveSlidingWindow:
                EqualizeSlidingWindow(pixels, levelPlane, width, height, options);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Method, "Unknown histogram equalization method.");
        }

        RowProcessor.WriteRegion(frame, RowProcessor.Bounds(frame), pixels);
    }

    // ----- Global -----

    private static void EqualizeGlobal(Rgba32[] pixels, byte[] levelPlane, int width, int height, HistogramEqualizationOptions options)
    {
        int levels = options.LuminanceLevels;
        var histogram = new int[levels];
        foreach (byte level in levelPlane)
        {
            histogram[level]++;
        }

        if (options.ClipHistogram)
        {
            ClipHistogram(histogram, options.ClipLimit);
        }

        float[] lut = BuildLut(histogram, pixels.Length, levels);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int i = start * width; i < end * width; i++)
            {
                pixels[i] = Remap(pixels[i], lut[levelPlane[i]], levels);
            }
        });
    }

    // ----- CLAHE (tiles + bilinear interpolation) -----

    private static void EqualizeTiles(Rgba32[] pixels, byte[] levelPlane, int width, int height, HistogramEqualizationOptions options)
    {
        int levels = options.LuminanceLevels;
        int tilesX = Math.Min(options.NumberOfTiles, width);
        int tilesY = Math.Min(options.NumberOfTiles, height);

        // Tile i spans [i * size / tiles, (i + 1) * size / tiles).
        var startsX = new int[tilesX + 1];
        var startsY = new int[tilesY + 1];
        for (int i = 0; i <= tilesX; i++)
        {
            startsX[i] = (int)((long)i * width / tilesX);
        }

        for (int i = 0; i <= tilesY; i++)
        {
            startsY[i] = (int)((long)i * height / tilesY);
        }

        var centersX = new float[tilesX];
        var centersY = new float[tilesY];
        for (int i = 0; i < tilesX; i++)
        {
            centersX[i] = ((startsX[i] + startsX[i + 1]) * 0.5f) - 0.5f;
        }

        for (int i = 0; i < tilesY; i++)
        {
            centersY[i] = ((startsY[i] + startsY[i + 1]) * 0.5f) - 0.5f;
        }

        // One mapping per tile.
        var luts = new float[tilesX * tilesY][];
        Parallel.For(0, tilesX * tilesY, new ParallelOptions { MaxDegreeOfParallelism = Configuration.Default.MaxDegreeOfParallelism }, t =>
        {
            int tx = t % tilesX;
            int ty = t / tilesX;
            var histogram = new int[levels];
            int count = 0;
            for (int y = startsY[ty]; y < startsY[ty + 1]; y++)
            {
                int row = y * width;
                for (int x = startsX[tx]; x < startsX[tx + 1]; x++)
                {
                    histogram[levelPlane[row + x]]++;
                    count++;
                }
            }

            if (options.ClipHistogram)
            {
                ClipHistogram(histogram, options.ClipLimit);
            }

            luts[t] = BuildLut(histogram, count, levels);
        });

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                (int ty0, int ty1, float wy) = Locate(centersY, y);
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    (int tx0, int tx1, float wx) = Locate(centersX, x);
                    int level = levelPlane[row + x];
                    float top = Lerp(luts[(ty0 * tilesX) + tx0][level], luts[(ty0 * tilesX) + tx1][level], wx);
                    float bottom = Lerp(luts[(ty1 * tilesX) + tx0][level], luts[(ty1 * tilesX) + tx1][level], wx);
                    pixels[row + x] = Remap(pixels[row + x], Lerp(top, bottom, wy), levels);
                }
            }
        });
    }

    /// <summary>Finds the two tile centres bracketing <paramref name="position"/> and the interpolation weight of the second.</summary>
    private static (int Lower, int Upper, float Weight) Locate(float[] centers, int position)
    {
        int last = centers.Length - 1;
        if (last == 0 || position <= centers[0])
        {
            return (0, 0, 0f);
        }

        if (position >= centers[last])
        {
            return (last, last, 0f);
        }

        int upper = 1;
        while (centers[upper] < position)
        {
            upper++;
        }

        int lower = upper - 1;
        float weight = (position - centers[lower]) / (centers[upper] - centers[lower]);
        return (lower, upper, weight);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    // ----- Sliding window -----

    private static void EqualizeSlidingWindow(Rgba32[] pixels, byte[] levelPlane, int width, int height, HistogramEqualizationOptions options)
    {
        int levels = options.LuminanceLevels;
        int windowWidth = Math.Max(1, width / Math.Min(options.NumberOfTiles, width));
        int windowHeight = Math.Max(1, height / Math.Min(options.NumberOfTiles, height));
        int halfW = windowWidth / 2;
        int halfH = windowHeight / 2;
        Rgba32[] source = (Rgba32[])pixels.Clone();

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var histogram = new int[levels];
            var working = new int[levels];
            for (int y = start; y < end; y++)
            {
                int y0 = Math.Max(0, y - halfH);
                int y1 = Math.Min(height - 1, y + halfH);
                Array.Clear(histogram);
                int count = 0;

                // Histogram of the window at x = 0.
                int xEnd = Math.Min(width - 1, halfW);
                for (int wy = y0; wy <= y1; wy++)
                {
                    int row = wy * width;
                    for (int wx = 0; wx <= xEnd; wx++)
                    {
                        histogram[levelPlane[row + wx]]++;
                        count++;
                    }
                }

                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (x > 0)
                    {
                        // Slide: drop the column leaving on the left, add the column entering on the right.
                        int leaving = x - halfW - 1;
                        int entering = x + halfW;
                        if (leaving >= 0)
                        {
                            for (int wy = y0; wy <= y1; wy++)
                            {
                                histogram[levelPlane[(wy * width) + leaving]]--;
                                count--;
                            }
                        }

                        if (entering < width)
                        {
                            for (int wy = y0; wy <= y1; wy++)
                            {
                                histogram[levelPlane[(wy * width) + entering]]++;
                                count++;
                            }
                        }
                    }

                    int level = levelPlane[rowOffset + x];
                    float mapped;
                    if (options.ClipHistogram)
                    {
                        histogram.AsSpan().CopyTo(working);
                        ClipHistogram(working, options.ClipLimit);
                        mapped = MapLevel(working, count, level, levels);
                    }
                    else
                    {
                        mapped = MapLevel(histogram, count, level, levels);
                    }

                    pixels[rowOffset + x] = Remap(source[rowOffset + x], mapped, levels);
                }
            }
        });
    }

    // ----- Shared -----

    /// <summary>Quantises an 8-bit luminance to a histogram bin.</summary>
    internal static int ToLevel(int luminance8, int levels)
        => levels == 256 ? luminance8 : (int)((luminance8 * (levels - 1) / 255f) + 0.5f);

    /// <summary>Clips bins at <paramref name="limit"/> and spreads the excess evenly over all bins.</summary>
    internal static void ClipHistogram(int[] histogram, int limit)
    {
        long excess = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > limit)
            {
                excess += histogram[i] - limit;
                histogram[i] = limit;
            }
        }

        if (excess == 0)
        {
            return;
        }

        int perBin = (int)(excess / histogram.Length);
        int remainder = (int)(excess % histogram.Length);
        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] += perBin + (i < remainder ? 1 : 0);
        }
    }

    /// <summary>Builds the equalization mapping for every level (output levels, 0..levels-1).</summary>
    internal static float[] BuildLut(int[] histogram, int total, int levels)
    {
        var lut = new float[levels];
        int last = LastOccupied(histogram);
        int occupied = 0;
        for (int i = 0; i < levels; i++)
        {
            if (histogram[i] > 0)
            {
                occupied++;
            }
        }

        double step = last < 0 ? 0 : (total - histogram[last]) / (double)(levels - 1);
        if (occupied <= 1 || step <= 0)
        {
            for (int i = 0; i < levels; i++)
            {
                lut[i] = i;
            }

            return lut;
        }

        double n = step / 2;
        double max = levels - 1;
        for (int i = 0; i < levels; i++)
        {
            lut[i] = (float)Math.Min(max, Math.Floor(n / step));
            n += histogram[i];
        }

        return lut;
    }

    /// <summary>Computes the mapping of a single level; identical to <c>BuildLut(...)[level]</c>.</summary>
    internal static float MapLevel(int[] histogram, int total, int level, int levels)
    {
        int last = LastOccupied(histogram);
        int occupied = 0;
        for (int i = 0; i < levels && occupied < 2; i++)
        {
            if (histogram[i] > 0)
            {
                occupied++;
            }
        }

        double step = last < 0 ? 0 : (total - histogram[last]) / (double)(levels - 1);
        if (occupied <= 1 || step <= 0)
        {
            return level;
        }

        double n = step / 2;
        for (int i = 0; i < level; i++)
        {
            n += histogram[i];
        }

        return (float)Math.Min(levels - 1, Math.Floor(n / step));
    }

    private static int LastOccupied(int[] histogram)
    {
        for (int i = histogram.Length - 1; i >= 0; i--)
        {
            if (histogram[i] > 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Scales a pixel's colour so its luminance becomes <paramref name="mappedLevel"/> (in output levels), keeping chroma.</summary>
    private static Rgba32 Remap(Rgba32 pixel, float mappedLevel, int levels)
    {
        int oldLuminance = PixelOps.Luminance8(pixel);
        if (oldLuminance == 0)
        {
            return pixel;
        }

        float newLuminance = levels == 256 ? mappedLevel : mappedLevel * 255f / (levels - 1);
        float scale = newLuminance / oldLuminance;
        return new Rgba32(
            RowProcessor.ClampToByte(pixel.R * scale),
            RowProcessor.ClampToByte(pixel.G * scale),
            RowProcessor.ClampToByte(pixel.B * scale),
            pixel.A);
    }
}
