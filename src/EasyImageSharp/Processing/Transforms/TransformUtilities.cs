using System.Numerics;

namespace EasyImageSharp.Processing;

/// <summary>
/// Matrix helpers shared by the transform builders and the warp engine. All matrices use the row-vector
/// convention of <see cref="System.Numerics"/>: a point <c>p</c> is transformed as <c>p * M</c>, translation
/// lives in the last row, and <c>A * B</c> applies <c>A</c> first. Projective (perspective) transforms are stored
/// in a <see cref="Matrix4x4"/> whose Z row and column are the identity: <c>(x, y, 0, 1) * M = (x', y', 0, w)</c>
/// and the transformed point is <c>(x' / w, y' / w)</c>, so <see cref="Matrix4x4.Invert"/> and the
/// <c>Matrix4x4.Create*</c> factories compose naturally with it.
/// </summary>
internal static class TransformUtilities
{
    /// <summary>
    /// Sizes derived from float bounding boxes are snapped by this much before rounding up, so an exact right
    /// angle or integer scale does not grow the canvas by a pixel because <c>cos(90°)</c> is not exactly zero.
    /// </summary>
    private const double SizeSnapTolerance = 1e-3;

    /// <summary>Corner weights below this are treated as points at infinity.</summary>
    private const float MinimumHomogeneousW = 1e-6f;

    public static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    public static Vector2 ToVector2(PointF point) => new(point.X, point.Y);

    public static Vector2 Center(Rectangle rectangle) => new(rectangle.X + (rectangle.Width / 2f), rectangle.Y + (rectangle.Height / 2f));

    /// <summary>Embeds a 2-D affine matrix in the projective 4x4 form described on this class.</summary>
    public static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix) => new(
        matrix.M11, matrix.M12, 0f, 0f,
        matrix.M21, matrix.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        matrix.M31, matrix.M32, 0f, 1f);

    /// <summary>Applies a projective matrix to a point, returning <see langword="false"/> when it maps to infinity or behind the viewer.</summary>
    public static bool TryTransform(Vector2 point, in Matrix4x4 matrix, out Vector2 result)
    {
        float x = (point.X * matrix.M11) + (point.Y * matrix.M21) + matrix.M41;
        float y = (point.X * matrix.M12) + (point.Y * matrix.M22) + matrix.M42;
        float w = (point.X * matrix.M14) + (point.Y * matrix.M24) + matrix.M44;
        if (!(w > MinimumHomogeneousW))
        {
            result = default;
            return false;
        }

        result = new Vector2(x / w, y / w);
        return true;
    }

    /// <summary>The axis-aligned bounding box of the rectangle's four corners after transformation.</summary>
    public static RectangleF GetBoundingBox(Rectangle rectangle, Matrix3x2 matrix)
    {
        Vector2 a = Vector2.Transform(new Vector2(rectangle.Left, rectangle.Top), matrix);
        Vector2 b = Vector2.Transform(new Vector2(rectangle.Right, rectangle.Top), matrix);
        Vector2 c = Vector2.Transform(new Vector2(rectangle.Right, rectangle.Bottom), matrix);
        Vector2 d = Vector2.Transform(new Vector2(rectangle.Left, rectangle.Bottom), matrix);
        return BoundsOf(a, b, c, d);
    }

    /// <summary>The axis-aligned bounding box of the rectangle's four corners after a projective transformation.</summary>
    /// <exception cref="ArgumentException">A corner maps to infinity or behind the viewer.</exception>
    public static RectangleF GetBoundingBox(Rectangle rectangle, Matrix4x4 matrix)
    {
        if (!TryTransform(new Vector2(rectangle.Left, rectangle.Top), matrix, out Vector2 a)
            || !TryTransform(new Vector2(rectangle.Right, rectangle.Top), matrix, out Vector2 b)
            || !TryTransform(new Vector2(rectangle.Right, rectangle.Bottom), matrix, out Vector2 c)
            || !TryTransform(new Vector2(rectangle.Left, rectangle.Bottom), matrix, out Vector2 d))
        {
            throw new ArgumentException("The projective transform maps a corner of the source rectangle to infinity or behind the viewer.", nameof(matrix));
        }

        return BoundsOf(a, b, c, d);
    }

    /// <summary>Rounds a bounding-box size up to whole pixels (with snapping), never below 1x1.</summary>
    /// <exception cref="ArgumentException">The size is not finite.</exception>
    public static Size CeilingSize(SizeF size)
    {
        if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height))
        {
            throw new ArgumentException("The transformed bounding box is not finite; the transform is degenerate.");
        }

        int width = (int)Math.Ceiling(Math.Round(size.Width, 6) - SizeSnapTolerance);
        int height = (int)Math.Ceiling(Math.Round(size.Height, 6) - SizeSnapTolerance);
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// The projective matrix mapping the corners of <paramref name="rectangle"/> onto an arbitrary quadrilateral
    /// (top-left, top-right, bottom-right, bottom-left), i.e. the classic four-point perspective distortion.
    /// </summary>
    /// <exception cref="ArgumentException">The rectangle is empty or the quadrilateral is degenerate.</exception>
    public static Matrix4x4 CreateQuadDistortion(Rectangle rectangle, PointF topLeft, PointF topRight, PointF bottomRight, PointF bottomLeft)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new ArgumentException("The source rectangle must have a positive width and height.", nameof(rectangle));
        }

        // Rectangle -> unit square (affine), then unit square -> quad (Heckbert's square-to-quad mapping).
        var toUnit = new Matrix4x4(
            1f / rectangle.Width, 0f, 0f, 0f,
            0f, 1f / rectangle.Height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -(float)rectangle.X / rectangle.Width, -(float)rectangle.Y / rectangle.Height, 0f, 1f);

        double x0 = topLeft.X, y0 = topLeft.Y;
        double x1 = topRight.X, y1 = topRight.Y;
        double x2 = bottomRight.X, y2 = bottomRight.Y;
        double x3 = bottomLeft.X, y3 = bottomLeft.Y;

        double dx1 = x1 - x2, dy1 = y1 - y2;
        double dx2 = x3 - x2, dy2 = y3 - y2;
        double sx = x0 - x1 + x2 - x3;
        double sy = y0 - y1 + y2 - y3;
        double det = (dx1 * dy2) - (dx2 * dy1);
        if (Math.Abs(det) < 1e-12)
        {
            throw new ArgumentException("The quadrilateral is degenerate: three of its corners are collinear.");
        }

        double g = ((sx * dy2) - (dx2 * sy)) / det;
        double h = ((dx1 * sy) - (sx * dy1)) / det;
        double a = x1 - x0 + (g * x1);
        double b = x3 - x0 + (h * x3);
        double c = x0;
        double d = y1 - y0 + (g * y1);
        double e = y3 - y0 + (h * y3);
        double f = y0;

        // Column form [x'; y'; w] = [a b c; d e f; g h 1] * [u; v; 1] transposed into the row-vector layout.
        var unitToQuad = new Matrix4x4(
            (float)a, (float)d, 0f, (float)g,
            (float)b, (float)e, 0f, (float)h,
            0f, 0f, 1f, 0f,
            (float)c, (float)f, 0f, 1f);

        return toUnit * unitToQuad;
    }

    /// <summary>The projective matrix that shrinks one side of <paramref name="rectangle"/> to <paramref name="fraction"/> of its length.</summary>
    public static Matrix4x4 CreateTaper(Rectangle rectangle, TaperSide side, TaperCorner corner, float fraction)
    {
        if (!(fraction > 0f) || fraction > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "The taper fraction must be in the range (0, 1].");
        }

        float x = rectangle.X;
        float y = rectangle.Y;
        float w = rectangle.Width;
        float h = rectangle.Height;
        var tl = new Vector2(x, y);
        var tr = new Vector2(x + w, y);
        var br = new Vector2(x + w, y + h);
        var bl = new Vector2(x, y + h);

        float shrink = 1f - fraction;
        bool both = corner == TaperCorner.Both;
        bool first = corner is TaperCorner.LeftOrTop or TaperCorner.Both;
        bool second = corner is TaperCorner.RightOrBottom or TaperCorner.Both;
        switch (side)
        {
            case TaperSide.Left:
            {
                float delta = h * shrink * (both ? 0.5f : 1f);
                if (first)
                {
                    tl = new Vector2(tl.X, tl.Y + delta);
                }

                if (second)
                {
                    bl = new Vector2(bl.X, bl.Y - delta);
                }

                break;
            }

            case TaperSide.Right:
            {
                float delta = h * shrink * (both ? 0.5f : 1f);
                if (first)
                {
                    tr = new Vector2(tr.X, tr.Y + delta);
                }

                if (second)
                {
                    br = new Vector2(br.X, br.Y - delta);
                }

                break;
            }

            case TaperSide.Top:
            {
                float delta = w * shrink * (both ? 0.5f : 1f);
                if (first)
                {
                    tl = new Vector2(tl.X + delta, tl.Y);
                }

                if (second)
                {
                    tr = new Vector2(tr.X - delta, tr.Y);
                }

                break;
            }

            case TaperSide.Bottom:
            {
                float delta = w * shrink * (both ? 0.5f : 1f);
                if (first)
                {
                    bl = new Vector2(bl.X + delta, bl.Y);
                }

                if (second)
                {
                    br = new Vector2(br.X - delta, br.Y);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown taper side.");
        }

        return CreateQuadDistortion(
            rectangle,
            new PointF(tl.X, tl.Y),
            new PointF(tr.X, tr.Y),
            new PointF(br.X, br.Y),
            new PointF(bl.X, bl.Y));
    }

    private static RectangleF BoundsOf(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float minX = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
        float maxX = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        float minY = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
        float maxY = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }
}
