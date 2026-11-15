namespace EasyImageSharp.Memory;

/// <summary>Describes what a caller of <see cref="MemoryAllocator.Allocate{T}(int, AllocationOptions)"/> needs from the buffer.</summary>
[Flags]
public enum AllocationOptions
{
    /// <summary>
    /// The contents are unspecified. A pooling allocator hands back whatever an earlier user of the memory left
    /// there, so the caller must write every element before anything reads one.
    /// </summary>
    None = 0,

    /// <summary>Every element of the buffer is zero.</summary>
    Clean = 1,
}
