using EasyImageSharp.Memory;

namespace EasyImageSharp;

/// <summary>
/// Library-wide execution settings. <see cref="Default"/> is consulted by every operation that can run in
/// parallel or allocates pixel memory; set <see cref="MaxDegreeOfParallelism"/> to 1 for fully single-threaded
/// behaviour.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> is a process-wide singleton. Set it during application startup.
/// Use <see cref="ImageExecutionOptions.Configuration"/> for an isolated request-local snapshot;
/// these overrides never mutate the process defaults.
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
    private MemoryAllocator memoryAllocator = MemoryAllocator.Shared;

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

    /// <summary>
    /// Where pixel buffers come from. Defaults to <see cref="MemoryAllocator.Shared"/>, a process-wide
    /// <see cref="PooledMemoryAllocator"/> that reuses the buffers of disposed images.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every frame buffer the library allocates - decoded images, new images, clones and the destinations of
    /// size-changing operations - is taken from the allocator of the configuration in effect when it is allocated,
    /// and handed back to that same allocator when the image is disposed or the buffer replaced. Disposing images
    /// is what makes pooling pay off: a buffer of an image that is never disposed is left to the garbage collector.
    /// </para>
    /// <para>
    /// Set <see cref="GCMemoryAllocator"/> to allocate a fresh managed array per frame, as releases before pooling
    /// did, or <see cref="UnmanagedMemoryAllocator"/> to keep pixel memory off the managed heap. A request-local
    /// <see cref="ImageExecutionOptions.Configuration"/> can carry its own allocator, for example a bounded pool per
    /// tenant.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public MemoryAllocator MemoryAllocator
    {
        get => this.memoryAllocator;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            this.memoryAllocator = value;
        }
    }
}
