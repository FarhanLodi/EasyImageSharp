using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Hough-based skew estimation. Text-line bottom edges (ink pixels with a background pixel below) of a
/// background-normalised, Otsu-binarised downscale vote into a (rho, theta) accumulator; the angle whose
/// accumulator column has the largest energy (sum of squared bin counts, i.e. the sharpest projection) is
/// searched coarsely (0.5 degree), refined at 0.05 degree and interpolated parabolically.
/// </summary>
internal static class SkewOps
{
    private const int MaxSampleDimension = 800;
    private const int MaxPoints = 60_000;

    /// <summary>Result of a skew estimate: the clockwise angle of the content and the relative peak strength.</summary>
    public readonly record struct SkewEstimate(double AngleDegrees, double Strength, int PointCount);

    public static SkewEstimate Estimate<TPixel>(ImageFrame<TPixel> frame, float maxAngleDegrees)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] luminance = DocumentPlanes.Luminance(frame);
        return Estimate(luminance, frame.Width, frame.Height, maxAngleDegrees);
    }

    public static SkewEstimate Estimate(byte[] luminance, int width, int height, float maxAngleDegrees)
    {
        double maxAngle = Math.Clamp(Math.Abs(maxAngleDegrees), 0.5, 45.0);
        int factor = DocumentPlanes.DownscaleFactor(width, height, MaxSampleDimension);
        byte[] small = DocumentPlanes.Downscale(luminance, width, height, factor, out int sw, out int sh);
        if (sw < 8 || sh < 8)
        {
            return new SkewEstimate(0, 0, 0);
        }

        // Flatten illumination before thresholding so gradients and shadows do not swallow the text.
        int radius = Math.Max(4, Math.Min(sw, sh) / 25);
        byte[] background = IlluminationOps.EstimateBackground(small, sw, sh, radius);
        var flat = new byte[small.Length];
        for (int i = 0; i < flat.Length; i++)
        {
            flat[i] = (byte)Math.Min(255, (small[i] * 255 + (background[i] / 2)) / Math.Max(1, (int)background[i]));
        }

        int threshold = DocumentPlanes.OtsuThreshold(flat);
        var mask = new byte[flat.Length];
        int inkCount = 0;
        for (int i = 0; i < flat.Length; i++)
        {
            if (flat[i] <= threshold)
            {
                mask[i] = 1;
                inkCount++;
            }
        }

        // Ink must be a minority (a page), otherwise the polarity is unknown and we bail out.
        if (inkCount < 32 || inkCount > flat.Length / 2)
        {
            return new SkewEstimate(0, 0, 0);
        }

        // Bottom-edge pixels approximate the baselines.
        var xs = new List<int>();
        var ys = new List<int>();
        for (int y = 0; y < sh; y++)
        {
            int row = y * sw;
            for (int x = 0; x < sw; x++)
            {
                if (mask[row + x] != 0 && (y == sh - 1 || mask[row + sw + x] == 0))
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        if (xs.Count < 32)
        {
            return new SkewEstimate(0, 0, 0);
        }

        int stride = Math.Max(1, xs.Count / MaxPoints);
        int n = xs.Count / stride;
        var px = new int[n];
        var py = new int[n];
        for (int i = 0; i < n; i++)
        {
            px[i] = xs[i * stride];
            py[i] = ys[i * stride];
        }

        int diagonal = (int)Math.Ceiling(Math.Sqrt(((double)sw * sw) + ((double)sh * sh)));
        var accumulator = new int[(2 * diagonal) + 3];

        double zeroScore = Energy(px, py, accumulator, diagonal, 0.0);
        double bestCoarse = BestAngle(px, py, accumulator, diagonal, -maxAngle, maxAngle, 0.5, out _);
        double lo = Math.Max(-maxAngle, bestCoarse - 0.6);
        double hi = Math.Min(maxAngle, bestCoarse + 0.6);
        double bestFine = BestAngle(px, py, accumulator, diagonal, lo, hi, 0.05, out double bestScore);

        // Parabolic interpolation through the neighbours of the fine peak.
        double left = Energy(px, py, accumulator, diagonal, bestFine - 0.05);
        double right = Energy(px, py, accumulator, diagonal, bestFine + 0.05);
        double denominator = left - (2 * bestScore) + right;
        double refined = bestFine;
        if (denominator < 0)
        {
            double delta = 0.5 * (left - right) / denominator;
            if (Math.Abs(delta) <= 1)
            {
                refined = bestFine + (delta * 0.05);
            }
        }

        refined = Math.Clamp(refined, -maxAngle, maxAngle);
        double strength = zeroScore > 0 ? bestScore / zeroScore : 1;
        return new SkewEstimate(refined, strength, n);
    }

    private static double BestAngle(int[] xs, int[] ys, int[] bins, int diagonal, double from, double to, double step, out double bestScore)
    {
        double bestAngle = from;
        bestScore = double.MinValue;
        int steps = (int)Math.Round((to - from) / step);
        for (int i = 0; i <= steps; i++)
        {
            double angle = from + (i * step);
            double score = Energy(xs, ys, bins, diagonal, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>Sum of squared accumulator counts for the projection perpendicular to <paramref name="angleDegrees"/>.</summary>
    private static double Energy(int[] xs, int[] ys, int[] bins, int diagonal, double angleDegrees)
    {
        Array.Clear(bins);
        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int offset = diagonal + 1;
        int last = bins.Length - 1;
        for (int i = 0; i < xs.Length; i++)
        {
            int bin = (int)Math.Round((ys[i] * cos) - (xs[i] * sin)) + offset;
            bins[Math.Clamp(bin, 0, last)]++;
        }

        double score = 0;
        foreach (int count in bins)
        {
            score += (double)count * count;
        }

        return score;
    }
}
