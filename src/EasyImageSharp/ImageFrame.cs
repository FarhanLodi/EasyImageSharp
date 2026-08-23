using System.Runtime.InteropServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>A single frame of pixel data within an <see cref="Image{TPixel}"/>.</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <remarks>
/// Like <see cref="Image{TPixel}"/>, a frame is not thread-safe: concurrent reads are fine, but a
/// frame must not be mutated while another thread reads or writes it.
/// </remarks>
public sealed class ImageFrame<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private Memory<TPixel> pixels;

    /// <summary>
    /// Non-<see langword="null"/> when <see cref="pixels"/> covers exactly this array, which lets the
    /// hot paths skip the <see cref="Memory{T}"/> indirection and lets processing operations hand the
    /// buffer around without copying.
    /// </summary>
    private TPixel[]? array;

    private bool isDisposed;

    internal ImageFrame(int width, int height)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.Width = width;
        this.Height = height;
        this.array = new TPixel[(long)width * height <= int.MaxValue
            ? width * height
            : throw new InvalidImageContentException($"Image dimensions {width}x{height} exceed the supported buffer size.")];
        this.pixels = this.array;
        this.Metadata = new ImageFrameMetadata();
    }

    internal ImageFrame(int width, int height, TPixel[] pixels)
        : this(width, height, pixels, null)
    {
    }

    internal ImageFrame(int width, int height, TPixel[] pixels, ImageFrameMetadata? metadata)
    {
        this.Width = width;
        this.Height = height;
        this.array = pixels;
        this.pixels = pixels;
        this.Metadata = metadata ?? new ImageFrameMetadata();
    }

    /// <summary>Creates a frame over caller-owned memory. The buffer is never copied or freed.</summary>
    internal ImageFrame(int width, int height, Memory<TPixel> pixels, ImageFrameMetadata? metadata)
    {
        this.Width = width;
        this.Height = height;
        this.pixels = pixels;
        this.array = MemoryMarshal.TryGetArray<TPixel>(pixels, out ArraySegment<TPixel> segment)
            && segment.Array is { } backing && segment.Offset == 0 && segment.Count == backing.Length
                ? backing
                : null;
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
    /// buffer or the owning image is disposed, hence the name.
    /// </summary>
    /// <param name="memory">The frame's pixels in row-major order, exactly <c>Width * Height</c> long.</param>
    /// <returns><see langword="true"/>; the buffer of a frame is always a single contiguous block.</returns>
    /// <exception cref="ObjectDisposedException">The owning image has been disposed.</exception>
    public bool DangerousTryGetSinglePixelMemory(out Memory<TPixel> memory)
    {
        this.EnsureNotDisposed();
        memory = this.pixels[..(this.Width * this.Height)];
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
    }

    /// <summary>Gets the whole backing buffer as a single contiguous span.</summary>
    internal Span<TPixel> PixelSpan
    {
        get
        {
            this.EnsureNotDisposed();
            return this.Buffer;
        }
    }

    /// <summary>
    /// The backing buffer as an array. Frames allocated by the library hand out their own buffer;
    /// a frame wrapping caller memory that is not a whole array falls back to a copy.
    /// </summary>
    internal TPixel[] PixelArray
    {
        get
        {
            this.EnsureNotDisposed();
            return this.array ?? this.pixels.ToArray();
        }
    }

    /// <summary>The backing buffer, preferring the array fast path when there is one.</summary>
    private Span<TPixel> Buffer => this.array is not null ? this.array.AsSpan() : this.pixels.Span;

    internal ImageFrame<TPixel> Clone()
    {
        Span<TPixel> source = this.PixelSpan;
        var copy = new TPixel[source.Length];
        source.CopyTo(copy);
        return new ImageFrame<TPixel>(this.Width, this.Height, copy, this.Metadata.DeepClone());
    }

    internal ImageFrame<TPixel2> CloneAs<TPixel2>()
        where TPixel2 : unmanaged, IPixel<TPixel2>
    {
        Span<TPixel> source = this.PixelSpan;
        var target = new ImageFrame<TPixel2>(this.Width, this.Height, new TPixel2[source.Length], this.Metadata.DeepClone());
        PixelOps.Convert<TPixel, TPixel2>(source, target.PixelSpan);
        return target;
    }

    /// <summary>Swaps in a new backing buffer, used by size-changing processing operations.</summary>
    internal void ReplaceBuffer(TPixel[] newPixels, int width, int height)
    {
        this.array = newPixels;
        this.pixels = newPixels;
        this.Width = width;
        this.Height = height;
    }

    /// <summary>Marks the frame as belonging to a disposed image; the buffer itself is left to the GC.</summary>
    internal void MarkDisposed() => this.isDisposed = true;

    internal void EnsureNotDisposed()
    {
        if (this.isDisposed)
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
}
