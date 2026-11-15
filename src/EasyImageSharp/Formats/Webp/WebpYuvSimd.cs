using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Vector version of the interior of <see cref="WebpYuv"/>'s fancy upsampler: sixteen output pixels of two rows
/// per step, with the chroma blend and the YCbCr to RGB conversion evaluated in 16-bit lanes.
/// <para>
/// Each output pixel blends its nearest chroma sample N and the horizontal neighbour F of two chroma rows (the
/// nearer row R1, the farther row R2) as <c>(((R1[N] + 3 R1[F] + 3 R2[N] + R2[F] + 8) &gt;&gt; 3) + R1[N]) &gt;&gt; 1</c>,
/// which is the scalar code's diagonal formulation regrouped per output position. Gathering N and F for sixteen
/// consecutive pixels is one zero-extending byte shuffle per row and channel. The conversion uses the scalar
/// code's fixed-point constants and evaluates <c>(v * k) &gt;&gt; 8</c> exactly, so the result is bit-identical.
/// </para>
/// </summary>
internal static class WebpYuvSimd
{
    private const int YScale = 19077;
    private const int VToR = 26149;
    private const int UToG = 6419;
    private const int VToG = 13320;
    private const int UToB = 33050;
    private const int RBias = 14234;
    private const int GBias = 8708;
    private const int BBias = 17685;

    /// <summary>True when this machine has the 128-bit byte shuffle the kernel is built on (SSSE3 or AdvSimd).</summary>
    public static bool IsSupported
        => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>True when the 256-bit (AVX2) kernel may run.</summary>
    public static bool Is256Supported => Vector256.IsHardwareAccelerated && Avx2.IsSupported && BitConverter.IsLittleEndian;

    /// <summary>True when the vector path is enabled for this call (hardware present, not disabled through <see cref="SimdConfig"/>).</summary>
    public static bool IsEnabled => IsSupported && !SimdConfig.ForceScalarFallback;

    /// <summary>
    /// Converts the interior chroma columns of one pair of output rows, starting at column 1, for as long as a whole
    /// step of eight chroma columns fits before <paramref name="lastPair"/> and every load stays inside its array.
    /// Returns the first chroma column it did not convert; the caller finishes the row with the scalar code.
    /// A row offset of -1 for <paramref name="bottomY"/> means the pair has only its top row.
    /// </summary>
    public static int UpsamplePairInterior(
        byte[] y, int topY, int bottomY,
        byte[] u, byte[] v, int topUv, int curUv,
        Span<Rgba32> destination, int topDst, int bottomDst, int lastPair, bool allow256)
    {
        // Step at chroma column x: reads chroma [x - 1, x + 15) (the 256-bit step also [x + 3, x + 19)) of both rows,
        // luma [2x - 1, 2x + 15) of each output row, and writes output pixels [2x - 1, 2x + 15).
        bool wide = allow256 && Is256Supported && SimdConfig.Vector256Enabled;
        int chromaReach = wide ? 19 : 15;
        int chromaLimit = Math.Min(u.Length, v.Length) - Math.Max(topUv, curUv) - chromaReach;
        int lumaLimit = (y.Length - Math.Max(topY, bottomY) - 15) >> 1;
        int last = Math.Min(Math.Min(lastPair - 7, chromaLimit), lumaLimit);
        if (last < 1)
        {
            return 1;
        }

        Span<uint> pixels = MemoryMarshal.Cast<Rgba32, uint>(destination);
        ref uint topOut = ref MemoryMarshal.GetReference(pixels.Slice(topDst, (2 * last) + 15));
        ref uint bottomOut = ref topOut;
        if (bottomY >= 0)
        {
            bottomOut = ref MemoryMarshal.GetReference(pixels.Slice(bottomDst, (2 * last) + 15));
        }

        ref byte yRef = ref MemoryMarshal.GetArrayDataReference(y);
        ref byte uRef = ref MemoryMarshal.GetArrayDataReference(u);
        ref byte vRef = ref MemoryMarshal.GetArrayDataReference(v);
        int x = 1;
        if (wide)
        {
            for (; x <= last; x += 8)
            {
                Step256(ref yRef, topY, bottomY, ref uRef, ref vRef, topUv, curUv, ref topOut, ref bottomOut, x);
            }
        }
        else
        {
            for (; x <= last; x += 8)
            {
                Step128(ref yRef, topY, bottomY, ref uRef, ref vRef, topUv, curUv, ref topOut, ref bottomOut, x);
            }
        }

        return x;
    }

    // ----- 128-bit step -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step128(
        ref byte yRef, int topY, int bottomY, ref byte uRef, ref byte vRef, int topUv, int curUv,
        ref uint topOut, ref uint bottomOut, int x)
    {
        // Lane k of the "near" / "far" gathers is output pixel 2x - 1 + k; byte index 0 of each load is column x - 1.
        Vector128<byte> nearLow = Vector128.Create((byte)0, 0xFF, 1, 0xFF, 1, 0xFF, 2, 0xFF, 2, 0xFF, 3, 0xFF, 3, 0xFF, 4, 0xFF);
        Vector128<byte> nearHigh = Vector128.Create((byte)4, 0xFF, 5, 0xFF, 5, 0xFF, 6, 0xFF, 6, 0xFF, 7, 0xFF, 7, 0xFF, 8, 0xFF);
        Vector128<byte> farLow = Vector128.Create((byte)1, 0xFF, 0, 0xFF, 2, 0xFF, 1, 0xFF, 3, 0xFF, 2, 0xFF, 4, 0xFF, 3, 0xFF);
        Vector128<byte> farHigh = Vector128.Create((byte)5, 0xFF, 4, 0xFF, 6, 0xFF, 5, 0xFF, 7, 0xFF, 6, 0xFF, 8, 0xFF, 7, 0xFF);

        Vector128<byte> uTop = Vector128.LoadUnsafe(ref uRef, (nuint)(topUv + x - 1));
        Vector128<byte> uCur = Vector128.LoadUnsafe(ref uRef, (nuint)(curUv + x - 1));
        Vector128<byte> vTop = Vector128.LoadUnsafe(ref vRef, (nuint)(topUv + x - 1));
        Vector128<byte> vCur = Vector128.LoadUnsafe(ref vRef, (nuint)(curUv + x - 1));

        Blend(Shuffle(uTop, nearLow), Shuffle(uTop, farLow), Shuffle(uCur, nearLow), Shuffle(uCur, farLow), out Vector128<ushort> uTopLow, out Vector128<ushort> uBottomLow);
        Blend(Shuffle(uTop, nearHigh), Shuffle(uTop, farHigh), Shuffle(uCur, nearHigh), Shuffle(uCur, farHigh), out Vector128<ushort> uTopHigh, out Vector128<ushort> uBottomHigh);
        Blend(Shuffle(vTop, nearLow), Shuffle(vTop, farLow), Shuffle(vCur, nearLow), Shuffle(vCur, farLow), out Vector128<ushort> vTopLow, out Vector128<ushort> vBottomLow);
        Blend(Shuffle(vTop, nearHigh), Shuffle(vTop, farHigh), Shuffle(vCur, nearHigh), Shuffle(vCur, farHigh), out Vector128<ushort> vTopHigh, out Vector128<ushort> vBottomHigh);

        int pixel = (2 * x) - 1;
        Vector128<byte> luma = Vector128.LoadUnsafe(ref yRef, (nuint)(topY + pixel));
        ref uint top = ref Unsafe.Add(ref topOut, pixel);
        StoreRgba(Vector128.WidenLower(luma), uTopLow, vTopLow, ref top);
        StoreRgba(Vector128.WidenUpper(luma), uTopHigh, vTopHigh, ref Unsafe.Add(ref top, 8));
        if (bottomY >= 0)
        {
            luma = Vector128.LoadUnsafe(ref yRef, (nuint)(bottomY + pixel));
            ref uint bottom = ref Unsafe.Add(ref bottomOut, pixel);
            StoreRgba(Vector128.WidenLower(luma), uBottomLow, vBottomLow, ref bottom);
            StoreRgba(Vector128.WidenUpper(luma), uBottomHigh, vBottomHigh, ref Unsafe.Add(ref bottom, 8));
        }
    }

    /// <summary>The chroma blend of eight output pixels of the top row (nearer to <c>t</c>) and of the bottom row (nearer to <c>c</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Blend(
        Vector128<ushort> tNear, Vector128<ushort> tFar, Vector128<ushort> cNear, Vector128<ushort> cFar,
        out Vector128<ushort> top, out Vector128<ushort> bottom)
    {
        Vector128<ushort> a = tNear + cFar;
        Vector128<ushort> b = tFar + cNear;
        Vector128<ushort> eight = Vector128.Create((ushort)8);
        top = Vector128.ShiftRightLogical(Vector128.ShiftRightLogical(a + (b * 3) + eight, 3) + tNear, 1);
        bottom = Vector128.ShiftRightLogical(Vector128.ShiftRightLogical(b + (a * 3) + eight, 3) + cNear, 1);
    }

    /// <summary>Converts eight YUV samples (each in [0, 255]) to opaque RGBA and stores the eight pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRgba(Vector128<ushort> luma, Vector128<ushort> u, Vector128<ushort> v, ref uint destination)
    {
        Vector128<ushort> scaledY = MulHi(luma, YScale);

        // R and G are exact in 16-bit two's complement: their true values lie in [-14234, 30814] and
        // [-10953, 27711], so wrapped intermediate sums cancel out before the arithmetic shift.
        Vector128<short> r = (scaledY + MulHi(v, VToR) - Vector128.Create((ushort)RBias)).AsInt16();
        Vector128<short> g = (scaledY + Vector128.Create((ushort)GBias) - MulHi(u, UToG) - MulHi(v, VToG)).AsInt16();

        // B reaches 34238 and needs the unsigned range: the subtraction saturates at zero, the scalar clip's lower bound.
        Vector128<ushort> b = scaledY + MulHi(u, UToB);
        Vector128<ushort> bias = Vector128.Create((ushort)BBias);
        b = Vector128.Max(b, bias) - bias;

        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        Vector128<ushort> red = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(r, 6), zero), max).AsUInt16();
        Vector128<ushort> green = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(g, 6), zero), max).AsUInt16();
        Vector128<ushort> blue = Vector128.Min(Vector128.ShiftRightLogical(b, 6), Vector128.Create((ushort)255));

        Vector128<ushort> redGreen = red | Vector128.ShiftLeft(green, 8);
        Vector128<ushort> blueAlpha = blue | Vector128.Create((ushort)0xFF00);
        ZipLow(redGreen, blueAlpha).AsUInt32().StoreUnsafe(ref destination);
        ZipHigh(redGreen, blueAlpha).AsUInt32().StoreUnsafe(ref destination, 4);
    }

    /// <summary><c>(v * k) &gt;&gt; 8</c> for <c>v</c> in [0, 255] and a 16-bit constant <paramref name="k"/>, exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> MulHi(Vector128<ushort> v, int k)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyHigh(Vector128.ShiftLeft(v, 8), Vector128.Create((ushort)k));
        }

        // (v * k) >> 8 == v * (k >> 8) + ((v * (k & 255)) >> 8): both products stay below 2^16 for v <= 255.
        return (v * (ushort)(k >> 8)) + Vector128.ShiftRightLogical(v * (ushort)(k & 255), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Shuffle(Vector128<byte> source, Vector128<byte> indices)
    {
        // Index 0xFF selects zero in all three forms, which zero-extends each gathered byte to 16 bits.
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(source, indices).AsUInt16();
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.VectorTableLookup(source, indices).AsUInt16();
        }

        return Vector128.Shuffle(source, indices).AsUInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> ZipLow(Vector128<ushort> a, Vector128<ushort> b)
        => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> ZipHigh(Vector128<ushort> a, Vector128<ushort> b)
        => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    // ----- 256-bit (AVX2) step -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step256(
        ref byte yRef, int topY, int bottomY, ref byte uRef, ref byte vRef, int topUv, int curUv,
        ref uint topOut, ref uint bottomOut, int x)
    {
        // The upper 128-bit lane is loaded four chroma columns further on, so the in-lane shuffle pattern of the low
        // lane (output pixels 0-7) produces output pixels 8-15 in the upper lane.
        Vector256<byte> near = Vector256.Create((byte)0, 0xFF, 1, 0xFF, 1, 0xFF, 2, 0xFF, 2, 0xFF, 3, 0xFF, 3, 0xFF, 4, 0xFF, 0, 0xFF, 1, 0xFF, 1, 0xFF, 2, 0xFF, 2, 0xFF, 3, 0xFF, 3, 0xFF, 4, 0xFF);
        Vector256<byte> far = Vector256.Create((byte)1, 0xFF, 0, 0xFF, 2, 0xFF, 1, 0xFF, 3, 0xFF, 2, 0xFF, 4, 0xFF, 3, 0xFF, 1, 0xFF, 0, 0xFF, 2, 0xFF, 1, 0xFF, 3, 0xFF, 2, 0xFF, 4, 0xFF, 3, 0xFF);

        Vector256<byte> uTop = LoadChroma(ref uRef, topUv + x - 1);
        Vector256<byte> uCur = LoadChroma(ref uRef, curUv + x - 1);
        Vector256<byte> vTop = LoadChroma(ref vRef, topUv + x - 1);
        Vector256<byte> vCur = LoadChroma(ref vRef, curUv + x - 1);

        Blend(
            Avx2.Shuffle(uTop, near).AsUInt16(), Avx2.Shuffle(uTop, far).AsUInt16(), Avx2.Shuffle(uCur, near).AsUInt16(), Avx2.Shuffle(uCur, far).AsUInt16(),
            out Vector256<ushort> uTopRow, out Vector256<ushort> uBottomRow);
        Blend(
            Avx2.Shuffle(vTop, near).AsUInt16(), Avx2.Shuffle(vTop, far).AsUInt16(), Avx2.Shuffle(vCur, near).AsUInt16(), Avx2.Shuffle(vCur, far).AsUInt16(),
            out Vector256<ushort> vTopRow, out Vector256<ushort> vBottomRow);

        int pixel = (2 * x) - 1;
        Vector256<ushort> luma = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref yRef, (nuint)(topY + pixel))).AsUInt16();
        StoreRgba(luma, uTopRow, vTopRow, ref Unsafe.Add(ref topOut, pixel));
        if (bottomY >= 0)
        {
            luma = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref yRef, (nuint)(bottomY + pixel))).AsUInt16();
            StoreRgba(luma, uBottomRow, vBottomRow, ref Unsafe.Add(ref bottomOut, pixel));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> LoadChroma(ref byte plane, int offset)
        => Vector256.Create(Vector128.LoadUnsafe(ref plane, (nuint)offset), Vector128.LoadUnsafe(ref plane, (nuint)(offset + 4)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Blend(
        Vector256<ushort> tNear, Vector256<ushort> tFar, Vector256<ushort> cNear, Vector256<ushort> cFar,
        out Vector256<ushort> top, out Vector256<ushort> bottom)
    {
        Vector256<ushort> a = tNear + cFar;
        Vector256<ushort> b = tFar + cNear;
        Vector256<ushort> eight = Vector256.Create((ushort)8);
        top = Vector256.ShiftRightLogical(Vector256.ShiftRightLogical(a + (b * 3) + eight, 3) + tNear, 1);
        bottom = Vector256.ShiftRightLogical(Vector256.ShiftRightLogical(b + (a * 3) + eight, 3) + cNear, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRgba(Vector256<ushort> luma, Vector256<ushort> u, Vector256<ushort> v, ref uint destination)
    {
        Vector256<ushort> scaledY = MulHi(luma, YScale);
        Vector256<short> r = (scaledY + MulHi(v, VToR) - Vector256.Create((ushort)RBias)).AsInt16();
        Vector256<short> g = (scaledY + Vector256.Create((ushort)GBias) - MulHi(u, UToG) - MulHi(v, VToG)).AsInt16();
        Vector256<ushort> b = scaledY + MulHi(u, UToB);
        Vector256<ushort> bias = Vector256.Create((ushort)BBias);
        b = Vector256.Max(b, bias) - bias;

        Vector256<short> zero = Vector256<short>.Zero;
        Vector256<short> max = Vector256.Create((short)255);
        Vector256<ushort> red = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(r, 6), zero), max).AsUInt16();
        Vector256<ushort> green = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(g, 6), zero), max).AsUInt16();
        Vector256<ushort> blue = Vector256.Min(Vector256.ShiftRightLogical(b, 6), Vector256.Create((ushort)255));

        Vector256<ushort> redGreen = red | Vector256.ShiftLeft(green, 8);
        Vector256<ushort> blueAlpha = blue | Vector256.Create((ushort)0xFF00);

        // The in-lane interleaves give pixels 0-3 | 8-11 and 4-7 | 12-15; the lane permutes restore raster order.
        Vector256<ushort> low = Avx2.UnpackLow(redGreen, blueAlpha);
        Vector256<ushort> high = Avx2.UnpackHigh(redGreen, blueAlpha);
        Avx2.Permute2x128(low, high, 0x20).AsUInt32().StoreUnsafe(ref destination);
        Avx2.Permute2x128(low, high, 0x31).AsUInt32().StoreUnsafe(ref destination, 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> MulHi(Vector256<ushort> v, int k)
        => Avx2.MultiplyHigh(Vector256.ShiftLeft(v, 8), Vector256.Create((ushort)k));
}
