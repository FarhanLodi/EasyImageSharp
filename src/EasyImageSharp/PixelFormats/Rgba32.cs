using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>A 32-bit pixel: 8 bits each for red, green, blue and alpha, stored in R, G, B, A order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba32 : IPixel<Rgba32>, IEquatable<Rgba32>
{
    /// <summary>The red component.</summary>
    public byte R;

    /// <summary>The green component.</summary>
    public byte G;

    /// <summary>The blue component.</summary>
    public byte B;

    /// <summary>The alpha component; 0 is fully transparent and 255 fully opaque.</summary>
    public byte A;

    /// <summary>Creates an opaque pixel from the given colour components.</summary>
    public Rgba32(byte r, byte g, byte b)
        : this(r, g, b, byte.MaxValue)
    {
    }

    /// <summary>Creates a pixel from the given colour and alpha components.</summary>
    public Rgba32(byte r, byte g, byte b, byte a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>A fully transparent black pixel.</summary>
    public static Rgba32 Transparent => default;

    /// <summary>An opaque black pixel.</summary>
    public static Rgba32 Black => new(0, 0, 0);

    /// <summary>An opaque white pixel.</summary>
    public static Rgba32 White => new(255, 255, 255);

    /// <inheritdoc/>
    public static Rgba32 FromRgba32(Rgba32 source) => source;

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => this;

    /// <inheritdoc/>
    public static Rgba32 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToByte(source.X),
            PixelComponent.ToByte(source.Y),
            PixelComponent.ToByte(source.Z),
            PixelComponent.ToByte(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new Vector4(this.R, this.G, this.B, this.A) / 255f;

    /// <inheritdoc/>
    public readonly bool Equals(Rgba32 other)
        => this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba32 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgba32({this.R}, {this.G}, {this.B}, {this.A})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);
}
