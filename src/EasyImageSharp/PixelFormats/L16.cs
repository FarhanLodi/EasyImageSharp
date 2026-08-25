using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 16-bit grayscale (luminance) pixel. Colour sources are reduced to luminance with the
/// ITU-R BT.709 coefficients at full 16-bit precision.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct L16 : IPixel<L16>, IEquatable<L16>
{
    /// <summary>The luminance value.</summary>
    public ushort PackedValue;

    /// <summary>Creates a pixel with the given luminance.</summary>
    public L16(ushort luminance) => this.PackedValue = luminance;

    /// <inheritdoc/>
    public static L16 FromRgba32(Rgba32 source)
        => new(PixelComponent.ToUInt16(PixelComponent.Luminance(source.R / 255f, source.G / 255f, source.B / 255f)));

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32()
    {
        byte l = PixelComponent.From16To8(this.PackedValue);
        return new Rgba32(l, l, l);
    }

    /// <inheritdoc/>
    public static L16 FromScaledVector4(Vector4 source)
        => new(PixelComponent.ToUInt16(PixelComponent.Luminance(source.X, source.Y, source.Z)));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
    {
        float l = this.PackedValue / 65535f;
        return new Vector4(l, l, l, 1f);
    }

    /// <inheritdoc/>
    public readonly bool Equals(L16 other) => this.PackedValue == other.PackedValue;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is L16 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => this.PackedValue.GetHashCode();

    /// <inheritdoc/>
    public override readonly string ToString() => $"L16({this.PackedValue})";

    /// <summary>Compares two pixels for equality.</summary>
    public static bool operator ==(L16 left, L16 right) => left.Equals(right);

    /// <summary>Compares two pixels for inequality.</summary>
    public static bool operator !=(L16 left, L16 right) => !left.Equals(right);
}
