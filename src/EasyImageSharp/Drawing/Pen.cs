namespace EasyImageSharp.Drawing;

/// <summary>A colour and stroke thickness pair for outline drawing operations.</summary>
public readonly struct Pen : IEquatable<Pen>
{
    private readonly float thickness;

    /// <summary>Initializes a pen.</summary>
    /// <param name="color">The stroke colour.</param>
    /// <param name="thickness">The stroke thickness in pixels; must be a positive finite number.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public Pen(Color color, float thickness = 1f)
    {
        if (!(thickness > 0f) || !float.IsFinite(thickness))
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "Pen thickness must be a positive finite number.");
        }

        this.Color = color;
        this.thickness = thickness;
    }

    /// <summary>The stroke colour.</summary>
    public Color Color { get; }

    /// <summary>The stroke thickness in pixels; a default-constructed pen has thickness 1.</summary>
    public float Thickness => this.thickness > 0f ? this.thickness : 1f;

    /// <inheritdoc/>
    public bool Equals(Pen other) => this.Color.Equals(other.Color) && this.Thickness == other.Thickness;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Pen p && this.Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Color, this.Thickness);

    /// <inheritdoc/>
    public override string ToString()
        => FormattableString.Invariant($"Pen [ Color={this.Color}, Thickness={this.Thickness} ]");

    /// <summary>Whether two pens are equal.</summary>
    public static bool operator ==(Pen left, Pen right) => left.Equals(right);

    /// <summary>Whether two pens differ.</summary>
    public static bool operator !=(Pen left, Pen right) => !left.Equals(right);
}
