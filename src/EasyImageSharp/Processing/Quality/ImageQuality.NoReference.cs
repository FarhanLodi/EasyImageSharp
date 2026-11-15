using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>No-reference measurements: sharpness, exposure and noise.</summary>
public static partial class ImageQuality
{
    /// <summary>Bins of the luma histogram behind <see cref="ExposureMetrics"/> percentiles.</summary>
    internal const int ExposureHistogramBins = 4096;

    /// <summary>
    /// Measures focus on the root frame's BT.601 luma in 8-bit units (0-255): the variance of the four-neighbour
    /// Laplacian and the Tenengrad score (mean squared 3 x 3 Sobel gradient magnitude), both over the interior
    /// pixels (a one-pixel border is excluded instead of padded). Higher is sharper; the numbers are only
    /// comparable between images of similar content, size and noise.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image is smaller than 3 x 3.</exception>
    public static SharpnessMetrics MeasureSharpness<TPixel>(Image<TPixel> image, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        (double[] luma, int width, int height) = PrepareLuma(image);
        int rows = height - 2;
        double[] laplacianSum = new double[rows];
        double[] laplacianSquares = new double[rows];
        double[] gradientSquares = new double[rows];
        ParallelRowIterator.IterateRows(width, rows, (start, end) =>
        {
            for (int r = start; r < end; r++)
            {
                int y = r + 1;
                double sum = 0, squares = 0, gradient = 0;
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (y * width) + x;
                    double up = luma[i - width], down = luma[i + width], left = luma[i - 1], right = luma[i + 1];
                    double laplacian = up + down + left + right - (4 * luma[i]);
                    sum += laplacian;
                    squares += laplacian * laplacian;
                    double gx = (luma[i - width + 1] + (2 * right) + luma[i + width + 1]) - (luma[i - width - 1] + (2 * left) + luma[i + width - 1]);
                    double gy = (luma[i + width - 1] + (2 * down) + luma[i + width + 1]) - (luma[i - width - 1] + (2 * up) + luma[i - width + 1]);
                    gradient += (gx * gx) + (gy * gy);
                }

                laplacianSum[r] = sum;
                laplacianSquares[r] = squares;
                gradientSquares[r] = gradient;
            }
        });

        double count = (double)(width - 2) * rows;
        double mean = QualityPlanes.SumInOrder(laplacianSum) / count;
        double variance = Math.Max(0, (QualityPlanes.SumInOrder(laplacianSquares) / count) - (mean * mean));
        return new SharpnessMetrics(variance, QualityPlanes.SumInOrder(gradientSquares) / count);
    }

    /// <summary>
    /// Estimates the standard deviation of additive white Gaussian noise in 8-bit luma units with Immerkaer's
    /// method ("Fast Noise Variance Estimation", 1996): <c>sqrt(pi / 2) / (6 (W - 2)(H - 2))</c> times the sum of
    /// the absolute response to the kernel <c>[1 -2 1; -2 4 -2; 1 -2 1]</c> over the interior pixels. The kernel
    /// cancels smooth gradients but not texture or edges, so detailed images read noisier than they are.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image is smaller than 3 x 3.</exception>
    public static double EstimateNoise<TPixel>(Image<TPixel> image, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        (double[] luma, int width, int height) = PrepareLuma(image);
        int rows = height - 2;
        double[] rowSums = new double[rows];
        ParallelRowIterator.IterateRows(width, rows, (start, end) =>
        {
            for (int r = start; r < end; r++)
            {
                int y = r + 1;
                double sum = 0;
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (y * width) + x;
                    double response = luma[i - width - 1] - (2 * luma[i - width]) + luma[i - width + 1]
                        - (2 * luma[i - 1]) + (4 * luma[i]) - (2 * luma[i + 1])
                        + luma[i + width - 1] - (2 * luma[i + width]) + luma[i + width + 1];
                    sum += Math.Abs(response);
                }

                rowSums[r] = sum;
            }
        });

        return Math.Sqrt(Math.PI / 2) * QualityPlanes.SumInOrder(rowSums) / (6.0 * (width - 2) * rows);
    }

    /// <summary>
    /// Measures the tonal distribution of the root frame: BT.601 luma mean and percentiles, and the fractions of
    /// clipped pixels. Fully transparent pixels are ignored. A sample counts as clipped when it lies within half an
    /// 8-bit step of the end of its range (0 or 255 for 8-bit images).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public static ExposureMetrics MeasureExposure<TPixel>(Image<TPixel> image, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        using var execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        int width = frame.Width;
        int height = frame.Height;
        const double Low = 0.5 / 255;
        const double High = 1 - (0.5 / 255);
        double[] rowLumaSums = new double[height];
        long[] totals = new long[5];
        long[] combined = new long[ExposureHistogramBins];
        object gate = new();

        // Counts are integers, so merging the per-batch histograms in any order gives the same totals; the
        // floating-point luma sum is kept per row and added in row order.
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var rgb = new double[width * 3];
            var alpha = new double[width];
            var histogram = new long[ExposureHistogramBins];
            long pixels = 0, black = 0, white = 0, anyLow = 0, anyHigh = 0;
            for (int y = start; y < end; y++)
            {
                QualityPlanes.ReadRgb(frame, y, rgb);
                QualityPlanes.ReadAlpha(frame, y, alpha);
                double lumaSum = 0;
                for (int x = 0; x < width; x++)
                {
                    if (alpha[x] <= 0)
                    {
                        continue;
                    }

                    double r = rgb[x * 3], g = rgb[(x * 3) + 1], b = rgb[(x * 3) + 2];
                    double luma = (QualityPlanes.LumaR * r) + (QualityPlanes.LumaG * g) + (QualityPlanes.LumaB * b);
                    histogram[Math.Min(ExposureHistogramBins - 1, (int)(luma * ExposureHistogramBins))]++;
                    lumaSum += luma;
                    pixels++;
                    black += r <= Low && g <= Low && b <= Low ? 1 : 0;
                    white += r >= High && g >= High && b >= High ? 1 : 0;
                    anyLow += r <= Low || g <= Low || b <= Low ? 1 : 0;
                    anyHigh += r >= High || g >= High || b >= High ? 1 : 0;
                }

                rowLumaSums[y] = lumaSum;
            }

            lock (gate)
            {
                totals[0] += pixels;
                totals[1] += black;
                totals[2] += white;
                totals[3] += anyLow;
                totals[4] += anyHigh;
                for (int i = 0; i < histogram.Length; i++)
                {
                    combined[i] += histogram[i];
                }
            }
        });

        double mean = totals[0] == 0 ? 0 : QualityPlanes.SumInOrder(rowLumaSums) / totals[0];
        return new ExposureMetrics(totals[0], mean, totals[1], totals[2], totals[3], totals[4], combined);
    }

    private static (double[] Luma, int Width, int Height) PrepareLuma<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        if (frame.Width < 3 || frame.Height < 3)
        {
            throw new ArgumentException("The image must be at least 3x3 pixels.", nameof(image));
        }

        return (QualityPlanes.Luma255(frame), frame.Width, frame.Height);
    }
}
