using System.Runtime.CompilerServices;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// Scalar component conversions shared by the pixel formats: widening and narrowing between 8- and
/// 16-bit components, and rounding of normalised (0-1) floating point components.
/// </summary>
internal static class PixelComponent
{
    /// <summary>The BT.709 red luma coefficient.</summary>
    public const float LumaR = 0.2126f;

    /// <summary>The BT.709 green luma coefficient.</summary>
    public const float LumaG = 0.7152f;

    /// <summary>The BT.709 blue luma coefficient.</summary>
    public const float LumaB = 0.0722f;

    /// <summary>
    /// Widens an 8-bit component to 16 bits by bit replication, so that 0 maps to 0x0000,
    /// 0x01 maps to 0x0101 and 0xFF maps to 0xFFFF.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort From8To16(byte value) => (ushort)(value * 257);

    /// <summary>
    /// Narrows a 16-bit component to 8 bits, rounding to the nearest representable value. This is
    /// equivalent to <c>round(value * 255 / 65535)</c>: 0x0000 becomes 0, 0x0101 becomes 1 and
    /// 0xFFFF becomes 255. A plain <c>value &gt;&gt; 8</c> would truncate instead and lose a half step.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte From16To8(ushort value) => (byte)(((value * 255) + 32895) >> 16);

    /// <summary>Clamps a normalised component to the 0-1 range, mapping NaN to 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp01(float value) => value >= 0f ? (value <= 1f ? value : 1f) : 0f;

    /// <summary>Rounds a normalised (0-1) component to an 8-bit value, clamping out-of-range input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(float value) => (byte)((Clamp01(value) * 255f) + 0.5f);

    /// <summary>Rounds a normalised (0-1) component to a 16-bit value, clamping out-of-range input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ToUInt16(float value) => (ushort)((Clamp01(value) * 65535f) + 0.5f);

    /// <summary>Computes the BT.709 luma of normalised (0-1) colour components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luminance(float r, float g, float b) => (r * LumaR) + (g * LumaG) + (b * LumaB);
}
