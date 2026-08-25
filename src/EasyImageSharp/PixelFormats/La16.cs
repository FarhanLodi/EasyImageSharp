using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 16-bit pixel holding an 8-bit luminance component followed by an 8-bit alpha component;
/// the layout PNG calls "grayscale with alpha" at bit depth 8.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct La16 : IPixel<La16>, IEquatable<La16>
{
    /// <summary>The luminance component.</summary>
    public byte L;

    /// <summary>The alpha component; 0 is fully transparent and 255 fully opaque.</summary>
    public byte A;

    /// <summary>Creates an opaque pixel with the given luminance.</summary>
    public La16(byte luminance)
        : this(luminance, byte.MaxValue)
    {
    }

    /// <summary>Creates a pixel with the given luminance and alpha.</summary>
    public La16(byte luminance, byte alpha)
    {
        this.L = luminance;
        this.A = alpha;
    }

    /// <inheritdoc/>
    public static La16 FromRgba32(Rgba32 source) => new(L8.FromRgba32(source).PackedValue, source.A);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(this.L, this.L, this.L, this.A);

    /// <inheritdoc/>
    public static La16 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToByte(PixelComponent.Luminance(source.X, source.Y, source.Z)),
            PixelComponent.ToByte(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
    {
        float l = this.L / 255f;
        return new Vector4(l, l, l, this.A / 255f);
    }

    /// <inheritdoc/>
    public readonly bool Equals(La16 other) => this.L == other.L && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is La16 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.L, this.A);

    /// <inheritdoc/>
    public override readonly string ToString() => $"La16({this.L}, {this.A})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(La16 left, La16 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(La16 left, La16 right) => !left.Equals(right);
}
