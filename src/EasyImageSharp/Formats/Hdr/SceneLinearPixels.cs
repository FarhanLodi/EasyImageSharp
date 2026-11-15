using System.Numerics;
using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Hdr;

/// <summary>
/// The pixel policy shared by the high dynamic range codecs (Radiance HDR and OpenEXR), which store
/// scene-linear light rather than display-encoded values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoding.</b> <see cref="RgbaVector"/> receives the file's linear values verbatim: nothing is clamped,
/// no transfer function is applied, and values above 1, negative values, infinities and NaNs survive exactly
/// as stored. Every other pixel format is backed by integer components and, by the library's convention,
/// holds sRGB-encoded values, so the colour components are clamped to 0..1 (NaN becomes 0), encoded with the
/// IEC 61966-2-1 sRGB transfer function and then rounded to the format's precision; alpha is clamped but
/// not transfer-encoded. That is a plain clip of everything brighter than 1 — apply a tone-mapping operator to
/// an <see cref="RgbaVector"/> image first when the highlights matter.
/// </para>
/// <para>
/// <b>Encoding.</b> The mirror image: <see cref="RgbaVector"/> components are written verbatim as linear
/// values, and every other pixel format is taken to be sRGB-encoded and is linearised with the inverse
/// transfer function before it is written. An 8-bit image therefore round-trips through a half-float EXR file
/// unchanged.
/// </para>
/// </remarks>
internal static class SceneLinearPixels
{
    /// <summary>
    /// True when <typeparamref name="TPixel"/> stores linear values verbatim. Folds to a constant at JIT time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLinear<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(RgbaVector);

    /// <summary>Packs one linear RGBA value into <typeparamref name="TPixel"/> under the decoding policy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel FromLinear<TPixel>(Vector4 linear)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (IsLinear<TPixel>())
        {
            return TPixel.FromScaledVector4(linear);
        }

        return TPixel.FromScaledVector4(new Vector4(
            EncodeSrgb(linear.X),
            EncodeSrgb(linear.Y),
            EncodeSrgb(linear.Z),
            Clamp01(linear.W)));
    }

    /// <summary>Unpacks one pixel into linear RGBA under the encoding policy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ToLinear<TPixel>(TPixel pixel)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Vector4 v = pixel.ToScaledVector4();
        if (IsLinear<TPixel>())
        {
            return v;
        }

        return new Vector4(DecodeSrgb(v.X), DecodeSrgb(v.Y), DecodeSrgb(v.Z), v.W);
    }

    /// <summary>Converts a row of linear values into <paramref name="destination"/> under the decoding policy.</summary>
    public static void FromLinear<TPixel>(ReadOnlySpan<Vector4> source, Span<TPixel> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = FromLinear<TPixel>(source[i]);
        }
    }

    /// <summary>Converts a row of pixels into linear values under the encoding policy.</summary>
    public static void ToLinear<TPixel>(ReadOnlySpan<TPixel> source, Span<Vector4> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = ToLinear(source[i]);
        }
    }

    /// <summary>
    /// The sRGB opto-electronic transfer function (IEC 61966-2-1) applied to a value clamped to 0..1; NaN maps
    /// to 0. The same piecewise definition, in the same single precision, as
    /// <see cref="ColorSpaces.ColorSpaceConverter.LinearToSrgb"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EncodeSrgb(float linear)
    {
        float v = Clamp01(linear);
        return v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
    }

    /// <summary>The inverse sRGB transfer function for an encoded value in 0..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeSrgb(float encoded)
    {
        float v = Clamp01(encoded);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Clamps to 0..1, mapping NaN to 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp01(float value)
        => value > 0f ? (value < 1f ? value : 1f) : 0f;
}
