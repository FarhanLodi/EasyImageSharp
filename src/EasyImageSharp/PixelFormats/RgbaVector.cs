using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 128-bit pixel holding four 32-bit floating point components in R, G, B, A order, normalised so
/// that 0 is black and 1 is full intensity. Values outside the 0-1 range are stored unchanged, which
/// makes the format suitable as an intermediate for high dynamic range and out-of-gamut work; the
/// clamp only happens when converting to a format backed by integer components.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RgbaVector : IPixel<RgbaVector>, IEquatable<RgbaVector>
{
    /// <summary>The red component.</summary>
    public float R;

    /// <summary>The green component.</summary>
    public float G;

    /// <summary>The blue component.</summary>
    public float B;

    /// <summary>The alpha component; 0 is fully transparent and 1 fully opaque.</summary>
    public float A;

    /// <summary>Creates an opaque pixel from the given colour components.</summary>
    public RgbaVector(float r, float g, float b)
        : this(r, g, b, 1f)
    {
    }

    /// <summary>Creates a pixel from the given colour and alpha components.</summary>
    public RgbaVector(float r, float g, float b, float a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <inheritdoc/>
    public static RgbaVector FromRgba32(Rgba32 source)
        => new(source.R / 255f, source.G / 255f, source.B / 255f, source.A / 255f);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32()
        => new(
            PixelComponent.ToByte(this.R),
            PixelComponent.ToByte(this.G),
            PixelComponent.ToByte(this.B),
            PixelComponent.ToByte(this.A));

    /// <inheritdoc/>
    /// <remarks>Components are stored verbatim; values outside the 0-1 range are preserved.</remarks>
    public static RgbaVector FromScaledVector4(Vector4 source) => new(source.X, source.Y, source.Z, source.W);

    /// <inheritdoc/>
    /// <remarks>Components are returned verbatim; values outside the 0-1 range are preserved.</remarks>
    public readonly Vector4 ToScaledVector4() => new(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public readonly bool Equals(RgbaVector other)
        => this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is RgbaVector p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public override readonly string ToString()
        => FormattableString.Invariant($"RgbaVector({this.R}, {this.G}, {this.B}, {this.A})");

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(RgbaVector left, RgbaVector right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(RgbaVector left, RgbaVector right) => !left.Equals(right);
}
