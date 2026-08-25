namespace EasyImageSharp;

/// <summary>Row-batched parallel loops honouring <see cref="Configuration"/>.</summary>
internal static class ParallelRowIterator
{
    /// <summary>Invokes <paramref name="body"/>(startRow, endRowExclusive) over [0, height) in contiguous batches.</summary>
    public static void IterateRows(int width, int height, Action<int, int> body, Configuration? configuration = null)
    {
        configuration ??= Configuration.Default;
        int maxDop = configuration.MaxDegreeOfParallelism;
        long pixels = (long)width * height;
        if (maxDop <= 1 || height < 2 || pixels < configuration.MinimumPixelsPerTask * 2L)
        {
            body(0, height);
            return;
        }

        int batches = (int)Math.Min(maxDop, Math.Max(1, pixels / configuration.MinimumPixelsPerTask));
        batches = Math.Min(batches, height);
        int rowsPerBatch = (height + batches - 1) / batches;
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = maxDop }, b =>
        {
            int start = b * rowsPerBatch;
            int end = Math.Min(height, start + rowsPerBatch);
            if (start < end)
            {
                body(start, end);
            }
        });
    }

    /// <summary>
    /// Like <see cref="IterateRows(int,int,Action{int,int},Configuration?)"/> but gives each batch its own scratch
    /// state created by <paramref name="createState"/>.
    /// </summary>
    public static void IterateRows<TState>(
        int width, int height, Func<TState> createState, Action<int, int, TState> body, Configuration? configuration = null)
        => IterateRows(width, height, (start, end) => body(start, end, createState()), configuration);
}
