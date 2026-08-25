using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Locates the page (largest convex quadrilateral) in a photograph or scan: Sobel gradient of a smoothed
/// downscale, hysteresis thresholding, one-pixel closing of the edge map, connected components of the edges,
/// convex hull of the largest component, greedy four-corner polygon approximation of the hull, then a
/// full-resolution refinement that fits each side to the strongest gradient positions and intersects the
/// four lines. Corners are returned in continuous pixel coordinates (see <see cref="PerspectiveWarp"/>).
/// </summary>
internal static class PageDetection
{
    private const int MaxSampleDimension = 400;

    public static PointF[]? Detect<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] luminance = DocumentPlanes.Luminance(frame);
        return Detect(luminance, width, height);
    }

    public static PointF[]? Detect(byte[] luminance, int width, int height)
    {
        int factor = DocumentPlanes.DownscaleFactor(width, height, MaxSampleDimension);
        byte[] small = DocumentPlanes.Downscale(luminance, width, height, factor, out int sw, out int sh);
        if (sw < 16 || sh < 16)
        {
            return null;
        }

        PointF[]? coarse = DetectCoarse(small, sw, sh);
        if (coarse is null)
        {
            return null;
        }

        // Coarse pixel-centre coordinates -> full-resolution continuous coordinates.
        var quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            quad[i] = new PointF((coarse[i].X + 0.5f) * factor, (coarse[i].Y + 0.5f) * factor);
        }

        return Refine(luminance, width, height, quad, factor);
    }

    // ----- Coarse stage -----

    private static PointF[]? DetectCoarse(byte[] plane, int width, int height)
    {
        byte[] smooth = IntegralImage.BoxBlur(plane, width, height, 1);
        var magnitude = new int[plane.Length];
        int maxMagnitude = 0;
        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                int gx = -smooth[i - width - 1] + smooth[i - width + 1]
                         - (2 * smooth[i - 1]) + (2 * smooth[i + 1])
                         - smooth[i + width - 1] + smooth[i + width + 1];
                int gy = -smooth[i - width - 1] - (2 * smooth[i - width]) - smooth[i - width + 1]
                         + smooth[i + width - 1] + (2 * smooth[i + width]) + smooth[i + width + 1];
                int m = Math.Abs(gx) + Math.Abs(gy);
                magnitude[i] = m;
                maxMagnitude = Math.Max(maxMagnitude, m);
            }
        }

        if (maxMagnitude < 16)
        {
            return null;
        }

        // Hysteresis thresholds from Otsu over the non-zero magnitudes (scaled to 8 bits).
        var histogram = new int[256];
        int nonZero = 0;
        double scale = 255.0 / maxMagnitude;
        for (int i = 0; i < magnitude.Length; i++)
        {
            if (magnitude[i] > 0)
            {
                histogram[(int)(magnitude[i] * scale)]++;
                nonZero++;
            }
        }

        int high = (int)(FilterOps.ComputeOtsuThreshold(histogram, nonZero) / scale);
        high = Math.Max(high, 24);
        int low = Math.Max(8, high * 2 / 5);

        var edges = new byte[plane.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < magnitude.Length; i++)
        {
            if (magnitude[i] >= high && edges[i] == 0)
            {
                edges[i] = 1;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int px = p % width;
                    int py = p / width;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx;
                            int ny = py + dy;
                            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                            {
                                continue;
                            }

                            int n = (ny * width) + nx;
                            if (edges[n] == 0 && magnitude[n] >= low)
                            {
                                edges[n] = 1;
                                stack.Push(n);
                            }
                        }
                    }
                }
            }
        }

        // Close one-pixel gaps and take the largest edge component.
        byte[] closed = MorphologyOps.Dilate(edges, width, height, StructuringElement.Square(1));
        int[] labels = ConnectedComponents.LabelMask(closed, width, height, Connectivity.Eight, out ComponentStats[] stats);
        if (stats.Length == 0)
        {
            return null;
        }

        int best = -1;
        long bestArea = 0;
        foreach (ComponentStats s in stats)
        {
            long boxArea = (long)s.Bounds.Width * s.Bounds.Height;
            if (boxArea > bestArea)
            {
                bestArea = boxArea;
                best = s.Label;
            }
        }

        if (bestArea < 0.1 * width * height)
        {
            return null;
        }

        var points = new List<PointF>();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (labels[row + x] == best && edges[row + x] != 0)
                {
                    points.Add(new PointF(x, y));
                }
            }
        }

        if (points.Count < 8)
        {
            return null;
        }

        List<PointF> hull = ConvexHull(points);
        if (hull.Count < 4)
        {
            return null;
        }

        PointF[] quad = ApproximateQuad(hull);
        quad = OrderCorners(quad);
        double area = PolygonArea(quad);
        if (area < 0.1 * width * height || !IsConvex(quad))
        {
            return null;
        }

        return quad;
    }

    // ----- Full-resolution refinement -----

    private static PointF[] Refine(byte[] luminance, int width, int height, PointF[] quad, int factor)
    {
        const int Samples = 32;
        int reach = Math.Max(4, (2 * factor) + 3);
        var lines = new (double A, double B, double C)?[4];
        for (int side = 0; side < 4; side++)
        {
            PointF p0 = quad[side];
            PointF p1 = quad[(side + 1) % 4];
            double dx = p1.X - p0.X;
            double dy = p1.Y - p0.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 8)
            {
                continue;
            }

            double nx = -dy / length;
            double ny = dx / length;
            var found = new List<PointF>();
            for (int s = 0; s < Samples; s++)
            {
                double t = 0.08 + (0.84 * s / (Samples - 1));
                double cx = p0.X + (dx * t);
                double cy = p0.Y + (dy * t);
                if (TryFindEdge(luminance, width, height, cx, cy, nx, ny, reach, out PointF edge))
                {
                    found.Add(edge);
                }
            }

            if (found.Count < Samples / 3)
            {
                continue;
            }

            (double a, double b, double c)? fit = FitLine(found);
            if (fit is null)
            {
                continue;
            }

            // One round of outlier rejection.
            var inliers = new List<PointF>();
            foreach (PointF p in found)
            {
                if (Math.Abs((fit.Value.a * p.X) + (fit.Value.b * p.Y) + fit.Value.c) <= 2.0)
                {
                    inliers.Add(p);
                }
            }

            if (inliers.Count >= Samples / 3 && inliers.Count < found.Count)
            {
                fit = FitLine(inliers) ?? fit;
            }

            lines[side] = fit;
        }

        var refined = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            // Corner i is the intersection of side (i - 1) and side i.
            var previous = lines[(i + 3) % 4];
            var current = lines[i];
            if (previous is not null && current is not null && TryIntersect(previous.Value, current.Value, out PointF p)
                && Distance(p, quad[i]) <= reach * 2.5)
            {
                refined[i] = p;
            }
            else
            {
                refined[i] = quad[i];
            }
        }

        return IsConvex(refined) ? refined : quad;
    }

    /// <summary>
    /// Walks along the normal through (cx, cy) in continuous coordinates and returns the continuous position of
    /// the strongest luminance step (parabolically refined).
    /// </summary>
    private static bool TryFindEdge(byte[] luminance, int width, int height, double cx, double cy, double nx, double ny, int reach, out PointF edge)
    {
        edge = default;
        int count = (2 * reach) + 1;
        var samples = new double[count + 2];
        for (int k = 0; k < count + 2; k++)
        {
            double t = k - reach - 1;
            double x = cx + (nx * t) - 0.5;
            double y = cy + (ny * t) - 0.5;
            if (!SampleBilinear(luminance, width, height, x, y, out samples[k]))
            {
                samples[k] = double.NaN;
            }
        }

        double best = 0;
        int bestIndex = -1;
        var gradient = new double[count];
        for (int k = 0; k < count; k++)
        {
            double a = samples[k];
            double b = samples[k + 2];
            gradient[k] = double.IsNaN(a) || double.IsNaN(b) ? 0 : Math.Abs(b - a);
            if (gradient[k] > best)
            {
                best = gradient[k];
                bestIndex = k;
            }
        }

        if (bestIndex < 0 || best < 12)
        {
            return false;
        }

        double offset = bestIndex - reach;
        if (bestIndex > 0 && bestIndex < count - 1)
        {
            double l = gradient[bestIndex - 1];
            double r = gradient[bestIndex + 1];
            double denominator = l - (2 * best) + r;
            if (denominator < 0)
            {
                offset += Math.Clamp(0.5 * (l - r) / denominator, -1, 1);
            }
        }

        edge = new PointF((float)(cx + (nx * offset)), (float)(cy + (ny * offset)));
        return true;
    }

    private static bool SampleBilinear(byte[] plane, int width, int height, double x, double y, out double value)
    {
        value = 0;
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return false;
        }

        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        double fx = x - x0;
        double fy = y - y0;
        double top = (plane[(y0 * width) + x0] * (1 - fx)) + (plane[(y0 * width) + x1] * fx);
        double bottom = (plane[(y1 * width) + x0] * (1 - fx)) + (plane[(y1 * width) + x1] * fx);
        value = (top * (1 - fy)) + (bottom * fy);
        return true;
    }

    /// <summary>Total least squares line a x + b y + c = 0 with a^2 + b^2 = 1.</summary>
    private static (double A, double B, double C)? FitLine(List<PointF> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        double mx = 0;
        double my = 0;
        foreach (PointF p in points)
        {
            mx += p.X;
            my += p.Y;
        }

        mx /= points.Count;
        my /= points.Count;
        double sxx = 0;
        double sxy = 0;
        double syy = 0;
        foreach (PointF p in points)
        {
            double dx = p.X - mx;
            double dy = p.Y - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        // Direction = eigenvector of the larger eigenvalue of the scatter matrix; normal is perpendicular.
        double trace = sxx + syy;
        double det = (sxx * syy) - (sxy * sxy);
        double lambda = (trace / 2) + Math.Sqrt(Math.Max(0, ((trace * trace) / 4) - det));
        double dirX;
        double dirY;
        if (Math.Abs(sxy) > 1e-12)
        {
            dirX = lambda - syy;
            dirY = sxy;
        }
        else
        {
            dirX = sxx >= syy ? 1 : 0;
            dirY = sxx >= syy ? 0 : 1;
        }

        double norm = Math.Sqrt((dirX * dirX) + (dirY * dirY));
        if (norm < 1e-12)
        {
            return null;
        }

        double a = -dirY / norm;
        double b = dirX / norm;
        double c = -((a * mx) + (b * my));
        return (a, b, c);
    }

    private static bool TryIntersect((double A, double B, double C) l1, (double A, double B, double C) l2, out PointF point)
    {
        double det = (l1.A * l2.B) - (l2.A * l1.B);
        if (Math.Abs(det) < 1e-9)
        {
            point = default;
            return false;
        }

        double x = ((l1.B * l2.C) - (l2.B * l1.C)) / det;
        double y = ((l2.A * l1.C) - (l1.A * l2.C)) / det;
        point = new PointF((float)x, (float)y);
        return true;
    }

    // ----- Geometry helpers -----

    /// <summary>Andrew's monotone chain; returns the hull counter-clockwise (in y-down coordinates: clockwise on screen).</summary>
    internal static List<PointF> ConvexHull(List<PointF> points)
    {
        var sorted = new List<PointF>(points);
        sorted.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        var hull = new List<PointF>(2 * sorted.Count);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            IEnumerable<PointF> sequence = pass == 0 ? sorted : Enumerable.Reverse(sorted);
            foreach (PointF p in sequence)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// <summary>
    /// Greedy four-vertex approximation of a closed convex polygon: start with the farthest pair, then twice add
    /// the vertex farthest from the current polygon's edges.
    /// </summary>
    internal static PointF[] ApproximateQuad(List<PointF> hull)
    {
        int n = hull.Count;
        int a = 0;
        int b = 0;
        double farthest = -1;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double d = Distance(hull[i], hull[j]);
                if (d > farthest)
                {
                    farthest = d;
                    a = i;
                    b = j;
                }
            }
        }

        var chosen = new List<int> { a, b };
        while (chosen.Count < 4)
        {
            chosen.Sort();
            int bestIndex = -1;
            double bestDistance = -1;
            for (int k = 0; k < chosen.Count; k++)
            {
                int from = chosen[k];
                int to = chosen[(k + 1) % chosen.Count];
                PointF p0 = hull[from];
                PointF p1 = hull[to];
                for (int i = (from + 1) % n; i != to; i = (i + 1) % n)
                {
                    double d = DistanceToSegment(hull[i], p0, p1);
                    if (d > bestDistance)
                    {
                        bestDistance = d;
                        bestIndex = i;
                    }
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            chosen.Add(bestIndex);
        }

        chosen.Sort();
        var quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            quad[i] = hull[chosen[Math.Min(i, chosen.Count - 1)]];
        }

        return quad;
    }

    /// <summary>Orders corners TL, TR, BR, BL (clockwise on screen, starting at the smallest x + y).</summary>
    internal static PointF[] OrderCorners(PointF[] quad)
    {
        double cx = 0;
        double cy = 0;
        foreach (PointF p in quad)
        {
            cx += p.X;
            cy += p.Y;
        }

        cx /= quad.Length;
        cy /= quad.Length;
        PointF[] sorted = quad.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();

        // Atan2 ascending in y-down coordinates is clockwise on screen. Rotate so the top-left corner comes first.
        int start = 0;
        double bestSum = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            double sum = sorted[i].X + sorted[i].Y;
            if (sum < bestSum)
            {
                bestSum = sum;
                start = i;
            }
        }

        var ordered = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            ordered[i] = sorted[(start + i) % 4];
        }

        return ordered;
    }

    internal static bool IsConvex(PointF[] quad)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            double cross = Cross(quad[i], quad[(i + 1) % 4], quad[(i + 2) % 4]);
            if (Math.Abs(cross) < 1e-9)
            {
                return false;
            }

            int s = cross > 0 ? 1 : -1;
            if (sign == 0)
            {
                sign = s;
            }
            else if (s != sign)
            {
                return false;
            }
        }

        return true;
    }

    internal static double PolygonArea(PointF[] polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            PointF p = polygon[i];
            PointF q = polygon[(i + 1) % polygon.Length];
            area += ((double)p.X * q.Y) - ((double)q.X * p.Y);
        }

        return Math.Abs(area) / 2;
    }

    private static double Cross(PointF o, PointF a, PointF b)
        => (((double)a.X - o.X) * ((double)b.Y - o.Y)) - (((double)a.Y - o.Y) * ((double)b.X - o.X));

    private static double Distance(PointF a, PointF b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToSegment(PointF p, PointF a, PointF b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq < 1e-12)
        {
            return Distance(p, a);
        }

        double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSq, 0, 1);
        double px = a.X + (t * dx);
        double py = a.Y + (t * dy);
        return Math.Sqrt(((p.X - px) * (p.X - px)) + ((p.Y - py) * (p.Y - py)));
    }
}
