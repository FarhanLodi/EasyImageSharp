using System.Buffers.Binary;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// Character-to-glyph mapping from the <c>cmap</c> table. One subtable is chosen, in this order of preference:
/// full-repertoire Unicode (Windows encoding 10 or Unicode platform, formats 12/13/10), BMP Unicode (Windows
/// encoding 1 or Unicode platform, formats 4/6/0), Windows Symbol, and finally Macintosh Roman for ASCII.
/// Formats 0, 4, 6, 10, 12 and 13 are read; format 14 (variation sequences) is not needed because variation
/// selectors are treated as default-ignorable by the layout. A code point that maps outside the glyph range
/// maps to glyph 0, the missing-glyph box.
/// </summary>
internal abstract class CmapTable
{
    private const string Label = "cmap table";

    /// <summary>A mapping with no characters, for fonts whose cmap holds no usable subtable.</summary>
    public static CmapTable Empty { get; } = new EmptyCmap();

    /// <summary>Whether this is a Windows Symbol mapping (code points in the private-use range U+F000-U+F0FF).</summary>
    public bool IsSymbol { get; private init; }

    public int GetGlyph(int codePoint, int numGlyphs)
    {
        int glyph = this.Lookup(codePoint);
        if (glyph == 0 && this.IsSymbol && codePoint is >= 0x20 and <= 0xFF)
        {
            // Symbol fonts map their repertoire into U+F020-U+F0FF; text written as plain Latin-1 still finds it.
            glyph = this.Lookup(0xF000 + codePoint);
        }

        return (uint)glyph < (uint)numGlyphs ? glyph : 0;
    }

    protected abstract int Lookup(int codePoint);

    public static CmapTable Parse(ReadOnlySpan<byte> table)
    {
        if (table.IsEmpty)
        {
            return Empty;
        }

        var r = new FontReader(table, Label);
        r.ReadUInt16();
        int count = r.ReadUInt16();
        int bestRank = int.MaxValue;
        int bestOffset = -1;
        bool bestSymbol = false;
        for (int i = 0; i < count; i++)
        {
            int platform = r.ReadUInt16();
            int encoding = r.ReadUInt16();
            int offset = r.ReadOffset32();
            if (offset > table.Length - 2)
            {
                continue;
            }

            int format = FontReader.UInt16At(table, offset, Label);
            bool full = format is 10 or 12 or 13;
            if (format is not (0 or 4 or 6 or 10 or 12 or 13))
            {
                continue;
            }

            int rank;
            bool symbol = false;
            if ((platform == 3 && encoding == 10) || (platform == 0 && encoding is 4 or 6))
            {
                rank = full ? 0 : 2;
            }
            else if ((platform == 3 && encoding == 1) || (platform == 0 && encoding <= 3))
            {
                rank = full ? 1 : 3;
            }
            else if (platform == 3 && encoding == 0)
            {
                rank = 4;
                symbol = true;
            }
            else if (platform == 1 && encoding == 0)
            {
                rank = 5;
            }
            else
            {
                continue;
            }

            if (rank < bestRank)
            {
                bestRank = rank;
                bestOffset = offset;
                bestSymbol = symbol;
            }
        }

        if (bestOffset < 0)
        {
            return Empty;
        }

        ReadOnlySpan<byte> sub = table[bestOffset..];
        int subFormat = FontReader.UInt16At(sub, 0, Label);
        CmapTable parsed = subFormat switch
        {
            0 => ByteCmap.Read(sub),
            4 => SegmentCmap.Read(sub),
            6 => TrimmedCmap.Read(sub, wide: false),
            10 => TrimmedCmap.Read(sub, wide: true),
            _ => GroupCmap.Read(sub, constant: subFormat == 13),
        };

        if (bestRank == 5)
        {
            parsed = new AsciiOnlyCmap(parsed);
        }

        return bestSymbol ? parsed.AsSymbol() : parsed;
    }

    private CmapTable AsSymbol() => new SymbolCmap(this);

    private sealed class EmptyCmap : CmapTable
    {
        protected override int Lookup(int codePoint) => 0;
    }

    private sealed class SymbolCmap : CmapTable
    {
        private readonly CmapTable inner;

        public SymbolCmap(CmapTable inner)
        {
            this.inner = inner;
            this.IsSymbol = true;
        }

        protected override int Lookup(int codePoint) => this.inner.Lookup(codePoint);
    }

    /// <summary>A Macintosh Roman subtable, trusted only for the ASCII range where it agrees with Unicode.</summary>
    private sealed class AsciiOnlyCmap : CmapTable
    {
        private readonly CmapTable inner;

        public AsciiOnlyCmap(CmapTable inner) => this.inner = inner;

        protected override int Lookup(int codePoint) => codePoint < 0x80 ? this.inner.Lookup(codePoint) : 0;
    }

    /// <summary>Format 0: 256 one-byte glyph indices.</summary>
    private sealed class ByteCmap : CmapTable
    {
        private readonly byte[] glyphs;

        private ByteCmap(byte[] glyphs) => this.glyphs = glyphs;

        public static ByteCmap Read(ReadOnlySpan<byte> sub) => new(FontReader.SliceOf(sub, 6, 256, Label).ToArray());

        protected override int Lookup(int codePoint) => (uint)codePoint < 256u ? this.glyphs[codePoint] : 0;
    }

    /// <summary>Format 6 (16-bit codes) and format 10 (32-bit codes): one glyph per code in a dense range.</summary>
    private sealed class TrimmedCmap : CmapTable
    {
        private readonly int firstCode;
        private readonly ushort[] glyphs;

        private TrimmedCmap(int firstCode, ushort[] glyphs)
        {
            this.firstCode = firstCode;
            this.glyphs = glyphs;
        }

        public static TrimmedCmap Read(ReadOnlySpan<byte> sub, bool wide)
        {
            var r = new FontReader(sub, Label);
            int first;
            int count;
            if (wide)
            {
                r.Position = 12;
                uint start = r.ReadUInt32();
                uint n = r.ReadUInt32();
                if (start > 0x10FFFF || n > 0x110000)
                {
                    throw FontReader.Fail(Label, "a format 10 subtable has an out-of-range code range");
                }

                first = (int)start;
                count = (int)n;
            }
            else
            {
                r.Position = 6;
                first = r.ReadUInt16();
                count = r.ReadUInt16();
            }

            ReadOnlySpan<byte> raw = r.ReadBytes(count * 2);
            ushort[] glyphs = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                glyphs[i] = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(i * 2, 2));
            }

            return new TrimmedCmap(first, glyphs);
        }

        protected override int Lookup(int codePoint)
        {
            int index = codePoint - this.firstCode;
            return (uint)index < (uint)this.glyphs.Length ? this.glyphs[index] : 0;
        }
    }

    /// <summary>Format 4: segment mapping to delta values over the BMP.</summary>
    private sealed class SegmentCmap : CmapTable
    {
        private readonly ushort[] endCodes;
        private readonly ushort[] startCodes;
        private readonly short[] deltas;
        private readonly ushort[] rangeOffsets;
        private readonly byte[] subtable;
        private readonly int rangeOffsetsStart;

        private SegmentCmap(ushort[] endCodes, ushort[] startCodes, short[] deltas, ushort[] rangeOffsets, byte[] subtable, int rangeOffsetsStart)
        {
            this.endCodes = endCodes;
            this.startCodes = startCodes;
            this.deltas = deltas;
            this.rangeOffsets = rangeOffsets;
            this.subtable = subtable;
            this.rangeOffsetsStart = rangeOffsetsStart;
        }

        public static SegmentCmap Read(ReadOnlySpan<byte> sub)
        {
            var r = new FontReader(sub, Label);
            r.Position = 6;
            int segCountX2 = r.ReadUInt16();
            if ((segCountX2 & 1) != 0 || segCountX2 == 0)
            {
                throw FontReader.Fail(Label, $"a format 4 subtable has an invalid segment count ({segCountX2} / 2)");
            }

            int segCount = segCountX2 / 2;

            // The declared 16-bit length overflows in large subtables, so it is not trusted. Instead the copy covers
            // every byte a lookup can address: an idRangeOffset reaches at most 64 KiB past its own entry, plus two
            // bytes per code in the segment. Each lookup is still bounds-checked against the copy.
            int reach = 16 + (segCount * 8) + 0x10000 + 0x20000;
            byte[] copy = sub[..Math.Min(sub.Length, reach)].ToArray();
            r = new FontReader(copy, Label);
            r.Position = 14;
            ushort[] ends = ReadUInt16Array(ref r, segCount);
            r.ReadUInt16();
            ushort[] starts = ReadUInt16Array(ref r, segCount);
            short[] deltas = new short[segCount];
            for (int i = 0; i < segCount; i++)
            {
                deltas[i] = r.ReadInt16();
            }

            int rangeStart = r.Position;
            ushort[] offsets = ReadUInt16Array(ref r, segCount);
            return new SegmentCmap(ends, starts, deltas, offsets, copy, rangeStart);
        }

        protected override int Lookup(int codePoint)
        {
            if ((uint)codePoint > 0xFFFF)
            {
                return 0;
            }

            // Binary search for the first segment whose end code is at least the code point.
            int lo = 0;
            int hi = this.endCodes.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (this.endCodes[mid] < codePoint)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (this.endCodes[lo] < codePoint || this.startCodes[lo] > codePoint)
            {
                return 0;
            }

            int rangeOffset = this.rangeOffsets[lo];
            if (rangeOffset == 0)
            {
                return (codePoint + this.deltas[lo]) & 0xFFFF;
            }

            // The offset is relative to the idRangeOffset entry itself.
            long address = this.rangeOffsetsStart + (lo * 2L) + rangeOffset + ((codePoint - this.startCodes[lo]) * 2L);
            if (address < 0 || address + 2 > this.subtable.Length)
            {
                return 0;
            }

            int glyph = BinaryPrimitives.ReadUInt16BigEndian(this.subtable.AsSpan((int)address, 2));
            return glyph == 0 ? 0 : (glyph + this.deltas[lo]) & 0xFFFF;
        }

        private static ushort[] ReadUInt16Array(ref FontReader r, int count)
        {
            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = r.ReadUInt16();
            }

            return values;
        }
    }

    /// <summary>Format 12 (sequential map groups) and 13 (many-to-one range mappings).</summary>
    private sealed class GroupCmap : CmapTable
    {
        private readonly uint[] starts;
        private readonly uint[] ends;
        private readonly uint[] glyphs;
        private readonly bool constant;

        private GroupCmap(uint[] starts, uint[] ends, uint[] glyphs, bool constant)
        {
            this.starts = starts;
            this.ends = ends;
            this.glyphs = glyphs;
            this.constant = constant;
        }

        public static GroupCmap Read(ReadOnlySpan<byte> sub, bool constant)
        {
            var r = new FontReader(sub, Label);
            r.Position = 12;
            uint count = r.ReadUInt32();
            if (count > (uint)(sub.Length / 12))
            {
                throw FontReader.Fail(Label, $"a format {(constant ? 13 : 12)} subtable declares {count} groups but has room for {sub.Length / 12}");
            }

            var groups = new (uint Start, uint End, uint Glyph)[count];
            for (int i = 0; i < groups.Length; i++)
            {
                uint start = r.ReadUInt32();
                uint end = r.ReadUInt32();
                uint glyph = r.ReadUInt32();
                if (end < start || end > 0x10FFFF)
                {
                    throw FontReader.Fail(Label, $"group {i} has an invalid code range U+{start:X}-U+{end:X}");
                }

                groups[i] = (start, end, glyph);
            }

            // The specification requires ascending groups; sorting tolerates fonts that do not comply.
            Array.Sort(groups, static (a, b) => a.Start.CompareTo(b.Start));
            uint[] starts = new uint[groups.Length];
            uint[] ends = new uint[groups.Length];
            uint[] glyphs = new uint[groups.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                (starts[i], ends[i], glyphs[i]) = groups[i];
            }

            return new GroupCmap(starts, ends, glyphs, constant);
        }

        protected override int Lookup(int codePoint)
        {
            uint c = (uint)codePoint;
            int lo = 0;
            int hi = this.starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (this.ends[mid] < c)
                {
                    lo = mid + 1;
                }
                else if (this.starts[mid] > c)
                {
                    hi = mid - 1;
                }
                else
                {
                    long glyph = this.constant ? this.glyphs[mid] : this.glyphs[mid] + (long)(c - this.starts[mid]);
                    return glyph <= 0xFFFF ? (int)glyph : 0;
                }
            }

            return 0;
        }
    }
}
