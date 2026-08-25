namespace EasyImageSharp.Processing;

/// <summary>Shape of a built-in <see cref="StructuringElement"/>.</summary>
public enum StructuringElementShape
{
    /// <summary>A filled rectangle (box).</summary>
    Rectangle,

    /// <summary>A filled ellipse (a disk when square).</summary>
    Ellipse,

    /// <summary>A plus-shaped cross one pixel wide.</summary>
    Cross,

    /// <summary>An arbitrary user-supplied mask.</summary>
    Custom,
}

/// <summary>
/// The neighbourhood used by the morphological operators (<c>Erode</c>, <c>Dilate</c>, <c>Open</c>, <c>Close</c>,
/// <c>TopHat</c>, <c>BlackHat</c>). Elements are anchored at their centre; built-in shapes always have odd
/// dimensions so the anchor is a whole pixel.
/// </summary>
public sealed class StructuringElement
{
    private readonly bool[] mask;

    private StructuringElement(StructuringElementShape shape, int width, int height, bool[] mask)
    {
        this.Shape = shape;
        this.Width = width;
        this.Height = height;
        this.mask = mask;
    }

    /// <summary>The shape family of the element.</summary>
    public StructuringElementShape Shape { get; }

    /// <summary>The element width in pixels (always odd for built-in shapes).</summary>
    public int Width { get; }

    /// <summary>The element height in pixels (always odd for built-in shapes).</summary>
    public int Height { get; }

    /// <summary>The horizontal anchor (the centre column).</summary>
    public int AnchorX => this.Width / 2;

    /// <summary>The vertical anchor (the centre row).</summary>
    public int AnchorY => this.Height / 2;

    /// <summary>Whether the pixel at (<paramref name="x"/>, <paramref name="y"/>) of the mask belongs to the element.</summary>
    public bool this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)this.Width || (uint)y >= (uint)this.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {this.Width}x{this.Height} element.");
            }

            return this.mask[(y * this.Width) + x];
        }
    }

    /// <summary>A (2r+1) x (2r+1) square.</summary>
    public static StructuringElement Square(int radius) => Rectangle((2 * CheckRadius(radius)) + 1, (2 * radius) + 1);

    /// <summary>A filled <paramref name="width"/> x <paramref name="height"/> box; even sizes are rounded up to odd.</summary>
    public static StructuringElement Rectangle(int width, int height)
    {
        (width, height) = CheckSize(width, height);
        var mask = new bool[width * height];
        mask.AsSpan().Fill(true);
        return new StructuringElement(StructuringElementShape.Rectangle, width, height, mask);
    }

    /// <summary>A disk of the given radius (a (2r+1) x (2r+1) ellipse).</summary>
    public static StructuringElement Disk(int radius) => Ellipse((2 * CheckRadius(radius)) + 1, (2 * radius) + 1);

    /// <summary>
    /// A filled ellipse inscribed in a <paramref name="width"/> x <paramref name="height"/> box (even sizes are rounded
    /// up to odd). A pixel belongs to the element when its centre satisfies <c>(dx/rx)^2 + (dy/ry)^2 &lt;= 1</c>.
    /// </summary>
    public static StructuringElement Ellipse(int width, int height)
    {
        (width, height) = CheckSize(width, height);
        int rx = width / 2;
        int ry = height / 2;
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            double dy = ry == 0 ? 0 : (double)(y - ry) / ry;
            for (int x = 0; x < width; x++)
            {
                double dx = rx == 0 ? 0 : (double)(x - rx) / rx;
                mask[(y * width) + x] = (dx * dx) + (dy * dy) <= 1.0 + 1e-9;
            }
        }

        return new StructuringElement(StructuringElementShape.Ellipse, width, height, mask);
    }

    /// <summary>A plus-shaped cross with arms of the given radius, one pixel thick.</summary>
    public static StructuringElement Cross(int radius)
    {
        int size = (2 * CheckRadius(radius)) + 1;
        var mask = new bool[size * size];
        for (int i = 0; i < size; i++)
        {
            mask[(radius * size) + i] = true;
            mask[(i * size) + radius] = true;
        }

        return new StructuringElement(StructuringElementShape.Cross, size, size, mask);
    }

    /// <summary>An arbitrary element from a row-major mask; both dimensions must be odd and at least one pixel must be set.</summary>
    public static StructuringElement FromMask(bool[,] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int height = mask.GetLength(0);
        int width = mask.GetLength(1);
        if (width < 1 || height < 1 || (width & 1) == 0 || (height & 1) == 0)
        {
            throw new ArgumentException("A custom structuring element must have odd, positive dimensions.", nameof(mask));
        }

        var flat = new bool[width * height];
        bool any = false;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                flat[(y * width) + x] = mask[y, x];
                any |= mask[y, x];
            }
        }

        if (!any)
        {
            throw new ArgumentException("A structuring element must contain at least one set pixel.", nameof(mask));
        }

        return new StructuringElement(StructuringElementShape.Custom, width, height, flat);
    }

    /// <summary>
    /// The row runs of the element: for each row the first and last set column, or (-1, -1) for an empty row.
    /// Returns <see langword="false"/> when some row is not a single contiguous run.
    /// </summary>
    internal bool TryGetRowRuns(out int[] starts, out int[] ends)
    {
        starts = new int[this.Height];
        ends = new int[this.Height];
        for (int y = 0; y < this.Height; y++)
        {
            int first = -1;
            int last = -1;
            for (int x = 0; x < this.Width; x++)
            {
                if (this.mask[(y * this.Width) + x])
                {
                    if (first < 0)
                    {
                        first = x;
                    }
                    else if (last != x - 1)
                    {
                        return false;
                    }

                    last = x;
                }
            }

            starts[y] = first;
            ends[y] = last;
        }

        return true;
    }

    internal ReadOnlySpan<bool> Mask => this.mask;

    private static int CheckRadius(int radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be non-negative.");
        }

        return radius;
    }

    private static (int Width, int Height) CheckSize(int width, int height)
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        return (width | 1, height | 1);
    }
}
