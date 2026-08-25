using System.Runtime.CompilerServices;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Rate estimation for the VP8 encoder: the entropy cost of a boolean, the cost of a coefficient level under
/// a given token probability set, and the cost of a whole residual block. All costs are in 1/256 of a bit so
/// that a rate-distortion score can stay in integer arithmetic.
/// </summary>
internal static class Vp8Cost
{
    /// <summary>Largest coefficient magnitude representable by the token trees.</summary>
    public const int MaxLevel = 2047;

    /// <summary>Above this magnitude the context-dependent part of a level's cost stops changing.</summary>
    public const int MaxVariableLevel = 67;

    /// <summary>Number of probabilities in one [band][context] group.</summary>
    public const int NumProbas = 11;

    /// <summary>Size of one coefficient type's probability block.</summary>
    public const int TypeStride = 8 * 3 * NumProbas;

    /// <summary>Total number of adaptive token probabilities in a frame.</summary>
    public const int ProbaCount = 4 * TypeStride;

    /// <summary>Number of level-cost entries stored per [type][band][context] group.</summary>
    public const int LevelTableStride = MaxVariableLevel + 1;

    /// <summary>Total size of the level-cost table.</summary>
    public const int LevelTableSize = 4 * 8 * 3 * LevelTableStride;

    private static readonly ushort[] EntropyCostTable = BuildEntropyCost();
    private static readonly ushort[] FixedLevelCostTable = BuildFixedLevelCost();

    /// <summary>Cost, in 1/256 bit, of coding <paramref name="bit"/> when zero has probability <paramref name="prob"/>/256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bit(int bit, int prob) => bit != 0 ? EntropyCostTable[256 - prob] : EntropyCostTable[prob];

    /// <summary>Index of the first probability of a [type][band][context] group inside a flat probability array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ProbaIndex(int type, int band, int ctx) => (((type * 8) + band) * 3 * NumProbas) + (ctx * NumProbas);

    /// <summary>Index of a [type][band][context] group inside the level-cost table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CostIndex(int type, int band, int ctx) => ((((type * 8) + band) * 3) + ctx) * LevelTableStride;

    /// <summary>Total cost of coding a coefficient of magnitude <paramref name="level"/> from a prepared table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LevelCost(ushort[] table, int offset, int level)
        => FixedLevelCostTable[level] + table[offset + (level > MaxVariableLevel ? MaxVariableLevel : level)];

    /// <summary>
    /// Fills <paramref name="table"/> with the cost of every coefficient magnitude in every
    /// [type][band][context] group, given the frame's token probabilities.
    /// </summary>
    public static void BuildLevelCosts(byte[] probas, ushort[] table)
    {
        for (int type = 0; type < 4; type++)
        {
            for (int band = 0; band < 8; band++)
            {
                for (int ctx = 0; ctx < 3; ctx++)
                {
                    int p = ProbaIndex(type, band, ctx);
                    int t = CostIndex(type, band, ctx);

                    // The "not end of block" bit belongs to every token except the one that follows a zero,
                    // which is exactly the context-zero case, so it is folded into the table here.
                    int cost0 = ctx > 0 ? Bit(1, probas[p]) : 0;
                    int costBase = Bit(1, probas[p + 1]) + cost0;
                    table[t] = (ushort)(Bit(0, probas[p + 1]) + cost0);
                    for (int v = 1; v <= MaxVariableLevel; v++)
                    {
                        table[t + v] = (ushort)(costBase + VariableLevelCost(v, probas, p));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Cost of the residual of one 4x4 block, in 1/256 bit. <paramref name="levels"/> holds the quantized
    /// magnitudes in zig-zag order and <paramref name="last"/> is the index of the last non-zero one, or -1.
    /// </summary>
    public static int ResidualCost(
        short[] levels, int off, int first, int last, int type, int ctx0, byte[] probas, ushort[] costs)
    {
        ReadOnlySpan<byte> bands = Vp8EncoderTables.Bands;
        int p0 = probas[ProbaIndex(type, bands[first], ctx0)];
        if (last < 0)
        {
            return Bit(0, p0);
        }

        // The tables already carry the "not end of block" bit for non-zero contexts; add it back otherwise.
        int cost = ctx0 == 0 ? Bit(1, p0) : 0;
        int t = CostIndex(type, bands[first], ctx0);
        int n = first;
        for (; n < last; n++)
        {
            int v = Math.Abs(levels[off + n]);
            cost += LevelCost(costs, t, v);
            t = CostIndex(type, bands[n + 1], v >= 2 ? 2 : v);
        }

        {
            int v = Math.Abs(levels[off + n]);
            cost += LevelCost(costs, t, v);
            if (n < 15)
            {
                // The block ends here, so an explicit end-of-block bit follows the last coefficient.
                cost += Bit(0, probas[ProbaIndex(type, bands[n + 1], v == 1 ? 1 : 2)]);
            }
        }

        return cost;
    }

    /// <summary>Index of the last non-zero entry of a zig-zag ordered level block, or -1 when it is empty.</summary>
    public static int LastNonZero(short[] levels, int off)
    {
        for (int n = 15; n >= 0; n--)
        {
            if (levels[off + n] != 0)
            {
                return n;
            }
        }

        return -1;
    }

    private static int VariableLevelCost(int level, byte[] p, int off)
    {
        if (level == 1)
        {
            return Bit(0, p[off + 2]);
        }

        int cost = Bit(1, p[off + 2]);
        if (level <= 4)
        {
            cost += Bit(0, p[off + 3]);
            cost += level == 2 ? Bit(0, p[off + 4]) : Bit(1, p[off + 4]) + Bit(level == 4 ? 1 : 0, p[off + 5]);
            return cost;
        }

        cost += Bit(1, p[off + 3]);
        if (level <= 10)
        {
            cost += Bit(0, p[off + 6]);
            cost += level <= 6 ? Bit(0, p[off + 7]) : Bit(1, p[off + 7]);
            return cost;
        }

        cost += Bit(1, p[off + 6]);
        if (level <= 18)
        {
            return cost + Bit(0, p[off + 8]) + Bit(0, p[off + 9]);
        }

        if (level <= 34)
        {
            return cost + Bit(0, p[off + 8]) + Bit(1, p[off + 9]);
        }

        return level <= 66
            ? cost + Bit(1, p[off + 8]) + Bit(0, p[off + 10])
            : cost + Bit(1, p[off + 8]) + Bit(1, p[off + 10]);
    }

    private static ushort[] BuildEntropyCost()
    {
        // Entry q is the cost of an event of probability q/256, so index 0 only ever stands in for an
        // impossible symbol and is capped rather than infinite.
        var table = new ushort[257];
        table[0] = 4096;
        for (int q = 1; q <= 256; q++)
        {
            double bits = -Math.Log2(q / 256.0) * 256.0;
            table[q] = (ushort)Math.Round(bits);
        }

        return table;
    }

    private static ushort[] BuildFixedLevelCost()
    {
        var table = new ushort[MaxLevel + 1];
        for (int v = 1; v <= MaxLevel; v++)
        {
            int cost = 256; // The sign bit is always coded with even probability.
            if (v >= 5)
            {
                if (v <= 6)
                {
                    cost += Bit(v - 5, 159);
                }
                else if (v <= 10)
                {
                    cost += Bit((v - 7) >> 1, 165) + Bit((v - 7) & 1, 145);
                }
                else
                {
                    ReadOnlySpan<byte> cat;
                    int baseValue;
                    if (v <= 18)
                    {
                        cat = Vp8EncoderTables.Cat3;
                        baseValue = 11;
                    }
                    else if (v <= 34)
                    {
                        cat = Vp8EncoderTables.Cat4;
                        baseValue = 19;
                    }
                    else if (v <= 66)
                    {
                        cat = Vp8EncoderTables.Cat5;
                        baseValue = 35;
                    }
                    else
                    {
                        cat = Vp8EncoderTables.Cat6;
                        baseValue = 67;
                    }

                    int extra = cat.Length - 1; // The tables are zero terminated.
                    int residual = v - baseValue;
                    for (int i = 0; i < extra; i++)
                    {
                        cost += Bit((residual >> (extra - 1 - i)) & 1, cat[i]);
                    }
                }
            }

            table[v] = (ushort)cost;
        }

        return table;
    }
}

/// <summary>
/// One quantisation matrix: the step size, its reciprocal, the rounding bias and the resulting zero
/// threshold for each of the sixteen coefficient positions (RFC 6386 section 14.1 plus the encoder-side
/// bias and frequency sharpening that libwebp documents).
/// </summary>
internal sealed class Vp8Matrix
{
    private const int QFix = 17;

    private readonly ushort[] q = new ushort[16];
    private readonly int[] iq = new int[16];
    private readonly int[] bias = new int[16];
    private readonly int[] zeroThreshold = new int[16];
    private readonly int[] sharpen = new int[16];

    /// <summary>Sets the DC and AC step sizes; call <see cref="Expand"/> afterwards.</summary>
    public void SetSteps(int dc, int ac)
    {
        this.q[0] = (ushort)dc;
        this.q[1] = (ushort)ac;
    }

    /// <summary>
    /// Derives the reciprocals, biases and thresholds. <paramref name="type"/> selects the bias pair
    /// (0 = luma, 1 = the second-order luma DC block, 2 = chroma); only luma is frequency sharpened.
    /// Returns the average step size, which drives the rate-distortion lambdas.
    /// </summary>
    public int Expand(int type)
    {
        ReadOnlySpan<byte> biasTable = Vp8EncoderTables.QuantBias;
        for (int i = 0; i < 2; i++)
        {
            this.iq[i] = (1 << QFix) / this.q[i];
            this.bias[i] = biasTable[(type * 2) + i] << (QFix - 8);
            this.zeroThreshold[i] = ((1 << QFix) - 1 - this.bias[i]) / this.iq[i];
        }

        for (int i = 2; i < 16; i++)
        {
            this.q[i] = this.q[1];
            this.iq[i] = this.iq[1];
            this.bias[i] = this.bias[1];
            this.zeroThreshold[i] = this.zeroThreshold[1];
        }

        int sum = 0;
        ReadOnlySpan<byte> sharpening = Vp8EncoderTables.FreqSharpening;
        for (int i = 0; i < 16; i++)
        {
            this.sharpen[i] = type == 0 ? (sharpening[i] * this.q[1]) >> 11 : 0;
            sum += this.q[i];
        }

        return (sum + 8) >> 4;
    }

    /// <summary>
    /// Quantises <paramref name="coefficients"/> in place, replacing each entry with the value the decoder
    /// will dequantise, and writes the zig-zag ordered magnitudes to <paramref name="levels"/>. Returns the
    /// index of the last non-zero level, or -1.
    /// </summary>
    public int Quantize(short[] coefficients, int coeffOff, short[] levels, int levelOff)
    {
        ReadOnlySpan<byte> zigzag = Vp8EncoderTables.Zigzag;
        int last = -1;
        for (int n = 0; n < 16; n++)
        {
            int j = zigzag[n];
            int value = coefficients[coeffOff + j];
            bool negative = value < 0;
            int magnitude = (negative ? -value : value) + this.sharpen[j];
            if (magnitude > this.zeroThreshold[j])
            {
                int level = ((magnitude * this.iq[j]) + this.bias[j]) >> QFix;
                if (level > Vp8Cost.MaxLevel)
                {
                    level = Vp8Cost.MaxLevel;
                }

                if (negative)
                {
                    level = -level;
                }

                coefficients[coeffOff + j] = (short)(level * this.q[j]);
                levels[levelOff + n] = (short)level;
                if (level != 0)
                {
                    last = n;
                }
            }
            else
            {
                levels[levelOff + n] = 0;
                coefficients[coeffOff + j] = 0;
            }
        }

        return last;
    }
}

/// <summary>
/// The quantiser and rate-distortion parameters of one segment. A key frame produced by this encoder uses a
/// single segment, so exactly one of these exists per frame.
/// </summary>
internal sealed class Vp8EncoderSegment
{
    /// <summary>Numerator of the rate-distortion slope, in units of the squared quantizer step.</summary>
    private const int LambdaFactor = 9;

    /// <summary>Denominator exponent of the rate-distortion slope.</summary>
    private const int LambdaShift = 7;

    /// <summary>Base quantizer index, 0 (finest) to 127 (coarsest).</summary>
    public int QuantIndex { get; set; }

    /// <summary>Luma matrix, used for the 4x4 luma blocks.</summary>
    public Vp8Matrix Y1 { get; } = new Vp8Matrix();

    /// <summary>Second-order luma DC matrix, used for the Y2 block of a 16x16 macroblock.</summary>
    public Vp8Matrix Y2 { get; } = new Vp8Matrix();

    /// <summary>Chroma matrix.</summary>
    public Vp8Matrix Uv { get; } = new Vp8Matrix();

    /// <summary>Rate weight for 4x4 luma decisions.</summary>
    public int LambdaI4 { get; private set; }

    /// <summary>Rate weight for 16x16 luma decisions.</summary>
    public int LambdaI16 { get; private set; }

    /// <summary>Rate weight for chroma decisions.</summary>
    public int LambdaUv { get; private set; }

    /// <summary>Rate weight for the choice between prediction modes.</summary>
    public int LambdaMode { get; private set; }

    /// <summary>Builds the three matrices and the lambdas for the segment's quantizer index.</summary>
    public void Setup()
    {
        int q = this.QuantIndex;
        ReadOnlySpan<byte> dcTable = Vp8EncoderTables.DcTable;
        ReadOnlySpan<ushort> acTable = Vp8EncoderTables.AcTable;

        this.Y1.SetSteps(dcTable[Clamp(q, 127)], acTable[Clamp(q, 127)]);

        // The second-order AC step is 155/100 of the plain AC step with a floor of 8, computed exactly as
        // the decoder does it.
        int y2Ac = Math.Max((acTable[Clamp(q, 127)] * 101581) >> 16, 8);
        this.Y2.SetSteps(dcTable[Clamp(q, 127)] * 2, y2Ac);
        this.Uv.SetSteps(dcTable[Clamp(q, 117)], acTable[Clamp(q, 127)]);

        int qI4 = this.Y1.Expand(0);
        this.Y2.Expand(1);
        int qUv = this.Uv.Expand(2);

        // A single consistent slope keeps the within-mode and the cross-mode comparisons commensurable;
        // the factor was tuned by measuring size at equal PSNR over the test corpus.
        this.LambdaI4 = Lambda(qI4);
        this.LambdaI16 = Lambda(qI4);
        this.LambdaUv = Lambda(qUv);
        this.LambdaMode = Lambda(qI4);
    }

    private static int Lambda(int q) => Math.Max((LambdaFactor * q * q) >> LambdaShift, 1);

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
}
