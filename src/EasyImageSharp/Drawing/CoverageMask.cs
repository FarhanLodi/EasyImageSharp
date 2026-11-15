namespace EasyImageSharp.Drawing;

/// <summary>
/// The 0-255 coverage of a clip path over the part of a frame it touches. Pixels outside <see cref="Bounds"/>
/// have coverage 0.
/// </summary>
internal sealed class CoverageMask
{
    private readonly byte[] data;

    private CoverageMask(Rectangle bounds, byte[] data, int frameWidth, int frameHeight)
    {
        this.Bounds = bounds;
        this.data = data;
        this.FrameWidth = frameWidth;
        this.FrameHeight = frameHeight;
    }

    /// <summary>The region of the frame the mask covers; empty when the clip path covers no pixel.</summary>
    public Rectangle Bounds { get; }

    /// <summary>The width of the frame the mask was made for.</summary>
    public int FrameWidth { get; }

    /// <summary>The height of the frame the mask was made for.</summary>
    public int FrameHeight { get; }

    public bool IsEmpty => this.Bounds.Width <= 0 || this.Bounds.Height <= 0;

    /// <summary>Rasterises <paramref name="path"/> as a clip for a <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public static CoverageMask Create(VectorPath path, FillRule fillRule, bool antialias, double tolerance, int width, int height)
    {
        var rasterizer = new PolygonRasterizer();
        DrawOps.AddPathFill(rasterizer, path, tolerance, width, height);

        // The path's bounds, grown to whole pixels (clamped in floating point, so huge coordinates cannot overflow).
        RectangleF b = path.Bounds;
        int left = (int)Math.Clamp(Math.Floor((double)b.X) - 1, 0, width);
        int top = (int)Math.Clamp(Math.Floor((double)b.Y) - 1, 0, height);
        int right = (int)Math.Clamp(Math.Ceiling((double)b.X + b.Width) + 1, 0, width);
        int bottom = (int)Math.Clamp(Math.Ceiling((double)b.Y + b.Height) + 1, 0, height);
        var bounds = new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        if (rasterizer.IsEmpty || bounds.Width == 0 || bounds.Height == 0)
        {
            return new CoverageMask(default, [], width, height);
        }

        var mask = new CoverageMask(bounds, new byte[(long)bounds.Width * bounds.Height], width, height);
        var writer = new Writer(mask);
        rasterizer.Rasterize(bounds, antialias, fillRule, ref writer);
        return mask;
    }

    /// <summary>The product of two masks of the same frame: a pixel is covered as much as it is by both.</summary>
    public CoverageMask Intersect(CoverageMask other)
    {
        Rectangle a = this.Bounds, b = other.Bounds;
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
        {
            return new CoverageMask(default, [], this.FrameWidth, this.FrameHeight);
        }

        var bounds = new Rectangle(left, top, right - left, bottom - top);
        var result = new CoverageMask(bounds, new byte[(long)bounds.Width * bounds.Height], this.FrameWidth, this.FrameHeight);
        for (int y = top; y < bottom; y++)
        {
            ReadOnlySpan<byte> rowA = this.GetRow(y).Slice(left - a.X, bounds.Width);
            ReadOnlySpan<byte> rowB = other.GetRow(y).Slice(left - b.X, bounds.Width);
            Span<byte> target = result.data.AsSpan((y - top) * bounds.Width, bounds.Width);
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = Multiply(rowA[i], rowB[i]);
            }
        }

        return result;
    }

    /// <summary>The product of two 0-255 coverages, rounded to nearest; 255 is the identity.</summary>
    public static byte Multiply(byte a, byte b) => (byte)(((a * b) + 127) / 255);

    /// <summary>The mask values of row <paramref name="y"/>, which must lie inside <see cref="Bounds"/>, starting at <see cref="Rectangle.X"/>.</summary>
    public ReadOnlySpan<byte> GetRow(int y) => this.data.AsSpan((y - this.Bounds.Y) * this.Bounds.Width, this.Bounds.Width);

    /// <summary>The coverage of pixel (<paramref name="x"/>, <paramref name="y"/>), 0 outside the bounds.</summary>
    public byte this[int x, int y]
    {
        get
        {
            Rectangle r = this.Bounds;
            return (uint)(x - r.X) < (uint)r.Width && (uint)(y - r.Y) < (uint)r.Height
                ? this.data[((y - r.Y) * r.Width) + (x - r.X)]
                : (byte)0;
        }
    }

    /// <summary>Copies rasterised rows into the mask.</summary>
    private readonly struct Writer : ICoverageSink
    {
        private readonly CoverageMask mask;

        public Writer(CoverageMask mask) => this.mask = mask;

        public void Blend(int y, int x, ReadOnlySpan<byte> coverage)
        {
            Rectangle r = this.mask.Bounds;
            coverage.CopyTo(this.mask.data.AsSpan(((y - r.Y) * r.Width) + (x - r.X), coverage.Length));
        }
    }
}

/// <summary>
/// Forwards coverage to another sink after multiplying it by a <see cref="CoverageMask"/>; with no mask it
/// forwards the coverage unchanged.
/// </summary>
internal struct ClippedSink<TSink> : ICoverageSink
    where TSink : struct, ICoverageSink
{
    private readonly CoverageMask? mask;
    private readonly byte[]? scratch;
    private TSink inner;

    public ClippedSink(TSink inner, CoverageMask? mask)
    {
        this.inner = inner;
        this.mask = mask;
        this.scratch = mask is null ? null : new byte[Math.Max(1, mask.Bounds.Width)];
    }

    public void Blend(int y, int x, ReadOnlySpan<byte> coverage)
    {
        if (this.mask is not CoverageMask m)
        {
            this.inner.Blend(y, x, coverage);
            return;
        }

        Rectangle r = m.Bounds;
        if ((uint)(y - r.Y) >= (uint)r.Height)
        {
            return;
        }

        int start = Math.Max(x, r.X);
        int end = Math.Min(x + coverage.Length, r.X + r.Width);
        if (start >= end)
        {
            return;
        }

        ReadOnlySpan<byte> clip = m.GetRow(y).Slice(start - r.X, end - start);
        ReadOnlySpan<byte> source = coverage.Slice(start - x, end - start);
        Span<byte> combined = this.scratch.AsSpan(0, end - start);
        bool any = false;
        for (int i = 0; i < combined.Length; i++)
        {
            byte value = CoverageMask.Multiply(source[i], clip[i]);
            combined[i] = value;
            any |= value != 0;
        }

        if (any)
        {
            this.inner.Blend(y, start, combined);
        }
    }
}
