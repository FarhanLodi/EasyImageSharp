namespace EasyImageSharp.Memory;

/// <summary>Bounds and policies for a <see cref="PooledMemoryAllocator"/>.</summary>
public sealed class PooledMemoryAllocatorOptions
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// The most memory, in bytes, the pool keeps for reuse across all buffer sizes and element types. A buffer
    /// returned while the pool is full is left to the garbage collector. Buffers in use do not count.
    /// </summary>
    /// <remarks>
    /// Defaults to one sixteenth of the memory available to the garbage collector (which honours container
    /// limits), clamped to between 64 MiB and 1 GiB. Zero disables retention entirely.
    /// </remarks>
    public long MaximumRetainedBytes { get; init; } = DefaultMaximumRetainedBytes();

    /// <summary>
    /// Buffers larger than this many bytes are allocated directly and never retained. Defaults to 128 MiB,
    /// enough for a 32-megapixel <see cref="PixelFormats.Rgba32"/> frame.
    /// </summary>
    public long MaximumPooledBufferBytes { get; init; } = 128 * MiB;

    /// <summary>
    /// Buffers smaller than this many bytes are allocated directly and never retained: the garbage collector
    /// already handles small, short-lived arrays cheaply. Defaults to 32 KiB.
    /// </summary>
    public long MinimumPooledBufferBytes { get; init; } = 32 * 1024;

    /// <summary>
    /// Whether the pool sheds retained memory after full (generation 2) garbage collections. When on, which is
    /// the default, each such collection releases the retained buffers nobody has rented for a minute - for ten
    /// seconds once the machine's memory load passes 70% of the garbage collector's high-memory threshold. Buffers
    /// a steady load keeps renting stay pooled. Independently of this setting, the pool releases everything it
    /// retains when an allocation fails for lack of memory, and <see cref="MemoryAllocator.ReleaseRetainedResources"/>
    /// does so on request.
    /// </summary>
    public bool TrimOnMemoryPressure { get; init; } = true;

    private static long DefaultMaximumRetainedBytes()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? Math.Clamp(available / 16, 64 * MiB, 1024 * MiB) : 256 * MiB;
    }
}
