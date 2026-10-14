using System.Numerics;

namespace EasyImageSharp.ColorSpaces;

/// <summary>
/// Converts floating-point colors without gamut clipping. RGB is encoded sRGB; XYZ and Lab use D65,
/// XYZ white has Y=1, Lab lightness has range 0-100, and hue angles are in degrees.
/// HSV/HSL saturation, value and lightness have range 0-1. Alpha is handled by the caller.
/// </summary>
public static class ColorSpaceConverter
{
    /// <summary>Decodes sRGB transfer functions to linear-light RGB, retaining extended-range components.</summary>
    public static Vector3 SrgbToLinear(Vector3 rgb)
    {
        Finite(rgb);
        return new(Decode(rgb.X), Decode(rgb.Y), Decode(rgb.Z));
    }

    /// <summary>Encodes linear-light RGB as sRGB, retaining extended-range components.</summary>
    public static Vector3 LinearToSrgb(Vector3 rgb)
    {
        Finite(rgb);
        return new(Encode(rgb.X), Encode(rgb.Y), Encode(rgb.Z));
    }

    /// <summary>Converts encoded sRGB to D65 XYZ with Y=1 for white.</summary>
    public static Vector3 SrgbToXyz(Vector3 rgb)
    {
        Vector3 v = SrgbToLinear(rgb);
        return new((0.4123908f * v.X) + (0.35758434f * v.Y) + (0.1804808f * v.Z),
            (0.212639f * v.X) + (0.7151687f * v.Y) + (0.07219232f * v.Z),
            (0.01933082f * v.X) + (0.11919478f * v.Y) + (0.95053215f * v.Z));
    }

    /// <summary>Converts D65 XYZ to encoded sRGB without clipping out-of-gamut channels.</summary>
    public static Vector3 XyzToSrgb(Vector3 xyz)
    {
        Finite(xyz);
        return LinearToSrgb(new((3.24096994f * xyz.X) - (1.53738318f * xyz.Y) - (0.49861076f * xyz.Z),
            (-0.96924364f * xyz.X) + (1.8759675f * xyz.Y) + (0.04155506f * xyz.Z),
            (0.05563008f * xyz.X) - (0.20397696f * xyz.Y) + (1.05697151f * xyz.Z)));
    }

    /// <summary>Converts D65 XYZ to D65 CIELAB (L*, a*, b*).</summary>
    public static Vector3 XyzToLab(Vector3 xyz)
    {
        Finite(xyz);
        float x = LabForward(xyz.X / 0.950455927f), y = LabForward(xyz.Y), z = LabForward(xyz.Z / 1.089057751f);
        return new((116 * y) - 16, 500 * (x - y), 200 * (y - z));
    }

    /// <summary>Converts D65 CIELAB to D65 XYZ.</summary>
    public static Vector3 LabToXyz(Vector3 lab)
    {
        Finite(lab);
        float y = (lab.X + 16) / 116;
        return new(0.950455927f * LabInverse(y + (lab.Y / 500)), LabInverse(y), 1.089057751f * LabInverse(y - (lab.Z / 200)));
    }

    /// <summary>Converts CIELAB to L*, chroma, hue degrees. Achromatic hue is zero.</summary>
    public static Vector3 LabToLch(Vector3 lab)
    {
        Finite(lab);
        return new(lab.X, MathF.Sqrt((lab.Y * lab.Y) + (lab.Z * lab.Z)), Hue(MathF.Atan2(lab.Z, lab.Y) * 180 / MathF.PI));
    }

    /// <summary>Converts L*, chroma, hue degrees to CIELAB.</summary>
    public static Vector3 LchToLab(Vector3 lch)
    {
        Finite(lch);
        if (lch.Y < 0) throw new ArgumentOutOfRangeException(nameof(lch), "Chroma must be nonnegative.");
        float radians = Hue(lch.Z) * MathF.PI / 180;
        return new(lch.X, lch.Y * MathF.Cos(radians), lch.Y * MathF.Sin(radians));
    }

    /// <summary>Converts unit-range encoded RGB to hue degrees, saturation, value.</summary>
    public static Vector3 SrgbToHsv(Vector3 rgb)
    {
        UnitRgb(rgb);
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)), min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float delta = max - min;
        return new(RgbHue(rgb, max, delta), max == 0 ? 0 : delta / max, max);
    }

    /// <summary>Converts hue degrees, saturation, value to encoded RGB.</summary>
    public static Vector3 HsvToSrgb(Vector3 hsv)
    {
        HueInput(hsv);
        float c = hsv.Y * hsv.Z;
        return HueRgb(hsv.X, c) + new Vector3(hsv.Z - c);
    }

    /// <summary>Converts unit-range encoded RGB to hue degrees, saturation, lightness.</summary>
    public static Vector3 SrgbToHsl(Vector3 rgb)
    {
        UnitRgb(rgb);
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)), min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float delta = max - min, lightness = (max + min) / 2;
        return new(RgbHue(rgb, max, delta), delta == 0 ? 0 : delta / (1 - MathF.Abs((2 * lightness) - 1)), lightness);
    }

    /// <summary>Converts hue degrees, saturation, lightness to encoded RGB.</summary>
    public static Vector3 HslToSrgb(Vector3 hsl)
    {
        HueInput(hsl);
        float c = (1 - MathF.Abs((2 * hsl.Z) - 1)) * hsl.Y;
        return HueRgb(hsl.X, c) + new Vector3(hsl.Z - (c / 2));
    }

    /// <summary>Bradford chromatic adaptation from D65 XYZ to D50 XYZ.</summary>
    public static Vector3 XyzD65ToD50(Vector3 xyz)
    {
        Finite(xyz);
        return new((1.0479298f * xyz.X) + (0.02294679f * xyz.Y) - (0.05019223f * xyz.Z),
            (0.02962781f * xyz.X) + (0.9904345f * xyz.Y) - (0.01707383f * xyz.Z),
            (-0.00924304f * xyz.X) + (0.01505519f * xyz.Y) + (0.7518743f * xyz.Z));
    }

    /// <summary>Bradford chromatic adaptation from D50 XYZ to D65 XYZ.</summary>
    public static Vector3 XyzD50ToD65(Vector3 xyz)
    {
        Finite(xyz);
        return new((0.9554734f * xyz.X) - (0.02309854f * xyz.Y) + (0.06325931f * xyz.Z),
            (-0.02836971f * xyz.X) + (1.0099955f * xyz.Y) + (0.0210414f * xyz.Z),
            (0.012314f * xyz.X) - (0.0205077f * xyz.Y) + (1.3303659f * xyz.Z));
    }

    private static float Decode(float v) => MathF.CopySign(MathF.Abs(v) <= 0.04045f ? MathF.Abs(v) / 12.92f : MathF.Pow((MathF.Abs(v) + 0.055f) / 1.055f, 2.4f), v);
    private static float Encode(float v) => MathF.CopySign(MathF.Abs(v) <= 0.0031308f ? MathF.Abs(v) * 12.92f : (1.055f * MathF.Pow(MathF.Abs(v), 1 / 2.4f)) - 0.055f, v);
    private static float LabForward(float v) => v > 216f / 24389 ? MathF.Cbrt(v) : (((24389f / 27) * v) + 16) / 116;
    private static float LabInverse(float v) => v > 6f / 29 ? v * v * v : ((116 * v) - 16) * 27 / 24389;
    private static float Hue(float v) => ((v % 360) + 360) % 360;
    private static float RgbHue(Vector3 v, float max, float delta) => delta == 0 ? 0 : Hue(60 * (max == v.X ? (v.Y - v.Z) / delta : max == v.Y ? ((v.Z - v.X) / delta) + 2 : ((v.X - v.Y) / delta) + 4));
    private static Vector3 HueRgb(float hue, float c)
    {
        float h = Hue(hue) / 60, x = c * (1 - MathF.Abs((h % 2) - 1));
        return h switch { < 1 => new(c, x, 0), < 2 => new(x, c, 0), < 3 => new(0, c, x), < 4 => new(0, x, c), < 5 => new(x, 0, c), _ => new(c, 0, x) };
    }
    private static void Finite(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new ArgumentOutOfRangeException(nameof(v), "Components must be finite.");
    }
    private static void UnitRgb(Vector3 v)
    {
        Finite(v);
        if (v.X < 0 || v.X > 1 || v.Y < 0 || v.Y > 1 || v.Z < 0 || v.Z > 1) throw new ArgumentOutOfRangeException(nameof(v), "RGB components must be in 0-1.");
    }
    private static void HueInput(Vector3 v)
    {
        Finite(v);
        if (v.Y < 0 || v.Y > 1 || v.Z < 0 || v.Z > 1) throw new ArgumentOutOfRangeException(nameof(v), "Saturation, value and lightness must be in 0-1.");
    }
}
