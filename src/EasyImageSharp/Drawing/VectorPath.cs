using System.Numerics;

namespace EasyImageSharp.Drawing;

/// <summary>
/// An immutable vector path: a sequence of figures made of straight lines and quadratic and cubic Bezier curves,
/// each figure open or closed. Build one with <see cref="PathBuilder"/>, then fill it with a brush or stroke it
/// with a pen.
/// </summary>
/// <remarks>
/// <para>
/// Coordinates are image pixels in the drawing model of the library: pixel (x, y) covers the unit square from
/// (x, y) to (x + 1, y + 1). Points are stored in double precision. Curves are flattened into line segments when
/// drawn, adaptively, so that no point of the polygon is further than
/// <see cref="DrawingOptions.CurveTolerance"/> from the true curve.
/// </para>
/// <para>
/// Filling treats every figure as closed (an open figure is closed with a straight line); stroking honours the
/// difference. Paths are thread-safe and may be reused freely.
/// </para>
/// </remarks>
public sealed class VectorPath
{
    private readonly PathVerb[] verbs;
    private readonly PointD[] points;

    internal VectorPath(PathVerb[] verbs, PointD[] points)
    {
        this.verbs = verbs;
        this.points = points;
        this.Bounds = ComputeBounds(verbs, points);
    }

    /// <summary>A path with no figures.</summary>
    public static VectorPath Empty { get; } = new([], []);

    /// <summary>Whether the path has no figures.</summary>
    public bool IsEmpty => this.verbs.Length == 0;

    /// <summary>
    /// The tight bounding box of the geometry: curves contribute their true extremes, not their control points.
    /// Stroke width is not included. An empty path has <see cref="RectangleF.Empty"/> bounds.
    /// </summary>
    public RectangleF Bounds { get; }

    /// <summary>The verbs, one per point except <see cref="PathVerb.Close"/> (no point) and the curves (two or three points).</summary>
    internal ReadOnlySpan<PathVerb> Verbs => this.verbs;

    /// <summary>The points referenced by <see cref="Verbs"/>, in order.</summary>
    internal ReadOnlySpan<PointD> Points => this.points;

    /// <summary>Creates a closed rectangular path; a rectangle with a negative size is normalized first.</summary>
    /// <exception cref="ArgumentException">A component of <paramref name="rectangle"/> is not finite.</exception>
    public static VectorPath FromRectangle(RectangleF rectangle) => new PathBuilder().AddRectangle(rectangle).Build();

    /// <summary>Creates a closed path for the ellipse inscribed in <paramref name="bounds"/>, as four cubic Bezier curves.</summary>
    /// <exception cref="ArgumentException">A component of <paramref name="bounds"/> is not finite.</exception>
    public static VectorPath FromEllipse(RectangleF bounds) => new PathBuilder().AddEllipse(bounds).Build();

    /// <summary>Creates a closed polygon through <paramref name="points"/>.</summary>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public static VectorPath FromPolygon(ReadOnlySpan<PointF> points) => new PathBuilder().AddPolygon(points).Build();

    /// <summary>Creates an open polyline through <paramref name="points"/>.</summary>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public static VectorPath FromLines(ReadOnlySpan<PointF> points) => new PathBuilder().AddLines(points).Build();

    /// <summary>
    /// Returns this path with every point transformed by <paramref name="matrix"/>. Bezier curves transform
    /// exactly by their control points under any affine matrix.
    /// </summary>
    /// <exception cref="ArgumentException">An element of <paramref name="matrix"/> is not finite, or a transformed coordinate overflows.</exception>
    public VectorPath Transform(Matrix3x2 matrix)
    {
        if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M21)
            || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32))
        {
            throw new ArgumentException("Matrix elements must be finite numbers.", nameof(matrix));
        }

        if (matrix.IsIdentity)
        {
            return this;
        }

        var transformed = new PointD[this.points.Length];
        for (int i = 0; i < transformed.Length; i++)
        {
            PointD p = this.points[i];
            double x = (p.X * matrix.M11) + (p.Y * matrix.M21) + matrix.M31;
            double y = (p.X * matrix.M12) + (p.Y * matrix.M22) + matrix.M32;
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                throw new ArgumentException("The transform produced coordinates that are not finite.", nameof(matrix));
            }

            transformed[i] = new PointD(x, y);
        }

        return new VectorPath(this.verbs, transformed);
    }

    /// <inheritdoc/>
    public override string ToString()
        => FormattableString.Invariant($"VectorPath [ Verbs={this.verbs.Length}, Bounds={this.Bounds} ]");

    /// <summary>A path of closed polygons, stored as straight lines exactly as given (no smoothing).</summary>
    internal static VectorPath FromContours(IReadOnlyList<PointD[]> contours)
    {
        var verbs = new List<PathVerb>();
        var points = new List<PointD>();
        foreach (PointD[] contour in contours)
        {
            if (contour.Length < 2)
            {
                continue;
            }

            verbs.Add(PathVerb.MoveTo);
            points.Add(contour[0]);
            for (int i = 1; i < contour.Length; i++)
            {
                verbs.Add(PathVerb.LineTo);
                points.Add(contour[i]);
            }

            verbs.Add(PathVerb.Close);
        }

        return new VectorPath(verbs.ToArray(), points.ToArray());
    }

    /// <summary>
    /// Flattens every figure into a polyline whose points stay within <paramref name="tolerance"/> pixels of the
    /// true curves. Curves whose control polygon lies entirely outside <paramref name="cull"/> are replaced by their
    /// chord: that only changes geometry inside the control polygon, so nothing visible changes. Pass
    /// <see langword="null"/> when arc length matters (dashing).
    /// </summary>
    internal List<FlatFigure> Flatten(double tolerance, CullRect? cull)
    {
        double toleranceSquared = tolerance * tolerance;
        var figures = new List<FlatFigure>();
        FlatFigure? figure = null;
        PointD start = default;
        PointD current = default;
        int p = 0;
        foreach (PathVerb verb in this.verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                    current = start = this.points[p++];
                    figure = new FlatFigure();
                    figure.Add(current, corner: true);
                    figures.Add(figure);
                    break;
                case PathVerb.Close:
                    if (figure is not null)
                    {
                        figure.Closed = true;
                        figure = null;
                    }

                    current = start;
                    break;
                default:
                    if (figure is null)
                    {
                        // A segment straight after Close continues from the figure's start point.
                        figure = new FlatFigure();
                        figure.Add(start, corner: true);
                        figures.Add(figure);
                    }

                    figure.HasSegments = true;
                    if (verb == PathVerb.LineTo)
                    {
                        current = this.points[p++];
                        figure.Add(current, corner: true);
                    }
                    else if (verb == PathVerb.QuadTo)
                    {
                        PointD c = this.points[p++];
                        PointD end = this.points[p++];
                        FlattenQuad(figure, current, c, end, toleranceSquared, cull);
                        current = end;
                    }
                    else
                    {
                        PointD c1 = this.points[p++];
                        PointD c2 = this.points[p++];
                        PointD end = this.points[p++];
                        FlattenCubic(figure, current, c1, c2, end, toleranceSquared, cull);
                        current = end;
                    }

                    break;
            }
        }

        return figures;
    }

    /// <summary>Maximum subdivision depth: at most 65,536 line segments per curve.</summary>
    private const int MaxDepth = 16;

    private static void FlattenQuad(FlatFigure figure, PointD p0, PointD p1, PointD p2, double toleranceSquared, CullRect? cull)
    {
        if (cull is CullRect r && !r.Intersects(Min(p0.X, p1.X, p2.X), Min(p0.Y, p1.Y, p2.Y), Max(p0.X, p1.X, p2.X), Max(p0.Y, p1.Y, p2.Y)))
        {
            figure.Add(p2, corner: true);
            return;
        }

        // Elevating to a cubic keeps one subdivision routine; the curve is identical.
        PointD c1 = new(p0.X + ((p1.X - p0.X) * 2 / 3), p0.Y + ((p1.Y - p0.Y) * 2 / 3));
        PointD c2 = new(p2.X + ((p1.X - p2.X) * 2 / 3), p2.Y + ((p1.Y - p2.Y) * 2 / 3));
        FlattenCubic(figure, p0, c1, c2, p2, toleranceSquared, cull: null);
    }

    /// <summary>
    /// Adaptive subdivision: a piece is emitted as its chord once both inner control points lie within the
    /// tolerance of that chord, which by the convex hull property bounds the whole piece.
    /// </summary>
    private static void FlattenCubic(FlatFigure figure, PointD p0, PointD p1, PointD p2, PointD p3, double toleranceSquared, CullRect? cull)
    {
        if (cull is CullRect r && !r.Intersects(
            Min(Math.Min(p0.X, p1.X), p2.X, p3.X), Min(Math.Min(p0.Y, p1.Y), p2.Y, p3.Y),
            Max(Math.Max(p0.X, p1.X), p2.X, p3.X), Max(Math.Max(p0.Y, p1.Y), p2.Y, p3.Y)))
        {
            figure.Add(p3, corner: true);
            return;
        }

        Span<PointD> stack = stackalloc PointD[(MaxDepth + 1) * 4];
        Span<int> depths = stackalloc int[MaxDepth + 1];
        int top = 0;
        stack[0] = p0;
        stack[1] = p1;
        stack[2] = p2;
        stack[3] = p3;
        depths[0] = 0;
        top = 1;
        while (top > 0)
        {
            top--;
            int b = top * 4;
            PointD a0 = stack[b], a1 = stack[b + 1], a2 = stack[b + 2], a3 = stack[b + 3];
            int depth = depths[top];
            if (depth >= MaxDepth
                || (DistanceSquaredToSegment(a1, a0, a3) <= toleranceSquared && DistanceSquaredToSegment(a2, a0, a3) <= toleranceSquared))
            {
                figure.Add(a3, corner: top == 0 && a3 == p3);
                continue;
            }

            // De Casteljau split at t = 0.5; push the second half first so the first half is emitted first.
            PointD m01 = Mid(a0, a1), m12 = Mid(a1, a2), m23 = Mid(a2, a3);
            PointD m012 = Mid(m01, m12), m123 = Mid(m12, m23);
            PointD mid = Mid(m012, m123);
            stack[b] = mid;
            stack[b + 1] = m123;
            stack[b + 2] = m23;
            stack[b + 3] = a3;
            depths[top] = depth + 1;
            top++;
            b = top * 4;
            stack[b] = a0;
            stack[b + 1] = m01;
            stack[b + 2] = m012;
            stack[b + 3] = mid;
            depths[top] = depth + 1;
            top++;
        }
    }

    private static PointD Mid(PointD a, PointD b) => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

    private static double Min(double a, double b, double c) => Math.Min(a, Math.Min(b, c));

    private static double Max(double a, double b, double c) => Math.Max(a, Math.Max(b, c));

    internal static double DistanceSquaredToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double px = p.X - a.X;
        double py = p.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared > 0)
        {
            double t = Math.Clamp(((px * dx) + (py * dy)) / lengthSquared, 0, 1);
            px -= t * dx;
            py -= t * dy;
        }

        return (px * px) + (py * py);
    }

    private static RectangleF ComputeBounds(PathVerb[] verbs, PointD[] points)
    {
        if (points.Length == 0)
        {
            return RectangleF.Empty;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int p = 0;
        PointD current = default;
        PointD start = default;
        foreach (PathVerb verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                case PathVerb.LineTo:
                    current = points[p++];
                    if (verb == PathVerb.MoveTo)
                    {
                        start = current;
                    }

                    Include(current);
                    break;
                case PathVerb.QuadTo:
                    {
                        PointD c = points[p++];
                        PointD end = points[p++];
                        Include(end);
                        IncludeQuadExtrema(current.X, c.X, end.X, current.Y, c.Y, end.Y, isX: true);
                        IncludeQuadExtrema(current.Y, c.Y, end.Y, current.X, c.X, end.X, isX: false);
                        current = end;
                        break;
                    }

                case PathVerb.CubicTo:
                    {
                        PointD c1 = points[p++];
                        PointD c2 = points[p++];
                        PointD end = points[p++];
                        Include(end);
                        IncludeCubicExtrema(current, c1, c2, end);
                        current = end;
                        break;
                    }

                default:
                    current = start;
                    break;
            }
        }

        return RectangleF.FromLTRB((float)minX, (float)minY, (float)maxX, (float)maxY);

        void Include(PointD point)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        void IncludeQuadExtrema(double a, double b, double c, double oa, double ob, double oc, bool isX)
        {
            // B'(t) = 0 at t = (a - b) / (a - 2b + c) along this axis.
            double denominator = a - (2 * b) + c;
            if (denominator == 0)
            {
                return;
            }

            double t = (a - b) / denominator;
            if (t > 0 && t < 1)
            {
                double u = 1 - t;
                double v = (u * u * a) + (2 * u * t * b) + (t * t * c);
                double w = (u * u * oa) + (2 * u * t * ob) + (t * t * oc);
                Include(isX ? new PointD(v, w) : new PointD(w, v));
            }
        }

        void IncludeCubicExtrema(PointD p0, PointD p1, PointD p2, PointD p3)
        {
            Span<double> roots = stackalloc double[4];
            int count = CubicDerivativeRoots(p0.X, p1.X, p2.X, p3.X, roots);
            count += CubicDerivativeRoots(p0.Y, p1.Y, p2.Y, p3.Y, roots[count..]);
            for (int i = 0; i < count; i++)
            {
                double t = roots[i];
                double u = 1 - t;
                double b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
                Include(new PointD(
                    (b0 * p0.X) + (b1 * p1.X) + (b2 * p2.X) + (b3 * p3.X),
                    (b0 * p0.Y) + (b1 * p1.Y) + (b2 * p2.Y) + (b3 * p3.Y)));
            }
        }
    }

    /// <summary>Roots in (0, 1) of the derivative of a one-dimensional cubic Bezier; writes at most two.</summary>
    private static int CubicDerivativeRoots(double p0, double p1, double p2, double p3, Span<double> roots)
    {
        // B'(t) / 3 = a t^2 + b t + c.
        double a = -p0 + (3 * p1) - (3 * p2) + p3;
        double b = 2 * (p0 - (2 * p1) + p2);
        double c = p1 - p0;
        int count = 0;
        double scale = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Abs(c));
        if (scale == 0)
        {
            return 0;
        }

        if (Math.Abs(a) <= scale * 1e-12)
        {
            if (b != 0)
            {
                count = Add(roots, count, -c / b);
            }

            return count;
        }

        double discriminant = (b * b) - (4 * a * c);
        if (discriminant < 0)
        {
            return 0;
        }

        double root = Math.Sqrt(discriminant);
        count = Add(roots, count, (-b + root) / (2 * a));
        count = Add(roots, count, (-b - root) / (2 * a));
        return count;

        static int Add(Span<double> roots, int count, double t)
        {
            if (t > 0 && t < 1)
            {
                roots[count++] = t;
            }

            return count;
        }
    }
}

/// <summary>The kind of each element of a <see cref="VectorPath"/>.</summary>
internal enum PathVerb : byte
{
    MoveTo,
    LineTo,
    QuadTo,
    CubicTo,
    Close,
}

/// <summary>One flattened figure: its polyline, which points are original path vertices, and whether it is closed.</summary>
internal sealed class FlatFigure
{
    public List<PointD> Points { get; } = new();

    /// <summary>
    /// For each point, whether it is a vertex of the original path (where the pen's join applies) rather than a
    /// point produced by flattening a curve (where the stroke must stay smooth).
    /// </summary>
    public List<bool> Corners { get; } = new();

    public bool Closed { get; set; }

    /// <summary>Whether the figure has at least one drawing command after its start, even a zero-length one.</summary>
    public bool HasSegments { get; set; }

    public void Add(PointD point, bool corner)
    {
        this.Points.Add(point);
        this.Corners.Add(corner);
    }
}

/// <summary>An axis-aligned rectangle used to skip geometry that cannot affect any visible pixel.</summary>
internal readonly record struct CullRect(double MinX, double MinY, double MaxX, double MaxY)
{
    public bool Intersects(double minX, double minY, double maxX, double maxY)
        => minX <= this.MaxX && maxX >= this.MinX && minY <= this.MaxY && maxY >= this.MinY;

    public CullRect Inflate(double amount) => new(this.MinX - amount, this.MinY - amount, this.MaxX + amount, this.MaxY + amount);
}
