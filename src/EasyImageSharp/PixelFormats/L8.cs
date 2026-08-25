using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>An 8-bit grayscale (luminance) pixel.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct L8 : IPixel<L8>, IEquatable<L8>
{
    /// <summary>The luminance value.</summary>
    public byte PackedValue;

    /// <summary>Creates a pixel with the given luminance.</summary>
    public L8(byte luminance) => this.PackedValue = luminance;

    /// <inheritdoc/>
    public static L8 FromRgba32(Rgba32 source)
    {
        // ITU-R BT.709 luma coefficients.
        int l = (int)((source.R * 0.2126f) + (source.G * 0.7152f) + (source.B * 0.0722f) + 0.5f);
        return new L8((byte)Math.Clamp(l, 0, 255));
    }

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.PackedValue, this.PackedValue, this.PackedValue);

    /// <inheritdoc/>
    public static L8 FromScaledVector4(Vector4 source)
        => new(PixelComponent.ToByte(PixelComponent.Luminance(source.X, source.Y, source.Z)));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
    {
        float l = this.PackedValue / 255f;
        return new Vector4(l, l, l, 1f);
    }

    /// <inheritdoc/>
    public readonly bool Equals(L8 other) => this.PackedValue == other.PackedValue;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is L8 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => this.PackedValue.GetHashCode();

    /// <inheritdoc/>
    public override readonly string ToString() => $"L8({this.PackedValue})";

    /// <summary>Compares two pixels for equality.</summary>
    public static bool operator ==(L8 left, L8 right) => left.Equals(right);

    /// <summary>Compares two pixels for inequality.</summary>
    public static bool operator !=(L8 left, L8 right) => !left.Equals(right);
}
