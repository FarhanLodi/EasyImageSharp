using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Memory;

/// <summary>
/// Allocates every buffer from the native heap, outside the garbage-collected heap, aligned to
/// <see cref="Alignment"/> bytes, and frees it as soon as the last image using it is disposed. Nothing is kept
/// for reuse.
/// </summary>
/// <remarks>
/// <para>
/// Native buffers never add to the managed heap, so the garbage collector neither scans nor compacts them and a
/// decode-process-dispose loop causes no collections for its pixel memory at all. It suits hosts that dispose
/// every image deterministically. A buffer whose image is never disposed is freed by a finalizer once the buffer
/// is unreachable - but only when a collection happens for other reasons: the allocator deliberately does not
/// report its memory through <see cref="GC.AddMemoryPressure(long)"/>, because for buffers that are disposed
/// promptly that report only provokes full collections (measured at about one per 24 MB frame).
/// </para>
/// <para>
/// Because native memory is invisible to the garbage collector, a span obtained from an image does not keep the
/// image's buffer alive: keep the image itself reachable, and undisposed, for as long as any span or
/// <see cref="Memory{T}"/> taken from it is used. Memory from this allocator is already fixed in place and does
/// not support <see cref="Memory{T}.Pin"/>, which throws <see cref="NotSupportedException"/>; take a pointer
/// to the span instead.
/// </para>
/// <para>The allocator is thread-safe.</para>
/// </remarks>
public sealed class UnmanagedMemoryAllocator : MemoryAllocator
{
    /// <summary>Creates an allocator whose buffers start on a 64-byte boundary, the size of a cache line.</summary>
    public UnmanagedMemoryAllocator()
        : this(64)
    {
    }

    /// <summary>Creates an allocator whose buffers start on an <paramref name="alignment"/>-byte boundary.</summary>
    /// <param name="alignment">A power of two from 1 to 4096.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alignment"/> is not a power of two in that range.</exception>
    public UnmanagedMemoryAllocator(int alignment)
    {
        if (alignment is < 1 or > 4096 || !BitOperations.IsPow2(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment must be a power of two from 1 to 4096.");
        }

        this.Alignment = alignment;
    }

    /// <summary>The byte boundary every buffer starts on.</summary>
    public int Alignment { get; }

    /// <inheritdoc/>
    public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.Clean)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new NativeBuffer<T>(length, this.Alignment, (options & AllocationOptions.Clean) != 0);
    }

    /// <summary>One native allocation, freed by <see cref="IDisposable.Dispose"/> or, failing that, by the finalizer.</summary>
    private sealed class NativeBuffer<T> : MemoryManager<T>
        where T : unmanaged
    {
        private readonly nint allocation;
        private readonly nint address;
        private readonly int length;
        private int released;

        public NativeBuffer(int length, int alignment, bool clean)
        {
            this.length = length;
            long bytes = (long)length * Unsafe.SizeOf<T>();
            long total = bytes + alignment - 1;
            if (total > nint.MaxValue)
            {
                // Only reachable in a 32-bit process, where no single allocation can be this large anyway.
                throw new OutOfMemoryException($"A native buffer of {bytes} bytes does not fit in the address space.");
            }

            // Over-allocate by alignment - 1 bytes and round the start up. Marshal.AllocHGlobal rather than
            // NativeMemory.AlignedAlloc keeps the library free of pointer types, which need unsafe blocks.
            this.allocation = Marshal.AllocHGlobal((nint)total);
            this.address = (this.allocation + (alignment - 1)) & ~(nint)(alignment - 1);

            if (clean)
            {
                this.GetSpan().Clear();
            }
        }

        // CA2015 warns that a span can outlive the manager it came from and read freed memory once the finalizer
        // has run. The finalizer is deliberate: without it every image a host forgets to dispose leaks native memory
        // for the life of the process. It only runs once no frame references this buffer any more; the library
        // keeps the frame (and so this object) reachable for as long as it works on the pixels, and callers are
        // told to do the same with spans they take from an image (see the remarks on UnmanagedMemoryAllocator).
#pragma warning disable CA2015
        ~NativeBuffer()
        {
            this.Dispose(false);
        }
#pragma warning restore CA2015

        public override Span<T> GetSpan()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.released) != 0, this);

            // A managed reference to native memory: the GC ignores references outside its heap, so this is the
            // pointer-free spelling of new Span<T>((void*)address, length).
            return MemoryMarshal.CreateSpan(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<T>(), this.address), this.length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
            => throw new NotSupportedException(
                "Memory from UnmanagedMemoryAllocator is native and never moves; it cannot be pinned through Memory<T>.Pin. Take a pointer to its span instead.");

        public override void Unpin()
        {
            // Nothing is ever pinned.
        }

        protected override void Dispose(bool disposing)
        {
            // The exchange makes a second Dispose a no-op instead of a double free. (IDisposable.Dispose on the base
            // class already suppresses the finalizer.)
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                Marshal.FreeHGlobal(this.allocation);
            }
        }
    }
}
