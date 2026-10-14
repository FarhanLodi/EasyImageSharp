namespace EasyImageSharp.Tensors;

/// <summary>
/// Geometry of a resized tensor. Coordinates describe pixel edges, suitable for bounding boxes;
/// crop offsets can be negative. Mapping does not clamp points to the visible canvas.
/// </summary>
public readonly struct TensorResizeMapping
{
    internal TensorResizeMapping(Size sourceSize, Size outputSize, Rectangle contentBounds)
    {
        this.SourceSize = sourceSize;
        this.OutputSize = outputSize;
        this.ContentBounds = contentBounds;
    }

    /// <summary>The original root-frame dimensions.</summary>
    public Size SourceSize { get; }

    /// <summary>The actual tensor canvas dimensions after resize mode and aspect rounding.</summary>
    public Size OutputSize { get; }

    /// <summary>The scaled source rectangle on the output canvas, including negative crop offsets.</summary>
    public Rectangle ContentBounds { get; }

    /// <summary>The actual horizontal scale after integer dimension rounding.</summary>
    public double ScaleX => (double)this.ContentBounds.Width / this.SourceSize.Width;

    /// <summary>The actual vertical scale after integer dimension rounding.</summary>
    public double ScaleY => (double)this.ContentBounds.Height / this.SourceSize.Height;

    /// <summary>Maps a point in source pixel-edge coordinates into tensor canvas coordinates.</summary>
    public PointF ToTensor(PointF source) => new(
        (float)((source.X * this.ScaleX) + this.ContentBounds.X),
        (float)((source.Y * this.ScaleY) + this.ContentBounds.Y));

    /// <summary>Maps a tensor canvas point back into source pixel-edge coordinates.</summary>
    public PointF ToSource(PointF tensor) => new(
        (float)((tensor.X - this.ContentBounds.X) / this.ScaleX),
        (float)((tensor.Y - this.ContentBounds.Y) / this.ScaleY));
}
