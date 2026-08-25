using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>A 32-bit pixel: 8 bits each for alpha, red, green and blue, stored in A, R, G, B order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Argb32 : IPixel<Argb32>, IEquatable<Argb32>
{
    /// <summary>The alpha component; 0 is fully transparent and 255 fully opaque.</summary>
    public byte A;

    /// <summary>The red component.</summary>
    public byte R;

    /// <summary>The green component.</summary>
    public byte G;

    /// <summary>The blue component.</summary>
    public byte B;

    /// <summary>Creates an opaque pixel from the given colour components.</summary>
    public Argb32(byte r, byte g, byte b)
        : this(r, g, b, byte.MaxValue)
    {
    }

    /// <summary>Creates a pixel from the given colour and alpha components.</summary>
    public Argb32(byte r, byte g, byte b, byte a)
    {
        this.A = a;
        this.R = r;
        this.G = g;
        this.B = b;
    }

    /// <inheritdoc/>
    public static Argb32 FromRgba32(Rgba32 source) => new(source.R, source.G, source.B, source.A);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public static Argb32 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToByte(source.X),
            PixelComponent.ToByte(source.Y),
            PixelComponent.ToByte(source.Z),
            PixelComponent.ToByte(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new Vector4(this.R, this.G, this.B, this.A) / 255f;

    /// <inheritdoc/>
    public readonly bool Equals(Argb32 other)
        => this.A == other.A && this.R == other.R && this.G == other.G && this.B == other.B;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Argb32 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.A, this.R, this.G, this.B);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Argb32({this.A}, {this.R}, {this.G}, {this.B})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Argb32 left, Argb32 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Argb32 left, Argb32 right) => !left.Equals(right);
}
