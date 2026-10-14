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
        Guard.MustBePositive(sourceSize, nameof(sourceSize));
        Guard.MustBePositive(destSize, nameof(destSize));
        ArgumentNullException.ThrowIfNull(sampler);
        if (!(sampler.Radius > 0f) || !float.IsFinite(sampler.Radius) || sampler.Radius > 1048576f)
        {
            throw new ArgumentException("Resampler radius must be finite, positive, and no greater than 1048576.", nameof(sampler));
        }

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
            OperationExecution.Checkpoint();
            (int windowStart, int windowLength) = Window(j, sourceSize, scale, radius);
            start[j] = windowStart;
            length[j] = windowLength;
            offset[j] = total;
            if (windowLength > Array.MaxLength - total)
            {
                throw new InvalidImageContentException("Resize kernel weights exceed the supported array size.");
            }

            total += windowLength;
            maxLength = Math.Max(maxLength, windowLength);
        }

        var weights = new float[total];
        for (int j = 0; j < destSize; j++)
        {
            OperationExecution.Checkpoint();
            double center = ((j + 0.5) / scale) - 0.5;
            (int left, int right) = KernelBounds(center, radius);

            int windowStart = start[j];
            int windowEnd = windowStart + length[j] - 1;
            Span<float> run = weights.AsSpan(offset[j], length[j]);
            double sum = 0;
            for (int i = left; i <= right; i++)
            {
                if ((i & 4095) == 0) OperationExecution.Checkpoint();
                double weight = sampler.GetValue((float)((i - center) / filterScale));
                if (!double.IsFinite(weight))
                {
                    throw new ArgumentException("Resampler weights must be finite.", nameof(sampler));
                }

                sum += weight;
                int index = Math.Clamp(i, 0, sourceSize - 1) - windowStart;
                run[index] += (float)weight;
                if (!float.IsFinite(run[index]))
                {
                    throw new ArgumentException("Accumulated resampler weights exceed the finite float range.", nameof(sampler));
                }
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
        (int left, int right) = KernelBounds(center, radius);

        int start = Math.Clamp(left, 0, sourceSize - 1);
        int end = Math.Clamp(right, 0, sourceSize - 1);
        return (start, end - start + 1);
    }

    private static (int Left, int Right) KernelBounds(double center, double radius)
    {
        double left = Math.Ceiling(center - radius);
        double right = Math.Max(left, Math.Floor(center + radius));
        // Leave room for the final loop increment; int.MaxValue would wrap and never terminate.
        if (left < int.MinValue || right >= int.MaxValue)
        {
            throw new InvalidImageContentException("Resize kernel sample coordinates exceed the supported range.");
        }

        return ((int)left, (int)right);
    }
}
