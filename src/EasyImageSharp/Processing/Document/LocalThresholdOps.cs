using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Local (adaptive) thresholds built on the shared <see cref="IntegralImage"/> engine, plus the automatic
/// Otsu-vs-Sauvola selector used by <c>Binarize</c>. Every method classifies a pixel as white when its
/// luminance is at or above the local threshold, so a perfectly uniform region stays white.
/// </summary>
internal static class LocalThresholdOps
{
    /// <summary>Which local formula to evaluate.</summary>
    public enum Kind
    {
        Niblack,
        WolfJolion,
        Phansalkar,
        Nick,
    }

    /// <summary>Applies the local threshold in place, writing opaque black/white pixels.</summary>
    public static void Apply<TPixel>(ImageFrame<TPixel> frame, Kind kind, int windowSize, float k, float p = 2f, float q = 10f)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] luminance = DocumentPlanes.Luminance(frame);
        byte[] mask = ComputeInkMask(luminance, frame.Width, frame.Height, kind, windowSize, k, p, q);
        DocumentPlanes.WriteBinary(frame, mask);
    }

    /// <summary>Evaluates the local threshold and returns an ink mask (1 = below threshold = black).</summary>
    public static byte[] ComputeInkMask(byte[] luminance, int width, int height, Kind kind, int windowSize, float k, float p, float q)
    {
        if (windowSize <= 0)
        {
            windowSize = 25;
        }

        int half = windowSize / 2;
        var integral = new IntegralImage(luminance, width, height, withSquares: true);
        var mask = new byte[luminance.Length];

        // Wolf-Jolion needs two global statistics: the minimum grey level M and the largest local standard
        // deviation R over the image (both computed with the same clamped windows as the second pass).
        double globalMin = 0;
        double maxStd = 1;
        if (kind == Kind.WolfJolion)
        {
            globalMin = 255;
            foreach (byte v in luminance)
            {
                if (v < globalMin)
                {
                    globalMin = v;
                }
            }

            double[] rowMax = new double[height];
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    double best = 0;
                    for (int x = 0; x < width; x++)
                    {
                        integral.WindowStats(x, y, half, out _, out double variance, out _);
                        if (variance > best)
                        {
                            best = variance;
                        }
                    }

                    rowMax[y] = best;
                }
            });

            double maxVar = 0;
            foreach (double v in rowMax)
            {
                maxVar = Math.Max(maxVar, v);
            }

            maxStd = Math.Sqrt(maxVar);
            if (maxStd <= 0)
            {
                maxStd = 1;
            }
        }

        double minM = globalMin;
        double r = maxStd;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    integral.WindowStats(x, y, half, out double mean, out double variance, out int area);
                    double std = Math.Sqrt(variance);
                    double threshold = kind switch
                    {
                        Kind.Niblack => mean + (k * std),
                        Kind.WolfJolion => ((1 - k) * mean) + (k * minM) + (k * (std / r) * (mean - minM)),
                        Kind.Phansalkar => Phansalkar(mean, std, k, p, q),
                        _ => Nick(mean, variance, area, k),
                    };

                    mask[offset + x] = luminance[offset + x] >= threshold ? (byte)0 : (byte)1;
                }
            }
        });

        return mask;
    }

    /// <summary>Phansalkar et al. 2011: on intensities normalised to [0, 1], T = m (1 + p e^(-q m) + k (s / R - 1)) with R = 0.5.</summary>
    private static double Phansalkar(double mean, double std, float k, float p, float q)
    {
        double m = mean / 255.0;
        double s = std / 255.0;
        double t = m * (1 + (p * Math.Exp(-q * m)) + (k * ((s / 0.5) - 1)));
        return t * 255.0;
    }

    /// <summary>Khurshid et al. 2009 (NICK): T = m + k sqrt((sum(p^2) - m^2) / NP).</summary>
    private static double Nick(double mean, double variance, int area, float k)
    {
        // sum(p^2) / NP = variance + mean^2, so (sum(p^2) - m^2) / NP = variance + m^2 - m^2 / NP.
        double inner = variance + (mean * mean) - ((mean * mean) / area);
        return mean + (k * Math.Sqrt(Math.Max(0, inner)));
    }

    // ----- Automatic method selection -----

    /// <summary>Statistics behind the automatic Otsu-vs-Sauvola decision.</summary>
    public readonly record struct PageStatistics(int OtsuThreshold, double Separability, int BackgroundSpread);

    /// <summary>
    /// Decides between Otsu and Sauvola from (a) the Otsu separability of the luminance histogram and (b) the
    /// spread of the background brightness sampled over an 8x8 grid of blocks (mean of pixels above the Otsu
    /// threshold in each block). See <see cref="BinarizeOptions"/> for the rule.
    /// </summary>
    public static BinarizeMethod ChooseMethod(byte[] luminance, int width, int height, BinarizeOptions options, out PageStatistics statistics)
    {
        int[] histogram = DocumentPlanes.Histogram(luminance);
        int total = luminance.Length;
        int threshold = FilterOps.ComputeOtsuThreshold(histogram, total);

        // Separability = between-class variance / total variance at the Otsu threshold.
        double sumAll = 0;
        double sumSqAll = 0;
        for (int i = 0; i < 256; i++)
        {
            sumAll += (double)i * histogram[i];
            sumSqAll += (double)i * i * histogram[i];
        }

        double meanAll = sumAll / total;
        double totalVariance = (sumSqAll / total) - (meanAll * meanAll);
        long w0 = 0;
        double sum0 = 0;
        for (int i = 0; i <= threshold; i++)
        {
            w0 += histogram[i];
            sum0 += (double)i * histogram[i];
        }

        long w1 = total - w0;
        double separability = 0;
        if (w0 > 0 && w1 > 0 && totalVariance > 1e-9)
        {
            double mean0 = sum0 / w0;
            double mean1 = (sumAll - sum0) / w1;
            double between = (double)w0 / total * ((double)w1 / total) * (mean0 - mean1) * (mean0 - mean1);
            separability = between / totalVariance;
        }

        // Background brightness per block.
        const int Grid = 8;
        int bw = Math.Max(1, width / Grid);
        int bh = Math.Max(1, height / Grid);
        int minBackground = 255;
        int maxBackground = 0;
        bool anyBlock = false;
        for (int by = 0; by < Grid; by++)
        {
            int y0 = by * bh;
            int y1 = by == Grid - 1 ? height : Math.Min(height, y0 + bh);
            if (y0 >= height)
            {
                break;
            }

            for (int bx = 0; bx < Grid; bx++)
            {
                int x0 = bx * bw;
                int x1 = bx == Grid - 1 ? width : Math.Min(width, x0 + bw);
                if (x0 >= width)
                {
                    break;
                }

                long sum = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        int v = luminance[row + x];
                        if (v > threshold)
                        {
                            sum += v;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    int mean = (int)(sum / count);
                    minBackground = Math.Min(minBackground, mean);
                    maxBackground = Math.Max(maxBackground, mean);
                    anyBlock = true;
                }
            }
        }

        int spread = anyBlock ? maxBackground - minBackground : 0;
        statistics = new PageStatistics(threshold, separability, spread);
        bool evenAndCrisp = separability >= options.MinSeparability && spread <= options.IlluminationTolerance;
        return evenAndCrisp ? BinarizeMethod.Otsu : BinarizeMethod.Sauvola;
    }
}
