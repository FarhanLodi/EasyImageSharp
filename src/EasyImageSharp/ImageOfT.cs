using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>An in-memory image with a strongly typed pixel format.</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <remarks>
/// <para>
/// An instance is not thread-safe. Any number of threads may read the same image concurrently, but a
/// single mutation - writing a pixel, running a processing operation, adding or removing a frame -
/// must not overlap with any other access to that image. Guard shared images with your own lock.
/// </para>
/// <para>
/// Distinct images are independent: decoding, encoding, cloning and processing several images in
/// parallel is safe, and that is the intended way to use the library from a server.
/// </para>
/// </remarks>
public sealed class Image<TPixel> : Image
    where TPixel : unmanaged, IPixel<TPixel>
{
    private bool isDisposed;

    /// <summary>Creates a new image with all pixels set to their default (zero) value.</summary>
    public Image(int width, int height)
        : this(new ImageFrame<TPixel>(width, height))
    {
    }

    /// <summary>Creates a new image with all pixels set to <paramref name="backgroundColor"/>.</summary>
    public Image(int width, int height, TPixel backgroundColor)
        : this(new ImageFrame<TPixel>(width, height))
    {
        this.Frames.RootFrame.PixelSpan.Fill(backgroundColor);
    }

    internal Image(ImageFrame<TPixel> frame)
        : this(new List<ImageFrame<TPixel>> { frame })
    {
    }

    internal Image(List<ImageFrame<TPixel>> frames)
        : this(frames, null)
    {
    }

    internal Image(List<ImageFrame<TPixel>> frames, ImageMetadata? metadata)
        : base(metadata)
        => this.Frames = new ImageFrameCollection<TPixel>(this, frames);

    /// <summary>The frames contained in this image (e.g. TIFF pages). The first frame is the root frame.</summary>
    public ImageFrameCollection<TPixel> Frames { get; }

    /// <inheritdoc/>
    public override int Width => this.Frames.RootFrame.Width;

    /// <inheritdoc/>
    public override int Height => this.Frames.RootFrame.Height;

    /// <summary>Gets or sets the pixel at the given coordinates on the root frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are outside the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public TPixel this[int x, int y]
    {
        get => this.Frames.RootFrame[x, y];
        set => this.Frames.RootFrame[x, y] = value;
    }

    /// <summary>
    /// Creates an image that reads and writes <paramref name="memory"/> directly, without copying it.
    /// </summary>
    /// <param name="memory">
    /// The pixels to wrap, in row-major order with no padding between rows. It must hold at least
    /// <paramref name="width"/> * <paramref name="height"/> pixels; any surplus is ignored.
    /// </param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>A single-frame image over the caller's buffer.</returns>
    /// <remarks>
    /// The caller owns the memory and must keep it alive and unmoved for as long as the image is used;
    /// disposing the image does not free it. Writing to the image writes through to the buffer and
    /// vice versa. A processing operation that changes the image size allocates a fresh buffer, after
    /// which the image and the wrapped memory are no longer connected.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer is too small for the given size.</exception>
    public static Image<TPixel> WrapMemory(Memory<TPixel> memory, int width, int height)
    {
        int required = CheckedPixelCount(width, height);
        if (memory.Length < required)
        {
            throw new ArgumentException(
                $"The buffer holds {memory.Length} pixels but {width}x{height} requires {required}.", nameof(memory));
        }

        return new Image<TPixel>(new ImageFrame<TPixel>(width, height, memory[..required], null));
    }

    /// <summary>
    /// Creates an image that reads and writes <paramref name="memory"/> directly, reinterpreting the
    /// bytes as <typeparamref name="TPixel"/> values without copying them.
    /// </summary>
    /// <param name="memory">
    /// The pixel bytes to wrap, in row-major order with no padding between rows and in the memory
    /// layout of <typeparamref name="TPixel"/>.
    /// </param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>A single-frame image over the caller's buffer.</returns>
    /// <remarks>
    /// The caller owns the memory and must keep it alive and unmoved for as long as the image is used;
    /// disposing the image does not free it. This is the overload to use for a buffer obtained from
    /// native code or another imaging stack, provided its rows are tightly packed.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer is too small for the given size.</exception>
    public static Image<TPixel> WrapMemory(Memory<byte> memory, int width, int height)
    {
        int required = CheckedPixelCount(width, height);
        long requiredBytes = (long)required * Unsafe.SizeOf<TPixel>();
        if (memory.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"The buffer holds {memory.Length} bytes but {width}x{height} requires {requiredBytes}.", nameof(memory));
        }

        return WrapMemory(new ByteMemoryManager(memory).Memory, width, height);
    }

    /// <summary>Executes the given action with fast row-level access to the root frame's pixels.</summary>
    /// <param name="processPixels">The callback receiving row access.</param>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public void ProcessPixelRows(PixelAccessorAction<TPixel> processPixels)
    {
        this.EnsureNotDisposed();
        this.Frames.RootFrame.ProcessPixelRows(processPixels);
    }

    /// <summary>
    /// Executes the given action with fast row-level access to the root frames of this image and
    /// <paramref name="second"/>, which is how two images are read or blended together in one pass.
    /// </summary>
    /// <typeparam name="TPixel2">The pixel format of the second image.</typeparam>
    /// <param name="second">The second image.</param>
    /// <param name="processPixels">The callback receiving row access to both images.</param>
    /// <remarks>The images may differ in size; the callback is responsible for staying in bounds.</remarks>
    /// <exception cref="ObjectDisposedException">Either image has been disposed.</exception>
    public void ProcessPixelRows<TPixel2>(Image<TPixel2> second, PixelAccessorAction<TPixel, TPixel2> processPixels)
        where TPixel2 : unmanaged, IPixel<TPixel2>
    {
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(processPixels);
        this.EnsureNotDisposed();
        second.EnsureNotDisposed();

        processPixels(
            new PixelAccessor<TPixel>(this.Frames.RootFrame),
            new PixelAccessor<TPixel2>(second.Frames.RootFrame));
    }

    /// <summary>
    /// Executes the given action with fast row-level access to the root frames of this image,
    /// <paramref name="second"/> and <paramref name="third"/>.
    /// </summary>
    /// <typeparam name="TPixel2">The pixel format of the second image.</typeparam>
    /// <typeparam name="TPixel3">The pixel format of the third image.</typeparam>
    /// <param name="second">The second image.</param>
    /// <param name="third">The third image.</param>
    /// <param name="processPixels">The callback receiving row access to all three images.</param>
    /// <remarks>The images may differ in size; the callback is responsible for staying in bounds.</remarks>
    /// <exception cref="ObjectDisposedException">Any of the images has been disposed.</exception>
    public void ProcessPixelRows<TPixel2, TPixel3>(
        Image<TPixel2> second,
        Image<TPixel3> third,
        PixelAccessorAction<TPixel, TPixel2, TPixel3> processPixels)
        where TPixel2 : unmanaged, IPixel<TPixel2>
        where TPixel3 : unmanaged, IPixel<TPixel3>
    {
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(processPixels);
        this.EnsureNotDisposed();
        second.EnsureNotDisposed();
        third.EnsureNotDisposed();

        processPixels(
            new PixelAccessor<TPixel>(this.Frames.RootFrame),
            new PixelAccessor<TPixel2>(second.Frames.RootFrame),
            new PixelAccessor<TPixel3>(third.Frames.RootFrame));
    }

    /// <summary>
    /// Exposes the root frame's backing buffer without copying it. Writing through the returned memory
    /// changes the image, and the memory becomes invalid once a size-changing operation replaces the
    /// buffer or the image is disposed, hence the name.
    /// </summary>
    /// <param name="memory">The root frame's pixels in row-major order.</param>
    /// <returns><see langword="true"/>; the root frame is always a single contiguous block.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public bool DangerousTryGetSinglePixelMemory(out Memory<TPixel> memory)
    {
        this.EnsureNotDisposed();
        return this.Frames.RootFrame.DangerousTryGetSinglePixelMemory(out memory);
    }

    /// <summary>Copies the root frame's pixels, row by row without padding, into <paramref name="destination"/>.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <exception cref="ArgumentException">The destination is too small to hold the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public void CopyPixelDataTo(Span<TPixel> destination)
    {
        this.EnsureNotDisposed();
        this.Frames.RootFrame.CopyPixelDataTo(destination);
    }

    /// <summary>
    /// Copies the root frame's pixels, row by row without padding, into <paramref name="destination"/>
    /// as raw bytes in the memory layout of <typeparamref name="TPixel"/>.
    /// </summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <exception cref="ArgumentException">The destination is too small to hold the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public void CopyPixelDataTo(Span<byte> destination)
    {
        this.EnsureNotDisposed();
        this.Frames.RootFrame.CopyPixelDataTo(destination);
    }

    /// <summary>Creates a deep copy of this image including all frames and metadata.</summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image<TPixel> Clone()
    {
        this.EnsureNotDisposed();
        var frames = new List<ImageFrame<TPixel>>(this.Frames.Count);
        foreach (ImageFrame<TPixel> frame in this.Frames)
        {
            frames.Add(frame.Clone());
        }

        return new Image<TPixel>(frames, this.Metadata.DeepClone());
    }

    /// <summary>Creates a deep copy of this image (frames and metadata) converted to another pixel format.</summary>
    /// <typeparam name="TPixel2">The pixel format of the copy.</typeparam>
    /// <remarks>
    /// The conversion keeps as much precision as the two formats share: between two formats that both
    /// carry more than 8 bits per component the values are passed through normalised floating point,
    /// so no component is squeezed into 8 bits on the way.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image<TPixel2> CloneAs<TPixel2>()
        where TPixel2 : unmanaged, IPixel<TPixel2>
    {
        this.EnsureNotDisposed();
        var frames = new List<ImageFrame<TPixel2>>(this.Frames.Count);
        foreach (ImageFrame<TPixel> frame in this.Frames)
        {
            frames.Add(frame.CloneAs<TPixel2>());
        }

        return new Image<TPixel2>(frames, this.Metadata.DeepClone());
    }

    internal override Rgba32 GetPixelRgba32(int x, int y) => this[x, y].ToRgba32();

    internal override void AcceptEncoder(IImageEncoder encoder, Stream stream)
    {
        this.EnsureNotDisposed();
        encoder.Encode(this, stream);
    }

    internal void EnsureNotDisposed()
    {
        if (this.isDisposed)
        {
            throw new ObjectDisposedException(nameof(Image<TPixel>), "Trying to execute an operation on a disposed image.");
        }
    }

    /// <summary>
    /// Releases the image. Buffers the library allocated are left to the garbage collector, and memory
    /// wrapped with <see cref="WrapMemory(Memory{TPixel}, int, int)"/> stays owned by the caller; what
    /// disposal does is make every further operation on the image throw.
    /// </summary>
    public override void Dispose()
    {
        if (this.isDisposed)
        {
            return;
        }

        this.isDisposed = true;
        foreach (ImageFrame<TPixel> frame in this.Frames.InnerList)
        {
            frame.MarkDisposed();
        }
    }

    /// <summary>Validates a size and returns the pixel count it needs.</summary>
    private static int CheckedPixelCount(int width, int height)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        long count = (long)width * height;
        return count <= int.MaxValue
            ? (int)count
            : throw new ArgumentException($"Image dimensions {width}x{height} exceed the supported buffer size.", nameof(width));
    }

    /// <summary>Presents a byte buffer as <typeparamref name="TPixel"/> values without copying it.</summary>
    private sealed class ByteMemoryManager : MemoryManager<TPixel>
    {
        private readonly Memory<byte> source;

        public ByteMemoryManager(Memory<byte> source) => this.source = source;

        public override Span<TPixel> GetSpan() => MemoryMarshal.Cast<byte, TPixel>(this.source.Span);

        public override MemoryHandle Pin(int elementIndex = 0)
            => this.source[(elementIndex * Unsafe.SizeOf<TPixel>())..].Pin();

        public override void Unpin()
        {
            // The handle returned by Pin owns the pinning; there is nothing of ours to release.
        }

        protected override void Dispose(bool disposing)
        {
            // The buffer belongs to the caller.
        }
    }
}
