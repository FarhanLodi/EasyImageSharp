using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>Callback invoked with fast row-level access to an image's pixel buffer.</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <param name="pixelAccessor">Row access to the image's pixels.</param>
public delegate void PixelAccessorAction<TPixel>(PixelAccessor<TPixel> pixelAccessor)
    where TPixel : unmanaged, IPixel<TPixel>;

/// <summary>Callback invoked with fast row-level access to the pixel buffers of two images at once.</summary>
/// <typeparam name="TPixel1">The pixel format of the first image.</typeparam>
/// <typeparam name="TPixel2">The pixel format of the second image.</typeparam>
/// <param name="pixelAccessor1">Row access to the first image's pixels.</param>
/// <param name="pixelAccessor2">Row access to the second image's pixels.</param>
public delegate void PixelAccessorAction<TPixel1, TPixel2>(
    PixelAccessor<TPixel1> pixelAccessor1,
    PixelAccessor<TPixel2> pixelAccessor2)
    where TPixel1 : unmanaged, IPixel<TPixel1>
    where TPixel2 : unmanaged, IPixel<TPixel2>;

/// <summary>Callback invoked with fast row-level access to the pixel buffers of three images at once.</summary>
/// <typeparam name="TPixel1">The pixel format of the first image.</typeparam>
/// <typeparam name="TPixel2">The pixel format of the second image.</typeparam>
/// <typeparam name="TPixel3">The pixel format of the third image.</typeparam>
/// <param name="pixelAccessor1">Row access to the first image's pixels.</param>
/// <param name="pixelAccessor2">Row access to the second image's pixels.</param>
/// <param name="pixelAccessor3">Row access to the third image's pixels.</param>
public delegate void PixelAccessorAction<TPixel1, TPixel2, TPixel3>(
    PixelAccessor<TPixel1> pixelAccessor1,
    PixelAccessor<TPixel2> pixelAccessor2,
    PixelAccessor<TPixel3> pixelAccessor3)
    where TPixel1 : unmanaged, IPixel<TPixel1>
    where TPixel2 : unmanaged, IPixel<TPixel2>
    where TPixel3 : unmanaged, IPixel<TPixel3>;

/// <summary>Provides span-based access to the rows of a pixel buffer.</summary>
/// <typeparam name="TPixel">The pixel format.</typeparam>
/// <remarks>
/// The accessor is only valid for the duration of the callback it was handed to: the buffer it
/// points at may be replaced by a later processing step or released with the owning image.
/// </remarks>
public readonly ref struct PixelAccessor<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly ImageFrame<TPixel> frame;

    internal PixelAccessor(ImageFrame<TPixel> frame) => this.frame = frame;

    /// <summary>The width of the buffer in pixels.</summary>
    public int Width => this.frame.Width;

    /// <summary>The height of the buffer in pixels.</summary>
    public int Height => this.frame.Height;

    /// <summary>Gets a span covering a single row of pixels.</summary>
    /// <param name="rowIndex">The zero-based row index.</param>
    /// <returns>The row's pixels, <see cref="Width"/> long.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row index is outside the buffer.</exception>
    public Span<TPixel> GetRowSpan(int rowIndex) => this.frame.GetRowSpan(rowIndex);
}
