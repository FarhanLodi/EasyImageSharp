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
    public static ImageFrame<TPixel> CreateUninitialized<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        long length = (long)width * height;
        if (length > int.MaxValue)
        {
            throw new InvalidImageContentException($"Image dimensions {width}x{height} exceed the supported buffer size.");
        }

        return new ImageFrame<TPixel>(width, height, GC.AllocateUninitializedArray<TPixel>((int)length));
    }

    /// <summary>
    /// Copies the frame list and all metadata of <paramref name="source"/> but keeps pointing at its pixel
    /// buffers. The result is only safe while nothing writes to those pixels; the processing pipeline uses
    /// it as the starting point of a copy-on-write clone and replaces or duplicates every shared buffer
    /// before returning.
    /// </summary>
    public static Image<TPixel> ShallowClone<TPixel>(Image<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        source.EnsureNotDisposed();
        var frames = new List<ImageFrame<TPixel>>(source.Frames.Count);
        foreach (ImageFrame<TPixel> frame in source.Frames)
        {
            frames.Add(new ImageFrame<TPixel>(frame.Width, frame.Height, frame.PixelArray, frame.Metadata.DeepClone()));
        }

        return new Image<TPixel>(frames, source.Metadata.DeepClone());
    }

    /// <summary>Duplicates a frame's pixel buffer, leaving its metadata alone.</summary>
    public static void DuplicateBuffer<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        TPixel[] source = frame.PixelArray;
        TPixel[] copy = GC.AllocateUninitializedArray<TPixel>(source.Length);
        source.AsSpan().CopyTo(copy);
        frame.ReplaceBuffer(copy, frame.Width, frame.Height);
    }
}
