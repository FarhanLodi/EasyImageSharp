using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Binary thresholding with selectable comparison metric and output colours.</summary>
internal static class ThresholdOps
{
    /// <summary>
    /// Sets every pixel of <paramref name="region"/> to <paramref name="upper"/> when its metric is at least
    /// <paramref name="threshold"/> (0-1) and to <paramref name="lower"/> otherwise.
    /// </summary>
    public static void BinaryThreshold<TPixel>(
        ImageFrame<TPixel> frame, Rectangle region, float threshold, Rgba32 upper, Rgba32 lower, BinaryThresholdMode mode)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        switch (mode)
        {
            case BinaryThresholdMode.Luminance:
            {
                // Same comparison as the classic luminance threshold: an 8-bit cutoff with round-half-up.
                byte cutoff = RowProcessor.ClampToByte(threshold * 255f);
                RowProcessor.ProcessPixels(frame, region, p => PixelOps.Luminance8(p) >= cutoff ? upper : lower);
                break;
            }

            case BinaryThresholdMode.Saturation:
                RowProcessor.ProcessPixels(frame, region, p => HslSaturation(p) >= threshold ? upper : lower);
                break;

            case BinaryThresholdMode.MaxChroma:
                RowProcessor.ProcessPixels(frame, region, p => MaxChroma(p) >= threshold ? upper : lower);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown binary threshold mode.");
        }
    }

    /// <summary>HSL saturation in 0-1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float HslSaturation(Rgba32 p)
    {
        float r = p.R / 255f;
        float g = p.G / 255f;
        float b = p.B / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float chroma = max - min;
        if (chroma <= 0f)
        {
            return 0f;
        }

        float lightness = (max + min) * 0.5f;
        return chroma / (1f - MathF.Abs((2f * lightness) - 1f));
    }

    /// <summary>
    /// The larger of |Cb| and |Cr| (BT.601 full-range YCbCr, chroma in -0.5..0.5) scaled by 2 so a fully
    /// saturated primary approaches 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float MaxChroma(Rgba32 p)
    {
        float r = p.R / 255f;
        float g = p.G / 255f;
        float b = p.B / 255f;
        float cb = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
        float cr = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
        return MathF.Min(1f, 2f * MathF.Max(MathF.Abs(cb), MathF.Abs(cr)));
    }
}
