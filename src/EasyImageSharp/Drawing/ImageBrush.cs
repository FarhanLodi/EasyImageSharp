using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>
/// A brush that paints with the pixels of an image, as a pattern or texture. In brush space the image's top-left
/// corner is at the origin and one source pixel is one unit; use <see cref="Brush.Transform"/> to move, scale or
/// rotate it, and <see cref="WrapModeX"/> / <see cref="WrapModeY"/> to decide what lies outside it.
/// </summary>
/// <remarks>
/// The brush copies the source pixels (as 8-bit RGBA) when it is created, so it stays valid after the source
/// image is changed or disposed and can be shared freely. With an identity transform and pixel-aligned
/// geometry every pixel is copied exactly, whichever <see cref="Sampling"/> is chosen.
/// </remarks>
public sealed class ImageBrush : Brush
{
    private readonly Rgba32[] pixels;
    private readonly int width;
    private readonly int height;

    /// <summary>Initializes a brush from the whole root frame of <paramref name="image"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="image"/> has been disposed.</exception>
    public ImageBrush(Image image)
        : this(image, FullBounds(image))
    {
    }

    /// <summary>Initializes a brush from a region of the root frame of <paramref name="image"/>.</summary>
    /// <param name="image">The source image; its pixels are copied.</param>
    /// <param name="sourceRegion">The region to copy, which becomes the pattern tile; it must lie inside the image and not be empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceRegion"/> is empty or not inside the image.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="image"/> has been disposed.</exception>
    public ImageBrush(Image image, Rectangle sourceRegion)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (sourceRegion.Width <= 0 || sourceRegion.Height <= 0 || sourceRegion.X < 0 || sourceRegion.Y < 0
            || sourceRegion.X > image.Width - sourceRegion.Width || sourceRegion.Y > image.Height - sourceRegion.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRegion), sourceRegion, "The source region must be a non-empty rectangle inside the image.");
        }

        this.width = sourceRegion.Width;
        this.height = sourceRegion.Height;
        this.pixels = new Rgba32[(long)this.width * this.height <= int.MaxValue
            ? this.width * this.height
            : throw new ArgumentOutOfRangeException(nameof(sourceRegion), sourceRegion, "The source region is too large.")];
        if (image is Image<Rgba32> rgba)
        {
            ImageFrame<Rgba32> frame = rgba.Frames.RootFrame;
            for (int y = 0; y < this.height; y++)
            {
                frame.GetRowSpan(sourceRegion.Y + y).Slice(sourceRegion.X, this.width).CopyTo(this.pixels.AsSpan(y * this.width, this.width));
            }

            return;
        }

        for (int y = 0; y < this.height; y++)
        {
            for (int x = 0; x < this.width; x++)
            {
                this.pixels[(y * this.width) + x] = image.GetPixelRgba32(sourceRegion.X + x, sourceRegion.Y + y);
            }
        }
    }

    /// <summary>The size of the pattern tile in source pixels (brush-space units).</summary>
    public Size Size => new(this.width, this.height);

    /// <summary>What lies left and right of the image; defaults to <see cref="WrapMode.Repeat"/> (tiling).</summary>
    public WrapMode WrapModeX { get; init; } = WrapMode.Repeat;

    /// <summary>What lies above and below the image; defaults to <see cref="WrapMode.Repeat"/> (tiling).</summary>
    public WrapMode WrapModeY { get; init; } = WrapMode.Repeat;

    /// <summary>How the image is read between pixel centres; defaults to <see cref="ImageBrushSampling.Bilinear"/>.</summary>
    public ImageBrushSampling Sampling { get; init; }

    internal override BrushShader? CreateShader()
    {
        InverseTransform? inverse = this.GetInverseTransform();
        return inverse is null ? null : new ImageShader(this, inverse.Value);
    }

    private static Rectangle FullBounds(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new Rectangle(0, 0, image.Width, image.Height);
    }

    /// <summary>Maps a pixel index onto the tile, or returns -1 when it falls in a transparent area.</summary>
    private static int WrapIndex(int index, int size, WrapMode mode)
    {
        switch (mode)
        {
            case WrapMode.Clamp:
                return Math.Clamp(index, 0, size - 1);
            case WrapMode.Repeat:
                {
                    int m = index % size;
                    return m < 0 ? m + size : m;
                }

            case WrapMode.Reflect:
                {
                    int period = 2 * size;
                    int m = index % period;
                    if (m < 0)
                    {
                        m += period;
                    }

                    return m < size ? m : period - 1 - m;
                }

            default:
                return (uint)index < (uint)size ? index : -1;
        }
    }

    /// <summary>Floors a brush-space coordinate to a pixel index without overflowing.</summary>
    private static int FloorIndex(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return (int)Math.Floor(Math.Clamp(value, -1e9, 1e9));
    }

    private sealed class ImageShader : BrushShader
    {
        private readonly ImageBrush brush;
        private readonly InverseTransform inverse;

        public ImageShader(ImageBrush brush, InverseTransform inverse)
        {
            this.brush = brush;
            this.inverse = inverse;
        }

        public override void Shade(int y, int x, Span<Rgba32> destination)
        {
            (double bx, double by) = this.inverse.Map(x + 0.5, y + 0.5);
            (double sx, double sy) = this.inverse.StepX;
            bool nearest = this.brush.Sampling == ImageBrushSampling.NearestNeighbor;
            for (int i = 0; i < destination.Length; i++)
            {
                double u = bx + (i * sx);
                double v = by + (i * sy);
                destination[i] = nearest ? this.SampleNearest(u, v) : this.SampleBilinear(u, v);
            }
        }

        private Rgba32 Fetch(int ix, int iy)
        {
            ImageBrush b = this.brush;
            int px = WrapIndex(ix, b.width, b.WrapModeX);
            int py = WrapIndex(iy, b.height, b.WrapModeY);
            return px < 0 || py < 0 ? default : b.pixels[(py * b.width) + px];
        }

        private Rgba32 SampleNearest(double u, double v) => this.Fetch(FloorIndex(u), FloorIndex(v));

        private Rgba32 SampleBilinear(double u, double v)
        {
            double fu = u - 0.5;
            double fv = v - 0.5;
            int x0 = FloorIndex(fu);
            int y0 = FloorIndex(fv);
            double wx = Math.Clamp(fu - x0, 0, 1);
            double wy = Math.Clamp(fv - y0, 0, 1);
            if (wx == 0 && wy == 0)
            {
                return this.Fetch(x0, y0);
            }

            // Blend premultiplied so transparent texels do not darken their neighbours.
            double pr = 0, pg = 0, pb = 0, pa = 0;
            Accumulate(this.Fetch(x0, y0), (1 - wx) * (1 - wy));
            Accumulate(this.Fetch(x0 + 1, y0), wx * (1 - wy));
            Accumulate(this.Fetch(x0, y0 + 1), (1 - wx) * wy);
            Accumulate(this.Fetch(x0 + 1, y0 + 1), wx * wy);
            if (pa <= 0)
            {
                return default;
            }

            return new Rgba32(Round(pr / pa), Round(pg / pa), Round(pb / pa), Round(pa));

            void Accumulate(Rgba32 c, double weight)
            {
                double w = weight * c.A;
                pr += c.R * w;
                pg += c.G * w;
                pb += c.B * w;
                pa += w;
            }

            static byte Round(double value) => (byte)Math.Clamp((int)(value + 0.5), 0, 255);
        }
    }
}
