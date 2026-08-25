using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>
/// Blends a single colour into a frame using per-pixel coverage: <c>alpha = coverage * colour alpha *
/// opacity</c>, composited source-over. Fast paths for <see cref="Rgba32"/> and <see cref="Rgb24"/>; every
/// other pixel format goes through its <see cref="Rgba32"/> conversion.
/// </summary>
internal readonly struct FramePainter<TPixel> : ICoverageSink
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly ImageFrame<TPixel> frame;
    private readonly byte[] alphaLut;
    private readonly byte r;
    private readonly byte g;
    private readonly byte b;

    public FramePainter(ImageFrame<TPixel> frame, Color color, float opacity)
    {
        this.frame = frame;
        this.r = color.R;
        this.g = color.G;
        this.b = color.B;
        this.alphaLut = new byte[256];
        double scale = color.A * Math.Clamp(opacity, 0f, 1f) / 255.0;
        for (int i = 1; i < 256; i++)
        {
            this.alphaLut[i] = (byte)Math.Clamp((int)Math.Round(i * scale), 0, 255);
        }
    }

    /// <summary>Whether the colour and opacity leave every pixel unchanged.</summary>
    public bool IsNoOp => this.alphaLut[255] == 0;

    /// <summary>Whether full coverage replaces the destination outright (opaque colour at full opacity).</summary>
    public bool IsOpaque => this.alphaLut[255] == 255;

    public void Blend(int y, int x, ReadOnlySpan<byte> coverage)
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Span<Rgba32> row = MemoryMarshal.Cast<TPixel, Rgba32>(this.frame.GetRowSpan(y)).Slice(x, coverage.Length);
            this.BlendRgba32(row, coverage);
        }
        else if (typeof(TPixel) == typeof(Rgb24))
        {
            Span<Rgb24> row = MemoryMarshal.Cast<TPixel, Rgb24>(this.frame.GetRowSpan(y)).Slice(x, coverage.Length);
            this.BlendRgb24(row, coverage);
        }
        else
        {
            Span<TPixel> row = this.frame.GetRowSpan(y).Slice(x, coverage.Length);
            this.BlendGeneric(row, coverage);
        }
    }

    private void BlendRgba32(Span<Rgba32> row, ReadOnlySpan<byte> coverage)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = this.alphaLut[coverage[i]];
            if (a == 0)
            {
                continue;
            }

            ref Rgba32 d = ref row[i];
            if (a == 255)
            {
                d = new Rgba32(this.r, this.g, this.b, 255);
            }
            else if (d.A == 255)
            {
                d.R = Mix(this.r, d.R, a);
                d.G = Mix(this.g, d.G, a);
                d.B = Mix(this.b, d.B, a);
            }
            else
            {
                d = this.BlendOverTranslucent(d, a);
            }
        }
    }

    private void BlendRgb24(Span<Rgb24> row, ReadOnlySpan<byte> coverage)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = this.alphaLut[coverage[i]];
            if (a == 0)
            {
                continue;
            }

            ref Rgb24 d = ref row[i];
            if (a == 255)
            {
                d = new Rgb24(this.r, this.g, this.b);
            }
            else
            {
                d.R = Mix(this.r, d.R, a);
                d.G = Mix(this.g, d.G, a);
                d.B = Mix(this.b, d.B, a);
            }
        }
    }

    private void BlendGeneric(Span<TPixel> row, ReadOnlySpan<byte> coverage)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = this.alphaLut[coverage[i]];
            if (a == 0)
            {
                continue;
            }

            Rgba32 result;
            if (a == 255)
            {
                result = new Rgba32(this.r, this.g, this.b, 255);
            }
            else
            {
                Rgba32 d = row[i].ToRgba32();
                result = d.A == 255
                    ? new Rgba32(Mix(this.r, d.R, a), Mix(this.g, d.G, a), Mix(this.b, d.B, a), 255)
                    : this.BlendOverTranslucent(d, a);
            }

            row[i] = TPixel.FromRgba32(result);
        }
    }

    /// <summary>Source-over of the colour with alpha <paramref name="a"/>/255 onto a destination with alpha below 255.</summary>
    private Rgba32 BlendOverTranslucent(Rgba32 d, int a)
    {
        float sa = a / 255f;
        float dw = d.A / 255f * (1f - sa);
        float outA = sa + dw;
        if (outA <= 0f)
        {
            return Rgba32.Transparent;
        }

        return new Rgba32(
            ClampByte(((this.r * sa) + (d.R * dw)) / outA),
            ClampByte(((this.g * sa) + (d.G * dw)) / outA),
            ClampByte(((this.b * sa) + (d.B * dw)) / outA),
            ClampByte(outA * 255f));
    }

    /// <summary>Linear interpolation <c>(src * a + dst * (255 - a)) / 255</c> rounded to nearest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Mix(int src, int dst, int a) => (byte)(((src * a) + (dst * (255 - a)) + 127) / 255);

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
