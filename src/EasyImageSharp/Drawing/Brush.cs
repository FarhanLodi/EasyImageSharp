using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>
/// Decides the colour of every pixel a fill or stroke covers: a single colour (<see cref="SolidBrush"/>), a
/// gradient (<see cref="LinearGradientBrush"/>, <see cref="RadialGradientBrush"/>, <see cref="ConicGradientBrush"/>)
/// or a pattern taken from an image (<see cref="ImageBrush"/>).
/// </summary>
/// <remarks>
/// <para>
/// Brushes are immutable and may be shared between threads and reused across operations. Each brush is defined
/// in its own <em>brush space</em>; <see cref="Transform"/> maps brush space onto the image, so a gradient can be
/// rotated or an image pattern scaled without changing the shape it fills. Every pixel is shaded at its centre,
/// (x + 0.5, y + 0.5) in image coordinates.
/// </para>
/// <para>The set of brush types is closed: the library cannot be extended with new brush classes.</para>
/// </remarks>
public abstract class Brush
{
    private readonly Matrix3x2 transform = Matrix3x2.Identity;

    private protected Brush()
    {
    }

    /// <summary>
    /// The transform from brush space to image coordinates; the identity (the default) makes brush space and
    /// image space the same. A transform that cannot be inverted (a zero scale) makes the brush paint nothing.
    /// </summary>
    /// <exception cref="ArgumentException">A matrix element is not a finite number.</exception>
    public Matrix3x2 Transform
    {
        get => this.transform;
        init
        {
            if (!float.IsFinite(value.M11) || !float.IsFinite(value.M12) || !float.IsFinite(value.M21)
                || !float.IsFinite(value.M22) || !float.IsFinite(value.M31) || !float.IsFinite(value.M32))
            {
                throw new ArgumentException("Brush transform elements must be finite numbers.", nameof(value));
            }

            this.transform = value;
        }
    }

    /// <summary>Creates the per-operation shader, or returns <see langword="null"/> when the brush paints nothing.</summary>
    internal abstract BrushShader? CreateShader();

    /// <summary>The inverse of <see cref="Transform"/> in double precision, or <see langword="null"/> when it is singular.</summary>
    internal InverseTransform? GetInverseTransform() => InverseTransform.Create(this.transform);
}

/// <summary>A brush that paints every pixel with one colour.</summary>
public sealed class SolidBrush : Brush
{
    /// <summary>Initializes a brush of the given colour.</summary>
    public SolidBrush(Color color) => this.Color = color;

    /// <summary>The colour, including its alpha.</summary>
    public Color Color { get; }

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"SolidBrush [ Color={this.Color} ]");

    internal override BrushShader? CreateShader() => new SolidShader(this.Color.ToRgba32());

    private sealed class SolidShader : BrushShader
    {
        private readonly Rgba32 color;

        public SolidShader(Rgba32 color) => this.color = color;

        public override void Shade(int y, int x, Span<Rgba32> destination) => destination.Fill(this.color);
    }
}

/// <summary>Produces straight-alpha colours for a run of pixel centres.</summary>
internal abstract class BrushShader
{
    /// <summary>Writes the colour of pixels (<paramref name="x"/> + i, <paramref name="y"/>) for every i in <paramref name="destination"/>.</summary>
    public abstract void Shade(int y, int x, Span<Rgba32> destination);
}

/// <summary>Maps image coordinates back into brush space; a double-precision inverse of a <see cref="Matrix3x2"/>.</summary>
internal readonly struct InverseTransform
{
    private readonly double m11;
    private readonly double m12;
    private readonly double m21;
    private readonly double m22;
    private readonly double m31;
    private readonly double m32;

    private InverseTransform(double m11, double m12, double m21, double m22, double m31, double m32)
    {
        this.m11 = m11;
        this.m12 = m12;
        this.m21 = m21;
        this.m22 = m22;
        this.m31 = m31;
        this.m32 = m32;
    }

    /// <summary>Brush-space change per one-pixel step to the right in image space.</summary>
    public (double X, double Y) StepX => (this.m11, this.m12);

    public static InverseTransform? Create(Matrix3x2 matrix)
    {
        double a = matrix.M11, b = matrix.M12, c = matrix.M21, d = matrix.M22, e = matrix.M31, f = matrix.M32;
        double det = (a * d) - (b * c);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12)
        {
            return null;
        }

        return new InverseTransform(
            d / det,
            -b / det,
            -c / det,
            a / det,
            ((c * f) - (d * e)) / det,
            ((b * e) - (a * f)) / det);
    }

    /// <summary>The brush-space point that image point (<paramref name="x"/>, <paramref name="y"/>) comes from.</summary>
    public (double X, double Y) Map(double x, double y)
        => ((x * this.m11) + (y * this.m21) + this.m31, (x * this.m12) + (y * this.m22) + this.m32);
}
