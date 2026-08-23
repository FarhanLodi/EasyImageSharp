using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>The result of quantizing a frame: a palette of at most 256 colours and one palette index per pixel.</summary>
/// <typeparam name="TPixel">The pixel format of the palette.</typeparam>
public sealed class IndexedImageFrame<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly byte[] indices;

    internal IndexedImageFrame(int width, int height, ReadOnlyMemory<TPixel> palette)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.Width = width;
        this.Height = height;
        this.Palette = palette;
        this.indices = new byte[checked(width * height)];
    }

    /// <summary>The width of the frame in pixels.</summary>
    public int Width { get; }

    /// <summary>The height of the frame in pixels.</summary>
    public int Height { get; }

    /// <summary>The palette; every index in the frame is smaller than its length.</summary>
    public ReadOnlyMemory<TPixel> Palette { get; }

    /// <summary>Gets the palette indices of one row.</summary>
    public ReadOnlySpan<byte> GetRowSpan(int rowIndex) => this.GetWritableRowSpan(rowIndex);

    /// <summary>The whole index buffer, row-major.</summary>
    internal Span<byte> IndexSpan => this.indices;

    /// <summary>The whole index buffer, row-major, as memory that can be captured by parallel work.</summary>
    internal Memory<byte> IndexMemory => this.indices;

    /// <summary>The backing index array (row-major, exactly Width * Height bytes).</summary>
    internal byte[] IndexArray => this.indices;

    internal Span<byte> GetWritableRowSpan(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)this.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, "Row index is outside the frame bounds.");
        }

        return this.indices.AsSpan(rowIndex * this.Width, this.Width);
    }
}
