using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 48-bit pixel: 16 bits each for red, green and blue, stored in R, G, B order. Converting to and
/// from 8-bit formats rounds to the nearest value; conversions to and from other formats with at
/// least 16 bits per component keep the full precision.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb48 : IPixel<Rgb48>, IEquatable<Rgb48>
{
    /// <summary>The red component.</summary>
    public ushort R;

    /// <summary>The green component.</summary>
    public ushort G;

    /// <summary>The blue component.</summary>
    public ushort B;

    /// <summary>Creates a pixel from the given colour components.</summary>
    public Rgb48(ushort r, ushort g, ushort b)
    {
        this.R = r;
        this.G = g;
        this.B = b;
    }

    /// <inheritdoc/>
    public static Rgb48 FromRgba32(Rgba32 source)
        => new(
            PixelComponent.From8To16(source.R),
            PixelComponent.From8To16(source.G),
            PixelComponent.From8To16(source.B));

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32()
        => new(
            PixelComponent.From16To8(this.R),
            PixelComponent.From16To8(this.G),
            PixelComponent.From16To8(this.B));

    /// <inheritdoc/>
    public static Rgb48 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToUInt16(source.X),
            PixelComponent.ToUInt16(source.Y),
            PixelComponent.ToUInt16(source.Z));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
        => new(this.R / 65535f, this.G / 65535f, this.B / 65535f, 1f);

    /// <inheritdoc/>
    public readonly bool Equals(Rgb48 other) => this.R == other.R && this.G == other.G && this.B == other.B;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgb48 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.R, this.G, this.B);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgb48({this.R}, {this.G}, {this.B})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Rgb48 left, Rgb48 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Rgb48 left, Rgb48 right) => !left.Equals(right);
}
