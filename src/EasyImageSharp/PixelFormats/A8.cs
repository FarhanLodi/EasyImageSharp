using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// An 8-bit pixel holding an alpha component only. Colour is not stored: converting to a colour
/// format yields black with the stored alpha.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct A8 : IPixel<A8>, IEquatable<A8>
{
    /// <summary>The alpha component; 0 is fully transparent and 255 fully opaque.</summary>
    public byte PackedValue;

    /// <summary>Creates a pixel with the given alpha.</summary>
    public A8(byte alpha) => this.PackedValue = alpha;

    /// <inheritdoc/>
    public static A8 FromRgba32(Rgba32 source) => new(source.A);

    /// <inheritdoc/>
    public readonly Rgba32 ToRgba32() => new(0, 0, 0, this.PackedValue);

    /// <inheritdoc/>
    public static A8 FromScaledVector4(Vector4 source) => new(PixelComponent.ToByte(source.W));

    /// <inheritdoc/>
    public readonly Vector4 ToScaledVector4() => new(0f, 0f, 0f, this.PackedValue / 255f);

    /// <inheritdoc/>
    public readonly bool Equals(A8 other) => this.PackedValue == other.PackedValue;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is A8 p && this.Equals(p);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => this.PackedValue.GetHashCode();

    /// <inheritdoc/>
    public override readonly string ToString() => $"A8({this.PackedValue})";

    /// <summary>Compares two pixels for equality.</summary>
    public static bool operator ==(A8 left, A8 right) => left.Equals(right);

    /// <summary>Compares two pixels for inequality.</summary>
    public static bool operator !=(A8 left, A8 right) => !left.Equals(right);
}
