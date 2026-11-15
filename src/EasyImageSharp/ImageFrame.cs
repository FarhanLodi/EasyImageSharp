using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using EasyImageSharp.Memory;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>A single frame of pixel data within an <see cref="Image{TPixel}"/>.</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <remarks>
/// <para>
/// Like <see cref="Image{TPixel}"/>, a frame is not thread-safe: concurrent reads are fine, but a
/// frame must not be mutated while another thread reads or writes it.
/// </para>
/// <para>
/// The pixels live in one contiguous buffer taken from the <see cref="Configuration.MemoryAllocator"/> in effect
/// when the frame was created. The buffer goes back to that allocator when the owning image is disposed or a
/// processing operation replaces it, so spans and memory obtained from a frame must not be used after either.
/// </para>
/// </remarks>
public sealed class ImageFrame<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>
    /// The buffer, holding one reference on behalf of this frame; <see langword="null"/> once the frame is
    /// disposed or its buffer has been moved into another frame. Swapped with interlocked operations so that the
    /// reference is released exactly once however disposal and replacement interleave.
    /// </summary>
    private PixelBuffer<TPixel>? buffer;

    /// <summary>The pixels, exactly <c>Width * Height</c> long; a cached copy of the buffer's memory.</summary>
    private Memory<TPixel> pixels;

    /// <summary>
    /// Non-<see langword="null"/> when <see cref="pixels"/> starts at index 0 of this array, which lets the hot
    /// paths skip the <see cref="Memory{T}"/> indirection. A pooled array may be longer than the frame.
    /// </summary>
    private TPixel[]? array;

    internal ImageFrame(int width, int height)
        : this(width, height, AllocationOptions.Clean)
    {
    }

    /// <summary>
    /// Allocates a frame from the current allocator after charging it to the operation's pixel budget.
    /// <see cref="AllocationOptions.None"/> is only for callers that write every pixel before the frame can be
    /// read; with a pooling allocator an uninitialized buffer holds another image's pixels.
    /// </summary>
    internal ImageFrame(int width, int height, AllocationOptions options)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.Width = width;
        this.Height = height;
        long length = (long)width * height;
        if (length > int.MaxValue)
            throw new InvalidImageContentException($"Image dimensions {width}x{height} exceed the supported buffer size.");
        OperationExecution.ReservePixelBuffer<TPixel>(length);
        this.Attach(PixelBuffer<TPixel>.Allocate((int)length, options));
        this.Metadata = new ImageFrameMetadata();
    }

    internal ImageFrame(int width, int height, TPixel[] pixels)
        : this(width, height, pixels, null)
    {
    }

    /// <summary>Creates a frame over a library-built array; the garbage collector keeps owning the array.</summary>
    internal ImageFrame(int width, int height, TPixel[] pixels, ImageFrameMetadata? metadata)
        : this(width, height, PixelBuffer<TPixel>.Adopt(pixels, FrameLength(width, height)), metadata)
    {
    }

    /// <summary>Creates a frame over caller-owned memory. The buffer is never copied or freed.</summary>
    internal ImageFrame(int width, int height, Memory<TPixel> pixels, ImageFrameMetadata? metadata)
        : this(width, height, PixelBuffer<TPixel>.Wrap(pixels), metadata)
    {
    }

    /// <summary>Creates a frame that takes over one reference to <paramref name="buffer"/>.</summary>
    internal ImageFrame(int width, int height, PixelBuffer<TPixel> buffer, ImageFrameMetadata? metadata)
    {
        this.Width = width;
        this.Height = height;
        this.Attach(buffer);
        this.Metadata = metadata ?? new ImageFrameMetadata();
    }

    /// <summary>The width of the frame in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>The height of the frame in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The frame-level metadata (per-frame GIF/TIFF facts and optional per-frame profiles).</summary>
    public ImageFrameMetadata Metadata { get; }

    /// <summary>Gets or sets the pixel at the given coordinates.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are outside the frame.</exception>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public TPixel this[int x, int y]
    {
        get
        {
            this.EnsureNotDisposed();
            this.CheckCoordinates(x, y);
            return this.Buffer[(y * this.Width) + x];
        }

        set
        {
            this.EnsureNotDisposed();
            this.CheckCoordinates(x, y);
            this.Buffer[(y * this.Width) + x] = value;
        }
    }

    /// <summary>Gets a span covering a single row of pixels.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row index is outside the frame.</exception>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public Span<TPixel> GetRowSpan(int rowIndex)
    {
        OperationExecution.Checkpoint();
        this.EnsureNotDisposed();
        if ((uint)rowIndex >= (uint)this.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, "Row index is outside the image bounds.");
        }

        return this.Buffer.Slice(rowIndex * this.Width, this.Width);
    }

    /// <summary>Executes the given action against the frame's pixel buffer.</summary>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public void ProcessPixelRows(PixelAccessorAction<TPixel> processPixels)
    {
        ArgumentNullException.ThrowIfNull(processPixels);
        this.EnsureNotDisposed();
        processPixels(new PixelAccessor<TPixel>(this));
    }

    /// <summary>
    /// Exposes the frame's backing buffer without copying it. Writing through the returned memory
    /// changes the frame, and the memory becomes invalid once a size-changing operation replaces the
    /// buffer or the owning image is disposed, hence the name: with the default pooling allocator the
    /// buffer is then reused for another image, so a stale reference reads or writes that image's pixels.
    /// </summary>
    /// <param name="memory">The frame's pixels in row-major order, exactly <c>Width * Height</c> long.</param>
    /// <returns><see langword="true"/>; the buffer of a frame is always a single contiguous block.</returns>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public bool DangerousTryGetSinglePixelMemory(out Memory<TPixel> memory)
    {
        this.EnsureNotDisposed();
        memory = this.pixels;
        return true;
    }

    /// <summary>Copies the frame's pixels, row by row without padding, into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">The destination is too small to hold the frame.</exception>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public void CopyPixelDataTo(Span<TPixel> destination)
    {
        this.EnsureNotDisposed();
        Span<TPixel> source = this.Buffer[..(this.Width * this.Height)];
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} pixels but the frame needs {source.Length}.", nameof(destination));
        }

        source.CopyTo(destination);
        GC.KeepAlive(this);
    }

    /// <summary>
    /// Copies the frame's pixels, row by row without padding, into <paramref name="destination"/> as raw
    /// bytes in the memory layout of <typeparamref name="TPixel"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is too small to hold the frame.</exception>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public void CopyPixelDataTo(Span<byte> destination)
    {
        this.EnsureNotDisposed();
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(this.Buffer[..(this.Width * this.Height)]);
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} bytes but the frame needs {source.Length}.", nameof(destination));
        }

        source.CopyTo(destination);
        GC.KeepAlive(this);
    }

    /// <summary>Gets the whole backing buffer as a single contiguous span, exactly <c>Width * Height</c> long.</summary>
    internal Span<TPixel> PixelSpan
    {
        get
        {
            this.EnsureNotDisposed();
            return this.Buffer;
        }
    }

    /// <summary>
    /// The pixels as an array of exactly <c>Width * Height</c> elements that may be kept for as long as the caller
    /// likes. When the buffer is such an array it is returned itself, and from then on it is never handed back to an
    /// allocator (it may now outlive the frame), so writes through it reach the frame. Otherwise - a pooled array
    /// rounded up to a size class, native memory, or caller memory that is not a whole array - the result is a copy
    /// and writes through it do not reach the frame.
    /// </summary>
    /// <remarks>
    /// This is the slow escape hatch for code that needs a <c>TPixel[]</c>. Moving a buffer into another frame is
    /// <see cref="TakeBuffer"/>; reading through an array for the duration of an operation is
    /// <see cref="TryGetBackingArray"/>.
    /// </remarks>
    internal TPixel[] PixelArray
    {
        get
        {
            this.EnsureNotDisposed();
            if (this.array is { } exact && exact.Length == this.pixels.Length)
            {
                this.buffer!.Abandon();
                return exact;
            }

            if (this.buffer!.IsCallerOwned)
            {
                OperationExecution.ReservePixelBuffer<TPixel>(this.pixels.Length);
            }

            // A library-owned buffer used to be exposed here without any allocation, so the copy that stands in for
            // it is not charged to the pixel budget; the accounting stays what it was before pooling.
            return this.pixels.ToArray();
        }
    }

    /// <summary>The backing buffer, preferring the array fast path when there is one.</summary>
    private Span<TPixel> Buffer => this.array is { } backing ? backing.AsSpan(0, this.pixels.Length) : this.pixels.Span;

    /// <summary>
    /// Gets the managed array the pixels start at, for reading or writing it while an operation runs. The array
    /// may be longer than <c>Width * Height</c>; elements past that belong to nobody and must not be touched. It
    /// is only valid while the frame is alive and its buffer has not been replaced, so it must never be stored.
    /// </summary>
    /// <returns><see langword="false"/> when the pixels are not in a managed array (native or wrapped memory).</returns>
    internal bool TryGetBackingArray([NotNullWhen(true)] out TPixel[]? backing)
    {
        this.EnsureNotDisposed();
        backing = this.array;
        return backing is not null;
    }

    internal ImageFrame<TPixel> Clone()
    {
        Span<TPixel> source = this.PixelSpan;
        OperationExecution.ReservePixelBuffer<TPixel>(source.Length);

        // Uninitialized is safe: the copy below writes every pixel before the clone is reachable.
        PixelBuffer<TPixel> copy = PixelBuffer<TPixel>.Allocate(source.Length, AllocationOptions.None);
        source.CopyTo(copy.Memory.Span);
        return new ImageFrame<TPixel>(this.Width, this.Height, copy, this.Metadata.DeepClone());
    }

    internal ImageFrame<TPixel2> CloneAs<TPixel2>()
        where TPixel2 : unmanaged, IPixel<TPixel2>
    {
        Span<TPixel> source = this.PixelSpan;
        OperationExecution.ReservePixelBuffer<TPixel2>(source.Length);

        // Uninitialized is safe: the conversion writes all source.Length pixels before the clone is reachable.
        PixelBuffer<TPixel2> converted = PixelBuffer<TPixel2>.Allocate(source.Length, AllocationOptions.None);
        var target = new ImageFrame<TPixel2>(this.Width, this.Height, converted, this.Metadata.DeepClone());
        PixelOps.Convert<TPixel, TPixel2>(source, target.PixelSpan);
        GC.KeepAlive(this);
        return target;
    }

    /// <summary>
    /// Creates a frame that shares this frame's buffer, with a deep copy of the metadata: the start of a
    /// copy-on-write clone. Neither frame may be written until one of them has been given its own buffer
    /// (<see cref="DuplicateBuffer"/>, <see cref="TakeBuffer"/> or <see cref="ReplaceBuffer"/>); the buffer goes
    /// back to its allocator only when both have let go of it.
    /// </summary>
    internal ImageFrame<TPixel> ShareBuffer()
    {
        this.EnsureNotDisposed();
        return new ImageFrame<TPixel>(this.Width, this.Height, this.buffer!.Share(), this.Metadata.DeepClone());
    }

    /// <summary>Gives the frame a private copy of its pixels and lets go of the buffer it was sharing.</summary>
    internal void DuplicateBuffer()
    {
        Span<TPixel> source = this.PixelSpan;
        OperationExecution.ReservePixelBuffer<TPixel>(source.Length);

        // Uninitialized is safe: the copy below writes every pixel before the frame switches to it.
        PixelBuffer<TPixel> copy = PixelBuffer<TPixel>.Allocate(source.Length, AllocationOptions.None);
        source.CopyTo(copy.Memory.Span);
        this.Swap(copy, this.Width, this.Height);
    }

    /// <summary>
    /// Swaps in a library-built array as the new backing buffer, used by size-changing operations. The array stays
    /// owned by the garbage collector; the previous buffer is released.
    /// </summary>
    internal void ReplaceBuffer(TPixel[] newPixels, int width, int height)
        => this.Swap(PixelBuffer<TPixel>.Adopt(newPixels, FrameLength(width, height)), width, height);

    /// <summary>
    /// Moves <paramref name="source"/>'s buffer, and its size, into this frame and releases this frame's previous
    /// buffer: how a processing operation installs the frame it built. <paramref name="source"/> is consumed - it
    /// behaves as disposed afterwards - so the buffer keeps exactly one owner. Passing the frame itself does nothing.
    /// </summary>
    internal void TakeBuffer(ImageFrame<TPixel> source)
    {
        if (ReferenceEquals(source, this))
        {
            return;
        }

        this.EnsureNotDisposed();
        PixelBuffer<TPixel> moved = source.Detach()
            ?? throw new ObjectDisposedException(nameof(Image<TPixel>), "Trying to execute an operation on a disposed image.");
        this.Swap(moved, source.Width, source.Height);
    }

    /// <summary>
    /// Disposes the frame: every later pixel access throws, and the frame's reference to its buffer is released,
    /// which hands the buffer back to its allocator unless another frame still shares it. Safe to call twice.
    /// </summary>
    internal void ReleaseBuffer() => this.Detach()?.Release();

    /// <summary>Marks the frame as belonging to a disposed image and releases its buffer; see <see cref="ReleaseBuffer"/>.</summary>
    internal void MarkDisposed() => this.ReleaseBuffer();

    // A note on GC.KeepAlive(this) in methods that end in a span-only loop: with UnmanagedMemoryAllocator the buffer
    // is native memory, freed by a finalizer if its image is dropped without being disposed, and a span does not
    // keep it reachable. Keeping the frame alive until the loop is done keeps the buffer's owner alive with it.

    internal void EnsureNotDisposed()
    {
        if (Volatile.Read(ref this.buffer) is null)
        {
            throw new ObjectDisposedException(
                nameof(Image<TPixel>), "Trying to execute an operation on a disposed image.");
        }
    }

    private void CheckCoordinates(int x, int y)
    {
        if ((uint)x >= (uint)this.Width || (uint)y >= (uint)this.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"Coordinates ({x}, {y}) are outside the image bounds {this.Width}x{this.Height}.");
        }
    }

    /// <summary>The pixel count of a frame, saturated so that a bogus size fails later with a clear message.</summary>
    private static int FrameLength(int width, int height) => (int)Math.Clamp((long)width * height, 0, int.MaxValue);

    /// <summary>Installs <paramref name="incoming"/>, whose reference this frame now owns, as the frame's buffer.</summary>
    private void Attach(PixelBuffer<TPixel> incoming)
    {
        int length = FrameLength(this.Width, this.Height);
        Memory<TPixel> memory = incoming.Memory;
        this.pixels = memory.Length > length ? memory[..length] : memory;
        this.array = incoming.Array;
        Volatile.Write(ref this.buffer, incoming);
    }

    /// <summary>Replaces the buffer and size, then releases this frame's reference to the previous buffer.</summary>
    private void Swap(PixelBuffer<TPixel> incoming, int width, int height)
    {
        PixelBuffer<TPixel>? previous = Interlocked.Exchange(ref this.buffer, null);
        if (previous is null)
        {
            // The frame was disposed with its image: it stays disposed, and nobody is left to own the new buffer.
            incoming.Release();
            return;
        }

        this.Width = width;
        this.Height = height;
        this.Attach(incoming);
        previous?.Release();
    }

    /// <summary>Takes this frame's buffer reference away, leaving the frame disposed; <see langword="null"/> if it had none.</summary>
    private PixelBuffer<TPixel>? Detach()
    {
        PixelBuffer<TPixel>? detached = Interlocked.Exchange(ref this.buffer, null);
        if (detached is not null)
        {
            this.pixels = default;
            this.array = null;
        }

        return detached;
    }
}
