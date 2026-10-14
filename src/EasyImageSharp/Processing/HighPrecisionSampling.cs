using System.Numerics;

namespace EasyImageSharp.Processing;

internal static class HighPrecisionSampling
{
    internal static Vector4 Load(Vector4 value, bool premultiply, bool compand = false)
    {
        if (compand)
            value = new Vector4(Decode(value.X), Decode(value.Y), Decode(value.Z), value.W);
        return premultiply ? new Vector4(value.X * value.W, value.Y * value.W, value.Z * value.W, value.W) : value;
    }

    internal static Vector4 Store(Vector4 value, bool premultiply, bool compand = false)
    {
        if (premultiply)
            value = value.W > 0f ? new Vector4(value.X / value.W, value.Y / value.W, value.Z / value.W, value.W) : Vector4.Zero;
        return compand ? new Vector4(Encode(value.X), Encode(value.Y), Encode(value.Z), value.W) : value;
    }

    private static float Decode(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static float Encode(float value) => value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;
}
