using System.Numerics;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>
/// Blends a processed copy of a frame back into the frame through a coverage mask: where the mask is 255 the
/// processed pixel replaces the original, where it is 0 the original stays, and in between the two are
/// interpolated with premultiplied alpha (so a transparent pixel contributes no colour).
/// </summary>
internal static class ClipComposite
{
    public static void Apply<TPixel>(ImageFrame<TPixel> destination, ImageFrame<TPixel> processed, CoverageMask mask)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (mask.IsEmpty)
        {
            return;
        }

        Rectangle r = mask.Bounds;
        for (int y = r.Y; y < r.Y + r.Height; y++)
        {
            if (((y - r.Y) & 63) == 0)
            {
                OperationExecution.Checkpoint();
            }

            ReadOnlySpan<byte> m = mask.GetRow(y);
            Span<TPixel> dst = destination.GetRowSpan(y).Slice(r.X, r.Width);
            ReadOnlySpan<TPixel> src = processed.GetRowSpan(y).Slice(r.X, r.Width);
            if (typeof(TPixel) == typeof(Rgba32))
            {
                BlendRgba32(MemoryMarshal.Cast<TPixel, Rgba32>(dst), MemoryMarshal.Cast<TPixel, Rgba32>(src), m);
            }
            else if (typeof(TPixel) == typeof(Rgb24))
            {
                BlendRgb24(MemoryMarshal.Cast<TPixel, Rgb24>(dst), MemoryMarshal.Cast<TPixel, Rgb24>(src), m);
            }
            else
            {
                BlendGeneric(dst, src, m);
            }
        }
    }

    private static void BlendRgba32(Span<Rgba32> dst, ReadOnlySpan<Rgba32> src, ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int m = mask[i];
            if (m == 0)
            {
                continue;
            }

            Rgba32 s = src[i];
            if (m == 255)
            {
                dst[i] = s;
                continue;
            }

            Rgba32 d = dst[i];
            if (s.A == 255 && d.A == 255)
            {
                dst[i] = new Rgba32(PixelBlend.Mix(s.R, d.R, m), PixelBlend.Mix(s.G, d.G, m), PixelBlend.Mix(s.B, d.B, m), 255);
                continue;
            }

            float t = m / 255f;
            float wd = d.A * (1f - t);
            float ws = s.A * t;
            float a = wd + ws;
            if (a <= 0f)
            {
                dst[i] = Rgba32.Transparent;
                continue;
            }

            dst[i] = new Rgba32(
                ToByte(((d.R * wd) + (s.R * ws)) / a),
                ToByte(((d.G * wd) + (s.G * ws)) / a),
                ToByte(((d.B * wd) + (s.B * ws)) / a),
                ToByte(a));
        }
    }

    private static void BlendRgb24(Span<Rgb24> dst, ReadOnlySpan<Rgb24> src, ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int m = mask[i];
            if (m == 0)
            {
                continue;
            }

            Rgb24 s = src[i];
            if (m == 255)
            {
                dst[i] = s;
                continue;
            }

            ref Rgb24 d = ref dst[i];
            d.R = PixelBlend.Mix(s.R, d.R, m);
            d.G = PixelBlend.Mix(s.G, d.G, m);
            d.B = PixelBlend.Mix(s.B, d.B, m);
        }
    }

    private static void BlendGeneric<TPixel>(Span<TPixel> dst, ReadOnlySpan<TPixel> src, ReadOnlySpan<byte> mask)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int m = mask[i];
            if (m == 0)
            {
                continue;
            }

            if (m == 255)
            {
                dst[i] = src[i];
                continue;
            }

            // Scaled vectors keep the precision of wide formats; premultiply, interpolate, unpremultiply.
            float t = m / 255f;
            Vector4 d = dst[i].ToScaledVector4();
            Vector4 s = src[i].ToScaledVector4();
            float wd = d.W * (1f - t);
            float ws = s.W * t;
            float a = wd + ws;
            Vector4 result = a > 0f
                ? new Vector4(((d.X * wd) + (s.X * ws)) / a, ((d.Y * wd) + (s.Y * ws)) / a, ((d.Z * wd) + (s.Z * ws)) / a, a)
                : Vector4.Zero;
            dst[i] = TPixel.FromScaledVector4(result);
        }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
