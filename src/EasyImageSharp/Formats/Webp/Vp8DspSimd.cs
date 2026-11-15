using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// 128-bit vector versions of the hot <see cref="Vp8Dsp"/> primitives: the inverse DCT (full, DC-only and the
/// three-coefficient shortcut), the TrueMotion intra predictor and the simple and normal loop filters.
/// <para>
/// Every kernel evaluates exactly the integer expressions of the scalar reference, lane by lane, so the output is
/// bit-identical for every input - including coefficient values a valid stream never produces. The inverse DCT
/// therefore runs in 32-bit lanes (the scalar code's 32-bit products can wrap, and so do the vector ones, in the
/// same way), while the loop filters and predictors run in 16-bit lanes, which hold every intermediate value of
/// those formulas without overflow. Transposes use the SSE2 / AdvSimd interleave instructions, so the kernels
/// are only enabled where one of the two exists; <see cref="Vp8Dsp"/> keeps the scalar path for everything else.
/// </para>
/// </summary>
internal static class Vp8DspSimd
{
    private const int Bps = Vp8Dsp.Bps;

    private const int C1 = 20091;
    private const int C2 = 35468;

    /// <summary>
    /// True when the vector kernels may run: 128-bit vectors are accelerated and not disabled through
    /// <see cref="SimdConfig"/>, and the CPU has the byte/word interleave instructions the transposes need.
    /// </summary>
    public static bool IsEnabled
        => SimdConfig.Vector128Enabled && BitConverter.IsLittleEndian && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    // ----- Inverse transforms -----

    /// <summary>Full 4x4 inverse DCT added onto the prediction; bit-exact with <see cref="Vp8Dsp.TransformOneScalar"/>.</summary>
    public static void TransformOne(short[] input, int inOff, byte[] dst, int off)
    {
        ref short coefficients = ref MemoryMarshal.GetReference(input.AsSpan(inOff, 16));
        Vector128<short> rows01 = Vector128.LoadUnsafe(ref coefficients);
        Vector128<short> rows23 = Vector128.LoadUnsafe(ref coefficients, 8);

        // Vertical pass: lane i works on column i, exactly the scalar first loop.
        Vector128<int> r0 = Vector128.WidenLower(rows01);
        Vector128<int> r1 = Vector128.WidenUpper(rows01);
        Vector128<int> r2 = Vector128.WidenLower(rows23);
        Vector128<int> r3 = Vector128.WidenUpper(rows23);
        Vector128<int> a = r0 + r2;
        Vector128<int> b = r0 - r2;
        Vector128<int> c = Mul2(r1) - Mul1(r3);
        Vector128<int> d = Mul1(r1) + Mul2(r3);
        Vector128<int> t0 = a + d;
        Vector128<int> t1 = b + c;
        Vector128<int> t2 = b - c;
        Vector128<int> t3 = a - d;

        // tj lane i is tmp[4i + j]; after the transpose tj lane i is tmp[4j + i], the operand order of the
        // second (horizontal) pass, whose lane i then produces output row i.
        Transpose4x4(ref t0, ref t1, ref t2, ref t3);
        Vector128<int> dc = t0 + Vector128.Create(4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector128<int> x0 = a + d;
        Vector128<int> x1 = b + c;
        Vector128<int> x2 = b - c;
        Vector128<int> x3 = a - d;

        // Back to raster order: xk lane y becomes row y lane k.
        Transpose4x4(ref x0, ref x1, ref x2, ref x3);
        AddResidual(dst, off, ResidualRows(x0, x1), ResidualRows(x2, x3));
    }

    /// <summary>Inverse DCT of a block whose only non-zero coefficients are 0, 1 and 4.</summary>
    public static void TransformAc3(short[] input, int inOff, byte[] dst, int off)
    {
        ReadOnlySpan<short> coefficients = input.AsSpan(inOff, 16);
        int a = coefficients[0] + 4;
        int c4 = Mul2(coefficients[4]);
        int d4 = Mul1(coefficients[4]);
        int c1 = Mul2(coefficients[1]);
        int d1 = Mul1(coefficients[1]);

        // Row y, column x is rowBase[y] + column[x]: Store2 of the scalar code with (dc, d, c) = (rowBase, d1, c1).
        Vector128<int> column = Vector128.Create(d1, c1, -c1, -d1);
        Vector128<int> y0 = Vector128.Create(a + d4) + column;
        Vector128<int> y1 = Vector128.Create(a + c4) + column;
        Vector128<int> y2 = Vector128.Create(a - c4) + column;
        Vector128<int> y3 = Vector128.Create(a - d4) + column;
        AddResidual(dst, off, ResidualRows(y0, y1), ResidualRows(y2, y3));
    }

    /// <summary>Inverse DCT of a DC-only block: one residual added to all sixteen pixels.</summary>
    public static void TransformDc(short[] input, int inOff, byte[] dst, int off)
    {
        int residual = Math.Clamp((input[inOff] + 4) >> 3, -256, 256);
        Vector128<short> all = Vector128.Create((short)residual);
        AddResidual(dst, off, all, all);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(int a) => ((a * C1) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(int a) => (a * C2) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * Vector128.Create(C1), 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * Vector128.Create(C2), 16);

    /// <summary>
    /// Shifts two rows of unscaled residuals down by three and packs them into 16-bit lanes. The residual is
    /// first clamped to [-256, 256]: added to a prediction in [0, 255] and clipped, any value beyond that range
    /// saturates exactly as the clamped one does, and the clamped one narrows to 16 bits without loss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ResidualRows(Vector128<int> first, Vector128<int> second)
    {
        Vector128<int> low = Vector128.Create(-256);
        Vector128<int> high = Vector128.Create(256);
        Vector128<int> a = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(first, 3), low), high);
        Vector128<int> b = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(second, 3), low), high);
        return Vector128.Narrow(a, b);
    }

    /// <summary>Adds rows 0-1 and 2-3 of a 4x4 residual to the prediction at <paramref name="off"/> and clips to 8 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddResidual(byte[] dst, int off, Vector128<short> rows01, Vector128<short> rows23)
    {
        ref byte block = ref MemoryMarshal.GetReference(dst.AsSpan(off, (3 * Bps) + 4));
        Vector128<byte> prediction = Vector128.Create(
            Unsafe.ReadUnaligned<uint>(ref block),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, Bps)),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, 2 * Bps)),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, 3 * Bps))).AsByte();
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        Vector128<short> low = Vector128.Min(Vector128.Max(Vector128.WidenLower(prediction).AsInt16() + rows01, zero), max);
        Vector128<short> high = Vector128.Min(Vector128.Max(Vector128.WidenUpper(prediction).AsInt16() + rows23, zero), max);
        Vector128<uint> result = Vector128.Narrow(low.AsUInt16(), high.AsUInt16()).AsUInt32();
        Unsafe.WriteUnaligned(ref block, result.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, Bps), result.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 2 * Bps), result.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 3 * Bps), result.GetElement(3));
    }

    // ----- Intra prediction -----

    /// <summary>TrueMotion prediction of a 16x16 or 8x8 block: <c>clip(top[x] + left[y] - topLeft)</c>.</summary>
    public static void TrueMotion(byte[] dst, int off, int size)
    {
        // One bounds check covers the top-left sample, the row above and the whole block.
        ref byte top = ref MemoryMarshal.GetReference(dst.AsSpan(off - Bps - 1, (size * Bps) + size + 1));
        int topLeft = top;
        top = ref Unsafe.Add(ref top, 1);
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        if (size == 16)
        {
            Vector128<byte> above = Vector128.LoadUnsafe(ref top);
            Vector128<short> aboveLow = Vector128.WidenLower(above).AsInt16();
            Vector128<short> aboveHigh = Vector128.WidenUpper(above).AsInt16();
            for (int y = 1; y <= 16; y++)
            {
                ref byte row = ref Unsafe.Add(ref top, y * Bps);
                Vector128<short> delta = Vector128.Create((short)(Unsafe.Add(ref row, -1) - topLeft));
                Vector128<short> low = Vector128.Min(Vector128.Max(aboveLow + delta, zero), max);
                Vector128<short> high = Vector128.Min(Vector128.Max(aboveHigh + delta, zero), max);
                Vector128.Narrow(low.AsUInt16(), high.AsUInt16()).StoreUnsafe(ref row);
            }
        }
        else
        {
            Vector128<short> above = Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref top)).AsByte()).AsInt16();
            for (int y = 1; y <= size; y++)
            {
                ref byte row = ref Unsafe.Add(ref top, y * Bps);
                Vector128<short> delta = Vector128.Create((short)(Unsafe.Add(ref row, -1) - topLeft));
                Vector128<short> value = Vector128.Min(Vector128.Max(above + delta, zero), max);
                Unsafe.WriteUnaligned(ref row, Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt64().ToScalar());
            }
        }
    }

    // ----- Loop filters -----

    /// <summary>Simple filter across a horizontal edge, 16 pixels wide.</summary>
    public static void SimpleVFilter16(byte[] p, int off, int stride, int thresh)
    {
        ref byte p1Row = ref MemoryMarshal.GetReference(p.AsSpan(off - (2 * stride), (3 * stride) + 16));
        Vector128<short> t = Vector128.Create((short)((2 * thresh) + 1));
        Vector128<byte> p1 = Vector128.LoadUnsafe(ref p1Row);
        Vector128<byte> p0 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p1Row, stride));
        Vector128<byte> q0 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p1Row, 2 * stride));
        Vector128<byte> q1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p1Row, 3 * stride));

        Vector128<short> p0Low = Low(p0);
        Vector128<short> q0Low = Low(q0);
        SimpleFilter(Low(p1), ref p0Low, ref q0Low, Low(q1), t);
        Vector128<short> p0High = High(p0);
        Vector128<short> q0High = High(q0);
        SimpleFilter(High(p1), ref p0High, ref q0High, High(q1), t);

        Pack(p0Low, p0High).StoreUnsafe(ref Unsafe.Add(ref p1Row, stride));
        Pack(q0Low, q0High).StoreUnsafe(ref Unsafe.Add(ref p1Row, 2 * stride));
    }

    /// <summary>Simple filter across a vertical edge, 16 pixels tall.</summary>
    public static void SimpleHFilter16(byte[] p, int off, int stride, int thresh)
    {
        Vector128<short> t = Vector128.Create((short)((2 * thresh) + 1));
        ref byte start = ref MemoryMarshal.GetReference(p.AsSpan(off - 4, (15 * stride) + 8));
        for (int half = 0; half < 2; half++)
        {
            ref byte rows = ref Unsafe.Add(ref start, half * 8 * stride);
            LoadColumns(ref rows, stride, out Vector128<byte> c01, out Vector128<byte> c23, out Vector128<byte> c45, out Vector128<byte> c67);
            Vector128<short> p0 = High(c23);
            Vector128<short> q0 = Low(c45);
            SimpleFilter(Low(c23), ref p0, ref q0, High(c45), t);
            StoreColumns(ref rows, stride, c01, Pack(Low(c23), p0), Pack(q0, High(c45)), c67);
        }
    }

    /// <summary>Simple filter across the three inner horizontal edges of a 16x16 luma block.</summary>
    public static void SimpleVFilter16i(byte[] p, int off, int stride, int thresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4 * stride;
            SimpleVFilter16(p, off, stride, thresh);
        }
    }

    /// <summary>Simple filter across the three inner vertical edges of a 16x16 luma block.</summary>
    public static void SimpleHFilter16i(byte[] p, int off, int stride, int thresh)
    {
        for (int k = 3; k > 0; k--)
        {
            off += 4;
            SimpleHFilter16(p, off, stride, thresh);
        }
    }

    /// <summary>Normal filter across a horizontal edge, 16 (luma) or 8 (chroma) pixels wide.</summary>
    public static void VFilter(byte[] p, int off, int stride, int width, bool macroblockEdge, int thresh, int ithresh, int hevThresh)
    {
        var parameters = new FilterParameters(thresh, ithresh, hevThresh);
        ref byte p3Row = ref MemoryMarshal.GetReference(p.AsSpan(off - (4 * stride), (7 * stride) + width));
        if (width == 16)
        {
            Vector128<byte> p3 = Vector128.LoadUnsafe(ref p3Row);
            Vector128<byte> p2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, stride));
            Vector128<byte> p1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 2 * stride));
            Vector128<byte> p0 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 3 * stride));
            Vector128<byte> q0 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 4 * stride));
            Vector128<byte> q1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 5 * stride));
            Vector128<byte> q2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 6 * stride));
            Vector128<byte> q3 = Vector128.LoadUnsafe(ref Unsafe.Add(ref p3Row, 7 * stride));

            var low = new EdgePixels(Low(p3), Low(p2), Low(p1), Low(p0), Low(q0), Low(q1), Low(q2), Low(q3));
            var high = new EdgePixels(High(p3), High(p2), High(p1), High(p0), High(q0), High(q1), High(q2), High(q3));
            bool filtered = NormalFilter(ref low, macroblockEdge, parameters);
            filtered |= NormalFilter(ref high, macroblockEdge, parameters);
            if (!filtered)
            {
                return;
            }

            Pack(low.P2, high.P2).StoreUnsafe(ref Unsafe.Add(ref p3Row, stride));
            Pack(low.P1, high.P1).StoreUnsafe(ref Unsafe.Add(ref p3Row, 2 * stride));
            Pack(low.P0, high.P0).StoreUnsafe(ref Unsafe.Add(ref p3Row, 3 * stride));
            Pack(low.Q0, high.Q0).StoreUnsafe(ref Unsafe.Add(ref p3Row, 4 * stride));
            Pack(low.Q1, high.Q1).StoreUnsafe(ref Unsafe.Add(ref p3Row, 5 * stride));
            Pack(low.Q2, high.Q2).StoreUnsafe(ref Unsafe.Add(ref p3Row, 6 * stride));
        }
        else
        {
            var pixels = new EdgePixels(
                Load8(ref p3Row), Load8(ref Unsafe.Add(ref p3Row, stride)),
                Load8(ref Unsafe.Add(ref p3Row, 2 * stride)), Load8(ref Unsafe.Add(ref p3Row, 3 * stride)),
                Load8(ref Unsafe.Add(ref p3Row, 4 * stride)), Load8(ref Unsafe.Add(ref p3Row, 5 * stride)),
                Load8(ref Unsafe.Add(ref p3Row, 6 * stride)), Load8(ref Unsafe.Add(ref p3Row, 7 * stride)));
            if (!NormalFilter(ref pixels, macroblockEdge, parameters))
            {
                return;
            }

            Store8(ref Unsafe.Add(ref p3Row, stride), pixels.P2);
            Store8(ref Unsafe.Add(ref p3Row, 2 * stride), pixels.P1);
            Store8(ref Unsafe.Add(ref p3Row, 3 * stride), pixels.P0);
            Store8(ref Unsafe.Add(ref p3Row, 4 * stride), pixels.Q0);
            Store8(ref Unsafe.Add(ref p3Row, 5 * stride), pixels.Q1);
            Store8(ref Unsafe.Add(ref p3Row, 6 * stride), pixels.Q2);
        }
    }

    /// <summary>Normal filter across a vertical edge, 16 (luma) or 8 (chroma) pixels tall.</summary>
    public static void HFilter(byte[] p, int off, int stride, int height, bool macroblockEdge, int thresh, int ithresh, int hevThresh)
    {
        var parameters = new FilterParameters(thresh, ithresh, hevThresh);
        ref byte start = ref MemoryMarshal.GetReference(p.AsSpan(off - 4, ((height - 1) * stride) + 8));
        for (int group = 0; group < height; group += 8)
        {
            ref byte rows = ref Unsafe.Add(ref start, group * stride);
            LoadColumns(ref rows, stride, out Vector128<byte> c01, out Vector128<byte> c23, out Vector128<byte> c45, out Vector128<byte> c67);
            var pixels = new EdgePixels(Low(c01), High(c01), Low(c23), High(c23), Low(c45), High(c45), Low(c67), High(c67));
            if (!NormalFilter(ref pixels, macroblockEdge, parameters))
            {
                continue;
            }

            StoreColumns(
                ref rows, stride, Pack(pixels.P3, pixels.P2), Pack(pixels.P1, pixels.P0), Pack(pixels.Q0, pixels.Q1), Pack(pixels.Q2, pixels.Q3));
        }
    }

    /// <summary>The thresholds of one normal-filter call, broadcast to every lane.</summary>
    private readonly struct FilterParameters
    {
        public FilterParameters(int thresh, int ithresh, int hevThresh)
        {
            this.Edge = Vector128.Create((short)((2 * thresh) + 1));
            this.Interior = Vector128.Create((short)ithresh);
            this.Hev = Vector128.Create((short)hevThresh);
        }

        /// <summary>The edge limit compared with <c>4 |p0 - q0| + |p1 - q1|</c>.</summary>
        public Vector128<short> Edge { get; }

        /// <summary>The interior limit every neighbouring difference must respect.</summary>
        public Vector128<short> Interior { get; }

        /// <summary>The high-edge-variance threshold.</summary>
        public Vector128<short> Hev { get; }
    }

    /// <summary>The eight pixels across an edge (p3 farthest on one side, q3 on the other), eight positions per vector.</summary>
    private struct EdgePixels
    {
        public Vector128<short> P3;
        public Vector128<short> P2;
        public Vector128<short> P1;
        public Vector128<short> P0;
        public Vector128<short> Q0;
        public Vector128<short> Q1;
        public Vector128<short> Q2;
        public Vector128<short> Q3;

        public EdgePixels(
            Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0,
            Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3)
        {
            this.P3 = p3;
            this.P2 = p2;
            this.P1 = p1;
            this.P0 = p0;
            this.Q0 = q0;
            this.Q1 = q1;
            this.Q2 = q2;
            this.Q3 = q3;
        }
    }

    /// <summary>NeedsFilter + DoFilter2 of the scalar code on eight positions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SimpleFilter(Vector128<short> p1, ref Vector128<short> p0, ref Vector128<short> q0, Vector128<short> q1, Vector128<short> t)
    {
        Vector128<short> mask = Vector128.LessThanOrEqual((Vector128.Abs(p0 - q0) * 4) + Vector128.Abs(p1 - q1), t);
        Filter2(p1, p0, q0, q1, out Vector128<short> newP0, out Vector128<short> newQ0);
        p0 = Vector128.ConditionalSelect(mask, newP0, p0);
        q0 = Vector128.ConditionalSelect(mask, newQ0, q0);
    }

    /// <summary>
    /// FilterLoop26 (macroblock edge) or FilterLoop24 (inner edge) of the scalar code on eight positions: both
    /// candidate filters are evaluated everywhere and the per-position decision selects between them. Returns false,
    /// leaving every pixel untouched, when no position passes the filter's activity tests.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NormalFilter(ref EdgePixels e, bool macroblockEdge, in FilterParameters parameters)
    {
        Vector128<short> dP1P0 = Vector128.Abs(e.P1 - e.P0);
        Vector128<short> dQ1Q0 = Vector128.Abs(e.Q1 - e.Q0);
        Vector128<short> interior = Vector128.Max(
            Vector128.Max(Vector128.Max(Vector128.Abs(e.P3 - e.P2), Vector128.Abs(e.P2 - e.P1)), dP1P0),
            Vector128.Max(Vector128.Max(Vector128.Abs(e.Q3 - e.Q2), Vector128.Abs(e.Q2 - e.Q1)), dQ1Q0));
        Vector128<short> edge = (Vector128.Abs(e.P0 - e.Q0) * 4) + Vector128.Abs(e.P1 - e.Q1);
        Vector128<short> mask = Vector128.LessThanOrEqual(edge, parameters.Edge) & Vector128.LessThanOrEqual(interior, parameters.Interior);
        if (mask == Vector128<short>.Zero)
        {
            return false;
        }

        Vector128<short> hev = Vector128.GreaterThan(Vector128.Max(dP1P0, dQ1Q0), parameters.Hev);
        Vector128<short> useFilter2 = mask & hev;
        Vector128<short> useWide = Vector128.AndNot(mask, hev);
        Filter2(e.P1, e.P0, e.Q0, e.Q1, out Vector128<short> p0Of2, out Vector128<short> q0Of2);

        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        if (macroblockEdge)
        {
            // DoFilter6.
            Vector128<short> w = Clamp(
                Clamp(e.P1 - e.Q1, -128, 127) + ((e.Q0 - e.P0) * 3), -128, 127);
            Vector128<short> rounding = Vector128.Create((short)63);
            Vector128<short> a1 = Vector128.ShiftRightArithmetic((w * 27) + rounding, 7);
            Vector128<short> a2 = Vector128.ShiftRightArithmetic((w * 18) + rounding, 7);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic((w * 9) + rounding, 7);
            Vector128<short> p0Wide = Vector128.Min(Vector128.Max(e.P0 + a1, zero), max);
            Vector128<short> q0Wide = Vector128.Min(Vector128.Max(e.Q0 - a1, zero), max);
            e.P2 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.P2 + a3, zero), max), e.P2);
            e.P1 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.P1 + a2, zero), max), e.P1);
            e.Q1 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.Q1 - a2, zero), max), e.Q1);
            e.Q2 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.Q2 - a3, zero), max), e.Q2);
            e.P0 = Vector128.ConditionalSelect(useFilter2, p0Of2, Vector128.ConditionalSelect(useWide, p0Wide, e.P0));
            e.Q0 = Vector128.ConditionalSelect(useFilter2, q0Of2, Vector128.ConditionalSelect(useWide, q0Wide, e.Q0));
        }
        else
        {
            // DoFilter4.
            Vector128<short> a = (e.Q0 - e.P0) * 3;
            Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)4), 3), -16, 15);
            Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)3), 3), -16, 15);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic(a1 + Vector128.Create((short)1), 1);
            Vector128<short> p0Four = Vector128.Min(Vector128.Max(e.P0 + a2, zero), max);
            Vector128<short> q0Four = Vector128.Min(Vector128.Max(e.Q0 - a1, zero), max);
            e.P1 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.P1 + a3, zero), max), e.P1);
            e.Q1 = Vector128.ConditionalSelect(useWide, Vector128.Min(Vector128.Max(e.Q1 - a3, zero), max), e.Q1);
            e.P0 = Vector128.ConditionalSelect(useFilter2, p0Of2, Vector128.ConditionalSelect(useWide, p0Four, e.P0));
            e.Q0 = Vector128.ConditionalSelect(useFilter2, q0Of2, Vector128.ConditionalSelect(useWide, q0Four, e.Q0));
        }

        return true;
    }

    /// <summary>DoFilter2 of the scalar code: the common adjustment of p0 and q0 that also uses the outer taps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter2(
        Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1,
        out Vector128<short> newP0, out Vector128<short> newQ0)
    {
        Vector128<short> a = ((q0 - p0) * 3) + Clamp(p1 - q1, -128, 127);
        Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)4), 3), -16, 15);
        Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)3), 3), -16, 15);
        newP0 = Clamp(p0 + a2, 0, 255);
        newQ0 = Clamp(q0 - a1, 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> v, short min, short max)
        => Vector128.Min(Vector128.Max(v, Vector128.Create(min)), Vector128.Create(max));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Low(Vector128<byte> v) => Vector128.WidenLower(v).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> High(Vector128<byte> v) => Vector128.WidenUpper(v).AsInt16();

    /// <summary>Narrows two vectors of pixel values that are already in [0, 255] back to bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pack(Vector128<short> low, Vector128<short> high) => Vector128.Narrow(low.AsUInt16(), high.AsUInt16());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load8(ref byte source)
        => Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref source)).AsByte()).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(ref byte destination, Vector128<short> value)
        => Unsafe.WriteUnaligned(ref destination, Pack(value, value).AsUInt64().ToScalar());

    /// <summary>
    /// Reads eight rows of eight bytes (the four pixels either side of a vertical edge) and transposes them, so
    /// <paramref name="c01"/> holds columns 0 and 1 (p3, p2) as two halves of eight row-lanes, and so on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadColumns(
        ref byte rows, int stride,
        out Vector128<byte> c01, out Vector128<byte> c23, out Vector128<byte> c45, out Vector128<byte> c67)
    {
        Vector128<byte> r01 = Vector128.Create(ReadRow(ref rows, 0), ReadRow(ref rows, stride)).AsByte();
        Vector128<byte> r23 = Vector128.Create(ReadRow(ref rows, 2 * stride), ReadRow(ref rows, 3 * stride)).AsByte();
        Vector128<byte> r45 = Vector128.Create(ReadRow(ref rows, 4 * stride), ReadRow(ref rows, 5 * stride)).AsByte();
        Vector128<byte> r67 = Vector128.Create(ReadRow(ref rows, 6 * stride), ReadRow(ref rows, 7 * stride)).AsByte();
        Transpose8x8(r01, r23, r45, r67, out c01, out c23, out c45, out c67);
    }

    /// <summary>The inverse of <see cref="LoadColumns"/>: transposes the column pairs back and writes the eight rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreColumns(
        ref byte rows, int stride, Vector128<byte> c01, Vector128<byte> c23, Vector128<byte> c45, Vector128<byte> c67)
    {
        Transpose8x8(c01, c23, c45, c67, out Vector128<byte> r01, out Vector128<byte> r23, out Vector128<byte> r45, out Vector128<byte> r67);
        WriteRows(ref rows, 0, stride, r01.AsUInt64());
        WriteRows(ref rows, 2 * stride, stride, r23.AsUInt64());
        WriteRows(ref rows, 4 * stride, stride, r45.AsUInt64());
        WriteRows(ref rows, 6 * stride, stride, r67.AsUInt64());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadRow(ref byte rows, int offset) => Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rows, offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRows(ref byte rows, int offset, int stride, Vector128<ulong> pair)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref rows, offset), pair.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref rows, offset + stride), pair.GetElement(1));
    }

    /// <summary>
    /// Transposes an 8x8 byte matrix held as four vectors of two lines each (line 2k in the low half of vector k,
    /// line 2k + 1 in the high half); the result uses the same layout for the columns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(
        Vector128<byte> l01, Vector128<byte> l23, Vector128<byte> l45, Vector128<byte> l67,
        out Vector128<byte> c01, out Vector128<byte> c23, out Vector128<byte> c45, out Vector128<byte> c67)
    {
        // a = line 0, b = line 1, ... h = line 7.
        Vector128<byte> ac = ZipLow(l01, l23);  // a0 c0 a1 c1 ... a7 c7
        Vector128<byte> bd = ZipHigh(l01, l23); // b0 d0 b1 d1 ... b7 d7
        Vector128<byte> eg = ZipLow(l45, l67);
        Vector128<byte> fh = ZipHigh(l45, l67);
        Vector128<byte> abcd0 = ZipLow(ac, bd);  // a0 b0 c0 d0 a1 b1 c1 d1 ... a3 b3 c3 d3
        Vector128<byte> abcd4 = ZipHigh(ac, bd); // a4 b4 c4 d4 ... a7 b7 c7 d7
        Vector128<byte> efgh0 = ZipLow(eg, fh);
        Vector128<byte> efgh4 = ZipHigh(eg, fh);
        c01 = ZipLow(abcd0.AsUInt32(), efgh0.AsUInt32()).AsByte();
        c23 = ZipHigh(abcd0.AsUInt32(), efgh0.AsUInt32()).AsByte();
        c45 = ZipLow(abcd4.AsUInt32(), efgh4.AsUInt32()).AsByte();
        c67 = ZipHigh(abcd4.AsUInt32(), efgh4.AsUInt32()).AsByte();
    }

    /// <summary>Transposes a 4x4 matrix of 32-bit values held as four row vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose4x4(ref Vector128<int> a, ref Vector128<int> b, ref Vector128<int> c, ref Vector128<int> d)
    {
        Vector128<ulong> ab0 = ZipLow(a.AsUInt32(), b.AsUInt32()).AsUInt64();  // a0 b0 a1 b1
        Vector128<ulong> ab1 = ZipHigh(a.AsUInt32(), b.AsUInt32()).AsUInt64(); // a2 b2 a3 b3
        Vector128<ulong> cd0 = ZipLow(c.AsUInt32(), d.AsUInt32()).AsUInt64();
        Vector128<ulong> cd1 = ZipHigh(c.AsUInt32(), d.AsUInt32()).AsUInt64();
        a = ZipLow(ab0, cd0).AsInt32();  // a0 b0 c0 d0
        b = ZipHigh(ab0, cd0).AsInt32(); // a1 b1 c1 d1
        c = ZipLow(ab1, cd1).AsInt32();
        d = ZipHigh(ab1, cd1).AsInt32();
    }

    /// <summary>Byte shuffle with constant indices; an index of 0xFF selects zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Shuffle(Vector128<byte> source, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(source, indices);
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.VectorTableLookup(source, indices);
        }

        return Vector128.Shuffle(source, indices);
    }

    // The interleaves are the only operations here without a portable Vector128 equivalent. IsEnabled guarantees
    // that one of the two instruction sets is present, so the AdvSimd branch is never reached on x86 and vice versa.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ZipLow(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ZipHigh(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ZipLow(Vector128<uint> a, Vector128<uint> b)
        => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ZipHigh(Vector128<uint> a, Vector128<uint> b)
        => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> ZipLow(Vector128<ulong> a, Vector128<ulong> b)
        => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> ZipHigh(Vector128<ulong> a, Vector128<ulong> b)
        => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);
}
