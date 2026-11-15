namespace EasyImageSharp.Fonts.Tables;

/// <summary>The fields of the <c>head</c> table the engine uses.</summary>
internal sealed class HeadTable
{
    private const string Label = "head table";

    public int UnitsPerEm { get; private init; }

    public short XMin { get; private init; }

    public short YMin { get; private init; }

    public short XMax { get; private init; }

    public short YMax { get; private init; }

    /// <summary>Bit 0 bold, bit 1 italic.</summary>
    public ushort MacStyle { get; private init; }

    /// <summary>0 for 16-bit loca offsets (stored halved), 1 for 32-bit offsets.</summary>
    public short IndexToLocFormat { get; private init; }

    public static HeadTable Parse(ReadOnlySpan<byte> table)
    {
        var r = new FontReader(table, Label);
        ushort major = r.ReadUInt16();
        if (major != 1)
        {
            throw FontReader.Fail(Label, $"version {major} is not 1");
        }

        r.Position = 18;
        int unitsPerEm = r.ReadUInt16();
        if (unitsPerEm is < 1 or > 16384)
        {
            throw FontReader.Fail(Label, $"unitsPerEm {unitsPerEm} is outside 1-16384");
        }

        r.Position = 36;
        short xMin = r.ReadInt16();
        short yMin = r.ReadInt16();
        short xMax = r.ReadInt16();
        short yMax = r.ReadInt16();
        ushort macStyle = r.ReadUInt16();
        r.Skip(4);
        short indexToLocFormat = r.ReadInt16();
        return new HeadTable
        {
            UnitsPerEm = unitsPerEm,
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            MacStyle = macStyle,
            IndexToLocFormat = indexToLocFormat,
        };
    }
}

/// <summary>The fields of the <c>hhea</c> table the engine uses.</summary>
internal sealed class HheaTable
{
    private const string Label = "hhea table";

    public short Ascender { get; private init; }

    public short Descender { get; private init; }

    public short LineGap { get; private init; }

    public int NumberOfHMetrics { get; private init; }

    public static HheaTable Parse(ReadOnlySpan<byte> table)
    {
        var r = new FontReader(table, Label);
        ushort major = r.ReadUInt16();
        if (major != 1)
        {
            throw FontReader.Fail(Label, $"version {major} is not 1");
        }

        r.ReadUInt16();
        short ascender = r.ReadInt16();
        short descender = r.ReadInt16();
        short lineGap = r.ReadInt16();
        r.Position = 34;
        int numberOfHMetrics = r.ReadUInt16();
        return new HheaTable
        {
            Ascender = ascender,
            Descender = descender,
            LineGap = lineGap,
            NumberOfHMetrics = numberOfHMetrics,
        };
    }
}

/// <summary>The glyph count from the <c>maxp</c> table (version 0.5 for CFF fonts, 1.0 for TrueType).</summary>
internal static class MaxpTable
{
    public static int ParseGlyphCount(ReadOnlySpan<byte> table)
    {
        const string Label = "maxp table";
        var r = new FontReader(table, Label);
        uint version = r.ReadUInt32();
        if (version is not (0x00005000 or 0x00010000))
        {
            throw FontReader.Fail(Label, $"version 0x{version:X8} is not 0.5 or 1.0");
        }

        int numGlyphs = r.ReadUInt16();
        if (numGlyphs == 0)
        {
            throw FontReader.Fail(Label, "the font has no glyphs");
        }

        return numGlyphs;
    }
}

/// <summary>
/// Horizontal metrics from <c>hmtx</c>: one advance width and left side bearing per glyph. Glyphs after the
/// last long metric repeat its advance. A truncated table is tolerated the way common rasterisers tolerate it:
/// missing advances repeat the last one present and missing side bearings read as zero.
/// </summary>
internal sealed class HmtxTable
{
    private readonly ushort[] advances;
    private readonly short[] leftSideBearings;

    private HmtxTable(ushort[] advances, short[] leftSideBearings)
    {
        this.advances = advances;
        this.leftSideBearings = leftSideBearings;
    }

    public static HmtxTable Parse(ReadOnlySpan<byte> table, int numberOfHMetrics, int numGlyphs)
    {
        const string Label = "hmtx table";
        if (numberOfHMetrics < 1)
        {
            throw FontReader.Fail(Label, "hhea declares no horizontal metrics");
        }

        numberOfHMetrics = Math.Min(numberOfHMetrics, numGlyphs);
        ushort[] advances = new ushort[numGlyphs];
        short[] lsbs = new short[numGlyphs];
        int longCount = Math.Min(numberOfHMetrics, table.Length / 4);
        if (longCount == 0)
        {
            throw FontReader.Fail(Label, "the table holds no complete metric");
        }

        for (int i = 0; i < longCount; i++)
        {
            advances[i] = FontReader.UInt16At(table, i * 4, Label);
            lsbs[i] = (short)FontReader.UInt16At(table, (i * 4) + 2, Label);
        }

        ushort last = advances[longCount - 1];
        int lsbStart = numberOfHMetrics * 4;
        for (int i = longCount; i < numGlyphs; i++)
        {
            advances[i] = last;
            if (i >= numberOfHMetrics)
            {
                int offset = lsbStart + ((i - numberOfHMetrics) * 2);
                if (offset + 2 <= table.Length)
                {
                    lsbs[i] = (short)FontReader.UInt16At(table, offset, Label);
                }
            }
        }

        return new HmtxTable(advances, lsbs);
    }

    public int GetAdvance(int glyph) => (uint)glyph < (uint)this.advances.Length ? this.advances[glyph] : 0;

    public int GetLeftSideBearing(int glyph) => (uint)glyph < (uint)this.leftSideBearings.Length ? this.leftSideBearings[glyph] : 0;
}

/// <summary>The fields of the <c>OS/2</c> table the engine uses.</summary>
internal sealed class Os2Table
{
    private const string Label = "OS/2 table";

    public int WeightClass { get; private init; }

    /// <summary>Bit 0 italic, bit 5 bold, bit 6 regular, bit 7 use typographic metrics, bit 9 oblique.</summary>
    public ushort FsSelection { get; private init; }

    public short TypoAscender { get; private init; }

    public short TypoDescender { get; private init; }

    public short TypoLineGap { get; private init; }

    public ushort WinAscent { get; private init; }

    public ushort WinDescent { get; private init; }

    /// <summary>The x-height, or 0 when the table version predates the field.</summary>
    public short XHeight { get; private init; }

    /// <summary>The cap height, or 0 when the table version predates the field.</summary>
    public short CapHeight { get; private init; }

    public static Os2Table? TryParse(ReadOnlySpan<byte> table)
    {
        // Version 0 tables written by old Apple tools stop at 68 bytes, before the typographic metrics; such a
        // table carries nothing the engine uses beyond the weight and selection flags.
        if (table.Length < 68)
        {
            return null;
        }

        var r = new FontReader(table, Label);
        ushort version = r.ReadUInt16();
        r.ReadInt16();
        int weight = r.ReadUInt16();
        r.Position = 62;
        ushort fsSelection = r.ReadUInt16();
        short typoAscender = 0, typoDescender = 0, typoLineGap = 0;
        ushort winAscent = 0, winDescent = 0;
        if (table.Length >= 78)
        {
            r.Position = 68;
            typoAscender = r.ReadInt16();
            typoDescender = r.ReadInt16();
            typoLineGap = r.ReadInt16();
            winAscent = r.ReadUInt16();
            winDescent = r.ReadUInt16();
        }

        short xHeight = 0, capHeight = 0;
        if (version >= 2 && table.Length >= 90)
        {
            r.Position = 86;
            xHeight = r.ReadInt16();
            capHeight = r.ReadInt16();
        }

        return new Os2Table
        {
            WeightClass = weight,
            FsSelection = fsSelection,
            TypoAscender = typoAscender,
            TypoDescender = typoDescender,
            TypoLineGap = typoLineGap,
            WinAscent = winAscent,
            WinDescent = winDescent,
            XHeight = xHeight,
            CapHeight = capHeight,
        };
    }
}

/// <summary>The fields of the <c>post</c> table the engine uses (glyph names are not read).</summary>
internal sealed class PostTable
{
    public float ItalicAngle { get; private init; }

    public short UnderlinePosition { get; private init; }

    public short UnderlineThickness { get; private init; }

    public bool IsFixedPitch { get; private init; }

    public static PostTable? TryParse(ReadOnlySpan<byte> table)
    {
        if (table.Length < 16)
        {
            return null;
        }

        var r = new FontReader(table, "post table");
        r.ReadUInt32();
        float italicAngle = r.ReadFixed();
        short underlinePosition = r.ReadInt16();
        short underlineThickness = r.ReadInt16();
        uint isFixedPitch = r.ReadUInt32();
        return new PostTable
        {
            ItalicAngle = italicAngle,
            UnderlinePosition = underlinePosition,
            UnderlineThickness = underlineThickness,
            IsFixedPitch = isFixedPitch != 0,
        };
    }
}

/// <summary>Derives a face's style flags and weight from its <c>OS/2</c> and <c>head</c> tables.</summary>
internal static class FaceStyle
{
    public static (FontStyle Style, int Weight) From(Os2Table? os2, HeadTable head)
    {
        // OS/2 fsSelection: bit 0 italic, bit 5 bold, bit 9 oblique; head macStyle: bit 0 bold, bit 1 italic.
        ushort fsSelection = os2?.FsSelection ?? 0;
        bool bold = (fsSelection & 0x20) != 0 || (head.MacStyle & 0x1) != 0;
        bool italic = (fsSelection & 0x201) != 0 || (head.MacStyle & 0x2) != 0;
        FontStyle style = (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular);
        int weight = os2?.WeightClass is int w && w is >= 1 and <= 1000 ? w : bold ? 700 : 400;
        return (style, weight);
    }
}
