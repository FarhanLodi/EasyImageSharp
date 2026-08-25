namespace EasyImageSharp.Drawing;

/// <summary>A double-precision point used by the internal geometry pipeline.</summary>
internal readonly record struct PointD(double X, double Y);

/// <summary>Receives the coverage computed by <see cref="PolygonRasterizer"/> one row at a time.</summary>
internal interface ICoverageSink
{
    /// <summary>
    /// Blends into row <paramref name="y"/> starting at column <paramref name="x"/>; <paramref name="coverage"/>
    /// holds one 0-255 coverage value per pixel and is guaranteed to lie inside the frame.
    /// </summary>
    void Blend(int y, int x, ReadOnlySpan<byte> coverage);
}

/// <summary>
/// Scanline coverage rasteriser for unions of polygons under the non-zero winding rule.
/// </summary>
/// <remarks>
/// <para>
/// Vertices are snapped to a 1/256 pixel grid and every intersection is computed with integer arithmetic and
/// symmetric rounding, so mirrored geometry produces exactly mirrored coverage. Anti-aliased coverage uses
/// 16 sub-scanlines per pixel row with exact horizontal span coverage; non-anti-aliased coverage samples the
/// pixel centre once (a pixel is covered when its centre lies inside the shape).
/// </para>
/// <para>
/// Polygons added with <see cref="AddPolygon"/> are normalised to a common orientation so overlapping pieces
/// simply union; pass <c>hole: true</c> to subtract a polygon (rings, outlines).
/// </para>
/// </remarks>
internal sealed class PolygonRasterizer
{
    private const int FixedBits = 8;
    private const int One = 1 << FixedBits;
    private const int Mask = One - 1;
    private const int AaSubSamples = 16;
    private const double MaxCoordinate = 1 << 22;

    private Edge[] edges = new Edge[32];
    private int edgeCount;
    private int minX;
    private int maxX;
    private int minY;
    private int maxY;

    /// <summary>Whether no non-horizontal edge has been added.</summary>
    public bool IsEmpty => this.edgeCount == 0;

    /// <summary>Removes every polygon.</summary>
    public void Clear() => this.edgeCount = 0;

    /// <summary>
    /// Adds a closed polygon. Its orientation is normalised so that overlapping polygons union; a
    /// <paramref name="hole"/> is given the opposite orientation and cuts out of what it overlaps.
    /// </summary>
    public void AddPolygon(ReadOnlySpan<PointD> points, bool hole = false)
    {
        int n = points.Length;
        if (n < 3)
        {
            return;
        }

        double area = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            area += (points[j].X * points[i].Y) - (points[i].X * points[j].Y);
        }

        int orientation = area >= 0 ? 1 : -1;
        if (hole)
        {
            orientation = -orientation;
        }

        int prevX = Snap(points[n - 1].X);
        int prevY = Snap(points[n - 1].Y);
        for (int i = 0; i < n; i++)
        {
            int x = Snap(points[i].X);
            int y = Snap(points[i].Y);
            this.AddEdge(prevX, prevY, x, y, orientation);
            prevX = x;
            prevY = y;
        }
    }

    /// <summary>Rasterises the accumulated polygons clipped to a <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public void Rasterize<TSink>(int width, int height, bool antialias, ref TSink sink)
        where TSink : struct, ICoverageSink
    {
        if (this.edgeCount == 0)
        {
            return;
        }

        int rowStart = Math.Max(0, this.minY >> FixedBits);
        int rowEnd = Math.Min(height, (this.maxY + Mask) >> FixedBits);
        int colStart = Math.Max(0, this.minX >> FixedBits);
        int colEnd = Math.Min(width, (this.maxX + Mask) >> FixedBits);
        if (rowStart >= rowEnd || colStart >= colEnd)
        {
            return;
        }

        int bufferWidth = colEnd - colStart;
        int[] cover = new int[bufferWidth + 2];
        int[] delta = new int[bufferWidth + 2];
        byte[] rowCoverage = new byte[bufferWidth];

        // Process edges in top-to-bottom order.
        long[] order = new long[this.edgeCount];
        for (int i = 0; i < this.edgeCount; i++)
        {
            order[i] = ((long)this.edges[i].Y0 << 32) | (uint)i;
        }

        Array.Sort(order);

        int subSamples = antialias ? AaSubSamples : 1;
        int step = One / subSamples;
        int offset = step / 2;
        int full = One * subSamples;
        long clipMin = (long)colStart << FixedBits;
        long clipMax = (long)colEnd << FixedBits;

        int[] active = new int[this.edgeCount];
        long[] crossings = new long[this.edgeCount];
        int activeCount = 0;
        int nextEdge = 0;

        for (int y = rowStart; y < rowEnd; y++)
        {
            Array.Clear(cover);
            Array.Clear(delta);
            bool touched = false;

            for (int s = 0; s < subSamples; s++)
            {
                int sampleY = (y << FixedBits) + offset + (s * step);

                while (nextEdge < this.edgeCount && this.edges[(int)order[nextEdge]].Y0 <= sampleY)
                {
                    int e = (int)order[nextEdge++];
                    if (this.edges[e].Y1 > sampleY)
                    {
                        active[activeCount++] = e;
                    }
                }

                int kept = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    if (this.edges[active[i]].Y1 > sampleY)
                    {
                        active[kept++] = active[i];
                    }
                }

                activeCount = kept;
                if (activeCount == 0)
                {
                    continue;
                }

                int count = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    ref Edge e = ref this.edges[active[i]];
                    long numerator = (long)(sampleY - e.Y0) * (e.X1 - e.X0);
                    long x = e.X0 + RoundDiv(numerator, e.Y1 - e.Y0);
                    crossings[count++] = (x << 1) | (e.Dir > 0 ? 1L : 0L);
                }

                if (count > 1)
                {
                    SortCrossings(crossings, count);
                }

                int winding = 0;
                long spanStart = 0;
                for (int i = 0; i < count; i++)
                {
                    long key = crossings[i];
                    long x = key >> 1;
                    int previous = winding;
                    winding += (key & 1) != 0 ? 1 : -1;
                    if (previous == 0)
                    {
                        spanStart = x;
                    }
                    else if (winding == 0)
                    {
                        long xa = Math.Max(spanStart, clipMin) - clipMin;
                        long xb = Math.Min(x, clipMax) - clipMin;
                        if (xa >= xb)
                        {
                            continue;
                        }

                        touched = true;
                        if (antialias)
                        {
                            int ia = (int)(xa >> FixedBits);
                            int ib = (int)(xb >> FixedBits);
                            if (ia == ib)
                            {
                                cover[ia] += (int)(xb - xa);
                            }
                            else
                            {
                                cover[ia] += One - (int)(xa & Mask);
                                delta[ia + 1] += One;
                                delta[ib] -= One;
                                cover[ib] += (int)(xb & Mask);
                            }
                        }
                        else
                        {
                            // Pixel i is covered when its centre (i + 0.5) lies inside [xa, xb).
                            int i0 = (int)((xa + (One / 2) - 1) >> FixedBits);
                            int i1 = (int)((xb + (One / 2) - 1) >> FixedBits);
                            if (i0 < i1)
                            {
                                delta[i0] += full;
                                delta[i1] -= full;
                            }
                        }
                    }
                }
            }

            if (!touched)
            {
                continue;
            }

            int run = 0;
            int first = -1;
            int last = -1;
            for (int i = 0; i < bufferWidth; i++)
            {
                run += delta[i];
                int total = cover[i] + run;
                byte value;
                if (total <= 0)
                {
                    value = 0;
                }
                else if (total >= full)
                {
                    value = 255;
                }
                else
                {
                    value = (byte)(((total * 255) + (full >> 1)) / full);
                }

                rowCoverage[i] = value;
                if (value != 0)
                {
                    if (first < 0)
                    {
                        first = i;
                    }

                    last = i;
                }
            }

            if (first >= 0)
            {
                sink.Blend(y, colStart + first, rowCoverage.AsSpan(first, last - first + 1));
            }
        }
    }

    private void AddEdge(int x0, int y0, int x1, int y1, int orientation)
    {
        if (y0 == y1)
        {
            return;
        }

        int dir = orientation;
        if (y0 > y1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            dir = -dir;
        }

        if (this.edgeCount == this.edges.Length)
        {
            Array.Resize(ref this.edges, this.edges.Length * 2);
        }

        if (this.edgeCount == 0)
        {
            this.minX = Math.Min(x0, x1);
            this.maxX = Math.Max(x0, x1);
            this.minY = y0;
            this.maxY = y1;
        }
        else
        {
            this.minX = Math.Min(this.minX, Math.Min(x0, x1));
            this.maxX = Math.Max(this.maxX, Math.Max(x0, x1));
            this.minY = Math.Min(this.minY, y0);
            this.maxY = Math.Max(this.maxY, y1);
        }

        this.edges[this.edgeCount++] = new Edge(x0, y0, x1, y1, dir);
    }

    private static int Snap(double value)
    {
        if (double.IsNaN(value))
        {
            value = 0;
        }

        value = Math.Clamp(value, -MaxCoordinate, MaxCoordinate);
        return (int)Math.Round(value * One, MidpointRounding.AwayFromZero);
    }

    /// <summary>Divides rounding half away from zero; <paramref name="denominator"/> must be positive.</summary>
    private static long RoundDiv(long numerator, long denominator)
        => numerator >= 0
            ? (numerator + (denominator >> 1)) / denominator
            : -((-numerator + (denominator >> 1)) / denominator);

    private static void SortCrossings(long[] keys, int count)
    {
        if (count > 24)
        {
            Array.Sort(keys, 0, count);
            return;
        }

        for (int i = 1; i < count; i++)
        {
            long key = keys[i];
            int j = i - 1;
            while (j >= 0 && keys[j] > key)
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = key;
        }
    }

    private readonly record struct Edge(int X0, int Y0, int X1, int Y1, int Dir);
}
