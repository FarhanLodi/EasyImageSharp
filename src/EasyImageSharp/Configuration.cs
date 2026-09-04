namespace EasyImageSharp;

/// <summary>
/// Library-wide execution settings. <see cref="Default"/> is consulted by every operation that can run in
/// parallel; set <see cref="MaxDegreeOfParallelism"/> to 1 for fully single-threaded behaviour.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> is a process-wide singleton and no public API accepts a <see cref="Configuration"/>
/// of its own, so mutating it changes the behaviour of every operation in the process. Both properties are
/// plain <see cref="int"/> fields, so reads and writes are atomic and no amount of concurrent mutation can
/// corrupt the instance, but an operation already in flight may observe either the old or the new value and
/// may observe different values at different points. Set these once during application start-up rather than
/// per request, and do not flip them from one thread while another is processing an image.
/// </para>
/// <para>
/// <see cref="MaxDegreeOfParallelism"/> set to 1 is the only way to force fully serial execution:
/// <see cref="MinimumPixelsPerTask"/> clamps to <c>Math.Max(1, value)</c> on set, so it can never be raised
/// high enough to disable threading on its own for an arbitrarily large image.
/// </para>
/// <para>
/// The parallel threshold is higher than <see cref="MinimumPixelsPerTask"/> alone suggests: a row-parallel
/// operation runs on the calling thread unless the frame has at least two rows <em>and</em> at least
/// <c>MinimumPixelsPerTask * 2</c> pixels — 65 536 pixels at the default, roughly a 256x256 frame. Beyond
/// that threshold the work is split into <c>min(MaxDegreeOfParallelism, pixels / MinimumPixelsPerTask)</c>
/// contiguous row batches, capped at one batch per row.
/// </para>
/// </remarks>
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
