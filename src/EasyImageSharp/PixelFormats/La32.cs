using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// A 32-bit pixel holding a 16-bit luminance component followed by a 16-bit alpha component;
/// the layout PNG calls "grayscale with alpha" at bit depth 16.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct La32 : IPixel<La32>, IEquatable<La32>
{
    /// <summary>The luminance component.</summary>
    public ushort L;

    /// <summary>The alpha component; 0 is fully transparent and 65535 fully opaque.</summary>
    public ushort A;

    /// <summary>Creates an opaque pixel with the given luminance.</summary>
    public La32(ushort luminance)
        : this(luminance, ushort.MaxValue)
    {
    }

    /// <summary>Creates a pixel with the given luminance and alpha.</summary>
    public La32(ushort luminance, ushort alpha)
    {
        this.L = luminance;
        this.A = alpha;
    }

    /// <inheritdoc/>
    public static La32 FromRgba32(Rgba32 source)
        => new(
            PixelComponent.ToUInt16(PixelComponent.Luminance(source.R / 255f, source.G / 255f, source.B / 255f)),
            PixelComponent.From8To16(source.A));

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32()
    {
        byte l = PixelComponent.From16To8(this.L);
        return new Rgba32(l, l, l, PixelComponent.From16To8(this.A));
    }

    /// <inheritdoc/>
    public static La32 FromScaledVector4(Vector4 source)
        => new(
            PixelComponent.ToUInt16(PixelComponent.Luminance(source.X, source.Y, source.Z)),
            PixelComponent.ToUInt16(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4()
    {
        float l = this.L / 65535f;
        return new Vector4(l, l, l, this.A / 65535f);
    }

    /// <inheritdoc/>
    public readonly bool Equals(La32 other) => this.L == other.L && this.A == other.A;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is La32 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(this.L, this.A);

    /// <inheritdoc/>
    public override readonly string ToString() => $"La32({this.L}, {this.A})";

    /// <summary>Compares two pixels for component-wise equality.</summary>
    public static bool operator ==(La32 left, La32 right) => left.Equals(right);

    /// <summary>Compares two pixels for component-wise inequality.</summary>
    public static bool operator !=(La32 left, La32 right) => !left.Equals(right);
}
