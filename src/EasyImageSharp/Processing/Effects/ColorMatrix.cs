using System.Numerics;
using System.Runtime.CompilerServices;

namespace EasyImageSharp.Processing;

/// <summary>
/// A 5x4 colour transformation matrix operating on straight (non-premultiplied) RGBA values in the
/// range 0-1. Colours are treated as row vectors <c>[R G B A 1]</c> that are multiplied by the matrix, so
/// row <c>n</c> holds the contribution of input channel <c>n</c> (1 = red, 2 = green, 3 = blue, 4 = alpha) to
/// each output channel, and the fifth row (<see cref="M51"/>..<see cref="M54"/>) is a constant offset:
/// <c>R' = R*M11 + G*M21 + B*M31 + A*M41 + M51</c>, and likewise for the other output channels.
/// </summary>
public struct ColorMatrix : IEquatable<ColorMatrix>
{
    /// <summary>Contribution of input red to output red.</summary>
    public float M11;

    /// <summary>Contribution of input red to output green.</summary>
    public float M12;

    /// <summary>Contribution of input red to output blue.</summary>
    public float M13;

    /// <summary>Contribution of input red to output alpha.</summary>
    public float M14;

    /// <summary>Contribution of input green to output red.</summary>
    public float M21;

    /// <summary>Contribution of input green to output green.</summary>
    public float M22;

    /// <summary>Contribution of input green to output blue.</summary>
    public float M23;

    /// <summary>Contribution of input green to output alpha.</summary>
    public float M24;

    /// <summary>Contribution of input blue to output red.</summary>
    public float M31;

    /// <summary>Contribution of input blue to output green.</summary>
    public float M32;

    /// <summary>Contribution of input blue to output blue.</summary>
    public float M33;

    /// <summary>Contribution of input blue to output alpha.</summary>
    public float M34;

    /// <summary>Contribution of input alpha to output red.</summary>
    public float M41;

    /// <summary>Contribution of input alpha to output green.</summary>
    public float M42;

    /// <summary>Contribution of input alpha to output blue.</summary>
    public float M43;

    /// <summary>Contribution of input alpha to output alpha.</summary>
    public float M44;

    /// <summary>Constant offset added to output red.</summary>
    public float M51;

    /// <summary>Constant offset added to output green.</summary>
    public float M52;

    /// <summary>Constant offset added to output blue.</summary>
    public float M53;

    /// <summary>Constant offset added to output alpha.</summary>
    public float M54;

    /// <summary>Initializes a matrix from all twenty components, given row by row.</summary>
    public ColorMatrix(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44,
        float m51, float m52, float m53, float m54)
    {
        this.M11 = m11;
        this.M12 = m12;
        this.M13 = m13;
        this.M14 = m14;
        this.M21 = m21;
        this.M22 = m22;
        this.M23 = m23;
        this.M24 = m24;
        this.M31 = m31;
        this.M32 = m32;
        this.M33 = m33;
        this.M34 = m34;
        this.M41 = m41;
        this.M42 = m42;
        this.M43 = m43;
        this.M44 = m44;
        this.M51 = m51;
        this.M52 = m52;
        this.M53 = m53;
        this.M54 = m54;
    }

    /// <summary>The identity matrix, which leaves colours unchanged.</summary>
    public static ColorMatrix Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Whether this matrix equals <see cref="Identity"/>.</summary>
    public readonly bool IsIdentity => this.Equals(Identity);

    /// <summary>
    /// Multiplies two matrices. Because colours are row vectors, <c>a * b</c> is the transform that applies
    /// <paramref name="a"/> first and then <paramref name="b"/>.
    /// </summary>
    public static ColorMatrix Multiply(in ColorMatrix a, in ColorMatrix b)
    {
        ColorMatrix result = default;

        result.M11 = (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31) + (a.M14 * b.M41);
        result.M12 = (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32) + (a.M14 * b.M42);
        result.M13 = (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33) + (a.M14 * b.M43);
        result.M14 = (a.M11 * b.M14) + (a.M12 * b.M24) + (a.M13 * b.M34) + (a.M14 * b.M44);

        result.M21 = (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31) + (a.M24 * b.M41);
        result.M22 = (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32) + (a.M24 * b.M42);
        result.M23 = (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33) + (a.M24 * b.M43);
        result.M24 = (a.M21 * b.M14) + (a.M22 * b.M24) + (a.M23 * b.M34) + (a.M24 * b.M44);

        result.M31 = (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31) + (a.M34 * b.M41);
        result.M32 = (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32) + (a.M34 * b.M42);
        result.M33 = (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33) + (a.M34 * b.M43);
        result.M34 = (a.M31 * b.M14) + (a.M32 * b.M24) + (a.M33 * b.M34) + (a.M34 * b.M44);

        result.M41 = (a.M41 * b.M11) + (a.M42 * b.M21) + (a.M43 * b.M31) + (a.M44 * b.M41);
        result.M42 = (a.M41 * b.M12) + (a.M42 * b.M22) + (a.M43 * b.M32) + (a.M44 * b.M42);
        result.M43 = (a.M41 * b.M13) + (a.M42 * b.M23) + (a.M43 * b.M33) + (a.M44 * b.M43);
        result.M44 = (a.M41 * b.M14) + (a.M42 * b.M24) + (a.M43 * b.M34) + (a.M44 * b.M44);

        result.M51 = (a.M51 * b.M11) + (a.M52 * b.M21) + (a.M53 * b.M31) + (a.M54 * b.M41) + b.M51;
        result.M52 = (a.M51 * b.M12) + (a.M52 * b.M22) + (a.M53 * b.M32) + (a.M54 * b.M42) + b.M52;
        result.M53 = (a.M51 * b.M13) + (a.M52 * b.M23) + (a.M53 * b.M33) + (a.M54 * b.M43) + b.M53;
        result.M54 = (a.M51 * b.M14) + (a.M52 * b.M24) + (a.M53 * b.M34) + (a.M54 * b.M44) + b.M54;

        return result;
    }

    /// <summary>Returns the transform that applies this matrix first and then <paramref name="next"/>.</summary>
    public readonly ColorMatrix Concat(in ColorMatrix next) => Multiply(this, next);

    /// <summary>Multiplies two matrices; see <see cref="Multiply"/>.</summary>
    public static ColorMatrix operator *(ColorMatrix left, ColorMatrix right) => Multiply(left, right);

    /// <summary>Adds two matrices component-wise.</summary>
    public static ColorMatrix operator +(ColorMatrix left, ColorMatrix right) => new(
        left.M11 + right.M11, left.M12 + right.M12, left.M13 + right.M13, left.M14 + right.M14,
        left.M21 + right.M21, left.M22 + right.M22, left.M23 + right.M23, left.M24 + right.M24,
        left.M31 + right.M31, left.M32 + right.M32, left.M33 + right.M33, left.M34 + right.M34,
        left.M41 + right.M41, left.M42 + right.M42, left.M43 + right.M43, left.M44 + right.M44,
        left.M51 + right.M51, left.M52 + right.M52, left.M53 + right.M53, left.M54 + right.M54);

    /// <summary>Subtracts two matrices component-wise.</summary>
    public static ColorMatrix operator -(ColorMatrix left, ColorMatrix right) => new(
        left.M11 - right.M11, left.M12 - right.M12, left.M13 - right.M13, left.M14 - right.M14,
        left.M21 - right.M21, left.M22 - right.M22, left.M23 - right.M23, left.M24 - right.M24,
        left.M31 - right.M31, left.M32 - right.M32, left.M33 - right.M33, left.M34 - right.M34,
        left.M41 - right.M41, left.M42 - right.M42, left.M43 - right.M43, left.M44 - right.M44,
        left.M51 - right.M51, left.M52 - right.M52, left.M53 - right.M53, left.M54 - right.M54);

    /// <summary>Scales every component by <paramref name="scalar"/>.</summary>
    public static ColorMatrix operator *(ColorMatrix matrix, float scalar) => new(
        matrix.M11 * scalar, matrix.M12 * scalar, matrix.M13 * scalar, matrix.M14 * scalar,
        matrix.M21 * scalar, matrix.M22 * scalar, matrix.M23 * scalar, matrix.M24 * scalar,
        matrix.M31 * scalar, matrix.M32 * scalar, matrix.M33 * scalar, matrix.M34 * scalar,
        matrix.M41 * scalar, matrix.M42 * scalar, matrix.M43 * scalar, matrix.M44 * scalar,
        matrix.M51 * scalar, matrix.M52 * scalar, matrix.M53 * scalar, matrix.M54 * scalar);

    /// <summary>Whether two matrices are component-wise equal.</summary>
    public static bool operator ==(ColorMatrix left, ColorMatrix right) => left.Equals(right);

    /// <summary>Whether two matrices differ in any component.</summary>
    public static bool operator !=(ColorMatrix left, ColorMatrix right) => !left.Equals(right);

    /// <summary>
    /// Transforms a straight RGBA colour (components in 0-1) by this matrix without clamping the result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector4 Transform(Vector4 color) => new(
        (color.X * this.M11) + (color.Y * this.M21) + (color.Z * this.M31) + (color.W * this.M41) + this.M51,
        (color.X * this.M12) + (color.Y * this.M22) + (color.Z * this.M32) + (color.W * this.M42) + this.M52,
        (color.X * this.M13) + (color.Y * this.M23) + (color.Z * this.M33) + (color.W * this.M43) + this.M53,
        (color.X * this.M14) + (color.Y * this.M24) + (color.Z * this.M34) + (color.W * this.M44) + this.M54);

    /// <inheritdoc/>
    public readonly bool Equals(ColorMatrix other)
        => this.M11 == other.M11 && this.M12 == other.M12 && this.M13 == other.M13 && this.M14 == other.M14
        && this.M21 == other.M21 && this.M22 == other.M22 && this.M23 == other.M23 && this.M24 == other.M24
        && this.M31 == other.M31 && this.M32 == other.M32 && this.M33 == other.M33 && this.M34 == other.M34
        && this.M41 == other.M41 && this.M42 == other.M42 && this.M43 == other.M43 && this.M44 == other.M44
        && this.M51 == other.M51 && this.M52 == other.M52 && this.M53 == other.M53 && this.M54 == other.M54;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is ColorMatrix m && this.Equals(m);

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(this.M11);
        hash.Add(this.M12);
        hash.Add(this.M13);
        hash.Add(this.M14);
        hash.Add(this.M21);
        hash.Add(this.M22);
        hash.Add(this.M23);
        hash.Add(this.M24);
        hash.Add(this.M31);
        hash.Add(this.M32);
        hash.Add(this.M33);
        hash.Add(this.M34);
        hash.Add(this.M41);
        hash.Add(this.M42);
        hash.Add(this.M43);
        hash.Add(this.M44);
        hash.Add(this.M51);
        hash.Add(this.M52);
        hash.Add(this.M53);
        hash.Add(this.M54);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
        => $"{{ {{M11:{this.M11} M12:{this.M12} M13:{this.M13} M14:{this.M14}}} "
         + $"{{M21:{this.M21} M22:{this.M22} M23:{this.M23} M24:{this.M24}}} "
         + $"{{M31:{this.M31} M32:{this.M32} M33:{this.M33} M34:{this.M34}}} "
         + $"{{M41:{this.M41} M42:{this.M42} M43:{this.M43} M44:{this.M44}}} "
         + $"{{M51:{this.M51} M52:{this.M52} M53:{this.M53} M54:{this.M54}}} }}";
}
