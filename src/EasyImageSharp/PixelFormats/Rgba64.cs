using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 64-bit pixel: 16 bits each for red, green, blue and alpha, stored in R, G, B, A order.
/// Converting to and from 8-bit formats rounds to the nearest value; conversions to and from other
/// formats with at least 16 bits per component keep the full precision.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba64 : IPixel<Rgba64>, IEquatable<Rgba64>
{
    /// <summary>The red component.</summary>
    public ushort R;

    /// <summary>The green component.</summary>
    public ushort G;

    /// <summary>The blue component.</summary>
    public ushort B;

    /// <summary>The alpha component; 0 is fully transparent and 65535 fully opaque.</summary>
    public ushort A;

    /// <summary>Creates an opaque pixel from the given colour components.</summary>
    public Rgba64(ushort r, ushort g, ushort b)
        : this(r, g, b, ushort.MaxValue)
    {
    }

    /// <summary>Creates a pixel from the given colour and alpha components.</summary>
    public Rgba64(ushort r, ushort g, ushort b, ushort a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <inheritdoc/>
    public static Rgba64 FromRgba32(Rgba32 source)
        => new(
            PixelComponent.From8To16(source.R),
            PixelComponent.From8To16(source.G),
            PixelComponent.From8To16(source.B),
            PixelComponent.From8To16(source.A));

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32()
        => new(
            PixelComponent.From16To8(this.R),
            PixelComponent.From16To8(this.G),
            PixelComponent.From16To8(this.B),
            PixelComponent.From16To8(this.A));

    /// <inheritdoc/>
    public static Rgba64 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToUInt16(source.X),
            PixelComponent.ToUInt16(source.Y),
            PixelComponent.ToUInt16(source.Z),
            PixelComponent.ToUInt16(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
        => new Vector4(this.R, this.G, this.B, this.A) / 65535f;

    /// <inheritdoc/>
    public readonly bool Equals(Rgba64 other)
        => this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba64 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.R, this.G, this.B, this.A);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgba64({this.R}, {this.G}, {this.B}, {this.A})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Rgba64 left, Rgba64 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Rgba64 left, Rgba64 right) => !left.Equals(right);
}
