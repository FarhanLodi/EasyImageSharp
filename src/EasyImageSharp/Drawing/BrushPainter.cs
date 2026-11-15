using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>
/// Blends a brush into a frame using per-pixel coverage: the brush is shaded for each covered run, then every
/// pixel is composited source-over with <c>alpha = coverage * brush alpha * opacity</c>. The compositing
/// arithmetic is the same as <see cref="FramePainter{TPixel}"/>'s, so a brush that shades one colour everywhere
/// produces the same pixels as painting that colour.
/// </summary>
internal readonly struct BrushPainter<TPixel> : ICoverageSink
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly ImageFrame<TPixel> frame;
    private readonly BrushShader shader;
    private readonly Rgba32[] colors;
    private readonly float opacity;

    public BrushPainter(ImageFrame<TPixel> frame, BrushShader shader, float opacity)
    {
        this.frame = frame;
        this.shader = shader;
        this.colors = new Rgba32[Math.Max(1, frame.Width)];
        this.opacity = Math.Clamp(opacity, 0f, 1f);
    }

    /// <summary>Whether the opacity leaves every pixel unchanged.</summary>
    public bool IsNoOp => this.opacity == 0f;

    public void Blend(int y, int x, ReadOnlySpan<byte> coverage)
    {
        Span<Rgba32> source = this.colors.AsSpan(0, coverage.Length);
        this.shader.Shade(y, x, source);
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Span<Rgba32> row = MemoryMarshal.Cast<TPixel, Rgba32>(this.frame.GetRowSpan(y)).Slice(x, coverage.Length);
            for (int i = 0; i < row.Length; i++)
            {
                Rgba32 s = source[i];
                int a = this.Alpha(coverage[i], s.A);
                if (a != 0)
                {
                    row[i] = PixelBlend.Over(row[i], s, a);
                }
            }
        }
        else if (typeof(TPixel) == typeof(Rgb24))
        {
            Span<Rgb24> row = MemoryMarshal.Cast<TPixel, Rgb24>(this.frame.GetRowSpan(y)).Slice(x, coverage.Length);
            for (int i = 0; i < row.Length; i++)
            {
                Rgba32 s = source[i];
                int a = this.Alpha(coverage[i], s.A);
                if (a == 0)
                {
                    continue;
                }

                ref Rgb24 d = ref row[i];
                if (a == 255)
                {
                    d = new Rgb24(s.R, s.G, s.B);
                }
                else
                {
                    d.R = PixelBlend.Mix(s.R, d.R, a);
                    d.G = PixelBlend.Mix(s.G, d.G, a);
                    d.B = PixelBlend.Mix(s.B, d.B, a);
                }
            }
        }
        else
        {
            Span<TPixel> row = this.frame.GetRowSpan(y).Slice(x, coverage.Length);
            for (int i = 0; i < row.Length; i++)
            {
                Rgba32 s = source[i];
                int a = this.Alpha(coverage[i], s.A);
                if (a != 0)
                {
                    row[i] = TPixel.FromRgba32(a == 255 ? new Rgba32(s.R, s.G, s.B, 255) : PixelBlend.Over(row[i].ToRgba32(), s, a));
                }
            }
        }
    }

    /// <summary>The effective 0-255 alpha, rounded exactly like <see cref="FramePainter{TPixel}"/>'s table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Alpha(byte coverage, byte sourceAlpha)
    {
        if (coverage == 0 || sourceAlpha == 0)
        {
            return 0;
        }

        // The same expression, evaluated in the same precision, as the painter's per-colour table.
        double scale = sourceAlpha * this.opacity / 255.0;
        return Math.Clamp((int)Math.Round(coverage * scale), 0, 255);
    }
}

/// <summary>The source-over arithmetic shared by the brush painter (identical to <see cref="FramePainter{TPixel}"/>'s).</summary>
internal static class PixelBlend
{
    /// <summary>Composites colour <paramref name="s"/> (its own alpha already folded into <paramref name="a"/>) over <paramref name="d"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 Over(Rgba32 d, Rgba32 s, int a)
    {
        if (a == 255)
        {
            return new Rgba32(s.R, s.G, s.B, 255);
        }

        if (d.A == 255)
        {
            return new Rgba32(Mix(s.R, d.R, a), Mix(s.G, d.G, a), Mix(s.B, d.B, a), 255);
        }

        float sa = a / 255f;
        float dw = d.A / 255f * (1f - sa);
        float outA = sa + dw;
        if (outA <= 0f)
        {
            return Rgba32.Transparent;
        }

        return new Rgba32(
            ClampByte(((s.R * sa) + (d.R * dw)) / outA),
            ClampByte(((s.G * sa) + (d.G * dw)) / outA),
            ClampByte(((s.B * sa) + (d.B * dw)) / outA),
            ClampByte(outA * 255f));
    }

    /// <summary>Linear interpolation <c>(src * a + dst * (255 - a)) / 255</c> rounded to nearest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Mix(int src, int dst, int a) => (byte)(((src * a) + (dst * (255 - a)) + 127) / 255);

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
