namespace EasyImageSharp.Drawing;

/// <summary>
/// Builds a <see cref="VectorPath"/> from drawing commands (move, line, quadratic and cubic Bezier, SVG-style arc,
/// close) and whole shapes (rectangles, rounded rectangles, ellipses, polygons, stars). Every method returns the
/// builder so calls can be chained; <see cref="Build"/> takes an immutable snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The commands follow SVG path semantics. A drawing command with no current figure starts one at the current
/// point (the origin for a new builder); after <see cref="Close"/> the current point returns to the start of the
/// closed figure. A <see cref="MoveTo(PointF)"/> that is not followed by a drawing command is dropped.
/// </para>
/// <para>
/// Each <c>Add</c> shape method adds a complete closed figure of its own, drawn clockwise as seen on screen
/// (y pointing down), and leaves the current point at the shape's first vertex. All coordinates must be finite;
/// shapes with a zero or negative size are skipped, as in SVG. A builder is not thread-safe.
/// </para>
/// </remarks>
public sealed class PathBuilder
{
    /// <summary>Distance of a cubic control point along the tangent for a quarter circle of unit radius.</summary>
    private const double Kappa = 0.55228474983079339840;

    private readonly List<PathVerb> verbs = new();
    private readonly List<PointD> points = new();
    private PointD current;
    private PointD figureStart;
    private bool figureOpen;
    private int figureSegments;

    /// <summary>The current point: where the next drawing command starts.</summary>
    public PointF CurrentPoint => new((float)this.current.X, (float)this.current.Y);

    /// <summary>Starts a new figure at <paramref name="point"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="point"/> is not finite.</exception>
    public PathBuilder MoveTo(PointF point)
    {
        PointD p = ToPoint(point, nameof(point));
        if (this.figureOpen && this.figureSegments == 0)
        {
            // Two moves in a row: the first one draws nothing, so replace it.
            this.points[^1] = p;
        }
        else
        {
            this.verbs.Add(PathVerb.MoveTo);
            this.points.Add(p);
        }

        this.figureOpen = true;
        this.figureSegments = 0;
        this.current = p;
        this.figureStart = p;
        return this;
    }

    /// <summary>Starts a new figure at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PathBuilder MoveTo(float x, float y) => this.MoveTo(new PointF(x, y));

    /// <summary>Adds a straight line from the current point to <paramref name="point"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="point"/> is not finite.</exception>
    public PathBuilder LineTo(PointF point)
    {
        PointD p = ToPoint(point, nameof(point));
        this.EnsureFigure();
        this.verbs.Add(PathVerb.LineTo);
        this.points.Add(p);
        this.figureSegments++;
        this.current = p;
        return this;
    }

    /// <summary>Adds a straight line from the current point to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">A coordinate is not finite.</exception>
    public PathBuilder LineTo(float x, float y) => this.LineTo(new PointF(x, y));

    /// <summary>Adds a quadratic Bezier curve from the current point to <paramref name="end"/>.</summary>
    /// <param name="control">The control point.</param>
    /// <param name="end">The end point, which becomes the current point.</param>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public PathBuilder QuadraticBezierTo(PointF control, PointF end)
    {
        PointD c = ToPoint(control, nameof(control));
        PointD e = ToPoint(end, nameof(end));
        this.EnsureFigure();
        this.verbs.Add(PathVerb.QuadTo);
        this.points.Add(c);
        this.points.Add(e);
        this.figureSegments++;
        this.current = e;
        return this;
    }

    /// <summary>Adds a cubic Bezier curve from the current point to <paramref name="end"/>.</summary>
    /// <param name="control1">The first control point, which sets the start tangent.</param>
    /// <param name="control2">The second control point, which sets the end tangent.</param>
    /// <param name="end">The end point, which becomes the current point.</param>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public PathBuilder CubicBezierTo(PointF control1, PointF control2, PointF end)
    {
        PointD c1 = ToPoint(control1, nameof(control1));
        PointD c2 = ToPoint(control2, nameof(control2));
        PointD e = ToPoint(end, nameof(end));
        this.EnsureFigure();
        this.AddCubic(c1, c2, e);
        return this;
    }

    /// <summary>
    /// Adds an elliptical arc from the current point to <paramref name="end"/> with SVG's endpoint parameterization
    /// (the <c>A</c> path command). The arc is stored as cubic Bezier curves of at most 90 degrees each.
    /// </summary>
    /// <param name="radiusX">The ellipse's x radius; its sign is ignored.</param>
    /// <param name="radiusY">The ellipse's y radius; its sign is ignored.</param>
    /// <param name="rotation">The rotation of the ellipse's x axis, in degrees.</param>
    /// <param name="largeArc">Whether to take the arc of more than 180 degrees.</param>
    /// <param name="sweep">
    /// Whether the arc runs in the direction of increasing angle, which appears clockwise on screen (y pointing
    /// down); SVG's <c>sweep-flag</c>.
    /// </param>
    /// <param name="end">The end point, which becomes the current point.</param>
    /// <remarks>
    /// As in SVG, radii too small to reach <paramref name="end"/> are scaled up uniformly until they do, a zero
    /// radius draws a straight line, and an arc ending at the current point is omitted.
    /// </remarks>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public PathBuilder ArcTo(float radiusX, float radiusY, float rotation, bool largeArc, bool sweep, PointF end)
    {
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY) || !float.IsFinite(rotation))
        {
            throw new ArgumentException("Arc radii and rotation must be finite numbers.");
        }

        PointD e = ToPoint(end, nameof(end));
        this.EnsureFigure();
        PointD s = this.current;
        if (s == e)
        {
            return this;
        }

        double rx = Math.Abs((double)radiusX);
        double ry = Math.Abs((double)radiusY);
        if (rx == 0 || ry == 0)
        {
            this.verbs.Add(PathVerb.LineTo);
            this.points.Add(e);
            this.figureSegments++;
            this.current = e;
            return this;
        }

        // SVG 1.1 implementation notes F.6.5: endpoint to centre parameterization.
        double phi = rotation * Math.PI / 180.0;
        double cos = Math.Cos(phi);
        double sin = Math.Sin(phi);
        double hx = (s.X - e.X) / 2;
        double hy = (s.Y - e.Y) / 2;
        double x1 = (cos * hx) + (sin * hy);
        double y1 = (-sin * hx) + (cos * hy);

        // F.6.6: scale radii up when they cannot span the endpoints.
        double lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1)
        {
            double scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        double rx2 = rx * rx, ry2 = ry * ry, x12 = x1 * x1, y12 = y1 * y1;
        double denominator = (rx2 * y12) + (ry2 * x12);
        double coefficient = denominator > 0 ? Math.Sqrt(Math.Max(0, ((rx2 * ry2) - (rx2 * y12) - (ry2 * x12)) / denominator)) : 0;
        if (largeArc == sweep)
        {
            coefficient = -coefficient;
        }

        double cxp = coefficient * rx * y1 / ry;
        double cyp = -coefficient * ry * x1 / rx;
        double cx = (cos * cxp) - (sin * cyp) + ((s.X + e.X) / 2);
        double cy = (sin * cxp) + (cos * cyp) + ((s.Y + e.Y) / 2);
        double ux = (x1 - cxp) / rx, uy = (y1 - cyp) / ry;
        double vx = (-x1 - cxp) / rx, vy = (-y1 - cyp) / ry;
        double theta = Math.Atan2(uy, ux);
        double delta = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        this.AddArcSegments(cx, cy, rx, ry, cos, sin, theta, delta, e);
        return this;
    }

    /// <summary>
    /// Closes the current figure with a straight line back to its start point (if it is not already there); the
    /// current point returns to that start. Does nothing when no figure is open.
    /// </summary>
    public PathBuilder Close()
    {
        if (this.figureOpen && this.figureSegments > 0)
        {
            this.verbs.Add(PathVerb.Close);
        }
        else if (this.figureOpen)
        {
            // A lone move: drop it.
            this.verbs.RemoveAt(this.verbs.Count - 1);
            this.points.RemoveAt(this.points.Count - 1);
        }

        this.figureOpen = false;
        this.figureSegments = 0;
        this.current = this.figureStart;
        return this;
    }

    /// <summary>Adds a closed rectangle; a negative size is normalized first, a zero size adds nothing.</summary>
    /// <exception cref="ArgumentException">A component of <paramref name="rectangle"/> is not finite.</exception>
    public PathBuilder AddRectangle(RectangleF rectangle)
    {
        (double x, double y, double w, double h) = Normalize(rectangle, nameof(rectangle));
        if (!(w > 0) || !(h > 0))
        {
            return this;
        }

        return this.AddClosedPolygon([new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);
    }

    /// <summary>Adds a closed rectangle whose corners are rounded with circular arcs of <paramref name="radius"/>.</summary>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public PathBuilder AddRoundedRectangle(RectangleF rectangle, float radius) => this.AddRoundedRectangle(rectangle, radius, radius);

    /// <summary>
    /// Adds a closed rectangle whose corners are rounded with elliptical arcs. Radii are clamped to half the
    /// rectangle's width and height; zero radii add a plain rectangle.
    /// </summary>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public PathBuilder AddRoundedRectangle(RectangleF rectangle, float radiusX, float radiusY)
    {
        (double x, double y, double w, double h) = Normalize(rectangle, nameof(rectangle));
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY))
        {
            throw new ArgumentException("Corner radii must be finite numbers.");
        }

        if (!(w > 0) || !(h > 0))
        {
            return this;
        }

        double rx = Math.Min(Math.Abs((double)radiusX), w / 2);
        double ry = Math.Min(Math.Abs((double)radiusY), h / 2);
        if (rx <= 0 || ry <= 0)
        {
            return this.AddRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h));
        }

        double kx = rx * Kappa, ky = ry * Kappa;
        double r = x + w, b = y + h;
        this.StartShape(new PointD(x + rx, y));
        this.AddLine(new PointD(r - rx, y));
        this.AddCubic(new PointD(r - rx + kx, y), new PointD(r, y + ry - ky), new PointD(r, y + ry));
        this.AddLine(new PointD(r, b - ry));
        this.AddCubic(new PointD(r, b - ry + ky), new PointD(r - rx + kx, b), new PointD(r - rx, b));
        this.AddLine(new PointD(x + rx, b));
        this.AddCubic(new PointD(x + rx - kx, b), new PointD(x, b - ry + ky), new PointD(x, b - ry));
        this.AddLine(new PointD(x, y + ry));
        this.AddCubic(new PointD(x, y + ry - ky), new PointD(x + rx - kx, y), new PointD(x + rx, y));
        return this.EndShape();
    }

    /// <summary>Adds the closed ellipse inscribed in <paramref name="bounds"/>; a zero size adds nothing.</summary>
    /// <exception cref="ArgumentException">A component of <paramref name="bounds"/> is not finite.</exception>
    public PathBuilder AddEllipse(RectangleF bounds)
    {
        (double x, double y, double w, double h) = Normalize(bounds, nameof(bounds));
        return this.AddEllipseCore(x + (w / 2), y + (h / 2), w / 2, h / 2);
    }

    /// <summary>Adds a closed axis-aligned ellipse; zero radii add nothing.</summary>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public PathBuilder AddEllipse(PointF center, float radiusX, float radiusY)
    {
        PointD c = ToPoint(center, nameof(center));
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY))
        {
            throw new ArgumentException("Ellipse radii must be finite numbers.");
        }

        return this.AddEllipseCore(c.X, c.Y, Math.Abs((double)radiusX), Math.Abs((double)radiusY));
    }

    /// <summary>Adds a closed circle; a zero radius adds nothing.</summary>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public PathBuilder AddCircle(PointF center, float radius) => this.AddEllipse(center, radius, radius);

    /// <summary>Adds a closed polygon through <paramref name="points"/>; fewer than two points add nothing.</summary>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public PathBuilder AddPolygon(ReadOnlySpan<PointF> points)
    {
        PointD[] polygon = ToPoints(points);
        return polygon.Length < 2 ? this : this.AddClosedPolygon(polygon);
    }

    /// <summary>
    /// Starts a new open figure through <paramref name="points"/> (a polyline). The figure stays open, so further
    /// drawing commands continue it. Fewer than two points add nothing.
    /// </summary>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    public PathBuilder AddLines(ReadOnlySpan<PointF> points)
    {
        PointD[] polyline = ToPoints(points);
        if (polyline.Length < 2)
        {
            return this;
        }

        this.MoveTo(points[0]);
        for (int i = 1; i < polyline.Length; i++)
        {
            this.AddLine(polyline[i]);
        }

        return this;
    }

    /// <summary>
    /// Adds a closed star: <paramref name="pointCount"/> outer vertices alternating with as many inner ones. With no
    /// rotation the first point is straight up from the centre.
    /// </summary>
    /// <param name="center">The centre of the star.</param>
    /// <param name="pointCount">The number of points; at least 2.</param>
    /// <param name="outerRadius">The distance of the tips from the centre.</param>
    /// <param name="innerRadius">The distance of the inner vertices from the centre.</param>
    /// <param name="rotation">A clockwise rotation in degrees.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointCount"/> is below 2, or a radius is negative or not finite.</exception>
    /// <exception cref="ArgumentException"><paramref name="center"/> or <paramref name="rotation"/> is not finite.</exception>
    public PathBuilder AddStar(PointF center, int pointCount, float outerRadius, float innerRadius, float rotation = 0f)
    {
        PointD c = ToPoint(center, nameof(center));
        if (pointCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(pointCount), pointCount, "A star needs at least two points.");
        }

        if (!(outerRadius >= 0f) || !float.IsFinite(outerRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadius), outerRadius, "The radius must be a non-negative finite number.");
        }

        if (!(innerRadius >= 0f) || !float.IsFinite(innerRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(innerRadius), innerRadius, "The radius must be a non-negative finite number.");
        }

        if (!float.IsFinite(rotation))
        {
            throw new ArgumentException("The rotation must be a finite number.", nameof(rotation));
        }

        if (outerRadius == 0f && innerRadius == 0f)
        {
            return this;
        }

        var star = new PointD[2 * pointCount];
        double start = (rotation - 90.0) * Math.PI / 180.0;
        for (int i = 0; i < star.Length; i++)
        {
            double angle = start + (i * Math.PI / pointCount);
            double r = (i & 1) == 0 ? outerRadius : innerRadius;
            star[i] = new PointD(c.X + (r * Math.Cos(angle)), c.Y + (r * Math.Sin(angle)));
        }

        return this.AddClosedPolygon(star);
    }

    /// <summary>Appends every figure of <paramref name="path"/> unchanged; the current point moves to its last point.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public PathBuilder AddPath(VectorPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty)
        {
            return this;
        }

        this.CloseLoneMove();
        this.verbs.AddRange(path.Verbs);
        this.points.AddRange(path.Points);
        this.figureOpen = false;
        this.figureSegments = 0;

        // Track the current point and figure start of the appended path.
        int p = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                    this.figureStart = this.current = path.Points[p++];
                    this.figureOpen = true;
                    this.figureSegments = 0;
                    break;
                case PathVerb.LineTo:
                    this.current = path.Points[p++];
                    this.figureSegments++;
                    break;
                case PathVerb.QuadTo:
                    p++;
                    this.current = path.Points[p++];
                    this.figureSegments++;
                    break;
                case PathVerb.CubicTo:
                    p += 2;
                    this.current = path.Points[p++];
                    this.figureSegments++;
                    break;
                default:
                    this.current = this.figureStart;
                    this.figureOpen = false;
                    this.figureSegments = 0;
                    break;
            }
        }

        return this;
    }

    /// <summary>Removes every figure and resets the current point to the origin.</summary>
    public PathBuilder Clear()
    {
        this.verbs.Clear();
        this.points.Clear();
        this.current = default;
        this.figureStart = default;
        this.figureOpen = false;
        this.figureSegments = 0;
        return this;
    }

    /// <summary>
    /// Returns an immutable path holding everything added so far. The builder keeps its contents and can be
    /// extended and built again.
    /// </summary>
    public VectorPath Build()
    {
        int verbCount = this.verbs.Count;
        int pointCount = this.points.Count;
        if (this.figureOpen && this.figureSegments == 0)
        {
            // Trailing lone move.
            verbCount--;
            pointCount--;
        }

        return verbCount == 0 ? VectorPath.Empty : new VectorPath(
            this.verbs.GetRange(0, verbCount).ToArray(),
            this.points.GetRange(0, pointCount).ToArray());
    }

    private void EnsureFigure()
    {
        if (!this.figureOpen)
        {
            this.verbs.Add(PathVerb.MoveTo);
            this.points.Add(this.current);
            this.figureStart = this.current;
            this.figureOpen = true;
            this.figureSegments = 0;
        }
    }

    private void CloseLoneMove()
    {
        if (this.figureOpen && this.figureSegments == 0)
        {
            this.verbs.RemoveAt(this.verbs.Count - 1);
            this.points.RemoveAt(this.points.Count - 1);
            this.figureOpen = false;
        }
    }

    private void AddLine(PointD point)
    {
        this.verbs.Add(PathVerb.LineTo);
        this.points.Add(point);
        this.figureSegments++;
        this.current = point;
    }

    private void AddCubic(PointD c1, PointD c2, PointD end)
    {
        this.verbs.Add(PathVerb.CubicTo);
        this.points.Add(c1);
        this.points.Add(c2);
        this.points.Add(end);
        this.figureSegments++;
        this.current = end;
    }

    private void StartShape(PointD start)
    {
        this.CloseLoneMove();
        this.verbs.Add(PathVerb.MoveTo);
        this.points.Add(start);
        this.figureStart = start;
        this.current = start;
        this.figureOpen = true;
        this.figureSegments = 0;
    }

    private PathBuilder EndShape()
    {
        this.verbs.Add(PathVerb.Close);
        this.figureOpen = false;
        this.figureSegments = 0;
        this.current = this.figureStart;
        return this;
    }

    private PathBuilder AddClosedPolygon(ReadOnlySpan<PointD> polygon)
    {
        this.StartShape(polygon[0]);
        for (int i = 1; i < polygon.Length; i++)
        {
            this.AddLine(polygon[i]);
        }

        return this.EndShape();
    }

    private PathBuilder AddEllipseCore(double cx, double cy, double rx, double ry)
    {
        if (!(rx > 0) || !(ry > 0))
        {
            return this;
        }

        double kx = rx * Kappa, ky = ry * Kappa;
        this.StartShape(new PointD(cx + rx, cy));
        this.AddCubic(new PointD(cx + rx, cy + ky), new PointD(cx + kx, cy + ry), new PointD(cx, cy + ry));
        this.AddCubic(new PointD(cx - kx, cy + ry), new PointD(cx - rx, cy + ky), new PointD(cx - rx, cy));
        this.AddCubic(new PointD(cx - rx, cy - ky), new PointD(cx - kx, cy - ry), new PointD(cx, cy - ry));
        this.AddCubic(new PointD(cx + kx, cy - ry), new PointD(cx + rx, cy - ky), new PointD(cx + rx, cy));
        return this.EndShape();
    }

    /// <summary>Appends an arc given in centre parameterization as cubic segments of at most 90 degrees.</summary>
    private void AddArcSegments(double cx, double cy, double rx, double ry, double cos, double sin, double theta, double delta, PointD end)
    {
        int segments = Math.Max(1, (int)Math.Ceiling((Math.Abs(delta) / (Math.PI / 2)) - 1e-9));
        double step = delta / segments;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        double a0 = theta;
        double c0 = Math.Cos(a0), s0 = Math.Sin(a0);
        for (int i = 0; i < segments; i++)
        {
            double a1 = theta + ((i + 1) * step);
            double c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            PointD control1 = Map(c0 - (k * s0), s0 + (k * c0));
            PointD control2 = Map(c1 + (k * s1), s1 - (k * c1));
            PointD target = i == segments - 1 ? end : Map(c1, s1);
            this.AddCubic(control1, control2, target);
            c0 = c1;
            s0 = s1;
        }

        PointD Map(double ux, double uy)
        {
            double px = ux * rx;
            double py = uy * ry;
            return new PointD(cx + (cos * px) - (sin * py), cy + (sin * px) + (cos * py));
        }
    }

    private static PointD ToPoint(PointF point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentException("Point coordinates must be finite numbers.", parameterName);
        }

        return new PointD(point.X, point.Y);
    }

    private static PointD[] ToPoints(ReadOnlySpan<PointF> points)
    {
        var result = new PointD[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = ToPoint(points[i], nameof(points));
        }

        return result;
    }

    private static (double X, double Y, double Width, double Height) Normalize(RectangleF rectangle, string parameterName)
    {
        if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) || !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height))
        {
            throw new ArgumentException("Rectangle components must be finite numbers.", parameterName);
        }

        double x = rectangle.X, y = rectangle.Y, w = rectangle.Width, h = rectangle.Height;
        if (w < 0)
        {
            x += w;
            w = -w;
        }

        if (h < 0)
        {
            y += h;
            h = -h;
        }

        return (x, y, w, h);
    }
}
