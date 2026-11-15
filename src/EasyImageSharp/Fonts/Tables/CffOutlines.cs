using EasyImageSharp.Fonts.Rendering;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// PostScript outlines from a <c>CFF </c> table (Compact Font Format 1, Adobe Technical Note #5176) with Type 2
/// charstrings (Technical Note #5177): local and global subroutines, flex, the deprecated arithmetic operators,
/// <c>seac</c> accented characters, and CID-keyed fonts with per-glyph Font DICTs. Hints are parsed and skipped.
/// CFF2 (variable fonts) is not supported.
/// </summary>
internal sealed class CffOutlines : IOutlineSource
{
    private const string Label = "CFF table";

    private readonly byte[] data;
    private readonly CffIndex charStrings;
    private readonly CffIndex globalSubrs;

    /// <summary>The local subroutines of each Font DICT (one entry for a name-keyed font).</summary>
    private readonly CffIndex[] localSubrs;

    /// <summary>The Font DICT of each glyph in a CID-keyed font, or <see langword="null"/> for a name-keyed font.</summary>
    private readonly byte[]? fdSelect;

    /// <summary>Glyph id of each standard-string id, for <c>seac</c>; built from the charset on first use.</summary>
    private readonly Lazy<Dictionary<int, int>> glyphBySid;

    public CffOutlines(byte[] data, int tableOffset, int tableLength, int numGlyphs)
    {
        this.data = data;
        ReadOnlySpan<byte> table = data.AsSpan(tableOffset, tableLength);
        var r = new FontReader(table, Label);
        int major = r.ReadUInt8();
        r.ReadUInt8();
        int headerSize = r.ReadUInt8();
        if (major != 1)
        {
            throw major == 2
                ? new NotSupportedException("CFF2 outlines (variable OpenType fonts) are not supported; use a TrueType-flavoured or CFF version 1 font.")
                : FontReader.Fail(Label, $"major version {major} is not 1");
        }

        CffIndex names = CffIndex.Read(data, tableOffset, tableLength, headerSize);
        CffIndex topDicts = CffIndex.Read(data, tableOffset, tableLength, names.End);
        CffIndex strings = CffIndex.Read(data, tableOffset, tableLength, topDicts.End);
        this.globalSubrs = CffIndex.Read(data, tableOffset, tableLength, strings.End);
        if (topDicts.Count < 1)
        {
            throw FontReader.Fail(Label, "the Top DICT INDEX is empty");
        }

        Dictionary<int, float[]> top = CffDict.Parse(topDicts.Get(data, 0));
        if (CffDict.Int(top, CffDict.CharstringType, 2) != 2)
        {
            throw new NotSupportedException("CFF fonts with Type 1 charstrings are not supported.");
        }

        int charStringsOffset = CffDict.Int(top, CffDict.CharStrings, -1);
        if (charStringsOffset < 0)
        {
            throw FontReader.Fail(Label, "the Top DICT has no CharStrings offset");
        }

        this.charStrings = CffIndex.Read(data, tableOffset, tableLength, charStringsOffset);
        if (this.charStrings.Count < numGlyphs)
        {
            throw FontReader.Fail(Label, $"CharStrings holds {this.charStrings.Count} glyphs but maxp declares {numGlyphs}");
        }

        if (top.ContainsKey(CffDict.Ros))
        {
            int fdArrayOffset = CffDict.Int(top, CffDict.FdArray, -1);
            int fdSelectOffset = CffDict.Int(top, CffDict.FdSelect, -1);
            if (fdArrayOffset < 0 || fdSelectOffset < 0)
            {
                throw FontReader.Fail(Label, "a CID-keyed font lacks its FDArray or FDSelect");
            }

            CffIndex fdArray = CffIndex.Read(data, tableOffset, tableLength, fdArrayOffset);
            if (fdArray.Count is < 1 or > 256)
            {
                throw FontReader.Fail(Label, $"FDArray holds {fdArray.Count} Font DICTs");
            }

            this.localSubrs = new CffIndex[fdArray.Count];
            for (int i = 0; i < fdArray.Count; i++)
            {
                Dictionary<int, float[]> fontDict = CffDict.Parse(fdArray.Get(data, i));
                this.localSubrs[i] = ReadLocalSubrs(data, tableOffset, tableLength, fontDict);
            }

            this.fdSelect = ReadFdSelect(table, fdSelectOffset, this.charStrings.Count, fdArray.Count);
        }
        else
        {
            this.localSubrs = [ReadLocalSubrs(data, tableOffset, tableLength, top)];
        }

        int charsetOffset = CffDict.Int(top, CffDict.Charset, 0);
        int glyphCount = this.charStrings.Count;
        this.glyphBySid = new Lazy<Dictionary<int, int>>(() => ReadCharset(data, tableOffset, tableLength, charsetOffset, glyphCount));
    }

    public GlyphOutline GetOutline(int glyph)
    {
        if ((uint)glyph >= (uint)this.charStrings.Count)
        {
            return GlyphOutline.Empty;
        }

        var builder = new GlyphOutline.Builder();
        var interpreter = new Type2Interpreter(this, builder);
        interpreter.Run(glyph, 0f, 0f, allowSeac: true);
        builder.Close();
        return builder.Build();
    }

    private CffIndex LocalSubrsFor(int glyph)
    {
        if (this.fdSelect is null)
        {
            return this.localSubrs[0];
        }

        return this.localSubrs[this.fdSelect[glyph]];
    }

    private static CffIndex ReadLocalSubrs(byte[] data, int tableOffset, int tableLength, Dictionary<int, float[]> dict)
    {
        if (!dict.TryGetValue(CffDict.Private, out float[]? priv) || priv.Length < 2)
        {
            return CffIndex.Empty;
        }

        int size = (int)priv[0];
        int offset = (int)priv[1];
        if (size < 0 || offset < 0 || (long)offset + size > tableLength)
        {
            throw FontReader.Fail(Label, "a Private DICT lies outside the table");
        }

        Dictionary<int, float[]> privateDict = CffDict.Parse(data.AsSpan(tableOffset + offset, size));
        int subrs = CffDict.Int(privateDict, CffDict.Subrs, -1);
        if (subrs <= 0)
        {
            return CffIndex.Empty;
        }

        // The Subrs offset is relative to the start of the Private DICT.
        return CffIndex.Read(data, tableOffset, tableLength, offset + subrs);
    }

    private static byte[] ReadFdSelect(ReadOnlySpan<byte> table, int offset, int glyphCount, int fdCount)
    {
        var r = new FontReader(table, "CFF FDSelect");
        r.Position = offset;
        int format = r.ReadUInt8();
        byte[] select = new byte[glyphCount];
        if (format == 0)
        {
            r.ReadBytes(glyphCount).CopyTo(select);
        }
        else if (format == 3)
        {
            int ranges = r.ReadUInt16();
            int first = r.ReadUInt16();
            for (int i = 0; i < ranges; i++)
            {
                int fd = r.ReadUInt8();
                int next = r.ReadUInt16();
                if (next < first || next > glyphCount)
                {
                    throw FontReader.Fail("CFF FDSelect", "its ranges are not ascending");
                }

                select.AsSpan(first, next - first).Fill((byte)fd);
                first = next;
            }
        }
        else
        {
            throw FontReader.Fail("CFF FDSelect", $"format {format} is not 0 or 3");
        }

        foreach (byte fd in select)
        {
            if (fd >= fdCount)
            {
                throw FontReader.Fail("CFF FDSelect", $"Font DICT {fd} does not exist");
            }
        }

        return select;
    }

    /// <summary>Maps standard string ids to glyph ids through the charset (format 0, 1, 2 or the predefined ISOAdobe).</summary>
    private static Dictionary<int, int> ReadCharset(byte[] data, int tableOffset, int tableLength, int offset, int glyphCount)
    {
        var map = new Dictionary<int, int>();
        try
        {
            if (offset is 0)
            {
                // ISOAdobe: glyph i has standard string i.
                for (int gid = 0; gid < Math.Min(glyphCount, 229); gid++)
                {
                    map.TryAdd(gid, gid);
                }

                return map;
            }

            if (offset is 1 or 2)
            {
                // The Expert charsets only cover glyphs that seac never references.
                return map;
            }

            var r = new FontReader(data.AsSpan(tableOffset, tableLength), "CFF charset");
            r.Position = offset;
            int format = r.ReadUInt8();
            int glyph = 1;
            if (format == 0)
            {
                for (; glyph < glyphCount; glyph++)
                {
                    map.TryAdd(r.ReadUInt16(), glyph);
                }
            }
            else if (format is 1 or 2)
            {
                while (glyph < glyphCount)
                {
                    int first = r.ReadUInt16();
                    int left = format == 1 ? r.ReadUInt8() : r.ReadUInt16();
                    for (int i = 0; i <= left && glyph < glyphCount; i++)
                    {
                        map.TryAdd(first + i, glyph++);
                    }
                }
            }
        }
        catch (InvalidFontException)
        {
            // A damaged charset only disables seac composition; it does not make the font unusable.
        }

        return map;
    }

    /// <summary>A CFF INDEX: a count, an offset array, and object data; offsets are absolute in the font buffer.</summary>
    private readonly struct CffIndex
    {
        private readonly int[] offsets;

        private CffIndex(int[] offsets, int end)
        {
            this.offsets = offsets;
            this.End = end;
        }

        public static CffIndex Empty { get; } = new([0], 0);

        public int Count => this.offsets.Length - 1;

        /// <summary>The offset just past the INDEX, relative to the start of the table.</summary>
        public int End { get; }

        public ReadOnlySpan<byte> Get(byte[] data, int index)
            => data.AsSpan(this.offsets[index], this.offsets[index + 1] - this.offsets[index]);

        public (int Start, int End) Range(int index) => (this.offsets[index], this.offsets[index + 1]);

        public static CffIndex Read(byte[] data, int tableOffset, int tableLength, int offset)
        {
            var r = new FontReader(data.AsSpan(tableOffset, tableLength), "CFF INDEX");
            r.Position = offset;
            int count = r.ReadUInt16();
            if (count == 0)
            {
                return new CffIndex([0], r.Position);
            }

            int offSize = r.ReadUInt8();
            if (offSize is < 1 or > 4)
            {
                throw FontReader.Fail("CFF INDEX", $"offset size {offSize} is not 1-4");
            }

            int arrayStart = r.Position;
            r.Skip((count + 1) * offSize);
            int dataStart = r.Position - 1;
            int[] offsets = new int[count + 1];
            int previous = 1;
            for (int i = 0; i <= count; i++)
            {
                int p = arrayStart + (i * offSize);
                uint value = 0;
                for (int b = 0; b < offSize; b++)
                {
                    value = (value << 8) | data[tableOffset + p + b];
                }

                if (value < (uint)previous || (i == 0 && value != 1) || (long)dataStart + value > tableLength)
                {
                    throw FontReader.Fail("CFF INDEX", "its offsets are not ascending or run past the table");
                }

                previous = (int)value;
                offsets[i] = tableOffset + dataStart + (int)value;
            }

            return new CffIndex(offsets, dataStart + previous);
        }
    }

    /// <summary>CFF DICT decoding: operands are collected and stored under their operator (two-byte operators as 1200 + b1).</summary>
    private static class CffDict
    {
        public const int Charset = 15;
        public const int CharStrings = 17;
        public const int Private = 18;
        public const int Subrs = 19;
        public const int CharstringType = 1206;
        public const int Ros = 1230;
        public const int FdArray = 1236;
        public const int FdSelect = 1237;

        public static int Int(Dictionary<int, float[]> dict, int key, int fallback)
            => dict.TryGetValue(key, out float[]? values) && values.Length > 0 && float.IsFinite(values[0])
                ? (int)Math.Clamp(values[0], int.MinValue, int.MaxValue)
                : fallback;

        public static Dictionary<int, float[]> Parse(ReadOnlySpan<byte> dict)
        {
            var result = new Dictionary<int, float[]>();
            var operands = new List<float>();
            var r = new FontReader(dict, "CFF DICT");
            while (r.Remaining > 0)
            {
                int b0 = r.ReadUInt8();
                if (b0 <= 21)
                {
                    int op = b0 == 12 ? 1200 + r.ReadUInt8() : b0;
                    result[op] = operands.ToArray();
                    operands.Clear();
                }
                else if (b0 == 28)
                {
                    operands.Add(r.ReadInt16());
                }
                else if (b0 == 29)
                {
                    operands.Add(r.ReadInt32());
                }
                else if (b0 == 30)
                {
                    operands.Add(ReadReal(ref r));
                }
                else if (b0 is >= 32 and <= 246)
                {
                    operands.Add(b0 - 139);
                }
                else if (b0 is >= 247 and <= 250)
                {
                    operands.Add(((b0 - 247) * 256) + r.ReadUInt8() + 108);
                }
                else if (b0 is >= 251 and <= 254)
                {
                    operands.Add((-(b0 - 251) * 256) - r.ReadUInt8() - 108);
                }
                else
                {
                    throw FontReader.Fail("CFF DICT", $"byte {b0} is not a valid operator or operand");
                }

                if (operands.Count > 48)
                {
                    throw FontReader.Fail("CFF DICT", "an operator has more than 48 operands");
                }
            }

            return result;
        }

        private static float ReadReal(ref FontReader r)
        {
            Span<char> text = stackalloc char[64];
            int length = 0;
            while (true)
            {
                int b = r.ReadUInt8();
                for (int shift = 4; shift >= 0; shift -= 4)
                {
                    int nibble = (b >> shift) & 0xF;
                    if (nibble == 0xF)
                    {
                        return float.TryParse(text[..length], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                            ? value
                            : 0f;
                    }

                    string piece = nibble switch
                    {
                        <= 9 => ((char)('0' + nibble)).ToString(),
                        0xA => ".",
                        0xB => "E",
                        0xC => "E-",
                        0xE => "-",
                        _ => string.Empty,
                    };

                    if (length + piece.Length > text.Length)
                    {
                        throw FontReader.Fail("CFF DICT", "a real number is too long");
                    }

                    piece.AsSpan().CopyTo(text[length..]);
                    length += piece.Length;
                }
            }
        }
    }

    /// <summary>Executes one Type 2 charstring, emitting its contours into a <see cref="GlyphOutline.Builder"/>.</summary>
    private sealed class Type2Interpreter
    {
        private const int MaxStack = 48;
        private const int MaxCallDepth = 10;

        /// <summary>Most operators executed for one glyph, which stops subroutine loops crafted to never end.</summary>
        private const int MaxOperations = 200_000;

        private readonly CffOutlines font;
        private readonly GlyphOutline.Builder builder;
        private readonly float[] stack = new float[MaxStack];
        private readonly float[] transient = new float[32];
        private int count;
        private float x;
        private float y;
        private int stems;
        private bool widthSeen;
        private bool open;
        private int operations;
        private float offsetX;
        private float offsetY;
        private CffIndex local;
        private uint randomState = 0x2545F491;

        public Type2Interpreter(CffOutlines font, GlyphOutline.Builder builder)
        {
            this.font = font;
            this.builder = builder;
        }

        /// <summary>Runs the charstring of <paramref name="glyph"/> with its outline shifted by the given offset.</summary>
        public void Run(int glyph, float dx, float dy, bool allowSeac)
        {
            this.offsetX = dx;
            this.offsetY = dy;
            this.x = 0;
            this.y = 0;
            this.count = 0;
            this.stems = 0;
            this.widthSeen = false;
            this.open = false;
            this.local = this.font.LocalSubrsFor(glyph);
            (int start, int end) = this.font.charStrings.Range(glyph);
            this.Execute(start, end, 0, allowSeac);
            this.CloseContour();
        }

        /// <summary>Executes a charstring or subroutine; returns <see langword="true"/> when <c>endchar</c> ended the glyph.</summary>
        private bool Execute(int position, int end, int depth, bool allowSeac)
        {
            byte[] code = this.font.data;
            while (position < end)
            {
                if (++this.operations > MaxOperations)
                {
                    throw FontReader.Fail(Label, "a charstring executes too many operators");
                }

                int b0 = code[position++];
                if (b0 >= 32 || b0 == 28)
                {
                    float value;
                    if (b0 == 28)
                    {
                        this.Need(position + 2 <= end);
                        value = (short)((code[position] << 8) | code[position + 1]);
                        position += 2;
                    }
                    else if (b0 <= 246)
                    {
                        value = b0 - 139;
                    }
                    else if (b0 <= 250)
                    {
                        this.Need(position < end);
                        value = ((b0 - 247) * 256) + code[position++] + 108;
                    }
                    else if (b0 <= 254)
                    {
                        this.Need(position < end);
                        value = (-(b0 - 251) * 256) - code[position++] - 108;
                    }
                    else
                    {
                        this.Need(position + 4 <= end);
                        int fixedValue = (code[position] << 24) | (code[position + 1] << 16) | (code[position + 2] << 8) | code[position + 3];
                        value = fixedValue / 65536f;
                        position += 4;
                    }

                    this.Push(value);
                    continue;
                }

                switch (b0)
                {
                    case 1: // hstem
                    case 3: // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        this.TakeWidth(this.count % 2 != 0);
                        this.stems += this.count / 2;
                        this.count = 0;
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        // Values left on the stack are an implied vstem.
                        this.TakeWidth(this.count % 2 != 0);
                        this.stems += this.count / 2;
                        this.count = 0;
                        position += (this.stems + 7) / 8;
                        this.Need(position <= end);
                        break;

                    case 21: // rmoveto
                        this.TakeWidth(this.count > 2);
                        this.Require(2);
                        this.MoveTo(this.x + this.stack[0], this.y + this.stack[1]);
                        this.count = 0;
                        break;

                    case 22: // hmoveto
                        this.TakeWidth(this.count > 1);
                        this.Require(1);
                        this.MoveTo(this.x + this.stack[0], this.y);
                        this.count = 0;
                        break;

                    case 4: // vmoveto
                        this.TakeWidth(this.count > 1);
                        this.Require(1);
                        this.MoveTo(this.x, this.y + this.stack[0]);
                        this.count = 0;
                        break;

                    case 5: // rlineto
                        for (int i = 0; i + 1 < this.count; i += 2)
                        {
                            this.LineTo(this.x + this.stack[i], this.y + this.stack[i + 1]);
                        }

                        this.count = 0;
                        break;

                    case 6: // hlineto
                    case 7: // vlineto
                    {
                        bool horizontal = b0 == 6;
                        for (int i = 0; i < this.count; i++)
                        {
                            if (horizontal)
                            {
                                this.LineTo(this.x + this.stack[i], this.y);
                            }
                            else
                            {
                                this.LineTo(this.x, this.y + this.stack[i]);
                            }

                            horizontal = !horizontal;
                        }

                        this.count = 0;
                        break;
                    }

                    case 8: // rrcurveto
                        for (int i = 0; i + 5 < this.count; i += 6)
                        {
                            this.CurveRelative(i);
                        }

                        this.count = 0;
                        break;

                    case 24: // rcurveline
                    {
                        int i = 0;
                        for (; i + 7 < this.count; i += 6)
                        {
                            this.CurveRelative(i);
                        }

                        if (i + 1 < this.count)
                        {
                            this.LineTo(this.x + this.stack[i], this.y + this.stack[i + 1]);
                        }

                        this.count = 0;
                        break;
                    }

                    case 25: // rlinecurve
                    {
                        int i = 0;
                        for (; i + 7 < this.count; i += 2)
                        {
                            this.LineTo(this.x + this.stack[i], this.y + this.stack[i + 1]);
                        }

                        if (i + 5 < this.count)
                        {
                            this.CurveRelative(i);
                        }

                        this.count = 0;
                        break;
                    }

                    case 26: // vvcurveto
                    {
                        int i = 0;
                        float dx1 = 0;
                        if (this.count % 4 == 1)
                        {
                            dx1 = this.stack[0];
                            i = 1;
                        }

                        for (; i + 3 < this.count; i += 4)
                        {
                            this.Curve(dx1, this.stack[i], this.stack[i + 1], this.stack[i + 2], 0, this.stack[i + 3]);
                            dx1 = 0;
                        }

                        this.count = 0;
                        break;
                    }

                    case 27: // hhcurveto
                    {
                        int i = 0;
                        float dy1 = 0;
                        if (this.count % 4 == 1)
                        {
                            dy1 = this.stack[0];
                            i = 1;
                        }

                        for (; i + 3 < this.count; i += 4)
                        {
                            this.Curve(this.stack[i], dy1, this.stack[i + 1], this.stack[i + 2], this.stack[i + 3], 0);
                            dy1 = 0;
                        }

                        this.count = 0;
                        break;
                    }

                    case 30: // vhcurveto
                    case 31: // hvcurveto
                    {
                        bool horizontal = b0 == 31;
                        int i = 0;
                        while (i + 3 < this.count)
                        {
                            // The last curve of the run may carry one extra value for its final tangent.
                            float extra = this.count - i == 5 ? this.stack[i + 4] : 0;
                            if (horizontal)
                            {
                                this.Curve(this.stack[i], 0, this.stack[i + 1], this.stack[i + 2], extra, this.stack[i + 3]);
                            }
                            else
                            {
                                this.Curve(0, this.stack[i], this.stack[i + 1], this.stack[i + 2], this.stack[i + 3], extra);
                            }

                            horizontal = !horizontal;
                            i += 4;
                        }

                        this.count = 0;
                        break;
                    }

                    case 10: // callsubr
                    case 29: // callgsubr
                    {
                        this.Require(1);
                        CffIndex subrs = b0 == 10 ? this.local : this.font.globalSubrs;
                        int index = (int)this.stack[--this.count] + Bias(subrs.Count);
                        if ((uint)index >= (uint)subrs.Count)
                        {
                            throw FontReader.Fail(Label, $"a charstring calls subroutine {index} of {subrs.Count}");
                        }

                        if (depth >= MaxCallDepth)
                        {
                            throw FontReader.Fail(Label, $"subroutine calls nest deeper than {MaxCallDepth} levels");
                        }

                        (int subStart, int subEnd) = subrs.Range(index);
                        if (this.Execute(subStart, subEnd, depth + 1, allowSeac))
                        {
                            return true;
                        }

                        break;
                    }

                    case 11: // return
                        return false;

                    case 14: // endchar
                        this.TakeWidth(this.count is 1 or 5);
                        if (this.count >= 4 && allowSeac)
                        {
                            this.Seac();
                        }

                        this.count = 0;
                        this.CloseContour();
                        return true;

                    case 12:
                        this.Need(position < end);
                        this.Escape(code[position++]);
                        break;

                    default:
                        // Reserved operators clear the stack, as the common interpreters do.
                        this.count = 0;
                        break;
                }
            }

            return false;
        }

        private void Escape(int op)
        {
            switch (op)
            {
                case 35: // flex
                    this.Require(12);
                    this.CurveRelative(0);
                    this.CurveRelative(6);
                    break;

                case 34: // hflex
                {
                    this.Require(7);
                    float startY = this.y;
                    float[] s = this.stack;
                    this.Curve(s[0], 0, s[1], s[2], s[3], 0);
                    this.Curve(s[4], 0, s[5], startY - this.y, s[6], 0);
                    break;
                }

                case 36: // hflex1
                {
                    this.Require(9);
                    float startY = this.y;
                    float[] s = this.stack;
                    this.Curve(s[0], s[1], s[2], s[3], s[4], 0);
                    this.Curve(s[5], 0, s[6], s[7], s[8], startY - (this.y + s[7]));
                    break;
                }

                case 37: // flex1
                {
                    this.Require(11);
                    float[] s = this.stack;
                    float dx = s[0] + s[2] + s[4] + s[6] + s[8];
                    float dy = s[1] + s[3] + s[5] + s[7] + s[9];
                    float startX = this.x;
                    float startY = this.y;
                    this.Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    float lastX, lastY;
                    if (MathF.Abs(dx) > MathF.Abs(dy))
                    {
                        lastX = s[10];
                        lastY = startY - (this.y + s[7] + s[9]);
                    }
                    else
                    {
                        lastX = startX - (this.x + s[6] + s[8]);
                        lastY = s[10];
                    }

                    this.Curve(s[6], s[7], s[8], s[9], lastX, lastY);
                    break;
                }

                case 3: // and
                    this.Binary((a, b) => a != 0 && b != 0 ? 1 : 0);
                    return;
                case 4: // or
                    this.Binary((a, b) => a != 0 || b != 0 ? 1 : 0);
                    return;
                case 5: // not
                    this.Unary(a => a == 0 ? 1 : 0);
                    return;
                case 9: // abs
                    this.Unary(MathF.Abs);
                    return;
                case 10: // add
                    this.Binary((a, b) => a + b);
                    return;
                case 11: // sub
                    this.Binary((a, b) => a - b);
                    return;
                case 12: // div
                    this.Binary((a, b) => b == 0 ? 0 : a / b);
                    return;
                case 14: // neg
                    this.Unary(a => -a);
                    return;
                case 15: // eq
                    this.Binary((a, b) => a == b ? 1 : 0);
                    return;
                case 18: // drop
                    this.Require(1);
                    this.count--;
                    return;
                case 20: // put
                {
                    this.Require(2);
                    int index = (int)this.stack[--this.count];
                    float value = this.stack[--this.count];
                    if ((uint)index < (uint)this.transient.Length)
                    {
                        this.transient[index] = value;
                    }

                    return;
                }

                case 21: // get
                {
                    this.Require(1);
                    int index = (int)this.stack[this.count - 1];
                    this.stack[this.count - 1] = (uint)index < (uint)this.transient.Length ? this.transient[index] : 0;
                    return;
                }

                case 22: // ifelse
                {
                    this.Require(4);
                    float v2 = this.stack[--this.count];
                    float v1 = this.stack[--this.count];
                    float s2 = this.stack[--this.count];
                    float s1 = this.stack[--this.count];
                    this.Push(v1 <= v2 ? s1 : s2);
                    return;
                }

                case 23: // random: deterministic, so rendering is reproducible
                    this.randomState ^= this.randomState << 13;
                    this.randomState ^= this.randomState >> 17;
                    this.randomState ^= this.randomState << 5;
                    this.Push(((this.randomState & 0xFFFF) + 1) / 65537f);
                    return;
                case 24: // mul
                    this.Binary((a, b) => a * b);
                    return;
                case 26: // sqrt
                    this.Unary(a => a > 0 ? MathF.Sqrt(a) : 0);
                    return;
                case 27: // dup
                    this.Require(1);
                    this.Push(this.stack[this.count - 1]);
                    return;
                case 28: // exch
                {
                    this.Require(2);
                    (this.stack[this.count - 1], this.stack[this.count - 2]) = (this.stack[this.count - 2], this.stack[this.count - 1]);
                    return;
                }

                case 29: // index
                {
                    this.Require(1);
                    int index = (int)this.stack[this.count - 1];
                    int source = this.count - 2 - Math.Max(index, 0);
                    this.stack[this.count - 1] = source >= 0 ? this.stack[source] : 0;
                    return;
                }

                case 30: // roll
                {
                    this.Require(2);
                    int shift = (int)this.stack[--this.count];
                    int n = (int)this.stack[--this.count];
                    if (n > 0 && n <= this.count)
                    {
                        Span<float> items = this.stack.AsSpan(this.count - n, n);
                        int k = ((shift % n) + n) % n;
                        items.Reverse();
                        items[..k].Reverse();
                        items[k..].Reverse();
                    }

                    return;
                }

                default:
                    // dotsection and reserved operators: no drawing effect.
                    break;
            }

            this.count = 0;
        }

        /// <summary>Composes an accented character from two standard-encoded glyphs (endchar with four arguments).</summary>
        private void Seac()
        {
            int achar = (int)this.stack[this.count - 1];
            int bchar = (int)this.stack[this.count - 2];
            float ady = this.stack[this.count - 3];
            float adx = this.stack[this.count - 4];
            Dictionary<int, int> glyphs = this.font.glyphBySid.Value;
            if (!glyphs.TryGetValue(StandardEncoding.Sid(bchar), out int baseGlyph)
                || !glyphs.TryGetValue(StandardEncoding.Sid(achar), out int accentGlyph))
            {
                return;
            }

            this.CloseContour();
            var nested = new Type2Interpreter(this.font, this.builder) { operations = this.operations };
            nested.Run(baseGlyph, this.offsetX, this.offsetY, allowSeac: false);
            nested.Run(accentGlyph, this.offsetX + adx, this.offsetY + ady, allowSeac: false);
            this.operations = nested.operations;
        }

        private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        private void TakeWidth(bool hasWidth)
        {
            // The first stack-clearing operator may carry the advance width (relative to nominalWidthX) as an
            // extra leading operand. hmtx is authoritative for advances, so it is only removed.
            if (!this.widthSeen && hasWidth && this.count > 0)
            {
                Array.Copy(this.stack, 1, this.stack, 0, this.count - 1);
                this.count--;
            }

            this.widthSeen = true;
        }

        private void MoveTo(float nx, float ny)
        {
            this.CloseContour();
            this.x = nx;
            this.y = ny;
            this.builder.MoveTo(nx + this.offsetX, ny + this.offsetY);
            this.open = true;
        }

        private void LineTo(float nx, float ny)
        {
            this.EnsureOpen();
            this.x = nx;
            this.y = ny;
            this.builder.LineTo(nx + this.offsetX, ny + this.offsetY);
        }

        private void CurveRelative(int i)
            => this.Curve(this.stack[i], this.stack[i + 1], this.stack[i + 2], this.stack[i + 3], this.stack[i + 4], this.stack[i + 5]);

        private void Curve(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
        {
            this.EnsureOpen();
            float x1 = this.x + dx1;
            float y1 = this.y + dy1;
            float x2 = x1 + dx2;
            float y2 = y1 + dy2;
            this.x = x2 + dx3;
            this.y = y2 + dy3;
            this.builder.CubicTo(
                x1 + this.offsetX,
                y1 + this.offsetY,
                x2 + this.offsetX,
                y2 + this.offsetY,
                this.x + this.offsetX,
                this.y + this.offsetY);
        }

        private void EnsureOpen()
        {
            if (!this.open)
            {
                // Drawing before the first moveto starts at the origin, as the common interpreters do.
                this.builder.MoveTo(this.x + this.offsetX, this.y + this.offsetY);
                this.open = true;
            }
        }

        private void CloseContour()
        {
            if (this.open)
            {
                this.builder.Close();
                this.open = false;
            }
        }

        private void Push(float value)
        {
            if (this.count >= MaxStack)
            {
                throw FontReader.Fail(Label, "the charstring operand stack overflows");
            }

            this.stack[this.count++] = value;
        }

        private void Require(int n)
        {
            if (this.count < n)
            {
                throw FontReader.Fail(Label, "an operator has too few operands");
            }
        }

        private void Need(bool condition)
        {
            if (!condition)
            {
                throw FontReader.Fail(Label, "a charstring is truncated");
            }
        }

        private void Unary(Func<float, float> op)
        {
            this.Require(1);
            this.stack[this.count - 1] = op(this.stack[this.count - 1]);
        }

        private void Binary(Func<float, float, float> op)
        {
            this.Require(2);
            float b = this.stack[--this.count];
            this.stack[this.count - 1] = op(this.stack[this.count - 1], b);
        }
    }
}
