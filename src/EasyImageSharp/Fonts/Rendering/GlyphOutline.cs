namespace EasyImageSharp.Fonts.Rendering;

/// <summary>Path verbs of a <see cref="GlyphOutline"/>.</summary>
internal enum OutlineVerb : byte
{
    /// <summary>Starts a contour at one point.</summary>
    MoveTo,

    /// <summary>A straight segment to one point.</summary>
    LineTo,

    /// <summary>A quadratic Bezier through one control point to an end point (two points).</summary>
    QuadTo,

    /// <summary>A cubic Bezier through two control points to an end point (three points).</summary>
    CubicTo,
}

/// <summary>
/// The outline of one glyph in font units (y up), as closed contours of lines, quadratic and cubic Bezier
/// segments. Every contour is implicitly closed back to its start point. Immutable and shared between threads.
/// </summary>
internal sealed class GlyphOutline
{
    private GlyphOutline(OutlineVerb[] verbs, float[] points, float xMin, float yMin, float xMax, float yMax)
    {
        this.Verbs = verbs;
        this.Points = points;
        this.XMin = xMin;
        this.YMin = yMin;
        this.XMax = xMax;
        this.YMax = yMax;
    }

    /// <summary>The outline of a glyph that draws nothing (a space, or a glyph whose data is unusable).</summary>
    public static GlyphOutline Empty { get; } = new([], [], 0, 0, 0, 0);

    public OutlineVerb[] Verbs { get; }

    /// <summary>Interleaved x, y coordinates consumed by the verbs in order.</summary>
    public float[] Points { get; }

    public bool IsEmpty => this.Verbs.Length == 0;

    /// <summary>Control-box bounds: they enclose every point, including off-curve control points.</summary>
    public float XMin { get; }

    public float YMin { get; }

    public float XMax { get; }

    public float YMax { get; }

    /// <summary>Accumulates contours and produces a <see cref="GlyphOutline"/>.</summary>
    internal sealed class Builder
    {
        /// <summary>Upper bound on the coordinates of one glyph, far above any real glyph.</summary>
        private const int MaxPoints = 1 << 20;

        private readonly List<OutlineVerb> verbs = new();
        private readonly List<float> points = new();
        private bool open;
        private float startX;
        private float startY;
        private float lastX;
        private float lastY;

        public int PointCount => this.points.Count / 2;

        public void MoveTo(float x, float y)
        {
            this.Close();
            this.verbs.Add(OutlineVerb.MoveTo);
            this.Add(x, y);
            this.open = true;
            this.startX = this.lastX = x;
            this.startY = this.lastY = y;
        }

        public void LineTo(float x, float y)
        {
            this.EnsureOpen();
            this.verbs.Add(OutlineVerb.LineTo);
            this.Add(x, y);
            this.lastX = x;
            this.lastY = y;
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            this.EnsureOpen();
            this.verbs.Add(OutlineVerb.QuadTo);
            this.Add(cx, cy);
            this.Add(x, y);
            this.lastX = x;
            this.lastY = y;
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            this.EnsureOpen();
            this.verbs.Add(OutlineVerb.CubicTo);
            this.Add(c1x, c1y);
            this.Add(c2x, c2y);
            this.Add(x, y);
            this.lastX = x;
            this.lastY = y;
        }

        /// <summary>Ends the current contour; the rasteriser closes it back to its start.</summary>
        public void Close() => this.open = false;

        /// <summary>The current point, or the origin before any contour started.</summary>
        public (float X, float Y) Current => (this.lastX, this.lastY);

        /// <summary>The start of the current contour.</summary>
        public (float X, float Y) Start => (this.startX, this.startY);

        public GlyphOutline Build()
        {
            if (this.verbs.Count == 0)
            {
                return Empty;
            }

            float[] pts = this.points.ToArray();
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < pts.Length; i += 2)
            {
                xMin = Math.Min(xMin, pts[i]);
                xMax = Math.Max(xMax, pts[i]);
                yMin = Math.Min(yMin, pts[i + 1]);
                yMax = Math.Max(yMax, pts[i + 1]);
            }

            return new GlyphOutline(this.verbs.ToArray(), pts, xMin, yMin, xMax, yMax);
        }

        private void EnsureOpen()
        {
            if (!this.open)
            {
                // A drawing operator without a preceding move starts a contour at the current point.
                this.verbs.Add(OutlineVerb.MoveTo);
                this.Add(this.lastX, this.lastY);
                this.open = true;
                this.startX = this.lastX;
                this.startY = this.lastY;
            }
        }

        private void Add(float x, float y)
        {
            if (this.points.Count >= MaxPoints * 2)
            {
                throw new InvalidFontException("Malformed font: a glyph outline has more than a million points.");
            }

            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                throw new InvalidFontException("Malformed font: a glyph outline has a non-finite coordinate.");
            }

            this.points.Add(x);
            this.points.Add(y);
        }
    }
}
