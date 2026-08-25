namespace EasyImageSharp.Processing;

/// <summary>
/// The resampling weights of one axis, flattened into contiguous arrays: every destination index owns a
/// window <c>[Start, Start + Length)</c> of source samples and a run of <c>Length</c> weights beginning at
/// <c>Offset</c> inside <see cref="Weights"/>.
/// </summary>
/// <remarks>
/// Windows are built exactly as the original per-destination-pixel computation did - the kernel is evaluated
/// over the same integer range, clamped samples fold into the edge weight, and the run is normalised by its
/// own double-precision sum - so the weights are bit-for-bit the same. Flattening only removes the array of
/// arrays and the bounds checks from the inner loop.
/// </remarks>
internal sealed class ResizeKernelMap
{
    private ResizeKernelMap(int[] start, int[] length, int[] offset, float[] weights, int maxLength)
    {
        this.Start = start;
        this.Length = length;
        this.Offset = offset;
        this.Weights = weights;
        this.MaxLength = maxLength;
    }

    /// <summary>First source sample of each destination index's window.</summary>
    public int[] Start { get; }

    /// <summary>Number of taps of each destination index's window.</summary>
    public int[] Length { get; }

    /// <summary>Index into <see cref="Weights"/> of each destination index's first tap.</summary>
    public int[] Offset { get; }

    /// <summary>All weight runs, concatenated in destination order.</summary>
    public float[] Weights { get; }

    /// <summary>The longest window in the map; bounds the working set a chunked pass has to hold.</summary>
    public int MaxLength { get; }

    /// <summary>Builds the map that resamples <paramref name="sourceSize"/> samples to <paramref name="destSize"/>.</summary>
    public static ResizeKernelMap Build(int sourceSize, int destSize, IResampler sampler)
    {
        double scale = (double)destSize / sourceSize;
        double filterScale = Math.Max(1.0, 1.0 / scale);
        double radius = sampler.Radius * filterScale;

        var start = new int[destSize];
        var length = new int[destSize];
        var offset = new int[destSize];
        int total = 0;
        int maxLength = 0;

        // First pass: window geometry only, so the weight array can be allocated once at the right size.
        for (int j = 0; j < destSize; j++)
        {
            (int windowStart, int windowLength) = Window(j, sourceSize, scale, radius);
            start[j] = windowStart;
            length[j] = windowLength;
            offset[j] = total;
            total += windowLength;
            maxLength = Math.Max(maxLength, windowLength);
        }

        var weights = new float[total];
        for (int j = 0; j < destSize; j++)
        {
            double center = ((j + 0.5) / scale) - 0.5;
            int left = (int)Math.Ceiling(center - radius);
            int right = (int)Math.Floor(center + radius);
            if (right < left)
            {
                right = left;
            }

            int windowStart = start[j];
            int windowEnd = windowStart + length[j] - 1;
            Span<float> run = weights.AsSpan(offset[j], length[j]);
            double sum = 0;
            for (int i = left; i <= right; i++)
            {
                double weight = sampler.GetValue((float)((i - center) / filterScale));
                sum += weight;
                run[Math.Clamp(i, 0, sourceSize - 1) - windowStart] += (float)weight;
            }

            if (Math.Abs(sum) < 1e-8)
            {
                run.Clear();
                run[Math.Clamp((int)Math.Round(center), windowStart, windowEnd) - windowStart] = 1f;
            }
            else
            {
                for (int i = 0; i < run.Length; i++)
                {
                    run[i] = (float)(run[i] / sum);
                }
            }
        }

        return new ResizeKernelMap(start, length, offset, weights, maxLength);
    }

    /// <summary>Source samples the given destination index reads from, clamped to the source extent.</summary>
    private static (int Start, int Length) Window(int j, int sourceSize, double scale, double radius)
    {
        double center = ((j + 0.5) / scale) - 0.5;
        int left = (int)Math.Ceiling(center - radius);
        int right = (int)Math.Floor(center + radius);
        if (right < left)
        {
            right = left;
        }

        int start = Math.Clamp(left, 0, sourceSize - 1);
        int end = Math.Clamp(right, 0, sourceSize - 1);
        return (start, end - start + 1);
    }
}
