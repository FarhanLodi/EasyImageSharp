using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Memory;

/// <summary>
/// One frame buffer and the bookkeeping that decides when it may go back to its allocator. Every frame that uses
/// the buffer holds one reference; a copy-on-write clone adds a reference instead of copying. The memory is handed
/// back exactly once, when the last reference is released, and never if it was abandoned to the garbage collector.
/// </summary>
/// <remarks>
/// <para>
/// There are three kinds of buffer. An <em>allocated</em> one came from a <see cref="MemoryAllocator"/> and is
/// returned to it. An <em>adopted</em> one is a plain array the library built itself; the garbage collector owns
/// it and releasing it does nothing. A <em>wrapped</em> one is caller memory from
/// <see cref="Image{TPixel}.WrapMemory(Memory{TPixel}, int, int)"/> (or library scratch memory used the same way),
/// which is never freed by the library.
/// </para>
/// <para>
/// The reference count only ever goes up while another reference is held (a clone shares a buffer of a frame
/// that is alive), so it can never come back from zero: a released buffer cannot be resurrected and returned twice.
/// </para>
/// </remarks>
internal sealed class PixelBuffer<T>
    where T : unmanaged
{
    private IMemoryOwner<T>? owner;
    private int references = 1;
    private volatile bool abandoned;

    private PixelBuffer(IMemoryOwner<T>? owner, Memory<T> memory, T[]? array, bool callerOwned)
    {
        this.owner = owner;
        this.Memory = memory;
        this.Array = array;
        this.IsCallerOwned = callerOwned;
    }

    /// <summary>The pixels: exactly the length the buffer was created for.</summary>
    public Memory<T> Memory { get; }

    /// <summary>
    /// The managed array behind <see cref="Memory"/> when the memory starts at its index 0, otherwise
    /// <see langword="null"/>. An allocated array may be longer than <see cref="Memory"/>, because pooled arrays are
    /// rounded up to a size class; the elements past <see cref="Memory"/> belong to nobody and are never read.
    /// </summary>
    public T[]? Array { get; }

    /// <summary>Whether the memory belongs to the caller (it was wrapped, not allocated or adopted).</summary>
    public bool IsCallerOwned { get; }

    /// <summary>Whether more than one frame currently uses this buffer.</summary>
    public bool IsShared => Volatile.Read(ref this.references) > 1;

    /// <summary>
    /// Allocates <paramref name="length"/> elements from the allocator of the configuration in effect. The caller
    /// has already charged the request to the operation's pixel budget.
    /// </summary>
    /// <param name="length">The number of elements.</param>
    /// <param name="options">
    /// <see cref="AllocationOptions.Clean"/> unless every element is provably written before anything can read it.
    /// </param>
    public static PixelBuffer<T> Allocate(int length, AllocationOptions options)
    {
        MemoryAllocator allocator = OperationExecution.CurrentMemoryAllocator;
        IMemoryOwner<T> owner = allocator.Allocate<T>(length, options);
        Memory<T> memory;
        try
        {
            memory = owner.Memory;
            if (memory.Length < length)
            {
                throw new InvalidOperationException(
                    $"{allocator.GetType().Name} returned {memory.Length} elements for a request of {length}.");
            }
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        memory = memory[..length];
        T[]? array = MemoryMarshal.TryGetArray<T>(memory, out ArraySegment<T> segment) && segment.Offset == 0 ? segment.Array : null;
        return new PixelBuffer<T>(owner, memory, array, callerOwned: false);
    }

    /// <summary>
    /// Takes a library-built array whose lifetime the garbage collector manages; nothing is ever returned. The
    /// buffer covers the first <paramref name="length"/> elements.
    /// </summary>
    public static PixelBuffer<T> Adopt(T[] array, int length)
    {
        Debug.Assert(array.Length >= length, "An adopted array must hold the whole frame.");
        Memory<T> memory = array.Length == length ? array : array.AsMemory(0, Math.Min(length, array.Length));
        return new PixelBuffer<T>(null, memory, array, callerOwned: false);
    }

    /// <summary>Wraps memory the library neither allocated nor may free.</summary>
    public static PixelBuffer<T> Wrap(Memory<T> memory)
    {
        T[]? array = MemoryMarshal.TryGetArray<T>(memory, out ArraySegment<T> segment) && segment.Offset == 0 ? segment.Array : null;
        return new PixelBuffer<T>(null, memory, array, callerOwned: true);
    }

    /// <summary>Adds a reference for another frame that is going to share this buffer, and returns the buffer.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has already been released.</exception>
    public PixelBuffer<T> Share()
    {
        while (true)
        {
            int current = Volatile.Read(ref this.references);
            ObjectDisposedException.ThrowIf(current <= 0, this);
            if (Interlocked.CompareExchange(ref this.references, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    /// <summary>Drops one reference. The last one hands allocated memory back to its allocator, unless it was abandoned.</summary>
    public void Release()
    {
        int remaining = Interlocked.Decrement(ref this.references);
        Debug.Assert(remaining >= 0, "A pixel buffer was released more often than it was referenced.");
        if (remaining != 0)
        {
            return;
        }

        // The exchange makes the hand-back happen at most once even if a bug released the buffer twice.
        IMemoryOwner<T>? released = Interlocked.Exchange(ref this.owner, null);
        if (released is not null && !this.abandoned)
        {
            released.Dispose();
        }
    }

    /// <summary>
    /// Hands the memory to the garbage collector for good: it is never returned to its allocator, because a raw
    /// array reference to it now lives outside of any frame's lifetime.
    /// </summary>
    public void Abandon() => this.abandoned = true;
}
