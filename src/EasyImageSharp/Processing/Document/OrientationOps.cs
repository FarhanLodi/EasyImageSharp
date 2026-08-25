using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Quarter-turn orientation heuristic for Latin-script pages. On a binarised downscale it (1) decides whether
/// text lines run horizontally or vertically from the derivative energy of the row versus column ink profiles,
/// then (2) decides upright versus upside-down from two asymmetries of the line profiles: ascenders carry more
/// ink than descenders (the zone above the dense x-height core is heavier than the zone below it) and lines
/// are left-aligned (the right edge is more ragged than the left). The confidence reports how clearly those
/// cues agreed; justified, centred, sparse or non-Latin pages yield low confidence.
/// </summary>
internal static class OrientationOps
{
    private const int MaxSampleDimension = 1000;

    public static OrientationEstimate Estimate<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
        => Estimate(DocumentPlanes.Luminance(frame), frame.Width, frame.Height);

    public static OrientationEstimate Estimate(byte[] luminance, int width, int height)
    {
        int factor = DocumentPlanes.DownscaleFactor(width, height, MaxSampleDimension);
        byte[] small = DocumentPlanes.Downscale(luminance, width, height, factor, out int sw, out int sh);
        if (sw < 8 || sh < 8)
        {
            return new OrientationEstimate(RotateMode.None, 0f);
        }

        int radius = Math.Max(4, Math.Min(sw, sh) / 25);
        byte[] background = IlluminationOps.EstimateBackground(small, sw, sh, radius);
        var mask = new byte[small.Length];
        var flat = new byte[small.Length];
        for (int i = 0; i < flat.Length; i++)
        {
            flat[i] = (byte)Math.Min(255, (small[i] * 255 + (background[i] / 2)) / Math.Max(1, (int)background[i]));
        }

        // Straighten small skews first: the profile cues need lines that project cleanly.
        SkewOps.SkewEstimate skew = SkewOps.Estimate(flat, sw, sh, 15f);
        if (skew.PointCount > 0 && Math.Abs(skew.AngleDegrees) >= 0.3 && skew.Strength > 1.02)
        {
            flat = DocumentPlanes.RotateNearest(flat, sw, sh, -skew.AngleDegrees, 255);
        }

        int threshold = DocumentPlanes.OtsuThreshold(flat);
        int ink = 0;
        for (int i = 0; i < flat.Length; i++)
        {
            if (flat[i] <= threshold)
            {
                mask[i] = 1;
                ink++;
            }
        }

        if (ink < 32 || ink > flat.Length / 2)
        {
            return new OrientationEstimate(RotateMode.None, 0f);
        }

        // Long rules and borders are not text: drop straight runs longer than a quarter of the page.
        int minRun = Math.Max(40, Math.Min(sw, sh) / 4);
        byte[] horizontalRuns = CleanupOps.LongRuns(mask, sw, sh, minRun, horizontal: true);
        byte[] verticalRuns = CleanupOps.LongRuns(mask, sw, sh, minRun, horizontal: false);
        for (int i = 0; i < mask.Length; i++)
        {
            if (horizontalRuns[i] != 0 || verticalRuns[i] != 0)
            {
                mask[i] = 0;
            }
        }

        // Evaluate the page as-is and rotated a quarter turn clockwise; 180 flips the sign of the asymmetry.
        Cues c0 = Analyse(mask, sw, sh);
        byte[] rotated = Rotate90(mask, sw, sh);
        Cues c90 = Analyse(rotated, sh, sw);

        bool horizontal = c0.Lineness >= c90.Lineness;
        Cues chosen = horizontal ? c0 : c90;
        double lineMax = Math.Max(c0.Lineness, c90.Lineness);
        double lineMin = Math.Min(c0.Lineness, c90.Lineness);
        double lineContrast = lineMax > 0 ? (lineMax - lineMin) / (lineMax + lineMin) : 0;

        double asymmetry = chosen.Asymmetry;
        RotateMode rotation;
        if (horizontal)
        {
            rotation = asymmetry >= 0 ? RotateMode.None : RotateMode.Rotate180;
        }
        else
        {
            rotation = asymmetry >= 0 ? RotateMode.Rotate90 : RotateMode.Rotate270;
        }

        double confidence = Math.Min(1, Math.Abs(asymmetry) * 1.5) * (0.4 + (0.6 * Math.Min(1, lineContrast * 2)));
        if (chosen.LineCount < 3)
        {
            confidence *= 0.3;
        }

        return new OrientationEstimate(rotation, (float)Math.Clamp(confidence, 0, 1));
    }

    private readonly record struct Cues(double Lineness, double Asymmetry, int LineCount);

    private static Cues Analyse(byte[] mask, int width, int height)
    {
        var rows = new int[height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask[row + x] != 0)
                {
                    rows[y]++;
                }
            }
        }

        // Horizontal text lines make the row profile alternate sharply between ink and gaps.
        double lineness = DerivativeEnergy(rows);

        // Segment lines from the row profile.
        var lines = new List<(int Y0, int Y1)>();
        int start = -1;
        int gap = 0;
        for (int y = 0; y <= height; y++)
        {
            bool inkRow = y < height && rows[y] > 0;
            if (inkRow)
            {
                if (start < 0)
                {
                    start = y;
                }

                gap = 0;
            }
            else if (start >= 0)
            {
                gap++;
                if (gap > 2 || y == height)
                {
                    int end = y - gap;
                    if (end - start + 1 >= 3)
                    {
                        lines.Add((start, end));
                    }

                    start = -1;
                    gap = 0;
                }
            }
        }

        if (lines.Count == 0)
        {
            return new Cues(lineness, 0, 0);
        }

        double aboveTotal = 0;
        double belowTotal = 0;
        var lefts = new List<double>();
        var rights = new List<double>();
        foreach ((int y0, int y1) in lines)
        {
            int max = 0;
            for (int y = y0; y <= y1; y++)
            {
                max = Math.Max(max, rows[y]);
            }

            double coreLevel = max * 0.5;
            int c0 = y0;
            while (c0 < y1 && rows[c0] < coreLevel)
            {
                c0++;
            }

            int c1 = y1;
            while (c1 > c0 && rows[c1] < coreLevel)
            {
                c1--;
            }

            for (int y = y0; y < c0; y++)
            {
                aboveTotal += rows[y];
            }

            for (int y = c1 + 1; y <= y1; y++)
            {
                belowTotal += rows[y];
            }

            int left = int.MaxValue;
            int right = -1;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (mask[row + x] != 0)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            if (right >= 0)
            {
                lefts.Add(left);
                rights.Add(right);
            }
        }

        double ascender = aboveTotal + belowTotal > 0 ? (aboveTotal - belowTotal) / (aboveTotal + belowTotal) : 0;
        double spreadLeft = Spread(lefts);
        double spreadRight = Spread(rights);
        double ragged = spreadLeft + spreadRight > 1e-9 ? (spreadRight - spreadLeft) / (spreadRight + spreadLeft) : 0;
        double asymmetry = (0.6 * ascender) + (0.4 * ragged);
        return new Cues(lineness, asymmetry, lines.Count);
    }

    /// <summary>Energy of the first derivative relative to the energy of the profile itself.</summary>
    private static double DerivativeEnergy(int[] profile)
    {
        double energy = 0;
        double diff = 0;
        for (int i = 0; i < profile.Length; i++)
        {
            energy += (double)profile[i] * profile[i];
            if (i > 0)
            {
                double d = profile[i] - profile[i - 1];
                diff += d * d;
            }
        }

        return energy > 0 ? diff / energy : 0;
    }

    /// <summary>Mean absolute deviation from the median: a spread measure that a few outliers (rules, noise) cannot dominate.</summary>
    private static double Spread(List<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        double median = sorted[sorted.Count / 2];
        double sum = 0;
        foreach (double v in values)
        {
            sum += Math.Abs(v - median);
        }

        return sum / values.Count;
    }

    /// <summary>Rotates a mask a quarter turn clockwise (result is height x width).</summary>
    internal static byte[] Rotate90(byte[] mask, int width, int height)
    {
        var result = new byte[mask.Length];
        int newWidth = height;
        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                // dest(x, y) = src(y, height - 1 - x)
                result[(y * newWidth) + x] = mask[((height - 1 - x) * width) + y];
            }
        }

        return result;
    }
}
