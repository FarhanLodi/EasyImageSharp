using System.Collections.ObjectModel;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>A colour at a position along a gradient, 0 at its start and 1 at its end.</summary>
public readonly struct ColorStop : IEquatable<ColorStop>
{
    /// <summary>Initializes a colour stop.</summary>
    /// <param name="offset">The position along the gradient; values outside 0-1 are clamped when the gradient is built.</param>
    /// <param name="color">The colour at that position, including its alpha.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is not a finite number.</exception>
    public ColorStop(float offset, Color color)
    {
        if (!float.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Colour stop offsets must be finite numbers.");
        }

        this.Offset = offset;
        this.Color = color;
    }

    /// <summary>The position along the gradient, 0 at its start and 1 at its end.</summary>
    public float Offset { get; }

    /// <summary>The colour at <see cref="Offset"/>.</summary>
    public Color Color { get; }

    /// <inheritdoc/>
    public bool Equals(ColorStop other) => this.Offset == other.Offset && this.Color == other.Color;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ColorStop other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Offset, this.Color);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"ColorStop [ Offset={this.Offset}, Color={this.Color} ]");

    /// <summary>Whether two stops are equal.</summary>
    public static bool operator ==(ColorStop left, ColorStop right) => left.Equals(right);

    /// <summary>Whether two stops differ.</summary>
    public static bool operator !=(ColorStop left, ColorStop right) => !left.Equals(right);
}

/// <summary>
/// The shared part of every gradient brush: the colour stops, how the gradient continues outside 0-1, and the
/// colour space it blends in.
/// </summary>
/// <remarks>
/// Stops are sorted by offset when the brush is built (stops with equal offsets keep their order and make a
/// hard edge), offsets are clamped to 0-1, and the colour before the first stop and after the last is that
/// stop's colour. Colours are blended with premultiplied alpha, so a fade to a transparent stop keeps its hue
/// instead of darkening. Every pixel is computed exactly from its own position; there is no colour table.
/// </remarks>
public abstract class GradientBrush : Brush
{
    private readonly ColorStop[] stops;

    private protected GradientBrush(ReadOnlySpan<ColorStop> stops)
    {
        if (stops.IsEmpty)
        {
            throw new ArgumentException("A gradient needs at least one colour stop.", nameof(stops));
        }

        var sorted = new ColorStop[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            sorted[i] = new ColorStop(Math.Clamp(stops[i].Offset, 0f, 1f), stops[i].Color);
        }

        // Insertion sort: stable, so equal offsets keep the caller's order (a hard colour edge).
        for (int i = 1; i < sorted.Length; i++)
        {
            ColorStop key = sorted[i];
            int j = i - 1;
            while (j >= 0 && sorted[j].Offset > key.Offset)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }

            sorted[j + 1] = key;
        }

        this.stops = sorted;
        this.Stops = new ReadOnlyCollection<ColorStop>(sorted);
    }

    /// <summary>The colour stops, sorted by offset and clamped to 0-1.</summary>
    public IReadOnlyList<ColorStop> Stops { get; }

    /// <summary>How the gradient continues before 0 and after 1; defaults to <see cref="WrapMode.Clamp"/>.</summary>
    public WrapMode WrapMode { get; init; }

    /// <summary>The colour space the stops are blended in; defaults to <see cref="GradientInterpolation.Srgb"/>.</summary>
    public GradientInterpolation Interpolation { get; init; }

    internal GradientRamp CreateRamp() => new(this.stops, this.WrapMode, this.Interpolation);

    /// <summary>The colour of the last stop, used by degenerate gradients (a zero-length line or a zero radius).</summary>
    internal Rgba32 LastStopColor => this.stops[^1].Color.ToRgba32();
}

/// <summary>
/// A gradient along the line from <see cref="Start"/> to <see cref="End"/>: every point is coloured by its
/// projection onto that line, so the colour is constant along each perpendicular.
/// </summary>
public sealed class LinearGradientBrush : GradientBrush
{
    /// <summary>Initializes a linear gradient.</summary>
    /// <param name="start">Where offset 0 lies, in brush space.</param>
    /// <param name="end">Where offset 1 lies, in brush space. When it equals <paramref name="start"/> the brush paints the last stop's colour.</param>
    /// <param name="stops">The colour stops; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="stops"/> is empty or a point is not finite.</exception>
    public LinearGradientBrush(PointF start, PointF end, params ReadOnlySpan<ColorStop> stops)
        : base(stops)
    {
        GradientGeometry.RequireFinite(start, nameof(start));
        GradientGeometry.RequireFinite(end, nameof(end));
        this.Start = start;
        this.End = end;
    }

    /// <summary>Initializes a two-colour linear gradient.</summary>
    /// <param name="start">Where <paramref name="startColor"/> lies, in brush space.</param>
    /// <param name="end">Where <paramref name="endColor"/> lies, in brush space.</param>
    /// <param name="startColor">The colour at offset 0.</param>
    /// <param name="endColor">The colour at offset 1.</param>
    public LinearGradientBrush(PointF start, PointF end, Color startColor, Color endColor)
        : this(start, end, new ColorStop(0f, startColor), new ColorStop(1f, endColor))
    {
    }

    /// <summary>Where offset 0 lies, in brush space.</summary>
    public PointF Start { get; }

    /// <summary>Where offset 1 lies, in brush space.</summary>
    public PointF End { get; }

    internal override BrushShader? CreateShader()
    {
        InverseTransform? inverse = this.GetInverseTransform();
        if (inverse is null)
        {
            return null;
        }

        double dx = (double)this.End.X - this.Start.X;
        double dy = (double)this.End.Y - this.Start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (!(lengthSquared > 1e-18))
        {
            return new ConstantShader(this.LastStopColor);
        }

        return new LinearShader(this.CreateRamp(), inverse.Value, this.Start.X, this.Start.Y, dx / lengthSquared, dy / lengthSquared);
    }

    private sealed class LinearShader : BrushShader
    {
        private readonly GradientRamp ramp;
        private readonly InverseTransform inverse;
        private readonly double startX;
        private readonly double startY;
        private readonly double scaledX;
        private readonly double scaledY;

        public LinearShader(GradientRamp ramp, InverseTransform inverse, double startX, double startY, double scaledX, double scaledY)
        {
            this.ramp = ramp;
            this.inverse = inverse;
            this.startX = startX;
            this.startY = startY;
            this.scaledX = scaledX;
            this.scaledY = scaledY;
        }

        public override void Shade(int y, int x, Span<Rgba32> destination)
        {
            // t is affine in image space, so it advances by a constant step along a row. Each pixel is computed
            // from its own index rather than by accumulation, so long rows do not drift.
            (double bx, double by) = this.inverse.Map(x + 0.5, y + 0.5);
            double t0 = ((bx - this.startX) * this.scaledX) + ((by - this.startY) * this.scaledY);
            (double sx, double sy) = this.inverse.StepX;
            double dt = (sx * this.scaledX) + (sy * this.scaledY);
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = this.ramp.ColorAt(t0 + (i * dt));
            }
        }
    }
}

/// <summary>
/// A radial gradient: offset 0 at the focal point and offset 1 on the circle of <see cref="Radius"/> around
/// <see cref="Center"/>, the colour constant on every circle in between (the SVG and CSS model).
/// </summary>
/// <remarks>
/// With the focal point at the centre this is the ordinary concentric gradient. Moving the focal point off
/// centre makes the gradient lopsided, like a highlight on a sphere. A focal point on or outside the circle is
/// moved just inside it (to 99.9% of the radius), as SVG 1.1 prescribes, so every point has a colour. Use
/// <see cref="Brush.Transform"/> for elliptical gradients.
/// </remarks>
public sealed class RadialGradientBrush : GradientBrush
{
    /// <summary>Initializes a concentric radial gradient.</summary>
    /// <param name="center">The centre, where offset 0 lies, in brush space.</param>
    /// <param name="radius">The radius of the circle where offset 1 lies; 0 paints the last stop's colour.</param>
    /// <param name="stops">The colour stops; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="stops"/> is empty or <paramref name="center"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public RadialGradientBrush(PointF center, float radius, params ReadOnlySpan<ColorStop> stops)
        : this(center, radius, center, stops)
    {
    }

    /// <summary>Initializes a radial gradient with a focal point.</summary>
    /// <param name="center">The centre of the end circle, in brush space.</param>
    /// <param name="radius">The radius of the circle where offset 1 lies; 0 paints the last stop's colour.</param>
    /// <param name="focalPoint">Where offset 0 lies, in brush space; moved inside the circle when it is not.</param>
    /// <param name="stops">The colour stops; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="stops"/> is empty or a point is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public RadialGradientBrush(PointF center, float radius, PointF focalPoint, params ReadOnlySpan<ColorStop> stops)
        : base(stops)
    {
        GradientGeometry.RequireFinite(center, nameof(center));
        GradientGeometry.RequireFinite(focalPoint, nameof(focalPoint));
        if (!(radius >= 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be a non-negative finite number.");
        }

        this.Center = center;
        this.Radius = radius;
        this.FocalPoint = focalPoint;
    }

    /// <summary>Initializes a two-colour concentric radial gradient.</summary>
    /// <param name="center">The centre, where <paramref name="centerColor"/> lies.</param>
    /// <param name="radius">The radius where <paramref name="edgeColor"/> lies.</param>
    /// <param name="centerColor">The colour at offset 0.</param>
    /// <param name="edgeColor">The colour at offset 1.</param>
    public RadialGradientBrush(PointF center, float radius, Color centerColor, Color edgeColor)
        : this(center, radius, center, new ColorStop(0f, centerColor), new ColorStop(1f, edgeColor))
    {
    }

    /// <summary>The centre of the circle where offset 1 lies, in brush space.</summary>
    public PointF Center { get; }

    /// <summary>The radius of the circle where offset 1 lies.</summary>
    public float Radius { get; }

    /// <summary>Where offset 0 lies, in brush space (as given; it is moved inside the circle when shading if necessary).</summary>
    public PointF FocalPoint { get; }

    internal override BrushShader? CreateShader()
    {
        InverseTransform? inverse = this.GetInverseTransform();
        if (inverse is null)
        {
            return null;
        }

        double radius = this.Radius;
        if (!(radius > 1e-9))
        {
            return new ConstantShader(this.LastStopColor);
        }

        double cx = this.Center.X, cy = this.Center.Y;
        double fx = this.FocalPoint.X, fy = this.FocalPoint.Y;
        double ox = fx - cx, oy = fy - cy;
        double distance = Math.Sqrt((ox * ox) + (oy * oy));
        double limit = radius * 0.999;
        if (distance > limit)
        {
            fx = cx + (ox * limit / distance);
            fy = cy + (oy * limit / distance);
        }

        return new RadialShader(this.CreateRamp(), inverse.Value, cx, cy, radius, fx, fy);
    }

    private sealed class RadialShader : BrushShader
    {
        private readonly GradientRamp ramp;
        private readonly InverseTransform inverse;
        private readonly double fx;
        private readonly double fy;
        private readonly double cdx;
        private readonly double cdy;
        private readonly double negA;

        public RadialShader(GradientRamp ramp, InverseTransform inverse, double cx, double cy, double radius, double fx, double fy)
        {
            this.ramp = ramp;
            this.inverse = inverse;
            this.fx = fx;
            this.fy = fy;
            this.cdx = cx - fx;
            this.cdy = cy - fy;

            // a = |c - f|^2 - r^2 is negative because the focal point is strictly inside the circle.
            this.negA = (radius * radius) - ((this.cdx * this.cdx) + (this.cdy * this.cdy));
        }

        public override void Shade(int y, int x, Span<Rgba32> destination)
        {
            // Offset t is the largest t >= 0 with |p - f - t (c - f)| = t r: the circle interpolated from the focal
            // point (radius 0) to the end circle that passes through p: a t^2 - 2 b t + c0 = 0 with
            // a = |c - f|^2 - r^2 < 0, b = (p - f).(c - f) and c0 = |p - f|^2, whose larger root is
            // t = (sqrt(b^2 - a c0) - b) / -a.
            (double bx, double by) = this.inverse.Map(x + 0.5, y + 0.5);
            (double sx, double sy) = this.inverse.StepX;
            for (int i = 0; i < destination.Length; i++)
            {
                double px = bx + (i * sx) - this.fx;
                double py = by + (i * sy) - this.fy;
                double b = (px * this.cdx) + (py * this.cdy);
                double c0 = (px * px) + (py * py);
                double t = (Math.Sqrt(Math.Max(0, (b * b) + (this.negA * c0))) - b) / this.negA;
                destination[i] = this.ramp.ColorAt(t);
            }
        }
    }
}

/// <summary>
/// A conic (sweep) gradient around <see cref="Center"/>: the colour depends only on the angle, running from offset
/// 0 at <see cref="StartAngle"/> clockwise through a full turn to offset 1.
/// </summary>
/// <remarks>
/// Angles are in degrees, measured clockwise from the positive x axis as it appears on screen (y pointing down).
/// Offsets always lie in 0-1, so <see cref="GradientBrush.WrapMode"/> has no effect. Use the same colour for the
/// first and last stop to avoid a hard edge at the start angle.
/// </remarks>
public sealed class ConicGradientBrush : GradientBrush
{
    /// <summary>Initializes a conic gradient.</summary>
    /// <param name="center">The centre, in brush space.</param>
    /// <param name="startAngle">The angle of offset 0 in degrees, clockwise from the positive x axis.</param>
    /// <param name="stops">The colour stops; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="stops"/> is empty or <paramref name="center"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startAngle"/> is not finite.</exception>
    public ConicGradientBrush(PointF center, float startAngle, params ReadOnlySpan<ColorStop> stops)
        : base(stops)
    {
        GradientGeometry.RequireFinite(center, nameof(center));
        if (!float.IsFinite(startAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngle), startAngle, "The start angle must be a finite number.");
        }

        this.Center = center;
        this.StartAngle = startAngle;
    }

    /// <summary>The centre, in brush space.</summary>
    public PointF Center { get; }

    /// <summary>The angle of offset 0 in degrees, clockwise from the positive x axis.</summary>
    public float StartAngle { get; }

    internal override BrushShader? CreateShader()
    {
        InverseTransform? inverse = this.GetInverseTransform();
        return inverse is null ? null : new ConicShader(this.CreateRamp(), inverse.Value, this.Center.X, this.Center.Y, this.StartAngle);
    }

    private sealed class ConicShader : BrushShader
    {
        private readonly GradientRamp ramp;
        private readonly InverseTransform inverse;
        private readonly double cx;
        private readonly double cy;
        private readonly double startTurns;

        public ConicShader(GradientRamp ramp, InverseTransform inverse, double cx, double cy, double startAngle)
        {
            this.ramp = ramp;
            this.inverse = inverse;
            this.cx = cx;
            this.cy = cy;
            this.startTurns = startAngle / 360.0;
        }

        public override void Shade(int y, int x, Span<Rgba32> destination)
        {
            (double bx, double by) = this.inverse.Map(x + 0.5, y + 0.5);
            (double sx, double sy) = this.inverse.StepX;
            for (int i = 0; i < destination.Length; i++)
            {
                double dx = bx + (i * sx) - this.cx;
                double dy = by + (i * sy) - this.cy;

                // With y pointing down, atan2 grows clockwise on screen.
                double turns = (Math.Atan2(dy, dx) / (2 * Math.PI)) - this.startTurns;
                turns -= Math.Floor(turns);
                destination[i] = this.ramp.ColorAt(turns);
            }
        }
    }
}

/// <summary>A shader that paints one colour everywhere.</summary>
internal sealed class ConstantShader : BrushShader
{
    private readonly Rgba32 color;

    public ConstantShader(Rgba32 color) => this.color = color;

    public override void Shade(int y, int x, Span<Rgba32> destination) => destination.Fill(this.color);
}

/// <summary>Argument checks shared by the gradient constructors.</summary>
internal static class GradientGeometry
{
    public static void RequireFinite(PointF point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentException("Point coordinates must be finite numbers.", parameterName);
        }
    }
}

/// <summary>
/// Evaluates a gradient's colour at an offset: applies the wrap mode, finds the surrounding stops and blends
/// them with premultiplied alpha in sRGB or linear light.
/// </summary>
internal sealed class GradientRamp
{
    /// <summary>
    /// Linear-light value at the boundary between each pair of adjacent 8-bit sRGB codes; a linear value
    /// encodes to the number of thresholds it reaches, which is exact round-to-nearest of the sRGB encoding.
    /// </summary>
    private static readonly double[] LinearThresholds = BuildThresholds();

    private readonly double[] offsets;
    private readonly double[] red;
    private readonly double[] green;
    private readonly double[] blue;
    private readonly double[] alpha;
    private readonly Rgba32[] stopColors;
    private readonly WrapMode wrap;
    private readonly bool linearLight;

    public GradientRamp(ReadOnlySpan<ColorStop> stops, WrapMode wrap, GradientInterpolation interpolation)
    {
        int n = stops.Length;
        this.offsets = new double[n];
        this.red = new double[n];
        this.green = new double[n];
        this.blue = new double[n];
        this.alpha = new double[n];
        this.stopColors = new Rgba32[n];
        this.wrap = wrap;
        this.linearLight = interpolation == GradientInterpolation.LinearLight;
        for (int i = 0; i < n; i++)
        {
            Color c = stops[i].Color;
            double a = c.A / 255.0;
            this.offsets[i] = stops[i].Offset;
            this.alpha[i] = a;
            this.red[i] = this.Decode(c.R) * a;
            this.green[i] = this.Decode(c.G) * a;
            this.blue[i] = this.Decode(c.B) * a;
            this.stopColors[i] = c.ToRgba32();
        }
    }

    public Rgba32 ColorAt(double t)
    {
        if (double.IsNaN(t))
        {
            t = 0;
        }

        switch (this.wrap)
        {
            case WrapMode.Repeat:
                t -= Math.Floor(t);
                break;
            case WrapMode.Reflect:
                t -= 2 * Math.Floor(t / 2);
                if (t > 1)
                {
                    t = 2 - t;
                }

                break;
            case WrapMode.Transparent:
                if (t < 0 || t > 1)
                {
                    return default;
                }

                break;
        }

        int last = this.offsets.Length - 1;
        if (t <= this.offsets[0])
        {
            return this.stopColors[0];
        }

        if (t >= this.offsets[last])
        {
            return this.stopColors[last];
        }

        // offsets[i] <= t < offsets[i + 1]; stops at an equal offset are skipped, so a hard edge takes the later colour.
        int i = 0;
        while (t >= this.offsets[i + 1])
        {
            i++;
        }

        double f = (t - this.offsets[i]) / (this.offsets[i + 1] - this.offsets[i]);
        double pa = this.alpha[i] + ((this.alpha[i + 1] - this.alpha[i]) * f);
        if (pa <= 0)
        {
            return default;
        }

        double pr = this.red[i] + ((this.red[i + 1] - this.red[i]) * f);
        double pg = this.green[i] + ((this.green[i + 1] - this.green[i]) * f);
        double pb = this.blue[i] + ((this.blue[i + 1] - this.blue[i]) * f);
        return new Rgba32(this.Encode(pr / pa), this.Encode(pg / pa), this.Encode(pb / pa), ToByte(pa));
    }

    private double Decode(byte value)
    {
        double c = value / 255.0;
        if (!this.linearLight)
        {
            return c;
        }

        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private byte Encode(double value)
    {
        if (!this.linearLight)
        {
            return ToByte(value);
        }

        // Binary search for the number of thresholds at or below the value.
        int lo = 0, hi = LinearThresholds.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (value >= LinearThresholds[mid])
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return (byte)lo;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)((value * 255.0) + 0.5), 0, 255);

    private static double[] BuildThresholds()
    {
        var thresholds = new double[255];
        for (int k = 0; k < 255; k++)
        {
            double c = (k + 0.5) / 255.0;
            thresholds[k] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return thresholds;
    }
}
