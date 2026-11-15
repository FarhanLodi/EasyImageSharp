using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Image quality metrics: full-reference comparisons (<see cref="Psnr"/>, <see cref="Ssim"/>,
/// <see cref="MsSsim"/>) and no-reference measurements (<see cref="MeasureSharpness"/>,
/// <see cref="MeasureExposure"/>, <see cref="EstimateNoise"/>). Every metric reads the root frame only.
/// </summary>
/// <remarks>
/// Full-reference metrics compare the colour channels; alpha is ignored. Values are taken as encoded
/// (gamma-corrected) R'G'B' in 0-1, so 8-bit images give the familiar <c>MAX = 255</c> numbers. Luma is BT.601
/// <c>Y' = 0.299 R' + 0.587 G' + 0.114 B'</c>, the plane SSIM was originally evaluated on. Results do not depend
/// on <see cref="Configuration.MaxDegreeOfParallelism"/>: partial sums are combined in a fixed row order.
/// </remarks>
public static partial class ImageQuality
{
    /// <summary>Side of the SSIM Gaussian window.</summary>
    private const int WindowSize = 11;

    /// <summary>Standard deviation of the SSIM Gaussian window.</summary>
    private const double WindowSigma = 1.5;

    /// <summary>SSIM stabilising constant <c>(K1 L)^2</c> with K1 = 0.01 and dynamic range L = 1.</summary>
    private const double C1 = 0.01 * 0.01;

    /// <summary>SSIM stabilising constant <c>(K2 L)^2</c> with K2 = 0.03 and dynamic range L = 1.</summary>
    private const double C2 = 0.03 * 0.03;

    /// <summary>The per-scale exponents of MS-SSIM, finest scale first (Wang, Simoncelli and Bovik 2003).</summary>
    private static readonly double[] MsSsimWeights = { 0.0448, 0.2856, 0.3001, 0.2363, 0.1333 };

    private static readonly double[] GaussianWindow = BuildGaussianWindow();

    /// <summary>
    /// The smallest width and height <see cref="MsSsim"/> accepts: five scales, each halving the image, with
    /// the coarsest still at least as large as the 11 x 11 window.
    /// </summary>
    public const int MsSsimMinimumDimension = 161;

    /// <summary>
    /// Peak signal-to-noise ratio in decibels, <c>10 log10(1 / MSE)</c> on values in 0-1 (identical to the
    /// <c>MAX = 255</c> form for 8-bit images). <see cref="QualityChannels.Rgb"/> pools the squared errors of
    /// all three channels. Identical images give <see cref="double.PositiveInfinity"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An image is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    public static double Psnr<TReference, TDistorted>(
        Image<TReference> reference,
        Image<TDistorted> distorted,
        QualityChannels channels = QualityChannels.Luma,
        CancellationToken cancellationToken = default)
        where TReference : unmanaged, IPixel<TReference>
        where TDistorted : unmanaged, IPixel<TDistorted>
    {
        (double[][] a, double[][] b, int width, int height, OperationExecution execution) = Prepare(reference, distorted, channels, cancellationToken, 1);
        using (execution)
        {
            double sum = 0;
            for (int p = 0; p < a.Length; p++)
            {
                double[] x = a[p];
                double[] y = b[p];
                double[] rowSums = new double[height];
                ParallelRowIterator.IterateRows(width, height, (start, end) =>
                {
                    for (int row = start; row < end; row++)
                    {
                        double s = 0;
                        for (int i = row * width; i < (row + 1) * width; i++)
                        {
                            double d = x[i] - y[i];
                            s += d * d;
                        }

                        rowSums[row] = s;
                    }
                });
                sum += QualityPlanes.SumInOrder(rowSums);
            }

            double mse = sum / ((double)width * height * a.Length);
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(1 / mse);
        }
    }

    /// <summary>
    /// Mean structural similarity (Wang, Bovik, Sheikh and Simoncelli 2004): an 11 x 11 Gaussian window with
    /// sigma 1.5, K1 = 0.01, K2 = 0.03, population (co)variances, averaged over every window position that lies
    /// fully inside the image (no padding and no automatic downsampling). <see cref="QualityChannels.Rgb"/>
    /// averages the three channel SSIMs. 1 means identical; the value can be negative for anti-correlated images.
    /// </summary>
    /// <exception cref="ArgumentNullException">An image is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The images differ in size or are smaller than 11 x 11.</exception>
    public static double Ssim<TReference, TDistorted>(
        Image<TReference> reference,
        Image<TDistorted> distorted,
        QualityChannels channels = QualityChannels.Luma,
        CancellationToken cancellationToken = default)
        where TReference : unmanaged, IPixel<TReference>
        where TDistorted : unmanaged, IPixel<TDistorted>
    {
        (double[][] a, double[][] b, int width, int height, OperationExecution execution) = Prepare(reference, distorted, channels, cancellationToken, WindowSize);
        using (execution)
        {
            double total = 0;
            for (int p = 0; p < a.Length; p++)
            {
                total += SsimCore(a[p], b[p], width, height).Ssim;
            }

            return total / a.Length;
        }
    }

    /// <summary>
    /// Multi-scale SSIM (Wang, Simoncelli and Bovik 2003) over five scales with the published weights
    /// 0.0448, 0.2856, 0.3001, 0.2363 and 0.1333: the contrast-structure term of the four finer scales and the
    /// full SSIM of the coarsest scale, each from the same window as <see cref="Ssim"/>. Between scales the image
    /// is low-passed with a 2 x 2 average and subsampled by two (the last row and column are repeated for odd
    /// sizes), as in the authors' reference code. Negative per-scale terms are clamped to 0 before exponentiation.
    /// </summary>
    /// <exception cref="ArgumentNullException">An image is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The images differ in size or either side is below <see cref="MsSsimMinimumDimension"/>.</exception>
    public static double MsSsim<TReference, TDistorted>(
        Image<TReference> reference,
        Image<TDistorted> distorted,
        QualityChannels channels = QualityChannels.Luma,
        CancellationToken cancellationToken = default)
        where TReference : unmanaged, IPixel<TReference>
        where TDistorted : unmanaged, IPixel<TDistorted>
    {
        (double[][] a, double[][] b, int width, int height, OperationExecution execution) = Prepare(reference, distorted, channels, cancellationToken, MsSsimMinimumDimension);
        using (execution)
        {
            double total = 0;
            for (int p = 0; p < a.Length; p++)
            {
                double[] x = a[p];
                double[] y = b[p];
                int w = width;
                int h = height;
                double product = 1;
                for (int scale = 0; scale < MsSsimWeights.Length; scale++)
                {
                    (double ssim, double cs) = SsimCore(x, y, w, h);
                    bool last = scale == MsSsimWeights.Length - 1;
                    product *= Math.Pow(Math.Max(0, last ? ssim : cs), MsSsimWeights[scale]);
                    if (!last)
                    {
                        x = Downsample(x, w, h);
                        y = Downsample(y, w, h);
                        w = (w + 1) / 2;
                        h = (h + 1) / 2;
                    }
                }

                total += product;
            }

            return total / a.Length;
        }
    }

    /// <summary>
    /// Mean SSIM and mean contrast-structure term of two planes over the valid window positions. Rows are
    /// filtered horizontally into a ring of the 11 rows the vertical pass needs, and each output row's sums are
    /// kept separately so the result is independent of how the rows are split across threads.
    /// </summary>
    internal static (double Ssim, double Cs) SsimCore(double[] a, double[] b, int width, int height)
    {
        int outWidth = width - WindowSize + 1;
        int outHeight = height - WindowSize + 1;
        double[] rowSsim = new double[outHeight];
        double[] rowCs = new double[outHeight];
        double[] kernel = GaussianWindow;
        ParallelRowIterator.IterateRows(outWidth, outHeight, (start, end) =>
        {
            // Five quantities per filtered row: x, y, x^2, y^2, x*y.
            double[] ring = new double[WindowSize * 5 * outWidth];
            void FilterRow(int y)
            {
                int slot = (y % WindowSize) * 5 * outWidth;
                int rowStart = y * width;
                for (int x = 0; x < outWidth; x++)
                {
                    double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                    for (int k = 0; k < WindowSize; k++)
                    {
                        double va = a[rowStart + x + k];
                        double vb = b[rowStart + x + k];
                        double w = kernel[k];
                        sa += w * va;
                        sb += w * vb;
                        saa += w * (va * va);
                        sbb += w * (vb * vb);
                        sab += w * (va * vb);
                    }

                    ring[slot + x] = sa;
                    ring[slot + outWidth + x] = sb;
                    ring[slot + (2 * outWidth) + x] = saa;
                    ring[slot + (3 * outWidth) + x] = sbb;
                    ring[slot + (4 * outWidth) + x] = sab;
                }
            }

            for (int y = start; y < start + WindowSize - 1; y++)
            {
                FilterRow(y);
            }

            for (int oy = start; oy < end; oy++)
            {
                FilterRow(oy + WindowSize - 1);
                double sumSsim = 0, sumCs = 0;
                for (int x = 0; x < outWidth; x++)
                {
                    double ma = 0, mb = 0, maa = 0, mbb = 0, mab = 0;
                    for (int k = 0; k < WindowSize; k++)
                    {
                        int slot = ((oy + k) % WindowSize) * 5 * outWidth;
                        double w = kernel[k];
                        ma += w * ring[slot + x];
                        mb += w * ring[slot + outWidth + x];
                        maa += w * ring[slot + (2 * outWidth) + x];
                        mbb += w * ring[slot + (3 * outWidth) + x];
                        mab += w * ring[slot + (4 * outWidth) + x];
                    }

                    double va = maa - (ma * ma);
                    double vb = mbb - (mb * mb);
                    double cov = mab - (ma * mb);
                    double cs = ((2 * cov) + C2) / (va + vb + C2);
                    double luminance = ((2 * ma * mb) + C1) / ((ma * ma) + (mb * mb) + C1);
                    sumCs += cs;
                    sumSsim += luminance * cs;
                }

                rowSsim[oy] = sumSsim;
                rowCs[oy] = sumCs;
            }
        });

        double count = (double)outWidth * outHeight;
        return (QualityPlanes.SumInOrder(rowSsim) / count, QualityPlanes.SumInOrder(rowCs) / count);
    }

    /// <summary>2 x 2 box average followed by keeping every second sample; the last row/column is repeated for odd sizes.</summary>
    internal static double[] Downsample(double[] plane, int width, int height)
    {
        int outWidth = (width + 1) / 2;
        int outHeight = (height + 1) / 2;
        double[] result = new double[outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            int y0 = 2 * y;
            int y1 = Math.Min(y0 + 1, height - 1);
            for (int x = 0; x < outWidth; x++)
            {
                int x0 = 2 * x;
                int x1 = Math.Min(x0 + 1, width - 1);
                result[(y * outWidth) + x] = (plane[(y0 * width) + x0] + plane[(y0 * width) + x1]
                    + plane[(y1 * width) + x0] + plane[(y1 * width) + x1]) / 4;
            }
        }

        return result;
    }

    private static (double[][] A, double[][] B, int Width, int Height, OperationExecution Execution) Prepare<TReference, TDistorted>(
        Image<TReference> reference,
        Image<TDistorted> distorted,
        QualityChannels channels,
        CancellationToken cancellationToken,
        int minimumDimension)
        where TReference : unmanaged, IPixel<TReference>
        where TDistorted : unmanaged, IPixel<TDistorted>
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(distorted);
        reference.EnsureNotDisposed();
        distorted.EnsureNotDisposed();
        if (reference.Width != distorted.Width || reference.Height != distorted.Height)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"The images must have the same size, but are {reference.Width}x{reference.Height} and {distorted.Width}x{distorted.Height}."), nameof(distorted));
        }

        if (reference.Width < minimumDimension || reference.Height < minimumDimension)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"This metric needs images of at least {minimumDimension}x{minimumDimension} pixels, but they are {reference.Width}x{reference.Height}."), nameof(reference));
        }

        OperationExecution execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        try
        {
            double[][] a = QualityPlanes.Extract(reference.Frames.RootFrame, channels);
            double[][] b = QualityPlanes.Extract(distorted.Frames.RootFrame, channels);
            return (a, b, reference.Width, reference.Height, execution);
        }
        catch
        {
            execution.Dispose();
            throw;
        }
    }

    private static double[] BuildGaussianWindow()
    {
        double[] window = new double[WindowSize];
        double sum = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            double d = i - (WindowSize / 2);
            window[i] = Math.Exp(-(d * d) / (2 * WindowSigma * WindowSigma));
            sum += window[i];
        }

        for (int i = 0; i < WindowSize; i++)
        {
            window[i] /= sum;
        }

        return window;
    }
}
