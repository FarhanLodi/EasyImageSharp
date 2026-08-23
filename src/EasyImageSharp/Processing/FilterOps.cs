using System.Buffers;
using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Thresholding and convolution filters applied in place to single frames.</summary>
internal static class FilterOps
{
    // ----- Global thresholding -----

    public static void OtsuThreshold<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int count = width * height;
        byte[] luminance = RentLuminancePlane(frame, new Rectangle(0, 0, width, height));
        try
        {
            int threshold = ComputeOtsuThreshold(BuildHistogram(luminance, count), count);
            TPixel upper = TPixel.FromRgba32(Rgba32.White);
            TPixel lower = TPixel.FromRgba32(Rgba32.Black);
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> row = frame.GetRowSpan(y);
                    ReadOnlySpan<byte> values = luminance.AsSpan(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = values[x] > threshold ? upper : lower;
                    }
                }
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(luminance);
        }
    }

    /// <summary>Histogram of the first <paramref name="count"/> samples, accumulated in parallel and merged.</summary>
    private static int[] BuildHistogram(byte[] values, int count)
    {
        var histogram = new int[256];
        object gate = new();
        ParallelRowIterator.IterateRows(1, count, (start, end) =>
        {
            Span<int> local = stackalloc int[256];
            local.Clear();
            for (int i = start; i < end; i++)
            {
                local[values[i]]++;
            }

            lock (gate)
            {
                for (int v = 0; v < 256; v++)
                {
                    histogram[v] += local[v];
                }
            }
        });

        return histogram;
    }

    internal static int ComputeOtsuThreshold(int[] histogram, int total)
    {
        long sumAll = 0;
        for (int i = 0; i < 256; i++)
        {
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        long weightBackground = 0;
        double bestVariance = -1;
        int bestThreshold = 127;

        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            long weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (long)t * histogram[t];
            double meanBackground = (double)sumBackground / weightBackground;
            double meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            double difference = meanBackground - meanForeground;
            double variance = (double)weightBackground * weightForeground * difference * difference;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }

    // ----- Local (adaptive) thresholding -----

    /// <summary>Bradley's local-mean threshold over the whole frame, writing white/black.</summary>
    public static void BradleyThreshold<TPixel>(ImageFrame<TPixel> frame, int windowSize, float thresholdLimit)
        where TPixel : unmanaged, IPixel<TPixel>
        => BradleyThreshold(frame, new Rectangle(0, 0, frame.Width, frame.Height), Rgba32.White, Rgba32.Black, windowSize, thresholdLimit);

    /// <summary>
    /// Bradley's local-mean threshold over <paramref name="region"/> (already clamped to the frame). The
    /// integral image and the local windows are confined to the region; pixels whose luminance is at least
    /// <paramref name="thresholdLimit"/> times the local mean become <paramref name="upper"/>, the rest
    /// <paramref name="lower"/>. A non-positive <paramref name="windowSize"/> derives one from the region size.
    /// </summary>
    public static void BradleyThreshold<TPixel>(
        ImageFrame<TPixel> frame, Rectangle region, Rgba32 upper, Rgba32 lower, int windowSize, float thresholdLimit)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = region.Width;
        int height = region.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (windowSize <= 0)
        {
            windowSize = Math.Max(15, Math.Min(width, height) / 16) | 1;
        }

        byte[] luminance = RentLuminancePlane(frame, region);
        long[] integral = RentIntegral(luminance, width, height, squared: false);
        try
        {
            int half = windowSize / 2;
            int stride = width + 1;
            TPixel upperPixel = TPixel.FromRgba32(upper);
            TPixel lowerPixel = TPixel.FromRgba32(lower);

            // The window's row range only depends on y, so it is hoisted out of the column loop; the
            // remaining work per pixel is four integral reads and a multiply, with no division.
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    int y1 = Math.Max(0, y - half);
                    int y2 = Math.Min(height - 1, y + half);
                    int rows = y2 - y1 + 1;
                    int top = y1 * stride;
                    int bottom = (y2 + 1) * stride;
                    Span<TPixel> destination = frame.GetRowSpan(region.Y + y).Slice(region.X, width);
                    ReadOnlySpan<byte> values = luminance.AsSpan(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        int x1 = Math.Max(0, x - half);
                        int x2 = Math.Min(width - 1, x + half);
                        long area = (long)(x2 - x1 + 1) * rows;
                        long sum = integral[bottom + x2 + 1] - integral[top + x2 + 1]
                            - integral[bottom + x1] + integral[top + x1];
                        destination[x] = values[x] * area >= sum * thresholdLimit ? upperPixel : lowerPixel;
                    }
                }
            });
        }
        finally
        {
            ArrayPool<long>.Shared.Return(integral);
            ArrayPool<byte>.Shared.Return(luminance);
        }
    }

    public static void SauvolaThreshold<TPixel>(ImageFrame<TPixel> frame, int windowSize, float k)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        if (windowSize <= 0)
        {
            windowSize = 25;
        }

        byte[] luminance = RentLuminancePlane(frame, new Rectangle(0, 0, width, height));
        long[] integral = RentIntegral(luminance, width, height, squared: false);
        long[] integralSq = RentIntegral(luminance, width, height, squared: true);
        try
        {
            int half = windowSize / 2;
            int stride = width + 1;
            const double R = 128.0;
            TPixel upperPixel = TPixel.FromRgba32(Rgba32.White);
            TPixel lowerPixel = TPixel.FromRgba32(Rgba32.Black);

            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    int y1 = Math.Max(0, y - half);
                    int y2 = Math.Min(height - 1, y + half);
                    int rows = y2 - y1 + 1;
                    int top = y1 * stride;
                    int bottom = (y2 + 1) * stride;
                    Span<TPixel> destination = frame.GetRowSpan(y);
                    ReadOnlySpan<byte> values = luminance.AsSpan(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        int x1 = Math.Max(0, x - half);
                        int x2 = Math.Min(width - 1, x + half);
                        long area = (long)(x2 - x1 + 1) * rows;
                        long sum = integral[bottom + x2 + 1] - integral[top + x2 + 1]
                            - integral[bottom + x1] + integral[top + x1];
                        long sumSquared = integralSq[bottom + x2 + 1] - integralSq[top + x2 + 1]
                            - integralSq[bottom + x1] + integralSq[top + x1];
                        double mean = (double)sum / area;
                        double meanSq = (double)sumSquared / area;
                        double stdDev = Math.Sqrt(Math.Max(0, meanSq - (mean * mean)));
                        double threshold = mean * (1 + (k * ((stdDev / R) - 1)));
                        destination[x] = values[x] > threshold ? upperPixel : lowerPixel;
                    }
                }
            });
        }
        finally
        {
            ArrayPool<long>.Shared.Return(integralSq);
            ArrayPool<long>.Shared.Return(integral);
            ArrayPool<byte>.Shared.Return(luminance);
        }
    }

    /// <summary>
    /// Builds a summed-area table of the plane (or of its squared samples) in a pooled buffer. Only the
    /// zero row and zero column are cleared; every other entry is written by the accumulation loop.
    /// </summary>
    private static long[] RentIntegral(byte[] values, int width, int height, bool squared)
    {
        int stride = width + 1;
        long[] integral = ArrayPool<long>.Shared.Rent(stride * (height + 1));
        integral.AsSpan(0, stride).Clear();
        for (int y = 1; y <= height; y++)
        {
            integral[y * stride] = 0;
        }

        for (int y = 0; y < height; y++)
        {
            long rowSum = 0;
            int sourceRow = y * width;
            int integralRow = (y + 1) * stride;
            int previousRow = y * stride;
            if (squared)
            {
                for (int x = 0; x < width; x++)
                {
                    int v = values[sourceRow + x];
                    rowSum += (long)v * v;
                    integral[integralRow + x + 1] = rowSum + integral[previousRow + x + 1];
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    rowSum += values[sourceRow + x];
                    integral[integralRow + x + 1] = rowSum + integral[previousRow + x + 1];
                }
            }
        }

        return integral;
    }

    // ----- Convolutions -----

    public static void GaussianBlur<TPixel>(ImageFrame<TPixel> frame, float sigma)
        where TPixel : unmanaged, IPixel<TPixel>
        => GaussianBlur(frame, sigma, new Rectangle(0, 0, frame.Width, frame.Height));

    /// <summary>Gaussian blur of a region (already clamped to the frame); the region is treated as its own image for edge handling.</summary>
    public static void GaussianBlur<TPixel>(ImageFrame<TPixel> frame, float sigma, Rectangle region)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (sigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be positive.");
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        float[] kernel = ConvolutionOps.BuildGaussianKernel(sigma);
        ConvolutionOps.ConvolveSeparable(frame, region, kernel, kernel, preserveAlpha: false);
    }

    public static void GaussianSharpen<TPixel>(ImageFrame<TPixel> frame, float sigma)
        where TPixel : unmanaged, IPixel<TPixel>
        => GaussianSharpen(frame, sigma, new Rectangle(0, 0, frame.Width, frame.Height));

    /// <summary>Unsharp mask of a region (already clamped to the frame): <c>2 * original - blurred</c>, preserving alpha.</summary>
    public static void GaussianSharpen<TPixel>(ImageFrame<TPixel> frame, float sigma, Rectangle region)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (sigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be positive.");
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        float[] kernel = ConvolutionOps.BuildGaussianKernel(sigma);
        Vector4[] original = ConvolutionOps.ReadRegion(frame, region);
        Vector4[] blurred = ConvolutionOps.ConvolveSeparable(original, region.Width, region.Height, kernel, kernel);

        int width = region.Width;
        ParallelRowIterator.IterateRows(width, region.Height, (start, end) =>
        {
            for (int i = start * width; i < end * width; i++)
            {
                Vector4 sharpened = (original[i] * 2f) - blurred[i];
                sharpened.W = original[i].W;
                blurred[i] = sharpened;
            }
        });

        ConvolutionOps.WriteRegion(frame, region, blurred, null);
    }

    public static void MedianBlur<TPixel>(ImageFrame<TPixel> frame, int radius)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (radius is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Median radius must be between 1 and 7.");
        }

        int width = frame.Width;
        int height = frame.Height;
        var source = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), source.AsSpan(y * width, width));
        }

        int windowLength = ((2 * radius) + 1) * ((2 * radius) + 1);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var reds = new byte[windowLength];
            var greens = new byte[windowLength];
            var blues = new byte[windowLength];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> destRow = frame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int count = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int sy = Math.Clamp(y + dy, 0, height - 1);
                        int rowOffset = sy * width;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int sx = Math.Clamp(x + dx, 0, width - 1);
                            Rgba32 p = source[rowOffset + sx];
                            reds[count] = p.R;
                            greens[count] = p.G;
                            blues[count] = p.B;
                            count++;
                        }
                    }

                    Array.Sort(reds, 0, count);
                    Array.Sort(greens, 0, count);
                    Array.Sort(blues, 0, count);
                    byte alpha = source[(y * width) + x].A;
                    destRow[x] = TPixel.FromRgba32(new Rgba32(reds[count / 2], greens[count / 2], blues[count / 2], alpha));
                }
            }
        });
    }

    // ----- Shared helpers -----

    /// <summary>
    /// BT.709 luminance of every pixel in <paramref name="region"/>, row-major and region-sized, into a
    /// pooled buffer the caller must return. The rented array is longer than the plane; only the first
    /// <c>region.Width * region.Height</c> bytes are meaningful.
    /// </summary>
    internal static byte[] RentLuminancePlane<TPixel>(ImageFrame<TPixel> frame, Rectangle region)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = region.Width;
        byte[] luminance = ArrayPool<byte>.Shared.Rent(width * region.Height);
        ParallelRowIterator.IterateRows(width, region.Height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                PixelOps.ToLuminance<TPixel>(
                    frame.GetRowSpan(region.Y + y).Slice(region.X, width),
                    luminance.AsSpan(y * width, width));
            }
        });

        return luminance;
    }
}
