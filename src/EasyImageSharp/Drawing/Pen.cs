using System.Collections.ObjectModel;

namespace EasyImageSharp.Drawing;

/// <summary>
/// How outlines are stroked: the paint (a colour or a <see cref="Drawing.Brush"/>), the stroke thickness and,
/// for paths, polylines and polygons, the shape of joins and line ends and an optional dash pattern.
/// </summary>
/// <remarks>
/// <para>
/// A pen built from just a colour and a thickness draws exactly as pens always have: mitered joins (bevelled
/// beyond a miter limit of 4), butt caps, no dashes. Set <see cref="LineJoin"/>, <see cref="LineCap"/>,
/// <see cref="MiterLimit"/>, <see cref="DashPattern"/> and <see cref="DashOffset"/> with an object initializer,
/// for example <c>new Pen(Color.Red, 3) { LineCap = LineCap.Round, DashPattern = [6, 3] }</c>.
/// </para>
/// <para>
/// The stroke style applies to operations that stroke a path centred on it (<c>Draw</c> with a
/// <see cref="VectorPath"/>, <c>DrawLine</c>, <c>DrawLines</c>, <c>DrawPolygon</c>). <c>DrawRectangle</c>,
/// <c>DrawEllipse</c> and <c>DrawCircle</c> keep their band inside the shape's bounds; with a styled pen
/// (any join, cap or dash setting other than the defaults) they stroke the outline inset by half the
/// thickness, which lays the same band on the inside.
/// </para>
/// </remarks>
public readonly struct Pen : IEquatable<Pen>
{
    /// <summary>The miter limit a pen uses unless told otherwise, the same as SVG's default.</summary>
    public const float DefaultMiterLimit = 4f;

    private static readonly ReadOnlyCollection<float> NoDashes = new([]);

    private readonly float thickness;
    private readonly float miterLimit;
    private readonly ReadOnlyCollection<float>? dashPattern;
    private readonly float dashOffset;

    /// <summary>Initializes a pen that paints a solid colour.</summary>
    /// <param name="color">The stroke colour.</param>
    /// <param name="thickness">The stroke thickness in pixels; must be a positive finite number.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public Pen(Color color, float thickness = 1f)
    {
        RequireThickness(thickness);
        this.Color = color;
        this.thickness = thickness;
    }

    /// <summary>
    /// Initializes a pen of thickness 1 that paints its stroke with a brush (a gradient, a pattern, or a solid
    /// colour); set <see cref="Thickness"/> in an object initializer, for example
    /// <c>new Pen(gradient) { Thickness = 4 }</c>.
    /// </summary>
    /// <param name="brush">The brush that paints the stroke; it is shaded in image coordinates, like a fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is <see langword="null"/>.</exception>
    public Pen(Brush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        this.Brush = brush;
        this.Color = brush is SolidBrush solid ? solid.Color : default;
        this.thickness = 1f;
    }

    /// <summary>
    /// The stroke colour. For a pen created from a brush this is the brush's colour when it is a
    /// <see cref="SolidBrush"/> and transparent otherwise; the <see cref="Brush"/> is what paints.
    /// </summary>
    public Color Color { get; }

    /// <summary>The brush that paints the stroke, or <see langword="null"/> when the pen paints its solid <see cref="Color"/>.</summary>
    public Brush? Brush { get; }

    /// <summary>The stroke thickness in pixels; a default-constructed pen has thickness 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a positive finite number.</exception>
    public float Thickness
    {
        get => this.thickness > 0f ? this.thickness : 1f;
        init
        {
            RequireThickness(value);
            this.thickness = value;
        }
    }

    /// <summary>The shape of the corners where segments meet; defaults to <see cref="Drawing.LineJoin.Miter"/>.</summary>
    public LineJoin LineJoin { get; init; }

    /// <summary>The shape of the two ends of open figures and of every dash; defaults to <see cref="Drawing.LineCap.Butt"/>.</summary>
    public LineCap LineCap { get; init; }

    /// <summary>
    /// The longest a miter join may be, as a multiple of half the thickness (the SVG <c>stroke-miterlimit</c>):
    /// sharper corners are bevelled. Defaults to <see cref="DefaultMiterLimit"/>; values below 1 are treated as 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number.</exception>
    public float MiterLimit
    {
        get => this.miterLimit == 0f ? DefaultMiterLimit : this.miterLimit;
        init
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The miter limit must be a finite number.");
            }

            this.miterLimit = Math.Max(1f, value);
        }
    }

    /// <summary>
    /// Alternating dash and gap lengths in pixels, measured along the path as in SVG's <c>stroke-dasharray</c>:
    /// <c>[6, 3]</c> draws dashes 6 pixels long separated by 3-pixel gaps. An empty list (the default) draws a
    /// solid stroke. A list of odd length is repeated to make it even, as in SVG. Every
    /// dash gets the pen's <see cref="LineCap"/>, so with round or square caps a zero-length dash draws a dot.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An entry is negative or not finite, or the entries add up to zero (a pattern with no length).
    /// </exception>
    public IReadOnlyList<float> DashPattern
    {
        get => this.dashPattern ?? NoDashes;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0)
            {
                this.dashPattern = null;
                return;
            }

            float[] copy = new float[value.Count];
            double total = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                float d = value[i];
                if (!(d >= 0f) || !float.IsFinite(d))
                {
                    throw new ArgumentException("Dash lengths must be non-negative finite numbers.", nameof(value));
                }

                copy[i] = d;
                total += d;
            }

            if (!(total > 0))
            {
                throw new ArgumentException("A dash pattern must have a positive total length.", nameof(value));
            }

            this.dashPattern = new ReadOnlyCollection<float>(copy);
        }
    }

    /// <summary>
    /// How far into the <see cref="DashPattern"/> the stroke starts, in pixels (SVG's <c>stroke-dashoffset</c>);
    /// positive values shift the dashes back towards the start of the path. Ignored without a dash pattern.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number.</exception>
    public float DashOffset
    {
        get => this.dashOffset;
        init
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The dash offset must be a finite number.");
            }

            this.dashOffset = value;
        }
    }

    /// <summary>Whether the pen uses the default joins, caps, miter limit and no dashes, the only style older drawing code knew.</summary>
    internal bool HasDefaultStyle
        => this.LineJoin == LineJoin.Miter && this.LineCap == LineCap.Butt && this.MiterLimit == DefaultMiterLimit && this.dashPattern is null;

    /// <summary>The brush that paints the stroke: <see cref="Brush"/>, or a solid brush of <see cref="Color"/>.</summary>
    internal Brush EffectiveBrush => this.Brush ?? new SolidBrush(this.Color);

    /// <summary>The geometric part of the pen for <see cref="Stroker"/>, in pixels.</summary>
    internal StrokeStyle ToStrokeStyle(double tolerance)
    {
        double width = this.Thickness;
        double[]? dashes = null;
        if (this.dashPattern is ReadOnlyCollection<float> pattern)
        {
            int count = pattern.Count % 2 == 0 ? pattern.Count : pattern.Count * 2;
            dashes = new double[count];
            for (int i = 0; i < count; i++)
            {
                dashes[i] = pattern[i % pattern.Count];
            }
        }

        return new StrokeStyle(width / 2, this.LineJoin, this.LineCap, this.MiterLimit, dashes, this.DashOffset, tolerance);
    }

    /// <inheritdoc/>
    public bool Equals(Pen other)
        => this.Color.Equals(other.Color)
            && this.Thickness == other.Thickness
            && ReferenceEquals(this.Brush, other.Brush)
            && this.LineJoin == other.LineJoin
            && this.LineCap == other.LineCap
            && this.MiterLimit == other.MiterLimit
            && this.DashOffset == other.DashOffset
            && this.DashPattern.SequenceEqual(other.DashPattern);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Pen p && this.Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (this.Brush is null && this.HasDefaultStyle && this.DashOffset == 0f)
        {
            // Unchanged from plain colour pens, so their hash codes stay stable.
            return HashCode.Combine(this.Color, this.Thickness);
        }

        var hash = default(HashCode);
        hash.Add(this.Color);
        hash.Add(this.Thickness);
        hash.Add(this.Brush);
        hash.Add(this.LineJoin);
        hash.Add(this.LineCap);
        hash.Add(this.MiterLimit);
        hash.Add(this.DashOffset);
        foreach (float d in this.DashPattern)
        {
            hash.Add(d);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        if (this.Brush is null && this.HasDefaultStyle)
        {
            return FormattableString.Invariant($"Pen [ Color={this.Color}, Thickness={this.Thickness} ]");
        }

        string paint = this.Brush is null ? FormattableString.Invariant($"Color={this.Color}") : FormattableString.Invariant($"Brush={this.Brush}");
        string dashes = this.dashPattern is null ? string.Empty
            : FormattableString.Invariant($", DashPattern=[{string.Join(", ", this.DashPattern.Select(static d => d.ToString(System.Globalization.CultureInfo.InvariantCulture)))}], DashOffset={this.DashOffset}");
        return FormattableString.Invariant(
            $"Pen [ {paint}, Thickness={this.Thickness}, LineJoin={this.LineJoin}, LineCap={this.LineCap}, MiterLimit={this.MiterLimit}{dashes} ]");
    }

    /// <summary>Whether two pens are equal: same colour, brush instance, thickness and style.</summary>
    public static bool operator ==(Pen left, Pen right) => left.Equals(right);

    /// <summary>Whether two pens differ.</summary>
    public static bool operator !=(Pen left, Pen right) => !left.Equals(right);

    private static void RequireThickness(float thickness)
    {
        if (!(thickness > 0f) || !float.IsFinite(thickness))
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "Pen thickness must be a positive finite number.");
        }
    }
}
