using EasyImageSharp.Memory;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>Frame allocation helpers for operations that fill every pixel they allocate.</summary>
internal static class FrameFactory
{
    /// <summary>
    /// Allocates a frame whose buffer is <em>not</em> zeroed. Only for callers that write every pixel before
    /// anything can read one - a resize, a rotation, a full decode - where pre-zeroing the buffer is pure
    /// overhead (a 24 MB Rgba32 frame costs a few milliseconds to clear).
    /// </summary>
    /// <remarks>
    /// With the default pooling allocator the buffer holds the pixels of whichever image used it last, so a caller
    /// that leaves even one pixel unwritten exposes another image's data. Every call site carries a comment saying
    /// why it writes the whole frame; when in doubt, allocate with <c>new ImageFrame&lt;TPixel&gt;(width, height)</c>,
    /// which is zeroed. A frame abandoned half-written because the operation threw is never exposed: it is
    /// unreachable, and its buffer goes to the garbage collector rather than back to the pool.
    /// </remarks>
    public static ImageFrame<TPixel> CreateUninitialized<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
        => new(width, height, AllocationOptions.None);

    /// <summary>
    /// Copies the frame list and all metadata of <paramref name="source"/> but keeps pointing at its pixel
    /// buffers. The result is only safe while nothing writes to those pixels; the processing pipeline uses
    /// it as the starting point of a copy-on-write clone and replaces or duplicates every shared buffer
    /// before returning. Each shared buffer is reference counted, so it goes back to its allocator only once
    /// both images have let go of it, whichever is disposed first.
    /// </summary>
    public static Image<TPixel> ShallowClone<TPixel>(Image<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        source.EnsureNotDisposed();
        var frames = new List<ImageFrame<TPixel>>(source.Frames.Count);
        try
        {
            foreach (ImageFrame<TPixel> frame in source.Frames)
            {
                frames.Add(frame.ShareBuffer());
            }
        }
        catch
        {
            foreach (ImageFrame<TPixel> frame in frames)
            {
                frame.ReleaseBuffer();
            }

            throw;
        }

        return new Image<TPixel>(frames, source.Metadata.DeepClone());
    }

    /// <summary>Duplicates a frame's pixel buffer, leaving its metadata alone.</summary>
    public static void DuplicateBuffer<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
        => frame.DuplicateBuffer();
}
