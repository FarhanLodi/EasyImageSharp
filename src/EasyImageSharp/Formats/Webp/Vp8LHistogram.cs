namespace EasyImageSharp.Formats.Webp;

/// <summary>How one entry of a VP8L symbol stream is coded.</summary>
internal enum Vp8LTokenKind : byte
{
    /// <summary>A literal ARGB pixel, coded with the green, red, blue and alpha codes.</summary>
    Literal = 0,

    /// <summary>A backward reference: copy <c>Length</c> pixels from <c>Value</c> pixels earlier.</summary>
    Copy = 1,

    /// <summary>A colour-cache hit; <c>Value</c> is the cache index.</summary>
    CacheHit = 2,
}

/// <summary>One symbol of the VP8L pixel stream: a literal, a backward reference or a colour-cache index.</summary>
internal struct Vp8LToken
{
    /// <summary>The literal ARGB value, the copy distance in pixels, or the colour-cache index.</summary>
    public uint Value;

    /// <summary>The number of pixels a <see cref="Vp8LTokenKind.Copy"/> covers.</summary>
    public ushort Length;

    /// <summary>The distance plane code of a <see cref="Vp8LTokenKind.Copy"/>.</summary>
    public ushort PlaneCode;

    /// <summary>What the token codes.</summary>
    public Vp8LTokenKind Kind;

    /// <summary>The number of pixels the token consumes.</summary>
    public readonly int PixelCount => this.Kind == Vp8LTokenKind.Copy ? this.Length : 1;

    public static Vp8LToken Literal(uint argb) => new() { Kind = Vp8LTokenKind.Literal, Value = argb, Length = 1 };

    public static Vp8LToken Copy(int length, int distance, int planeCode)
        => new() { Kind = Vp8LTokenKind.Copy, Value = (uint)distance, Length = (ushort)length, PlaneCode = (ushort)planeCode };

    public static Vp8LToken Cache(int index) => new() { Kind = Vp8LTokenKind.CacheHit, Value = (uint)index, Length = 1 };
}

/// <summary>
/// The prefix (Huffman) codes of the VP8L format use an "N most significant bits plus extra bits" scheme for
/// lengths and distances, and map short backward-reference distances onto 120 two-dimensional plane codes.
/// This class holds both mappings, in the encoding direction.
/// </summary>
internal static class Vp8LPrefix
{
    /// <summary>Number of two-dimensional short-distance codes.</summary>
    public const int CodeToPlaneCodes = 120;

    /// <summary>The 120 short-distance codes as (dx, dy) pairs (RFC 9649 section 3.7.2.3).</summary>
    private static ReadOnlySpan<sbyte> DistanceMap => new sbyte[]
    {
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2,
        2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0,
        1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0,
        1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0,
        1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3,
        0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2,
        3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6,
        -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    };

    /// <summary>Splits <paramref name="value"/> (at least 1) into its prefix symbol and the extra bits that follow it.</summary>
    public static void Encode(int value, out int code, out int extraBits, out int extraValue)
    {
        if (value <= 4)
        {
            code = value - 1;
            extraBits = 0;
            extraValue = 0;
            return;
        }

        int shifted = value - 1;
        int highest = Log2Floor(shifted);
        int secondHighest = (shifted >> (highest - 1)) & 1;
        extraBits = highest - 1;
        extraValue = shifted & ((1 << extraBits) - 1);
        code = (2 * highest) + secondHighest;
    }

    /// <summary>
    /// Maps a backward-reference distance to the plane code the bitstream carries: one of the 120 short
    /// two-dimensional codes when the distance is a small offset in the previous rows, or <c>distance + 120</c>.
    /// </summary>
    public static int DistanceToPlaneCode(int xsize, int distance)
    {
        int yoffset = distance / xsize;
        int xoffset = distance - (yoffset * xsize);
        int best = distance + CodeToPlaneCodes;
        int candidate = LookupPlaneCode(xoffset, yoffset);
        if (candidate < best && PlaneCodeToDistance(xsize, candidate) == distance)
        {
            best = candidate;
        }

        candidate = LookupPlaneCode(xoffset - xsize, yoffset + 1);
        if (candidate < best && PlaneCodeToDistance(xsize, candidate) == distance)
        {
            best = candidate;
        }

        return best;
    }

    /// <summary>The inverse of <see cref="DistanceToPlaneCode"/>, mirroring the decoder exactly.</summary>
    public static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes)
        {
            return planeCode - CodeToPlaneCodes;
        }

        int dx = DistanceMap[(planeCode - 1) * 2];
        int dy = DistanceMap[((planeCode - 1) * 2) + 1];
        int distance = dx + (dy * xsize);
        return distance >= 1 ? distance : 1;
    }

    /// <summary>(dx, dy) to plane code, indexed by <c>dy * 17 + dx + 8</c>; zero where no short code exists.</summary>
    private static readonly byte[] InverseDistanceMap = CreateInverseDistanceMap();

    private static byte[] CreateInverseDistanceMap()
    {
        var map = new byte[9 * 17];
        for (int i = CodeToPlaneCodes - 1; i >= 0; i--)
        {
            int dx = DistanceMap[i * 2];
            int dy = DistanceMap[(i * 2) + 1];
            map[(dy * 17) + dx + 8] = (byte)(i + 1);
        }

        return map;
    }

    private static int LookupPlaneCode(int dx, int dy)
    {
        if (dx is < -8 or > 8 || dy is < 0 or > 8)
        {
            return int.MaxValue;
        }

        byte code = InverseDistanceMap[(dy * 17) + dx + 8];
        return code == 0 ? int.MaxValue : code;
    }

    private static int Log2Floor(int value)
    {
        int result = 0;
        while (value > 1)
        {
            value >>= 1;
            result++;
        }

        return result;
    }
}

/// <summary>
/// The five symbol populations one VP8L prefix code group covers (green plus lengths plus cache indices, red,
/// blue, alpha and distances) together with the extra bits its length and distance symbols carry. Estimated
/// costs are used to choose transforms, colour-cache sizes and the meta prefix code clustering.
/// </summary>
internal sealed class Vp8LHistogram
{
    /// <summary>The 256 literal green values.</summary>
    public const int NumLiteralCodes = 256;

    /// <summary>The 24 backward-reference length codes.</summary>
    public const int NumLengthCodes = 24;

    /// <summary>The 40 backward-reference distance codes.</summary>
    public const int NumDistanceCodes = 40;

    private static readonly float[] LogTable = CreateLogTable();

    public Vp8LHistogram(int cacheBits)
    {
        this.CacheBits = cacheBits;
        this.Literal = new uint[NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0)];
        this.Red = new uint[NumLiteralCodes];
        this.Blue = new uint[NumLiteralCodes];
        this.Alpha = new uint[NumLiteralCodes];
        this.Distance = new uint[NumDistanceCodes];
    }

    /// <summary>The colour-cache size this histogram was built for, in bits.</summary>
    public int CacheBits { get; }

    /// <summary>Green literals, length codes and colour-cache indices, in that order.</summary>
    public uint[] Literal { get; }

    /// <summary>Red literals.</summary>
    public uint[] Red { get; }

    /// <summary>Blue literals.</summary>
    public uint[] Blue { get; }

    /// <summary>Alpha literals.</summary>
    public uint[] Alpha { get; }

    /// <summary>Distance plane codes.</summary>
    public uint[] Distance { get; }

    /// <summary>The number of raw extra bits the length and distance symbols carry.</summary>
    public long ExtraBits { get; private set; }

    /// <summary>Accumulates one symbol.</summary>
    public void Add(in Vp8LToken token)
    {
        switch (token.Kind)
        {
            case Vp8LTokenKind.Literal:
            {
                uint argb = token.Value;
                this.Alpha[(int)(argb >> 24)]++;
                this.Red[(int)((argb >> 16) & 0xff)]++;
                this.Literal[(int)((argb >> 8) & 0xff)]++;
                this.Blue[(int)(argb & 0xff)]++;
                break;
            }

            case Vp8LTokenKind.Copy:
            {
                Vp8LPrefix.Encode(token.Length, out int lengthCode, out int lengthExtra, out _);
                this.Literal[NumLiteralCodes + lengthCode]++;
                Vp8LPrefix.Encode(token.PlaneCode, out int distanceCode, out int distanceExtra, out _);
                this.Distance[distanceCode]++;
                this.ExtraBits += lengthExtra + distanceExtra;
                break;
            }

            default:
                this.Literal[NumLiteralCodes + NumLengthCodes + (int)token.Value]++;
                break;
        }
    }

    /// <summary>Adds every symbol of <paramref name="other"/> to this histogram.</summary>
    public void AddFrom(Vp8LHistogram other)
    {
        AddInto(other.Literal, this.Literal);
        AddInto(other.Red, this.Red);
        AddInto(other.Blue, this.Blue);
        AddInto(other.Alpha, this.Alpha);
        AddInto(other.Distance, this.Distance);
        this.ExtraBits += other.ExtraBits;
    }

    /// <summary>An estimate, in bits, of everything this group costs: its five code descriptions and the coded symbols.</summary>
    public double EstimatedCost()
        => PopulationCost(this.Literal)
            + PopulationCost(this.Red)
            + PopulationCost(this.Blue)
            + PopulationCost(this.Alpha)
            + PopulationCost(this.Distance)
            + this.ExtraBits;

    /// <summary>An estimate, in bits, of the merged cost of two groups, without building the merged histogram.</summary>
    public static double MergedCost(Vp8LHistogram a, Vp8LHistogram b)
        => PairPopulationCost(a.Literal, b.Literal)
            + PairPopulationCost(a.Red, b.Red)
            + PairPopulationCost(a.Blue, b.Blue)
            + PairPopulationCost(a.Alpha, b.Alpha)
            + PairPopulationCost(a.Distance, b.Distance)
            + a.ExtraBits + b.ExtraBits;

    /// <summary>The Shannon entropy of a population, in bits.</summary>
    public static double Entropy(ReadOnlySpan<uint> population)
    {
        long total = 0;
        double sum = 0;
        foreach (uint value in population)
        {
            if (value != 0)
            {
                total += value;
                sum += value * FastLog2(value);
            }
        }

        return total == 0 ? 0 : (total * FastLog2(total)) - sum;
    }

    /// <summary>Base-2 logarithm, table-driven for the small counts that dominate a histogram.</summary>
    public static double FastLog2(long value) => value < LogTable.Length ? LogTable[(int)value] : Math.Log2(value);

    /// <summary>The entropy of a population plus an estimate of what describing its prefix code costs.</summary>
    private static double PopulationCost(ReadOnlySpan<uint> population)
    {
        double bits = Entropy(population);
        return bits + TableCost(population);
    }

    private static double PairPopulationCost(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        long total = 0;
        double sum = 0;
        int nonZero = 0;
        int transitions = 0;
        bool previous = false;
        for (int i = 0; i < a.Length; i++)
        {
            uint value = a[i] + b[i];
            if (value != 0)
            {
                total += value;
                sum += value * FastLog2(value);
                nonZero++;
            }

            bool current = value != 0;
            if (current != previous)
            {
                transitions++;
            }

            previous = current;
        }

        double entropy = total == 0 ? 0 : (total * FastLog2(total)) - sum;
        return entropy + EstimateTableCost(nonZero, transitions);
    }

    /// <summary>
    /// A rough model of the bits a prefix code description costs: a fixed header, a few bits per symbol that
    /// carries a code length, and a little extra wherever a run of unused symbols starts or ends.
    /// </summary>
    private static double TableCost(ReadOnlySpan<uint> population)
    {
        int nonZero = 0;
        int transitions = 0;
        bool previous = false;
        for (int i = 0; i < population.Length; i++)
        {
            bool current = population[i] != 0;
            if (current)
            {
                nonZero++;
            }

            if (current != previous)
            {
                transitions++;
            }

            previous = current;
        }

        return EstimateTableCost(nonZero, transitions);
    }

    private static double EstimateTableCost(int nonZero, int transitions)
    {
        if (nonZero <= 1)
        {
            return 12;
        }

        return 48 + (2.4 * nonZero) + (1.6 * transitions);
    }

    private static void AddInto(uint[] source, uint[] destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] += source[i];
        }
    }

    private static float[] CreateLogTable()
    {
        var table = new float[8192];
        for (int i = 1; i < table.Length; i++)
        {
            table[i] = (float)Math.Log2(i);
        }

        return table;
    }
}
