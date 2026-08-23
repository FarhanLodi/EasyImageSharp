using System.Runtime.CompilerServices;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// The pixel-level primitives of the VP8 decoder (RFC 6386 sections 12, 14 and 15): the exact integer
/// inverse DCT and Walsh-Hadamard transforms, the luma/chroma intra predictors and the simple and normal
/// in-loop deblocking filters. Everything is written to be bit-exact with the reference decoder.
/// </summary>
internal static class Vp8Dsp
{
    /// <summary>Row stride of the macroblock work buffer.</summary>
    public const int Bps = 32;

    private const int C1 = 20091;
    private const int C2 = 35468;

    // ----- Inverse transforms (section 14.3 / 14.4) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(int a) => ((a * C1) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(int a) => (a * C2) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(int v) => (v & ~0xff) == 0 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(byte[] dst, int off, int x, int y, int v)
    {
        int i = off + x + (y * Bps);
        dst[i] = Clip8(dst[i] + (v >> 3));
    }

    /// <summary>Full 4x4 inverse DCT of <paramref name="input"/> (16 coefficients at <paramref name="inOff"/>) added onto the prediction.</summary>
    public static void TransformOne(short[] input, int inOff, byte[] dst, int off)
    {
        Span<int> tmp = stackalloc int[16];
        int t = 0;
        for (int i = 0; i < 4; i++)
        {
            int a = input[inOff + i] + input[inOff + 8 + i];
            int b = input[inOff + i] - input[inOff + 8 + i];
            int c = Mul2(input[inOff + 4 + i]) - Mul1(input[inOff + 12 + i]);
            int d = Mul1(input[inOff + 4 + i]) + Mul2(input[inOff + 12 + i]);
            tmp[t + 0] = a + d;
            tmp[t + 1] = b + c;
            tmp[t + 2] = b - c;
            tmp[t + 3] = a - d;
            t += 4;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i] + 4;
            int a = dc + tmp[8 + i];
            int b = dc - tmp[8 + i];
            int c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            Store(dst, off, 0, i, a + d);
            Store(dst, off, 1, i, b + c);
            Store(dst, off, 2, i, b - c);
            Store(dst, off, 3, i, a - d);
        }
    }

    /// <summary>Simplified inverse DCT for blocks whose only non-zero coefficients are at positions 0, 1 and 4.</summary>
    public static void TransformAc3(short[] input, int inOff, byte[] dst, int off)
    {
        int a = input[inOff] + 4;
        int c4 = Mul2(input[inOff + 4]);
        int d4 = Mul1(input[inOff + 4]);
        int c1 = Mul2(input[inOff + 1]);
        int d1 = Mul1(input[inOff + 1]);
        Store2(dst, off, 0, a + d4, d1, c1);
        Store2(dst, off, 1, a + c4, d1, c1);
        Store2(dst, off, 2, a - c4, d1, c1);
        Store2(dst, off, 3, a - d4, d1, c1);
    }

    private static void Store2(byte[] dst, int off, int y, int dc, int d, int c)
    {
        Store(dst, off, 0, y, dc + d);
        Store(dst, off, 1, y, dc + c);
        Store(dst, off, 2, y, dc - c);
        Store(dst, off, 3, y, dc - d);
    }

    /// <summary>Inverse DCT for a DC-only block.</summary>
    public static void TransformDc(short[] input, int inOff, byte[] dst, int off)
    {
        int dc = input[inOff] + 4;
        for (int j = 0; j < 4; j++)
        {
            for (int i = 0; i < 4; i++)
            {
                Store(dst, off, i, j, dc);
            }
        }
    }

    /// <summary>Full transform of the four 4x4 blocks of a chroma plane.</summary>
    public static void TransformUv(short[] input, int inOff, byte[] dst, int off)
    {
        TransformOne(input, inOff + (0 * 16), dst, off);
        TransformOne(input, inOff + (1 * 16), dst, off + 4);
        TransformOne(input, inOff + (2 * 16), dst, off + (4 * Bps));
        TransformOne(input, inOff + (3 * 16), dst, off + (4 * Bps) + 4);
    }

    /// <summary>DC-only transform of the four 4x4 blocks of a chroma plane.</summary>
    public static void TransformDcUv(short[] input, int inOff, byte[] dst, int off)
    {
        if (input[inOff + (0 * 16)] != 0)
        {
            TransformDc(input, inOff + (0 * 16), dst, off);
        }

        if (input[inOff + (1 * 16)] != 0)
        {
            TransformDc(input, inOff + (1 * 16), dst, off + 4);
        }

        if (input[inOff + (2 * 16)] != 0)
        {
            TransformDc(input, inOff + (2 * 16), dst, off + (4 * Bps));
        }

        if (input[inOff + (3 * 16)] != 0)
        {
            TransformDc(input, inOff + (3 * 16), dst, off + (4 * Bps) + 4);
        }
    }

    /// <summary>Inverse Walsh-Hadamard transform of the Y2 block, scattering the 16 DC values into the luma coefficient blocks.</summary>
    public static void TransformWht(short[] input, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];
            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        int outOff = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[0 + (i * 4)] + 3;
            int a0 = dc + tmp[3 + (i * 4)];
            int a1 = tmp[1 + (i * 4)] + tmp[2 + (i * 4)];
            int a2 = tmp[1 + (i * 4)] - tmp[2 + (i * 4)];
            int a3 = dc - tmp[3 + (i * 4)];
            output[outOff + 0] = (short)((a0 + a1) >> 3);
            output[outOff + 16] = (short)((a3 + a2) >> 3);
            output[outOff + 32] = (short)((a0 - a1) >> 3);
            output[outOff + 48] = (short)((a3 - a2) >> 3);
            outOff += 64;
        }
    }

    // ----- Intra prediction (section 12) -----

    private static void TrueMotion(byte[] dst, int off, int size)
    {
        int top = off - Bps;
        int topLeft = dst[top - 1];
        for (int y = 0; y < size; y++)
        {
            int delta = dst[off - 1] - topLeft;
            for (int x = 0; x < size; x++)
            {
                dst[off + x] = Clip8(dst[top + x] + delta);
            }

            off += Bps;
        }
    }

    private static void Fill(byte[] dst, int off, int size, byte value)
    {
        for (int y = 0; y < size; y++)
        {
            dst.AsSpan(off + (y * Bps), size).Fill(value);
        }
    }

    private static void Vertical(byte[] dst, int off, int size)
    {
        for (int y = 0; y < size; y++)
        {
            dst.AsSpan(off - Bps, size).CopyTo(dst.AsSpan(off + (y * Bps), size));
        }
    }

    private static void Horizontal(byte[] dst, int off, int size)
    {
        for (int y = 0; y < size; y++)
        {
            dst.AsSpan(off + (y * Bps), size).Fill(dst[off + (y * Bps) - 1]);
        }
    }

    /// <summary>Predicts a 16x16 luma block or an 8x8 chroma block; <paramref name="mode"/> uses the DC_PRED_NO* variants at frame edges.</summary>
    public static void PredictBlock(byte[] dst, int off, int size, int mode)
    {
        switch (mode)
        {
            case Vp8Decoder.DcPred:
            {
                int sum = size;
                for (int i = 0; i < size; i++)
                {
                    sum += dst[off - 1 + (i * Bps)] + dst[off + i - Bps];
                }

                Fill(dst, off, size, (byte)(sum >> (size == 16 ? 5 : 4)));
                break;
            }

            case Vp8Decoder.DcPredNoTop:
            {
                int sum = size >> 1;
                for (int i = 0; i < size; i++)
                {
                    sum += dst[off - 1 + (i * Bps)];
                }

                Fill(dst, off, size, (byte)(sum >> (size == 16 ? 4 : 3)));
                break;
            }

            case Vp8Decoder.DcPredNoLeft:
            {
                int sum = size >> 1;
                for (int i = 0; i < size; i++)
                {
                    sum += dst[off + i - Bps];
                }

                Fill(dst, off, size, (byte)(sum >> (size == 16 ? 4 : 3)));
                break;
            }

            case Vp8Decoder.DcPredNoTopLeft:
                Fill(dst, off, size, 0x80);
                break;

            case Vp8Decoder.TmPred:
                TrueMotion(dst, off, size);
                break;

            case Vp8Decoder.VPred:
                Vertical(dst, off, size);
                break;

            case Vp8Decoder.HPred:
                Horizontal(dst, off, size);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set(byte[] dst, int off, int x, int y, byte v) => dst[off + x + (y * Bps)] = v;

    /// <summary>Predicts a 4x4 luma sub-block with one of the ten B_*_PRED modes.</summary>
    public static void PredictLuma4(byte[] dst, int off, int mode)
    {
        int top = off - Bps;
        switch (mode)
        {
            case Vp8Decoder.BDcPred:
            {
                int dc = 4;
                for (int i = 0; i < 4; i++)
                {
                    dc += dst[top + i] + dst[off - 1 + (i * Bps)];
                }

                Fill(dst, off, 4, (byte)(dc >> 3));
                break;
            }

            case Vp8Decoder.BTmPred:
                TrueMotion(dst, off, 4);
                break;

            case Vp8Decoder.BVePred:
            {
                byte v0 = Avg3(dst[top - 1], dst[top], dst[top + 1]);
                byte v1 = Avg3(dst[top], dst[top + 1], dst[top + 2]);
                byte v2 = Avg3(dst[top + 1], dst[top + 2], dst[top + 3]);
                byte v3 = Avg3(dst[top + 2], dst[top + 3], dst[top + 4]);
                for (int y = 0; y < 4; y++)
                {
                    int o = off + (y * Bps);
                    dst[o] = v0;
                    dst[o + 1] = v1;
                    dst[o + 2] = v2;
                    dst[o + 3] = v3;
                }

                break;
            }

            case Vp8Decoder.BHePred:
            {
                int a = dst[off - 1 - Bps];
                int b = dst[off - 1];
                int c = dst[off - 1 + Bps];
                int d = dst[off - 1 + (2 * Bps)];
                int e = dst[off - 1 + (3 * Bps)];
                dst.AsSpan(off, 4).Fill(Avg3(a, b, c));
                dst.AsSpan(off + Bps, 4).Fill(Avg3(b, c, d));
                dst.AsSpan(off + (2 * Bps), 4).Fill(Avg3(c, d, e));
                dst.AsSpan(off + (3 * Bps), 4).Fill(Avg3(d, e, e));
                break;
            }

            case Vp8Decoder.BRdPred:
            {
                int i = dst[off - 1];
                int j = dst[off - 1 + Bps];
                int k = dst[off - 1 + (2 * Bps)];
                int l = dst[off - 1 + (3 * Bps)];
                int x = dst[off - 1 - Bps];
                int a = dst[top];
                int b = dst[top + 1];
                int c = dst[top + 2];
                int d = dst[top + 3];
                Set(dst, off, 0, 3, Avg3(j, k, l));
                byte v = Avg3(i, j, k);
                Set(dst, off, 1, 3, v);
                Set(dst, off, 0, 2, v);
                v = Avg3(x, i, j);
                Set(dst, off, 2, 3, v);
                Set(dst, off, 1, 2, v);
                Set(dst, off, 0, 1, v);
                v = Avg3(a, x, i);
                Set(dst, off, 3, 3, v);
                Set(dst, off, 2, 2, v);
                Set(dst, off, 1, 1, v);
                Set(dst, off, 0, 0, v);
                v = Avg3(b, a, x);
                Set(dst, off, 3, 2, v);
                Set(dst, off, 2, 1, v);
                Set(dst, off, 1, 0, v);
                v = Avg3(c, b, a);
                Set(dst, off, 3, 1, v);
                Set(dst, off, 2, 0, v);
                Set(dst, off, 3, 0, Avg3(d, c, b));
                break;
            }

            case Vp8Decoder.BLdPred:
            {
                int a = dst[top];
                int b = dst[top + 1];
                int c = dst[top + 2];
                int d = dst[top + 3];
                int e = dst[top + 4];
                int f = dst[top + 5];
                int g = dst[top + 6];
                int h = dst[top + 7];
                Set(dst, off, 0, 0, Avg3(a, b, c));
                byte v = Avg3(b, c, d);
                Set(dst, off, 1, 0, v);
                Set(dst, off, 0, 1, v);
                v = Avg3(c, d, e);
                Set(dst, off, 2, 0, v);
                Set(dst, off, 1, 1, v);
                Set(dst, off, 0, 2, v);
                v = Avg3(d, e, f);
                Set(dst, off, 3, 0, v);
                Set(dst, off, 2, 1, v);
                Set(dst, off, 1, 2, v);
                Set(dst, off, 0, 3, v);
                v = Avg3(e, f, g);
                Set(dst, off, 3, 1, v);
                Set(dst, off, 2, 2, v);
                Set(dst, off, 1, 3, v);
                v = Avg3(f, g, h);
                Set(dst, off, 3, 2, v);
                Set(dst, off, 2, 3, v);
                Set(dst, off, 3, 3, Avg3(g, h, h));
                break;
            }

            case Vp8Decoder.BVrPred:
            {
                int i = dst[off - 1];
                int j = dst[off - 1 + Bps];
                int k = dst[off - 1 + (2 * Bps)];
                int x = dst[off - 1 - Bps];
                int a = dst[top];
                int b = dst[top + 1];
                int c = dst[top + 2];
                int d = dst[top + 3];
                byte v = Avg2(x, a);
                Set(dst, off, 0, 0, v);
                Set(dst, off, 1, 2, v);
                v = Avg2(a, b);
                Set(dst, off, 1, 0, v);
                Set(dst, off, 2, 2, v);
                v = Avg2(b, c);
                Set(dst, off, 2, 0, v);
                Set(dst, off, 3, 2, v);
                Set(dst, off, 3, 0, Avg2(c, d));
                Set(dst, off, 0, 3, Avg3(k, j, i));
                Set(dst, off, 0, 2, Avg3(j, i, x));
                v = Avg3(i, x, a);
                Set(dst, off, 0, 1, v);
                Set(dst, off, 1, 3, v);
                v = Avg3(x, a, b);
                Set(dst, off, 1, 1, v);
                Set(dst, off, 2, 3, v);
                v = Avg3(a, b, c);
                Set(dst, off, 2, 1, v);
                Set(dst, off, 3, 3, v);
                Set(dst, off, 3, 1, Avg3(b, c, d));
                break;
            }

            case Vp8Decoder.BVlPred:
            {
                int a = dst[top];
                int b = dst[top + 1];
                int c = dst[top + 2];
                int d = dst[top + 3];
                int e = dst[top + 4];
                int f = dst[top + 5];
                int g = dst[top + 6];
                int h = dst[top + 7];
                Set(dst, off, 0, 0, Avg2(a, b));
                byte v = Avg2(b, c);
                Set(dst, off, 1, 0, v);
                Set(dst, off, 0, 2, v);
                v = Avg2(c, d);
                Set(dst, off, 2, 0, v);
                Set(dst, off, 1, 2, v);
                v = Avg2(d, e);
                Set(dst, off, 3, 0, v);
                Set(dst, off, 2, 2, v);
                Set(dst, off, 0, 1, Avg3(a, b, c));
                v = Avg3(b, c, d);
                Set(dst, off, 1, 1, v);
                Set(dst, off, 0, 3, v);
                v = Avg3(c, d, e);
                Set(dst, off, 2, 1, v);
                Set(dst, off, 1, 3, v);
                v = Avg3(d, e, f);
                Set(dst, off, 3, 1, v);
                Set(dst, off, 2, 3, v);
                Set(dst, off, 3, 2, Avg3(e, f, g));
                Set(dst, off, 3, 3, Avg3(f, g, h));
                break;
            }

            case Vp8Decoder.BHuPred:
            {
                int i = dst[off - 1];
                int j = dst[off - 1 + Bps];
                int k = dst[off - 1 + (2 * Bps)];
                int l = dst[off - 1 + (3 * Bps)];
                Set(dst, off, 0, 0, Avg2(i, j));
                byte v = Avg2(j, k);
                Set(dst, off, 2, 0, v);
                Set(dst, off, 0, 1, v);
                v = Avg2(k, l);
                Set(dst, off, 2, 1, v);
                Set(dst, off, 0, 2, v);
                Set(dst, off, 1, 0, Avg3(i, j, k));
                v = Avg3(j, k, l);
                Set(dst, off, 3, 0, v);
                Set(dst, off, 1, 1, v);
                v = Avg3(k, l, l);
                Set(dst, off, 3, 1, v);
                Set(dst, off, 1, 2, v);
                byte lv = (byte)l;
                Set(dst, off, 3, 2, lv);
                Set(dst, off, 2, 2, lv);
                Set(dst, off, 0, 3, lv);
                Set(dst, off, 1, 3, lv);
                Set(dst, off, 2, 3, lv);
                Set(dst, off, 3, 3, lv);
                break;
            }

            case Vp8Decoder.BHdPred:
            {
                int i = dst[off - 1];
                int j = dst[off - 1 + Bps];
                int k = dst[off - 1 + (2 * Bps)];
                int l = dst[off - 1 + (3 * Bps)];
                int x = dst[off - 1 - Bps];
                int a = dst[top];
                int b = dst[top + 1];
                int c = dst[top + 2];
                byte v = Avg2(i, x);
                Set(dst, off, 0, 0, v);
                Set(dst, off, 2, 1, v);
                v = Avg2(j, i);
                Set(dst, off, 0, 1, v);
                Set(dst, off, 2, 2, v);
                v = Avg2(k, j);
                Set(dst, off, 0, 2, v);
                Set(dst, off, 2, 3, v);
                Set(dst, off, 0, 3, Avg2(l, k));
                Set(dst, off, 3, 0, Avg3(a, b, c));
                Set(dst, off, 2, 0, Avg3(x, a, b));
                v = Avg3(i, x, a);
                Set(dst, off, 1, 0, v);
                Set(dst, off, 3, 1, v);
                v = Avg3(j, i, x);
                Set(dst, off, 1, 1, v);
                Set(dst, off, 3, 2, v);
                v = Avg3(k, j, i);
                Set(dst, off, 1, 2, v);
                Set(dst, off, 3, 3, v);
                Set(dst, off, 1, 3, Avg3(l, k, j));
                break;
            }
        }
    }

    // ----- Loop filters (section 15) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip1(int v) => Math.Clamp(v, -128, 127);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip2(int v) => Math.Clamp(v, -16, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip1(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>4 pixels in, 2 pixels out (the common adjustment with outer taps).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(byte[] p, int i, int step)
    {
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        int a = (3 * (q0 - p0)) + SClip1(p1 - q1);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        p[i - step] = Clip1(p0 + a2);
        p[i] = Clip1(q0 - a1);
    }

    /// <summary>4 pixels in, 4 pixels out (inner-edge filter without high edge variance).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter4(byte[] p, int i, int step)
    {
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        p[i - (2 * step)] = Clip1(p1 + a3);
        p[i - step] = Clip1(p0 + a2);
        p[i] = Clip1(q0 - a1);
        p[i + step] = Clip1(q1 - a3);
    }

    /// <summary>6 pixels in, 6 pixels out (macroblock-edge filter without high edge variance).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter6(byte[] p, int i, int step)
    {
        int p2 = p[i - (3 * step)];
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        int q2 = p[i + (2 * step)];
        int a = SClip1((3 * (q0 - p0)) + SClip1(p1 - q1));
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;
        p[i - (3 * step)] = Clip1(p2 + a3);
        p[i - (2 * step)] = Clip1(p1 + a2);
        p[i - step] = Clip1(p0 + a1);
        p[i] = Clip1(q0 - a1);
        p[i + step] = Clip1(q1 - a2);
        p[i + (2 * step)] = Clip1(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hev(byte[] p, int i, int step, int thresh)
    {
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        return Math.Abs(p1 - p0) > thresh || Math.Abs(q1 - q0) > thresh;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter(byte[] p, int i, int step, int t)
    {
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        return ((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1)) <= t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter2(byte[] p, int i, int step, int t, int it)
    {
        int p3 = p[i - (4 * step)];
        int p2 = p[i - (3 * step)];
        int p1 = p[i - (2 * step)];
        int p0 = p[i - step];
        int q0 = p[i];
        int q1 = p[i + step];
        int q2 = p[i + (2 * step)];
        int q3 = p[i + (3 * step)];
        if (((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1)) > t)
        {
            return false;
        }

        return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it && Math.Abs(p1 - p0) <= it
            && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
    }

    /// <summary>Simple filter across a horizontal edge (16 pixels wide).</summary>
    public static void SimpleVFilter16(byte[] p, int off, int stride, int thresh)
    {
        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilter(p, off + i, stride, thresh2))
            {
                DoFilter2(p, off + i, stride);
            }
        }
    }

    /// <summary>Simple filter across a vertical edge (16 pixels tall).</summary>
    public static void SimpleHFilter16(byte[] p, int off, int stride, int thresh)
    {
        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilter(p, off + (i * stride), 1, thresh2))
            {
                DoFilter2(p, off + (i * stride), 1);
            }
        }
    }

    public static void SimpleVFilter16i(byte[] p, int off, int stride, int thresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4 * stride;
            SimpleVFilter16(p, off, stride, thresh);
        }
    }

    public static void SimpleHFilter16i(byte[] p, int off, int stride, int thresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4;
            SimpleHFilter16(p, off, stride, thresh);
        }
    }

    private static void FilterLoop26(byte[] p, int off, int hstride, int vstride, int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = (2 * thresh) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
            {
                if (Hev(p, off, hstride, hevThresh))
                {
                    DoFilter2(p, off, hstride);
                }
                else
                {
                    DoFilter6(p, off, hstride);
                }
            }

            off += vstride;
        }
    }

    private static void FilterLoop24(byte[] p, int off, int hstride, int vstride, int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = (2 * thresh) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
            {
                if (Hev(p, off, hstride, hevThresh))
                {
                    DoFilter2(p, off, hstride);
                }
                else
                {
                    DoFilter4(p, off, hstride);
                }
            }

            off += vstride;
        }
    }

    /// <summary>Normal filter across the top macroblock edge of a 16-wide luma block.</summary>
    public static void VFilter16(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop26(p, off, stride, 1, 16, thresh, ithresh, hevThresh);

    /// <summary>Normal filter across the left macroblock edge of a 16-tall luma block.</summary>
    public static void HFilter16(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop26(p, off, 1, stride, 16, thresh, ithresh, hevThresh);

    public static void VFilter16i(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4 * stride;
            FilterLoop24(p, off, stride, 1, 16, thresh, ithresh, hevThresh);
        }
    }

    public static void HFilter16i(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4;
            FilterLoop24(p, off, 1, stride, 16, thresh, ithresh, hevThresh);
        }
    }

    /// <summary>Normal filter across the top macroblock edge of an 8-wide chroma block.</summary>
    public static void VFilter8(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop26(p, off, stride, 1, 8, thresh, ithresh, hevThresh);

    public static void HFilter8(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop26(p, off, 1, stride, 8, thresh, ithresh, hevThresh);

    public static void VFilter8i(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop24(p, off + (4 * stride), stride, 1, 8, thresh, ithresh, hevThresh);

    public static void HFilter8i(byte[] p, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop24(p, off + 4, 1, stride, 8, thresh, ithresh, hevThresh);
}
