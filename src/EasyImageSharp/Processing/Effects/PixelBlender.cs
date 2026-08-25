using System.Numerics;
using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Blends and composites <see cref="Rgba32"/> pixels following the W3C Compositing and Blending Level 1
/// specification: the source colour is first combined with the backdrop colour by the chosen
/// <see cref="PixelColorBlendingMode"/> (weighted by the backdrop alpha), then the two are composited with the
/// chosen Porter-Duff <see cref="PixelAlphaCompositionMode"/> on premultiplied colour, and the result is
/// converted back to straight alpha. Colours are handled as 0-1 floats and rounded once at the end.
/// </summary>
public static class PixelBlender
{
    private const float LumR = 0.3f;
    private const float LumG = 0.59f;
    private const float LumB = 0.11f;

    /// <summary>
    /// Blends a single source pixel over a backdrop pixel. <paramref name="opacity"/> (0-1) scales the source
    /// alpha before compositing.
    /// </summary>
    public static Rgba32 Blend(
        Rgba32 backdrop,
        Rgba32 source,
        float opacity,
        PixelColorBlendingMode colorBlending = PixelColorBlendingMode.Normal,
        PixelAlphaCompositionMode alphaComposition = PixelAlphaCompositionMode.SrcOver)
    {
        Vector4 b = RowProcessor.ToUnitVector(backdrop);
        Vector4 s = RowProcessor.ToUnitVector(source);
        s.W *= Math.Clamp(opacity, 0f, 1f);
        return RowProcessor.FromUnitVector(BlendUnit(b, s, colorBlending, alphaComposition));
    }

    /// <summary>
    /// Blends a row of source pixels over a row of backdrop pixels into <paramref name="destination"/>, which
    /// may alias <paramref name="backdrop"/>. <paramref name="opacity"/> (0-1) scales the source alpha.
    /// </summary>
    public static void Blend(
        Span<Rgba32> destination,
        ReadOnlySpan<Rgba32> backdrop,
        ReadOnlySpan<Rgba32> source,
        float opacity,
        PixelColorBlendingMode colorBlending = PixelColorBlendingMode.Normal,
        PixelAlphaCompositionMode alphaComposition = PixelAlphaCompositionMode.SrcOver)
    {
        if (backdrop.Length != source.Length || destination.Length != source.Length)
        {
            throw new ArgumentException("Backdrop, source and destination rows must have the same length.");
        }

        opacity = Math.Clamp(opacity, 0f, 1f);
        bool plainSourceOver = colorBlending == PixelColorBlendingMode.Normal && alphaComposition == PixelAlphaCompositionMode.SrcOver;
        for (int i = 0; i < source.Length; i++)
        {
            Rgba32 sp = source[i];
            if (plainSourceOver)
            {
                // Fast paths for the overwhelmingly common case.
                if (sp.A == 0 || opacity == 0f)
                {
                    destination[i] = backdrop[i];
                    continue;
                }

                if (sp.A == byte.MaxValue && opacity == 1f)
                {
                    destination[i] = sp;
                    continue;
                }
            }

            Vector4 b = RowProcessor.ToUnitVector(backdrop[i]);
            Vector4 s = RowProcessor.ToUnitVector(sp);
            s.W *= opacity;
            destination[i] = RowProcessor.FromUnitVector(BlendUnit(b, s, colorBlending, alphaComposition));
        }
    }

    /// <summary>
    /// Blends a straight-alpha source colour over a straight-alpha backdrop colour (both 0-1) and returns
    /// the straight-alpha result without rounding.
    /// </summary>
    public static Vector4 BlendUnit(Vector4 backdrop, Vector4 source, PixelColorBlendingMode colorBlending, PixelAlphaCompositionMode alphaComposition)
    {
        float ab = backdrop.W;
        float asrc = source.W;

        // Blend function, weighted by the backdrop alpha: Cs' = (1 - ab) Cs + ab B(Cb, Cs).
        Vector4 blended = colorBlending == PixelColorBlendingMode.Normal
            ? source
            : Vector4.Lerp(source, BlendColor(backdrop, source, colorBlending), ab);

        // Porter-Duff coefficients.
        float fa;
        float fb;
        switch (alphaComposition)
        {
            case PixelAlphaCompositionMode.SrcOver:
                fa = 1f;
                fb = 1f - asrc;
                break;
            case PixelAlphaCompositionMode.Src:
                fa = 1f;
                fb = 0f;
                break;
            case PixelAlphaCompositionMode.SrcAtop:
                fa = ab;
                fb = 1f - asrc;
                break;
            case PixelAlphaCompositionMode.SrcIn:
                fa = ab;
                fb = 0f;
                break;
            case PixelAlphaCompositionMode.SrcOut:
                fa = 1f - ab;
                fb = 0f;
                break;
            case PixelAlphaCompositionMode.Dest:
                fa = 0f;
                fb = 1f;
                break;
            case PixelAlphaCompositionMode.DestOver:
                fa = 1f - ab;
                fb = 1f;
                break;
            case PixelAlphaCompositionMode.DestAtop:
                fa = 1f - ab;
                fb = asrc;
                break;
            case PixelAlphaCompositionMode.DestIn:
                fa = 0f;
                fb = asrc;
                break;
            case PixelAlphaCompositionMode.DestOut:
                fa = 0f;
                fb = 1f - asrc;
                break;
            case PixelAlphaCompositionMode.Clear:
                fa = 0f;
                fb = 0f;
                break;
            case PixelAlphaCompositionMode.Xor:
                fa = 1f - ab;
                fb = 1f - asrc;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(alphaComposition), alphaComposition, "Unknown alpha composition mode.");
        }

        float ws = asrc * fa;
        float wb = ab * fb;
        float ao = ws + wb;
        if (ao <= 0f)
        {
            return Vector4.Zero;
        }

        // Premultiplied composite, then back to straight alpha.
        Vector4 co = (blended * ws) + (backdrop * wb);
        Vector4 result = co / ao;
        result.W = ao;
        return result;
    }

    /// <summary>Applies the blend function B(Cb, Cs) to straight 0-1 colours; the returned W component is unspecified.</summary>
    public static Vector4 BlendColor(Vector4 backdrop, Vector4 source, PixelColorBlendingMode mode) => mode switch
    {
        PixelColorBlendingMode.Normal => source,
        PixelColorBlendingMode.Multiply => backdrop * source,
        PixelColorBlendingMode.Add => Vector4.Min(backdrop + source, Vector4.One),
        PixelColorBlendingMode.Subtract => Vector4.Max(backdrop - source, Vector4.Zero),
        PixelColorBlendingMode.Screen => backdrop + source - (backdrop * source),
        PixelColorBlendingMode.Darken => Vector4.Min(backdrop, source),
        PixelColorBlendingMode.Lighten => Vector4.Max(backdrop, source),
        PixelColorBlendingMode.Overlay => new Vector4(HardLight(source.X, backdrop.X), HardLight(source.Y, backdrop.Y), HardLight(source.Z, backdrop.Z), 0f),
        PixelColorBlendingMode.HardLight => new Vector4(HardLight(backdrop.X, source.X), HardLight(backdrop.Y, source.Y), HardLight(backdrop.Z, source.Z), 0f),
        PixelColorBlendingMode.SoftLight => new Vector4(SoftLight(backdrop.X, source.X), SoftLight(backdrop.Y, source.Y), SoftLight(backdrop.Z, source.Z), 0f),
        PixelColorBlendingMode.ColorDodge => new Vector4(ColorDodge(backdrop.X, source.X), ColorDodge(backdrop.Y, source.Y), ColorDodge(backdrop.Z, source.Z), 0f),
        PixelColorBlendingMode.ColorBurn => new Vector4(ColorBurn(backdrop.X, source.X), ColorBurn(backdrop.Y, source.Y), ColorBurn(backdrop.Z, source.Z), 0f),
        PixelColorBlendingMode.Difference => Vector4.Abs(backdrop - source),
        PixelColorBlendingMode.Exclusion => backdrop + source - (2f * backdrop * source),
        PixelColorBlendingMode.Hue => SetLum(SetSat(source, Sat(backdrop)), Lum(backdrop)),
        PixelColorBlendingMode.Saturation => SetLum(SetSat(backdrop, Sat(source)), Lum(backdrop)),
        PixelColorBlendingMode.Color => SetLum(source, Lum(backdrop)),
        PixelColorBlendingMode.Luminosity => SetLum(backdrop, Lum(source)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown colour blending mode."),
    };

    // ----- Separable helpers (W3C compositing spec) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HardLight(float cb, float cs)
        => cs <= 0.5f ? cb * 2f * cs : Screen(cb, (2f * cs) - 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Screen(float cb, float cs) => cb + cs - (cb * cs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftLight(float cb, float cs)
    {
        if (cs <= 0.5f)
        {
            return cb - ((1f - (2f * cs)) * cb * (1f - cb));
        }

        float d = cb <= 0.25f ? ((((16f * cb) - 12f) * cb) + 4f) * cb : MathF.Sqrt(cb);
        return cb + (((2f * cs) - 1f) * (d - cb));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ColorDodge(float cb, float cs)
    {
        if (cb <= 0f)
        {
            return 0f;
        }

        if (cs >= 1f)
        {
            return 1f;
        }

        return MathF.Min(1f, cb / (1f - cs));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ColorBurn(float cb, float cs)
    {
        if (cb >= 1f)
        {
            return 1f;
        }

        if (cs <= 0f)
        {
            return 0f;
        }

        return 1f - MathF.Min(1f, (1f - cb) / cs);
    }

    // ----- Non-separable helpers (W3C compositing spec) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lum(Vector4 c) => (LumR * c.X) + (LumG * c.Y) + (LumB * c.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Sat(Vector4 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    private static Vector4 ClipColor(Vector4 c)
    {
        float l = Lum(c);
        float n = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float x = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var lv = new Vector4(l, l, l, 0f);
        if (n < 0f)
        {
            c = lv + ((c - lv) * (l / (l - n)));
        }

        if (x > 1f)
        {
            c = lv + ((c - lv) * ((1f - l) / (x - l)));
        }

        return c;
    }

    private static Vector4 SetLum(Vector4 c, float l)
    {
        float d = l - Lum(c);
        return ClipColor(new Vector4(c.X + d, c.Y + d, c.Z + d, 0f));
    }

    private static Vector4 SetSat(Vector4 c, float s)
    {
        // Order the components, scale the mid value between min and max, set max to s and min to 0.
        float r = c.X;
        float g = c.Y;
        float b = c.Z;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        if (max <= min)
        {
            return new Vector4(0f, 0f, 0f, 0f);
        }

        float range = max - min;
        return new Vector4(Scale(r), Scale(g), Scale(b), 0f);

        float Scale(float v)
        {
            if (v == max)
            {
                return s;
            }

            if (v == min)
            {
                return 0f;
            }

            return (v - min) * s / range;
        }
    }
}
