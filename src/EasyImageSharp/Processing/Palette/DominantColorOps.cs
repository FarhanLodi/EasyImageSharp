using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Dominant colours by weighted k-means in OKLab (Ottosson 2020) over a 15-bit RGB histogram of point samples.
/// Everything is sequential with fixed tie-breaking, so the result is deterministic.
/// </summary>
internal static class DominantColorOps
{
    private const int Bins = 1 << 15;

    private static readonly double[] SrgbToLinear = BuildLinear();

    public static IReadOnlyList<DominantColor> Find<TPixel>(ImageFrame<TPixel> frame, int count, DominantColorOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int step = options.MaxSampleDimension == 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)options.MaxSampleDimension));

        // Histogram of 5-bit-per-channel bins holding the count and the exact OKLab sum of their samples.
        long[] binCount = new long[Bins];
        double[] binLab = new double[Bins * 3];
        long total = 0;
        var row = new Rgba32[width];
        for (int y = step / 2; y < height; y += step)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
            for (int x = step / 2; x < width; x += step)
            {
                Rgba32 p = row[x];
                if (p.A < options.MinimumAlpha)
                {
                    continue;
                }

                int bin = ((p.R >> 3) << 10) | ((p.G >> 3) << 5) | (p.B >> 3);
                (double l, double a, double b) = ToOklab(SrgbToLinear[p.R], SrgbToLinear[p.G], SrgbToLinear[p.B]);
                binCount[bin]++;
                binLab[bin * 3] += l;
                binLab[(bin * 3) + 1] += a;
                binLab[(bin * 3) + 2] += b;
                total++;
            }
        }

        if (total == 0)
        {
            return Array.Empty<DominantColor>();
        }

        // The occupied bins become weighted points at their mean colour.
        var points = new List<(double L, double A, double B, long Weight)>();
        for (int i = 0; i < Bins; i++)
        {
            if (binCount[i] > 0)
            {
                double n = binCount[i];
                points.Add((binLab[i * 3] / n, binLab[(i * 3) + 1] / n, binLab[(i * 3) + 2] / n, binCount[i]));
            }
        }

        int k = Math.Min(count, points.Count);
        double[] centres = Seed(points, k);
        int[] assignment = new int[points.Count];
        Array.Fill(assignment, -1);
        double[] sums = new double[k * 3];
        long[] weights = new long[k];
        for (int iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            OperationExecution.Checkpoint();
            bool changed = false;
            for (int i = 0; i < points.Count; i++)
            {
                int nearest = Nearest(centres, k, points[i].L, points[i].A, points[i].B);
                if (nearest != assignment[i])
                {
                    assignment[i] = nearest;
                    changed = true;
                }
            }

            if (!changed && iteration > 0)
            {
                break;
            }

            Accumulate(points, assignment, sums, weights);
            for (int c = 0; c < k; c++)
            {
                if (weights[c] > 0)
                {
                    centres[c * 3] = sums[c * 3] / weights[c];
                    centres[(c * 3) + 1] = sums[(c * 3) + 1] / weights[c];
                    centres[(c * 3) + 2] = sums[(c * 3) + 2] / weights[c];
                }
            }
        }

        Accumulate(points, assignment, sums, weights);
        var result = new List<(DominantColor Color, long Weight, int Index)>(k);
        for (int c = 0; c < k; c++)
        {
            if (weights[c] == 0)
            {
                continue;
            }

            double n = weights[c];
            Color color = ToColor(sums[c * 3] / n, sums[(c * 3) + 1] / n, sums[(c * 3) + 2] / n);
            result.Add((new DominantColor(color, (float)(weights[c] / (double)total)), weights[c], c));
        }

        result.Sort((x, y) => x.Weight != y.Weight ? y.Weight.CompareTo(x.Weight) : x.Index.CompareTo(y.Index));
        return result.Select(r => r.Color).ToArray();
    }

    /// <summary>
    /// Deterministic k-means++-style seeding: the heaviest point first, then repeatedly the point with the largest
    /// weighted squared distance to its nearest centre (the lowest index wins ties).
    /// </summary>
    private static double[] Seed(List<(double L, double A, double B, long Weight)> points, int k)
    {
        double[] centres = new double[k * 3];
        int first = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Weight > points[first].Weight)
            {
                first = i;
            }
        }

        double[] nearest = new double[points.Count];
        Array.Fill(nearest, double.MaxValue);
        int chosen = first;
        for (int c = 0; c < k; c++)
        {
            centres[c * 3] = points[chosen].L;
            centres[(c * 3) + 1] = points[chosen].A;
            centres[(c * 3) + 2] = points[chosen].B;
            double best = -1;
            int next = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double dl = points[i].L - centres[c * 3], da = points[i].A - centres[(c * 3) + 1], db = points[i].B - centres[(c * 3) + 2];
                nearest[i] = Math.Min(nearest[i], (dl * dl) + (da * da) + (db * db));
                double score = nearest[i] * points[i].Weight;
                if (score > best)
                {
                    best = score;
                    next = i;
                }
            }

            chosen = next;
        }

        return centres;
    }

    private static int Nearest(double[] centres, int k, double l, double a, double b)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int c = 0; c < k; c++)
        {
            double dl = l - centres[c * 3], da = a - centres[(c * 3) + 1], db = b - centres[(c * 3) + 2];
            double d = (dl * dl) + (da * da) + (db * db);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best;
    }

    private static void Accumulate(List<(double L, double A, double B, long Weight)> points, int[] assignment, double[] sums, long[] weights)
    {
        Array.Clear(sums);
        Array.Clear(weights);
        for (int i = 0; i < points.Count; i++)
        {
            int c = assignment[i];
            long w = points[i].Weight;
            sums[c * 3] += points[i].L * w;
            sums[(c * 3) + 1] += points[i].A * w;
            sums[(c * 3) + 2] += points[i].B * w;
            weights[c] += w;
        }
    }

    internal static (double L, double A, double B) ToOklab(double r, double g, double b)
    {
        double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return ((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    internal static Color ToColor(double lightness, double a, double b)
    {
        double l = lightness + (0.3963377774 * a) + (0.2158037573 * b);
        double m = lightness - (0.1055613458 * a) - (0.0638541728 * b);
        double s = lightness - (0.0894841775 * a) - (1.2914855480 * b);
        l *= l * l;
        m *= m * m;
        s *= s * s;
        double red = (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
        double green = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
        double blue = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);
        return new Color(Encode(red), Encode(green), Encode(blue));
    }

    private static byte Encode(double linear)
    {
        double v = Math.Clamp(linear, 0, 1);
        double encoded = v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static double[] BuildLinear()
    {
        var table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            table[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
