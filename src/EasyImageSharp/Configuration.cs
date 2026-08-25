namespace EasyImageSharp;

/// <summary>
/// Library-wide execution settings. <see cref="Default"/> is consulted by every operation that can run in
/// parallel; set <see cref="MaxDegreeOfParallelism"/> to 1 for fully single-threaded behaviour.
/// </summary>
public sealed class Configuration
{
    private int maxDegreeOfParallelism = Environment.ProcessorCount;
    private int minimumPixelsPerTask = 4096 * 8;

    /// <summary>The configuration used when none is supplied.</summary>
    public static Configuration Default { get; } = new();

    /// <summary>
    /// The maximum number of threads an operation may use for row-parallel work. Defaults to
    /// <see cref="Environment.ProcessorCount"/>. Values below 1 are treated as 1.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get => this.maxDegreeOfParallelism;
        set => this.maxDegreeOfParallelism = Math.Max(1, value);
    }

    /// <summary>
    /// Operations only split work across threads when a frame contains at least this many pixels; smaller
    /// frames run on the calling thread to avoid scheduling overhead. Defaults to 32 768 pixels.
    /// </summary>
    public int MinimumPixelsPerTask
    {
        get => this.minimumPixelsPerTask;
        set => this.minimumPixelsPerTask = Math.Max(1, value);
    }
}
