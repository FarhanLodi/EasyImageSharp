using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>A 24-bit pixel: 8 bits each for red, green and blue, stored in R, G, B order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb24 : IPixel<Rgb24>, IEquatable<Rgb24>
{
    /// <summary>The red component.</summary>
    public byte R;

    /// <summary>The green component.</summary>
    public byte G;

    /// <summary>The blue component.</summary>
    public byte B;

    /// <summary>Creates a pixel from the given colour components.</summary>
    public Rgb24(byte r, byte g, byte b)
    {
        this.R = r;
        this.G = g;
        this.B = b;
    }

    /// <inheritdoc/>
    public static Rgb24 FromRgba32(Rgba32 source) => new(source.R, source.G, source.B);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.R, this.G, this.B);

    /// <inheritdoc/>
    public static Rgb24 FromScaledVector4(Vector4 source)
        => new(PixelComponent.ToByte(source.X), PixelComponent.ToByte(source.Y), PixelComponent.ToByte(source.Z));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new(this.R / 255f, this.G / 255f, this.B / 255f, 1f);

    /// <inheritdoc/>
    public readonly bool Equals(Rgb24 other) => this.R == other.R && this.G == other.G && this.B == other.B;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgb24 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.R, this.G, this.B);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgb24({this.R}, {this.G}, {this.B})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Rgb24 left, Rgb24 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Rgb24 left, Rgb24 right) => !left.Equals(right);
}
