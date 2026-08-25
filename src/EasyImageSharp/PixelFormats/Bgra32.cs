using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>A 32-bit pixel: 8 bits each for blue, green, red and alpha, stored in B, G, R, A order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Bgra32 : IPixel<Bgra32>, IEquatable<Bgra32>
{
    /// <summary>The blue component.</summary>
    public byte B;

    /// <summary>The green component.</summary>
    public byte G;

    /// <summary>The red component.</summary>
    public byte R;

    /// <summary>The alpha component; 0 is fully transparent and 255 fully opaque.</summary>
    public byte A;

    /// <summary>Creates an opaque pixel from the given colour components.</summary>
    public Bgra32(byte r, byte g, byte b)
        : this(r, g, b, byte.MaxValue)
    {
    }

    /// <summary>Creates a pixel from the given colour and alpha components.</summary>
    public Bgra32(byte r, byte g, byte b, byte a)
    {
        this.B = b;
        this.G = g;
        this.R = r;
        this.A = a;
    }

    /// <inheritdoc/>
    public static Bgra32 FromRgba32(Rgba32 source) => new(source.R, source.G, source.B, source.A);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public static Bgra32 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToByte(source.X),
            PixelComponent.ToByte(source.Y),
            PixelComponent.ToByte(source.Z),
            PixelComponent.ToByte(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new Vector4(this.R, this.G, this.B, this.A) / 255f;

    /// <inheritdoc/>
    public readonly bool Equals(Bgra32 other)
        => this.B == other.B && this.G == other.G && this.R == other.R && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Bgra32 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.B, this.G, this.R, this.A);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Bgra32({this.B}, {this.G}, {this.R}, {this.A})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Bgra32 left, Bgra32 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Bgra32 left, Bgra32 right) => !left.Equals(right);
}
