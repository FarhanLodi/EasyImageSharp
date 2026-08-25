namespace EasyImageSharp.Drawing;

/// <summary>Turns primitives (rectangles, ellipses, stroked paths) into polygons for <see cref="PolygonRasterizer"/>.</summary>
internal static class ShapeBuilder
{
    /// <summary>Maximum chord deviation, in pixels, when flattening ellipses.</summary>
    private const double FlattenTolerance = 0.05;

    /// <summary>Miter joins longer than this multiple of the half thickness fall back to bevel joins.</summary>
    private const double MiterLimit = 4.0;

    public static void AddRectangle(PolygonRasterizer rasterizer, double x, double y, double width, double height, bool hole = false)
    {
        if (!(width > 0) || !(height > 0))
        {
            return;
        }

        ReadOnlySpan<PointD> points =
        [
            new(x, y),
            new(x + width, y),
            new(x + width, y + height),
            new(x, y + height),
        ];
        rasterizer.AddPolygon(points, hole);
    }

    /// <summary>
    /// Adds an ellipse flattened by curvature. Vertices are generated for one quadrant and mirrored so the
    /// polygon is exactly symmetric about both axes through the centre.
    /// </summary>
    public static void AddEllipse(PolygonRasterizer rasterizer, double cx, double cy, double rx, double ry, bool hole = false)
    {
        if (!(rx > 0) || !(ry > 0))
        {
            return;
        }

        double rmax = Math.Max(rx, ry);
        int q = 2;
        if (rmax > FlattenTolerance)
        {
            double theta = 2 * Math.Acos(1 - (FlattenTolerance / rmax));
            q = Math.Clamp((int)Math.Ceiling(Math.PI / 2 / theta), 2, 256);
        }

        double[] dx = new double[q + 1];
        double[] dy = new double[q + 1];
        for (int k = 1; k < q; k++)
        {
            double angle = k * (Math.PI / 2) / q;
            dx[k] = rx * Math.Cos(angle);
            dy[k] = ry * Math.Sin(angle);
        }

        dx[0] = rx;
        dy[0] = 0;
        dx[q] = 0;
        dy[q] = ry;

        var points = new PointD[4 * q];
        for (int k = 0; k < q; k++)
        {
            points[k] = new PointD(cx + dx[k], cy - dy[k]);                       // right -> top
            points[q + k] = new PointD(cx - dx[q - k], cy - dy[q - k]);           // top -> left
            points[(2 * q) + k] = new PointD(cx - dx[k], cy + dy[k]);             // left -> bottom
            points[(3 * q) + k] = new PointD(cx + dx[q - k], cy + dy[q - k]);     // bottom -> right
        }

        rasterizer.AddPolygon(points, hole);
    }

    /// <summary>
    /// Adds the stroke of a polyline (or closed polygon) as a union of per-segment quads with miter joins
    /// (bevelled beyond the miter limit) and butt caps, centred on the path.
    /// </summary>
    public static void AddStroke(PolygonRasterizer rasterizer, ReadOnlySpan<PointF> path, double thickness, bool closed)
    {
        var points = new List<PointD>(path.Length);
        foreach (PointF p in path)
        {
            var candidate = new PointD(p.X, p.Y);
            if (points.Count == 0 || points[^1] != candidate)
            {
                points.Add(candidate);
            }
        }

        if (closed && points.Count > 1 && points[0] == points[^1])
        {
            points.RemoveAt(points.Count - 1);
        }

        int n = points.Count;
        if (n < 2)
        {
            return;
        }

        if (n == 2)
        {
            closed = false;
        }

        double half = thickness / 2;
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
            rasterizer.AddPolygon(quad);
        }

        int firstJoin = closed ? 0 : 1;
        int lastJoin = closed ? n - 1 : n - 2;
        for (int i = firstJoin; i <= lastJoin; i++)
        {
            PointD p = points[(i - 1 + n) % n];
            PointD v = points[i];
            PointD q = points[(i + 1) % n];
            AddJoin(rasterizer, p, v, q, half, quad);
        }
    }

    private static void AddJoin(PolygonRasterizer rasterizer, PointD p, PointD v, PointD q, double half, Span<PointD> scratch)
    {
        (double d1x, double d1y) = Direction(p, v);
        (double d2x, double d2y) = Direction(v, q);
        double cross = (d1x * d2y) - (d1y * d2x);
        double dot = (d1x * d2x) + (d1y * d2y);
        if (Math.Abs(cross) < 1e-9)
        {
            // Collinear continuation or a U-turn: the segment quads already cover the join.
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

        // Miter length is half / cos(theta / 2) = half * sqrt(2 / (1 + dot)).
        double onePlusDot = 1 + dot;
        if (onePlusDot >= 2 / (MiterLimit * MiterLimit))
        {
            var miter = new PointD(v.X + ((n1x + n2x) / onePlusDot), v.Y + ((n1y + n2y) / onePlusDot));
            scratch[0] = v;
            scratch[1] = o1;
            scratch[2] = miter;
            scratch[3] = o2;
            rasterizer.AddPolygon(scratch[..4]);
        }
        else
        {
            scratch[0] = v;
            scratch[1] = o1;
            scratch[2] = o2;
            rasterizer.AddPolygon(scratch[..3]);
        }
    }

    private static (double X, double Y) Direction(PointD from, PointD to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return (dx / length, dy / length);
    }
}
