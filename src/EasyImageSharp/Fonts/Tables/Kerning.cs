namespace EasyImageSharp.Fonts.Tables;

/// <summary>A pair positioning adjustment in font units.</summary>
/// <param name="FirstAdvance">Added to the advance of the first glyph (the classic kerning value).</param>
/// <param name="FirstPlacement">Horizontal shift of the first glyph that does not change its advance.</param>
/// <param name="SecondPlacement">Horizontal shift of the second glyph that does not change its advance.</param>
/// <param name="SecondAdvance">Added to the advance of the second glyph.</param>
internal readonly record struct PairAdjustment(int FirstAdvance, int FirstPlacement, int SecondPlacement, int SecondAdvance)
{
    public bool IsZero => this.FirstAdvance == 0 && this.FirstPlacement == 0 && this.SecondPlacement == 0 && this.SecondAdvance == 0;

    public static PairAdjustment operator +(PairAdjustment a, PairAdjustment b)
        => new(a.FirstAdvance + b.FirstAdvance, a.FirstPlacement + b.FirstPlacement, a.SecondPlacement + b.SecondPlacement, a.SecondAdvance + b.SecondAdvance);
}

/// <summary>Horizontal pair kerning of one face, from GPOS pair adjustment or the legacy <c>kern</c> table.</summary>
internal abstract class Kerning
{
    public static Kerning None { get; } = new NoKerning();

    public virtual bool IsEmpty => false;

    public abstract PairAdjustment Get(int left, int right);

    /// <summary>
    /// Builds the kerning of a face: GPOS pair adjustment (lookup type 2, formats 1 and 2) from the <c>kern</c>
    /// feature when the font has it, otherwise the format 0 subtables of the <c>kern</c> table. A malformed
    /// positioning table disables that source instead of failing the font, as browsers and system text stacks do.
    /// </summary>
    public static Kerning Load(FontSource source)
    {
        ReadOnlySpan<byte> gpos = source.GetTable(FontReader.Tag("GPOS"));
        if (!gpos.IsEmpty)
        {
            try
            {
                Kerning? fromGpos = GposKerning.Parse(gpos);
                if (fromGpos is not null)
                {
                    return fromGpos;
                }
            }
            catch (InvalidFontException)
            {
            }
        }

        ReadOnlySpan<byte> kern = source.GetTable(FontReader.Tag("kern"));
        if (!kern.IsEmpty)
        {
            try
            {
                return KernTableKerning.Parse(kern) ?? None;
            }
            catch (InvalidFontException)
            {
            }
        }

        return None;
    }

    private sealed class NoKerning : Kerning
    {
        public override bool IsEmpty => true;

        public override PairAdjustment Get(int left, int right) => default;
    }
}

/// <summary>Format 0 subtables of the legacy <c>kern</c> table (Microsoft version 0 and Apple version 1 headers).</summary>
internal sealed class KernTableKerning : Kerning
{
    private const string Label = "kern table";

    private readonly Dictionary<uint, int> pairs;

    private KernTableKerning(Dictionary<uint, int> pairs) => this.pairs = pairs;

    public override PairAdjustment Get(int left, int right)
        => this.pairs.TryGetValue(((uint)left << 16) | (uint)right, out int value) ? new PairAdjustment(value, 0, 0, 0) : default;

    public static KernTableKerning? Parse(ReadOnlySpan<byte> table)
    {
        var r = new FontReader(table, Label);
        int version = r.ReadUInt16();
        bool apple = version == 1;
        int count;
        if (apple)
        {
            r.ReadUInt16();
            count = (int)Math.Min(r.ReadUInt32(), 256);
        }
        else if (version == 0)
        {
            count = r.ReadUInt16();
        }
        else
        {
            return null;
        }

        var pairs = new Dictionary<uint, int>();
        for (int i = 0; i < count && r.Remaining > 0; i++)
        {
            int start = r.Position;
            int length;
            int format;
            bool horizontal;
            bool usable;
            if (apple)
            {
                length = (int)Math.Min(r.ReadUInt32(), int.MaxValue);
                int coverage = r.ReadUInt16();
                r.ReadUInt16();
                format = coverage & 0xFF;

                // Apple: bit 15 vertical, bit 14 cross-stream, bit 13 variation.
                horizontal = (coverage & 0x8000) == 0;
                usable = (coverage & 0x6000) == 0;
            }
            else
            {
                r.ReadUInt16();
                length = r.ReadUInt16();
                int coverage = r.ReadUInt16();
                format = coverage >> 8;

                // Microsoft: bit 0 horizontal, bit 1 minimum values, bit 2 cross-stream.
                horizontal = (coverage & 0x1) != 0;
                usable = (coverage & 0x6) == 0;
            }

            if (format == 0)
            {
                int nPairs = r.ReadUInt16();
                r.Skip(6);

                // The 16-bit subtable length overflows in large tables, so the pair count is trusted instead
                // (bounded by the bytes actually present) and the next subtable starts right after the pairs.
                nPairs = Math.Min(nPairs, r.Remaining / 6);
                if (!horizontal || !usable)
                {
                    r.Skip(nPairs * 6);
                    continue;
                }

                for (int p = 0; p < nPairs; p++)
                {
                    uint key = ((uint)r.ReadUInt16() << 16) | r.ReadUInt16();
                    int value = r.ReadInt16();

                    // Values of the same pair in several subtables add up.
                    pairs[key] = pairs.TryGetValue(key, out int existing) ? existing + value : value;
                }

                continue;
            }

            if (length < 6)
            {
                break;
            }

            r.Position = Math.Min(r.Length, start + length);
        }

        return pairs.Count == 0 ? null : new KernTableKerning(pairs);
    }
}
