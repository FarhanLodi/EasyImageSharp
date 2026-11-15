namespace EasyImageSharp.Drawing;

/// <summary>The geometric part of a pen, in pixels.</summary>
internal readonly record struct StrokeStyle(
    double HalfWidth,
    LineJoin Join,
    LineCap Cap,
    double MiterLimit,
    double[]? Dashes,
    double DashOffset,
    double Tolerance);

/// <summary>
/// Turns flattened figures into the polygons that make up their stroke, for <see cref="PolygonRasterizer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stroke is emitted as a union of simple pieces: one quadrilateral per segment, one wedge per join on the
/// outer side of the turn, and the caps. <see cref="PolygonRasterizer.AddPolygon"/> normalizes every piece to the
/// same orientation, so the non-zero rule unions them exactly: overlaps (the inside of every join, tight curves
/// whose radius is smaller than half the width, self-crossing figures) are covered once, never twice, and
/// nothing needs to be offset or untangled. This is the same construction the inner side of a join gets in
/// offset-curve strokers, which connect through the vertex.
/// </para>
/// <para>
/// Vertices that come from the original path get the pen's join; vertices produced by flattening a curve always
/// get a round join, so a curve's stroke is the swept disc it should be even where a cusp turns sharply. Pieces
/// whose bounding box misses the cull rectangle are skipped; under the union rule they cannot change a visible
/// pixel.
/// </para>
/// </remarks>
internal sealed class Stroker
{
    /// <summary>Upper bound on dash boundaries per stroke, so a tiny dash on a huge path fails fast instead of hanging.</summary>
    internal const long MaxDashBoundaries = 2_000_000;

    /// <summary>Points closer than this (squared, in pixels) are treated as one.</summary>
    private const double CoincidentSquared = 1e-18;

    private readonly PolygonRasterizer rasterizer;
    private readonly StrokeStyle style;
    private readonly CullRect? cull;
    private readonly List<PointD> scratch = new();
    private long dashBoundaries;

    private Stroker(PolygonRasterizer rasterizer, in StrokeStyle style, CullRect? cull)
    {
        this.rasterizer = rasterizer;
        this.style = style;
        this.cull = cull;
    }

    /// <summary>Adds the stroke of every figure to <paramref name="rasterizer"/>.</summary>
    /// <exception cref="ArgumentException">The dash pattern would produce more than <see cref="MaxDashBoundaries"/> dash boundaries.</exception>
    public static void Stroke(PolygonRasterizer rasterizer, List<FlatFigure> figures, in StrokeStyle style, CullRect? cull)
    {
        var stroker = new Stroker(rasterizer, style, cull);
        foreach (FlatFigure figure in figures)
        {
            stroker.StrokeFigure(figure);
        }
    }

    private void StrokeFigure(FlatFigure figure)
    {
        // Drop coincident points; a dropped vertex passes its corner flag on.
        var points = new List<PointD>(figure.Points.Count);
        var corners = new List<bool>(figure.Points.Count);
        for (int i = 0; i < figure.Points.Count; i++)
        {
            PointD p = figure.Points[i];
            if (points.Count > 0 && DistanceSquared(points[^1], p) <= CoincidentSquared)
            {
                corners[^1] |= figure.Corners[i];
                continue;
            }

            points.Add(p);
            corners.Add(figure.Corners[i]);
        }

        bool closed = figure.Closed;
        if (closed && points.Count > 1 && DistanceSquared(points[0], points[^1]) <= CoincidentSquared)
        {
            corners[0] |= corners[^1];
            points.RemoveAt(points.Count - 1);
            corners.RemoveAt(corners.Count - 1);
        }

        if (points.Count == 1)
        {
            // A zero-length figure that was actually drawn: round caps make a dot, square caps an axis-aligned square.
            if (figure.HasSegments)
            {
                this.AddDot(points[0], 1, 0);
            }

            return;
        }

        if (points.Count == 0)
        {
            return;
        }

        if (this.style.Dashes is double[] dashes)
        {
            this.StrokeDashed(points, corners, closed, dashes);
        }
        else
        {
            this.StrokePolyline(points, corners, closed);
        }
    }

    /// <summary>Strokes a cleaned polyline (at least two distinct consecutive points).</summary>
    private void StrokePolyline(List<PointD> points, List<bool> corners, bool closed)
    {
        int n = points.Count;
        double half = this.style.HalfWidth;
        int segments = closed ? n : n - 1;
        Span<PointD> quad = stackalloc PointD[4];
        for (int i = 0; i < segments; i++)
        {
            PointD a = points[i];
            PointD b = points[(i + 1) % n];
            (double ux, double uy) = Direction(a, b);
            double nx = -uy * half;
            double ny = ux * half;
            quad[0] = new PointD(a.X + nx, a.Y + ny);
            quad[1] = new PointD(b.X + nx, b.Y + ny);
            quad[2] = new PointD(b.X - nx, b.Y - ny);
            quad[3] = new PointD(a.X - nx, a.Y - ny);
            this.Emit(quad);
        }

        int firstJoin = closed ? 0 : 1;
        int lastJoin = closed ? n - 1 : n - 2;
        for (int i = firstJoin; i <= lastJoin; i++)
        {
            LineJoin join = corners[i] ? this.style.Join : LineJoin.Round;
            this.AddJoin(points[(i - 1 + n) % n], points[i], points[(i + 1) % n], join);
        }

        if (!closed)
        {
            (double sx, double sy) = Direction(points[1], points[0]);
            this.AddCap(points[0], sx, sy);
            (double ex, double ey) = Direction(points[n - 2], points[n - 1]);
            this.AddCap(points[n - 1], ex, ey);
        }
    }

    /// <summary>
    /// Adds the join at <paramref name="v"/> between segments p-v and v-q. Miter and bevel joins reproduce the
    /// construction the library has always used for polylines, so plain pens draw exactly as before.
    /// </summary>
    private void AddJoin(PointD p, PointD v, PointD q, LineJoin join)
    {
        double half = this.style.HalfWidth;
        (double d1x, double d1y) = Direction(p, v);
        (double d2x, double d2y) = Direction(v, q);
        double cross = (d1x * d2y) - (d1y * d2x);
        double dot = (d1x * d2x) + (d1y * d2y);
        if (Math.Abs(cross) < 1e-9)
        {
            // A collinear continuation is already covered. A U-turn has no outer corner for miter or bevel joins;
            // a round join closes it with a half disc ahead of the vertex.
            if (dot < 0 && join == LineJoin.Round)
            {
                this.AddHalfDisc(v, d1x, d1y);
            }

            return;
        }

        // The outer side of the turn is opposite to the turn direction.
        double side = cross > 0 ? -1 : 1;
        double n1x = -d1y * half * side;
        double n1y = d1x * half * side;
        double n2x = -d2y * half * side;
        double n2y = d2x * half * side;
        var o1 = new PointD(v.X + n1x, v.Y + n1y);
        var o2 = new PointD(v.X + n2x, v.Y + n2y);
        switch (join)
        {
            case LineJoin.Miter:
                {
                    // Miter length is half / cos(theta / 2) = half * sqrt(2 / (1 + dot)).
                    double onePlusDot = 1 + dot;
                    double limit = this.style.MiterLimit;
                    if (onePlusDot >= 2 / (limit * limit))
                    {
                        var miter = new PointD(v.X + ((n1x + n2x) / onePlusDot), v.Y + ((n1y + n2y) / onePlusDot));
                        this.Emit([v, o1, miter, o2]);
                    }
                    else
                    {
                        this.Emit([v, o1, o2]);
                    }

                    break;
                }

            case LineJoin.Bevel:
                this.Emit([v, o1, o2]);
                break;
            default:
                {
                    double a1 = Math.Atan2(n1y, n1x);
                    double sweep = Math.Atan2(n2y, n2x) - a1;
                    if (sweep > Math.PI)
                    {
                        sweep -= 2 * Math.PI;
                    }
                    else if (sweep < -Math.PI)
                    {
                        sweep += 2 * Math.PI;
                    }

                    int steps = this.ArcSteps(Math.Abs(sweep));
                    List<PointD> wedge = this.scratch;
                    wedge.Clear();
                    wedge.Add(v);
                    wedge.Add(o1);
                    for (int k = 1; k < steps; k++)
                    {
                        double angle = a1 + (sweep * k / steps);
                        wedge.Add(new PointD(v.X + (half * Math.Cos(angle)), v.Y + (half * Math.Sin(angle))));
                    }

                    wedge.Add(o2);
                    this.Emit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wedge));
                    break;
                }
        }
    }

    /// <summary>Adds the cap at end point <paramref name="p"/>; (<paramref name="ux"/>, <paramref name="uy"/>) points away from the stroke.</summary>
    private void AddCap(PointD p, double ux, double uy)
    {
        double half = this.style.HalfWidth;
        switch (this.style.Cap)
        {
            case LineCap.Round:
                this.AddHalfDisc(p, ux, uy);
                break;
            case LineCap.Square:
                {
                    double nx = -uy * half;
                    double ny = ux * half;
                    double ex = ux * half;
                    double ey = uy * half;
                    this.Emit([
                        new PointD(p.X + nx, p.Y + ny),
                        new PointD(p.X + nx + ex, p.Y + ny + ey),
                        new PointD(p.X - nx + ex, p.Y - ny + ey),
                        new PointD(p.X - nx, p.Y - ny),
                    ]);
                    break;
                }
        }
    }

    /// <summary>
    /// Adds the cap shape of a zero-length stroke at <paramref name="p"/>: a disc for round caps, a square aligned with
    /// direction (<paramref name="ux"/>, <paramref name="uy"/>) for square caps, nothing for butt caps.
    /// </summary>
    private void AddDot(PointD p, double ux, double uy)
    {
        if (this.style.Cap == LineCap.Round)
        {
            this.AddHalfDisc(p, ux, uy);
            this.AddHalfDisc(p, -ux, -uy);
        }
        else if (this.style.Cap == LineCap.Square)
        {
            this.AddCap(p, ux, uy);
            this.AddCap(p, -ux, -uy);
        }
    }

    /// <summary>Adds the half disc of radius half the width centred on <paramref name="center"/>, bulging towards (<paramref name="ux"/>, <paramref name="uy"/>).</summary>
    private void AddHalfDisc(PointD center, double ux, double uy)
    {
        double half = this.style.HalfWidth;
        double baseAngle = Math.Atan2(uy, ux) - (Math.PI / 2);
        int steps = this.ArcSteps(Math.PI);
        List<PointD> arc = this.scratch;
        arc.Clear();
        for (int k = 0; k <= steps; k++)
        {
            double angle = baseAngle + (Math.PI * k / steps);
            arc.Add(new PointD(center.X + (half * Math.Cos(angle)), center.Y + (half * Math.Sin(angle))));
        }

        this.Emit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(arc));
    }

    /// <summary>
    /// Number of chords for an arc of <paramref name="sweep"/> radians. Joins and caps are drawn eight times finer
    /// than the curve tolerance: they are small and cheap, and a coarse inscribed polygon visibly shrinks a round
    /// cap. The polygon stays inscribed, so a join or cap never reaches beyond the true stroke outline.
    /// </summary>
    private int ArcSteps(double sweep)
    {
        double half = this.style.HalfWidth;
        double tolerance = this.style.Tolerance / 8;
        if (half <= tolerance)
        {
            return Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2)));
        }

        double step = 2 * Math.Acos(1 - (tolerance / half));
        return Math.Clamp((int)Math.Ceiling(sweep / step), 1, 1024);
    }

    /// <summary>Splits a cleaned polyline into dashes and strokes each one as an open polyline with caps.</summary>
    private void StrokeDashed(List<PointD> points, List<bool> corners, bool closed, double[] dashes)
    {
        double total = 0;
        foreach (double d in dashes)
        {
            total += d;
        }

        // Find the dash element the offset lands in.
        double phase = this.style.DashOffset % total;
        if (phase < 0)
        {
            phase += total;
        }

        int index = 0;
        double remaining = dashes[0];
        while (phase > 0)
        {
            if (phase >= remaining)
            {
                phase -= remaining;
                index = (index + 1) % dashes.Length;
                remaining = dashes[index];
            }
            else
            {
                remaining -= phase;
                phase = 0;
            }
        }

        bool on = (index & 1) == 0;
        DashRun? currentDash = on ? new DashRun(points[0], corners[0]) : null;
        DashRun? firstDash = currentDash;
        bool firstDashFinished = false;
        bool toggled = false;
        int n = points.Count;
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            PointD a = points[i];
            int j = (i + 1) % n;
            PointD b = points[j];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            double ux = dx / length;
            double uy = dy / length;
            double position = 0;
            while (length - position > remaining)
            {
                position += remaining;
                var point = new PointD(a.X + (ux * position), a.Y + (uy * position));
                if (on)
                {
                    currentDash!.Add(point, corner: false);
                    currentDash.EndDirection = (ux, uy);
                    if (ReferenceEquals(currentDash, firstDash) && closed)
                    {
                        // Held back: it may join the last dash across the closing point.
                        firstDashFinished = true;
                    }
                    else
                    {
                        this.FinishDash(currentDash);
                    }

                    currentDash = null;
                }
                else
                {
                    currentDash = new DashRun(point, corner: false) { EndDirection = (ux, uy) };
                }

                toggled = true;
                on = !on;
                index = (index + 1) % dashes.Length;
                remaining = dashes[index];
                if (++this.dashBoundaries > MaxDashBoundaries)
                {
                    throw new ArgumentException(FormattableString.Invariant(
                        $"The pen's dash pattern would split this path into more than {MaxDashBoundaries / 2} dashes."));
                }
            }

            remaining -= length - position;
            if (on)
            {
                currentDash!.Add(b, corners[j]);
                currentDash.EndDirection = (ux, uy);
            }
        }

        if (closed && !toggled && on)
        {
            // One dash covers the whole closed figure: it is simply the closed stroke.
            this.StrokePolyline(points, corners, closed: true);
            return;
        }

        if (currentDash is not null && firstDash is not null && closed && firstDashFinished && !ReferenceEquals(currentDash, firstDash))
        {
            // The last dash runs through the closing point into the first one: draw them as one, joined there.
            for (int k = 1; k < firstDash.Points.Count; k++)
            {
                currentDash.Add(firstDash.Points[k], firstDash.Corners[k]);
            }

            currentDash.EndDirection = firstDash.EndDirection;
            this.FinishDash(currentDash);
            return;
        }

        if (firstDashFinished && firstDash is not null)
        {
            this.FinishDash(firstDash);
        }

        if (currentDash is not null && !ReferenceEquals(currentDash, firstDash))
        {
            this.FinishDash(currentDash);
        }
        else if (currentDash is not null && !firstDashFinished)
        {
            this.FinishDash(currentDash);
        }
    }

    private void FinishDash(DashRun dash)
    {
        // Clean coincident points (a dash boundary can land on a vertex).
        var points = new List<PointD>(dash.Points.Count);
        var corners = new List<bool>(dash.Points.Count);
        for (int i = 0; i < dash.Points.Count; i++)
        {
            if (points.Count > 0 && DistanceSquared(points[^1], dash.Points[i]) <= CoincidentSquared)
            {
                corners[^1] |= dash.Corners[i];
                continue;
            }

            points.Add(dash.Points[i]);
            corners.Add(dash.Corners[i]);
        }

        if (points.Count == 1)
        {
            // A zero-length dash: caps only, square ones aligned with the path.
            this.AddDot(points[0], dash.EndDirection.X, dash.EndDirection.Y);
            return;
        }

        this.StrokePolyline(points, corners, closed: false);
    }

    private void Emit(ReadOnlySpan<PointD> polygon)
    {
        if (this.cull is CullRect r)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (PointD p in polygon)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            if (!r.Intersects(minX, minY, maxX, maxY))
            {
                return;
            }
        }

        this.rasterizer.AddPolygon(polygon);
    }

    private static double DistanceSquared(PointD a, PointD b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return (dx * dx) + (dy * dy);
    }

    private static (double X, double Y) Direction(PointD from, PointD to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return (dx / length, dy / length);
    }

    /// <summary>The points of one dash while it is being collected.</summary>
    private sealed class DashRun
    {
        public DashRun(PointD start, bool corner) => this.Add(start, corner);

        public List<PointD> Points { get; } = new();

        public List<bool> Corners { get; } = new();

        /// <summary>The path direction at the dash's end, which orients square caps on a zero-length dash.</summary>
        public (double X, double Y) EndDirection { get; set; } = (1, 0);

        public void Add(PointD point, bool corner)
        {
            this.Points.Add(point);
            this.Corners.Add(corner);
        }
    }
}
