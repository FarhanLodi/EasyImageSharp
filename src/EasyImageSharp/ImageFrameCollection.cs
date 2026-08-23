using System.Collections;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>The collection of frames owned by an <see cref="Image{TPixel}"/> (e.g. TIFF pages).</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <remarks>
/// The collection is ordered and always holds at least one frame. Frames may differ in size from the
/// root frame; codecs that require uniform frames say so. Like the owning image, the collection is
/// not safe for concurrent mutation.
/// </remarks>
public sealed class ImageFrameCollection<TPixel> : IReadOnlyList<ImageFrame<TPixel>>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly List<ImageFrame<TPixel>> frames;
    private readonly Image<TPixel> owner;

    internal ImageFrameCollection(Image<TPixel> owner, List<ImageFrame<TPixel>> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("An image must contain at least one frame.", nameof(frames));
        }

        this.owner = owner;
        this.frames = frames;
    }

    /// <summary>The number of frames in the image; never less than one.</summary>
    public int Count => this.frames.Count;

    /// <summary>Gets the first frame; the one exposed directly by <see cref="Image{TPixel}"/>.</summary>
    public ImageFrame<TPixel> RootFrame => this.frames[0];

    /// <summary>Gets the frame at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the collection.</exception>
    public ImageFrame<TPixel> this[int index] => this.frames[index];

    /// <summary>Returns a new single-frame image containing a deep copy of the frame at <paramref name="index"/> (and of the image metadata).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the collection.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image<TPixel> CloneFrame(int index)
    {
        this.owner.EnsureNotDisposed();
        return new Image<TPixel>(
            new List<ImageFrame<TPixel>> { this.frames[index].Clone() }, this.owner.Metadata.DeepClone());
    }

    /// <summary>Appends a deep copy of the given frame and returns the stored copy.</summary>
    /// <param name="frame">The frame to copy; it may belong to another image.</param>
    /// <returns>The copy that was added to this collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Either image has been disposed.</exception>
    public ImageFrame<TPixel> AddFrame(ImageFrame<TPixel> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        this.owner.EnsureNotDisposed();
        ImageFrame<TPixel> clone = frame.Clone();
        this.frames.Add(clone);
        return clone;
    }

    /// <summary>
    /// Appends a new frame the size of the root frame, filled from <paramref name="pixels"/> in
    /// row-major order, and returns it.
    /// </summary>
    /// <param name="pixels">
    /// The pixels to copy in; it must hold at least <c>Width * Height</c> values and any surplus is ignored.
    /// </param>
    /// <returns>The frame that was added.</returns>
    /// <exception cref="ArgumentException">The buffer is too small for the root frame's size.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public ImageFrame<TPixel> AddFrame(ReadOnlySpan<TPixel> pixels)
    {
        this.owner.EnsureNotDisposed();
        int width = this.RootFrame.Width;
        int height = this.RootFrame.Height;
        int required = width * height;
        if (pixels.Length < required)
        {
            throw new ArgumentException(
                $"The pixel buffer holds {pixels.Length} pixels but {width}x{height} requires {required}.", nameof(pixels));
        }

        var buffer = new TPixel[required];
        pixels[..required].CopyTo(buffer);
        var frame = new ImageFrame<TPixel>(width, height, buffer);
        this.frames.Add(frame);
        return frame;
    }

    /// <summary>Inserts a deep copy of the given frame at <paramref name="index"/> and returns the stored copy.</summary>
    /// <param name="index">
    /// The position the copy takes; frames from there on move up by one. An index equal to
    /// <see cref="Count"/> appends, and index 0 makes the copy the new root frame.
    /// </param>
    /// <param name="frame">The frame to copy; it may belong to another image.</param>
    /// <returns>The copy that was added to this collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative or greater than <see cref="Count"/>.</exception>
    /// <exception cref="ObjectDisposedException">Either image has been disposed.</exception>
    public ImageFrame<TPixel> InsertFrame(int index, ImageFrame<TPixel> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        this.owner.EnsureNotDisposed();
        if ((uint)index > (uint)this.frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The index must be between 0 and {this.frames.Count}.");
        }

        ImageFrame<TPixel> clone = frame.Clone();
        this.frames.Insert(index, clone);
        return clone;
    }

    /// <summary>Moves the frame at <paramref name="from"/> so that it ends up at index <paramref name="to"/>.</summary>
    /// <param name="from">The index of the frame to move.</param>
    /// <param name="to">The index the frame ends up at once it has been taken out of its old position.</param>
    /// <remarks>Frames are not copied; moving one to its own index leaves the collection untouched.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Either index is outside the collection.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public void MoveFrame(int from, int to)
    {
        this.owner.EnsureNotDisposed();
        if ((uint)from >= (uint)this.frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(from), from, $"The index must be between 0 and {this.frames.Count - 1}.");
        }

        if ((uint)to >= (uint)this.frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(to), to, $"The index must be between 0 and {this.frames.Count - 1}.");
        }

        if (from == to)
        {
            return;
        }

        ImageFrame<TPixel> frame = this.frames[from];
        this.frames.RemoveAt(from);
        this.frames.Insert(to, frame);
    }

    /// <summary>Creates a new empty (all zero pixels) frame of the given size and appends it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public ImageFrame<TPixel> CreateFrame(int width, int height)
    {
        this.owner.EnsureNotDisposed();
        var frame = new ImageFrame<TPixel>(width, height);
        this.frames.Add(frame);
        return frame;
    }

    /// <summary>Removes the frame at <paramref name="index"/> and returns it as a standalone image.</summary>
    /// <param name="index">The index of the frame to export.</param>
    /// <returns>A new single-frame image holding the removed frame and a copy of the image metadata.</returns>
    /// <exception cref="InvalidOperationException">The image has only one frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the collection.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Image<TPixel> ExportFrame(int index)
    {
        this.owner.EnsureNotDisposed();
        if (this.frames.Count == 1)
        {
            throw new InvalidOperationException("Cannot export the last remaining frame of an image.");
        }

        ImageFrame<TPixel> frame = this.frames[index];
        this.frames.RemoveAt(index);
        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, this.owner.Metadata.DeepClone());
    }

    /// <summary>Removes the frame at <paramref name="index"/>.</summary>
    /// <param name="index">The index of the frame to remove.</param>
    /// <remarks>
    /// An image always keeps at least one frame, so removing the only frame is rejected. That check
    /// runs first: on a single-frame image every index, valid or not, reports
    /// <see cref="InvalidOperationException"/>. Otherwise an index outside 0 to <see cref="Count"/> - 1
    /// reports <see cref="ArgumentOutOfRangeException"/>, and the frames after the removed one move
    /// down by one, which makes frame 1 the new root frame when index 0 is removed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The image has only one frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the collection.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public void RemoveFrame(int index)
    {
        this.owner.EnsureNotDisposed();
        if (this.frames.Count == 1)
        {
            throw new InvalidOperationException("Cannot remove the last remaining frame of an image.");
        }

        if ((uint)index >= (uint)this.frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The index must be between 0 and {this.frames.Count - 1}.");
        }

        this.frames.RemoveAt(index);
    }

    internal List<ImageFrame<TPixel>> InnerList => this.frames;

    /// <summary>Returns an enumerator over the frames, in order.</summary>
    /// <remarks>
    /// The enumerator walks the live collection rather than a snapshot: adding, inserting, moving or
    /// removing a frame while an enumeration is in progress invalidates it, and the next step throws
    /// <see cref="InvalidOperationException"/>. Reading frames and writing their pixels during
    /// enumeration is fine.
    /// </remarks>
    public IEnumerator<ImageFrame<TPixel>> GetEnumerator() => this.frames.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
