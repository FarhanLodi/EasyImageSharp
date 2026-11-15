namespace EasyImageSharp.Fonts.Rendering;

/// <summary>
/// An anti-aliased coverage bitmap of one glyph. Column 0 sits <see cref="Left"/> pixels right of the integer pen
/// position and row 0 sits <see cref="Top"/> pixels below the baseline (negative: above it).
/// </summary>
internal sealed class GlyphBitmap
{
    public GlyphBitmap(int left, int top, int width, int height, byte[] coverage)
    {
        this.Left = left;
        this.Top = top;
        this.Width = width;
        this.Height = height;
        this.Coverage = coverage;
    }

    public static GlyphBitmap Empty { get; } = new(0, 0, 0, 0, []);

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major 0-255 coverage, <see cref="Width"/> bytes per row.</summary>
    public byte[] Coverage { get; }

    public bool IsEmpty => this.Width == 0 || this.Height == 0;
}

/// <summary>
/// Scan converts glyph outlines into coverage bitmaps. Curves are flattened to lines within 1/20 pixel, and each
/// line deposits its exact signed area into an accumulation buffer; a running sum along every row then yields
/// each pixel's covered fraction under the non-zero winding rule (|winding| clamped to 1), the same
/// area-coverage model FreeType's smooth rasteriser uses. There is no hinting and no gamma: coverage is linear.
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>Largest distance, in pixels, between a curve and the lines that approximate it.</summary>
    private const float FlatnessTolerance = 0.05f;

    /// <summary>Most lines one curve is split into.</summary>
    private const int MaxSubdivisions = 256;

    /// <summary>Rows accumulated per pass, which bounds the scratch buffer for very large glyphs.</summary>
    private const int BandRows = 64;

    /// <summary>Largest bitmap rendered for one glyph, in pixels; anything larger comes from a damaged font.</summary>
    private const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Largest coordinate, in pixels, a glyph's bounds may reach.</summary>
    private const float MaxCoordinate = 1 << 20;

    /// <summary>Most line segments one glyph may flatten to (real glyphs need a few thousand).</summary>
    private const int MaxLines = 1 << 21;

    /// <summary>Most pixel visits spent growing one glyph for an outline; beyond it the halo is left out.</summary>
    private const long MaxGrowWork = 1L << 28;

    /// <summary>
    /// Renders <paramref name="outline"/> scaled by <paramref name="scale"/> pixels per font unit, with the pen
    /// <paramref name="offsetX"/> pixels (0 to 1) right of an integer position. A positive
    /// <paramref name="outlineWidth"/> renders the glyph grown outwards by that many pixels instead (a halo used
    /// for outlined text): coverage is full inside the glyph and fades out at <paramref name="outlineWidth"/>
    /// pixels from its edge. Outlines so large or complex that only a damaged font produces them render empty.
    /// </summary>
    public static GlyphBitmap Render(GlyphOutline outline, float scale, float offsetX, float outlineWidth)
    {
        if (outline.IsEmpty)
        {
            return GlyphBitmap.Empty;
        }

        float pad = outlineWidth > 0 ? MathF.Ceiling(outlineWidth) + 1 : 0;
        float leftF = MathF.Floor(offsetX + (outline.XMin * scale)) - pad;
        float rightF = MathF.Ceiling(offsetX + (outline.XMax * scale)) + pad;
        float topF = MathF.Floor(-outline.YMax * scale) - pad;
        float bottomF = MathF.Ceiling(-outline.YMin * scale) + pad;
        if (!(MathF.Abs(leftF) < MaxCoordinate && MathF.Abs(rightF) < MaxCoordinate && MathF.Abs(topF) < MaxCoordinate && MathF.Abs(bottomF) < MaxCoordinate))
        {
            return GlyphBitmap.Empty;
        }

        int left = (int)leftF;
        int top = (int)topF;
        int width = (int)rightF - left;
        int height = (int)bottomF - top;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            return GlyphBitmap.Empty;
        }

        List<float>? lines = Flatten(outline, scale, offsetX - left, -top);
        if (lines is null)
        {
            return GlyphBitmap.Empty;
        }

        byte[] coverage = new byte[width * height];
        Accumulate(lines, width, height, coverage);
        if (outlineWidth > 0)
        {
            Grow(lines, width, height, outlineWidth, coverage);
        }

        return new GlyphBitmap(left, top, width, height, coverage);
    }

    /// <summary>
    /// Converts the outline to closed polylines in bitmap space (x right, y down), stored as consecutive
    /// (x0, y0, x1, y1) line segments; <see langword="null"/> when it needs more than <see cref="MaxLines"/>.
    /// </summary>
    private static List<float>? Flatten(GlyphOutline outline, float scale, float dx, float dy)
    {
        var lines = new List<float>(outline.Points.Length * 4);
        float[] p = outline.Points;
        int pi = 0;
        float startX = 0, startY = 0, x = 0, y = 0;
        bool open = false;
        foreach (OutlineVerb verb in outline.Verbs)
        {
            switch (verb)
            {
                case OutlineVerb.MoveTo:
                    if (open)
                    {
                        AddLine(lines, x, y, startX, startY);
                    }

                    startX = x = dx + (p[pi] * scale);
                    startY = y = dy - (p[pi + 1] * scale);
                    pi += 2;
                    open = true;
                    break;

                case OutlineVerb.LineTo:
                {
                    float nx = dx + (p[pi] * scale);
                    float ny = dy - (p[pi + 1] * scale);
                    pi += 2;
                    AddLine(lines, x, y, nx, ny);
                    x = nx;
                    y = ny;
                    break;
                }

                case OutlineVerb.QuadTo:
                {
                    float cx = dx + (p[pi] * scale);
                    float cy = dy - (p[pi + 1] * scale);
                    float ex = dx + (p[pi + 2] * scale);
                    float ey = dy - (p[pi + 3] * scale);
                    pi += 4;
                    float ddx = x - (2 * cx) + ex;
                    float ddy = y - (2 * cy) + ey;
                    float deviation = MathF.Sqrt((ddx * ddx) + (ddy * ddy));
                    int n = Subdivisions(deviation / (4 * FlatnessTolerance));
                    float px = x, py = y;
                    for (int i = 1; i <= n; i++)
                    {
                        float t = (float)i / n;
                        float mt = 1 - t;
                        float qx = (mt * mt * x) + (2 * mt * t * cx) + (t * t * ex);
                        float qy = (mt * mt * y) + (2 * mt * t * cy) + (t * t * ey);
                        AddLine(lines, px, py, qx, qy);
                        px = qx;
                        py = qy;
                    }

                    x = ex;
                    y = ey;
                    break;
                }

                case OutlineVerb.CubicTo:
                {
                    float c1x = dx + (p[pi] * scale);
                    float c1y = dy - (p[pi + 1] * scale);
                    float c2x = dx + (p[pi + 2] * scale);
                    float c2y = dy - (p[pi + 3] * scale);
                    float ex = dx + (p[pi + 4] * scale);
                    float ey = dy - (p[pi + 5] * scale);
                    pi += 6;
                    float d1x = x - (2 * c1x) + c2x;
                    float d1y = y - (2 * c1y) + c2y;
                    float d2x = c1x - (2 * c2x) + ex;
                    float d2y = c1y - (2 * c2y) + ey;
                    float deviation = MathF.Sqrt(MathF.Max((d1x * d1x) + (d1y * d1y), (d2x * d2x) + (d2y * d2y)));
                    int n = Subdivisions(0.75f * deviation / FlatnessTolerance);
                    float px = x, py = y;
                    for (int i = 1; i <= n; i++)
                    {
                        float t = (float)i / n;
                        float mt = 1 - t;
                        float a = mt * mt * mt;
                        float b = 3 * mt * mt * t;
                        float c = 3 * mt * t * t;
                        float d = t * t * t;
                        float qx = (a * x) + (b * c1x) + (c * c2x) + (d * ex);
                        float qy = (a * y) + (b * c1y) + (c * c2y) + (d * ey);
                        AddLine(lines, px, py, qx, qy);
                        px = qx;
                        py = qy;
                    }

                    x = ex;
                    y = ey;
                    break;
                }
            }

            if (lines.Count > MaxLines * 4)
            {
                return null;
            }
        }

        if (open)
        {
            AddLine(lines, x, y, startX, startY);
        }

        return lines;
    }

    /// <summary>Number of segments that keep a curve within tolerance, given (error-at-one-segment / tolerance).</summary>
    private static int Subdivisions(float ratio)
    {
        if (!(ratio > 1f))
        {
            return 1;
        }

        return (int)Math.Min(MathF.Ceiling(MathF.Sqrt(ratio)), MaxSubdivisions);
    }

    private static void AddLine(List<float> lines, float x0, float y0, float x1, float y1)
    {
        if (x0 == x1 && y0 == y1)
        {
            return;
        }

        lines.Add(x0);
        lines.Add(y0);
        lines.Add(x1);
        lines.Add(y1);
    }

    /// <summary>Deposits every line's signed area band by band and resolves it into coverage.</summary>
    private static void Accumulate(List<float> lines, int width, int height, byte[] coverage)
    {
        int stride = width + 2;
        int bandRows = Math.Min(BandRows, height);
        int bandCount = (height + bandRows - 1) / bandRows;
        float[] area = new float[stride * bandRows];

        // Bucket the lines by the bands they cross (a counting sort), so each band only visits its own lines.
        int lineCount = lines.Count / 4;
        int[] bandStarts = new int[bandCount + 1];
        for (int i = 0; i < lineCount; i++)
        {
            (int first, int last) = BandRange(lines[(i * 4) + 1], lines[(i * 4) + 3], bandRows, bandCount);
            for (int b = first; b <= last; b++)
            {
                bandStarts[b + 1]++;
            }
        }

        for (int b = 0; b < bandCount; b++)
        {
            bandStarts[b + 1] += bandStarts[b];
        }

        int[] bucket = new int[bandStarts[bandCount]];
        int[] fill = (int[])bandStarts.Clone();
        for (int i = 0; i < lineCount; i++)
        {
            (int first, int last) = BandRange(lines[(i * 4) + 1], lines[(i * 4) + 3], bandRows, bandCount);
            for (int b = first; b <= last; b++)
            {
                bucket[fill[b]++] = i;
            }
        }

        for (int band = 0; band < bandCount; band++)
        {
            int bandTop = band * bandRows;
            int bandBottom = Math.Min(height, bandTop + bandRows);
            Array.Clear(area);
            for (int k = bandStarts[band]; k < bandStarts[band + 1]; k++)
            {
                int i = bucket[k] * 4;
                DrawLine(area, stride, width, bandTop, bandBottom, lines[i], lines[i + 1], lines[i + 2], lines[i + 3]);
            }

            for (int y = bandTop; y < bandBottom; y++)
            {
                int rowStart = (y - bandTop) * stride;
                int outStart = y * width;
                float sum = 0;
                for (int x = 0; x < width; x++)
                {
                    sum += area[rowStart + x];
                    float value = MathF.Abs(sum);
                    coverage[outStart + x] = value >= 1f ? (byte)255 : (byte)((value * 255f) + 0.5f);
                }
            }
        }
    }

    /// <summary>The first and last band a line with these end-point y coordinates touches.</summary>
    private static (int First, int Last) BandRange(float y0, float y1, int bandRows, int bandCount)
    {
        float top = MathF.Min(y0, y1);
        float bottom = MathF.Max(y0, y1);
        int first = Math.Clamp((int)MathF.Floor(top) / bandRows, 0, bandCount - 1);
        int last = Math.Clamp(((int)MathF.Ceiling(bottom) - 1) / bandRows, 0, bandCount - 1);
        return (first, Math.Max(first, last));
    }

    /// <summary>
    /// Adds one line's contribution to the rows of the band it crosses. For each row, the part of the line inside
    /// the row deposits its height (signed by direction) split between the cells it crosses so that a running sum
    /// along the row gives, for every pixel, the area to its left that the line encloses.
    /// </summary>
    private static void DrawLine(float[] area, int stride, int width, int bandTop, int bandBottom, float x0, float y0, float x1, float y1)
    {
        if (y0 == y1)
        {
            return;
        }

        float direction = 1f;
        if (y0 > y1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            direction = -1f;
        }

        int rowStart = Math.Max(bandTop, (int)MathF.Floor(y0));
        int rowEnd = Math.Min(bandBottom, (int)MathF.Ceiling(y1));
        if (rowStart >= rowEnd)
        {
            return;
        }

        float dxdy = (x1 - x0) / (y1 - y0);
        for (int row = rowStart; row < rowEnd; row++)
        {
            float segmentTop = MathF.Max(row, y0);
            float segmentBottom = MathF.Min(row + 1, y1);
            float dy = segmentBottom - segmentTop;
            if (dy <= 0)
            {
                continue;
            }

            float xa = Math.Clamp(x0 + ((segmentTop - y0) * dxdy), 0, width);
            float xb = Math.Clamp(x0 + ((segmentBottom - y0) * dxdy), 0, width);
            float d = dy * direction;
            int rowOffset = (row - bandTop) * stride;
            float xLeft = MathF.Min(xa, xb);
            float xRight = MathF.Max(xa, xb);
            float leftFloor = MathF.Floor(xLeft);
            int leftCell = (int)leftFloor;
            int rightCell = (int)MathF.Ceiling(xRight);
            if (rightCell <= leftCell + 1)
            {
                // The segment stays within one pixel column: split by its mean x inside that column.
                float mid = (0.5f * (xa + xb)) - leftFloor;
                area[rowOffset + leftCell] += d - (d * mid);
                area[rowOffset + leftCell + 1] += d * mid;
            }
            else
            {
                float inverse = 1f / (xRight - xLeft);
                float leftFraction = xLeft - leftFloor;
                float firstArea = 0.5f * inverse * (1f - leftFraction) * (1f - leftFraction);
                float rightFraction = xRight - rightCell + 1f;
                float lastArea = 0.5f * inverse * rightFraction * rightFraction;
                area[rowOffset + leftCell] += d * firstArea;
                if (rightCell == leftCell + 2)
                {
                    area[rowOffset + leftCell + 1] += d * (1f - firstArea - lastArea);
                }
                else
                {
                    float secondArea = inverse * (1.5f - leftFraction);
                    area[rowOffset + leftCell + 1] += d * (secondArea - firstArea);
                    for (int cell = leftCell + 2; cell < rightCell - 1; cell++)
                    {
                        area[rowOffset + cell] += d * inverse;
                    }

                    float beforeLast = secondArea + ((rightCell - leftCell - 3) * inverse);
                    area[rowOffset + rightCell - 1] += d * (1f - beforeLast - lastArea);
                }

                area[rowOffset + rightCell] += d * lastArea;
            }
        }
    }

    /// <summary>
    /// Grows filled coverage outwards: a pixel whose centre lies within <paramref name="radius"/> of the outline
    /// becomes covered, with a one-pixel linear fade at the boundary.
    /// </summary>
    private static void Grow(List<float> lines, int width, int height, float radius, byte[] coverage)
    {
        float reachEstimate = (2 * radius) + 3;
        double work = 0;
        for (int i = 0; i < lines.Count; i += 4)
        {
            work += (MathF.Abs(lines[i + 2] - lines[i]) + reachEstimate) * (MathF.Abs(lines[i + 3] - lines[i + 1]) + reachEstimate);
        }

        if (work > MaxGrowWork)
        {
            // Only a pathological outline gets here; it keeps its fill and goes without a halo.
            return;
        }

        float[] distance = new float[width * height];
        distance.AsSpan().Fill(float.MaxValue);
        float reach = radius + 1f;
        for (int i = 0; i < lines.Count; i += 4)
        {
            float x0 = lines[i], y0 = lines[i + 1], x1 = lines[i + 2], y1 = lines[i + 3];
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - reach));
            int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + reach));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - reach));
            int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + reach));
            float ex = x1 - x0;
            float ey = y1 - y0;
            float lengthSquared = (ex * ex) + (ey * ey);
            for (int py = minY; py <= maxY; py++)
            {
                float cy = py + 0.5f;
                int row = py * width;
                for (int px = minX; px <= maxX; px++)
                {
                    float cx = px + 0.5f;
                    float t = lengthSquared > 0 ? Math.Clamp((((cx - x0) * ex) + ((cy - y0) * ey)) / lengthSquared, 0f, 1f) : 0f;
                    float qx = x0 + (t * ex) - cx;
                    float qy = y0 + (t * ey) - cy;
                    float dist = (qx * qx) + (qy * qy);
                    if (dist < distance[row + px])
                    {
                        distance[row + px] = dist;
                    }
                }
            }
        }

        for (int i = 0; i < coverage.Length; i++)
        {
            float d = distance[i];
            if (d == float.MaxValue)
            {
                continue;
            }

            float halo = Math.Clamp(radius + 0.5f - MathF.Sqrt(d), 0f, 1f);
            byte value = (byte)((halo * 255f) + 0.5f);
            if (value > coverage[i])
            {
                coverage[i] = value;
            }
        }
    }
}
