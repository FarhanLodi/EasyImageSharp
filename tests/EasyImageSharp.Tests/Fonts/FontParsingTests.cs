using System.Buffers.Binary;
using System.Text.Json;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Rendering;
using EasyImageSharp.Fonts.Tables;
using Xunit;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>Font parsing checked against fontTools (reference.json) and against hand-built tables.</summary>
public class FontParsingTests
{
    public static TheoryData<string> ReferenceFonts => new() { FontAssets.Regular, FontAssets.Bold, FontAssets.Cff, FontAssets.Cid };

    [Theory]
    [MemberData(nameof(ReferenceFonts))]
    public void Metrics_and_names_match_fontTools(string file)
    {
        FontFace face = FontAssets.Face(file);
        JsonElement reference = FontAssets.Reference(file);
        Assert.Equal(reference.GetProperty("unitsPerEm").GetInt32(), face.UnitsPerEm);
        Assert.Equal(reference.GetProperty("ascender").GetInt32(), face.Ascender);
        Assert.Equal(reference.GetProperty("descender").GetInt32(), face.Descender);
        Assert.Equal(reference.GetProperty("lineGap").GetInt32(), face.LineGap);
        Assert.Equal(reference.GetProperty("numGlyphs").GetInt32(), face.GlyphCount);
        Assert.Equal(reference.GetProperty("familyName").GetString(), face.FamilyName);
        Assert.Equal(reference.GetProperty("subfamilyName").GetString(), face.SubfamilyName);
        Assert.Equal(reference.GetProperty("fullName").GetString(), face.FullName);
        Assert.Equal(reference.GetProperty("weight").GetInt32(), face.Weight);
        Assert.Equal(file.EndsWith(".otf", StringComparison.Ordinal) ? FontOutlineFormat.Cff : FontOutlineFormat.TrueType, face.OutlineFormat);
    }

    [Theory]
    [MemberData(nameof(ReferenceFonts))]
    public void Cmap_advances_and_control_bounds_match_fontTools(string file)
    {
        FontFace face = FontAssets.Face(file);
        foreach (JsonElement entry in FontAssets.Reference(file).GetProperty("glyphs").EnumerateArray())
        {
            int codePoint = entry.GetProperty("codepoint").GetInt32();
            int glyph = entry.GetProperty("glyph").GetInt32();
            Assert.Equal(glyph, face.GetGlyphIndex(codePoint));
            Assert.Equal(entry.GetProperty("advance").GetInt32(), face.GetAdvanceWidth(glyph));

            GlyphOutline outline = face.GetOutline(glyph);
            JsonElement bounds = entry.GetProperty("bounds");
            if (bounds.ValueKind == JsonValueKind.Null)
            {
                Assert.True(outline.IsEmpty, $"U+{codePoint:X4} should have no outline");
                continue;
            }

            // CFF operands are 16.16 fixed point, so allow a hair of rounding there.
            float[] expected = bounds.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
            const float Tolerance = 0.01f;
            Assert.InRange(outline.XMin, expected[0] - Tolerance, expected[0] + Tolerance);
            Assert.InRange(outline.YMin, expected[1] - Tolerance, expected[1] + Tolerance);
            Assert.InRange(outline.XMax, expected[2] - Tolerance, expected[2] + Tolerance);
            Assert.InRange(outline.YMax, expected[3] - Tolerance, expected[3] + Tolerance);
        }
    }

    [Theory]
    [InlineData(FontAssets.Regular)]
    [InlineData(FontAssets.Bold)]
    [InlineData(FontAssets.Cff)]
    public void Gpos_pair_kerning_matches_fontTools(string file)
    {
        FontFace face = FontAssets.Face(file);
        Assert.True(face.HasKerning);
        int nonZero = 0;
        foreach (JsonElement pair in FontAssets.Reference(file).GetProperty("kerning").EnumerateArray())
        {
            int expected = pair.GetProperty("value").GetInt32();
            int actual = face.GetKerning(pair.GetProperty("left").GetInt32(), pair.GetProperty("right").GetInt32());
            Assert.True(expected == actual, $"{pair.GetProperty("pair").GetString()}: expected {expected}, got {actual}");
            nonZero += expected != 0 ? 1 : 0;
        }

        Assert.True(nonZero >= 5, "the sample pairs should include real kerning");
    }

    [Fact]
    public void Legacy_kern_table_matches_fontTools()
    {
        FontSource source = FontContainer.Open(FontAssets.Read(FontAssets.Regular))[0];
        KernTableKerning? kern = KernTableKerning.Parse(source.GetTable(FontReader.Tag("kern")));
        Assert.NotNull(kern);
        foreach (JsonElement pair in FontAssets.Reference(FontAssets.Regular).GetProperty("legacyKerning").EnumerateArray())
        {
            int expected = pair.GetProperty("value").GetInt32();
            Assert.Equal(expected, kern.Get(pair.GetProperty("left").GetInt32(), pair.GetProperty("right").GetInt32()).FirstAdvance);
        }
    }

    [Fact]
    public void Supplementary_plane_code_point_maps_through_format_12()
    {
        FontFace face = FontAssets.Face(FontAssets.Regular);
        Assert.NotEqual(0, face.GetGlyphIndex('A'));
        Assert.Equal(face.GetGlyphIndex('A'), face.GetGlyphIndex(0x1D400));
        Assert.False(face.HasGlyph(0x1D401));
    }

    [Fact]
    public void Collection_yields_both_faces_with_their_styles()
    {
        IReadOnlyList<FontFace> faces = FontFace.LoadAll(FontAssets.Path(FontAssets.Collection));
        Assert.Equal(2, faces.Count);
        Assert.Equal(FontStyle.Regular, faces[0].Style);
        Assert.Equal(FontStyle.Bold, faces[1].Style);
        Assert.Equal(700, faces[1].Weight);
        Assert.Equal("DejaVu Sans", faces[1].FamilyName);

        FontFace bold = FontFace.Load(FontAssets.Path(FontAssets.Collection), 1);
        AssertSameOutlines(FontAssets.Face(FontAssets.Bold), bold);
        Assert.Throws<ArgumentOutOfRangeException>(() => FontFace.Load(FontAssets.Path(FontAssets.Collection), 2));
    }

    [Theory]
    [InlineData(FontAssets.Woff)]
    [InlineData(FontAssets.Woff2)]
    public void Web_fonts_decode_to_the_same_font_as_the_ttf(string file)
    {
        FontFace web = FontFace.Load(FontAssets.Read(file));
        FontFace ttf = FontAssets.Face(FontAssets.Regular);
        Assert.Equal(ttf.FamilyName, web.FamilyName);
        Assert.Equal(ttf.UnitsPerEm, web.UnitsPerEm);
        Assert.Equal(ttf.Ascender, web.Ascender);
        Assert.Equal(ttf.GlyphCount, web.GlyphCount);
        for (int cp = 0x20; cp < 0x100; cp++)
        {
            Assert.Equal(ttf.GetGlyphIndex(cp), web.GetGlyphIndex(cp));
        }

        for (int g = 0; g < ttf.GlyphCount; g++)
        {
            Assert.Equal(ttf.GetAdvanceWidth(g), web.GetAdvanceWidth(g));
        }

        AssertSameOutlines(ttf, web);
        Assert.Equal(ttf.GetKerning(ttf.GetGlyphIndex('A'), ttf.GetGlyphIndex('V')), web.GetKerning(web.GetGlyphIndex('A'), web.GetGlyphIndex('V')));
    }

    [Fact]
    public void Stream_bytes_and_path_loading_agree()
    {
        byte[] data = FontAssets.Read(FontAssets.Cff);
        using var stream = new MemoryStream(data);
        FontFace fromStream = FontFace.Load(stream);
        FontFace fromBytes = FontFace.Load(data);
        FontFace fromPath = FontFace.Load(FontAssets.Path(FontAssets.Cff));
        Assert.Equal(fromPath.FullName, fromStream.FullName);
        AssertSameOutlines(fromPath, fromBytes);
    }

    [Fact]
    public void Font_size_units_convert_points_with_dpi()
    {
        FontFace face = FontAssets.Face(FontAssets.Regular);
        var points = new Font(face, 12, FontSizeUnit.Point, 96);
        Assert.Equal(16f, points.SizeInPixels, 4);
        var pixels = new Font(face, 20);
        Assert.Equal(20f, pixels.SizeInPixels);
        Assert.Equal(20f * face.Ascender / face.UnitsPerEm, pixels.Ascent, 4);
        Assert.Equal(20f * -face.Descender / face.UnitsPerEm, pixels.Descent, 4);
        Assert.Equal(40f, pixels.WithSize(40).SizeInPixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Font(face, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Font(face, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Font(face, 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Font(face, 12, FontSizeUnit.Point, 0));
    }

    [Fact]
    public void Collection_groups_faces_into_families_and_matches_styles()
    {
        var collection = new FontCollection();
        collection.Add(FontAssets.Path(FontAssets.Regular));
        collection.Add(FontAssets.Read(FontAssets.Bold));
        collection.Add(FontAssets.Face(FontAssets.Cff));

        Assert.True(collection.TryGetFamily("dejavu sans", out FontFamily? family));
        Assert.Equal(new[] { FontStyle.Regular, FontStyle.Bold }, family.AvailableStyles);
        Assert.Equal(FontStyle.Bold, family.GetFace(FontStyle.Bold).Style);
        Assert.Equal(FontStyle.Bold, family.GetFace(FontStyle.BoldItalic).Style);
        Assert.Equal(FontStyle.Regular, family.GetFace(FontStyle.Italic).Style);
        Assert.False(family.TryGetFace(FontStyle.Italic, out _));
        Assert.Equal(24f, family.CreateFont(24, FontStyle.Bold).SizeInPixels);
        Assert.Equal(2, collection.Families.Count);
        Assert.Throws<KeyNotFoundException>(() => collection.GetFamily("No Such Family"));
    }

    [Fact]
    public void Lazy_indexing_reads_names_without_loading_and_loads_on_demand()
    {
        var collection = new FontCollection();
        Assert.Equal(2, collection.AddLazy(FontAssets.Path(FontAssets.Collection)));
        Assert.Equal(0, collection.AddLazy(FontAssets.Path("PROVENANCE.txt")));
        FontFamily family = collection.GetFamily("DejaVu Sans");
        Assert.Equal(new[] { FontStyle.Regular, FontStyle.Bold }, family.AvailableStyles);
        FontFace bold = family.GetFace(FontStyle.Bold);
        Assert.Equal(FontStyle.Bold, bold.Style);
        Assert.True(bold.HasGlyph(0x2665));
    }

    [Fact]
    public void System_font_discovery_does_not_throw()
    {
        Assert.NotEmpty(SystemFonts.SearchDirectories);
        IReadOnlyList<FontFamily> families = SystemFonts.Families;
        foreach (FontFamily family in families.Take(3))
        {
            // Loading may legitimately fail for a damaged system font, but only with the documented exceptions.
            try
            {
                FontFace face = family.GetFace();
                Assert.True(face.GlyphCount > 0);
            }
            catch (Exception ex) when (ex is InvalidFontException or NotSupportedException)
            {
            }
        }
    }

    // ----- Hand-built cmap subtables -----

    [Fact]
    public void Cmap_format_0_maps_bytes()
    {
        byte[] sub = new byte[6 + 256];
        BinaryPrimitives.WriteUInt16BigEndian(sub, 0);
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(2), (ushort)sub.Length);
        sub[6 + 'A'] = 7;
        CmapTable cmap = CmapTable.Parse(SfntBuilder.CmapWith(3, 1, sub));
        Assert.Equal(7, cmap.GetGlyph('A', 10));
        Assert.Equal(0, cmap.GetGlyph('B', 10));
        Assert.Equal(0, cmap.GetGlyph('A', 5));
        Assert.Equal(0, cmap.GetGlyph(0x141, 10));
    }

    [Fact]
    public void Cmap_format_4_with_range_offsets_and_deltas()
    {
        // Segment 1: 'a'-'c' through glyphIdArray (with a delta of 1); segment 2: 'x'-'z' by delta; terminator.
        const int segments = 3;
        var sub = new List<byte>();
        void W(int v)
        {
            sub.Add((byte)(v >> 8));
            sub.Add((byte)v);
        }

        W(4);
        W(0);
        W(0);
        W(segments * 2);
        W(0);
        W(0);
        W(0);
        W('c');
        W('z');
        W(0xFFFF);
        W(0);
        W('a');
        W('x');
        W(0xFFFF);
        W(1);
        W(100 - 'x');
        W(1);

        // idRangeOffset[0] points from its own position to glyphIdArray[0]: two more entries to skip, so 6 bytes.
        W(6);
        W(0);
        W(0);
        W(10);
        W(0);
        W(30);
        byte[] bytes = sub.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)bytes.Length);
        CmapTable cmap = CmapTable.Parse(SfntBuilder.CmapWith(3, 1, bytes));
        Assert.Equal(11, cmap.GetGlyph('a', 200));
        Assert.Equal(0, cmap.GetGlyph('b', 200));
        Assert.Equal(31, cmap.GetGlyph('c', 200));
        Assert.Equal(100, cmap.GetGlyph('x', 200));
        Assert.Equal(102, cmap.GetGlyph('z', 200));
        Assert.Equal(0, cmap.GetGlyph('d', 200));
        Assert.Equal(0, cmap.GetGlyph(0x10000, 200));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public void Cmap_trimmed_formats_map_dense_ranges(int format)
    {
        var sub = new List<byte>();
        void W16(int v)
        {
            sub.Add((byte)(v >> 8));
            sub.Add((byte)v);
        }

        void W32(int v)
        {
            W16(v >> 16);
            W16(v & 0xFFFF);
        }

        int first = format == 6 ? 0x41 : 0x10400;
        if (format == 6)
        {
            W16(6);
            W16(0);
            W16(0);
            W16(first);
            W16(3);
        }
        else
        {
            W16(10);
            W16(0);
            W32(0);
            W32(0);
            W32(first);
            W32(3);
        }

        W16(5);
        W16(6);
        W16(7);
        CmapTable cmap = CmapTable.Parse(SfntBuilder.CmapWith(format == 6 ? 3 : 3, format == 6 ? 1 : 10, sub.ToArray()));
        Assert.Equal(5, cmap.GetGlyph(first, 10));
        Assert.Equal(7, cmap.GetGlyph(first + 2, 10));
        Assert.Equal(0, cmap.GetGlyph(first + 3, 10));
        Assert.Equal(0, cmap.GetGlyph(first - 1, 10));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void Cmap_group_formats_map_ranges(int format)
    {
        var sub = new List<byte>();
        void W16(int v)
        {
            sub.Add((byte)(v >> 8));
            sub.Add((byte)v);
        }

        void W32(uint v)
        {
            W16((int)(v >> 16));
            W16((int)(v & 0xFFFF));
        }

        W16(format);
        W16(0);
        W32(0);
        W32(0);
        W32(2);

        // Deliberately out of order: the parser sorts groups.
        W32(0x1F600);
        W32(0x1F602);
        W32(50);
        W32(0x41);
        W32(0x43);
        W32(10);
        CmapTable cmap = CmapTable.Parse(SfntBuilder.CmapWith(3, 10, sub.ToArray()));
        Assert.Equal(10, cmap.GetGlyph('A', 100));
        Assert.Equal(format == 12 ? 12 : 10, cmap.GetGlyph('C', 100));
        Assert.Equal(format == 12 ? 51 : 50, cmap.GetGlyph(0x1F601, 100));
        Assert.Equal(0, cmap.GetGlyph('D', 100));
    }

    [Fact]
    public void Cmap_reads_symbol_tables_and_trusts_mac_roman_only_for_ascii()
    {
        byte[] symbol = new byte[6 + 256];
        BinaryPrimitives.WriteUInt16BigEndian(symbol.AsSpan(2), (ushort)symbol.Length);
        symbol[6 + 0x41] = 3;
        CmapTable sym = CmapTable.Parse(SfntBuilder.CmapWith(3, 0, symbol));
        Assert.Equal(3, sym.GetGlyph(0x41, 10));

        // A Macintosh Roman table is trusted for ASCII only.
        byte[] mac = new byte[6 + 256];
        BinaryPrimitives.WriteUInt16BigEndian(mac.AsSpan(2), (ushort)mac.Length);
        mac[6 + 0x41] = 4;
        mac[6 + 0xE9] = 5;
        CmapTable roman = CmapTable.Parse(SfntBuilder.CmapWith(1, 0, mac));
        Assert.Equal(4, roman.GetGlyph(0x41, 10));
        Assert.Equal(0, roman.GetGlyph(0xE9, 10));
    }

    // ----- Crafted fonts -----

    [Fact]
    public void Crafted_truetype_font_decodes_simple_and_composite_glyphs()
    {
        byte[] square = SfntBuilder.SimpleGlyph((0, 0), (0, 500), (500, 500), (500, 0));
        (byte[] glyf, byte[] loca) = SfntBuilder.GlyfLoca([], square, SfntBuilder.CompositeGlyph(1, 1));
        byte[] font = SfntBuilder.Minimal(3).Add("glyf", glyf).Add("loca", loca).Build();
        FontFace face = FontFace.Load(font);
        Assert.Equal(1, face.GetGlyphIndex('A'));
        Assert.True(face.GetOutline(0).IsEmpty);
        GlyphOutline simple = face.GetOutline(1);
        Assert.Equal(0, simple.XMin);
        Assert.Equal(500, simple.YMax);
        Assert.Equal(2, face.GetOutline(2).Verbs.Count(v => v == OutlineVerb.MoveTo));
    }

    [Fact]
    public void Self_referencing_and_exponential_composites_render_empty_quickly()
    {
        // Glyph 1 references itself; glyphs 2..21 each reference the next one twice (2^20 expansions).
        var glyphs = new List<byte[]> { Array.Empty<byte>(), SfntBuilder.CompositeGlyph(1) };
        for (int g = 2; g < 22; g++)
        {
            glyphs.Add(SfntBuilder.CompositeGlyph(g + 1, g + 1));
        }

        glyphs.Add(SfntBuilder.SimpleGlyph((0, 0), (0, 10), (10, 10)));
        (byte[] glyf, byte[] loca) = SfntBuilder.GlyfLoca(glyphs.ToArray());
        FontFace face = FontFace.Load(SfntBuilder.Minimal(glyphs.Count).Add("glyf", glyf).Add("loca", loca).Build());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(face.GetOutline(1).IsEmpty);
        Assert.True(face.GetOutline(2).IsEmpty);
        Assert.False(face.GetOutline(glyphs.Count - 1).IsEmpty);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
    }

    [Fact]
    public void Cff_subroutine_recursion_renders_empty()
    {
        // A CFF whose only glyph calls local subroutine 0, which calls itself forever.
        byte[] cff = CffBuilder.Build(charStrings: [[139, 139, 21, 32, 10, 14]], localSubrs: [[32, 10, 11]]);
        byte[] font = SfntBuilder.Minimal(1).Add("CFF ", cff).Add("maxp", SfntBuilder.Maxp(1, cff: true)).Build();
        FontFace face = FontFace.Load(font);
        Assert.Equal(FontOutlineFormat.Cff, face.OutlineFormat);
        Assert.True(face.GetOutline(0).IsEmpty);
    }

    [Fact]
    public void Cff_charstring_draws_a_box_with_hints_and_a_global_subroutine()
    {
        // 50 100 rmoveto (with width 600 first), a stem and hintmask, then a global subroutine drawing the box.
        byte[] width = CffBuilder.Number(600);
        byte[] glyph = [.. width, .. CffBuilder.Number(0), .. CffBuilder.Number(20), 1, 19, 0x80, .. CffBuilder.Number(50), .. CffBuilder.Number(100), 21, .. CffBuilder.Number(-107), 29, 14];
        byte[] box = [.. CffBuilder.Number(400), 6, .. CffBuilder.Number(300), 7, .. CffBuilder.Number(-400), 6, 11];
        byte[] cff = CffBuilder.Build(charStrings: [glyph], globalSubrs: [box]);
        FontFace face = FontFace.Load(SfntBuilder.Minimal(1).Add("CFF ", cff).Add("maxp", SfntBuilder.Maxp(1, cff: true)).Build());
        GlyphOutline outline = face.GetOutline(0);
        Assert.Equal(50, outline.XMin);
        Assert.Equal(100, outline.YMin);
        Assert.Equal(450, outline.XMax);
        Assert.Equal(400, outline.YMax);
    }

    [Fact]
    public void Cff_seac_composes_base_and_accent_through_the_charset()
    {
        // Glyph 1 is 'A' (standard string 34), glyph 2 is 'acute' (125); glyph 3 is "A + acute raised by 500"
        // written as endchar with four arguments (adx ady bchar achar).
        byte[] square = [.. CffBuilder.Number(0), .. CffBuilder.Number(0), 21, .. CffBuilder.Number(100), 6, .. CffBuilder.Number(100), 7, .. CffBuilder.Number(-100), 6, 14];
        byte[] tick = [.. CffBuilder.Number(10), .. CffBuilder.Number(0), 21, .. CffBuilder.Number(20), 6, .. CffBuilder.Number(30), 7, .. CffBuilder.Number(-20), 6, 14];
        byte[] seac = [.. CffBuilder.Number(0), .. CffBuilder.Number(500), .. CffBuilder.Number(65), 28, 0, 194, 14];
        byte[] cff = CffBuilder.Build(charStrings: [[14], square, tick, seac], charsetSids: [34, 125, 391]);
        FontFace face = FontFace.Load(SfntBuilder.Minimal(4).Add("CFF ", cff).Add("maxp", SfntBuilder.Maxp(4, cff: true)).Build());
        GlyphOutline composed = face.GetOutline(3);
        Assert.Equal(2, composed.Verbs.Count(v => v == OutlineVerb.MoveTo));
        Assert.Equal(0, composed.XMin);
        Assert.Equal(0, composed.YMin);
        Assert.Equal(100, composed.XMax);
        Assert.Equal(530, composed.YMax);
    }

    [Fact]
    public void Malformed_inputs_throw_the_documented_exceptions()
    {
        Assert.Throws<InvalidFontException>(() => FontFace.Load(Array.Empty<byte>()));
        Assert.Throws<InvalidFontException>(() => FontFace.Load("not a font at all"u8));
        Assert.Throws<InvalidFontException>(() => FontFace.Load(FontAssets.Read(FontAssets.Regular).AsSpan(0, 40)));
        Assert.Throws<InvalidFontException>(() => FontFace.Load(SfntBuilder.Minimal(1).Remove("head").Build()));
        Assert.Throws<InvalidFontException>(() => FontFace.Load(SfntBuilder.Minimal(1).Add("head", SfntBuilder.Head(0, 0)).Build()));

        var type1 = SfntBuilder.Minimal(1);
        type1.Flavor = 0x74797031;
        Assert.Throws<NotSupportedException>(() => FontFace.Load(type1.Build()));
        Assert.Throws<NotSupportedException>(() => FontFace.Load(SfntBuilder.Minimal(1).Add("CFF2", new byte[16]).Build()));
        Assert.Throws<NotSupportedException>(() => FontFace.Load(SfntBuilder.Minimal(1).Build()));
    }

    private static void AssertSameOutlines(FontFace expected, FontFace actual)
    {
        Assert.Equal(expected.GlyphCount, actual.GlyphCount);
        for (int g = 0; g < expected.GlyphCount; g++)
        {
            GlyphOutline a = expected.GetOutline(g);
            GlyphOutline b = actual.GetOutline(g);
            Assert.Equal(a.Verbs, b.Verbs);
            Assert.Equal(a.Points, b.Points);
        }
    }
}

/// <summary>Assembles minimal CFF tables (one name-keyed font) for crafted charstring tests.</summary>
internal static class CffBuilder
{
    public static byte[] Number(int value)
    {
        if (value is >= -107 and <= 107)
        {
            return [(byte)(value + 139)];
        }

        return [28, (byte)(value >> 8), (byte)value];
    }

    /// <summary>Builds the table; <paramref name="charsetSids"/>, when given, is written as a format 0 charset (glyphs 1..n).</summary>
    public static byte[] Build(byte[][] charStrings, byte[][]? localSubrs = null, byte[][]? globalSubrs = null, ushort[]? charsetSids = null)
    {
        byte[] header = [1, 0, 4, 4];
        byte[] names = Index([[(byte)'T']]);
        byte[] strings = Index([]);
        byte[] gsubrs = Index(globalSubrs ?? []);
        byte[] charStringsIndex = Index(charStrings);
        byte[] subrs = localSubrs is null ? [] : Index(localSubrs);
        byte[] charset = charsetSids is null ? [] : [0, .. charsetSids.SelectMany(s => new[] { (byte)(s >> 8), (byte)s })];

        // Private DICT: Subrs offset (relative to the Private DICT) right after it, when there are subrs.
        byte[] privateDict = localSubrs is null ? [] : [.. Int32(6), 19];

        // The Top DICT is written with fixed-width (5-byte) integers so its size is known before the offsets.
        int topDictSize = 5 + 1 + 5 + 5 + 1 + (charsetSids is null ? 0 : 6);
        int topIndexSize = 2 + 1 + 2 + topDictSize;
        int charStringsOffset = header.Length + names.Length + topIndexSize + strings.Length + gsubrs.Length;
        int charsetOffset = charStringsOffset + charStringsIndex.Length;
        int privateOffset = charsetOffset + charset.Length;
        byte[] charsetEntry = charsetSids is null ? [] : [.. Int32(charsetOffset), 15];
        byte[] topDict = [.. Int32(charStringsOffset), 17, .. Int32(privateDict.Length), .. Int32(privateOffset), 18, .. charsetEntry];
        byte[] topIndex = Index([topDict]);
        return [.. header, .. names, .. topIndex, .. strings, .. gsubrs, .. charStringsIndex, .. charset, .. privateDict, .. subrs];
    }

    private static byte[] Int32(int value) => [29, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0)
        {
            return [0, 0];
        }

        var data = new List<byte>();
        var offsets = new List<int> { 1 };
        foreach (byte[] item in items)
        {
            data.AddRange(item);
            offsets.Add(data.Count + 1);
        }

        var output = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 1 };
        if (offsets[^1] > 255)
        {
            throw new InvalidOperationException("Test INDEX too large for one-byte offsets.");
        }

        foreach (int offset in offsets)
        {
            output.Add((byte)offset);
        }

        output.AddRange(data);
        return output.ToArray();
    }
}
