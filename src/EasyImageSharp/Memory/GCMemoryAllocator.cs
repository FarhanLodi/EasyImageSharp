using System.Buffers;

namespace EasyImageSharp.Memory;

/// <summary>
/// Allocates every buffer as a new managed array and keeps nothing for reuse; the garbage collector reclaims the
/// memory once the images using it are gone. This is how the library allocated pixel memory before pooling.
/// </summary>
/// <remarks>
/// Choose it when the process should hold no memory beyond what live images use, at the price of a fresh
/// large-object-heap allocation for every frame above 85 000 bytes. The allocator is thread-safe.
/// </remarks>
public sealed class GCMemoryAllocator : MemoryAllocator
{
    /// <inheritdoc/>
    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        T[] array = (options & AllocationOptions.Clean) != 0 ? new T[length] : GC.AllocateUninitializedArray<T>(length);
        return new ArrayMemoryOwner<T>(array, length);
    }
}
