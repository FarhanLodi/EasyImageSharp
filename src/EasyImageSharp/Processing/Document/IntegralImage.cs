namespace EasyImageSharp.Processing;

/// <summary>
/// Summed-area tables of a byte plane and (optionally) of its squares, giving O(1) window sums, means and
/// variances for the local thresholds, box blurs and background statistics.
/// </summary>
internal sealed class IntegralImage
{
    private readonly long[] sum;
    private readonly long[]? sumSq;
    private readonly int stride;

    public IntegralImage(byte[] plane, int width, int height, bool withSquares)
    {
        this.Width = width;
        this.Height = height;
        this.stride = width + 1;
        this.sum = new long[(width + 1) * (height + 1)];
        this.sumSq = withSquares ? new long[(width + 1) * (height + 1)] : null;

        // Row prefix sums in parallel, then a sequential vertical accumulation (memory bound, cheap).
        long[] s = this.sum;
        long[]? sq = this.sumSq;
        int localStride = this.stride;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                long rowSum = 0;
                long rowSq = 0;
                int source = y * width;
                int target = ((y + 1) * localStride) + 1;
                for (int x = 0; x < width; x++)
                {
                    int v = plane[source + x];
                    rowSum += v;
                    s[target + x] = rowSum;
                    if (sq is not null)
                    {
                        rowSq += (long)v * v;
                        sq[target + x] = rowSq;
                    }
                }
            }
        });

        for (int y = 2; y <= height; y++)
        {
            int row = y * localStride;
            int prev = (y - 1) * localStride;
            for (int x = 1; x <= width; x++)
            {
                s[row + x] += s[prev + x];
            }

            if (sq is not null)
            {
                for (int x = 1; x <= width; x++)
                {
                    sq[row + x] += sq[prev + x];
                }
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Sum over the inclusive pixel rectangle [x1..x2] x [y1..y2] (must be inside the image).</summary>
    public long Sum(int x1, int y1, int x2, int y2)
        => this.sum[((y2 + 1) * this.stride) + x2 + 1]
         - this.sum[(y1 * this.stride) + x2 + 1]
         - this.sum[((y2 + 1) * this.stride) + x1]
         + this.sum[(y1 * this.stride) + x1];

    /// <summary>Sum of squares over the inclusive pixel rectangle (requires <c>withSquares</c>).</summary>
    public long SumOfSquares(int x1, int y1, int x2, int y2)
    {
        long[] sq = this.sumSq ?? throw new InvalidOperationException("Squares were not accumulated.");
        return sq[((y2 + 1) * this.stride) + x2 + 1]
             - sq[(y1 * this.stride) + x2 + 1]
             - sq[((y2 + 1) * this.stride) + x1]
             + sq[(y1 * this.stride) + x1];
    }

    /// <summary>Mean and variance of the window of half-size <paramref name="half"/> centred at (x, y), clamped to the image.</summary>
    public void WindowStats(int x, int y, int half, out double mean, out double variance, out int area)
    {
        int x1 = Math.Max(0, x - half);
        int y1 = Math.Max(0, y - half);
        int x2 = Math.Min(this.Width - 1, x + half);
        int y2 = Math.Min(this.Height - 1, y + half);
        area = (x2 - x1 + 1) * (y2 - y1 + 1);
        mean = (double)this.Sum(x1, y1, x2, y2) / area;
        double meanSq = (double)this.SumOfSquares(x1, y1, x2, y2) / area;
        variance = Math.Max(0, meanSq - (mean * mean));
    }

    /// <summary>Mean of the clamped window centred at (x, y).</summary>
    public double WindowMean(int x, int y, int half)
    {
        int x1 = Math.Max(0, x - half);
        int y1 = Math.Max(0, y - half);
        int x2 = Math.Min(this.Width - 1, x + half);
        int y2 = Math.Min(this.Height - 1, y + half);
        return (double)this.Sum(x1, y1, x2, y2) / ((x2 - x1 + 1) * (y2 - y1 + 1));
    }

    /// <summary>Box blur of radius <paramref name="radius"/> (windows clamped to the image) computed through the integral.</summary>
    public static byte[] BoxBlur(byte[] plane, int width, int height, int radius)
    {
        var integral = new IntegralImage(plane, width, height, withSquares: false);
        var result = new byte[plane.Length];
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int y1 = Math.Max(0, y - radius);
                int y2 = Math.Min(height - 1, y + radius);
                int rows = y2 - y1 + 1;
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int x1 = Math.Max(0, x - radius);
                    int x2 = Math.Min(width - 1, x + radius);
                    long area = (long)(x2 - x1 + 1) * rows;
                    result[offset + x] = (byte)((integral.Sum(x1, y1, x2, y2) + (area / 2)) / area);
                }
            }
        });
        return result;
    }
}
