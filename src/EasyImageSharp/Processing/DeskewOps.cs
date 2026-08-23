using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Automatic document deskewing using projection profiling: finds the rotation angle that
/// maximizes the sharpness of the horizontal projection of dark (text) pixels. The Hough-based
/// alternative lives in <see cref="SkewOps"/>; both are reachable through <see cref="DeskewOptions"/>.
/// </summary>
internal static class DeskewOps
{
    private const int MaxSampleDimension = 1200;

    public static ImageFrame<TPixel> Deskew<TPixel>(ImageFrame<TPixel> frame, float maxAngleDegrees)
        where TPixel : unmanaged, IPixel<TPixel>
        => Deskew(frame, maxAngleDegrees, Rgba32.White);

    /// <summary>
    /// Deskews with the projection-profile estimator, filling the revealed corners with <paramref name="fill"/>.
    /// Pages whose estimated skew is below 0.25 degrees, or whose projection is not meaningfully sharper than
    /// at zero, are returned unchanged.
    /// </summary>
    public static ImageFrame<TPixel> Deskew<TPixel>(ImageFrame<TPixel> frame, float maxAngleDegrees, Rgba32 fill)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double bestAngle = EstimateProjectionSkew(frame, maxAngleDegrees, out bool significant);
        if (!significant)
        {
            return frame;
        }

        // Rotating by the negated angle aligns the text rows.
        return FrameOps.RotateArbitrary(frame, (float)(-bestAngle), fill);
    }

    /// <summary>
    /// Estimates the clockwise skew of the content (degrees) by projection profiling. <paramref name="significant"/>
    /// is <see langword="false"/> when there is too little (or too much) ink to judge, when the best angle is below
    /// 0.25 degrees, or when the projection at the best angle is not at least 5 % sharper than at zero.
    /// </summary>
    public static double EstimateProjectionSkew<TPixel>(ImageFrame<TPixel> frame, float maxAngleDegrees, out bool significant)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        significant = false;
        maxAngleDegrees = Math.Clamp(Math.Abs(maxAngleDegrees), 0.5f, 45f);

        // Downsample by striding so the search cost is independent of the input size.
        int step = Math.Max(1, Math.Max(frame.Width, frame.Height) / MaxSampleDimension);
        int sampledWidth = (frame.Width + step - 1) / step;
        int sampledHeight = (frame.Height + step - 1) / step;

        var gray = new byte[sampledWidth * sampledHeight];
        var histogram = new int[256];
        var rgbaRow = new Rgba32[frame.Width];
        for (int sy = 0; sy < sampledHeight; sy++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(Math.Min(sy * step, frame.Height - 1)), rgbaRow);
            int offset = sy * sampledWidth;
            for (int sx = 0; sx < sampledWidth; sx++)
            {
                byte l = PixelOps.Luminance8(rgbaRow[Math.Min(sx * step, frame.Width - 1)]);
                gray[offset + sx] = l;
                histogram[l]++;
            }
        }

        int threshold = FilterOps.ComputeOtsuThreshold(histogram, gray.Length);

        // Collect dark (text) pixel coordinates.
        var xs = new List<int>();
        var ys = new List<int>();
        for (int sy = 0; sy < sampledHeight; sy++)
        {
            int offset = sy * sampledWidth;
            for (int sx = 0; sx < sampledWidth; sx++)
            {
                if (gray[offset + sx] <= threshold)
                {
                    xs.Add(sx);
                    ys.Add(sy);
                }
            }
        }

        // Not enough content to estimate an angle reliably.
        if (xs.Count < 64 || xs.Count > gray.Length / 2)
        {
            return 0;
        }

        int[] xArray = xs.ToArray();
        int[] yArray = ys.ToArray();
        int diagonal = (int)Math.Ceiling(Math.Sqrt(((double)sampledWidth * sampledWidth) + ((double)sampledHeight * sampledHeight)));
        var bins = new int[(2 * diagonal) + 3];

        // Coarse search followed by fine refinement.
        double zeroScore = ProjectionScore(xArray, yArray, bins, diagonal, 0);
        double bestAngle = SearchBestAngle(xArray, yArray, bins, diagonal, -maxAngleDegrees, maxAngleDegrees, 1.0);
        bestAngle = SearchBestAngle(xArray, yArray, bins, diagonal, bestAngle - 1.0, bestAngle + 1.0, 0.1);
        double bestScore = ProjectionScore(xArray, yArray, bins, diagonal, bestAngle);

        // Only rotate when the projection is meaningfully sharper than at zero; this keeps
        // already-straight pages untouched instead of chasing noise.
        significant = !(Math.Abs(bestAngle) < 0.25 || bestScore <= zeroScore * 1.05);
        return bestAngle;
    }

    private static double SearchBestAngle(
        int[] xs, int[] ys, int[] bins, int diagonal, double from, double to, double stepSize)
    {
        double bestAngle = 0;
        double bestScore = double.MinValue;
        for (double angle = from; angle <= to + 1e-9; angle += stepSize)
        {
            double score = ProjectionScore(xs, ys, bins, diagonal, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>Sum of squared projection bin counts; maximal when text rows align with the projection axis.</summary>
    private static double ProjectionScore(int[] xs, int[] ys, int[] bins, int diagonal, double angleDegrees)
    {
        Array.Clear(bins);
        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int offset = diagonal + 1;

        for (int i = 0; i < xs.Length; i++)
        {
            int bin = (int)Math.Round((ys[i] * cos) - (xs[i] * sin)) + offset;
            bins[Math.Clamp(bin, 0, bins.Length - 1)]++;
        }

        double score = 0;
        foreach (int count in bins)
        {
            score += (double)count * count;
        }

        return score;
    }
}
