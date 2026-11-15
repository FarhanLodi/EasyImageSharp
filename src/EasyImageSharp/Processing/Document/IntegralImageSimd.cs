using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp.Processing;

/// <summary>
/// Vector kernels behind the local-statistics engine: the window mean and variance of a whole row of interior
/// pixels from an <see cref="IntegralImage"/>, and the Niblack, Wolf-Jolion and NICK threshold formulas over those
/// row buffers, plus the Sauvola formula for callers that keep their own summed-area tables.
/// <para>
/// The interior windows of a row all have the same size, so their four corner sums are contiguous runs of the
/// summed-area table and load as vectors. The 64-bit window sums are non-negative and far below 2^52, so they are
/// converted to doubles exactly with the exponent-bias trick. Every floating-point operation is then the scalar
/// formula's own operation in the same order (IEEE division and square root are correctly rounded in vector and
/// scalar form alike, and nothing is fused), so the results are bit-identical to the scalar code.
/// </para>
/// </summary>
internal static class IntegralImageSimd
{
    /// <summary>2^52 as a double; OR-ing its bit pattern onto an integer in [0, 2^52) gives 2^52 + that integer.</summary>
    private const double TwoPow52 = 4503599627370496.0;

    /// <summary>True when at least the 128-bit kernels may run.</summary>
    public static bool IsEnabled => SimdConfig.Vector128Enabled && Vector128<double>.IsSupported;

    /// <summary>
    /// <see cref="IntegralImage.WindowStats"/> for the interior pixels <c>[x, lastX]</c> of row <paramref name="y"/>
    /// (pixels whose window of half-size <paramref name="half"/> is not clipped horizontally). Returns the first
    /// pixel it did not process; the caller finishes with the scalar formula.
    /// </summary>
    public static int InteriorRowStats(
        long[] sum, long[] sumSq, int stride, int height, int y, int half, int x, int lastX,
        Span<double> mean, Span<double> variance, Span<int> area)
    {
        int y1 = Math.Max(0, y - half);
        int y2 = Math.Min(height - 1, y + half);
        int top = y1 * stride;
        int bottom = (y2 + 1) * stride;

        // The same int product as the scalar (x2 - x1 + 1) * (y2 - y1 + 1).
        int windowArea = ((2 * half) + 1) * (y2 - y1 + 1);
        double areaValue = windowArea;

        // Every load below reads table entries [top + x - half, bottom + lastX + half + 1], which the spans check once.
        ref long sums = ref MemoryMarshal.GetArrayDataReference(sum);
        ref long squares = ref MemoryMarshal.GetArrayDataReference(sumSq);
        _ = sum.AsSpan(top + x - half, bottom + lastX + half + 2 - (top + x - half));
        _ = sumSq.AsSpan(top + x - half, bottom + lastX + half + 2 - (top + x - half));
        ref double meanOut = ref MemoryMarshal.GetReference(mean[..(lastX + 1)]);
        ref double varianceOut = ref MemoryMarshal.GetReference(variance[..(lastX + 1)]);
        area[x..(lastX + 1)].Fill(windowArea);

        if (SimdConfig.Vector256Enabled && Vector256<double>.IsSupported)
        {
            Vector256<double> divisor = Vector256.Create(areaValue);
            for (; x + Vector256<long>.Count - 1 <= lastX; x += Vector256<long>.Count)
            {
                Vector256<long> windowSum = WindowSum256(ref sums, top, bottom, x, half);
                Vector256<long> windowSq = WindowSum256(ref squares, top, bottom, x, half);
                Vector256<double> m = ToDouble(windowSum) / divisor;
                Vector256<double> meanSq = ToDouble(windowSq) / divisor;
                Vector256<double> v = meanSq - (m * m);
                v = Vector256.ConditionalSelect(Vector256.GreaterThan(v, Vector256<double>.Zero), v, Vector256<double>.Zero);
                m.StoreUnsafe(ref meanOut, (nuint)x);
                v.StoreUnsafe(ref varianceOut, (nuint)x);
            }
        }

        Vector128<double> divisor128 = Vector128.Create(areaValue);
        for (; x + Vector128<long>.Count - 1 <= lastX; x += Vector128<long>.Count)
        {
            Vector128<long> windowSum = WindowSum128(ref sums, top, bottom, x, half);
            Vector128<long> windowSq = WindowSum128(ref squares, top, bottom, x, half);
            Vector128<double> m = ToDouble(windowSum) / divisor128;
            Vector128<double> meanSq = ToDouble(windowSq) / divisor128;
            Vector128<double> v = meanSq - (m * m);
            v = Vector128.ConditionalSelect(Vector128.GreaterThan(v, Vector128<double>.Zero), v, Vector128<double>.Zero);
            m.StoreUnsafe(ref meanOut, (nuint)x);
            v.StoreUnsafe(ref varianceOut, (nuint)x);
        }

        return x;
    }

    /// <summary>
    /// Evaluates the Niblack, Wolf-Jolion or NICK threshold for as many leading pixels of a row as whole vectors
    /// cover, with exactly the operations of <see cref="LocalThresholdOps"/>' scalar formulas. Returns the number of
    /// pixels written, or 0 for a formula that has no vector form (Phansalkar needs <see cref="Math.Exp"/>).
    /// </summary>
    /// <param name="kind">The formula.</param>
    /// <param name="mean">Window means of the row.</param>
    /// <param name="variance">Window variances of the row.</param>
    /// <param name="area">Window areas of the row.</param>
    /// <param name="k">The formula's k, already widened from <see cref="float"/>.</param>
    /// <param name="oneMinusK">Wolf-Jolion's <c>1 - k</c>, evaluated in single precision as the scalar code does, then widened.</param>
    /// <param name="minM">Wolf-Jolion's global minimum grey level.</param>
    /// <param name="r">Wolf-Jolion's largest local standard deviation.</param>
    /// <param name="threshold">Receives the thresholds.</param>
    public static int Thresholds(
        LocalThresholdOps.Kind kind, ReadOnlySpan<double> mean, ReadOnlySpan<double> variance, ReadOnlySpan<int> area,
        double k, double oneMinusK, double minM, double r, Span<double> threshold)
    {
        if (kind == LocalThresholdOps.Kind.Phansalkar)
        {
            return 0;
        }

        int width = Math.Min(Math.Min(mean.Length, variance.Length), Math.Min(area.Length, threshold.Length));
        ref double meanIn = ref MemoryMarshal.GetReference(mean);
        ref double varianceIn = ref MemoryMarshal.GetReference(variance);
        ref int areaIn = ref MemoryMarshal.GetReference(area);
        ref double output = ref MemoryMarshal.GetReference(threshold);
        Vector128<double> k2 = Vector128.Create(k);
        Vector128<double> oneMinusK2 = Vector128.Create(oneMinusK);
        Vector128<double> minM2 = Vector128.Create(minM);
        Vector128<double> r2 = Vector128.Create(r);
        int x = 0;

        if (SimdConfig.Vector256Enabled && Vector256<double>.IsSupported)
        {
            Vector256<double> k4 = Vector256.Create(k);
            Vector256<double> oneMinusK4 = Vector256.Create(oneMinusK);
            Vector256<double> minM4 = Vector256.Create(minM);
            Vector256<double> r4 = Vector256.Create(r);
            for (; x <= width - Vector256<double>.Count; x += Vector256<double>.Count)
            {
                Vector256<double> m = Vector256.LoadUnsafe(ref meanIn, (nuint)x);
                Vector256<double> v = Vector256.LoadUnsafe(ref varianceIn, (nuint)x);
                Vector256<double> t;
                switch (kind)
                {
                    case LocalThresholdOps.Kind.Niblack:
                        t = m + (k4 * Vector256.Sqrt(v));
                        break;
                    case LocalThresholdOps.Kind.WolfJolion:
                        t = (oneMinusK4 * m) + (k4 * minM4) + (k4 * (Vector256.Sqrt(v) / r4) * (m - minM4));
                        break;
                    default:
                    {
                        Vector256<double> squared = m * m;
                        Vector256<double> a = Vector256.Create(
                            (double)Unsafe.Add(ref areaIn, x), Unsafe.Add(ref areaIn, x + 1), Unsafe.Add(ref areaIn, x + 2), Unsafe.Add(ref areaIn, x + 3));
                        Vector256<double> inner = v + squared - (squared / a);
                        inner = Vector256.ConditionalSelect(Vector256.GreaterThan(inner, Vector256<double>.Zero), inner, Vector256<double>.Zero);
                        t = m + (k4 * Vector256.Sqrt(inner));
                        break;
                    }
                }

                t.StoreUnsafe(ref output, (nuint)x);
            }
        }

        for (; x <= width - Vector128<double>.Count; x += Vector128<double>.Count)
        {
            Vector128<double> m = Vector128.LoadUnsafe(ref meanIn, (nuint)x);
            Vector128<double> v = Vector128.LoadUnsafe(ref varianceIn, (nuint)x);
            Vector128<double> t;
            switch (kind)
            {
                case LocalThresholdOps.Kind.Niblack:
                    t = m + (k2 * Vector128.Sqrt(v));
                    break;
                case LocalThresholdOps.Kind.WolfJolion:
                    t = (oneMinusK2 * m) + (k2 * minM2) + (k2 * (Vector128.Sqrt(v) / r2) * (m - minM2));
                    break;
                default:
                {
                    Vector128<double> squared = m * m;
                    Vector128<double> a = Vector128.Create((double)Unsafe.Add(ref areaIn, x), Unsafe.Add(ref areaIn, x + 1));
                    Vector128<double> inner = v + squared - (squared / a);
                    inner = Vector128.ConditionalSelect(Vector128.GreaterThan(inner, Vector128<double>.Zero), inner, Vector128<double>.Zero);
                    t = m + (k2 * Vector128.Sqrt(inner));
                    break;
                }
            }

            t.StoreUnsafe(ref output, (nuint)x);
        }

        return x;
    }

    /// <summary>
    /// Sauvola thresholds <c>mean * (1 + k * (sqrt(variance) / r - 1))</c> for as many leading pixels of a row as whole
    /// vectors cover, with exactly the operations of the scalar Sauvola formula (k widened from <see cref="float"/>).
    /// Returns the number of pixels written; the caller finishes the row with the scalar formula.
    /// </summary>
    public static int SauvolaThresholds(ReadOnlySpan<double> mean, ReadOnlySpan<double> variance, double k, double r, Span<double> threshold)
    {
        int width = Math.Min(Math.Min(mean.Length, variance.Length), threshold.Length);
        ref double meanIn = ref MemoryMarshal.GetReference(mean);
        ref double varianceIn = ref MemoryMarshal.GetReference(variance);
        ref double output = ref MemoryMarshal.GetReference(threshold);
        int x = 0;
        if (SimdConfig.Vector256Enabled && Vector256<double>.IsSupported)
        {
            Vector256<double> k4 = Vector256.Create(k);
            Vector256<double> r4 = Vector256.Create(r);
            Vector256<double> one = Vector256.Create(1.0);
            for (; x <= width - Vector256<double>.Count; x += Vector256<double>.Count)
            {
                Vector256<double> m = Vector256.LoadUnsafe(ref meanIn, (nuint)x);
                Vector256<double> s = Vector256.Sqrt(Vector256.LoadUnsafe(ref varianceIn, (nuint)x));
                (m * (one + (k4 * ((s / r4) - one)))).StoreUnsafe(ref output, (nuint)x);
            }
        }

        Vector128<double> k2 = Vector128.Create(k);
        Vector128<double> r2 = Vector128.Create(r);
        Vector128<double> one2 = Vector128.Create(1.0);
        for (; x <= width - Vector128<double>.Count; x += Vector128<double>.Count)
        {
            Vector128<double> m = Vector128.LoadUnsafe(ref meanIn, (nuint)x);
            Vector128<double> s = Vector128.Sqrt(Vector128.LoadUnsafe(ref varianceIn, (nuint)x));
            (m * (one2 + (k2 * ((s / r2) - one2)))).StoreUnsafe(ref output, (nuint)x);
        }

        return x;
    }

    /// <summary><c>row[i] += previous[i]</c>, the vertical accumulation of a summed-area table (exact integer adds).</summary>
    public static void AddRow(Span<long> row, ReadOnlySpan<long> previous)
    {
        int length = row.Length;
        ref long target = ref MemoryMarshal.GetReference(row);
        ref long source = ref MemoryMarshal.GetReference(previous[..length]);
        int x = 0;
        if (SimdConfig.Vector256Enabled)
        {
            for (; x <= length - Vector256<long>.Count; x += Vector256<long>.Count)
            {
                (Vector256.LoadUnsafe(ref target, (nuint)x) + Vector256.LoadUnsafe(ref source, (nuint)x)).StoreUnsafe(ref target, (nuint)x);
            }
        }

        for (; x <= length - Vector128<long>.Count; x += Vector128<long>.Count)
        {
            (Vector128.LoadUnsafe(ref target, (nuint)x) + Vector128.LoadUnsafe(ref source, (nuint)x)).StoreUnsafe(ref target, (nuint)x);
        }

        for (; x < length; x++)
        {
            Unsafe.Add(ref target, x) += Unsafe.Add(ref source, x);
        }
    }

    /// <summary>Largest value of a row of non-negative, finite variances (the order of the comparisons cannot matter).</summary>
    public static double Max(ReadOnlySpan<double> values, double seed)
    {
        ref double input = ref MemoryMarshal.GetReference(values);
        int x = 0;
        double best = seed;
        if (values.Length >= Vector128<double>.Count)
        {
            Vector128<double> max = Vector128.Create(seed);
            for (; x <= values.Length - Vector128<double>.Count; x += Vector128<double>.Count)
            {
                Vector128<double> v = Vector128.LoadUnsafe(ref input, (nuint)x);
                max = Vector128.ConditionalSelect(Vector128.GreaterThan(v, max), v, max);
            }

            best = Math.Max(max.GetElement(0), max.GetElement(1));
        }

        for (; x < values.Length; x++)
        {
            if (values[x] > best)
            {
                best = values[x];
            }
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<long> WindowSum256(ref long table, int top, int bottom, int x, int half)
    {
        nuint right = (nuint)(x + half + 1);
        nuint left = (nuint)(x - half);
        return Vector256.LoadUnsafe(ref table, (nuint)bottom + right)
            - Vector256.LoadUnsafe(ref table, (nuint)top + right)
            - Vector256.LoadUnsafe(ref table, (nuint)bottom + left)
            + Vector256.LoadUnsafe(ref table, (nuint)top + left);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> WindowSum128(ref long table, int top, int bottom, int x, int half)
    {
        nuint right = (nuint)(x + half + 1);
        nuint left = (nuint)(x - half);
        return Vector128.LoadUnsafe(ref table, (nuint)bottom + right)
            - Vector128.LoadUnsafe(ref table, (nuint)top + right)
            - Vector128.LoadUnsafe(ref table, (nuint)bottom + left)
            + Vector128.LoadUnsafe(ref table, (nuint)top + left);
    }

    /// <summary>Exact conversion of integers in [0, 2^52) to double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> ToDouble(Vector256<long> value)
    {
        Vector256<double> bias = Vector256.Create(TwoPow52);
        return (value | bias.AsInt64()).AsDouble() - bias;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> ToDouble(Vector128<long> value)
    {
        Vector128<double> bias = Vector128.Create(TwoPow52);
        return (value | bias.AsInt64()).AsDouble() - bias;
    }
}
