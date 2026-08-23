namespace EasyImageSharp;

/// <summary>An integer (x, y) coordinate pair.</summary>
public readonly struct Point : IEquatable<Point>
{
    /// <summary>The point at the origin (0, 0).</summary>
    public static readonly Point Empty = default;

    /// <summary>Initializes a new point.</summary>
    public Point(int x, int y)
    {
        this.X = x;
        this.Y = y;
    }

    /// <summary>The horizontal coordinate.</summary>
    public int X { get; }

    /// <summary>The vertical coordinate.</summary>
    public int Y { get; }

    /// <summary>Whether both coordinates are zero.</summary>
    public bool IsEmpty => this.X == 0 && this.Y == 0;

    /// <summary>Converts a <see cref="PointF"/> by rounding each coordinate to the nearest integer (midpoints round away from zero).</summary>
    public static Point Round(PointF point) => new(RoundToInt(point.X), RoundToInt(point.Y));

    /// <summary>Converts a <see cref="PointF"/> by truncating each coordinate toward zero.</summary>
    public static Point Truncate(PointF point) => new((int)point.X, (int)point.Y);

    /// <summary>Converts a <see cref="PointF"/> by rounding each coordinate up to the next integer.</summary>
    public static Point Ceiling(PointF point) => new((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y));

    /// <summary>Returns a point moved by the given amounts.</summary>
    public Point Offset(int dx, int dy) => new(this.X + dx, this.Y + dy);

    /// <summary>Splits the point into its coordinates.</summary>
    public void Deconstruct(out int x, out int y)
    {
        x = this.X;
        y = this.Y;
    }

    /// <inheritdoc/>
    public bool Equals(Point other) => this.X == other.X && this.Y == other.Y;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Point p && this.Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.X, this.Y);

    /// <inheritdoc/>
    public override string ToString() => $"Point [ X={this.X}, Y={this.Y} ]";

    /// <summary>Whether two points are equal.</summary>
    public static bool operator ==(Point left, Point right) => left.Equals(right);

    /// <summary>Whether two points differ.</summary>
    public static bool operator !=(Point left, Point right) => !left.Equals(right);

    /// <summary>Translates a point by a size.</summary>
    public static Point operator +(Point point, Size size) => new(point.X + size.Width, point.Y + size.Height);

    /// <summary>Translates a point by the negative of a size.</summary>
    public static Point operator -(Point point, Size size) => new(point.X - size.Width, point.Y - size.Height);

    internal static int RoundToInt(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>An integer width/height pair.</summary>
public readonly struct Size : IEquatable<Size>
{
    /// <summary>The size with zero width and height.</summary>
    public static readonly Size Empty = default;

    /// <summary>Initializes a square size.</summary>
    public Size(int value)
        : this(value, value)
    {
    }

    /// <summary>Initializes a new size.</summary>
    public Size(int width, int height)
    {
        this.Width = width;
        this.Height = height;
    }

    /// <summary>The horizontal extent.</summary>
    public int Width { get; }

    /// <summary>The vertical extent.</summary>
    public int Height { get; }

    /// <summary>Whether both dimensions are zero.</summary>
    public bool IsEmpty => this.Width == 0 && this.Height == 0;

    /// <summary>Converts a <see cref="SizeF"/> by rounding each dimension to the nearest integer (midpoints round away from zero).</summary>
    public static Size Round(SizeF size) => new(Point.RoundToInt(size.Width), Point.RoundToInt(size.Height));

    /// <summary>Converts a <see cref="SizeF"/> by truncating each dimension toward zero.</summary>
    public static Size Truncate(SizeF size) => new((int)size.Width, (int)size.Height);

    /// <summary>Converts a <see cref="SizeF"/> by rounding each dimension up to the next integer.</summary>
    public static Size Ceiling(SizeF size) => new((int)MathF.Ceiling(size.Width), (int)MathF.Ceiling(size.Height));

    /// <summary>Splits the size into its dimensions.</summary>
    public void Deconstruct(out int width, out int height)
    {
        width = this.Width;
        height = this.Height;
    }

    /// <inheritdoc/>
    public bool Equals(Size other) => this.Width == other.Width && this.Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Size s && this.Equals(s);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Width, this.Height);

    /// <inheritdoc/>
    public override string ToString() => $"Size [ Width={this.Width}, Height={this.Height} ]";

    /// <summary>Whether two sizes are equal.</summary>
    public static bool operator ==(Size left, Size right) => left.Equals(right);

    /// <summary>Whether two sizes differ.</summary>
    public static bool operator !=(Size left, Size right) => !left.Equals(right);

    /// <summary>Adds two sizes component-wise.</summary>
    public static Size operator +(Size left, Size right) => new(left.Width + right.Width, left.Height + right.Height);

    /// <summary>Subtracts two sizes component-wise.</summary>
    public static Size operator -(Size left, Size right) => new(left.Width - right.Width, left.Height - right.Height);

    /// <summary>Scales both dimensions by an integer factor.</summary>
    public static Size operator *(Size size, int factor) => new(size.Width * factor, size.Height * factor);

    /// <summary>Scales both dimensions by an integer factor.</summary>
    public static Size operator *(int factor, Size size) => size * factor;
}

/// <summary>An integer rectangle described by its top-left corner and size.</summary>
public readonly struct Rectangle : IEquatable<Rectangle>
{
    /// <summary>The rectangle at the origin with zero width and height.</summary>
    public static readonly Rectangle Empty = default;

    /// <summary>Initializes a new rectangle from its top-left corner and size.</summary>
    public Rectangle(int x, int y, int width, int height)
    {
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
    }

    /// <summary>Initializes a new rectangle from its top-left corner and size.</summary>
    public Rectangle(Point location, Size size)
        : this(location.X, location.Y, size.Width, size.Height)
    {
    }

    /// <summary>The horizontal coordinate of the left edge.</summary>
    public int X { get; }

    /// <summary>The vertical coordinate of the top edge.</summary>
    public int Y { get; }

    /// <summary>The horizontal extent.</summary>
    public int Width { get; }

    /// <summary>The vertical extent.</summary>
    public int Height { get; }

    /// <summary>The left edge (same as <see cref="X"/>).</summary>
    public int Left => this.X;

    /// <summary>The top edge (same as <see cref="Y"/>).</summary>
    public int Top => this.Y;

    /// <summary>The exclusive right edge (<c>X + Width</c>).</summary>
    public int Right => this.X + this.Width;

    /// <summary>The exclusive bottom edge (<c>Y + Height</c>).</summary>
    public int Bottom => this.Y + this.Height;

    /// <summary>The top-left corner.</summary>
    public Point Location => new(this.X, this.Y);

    /// <summary>The width and height.</summary>
    public Size Size => new(this.Width, this.Height);

    /// <summary>Whether both dimensions are zero.</summary>
    public bool IsEmpty => this.Width == 0 && this.Height == 0;

    /// <summary>Creates a rectangle from its edges; <paramref name="right"/> and <paramref name="bottom"/> are exclusive.</summary>
    public static Rectangle FromLTRB(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>Converts a <see cref="RectangleF"/> by rounding each component to the nearest integer (midpoints round away from zero).</summary>
    public static Rectangle Round(RectangleF rectangle) => new(
        Point.RoundToInt(rectangle.X),
        Point.RoundToInt(rectangle.Y),
        Point.RoundToInt(rectangle.Width),
        Point.RoundToInt(rectangle.Height));

    /// <summary>Converts a <see cref="RectangleF"/> by truncating each component toward zero.</summary>
    public static Rectangle Truncate(RectangleF rectangle)
        => new((int)rectangle.X, (int)rectangle.Y, (int)rectangle.Width, (int)rectangle.Height);

    /// <summary>Converts a <see cref="RectangleF"/> by rounding each component up to the next integer.</summary>
    public static Rectangle Ceiling(RectangleF rectangle) => new(
        (int)MathF.Ceiling(rectangle.X),
        (int)MathF.Ceiling(rectangle.Y),
        (int)MathF.Ceiling(rectangle.Width),
        (int)MathF.Ceiling(rectangle.Height));

    /// <summary>Whether the point (<paramref name="x"/>, <paramref name="y"/>) lies inside this rectangle.</summary>
    public bool Contains(int x, int y) => x >= this.X && x < this.Right && y >= this.Y && y < this.Bottom;

    /// <summary>Whether <paramref name="point"/> lies inside this rectangle.</summary>
    public bool Contains(Point point) => this.Contains(point.X, point.Y);

    /// <summary>Whether <paramref name="other"/> lies entirely inside this rectangle.</summary>
    public bool Contains(Rectangle other)
        => other.X >= this.X && other.Right <= this.Right && other.Y >= this.Y && other.Bottom <= this.Bottom;

    /// <summary>Whether this rectangle and <paramref name="other"/> share any area.</summary>
    public bool IntersectsWith(Rectangle other)
        => other.X < this.Right && this.X < other.Right && other.Y < this.Bottom && this.Y < other.Bottom;

    /// <summary>Returns a rectangle grown by <paramref name="width"/> on the left and right and <paramref name="height"/> on the top and bottom.</summary>
    public Rectangle Inflate(int width, int height)
        => new(this.X - width, this.Y - height, this.Width + (2 * width), this.Height + (2 * height));

    /// <summary>Returns a rectangle moved by the given amounts.</summary>
    public Rectangle Offset(int dx, int dy) => new(this.X + dx, this.Y + dy, this.Width, this.Height);

    /// <summary>Returns the intersection of two rectangles, or an empty rectangle when they do not overlap.</summary>
    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        int x1 = Math.Max(a.X, b.X);
        int x2 = Math.Min(a.Right, b.Right);
        int y1 = Math.Max(a.Y, b.Y);
        int y2 = Math.Min(a.Bottom, b.Bottom);
        return x2 > x1 && y2 > y1 ? new Rectangle(x1, y1, x2 - x1, y2 - y1) : Empty;
    }

    /// <summary>
    /// Returns the smallest rectangle containing both rectangles. An empty rectangle
    /// (see <see cref="IsEmpty"/>) contributes nothing, so <c>Union(Empty, r)</c> is <c>r</c>.
    /// </summary>
    public static Rectangle Union(Rectangle a, Rectangle b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        int x1 = Math.Min(a.X, b.X);
        int x2 = Math.Max(a.Right, b.Right);
        int y1 = Math.Min(a.Y, b.Y);
        int y2 = Math.Max(a.Bottom, b.Bottom);
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Splits the rectangle into its components.</summary>
    public void Deconstruct(out int x, out int y, out int width, out int height)
    {
        x = this.X;
        y = this.Y;
        width = this.Width;
        height = this.Height;
    }

    /// <inheritdoc/>
    public bool Equals(Rectangle other)
        => this.X == other.X && this.Y == other.Y && this.Width == other.Width && this.Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Rectangle r && this.Equals(r);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.X, this.Y, this.Width, this.Height);

    /// <inheritdoc/>
    public override string ToString()
        => $"Rectangle [ X={this.X}, Y={this.Y}, Width={this.Width}, Height={this.Height} ]";

    /// <summary>Whether two rectangles are equal.</summary>
    public static bool operator ==(Rectangle left, Rectangle right) => left.Equals(right);

    /// <summary>Whether two rectangles differ.</summary>
    public static bool operator !=(Rectangle left, Rectangle right) => !left.Equals(right);
}

/// <summary>A single-precision (x, y) coordinate pair.</summary>
public readonly struct PointF : IEquatable<PointF>
{
    /// <summary>The point at the origin (0, 0).</summary>
    public static readonly PointF Empty = default;

    /// <summary>Initializes a new point.</summary>
    public PointF(float x, float y)
    {
        this.X = x;
        this.Y = y;
    }

    /// <summary>The horizontal coordinate.</summary>
    public float X { get; }

    /// <summary>The vertical coordinate.</summary>
    public float Y { get; }

    /// <summary>Whether both coordinates are zero.</summary>
    public bool IsEmpty => this.X == 0f && this.Y == 0f;

    /// <summary>Returns a point moved by the given amounts.</summary>
    public PointF Offset(float dx, float dy) => new(this.X + dx, this.Y + dy);

    /// <summary>Splits the point into its coordinates.</summary>
    public void Deconstruct(out float x, out float y)
    {
        x = this.X;
        y = this.Y;
    }

    /// <inheritdoc/>
    public bool Equals(PointF other) => this.X == other.X && this.Y == other.Y;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PointF p && this.Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.X, this.Y);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"PointF [ X={this.X}, Y={this.Y} ]");

    /// <summary>Whether two points are equal.</summary>
    public static bool operator ==(PointF left, PointF right) => left.Equals(right);

    /// <summary>Whether two points differ.</summary>
    public static bool operator !=(PointF left, PointF right) => !left.Equals(right);

    /// <summary>Translates a point by a size.</summary>
    public static PointF operator +(PointF point, SizeF size) => new(point.X + size.Width, point.Y + size.Height);

    /// <summary>Translates a point by the negative of a size.</summary>
    public static PointF operator -(PointF point, SizeF size) => new(point.X - size.Width, point.Y - size.Height);

    /// <summary>Widens an integer point.</summary>
    public static implicit operator PointF(Point point) => new(point.X, point.Y);
}

/// <summary>A single-precision width/height pair.</summary>
public readonly struct SizeF : IEquatable<SizeF>
{
    /// <summary>The size with zero width and height.</summary>
    public static readonly SizeF Empty = default;

    /// <summary>Initializes a square size.</summary>
    public SizeF(float value)
        : this(value, value)
    {
    }

    /// <summary>Initializes a new size.</summary>
    public SizeF(float width, float height)
    {
        this.Width = width;
        this.Height = height;
    }

    /// <summary>The horizontal extent.</summary>
    public float Width { get; }

    /// <summary>The vertical extent.</summary>
    public float Height { get; }

    /// <summary>Whether both dimensions are zero.</summary>
    public bool IsEmpty => this.Width == 0f && this.Height == 0f;

    /// <summary>Splits the size into its dimensions.</summary>
    public void Deconstruct(out float width, out float height)
    {
        width = this.Width;
        height = this.Height;
    }

    /// <inheritdoc/>
    public bool Equals(SizeF other) => this.Width == other.Width && this.Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SizeF s && this.Equals(s);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Width, this.Height);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"SizeF [ Width={this.Width}, Height={this.Height} ]");

    /// <summary>Whether two sizes are equal.</summary>
    public static bool operator ==(SizeF left, SizeF right) => left.Equals(right);

    /// <summary>Whether two sizes differ.</summary>
    public static bool operator !=(SizeF left, SizeF right) => !left.Equals(right);

    /// <summary>Adds two sizes component-wise.</summary>
    public static SizeF operator +(SizeF left, SizeF right) => new(left.Width + right.Width, left.Height + right.Height);

    /// <summary>Subtracts two sizes component-wise.</summary>
    public static SizeF operator -(SizeF left, SizeF right) => new(left.Width - right.Width, left.Height - right.Height);

    /// <summary>Scales both dimensions by a factor.</summary>
    public static SizeF operator *(SizeF size, float factor) => new(size.Width * factor, size.Height * factor);

    /// <summary>Scales both dimensions by a factor.</summary>
    public static SizeF operator *(float factor, SizeF size) => size * factor;

    /// <summary>Widens an integer size.</summary>
    public static implicit operator SizeF(Size size) => new(size.Width, size.Height);
}

/// <summary>A single-precision rectangle described by its top-left corner and size.</summary>
public readonly struct RectangleF : IEquatable<RectangleF>
{
    /// <summary>The rectangle at the origin with zero width and height.</summary>
    public static readonly RectangleF Empty = default;

    /// <summary>Initializes a new rectangle from its top-left corner and size.</summary>
    public RectangleF(float x, float y, float width, float height)
    {
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
    }

    /// <summary>Initializes a new rectangle from its top-left corner and size.</summary>
    public RectangleF(PointF location, SizeF size)
        : this(location.X, location.Y, size.Width, size.Height)
    {
    }

    /// <summary>The horizontal coordinate of the left edge.</summary>
    public float X { get; }

    /// <summary>The vertical coordinate of the top edge.</summary>
    public float Y { get; }

    /// <summary>The horizontal extent.</summary>
    public float Width { get; }

    /// <summary>The vertical extent.</summary>
    public float Height { get; }

    /// <summary>The left edge (same as <see cref="X"/>).</summary>
    public float Left => this.X;

    /// <summary>The top edge (same as <see cref="Y"/>).</summary>
    public float Top => this.Y;

    /// <summary>The exclusive right edge (<c>X + Width</c>).</summary>
    public float Right => this.X + this.Width;

    /// <summary>The exclusive bottom edge (<c>Y + Height</c>).</summary>
    public float Bottom => this.Y + this.Height;

    /// <summary>The top-left corner.</summary>
    public PointF Location => new(this.X, this.Y);

    /// <summary>The width and height.</summary>
    public SizeF Size => new(this.Width, this.Height);

    /// <summary>Whether both dimensions are zero.</summary>
    public bool IsEmpty => this.Width == 0f && this.Height == 0f;

    /// <summary>Creates a rectangle from its edges; <paramref name="right"/> and <paramref name="bottom"/> are exclusive.</summary>
    public static RectangleF FromLTRB(float left, float top, float right, float bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>Whether the point (<paramref name="x"/>, <paramref name="y"/>) lies inside this rectangle.</summary>
    public bool Contains(float x, float y) => x >= this.X && x < this.Right && y >= this.Y && y < this.Bottom;

    /// <summary>Whether <paramref name="point"/> lies inside this rectangle.</summary>
    public bool Contains(PointF point) => this.Contains(point.X, point.Y);

    /// <summary>Whether <paramref name="other"/> lies entirely inside this rectangle.</summary>
    public bool Contains(RectangleF other)
        => other.X >= this.X && other.Right <= this.Right && other.Y >= this.Y && other.Bottom <= this.Bottom;

    /// <summary>Whether this rectangle and <paramref name="other"/> share any area.</summary>
    public bool IntersectsWith(RectangleF other)
        => other.X < this.Right && this.X < other.Right && other.Y < this.Bottom && this.Y < other.Bottom;

    /// <summary>Returns a rectangle grown by <paramref name="width"/> on the left and right and <paramref name="height"/> on the top and bottom.</summary>
    public RectangleF Inflate(float width, float height)
        => new(this.X - width, this.Y - height, this.Width + (2f * width), this.Height + (2f * height));

    /// <summary>Returns a rectangle moved by the given amounts.</summary>
    public RectangleF Offset(float dx, float dy) => new(this.X + dx, this.Y + dy, this.Width, this.Height);

    /// <summary>Returns the intersection of two rectangles, or an empty rectangle when they do not overlap.</summary>
    public static RectangleF Intersect(RectangleF a, RectangleF b)
    {
        float x1 = MathF.Max(a.X, b.X);
        float x2 = MathF.Min(a.Right, b.Right);
        float y1 = MathF.Max(a.Y, b.Y);
        float y2 = MathF.Min(a.Bottom, b.Bottom);
        return x2 > x1 && y2 > y1 ? new RectangleF(x1, y1, x2 - x1, y2 - y1) : Empty;
    }

    /// <summary>
    /// Returns the smallest rectangle containing both rectangles. An empty rectangle
    /// (see <see cref="IsEmpty"/>) contributes nothing, so <c>Union(Empty, r)</c> is <c>r</c>.
    /// </summary>
    public static RectangleF Union(RectangleF a, RectangleF b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        float x1 = MathF.Min(a.X, b.X);
        float x2 = MathF.Max(a.Right, b.Right);
        float y1 = MathF.Min(a.Y, b.Y);
        float y2 = MathF.Max(a.Bottom, b.Bottom);
        return new RectangleF(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Splits the rectangle into its components.</summary>
    public void Deconstruct(out float x, out float y, out float width, out float height)
    {
        x = this.X;
        y = this.Y;
        width = this.Width;
        height = this.Height;
    }

    /// <inheritdoc/>
    public bool Equals(RectangleF other)
        => this.X == other.X && this.Y == other.Y && this.Width == other.Width && this.Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RectangleF r && this.Equals(r);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.X, this.Y, this.Width, this.Height);

    /// <inheritdoc/>
    public override string ToString()
        => FormattableString.Invariant($"RectangleF [ X={this.X}, Y={this.Y}, Width={this.Width}, Height={this.Height} ]");

    /// <summary>Whether two rectangles are equal.</summary>
    public static bool operator ==(RectangleF left, RectangleF right) => left.Equals(right);

    /// <summary>Whether two rectangles differ.</summary>
    public static bool operator !=(RectangleF left, RectangleF right) => !left.Equals(right);

    /// <summary>Widens an integer rectangle.</summary>
    public static implicit operator RectangleF(Rectangle rectangle)
        => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
