using System.Buffers;

namespace EasyImageSharp.Memory;

/// <summary>
/// Supplies the memory behind image pixel buffers. Every buffer the library allocates for a frame - a decoded
/// image, a new or cloned image, the destination of a resize, rotation or other size-changing operation - comes
/// from the allocator of the <see cref="Configuration"/> in effect when the buffer is created: the request-local
/// one from <see cref="ImageExecutionOptions.Configuration"/> if there is one, otherwise
/// <see cref="Configuration.Default"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three allocators are built in. <see cref="PooledMemoryAllocator"/>, the default, reuses released buffers;
/// <see cref="GCMemoryAllocator"/> allocates a new managed array every time; <see cref="UnmanagedMemoryAllocator"/>
/// allocates outside the managed heap.
/// </para>
/// <para>
/// The library disposes each owner it receives exactly once: when the last image that uses the buffer is
/// disposed, or when a processing operation replaces the buffer with a new one. A buffer is never handed back
/// while another image (a copy-on-write clone in progress, for example) still uses it. Memory wrapped with
/// <see cref="Image{TPixel}.WrapMemory(Memory{TPixel}, int, int)"/> never comes from an allocator and is never
/// handed to one.
/// </para>
/// <para>
/// Buffers of images that are never disposed are never handed back, and neither is a buffer whose array the
/// library had to expose beyond the lifetime of its image. An implementation has to cope with that: a pooling
/// allocator simply loses the buffer to the garbage collector, an allocator of native memory must also free it
/// from a finalizer.
/// </para>
/// <para>
/// Implementations must be thread-safe: images are decoded and processed on many threads at once, and a buffer
/// may be handed back on a different thread from the one that allocated it.
/// </para>
/// </remarks>
public abstract class MemoryAllocator
{
    /// <summary>Initializes a new instance of the <see cref="MemoryAllocator"/> class.</summary>
    protected MemoryAllocator()
    {
    }

    /// <summary>
    /// The process-wide <see cref="PooledMemoryAllocator"/> with default options. Every new
    /// <see cref="Configuration"/>, including <see cref="Configuration.Default"/>, starts out with it.
    /// </summary>
    public static MemoryAllocator Shared { get; } = new PooledMemoryAllocator();

    /// <summary>Allocates a buffer of <paramref name="length"/> elements of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="length">The number of elements; zero is allowed.</param>
    /// <param name="options">
    /// <see cref="AllocationOptions.Clean"/>, the default, to receive zeroed memory. With
    /// <see cref="AllocationOptions.None"/> the contents are unspecified and, from a pooling allocator, may be data
    /// another user of the memory left behind; only ask for that when every element is written before any is read.
    /// </param>
    /// <returns>
    /// The owner of the buffer. Its <see cref="IMemoryOwner{T}.Memory"/> holds at least <paramref name="length"/>
    /// elements; the library only ever uses the first <paramref name="length"/>. Disposing the owner releases the
    /// buffer, after which the memory must not be touched.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="OutOfMemoryException">The memory could not be allocated.</exception>
    public abstract IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
        where T : unmanaged;

    /// <summary>
    /// Releases any memory the allocator keeps for reuse. Buffers that are still in use are not affected.
    /// The base implementation does nothing.
    /// </summary>
    public virtual void ReleaseRetainedResources()
    {
    }
}
