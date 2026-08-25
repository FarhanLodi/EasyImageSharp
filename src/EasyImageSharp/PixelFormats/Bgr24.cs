using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>A 24-bit pixel: 8 bits each for blue, green and red, stored in B, G, R order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Bgr24 : IPixel<Bgr24>, IEquatable<Bgr24>
{
    /// <summary>The blue component.</summary>
    public byte B;

    /// <summary>The green component.</summary>
    public byte G;

    /// <summary>The red component.</summary>
    public byte R;

    /// <summary>Creates a pixel from the given colour components.</summary>
    public Bgr24(byte r, byte g, byte b)
    {
        this.B = b;
        this.G = g;
        this.R = r;
    }

    /// <inheritdoc/>
    public static Bgr24 FromRgba32(Rgba32 source) => new(source.R, source.G, source.B);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.R, this.G, this.B);

    /// <inheritdoc/>
    public static Bgr24 FromScaledVector4(Vector4 source)
        => new(PixelComponent.ToByte(source.X), PixelComponent.ToByte(source.Y), PixelComponent.ToByte(source.Z));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new(this.R / 255f, this.G / 255f, this.B / 255f, 1f);

    /// <inheritdoc/>
    public readonly bool Equals(Bgr24 other) => this.B == other.B && this.G == other.G && this.R == other.R;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Bgr24 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.B, this.G, this.R);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Bgr24({this.B}, {this.G}, {this.R})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(Bgr24 left, Bgr24 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(Bgr24 left, Bgr24 right) => !left.Equals(right);
}
