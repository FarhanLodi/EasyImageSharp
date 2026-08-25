using System.Numerics;
using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Nearest-palette-entry lookup with three accelerations: a fully transparent entry short-circuit driven by the
/// alpha cutoff, a lazily built 5/6/5-bit RGB bucket cache holding the few palette entries that can be nearest to
/// any colour in the bucket (so exact matching costs a handful of distance evaluations), and a small
/// direct-mapped memo for colours that need a full RGBA search. Full palette scans are vectorised over
/// channel-planar arrays. Safe for concurrent lookups.
/// </summary>
internal sealed class PaletteIndexMap : IPaletteMap
{
    private const int BucketCount = 1 << 16;
    private const int CandidateSlots = 16;
    private const byte FullScanMarker = byte.MaxValue;
    private const int MemoBits = 14;
    private const int MemoSize = 1 << MemoBits;

    /// <summary>Padding value for unused vector lanes: far from every real colour, so never nearest and never the bound.</summary>
    private const int Sentinel = 4096;

    private readonly Rgba32[] palette;
    private readonly int transparentIndex;
    private readonly byte alphaCutoff;
    private readonly bool coarse;

    // Palette entries that take part in nearest-colour searches (everything except fully transparent entries),
    // stored channel-planar and padded to whole vectors. sIndex maps a searchable position to a palette index.
    private readonly int[] sr;
    private readonly int[] sg;
    private readonly int[] sb;
    private readonly int[] sa;
    private readonly byte[] sIndex;
    private readonly int searchableCount;
    private readonly int paddedCount;
    private readonly bool allOpaque;
    private readonly bool useMemo = Environment.Is64BitProcess; // 64-bit stores are atomic; the memo packs key and value into one long.

    private byte[]? candidates;
    private byte[]? candidateCounts;
    private long[]? memo;

    public PaletteIndexMap(ReadOnlySpan<Rgba32> palette, ColorMatchingMode mode, byte alphaCutoff)
    {
        if (palette.Length is 0 or > 256)
        {
            throw new ArgumentException("A palette must contain between 1 and 256 colours.", nameof(palette));
        }

        this.palette = palette.ToArray();
        this.alphaCutoff = alphaCutoff;
        this.coarse = mode == ColorMatchingMode.Coarse;

        this.transparentIndex = -1;
        int count = 0;
        for (int i = 0; i < this.palette.Length; i++)
        {
            if (this.palette[i].A == 0)
            {
                if (this.transparentIndex < 0)
                {
                    this.transparentIndex = i;
                }
            }
            else
            {
                count++;
            }
        }

        int lanes = Vector<int>.Count;
        this.searchableCount = count;
        this.paddedCount = (count + lanes - 1) / lanes * lanes;
        this.sr = new int[this.paddedCount];
        this.sg = new int[this.paddedCount];
        this.sb = new int[this.paddedCount];
        this.sa = new int[this.paddedCount];
        this.sIndex = new byte[count];
        Array.Fill(this.sr, Sentinel);
        Array.Fill(this.sg, Sentinel);
        Array.Fill(this.sb, Sentinel);
        Array.Fill(this.sa, Sentinel);
        this.allOpaque = true;
        int s = 0;
        for (int i = 0; i < this.palette.Length; i++)
        {
            Rgba32 p = this.palette[i];
            if (p.A == 0)
            {
                continue;
            }

            this.sr[s] = p.R;
            this.sg[s] = p.G;
            this.sb[s] = p.B;
            this.sa[s] = p.A;
            this.sIndex[s] = (byte)i;
            this.allOpaque &= p.A == byte.MaxValue;
            s++;
        }
    }

    public ReadOnlySpan<Rgba32> Palette => this.palette;

    /// <summary>The index of the fully transparent palette entry, or -1 when the palette has none.</summary>
    public int TransparentIndex => this.transparentIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPaletteIndex(Rgba32 color, out Rgba32 match)
    {
        if (this.transparentIndex >= 0 && color.A < this.alphaCutoff)
        {
            match = this.palette[this.transparentIndex];
            return this.transparentIndex;
        }

        if (this.searchableCount == 0)
        {
            int only = this.transparentIndex >= 0 ? this.transparentIndex : 0;
            match = this.palette[only];
            return only;
        }

        int position = this.allOpaque ? this.LookupRgb(color) : this.LookupRgba(color);
        int index = this.sIndex[position];
        match = this.palette[index];
        return index;
    }

    // ----- Opaque palette: RGB bucket cache -----

    private int LookupRgb(Rgba32 color)
    {
        int key = ((color.R >> 3) << 11) | ((color.G >> 2) << 5) | (color.B >> 3);
        byte[] counts = this.candidateCounts ?? this.EnsureBucketTables();
        int count = Volatile.Read(ref counts[key]);
        if (count == 0)
        {
            count = this.BuildBucket(key);
        }

        byte[] table = this.candidates!;
        int slot = key * CandidateSlots;
        if (count == 1)
        {
            return table[slot];
        }

        if (count == FullScanMarker)
        {
            return this.NearestRgbFull(color.R, color.G, color.B);
        }

        int best = table[slot];
        int bestDistance = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int s = table[slot + i];
            int dr = this.sr[s] - color.R;
            int dg = this.sg[s] - color.G;
            int db = this.sb[s] - color.B;
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = s;
            }
        }

        return best;
    }

    private byte[] EnsureBucketTables()
    {
        // Both arrays are idempotent caches: racing initialisers produce interchangeable results.
        Interlocked.CompareExchange(ref this.candidates, new byte[BucketCount * CandidateSlots], null);
        Interlocked.CompareExchange(ref this.candidateCounts, new byte[BucketCount], null);
        return this.candidateCounts!;
    }

    /// <summary>
    /// Fills the candidate slots of one bucket. Every palette entry whose minimum distance to the bucket cube is
    /// not larger than the smallest maximum distance of any entry can be the nearest for some colour in the
    /// bucket; those are the candidates. Coarse mode keeps only the entry nearest to the bucket centre.
    /// </summary>
    private int BuildBucket(int key)
    {
        int r0 = (key >> 11) << 3;
        int g0 = ((key >> 5) & 63) << 2;
        int b0 = (key & 31) << 3;
        int r1 = r0 + 7;
        int g1 = g0 + 3;
        int b1 = b0 + 7;

        byte[] table = this.candidates!;
        int slot = key * CandidateSlots;
        int count;
        if (this.coarse)
        {
            table[slot] = (byte)this.NearestRgbFull(r0 + 4, g0 + 2, b0 + 4);
            count = 1;
        }
        else
        {
            Span<int> nearestDistances = stackalloc int[this.paddedCount];
            var vr0 = new Vector<int>(r0);
            var vr1 = new Vector<int>(r1);
            var vg0 = new Vector<int>(g0);
            var vg1 = new Vector<int>(g1);
            var vb0 = new Vector<int>(b0);
            var vb1 = new Vector<int>(b1);
            var bound = new Vector<int>(int.MaxValue);
            for (int s = 0; s < this.paddedCount; s += Vector<int>.Count)
            {
                var r = new Vector<int>(this.sr, s);
                var g = new Vector<int>(this.sg, s);
                var b = new Vector<int>(this.sb, s);

                Vector<int> dr = Vector.Max(vr0 - r, Vector<int>.Zero) + Vector.Max(r - vr1, Vector<int>.Zero);
                Vector<int> dg = Vector.Max(vg0 - g, Vector<int>.Zero) + Vector.Max(g - vg1, Vector<int>.Zero);
                Vector<int> db = Vector.Max(vb0 - b, Vector<int>.Zero) + Vector.Max(b - vb1, Vector<int>.Zero);
                ((dr * dr) + (dg * dg) + (db * db)).CopyTo(nearestDistances[s..]);

                Vector<int> er = Vector.Max(r - vr0, vr1 - r);
                Vector<int> eg = Vector.Max(g - vg0, vg1 - g);
                Vector<int> eb = Vector.Max(b - vb0, vb1 - b);
                bound = Vector.Min(bound, (er * er) + (eg * eg) + (eb * eb));
            }

            int scalarBound = HorizontalMin(bound);
            count = 0;
            for (int s = 0; s < this.searchableCount; s++)
            {
                if (nearestDistances[s] <= scalarBound)
                {
                    if (count == CandidateSlots)
                    {
                        count = FullScanMarker;
                        break;
                    }

                    table[slot + count] = (byte)s;
                    count++;
                }
            }
        }

        // Publish the count only after the candidates are in place so concurrent readers never see a partial bucket.
        Volatile.Write(ref this.candidateCounts![key], (byte)count);
        return count;
    }

    private int NearestRgbFull(int r, int g, int b)
    {
        var vr = new Vector<int>(r);
        var vg = new Vector<int>(g);
        var vb = new Vector<int>(b);
        var best = new Vector<int>(int.MaxValue);
        for (int s = 0; s < this.paddedCount; s += Vector<int>.Count)
        {
            Vector<int> dr = new Vector<int>(this.sr, s) - vr;
            Vector<int> dg = new Vector<int>(this.sg, s) - vg;
            Vector<int> db = new Vector<int>(this.sb, s) - vb;
            best = Vector.Min(best, (dr * dr) + (dg * dg) + (db * db));
        }

        int bestDistance = HorizontalMin(best);
        for (int s = 0; s < this.searchableCount; s++)
        {
            int dr = this.sr[s] - r;
            int dg = this.sg[s] - g;
            int db = this.sb[s] - b;
            if ((dr * dr) + (dg * dg) + (db * db) == bestDistance)
            {
                return s;
            }
        }

        return 0;
    }

    // ----- Palettes with partial alpha: exact RGBA search with a memo -----

    private int LookupRgba(Rgba32 color)
    {
        uint key = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        long[]? memo = null;
        int hash = 0;
        if (this.useMemo)
        {
            memo = this.memo ?? Interlocked.CompareExchange(ref this.memo, new long[MemoSize], null) ?? this.memo;
            hash = (int)((key * 2654435761u) >> (32 - MemoBits));
            long entry = memo[hash];
            if (entry != 0 && (uint)entry == key)
            {
                return (int)(entry >> 32) - 1;
            }
        }

        var vr = new Vector<int>(color.R);
        var vg = new Vector<int>(color.G);
        var vb = new Vector<int>(color.B);
        var va = new Vector<int>(color.A);
        var best = new Vector<int>(int.MaxValue);
        for (int s = 0; s < this.paddedCount; s += Vector<int>.Count)
        {
            Vector<int> dr = new Vector<int>(this.sr, s) - vr;
            Vector<int> dg = new Vector<int>(this.sg, s) - vg;
            Vector<int> db = new Vector<int>(this.sb, s) - vb;
            Vector<int> da = new Vector<int>(this.sa, s) - va;
            best = Vector.Min(best, (dr * dr) + (dg * dg) + (db * db) + (da * da));
        }

        int bestDistance = HorizontalMin(best);
        int bestPosition = 0;
        for (int s = 0; s < this.searchableCount; s++)
        {
            int dr = this.sr[s] - color.R;
            int dg = this.sg[s] - color.G;
            int db = this.sb[s] - color.B;
            int da = this.sa[s] - color.A;
            if ((dr * dr) + (dg * dg) + (db * db) + (da * da) == bestDistance)
            {
                bestPosition = s;
                break;
            }
        }

        if (memo is not null)
        {
            memo[hash] = ((long)(bestPosition + 1) << 32) | key;
        }

        return bestPosition;
    }

    private static int HorizontalMin(Vector<int> vector)
    {
        int min = vector[0];
        for (int i = 1; i < Vector<int>.Count; i++)
        {
            min = Math.Min(min, vector[i]);
        }

        return min;
    }
}
