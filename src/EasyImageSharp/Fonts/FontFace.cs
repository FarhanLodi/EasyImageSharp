using System.Collections.Concurrent;
using EasyImageSharp.Fonts.Rendering;
using EasyImageSharp.Fonts.Tables;

namespace EasyImageSharp.Fonts;

/// <summary>Style flags of a font face.</summary>
[Flags]
public enum FontStyle
{
    /// <summary>Upright, regular weight.</summary>
    Regular = 0,

    /// <summary>Bold weight.</summary>
    Bold = 1,

    /// <summary>Italic or oblique.</summary>
    Italic = 2,

    /// <summary>Bold and italic.</summary>
    BoldItalic = Bold | Italic,
}

/// <summary>The kind of glyph outlines a face stores.</summary>
public enum FontOutlineFormat
{
    /// <summary>Quadratic TrueType outlines (<c>glyf</c> table).</summary>
    TrueType,

    /// <summary>Cubic PostScript outlines in the Compact Font Format (<c>CFF </c> table), name-keyed or CID-keyed.</summary>
    Cff,
}

/// <summary>
/// One face of a TrueType or OpenType font, parsed by the library's own managed reader: metrics, the character
/// map, kerning and glyph outlines. Faces are immutable and safe to share between threads; glyph outlines are
/// decoded on first use and cached.
/// </summary>
/// <remarks>
/// <para>
/// Accepted containers are bare TrueType (<c>.ttf</c>) and OpenType (<c>.otf</c>) files, TrueType/OpenType
/// collections (<c>.ttc</c>/<c>.otc</c>), WOFF and WOFF2. Outlines are rendered without hinting. Kerning comes
/// from GPOS pair adjustment or the legacy <c>kern</c> table. There is no complex-script shaping (ligatures,
/// contextual forms, mark positioning) and no bidirectional reordering: text is laid out one code point per
/// glyph, left to right. Scripts that need shaping, such as Arabic or Devanagari, should be rendered with the
/// EasyImageSharp.SkiaSharp add-on, which shapes with HarfBuzz.
/// </para>
/// <para>
/// Loading validates the tables the engine reads and throws <see cref="InvalidFontException"/> for malformed data.
/// A glyph whose outline data turns out to be malformed when it is first drawn renders as an empty glyph rather
/// than failing the drawing operation.
/// </para>
/// </remarks>
public sealed class FontFace
{
    private static int nextId;

    private readonly FontSource source;
    private readonly HmtxTable hmtx;
    private readonly CmapTable cmap;
    private readonly IOutlineSource outlines;
    private readonly Lazy<Kerning> kerning;
    private readonly ConcurrentDictionary<int, GlyphOutline> outlineCache = new();

    private FontFace(FontSource source)
    {
        this.Id = Interlocked.Increment(ref nextId);
        this.source = source;
        HeadTable head = HeadTable.Parse(source.GetRequiredTable(FontReader.Tag("head")));
        HheaTable hhea = HheaTable.Parse(source.GetRequiredTable(FontReader.Tag("hhea")));
        this.GlyphCount = MaxpTable.ParseGlyphCount(source.GetRequiredTable(FontReader.Tag("maxp")));
        this.hmtx = HmtxTable.Parse(source.GetRequiredTable(FontReader.Tag("hmtx")), hhea.NumberOfHMetrics, this.GlyphCount);
        this.cmap = CmapTable.Parse(source.GetTable(FontReader.Tag("cmap")));
        NameTable names = NameTable.Parse(source.GetTable(FontReader.Tag("name")));
        Os2Table? os2 = Os2Table.TryParse(source.GetTable(FontReader.Tag("OS/2")));
        PostTable? post = PostTable.TryParse(source.GetTable(FontReader.Tag("post")));

        if (source.HasTable(FontReader.Tag("glyf")))
        {
            this.outlines = new GlyfOutlines(source, head, this.GlyphCount);
            this.OutlineFormat = FontOutlineFormat.TrueType;
        }
        else if (source.GetRecord(FontReader.Tag("CFF ")) is TableRecord cff)
        {
            this.outlines = new CffOutlines(source.Data, cff.Offset, cff.Length, this.GlyphCount);
            this.OutlineFormat = FontOutlineFormat.Cff;
        }
        else if (source.HasTable(FontReader.Tag("CFF2")))
        {
            throw new NotSupportedException("CFF2 outlines (variable OpenType fonts) are not supported; use a TrueType-flavoured or CFF version 1 font.");
        }
        else
        {
            throw new NotSupportedException("The font has no glyf or CFF outlines (bitmap-only and SVG-only fonts are not supported).");
        }

        this.kerning = new Lazy<Kerning>(() => Kerning.Load(this.source), LazyThreadSafetyMode.ExecutionAndPublication);

        this.UnitsPerEm = head.UnitsPerEm;
        string family = names.Get(NameTable.TypographicFamilyId) ?? names.Get(NameTable.FamilyId) ?? names.Get(NameTable.PostScriptNameId) ?? "Unknown";
        this.FamilyName = family;
        this.SubfamilyName = names.Get(NameTable.TypographicSubfamilyId) ?? names.Get(NameTable.SubfamilyId) ?? "Regular";
        this.FullName = names.Get(NameTable.FullNameId) ?? $"{family} {this.SubfamilyName}";
        this.PostScriptName = names.Get(NameTable.PostScriptNameId) ?? string.Empty;

        (this.Style, this.Weight) = FaceStyle.From(os2, head);
        ushort fsSelection = os2?.FsSelection ?? 0;

        // Line metrics: the typographic values when the font asks for them (USE_TYPO_METRICS), otherwise hhea,
        // which is what macOS and FreeType-based stacks use; Windows metrics are a last resort.
        if (os2 is not null && (fsSelection & 0x80) != 0 && os2.TypoAscender - os2.TypoDescender > 0)
        {
            (this.Ascender, this.Descender, this.LineGap) = (os2.TypoAscender, os2.TypoDescender, os2.TypoLineGap);
        }
        else if (hhea.Ascender - hhea.Descender > 0)
        {
            (this.Ascender, this.Descender, this.LineGap) = (hhea.Ascender, hhea.Descender, hhea.LineGap);
        }
        else if (os2 is not null && os2.TypoAscender - os2.TypoDescender > 0)
        {
            (this.Ascender, this.Descender, this.LineGap) = (os2.TypoAscender, os2.TypoDescender, os2.TypoLineGap);
        }
        else if (os2 is not null && os2.WinAscent + os2.WinDescent > 0)
        {
            (this.Ascender, this.Descender, this.LineGap) = (os2.WinAscent, -os2.WinDescent, 0);
        }
        else
        {
            (this.Ascender, this.Descender, this.LineGap) = (head.YMax, head.YMin, 0);
        }

        this.LineGap = Math.Max(0, this.LineGap);
        this.CapHeight = os2 is { CapHeight: > 0 } ? os2.CapHeight : (int)Math.Round(this.Ascender * 0.7);
        this.XHeight = os2 is { XHeight: > 0 } ? os2.XHeight : (int)Math.Round(this.Ascender * 0.5);
        this.ItalicAngle = post?.ItalicAngle ?? 0f;
        this.UnderlinePosition = post?.UnderlinePosition ?? -(this.UnitsPerEm / 10);
        this.UnderlineThickness = post is { UnderlineThickness: > 0 } ? post.UnderlineThickness : Math.Max(1, this.UnitsPerEm / 20);
        this.IsMonospaced = post?.IsFixedPitch ?? false;
    }

    /// <summary>The family name, preferring the typographic family (name id 16) over the legacy family (id 1).</summary>
    public string FamilyName { get; }

    /// <summary>The style name within the family, such as <c>Regular</c> or <c>Bold Italic</c>.</summary>
    public string SubfamilyName { get; }

    /// <summary>The full name, such as <c>DejaVu Sans Bold</c>.</summary>
    public string FullName { get; }

    /// <summary>The PostScript name, or an empty string when the font has none.</summary>
    public string PostScriptName { get; }

    /// <summary>Bold and italic flags from the <c>OS/2</c> and <c>head</c> tables.</summary>
    public FontStyle Style { get; }

    /// <summary>The weight class, 1-1000 (400 regular, 700 bold).</summary>
    public int Weight { get; }

    /// <summary>Whether the <c>post</c> table marks the face as monospaced.</summary>
    public bool IsMonospaced { get; }

    /// <summary>The kind of outlines the face stores.</summary>
    public FontOutlineFormat OutlineFormat { get; }

    /// <summary>Font design units per em; every metric in font units is relative to this.</summary>
    public int UnitsPerEm { get; }

    /// <summary>The typographic ascent in font units, positive above the baseline.</summary>
    public int Ascender { get; }

    /// <summary>The typographic descent in font units, negative below the baseline.</summary>
    public int Descender { get; }

    /// <summary>The recommended extra space between lines in font units (never negative).</summary>
    public int LineGap { get; }

    /// <summary>The height of flat capital letters in font units (estimated from the ascent when the font does not say).</summary>
    public int CapHeight { get; }

    /// <summary>The height of flat lowercase letters in font units (estimated from the ascent when the font does not say).</summary>
    public int XHeight { get; }

    /// <summary>The italic angle in degrees counter-clockwise from vertical (negative for forward-leaning faces).</summary>
    public float ItalicAngle { get; }

    /// <summary>The suggested underline position in font units (negative below the baseline).</summary>
    public int UnderlinePosition { get; }

    /// <summary>The suggested underline thickness in font units.</summary>
    public int UnderlineThickness { get; }

    /// <summary>The number of glyphs in the face.</summary>
    public int GlyphCount { get; }

    /// <summary>Whether the face has any pair kerning (GPOS pair adjustment or a <c>kern</c> table).</summary>
    public bool HasKerning => !this.kerning.Value.IsEmpty;

    /// <summary>A process-unique identity used to key glyph caches.</summary>
    internal int Id { get; }

    /// <summary>Loads a font file; for a collection, its first face.</summary>
    /// <param name="path">A TrueType, OpenType, collection, WOFF or WOFF2 file.</param>
    /// <exception cref="InvalidFontException">The file is not a font this library reads, or it is malformed.</exception>
    /// <exception cref="NotSupportedException">The font uses outlines this library does not render (CFF2, bitmap-only).</exception>
    public static FontFace Load(string path) => Load(path, 0);

    /// <summary>Loads the face at <paramref name="faceIndex"/> of a font file.</summary>
    /// <param name="path">A TrueType, OpenType, collection, WOFF or WOFF2 file.</param>
    /// <param name="faceIndex">The face within a collection; 0 for single-face files.</param>
    /// <exception cref="InvalidFontException">The file is not a font this library reads, or it is malformed.</exception>
    /// <exception cref="NotSupportedException">The font uses outlines this library does not render (CFF2, bitmap-only).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="faceIndex"/> is not a face of the file.</exception>
    public static FontFace Load(string path, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Select(FontContainer.Open(FontData.ReadFile(path)), faceIndex);
    }

    /// <summary>Loads a font read from <paramref name="stream"/> (read to its end); for a collection, its first face.</summary>
    /// <inheritdoc cref="Load(string)" path="/exception"/>
    public static FontFace Load(Stream stream) => Load(stream, 0);

    /// <summary>Loads the face at <paramref name="faceIndex"/> of a font read from <paramref name="stream"/> (read to its end).</summary>
    /// <inheritdoc cref="Load(string, int)" path="/exception"/>
    public static FontFace Load(Stream stream, int faceIndex)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Select(FontContainer.Open(FontData.ReadStream(stream)), faceIndex);
    }

    /// <summary>Loads font data (the bytes are copied); for a collection, its first face.</summary>
    /// <inheritdoc cref="Load(string)" path="/exception"/>
    public static FontFace Load(ReadOnlySpan<byte> data) => Load(data, 0);

    /// <summary>Loads the face at <paramref name="faceIndex"/> of font data; the bytes are copied.</summary>
    /// <inheritdoc cref="Load(string, int)" path="/exception"/>
    public static FontFace Load(ReadOnlySpan<byte> data, int faceIndex)
        => Select(FontContainer.Open(data.ToArray()), faceIndex);

    /// <summary>Loads every face of a font file (one for a plain font, several for a collection).</summary>
    /// <inheritdoc cref="Load(string)" path="/exception"/>
    public static IReadOnlyList<FontFace> LoadAll(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Create(FontContainer.Open(FontData.ReadFile(path)));
    }

    /// <summary>Loads every face of a font read from <paramref name="stream"/>.</summary>
    /// <inheritdoc cref="Load(string)" path="/exception"/>
    public static IReadOnlyList<FontFace> LoadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Create(FontContainer.Open(FontData.ReadStream(stream)));
    }

    /// <summary>Loads every face of font data; the bytes are copied.</summary>
    /// <inheritdoc cref="Load(string)" path="/exception"/>
    public static IReadOnlyList<FontFace> LoadAll(ReadOnlySpan<byte> data)
        => Create(FontContainer.Open(data.ToArray()));

    /// <summary>Returns the glyph that maps <paramref name="codePoint"/>, or 0 (the missing-glyph box) when the face has none.</summary>
    public int GetGlyphIndex(int codePoint) => this.cmap.GetGlyph(codePoint, this.GlyphCount);

    /// <summary>Whether the face maps <paramref name="codePoint"/> to a glyph.</summary>
    public bool HasGlyph(int codePoint) => this.GetGlyphIndex(codePoint) != 0;

    /// <summary>The advance width of a glyph in font units (0 for an index outside the face).</summary>
    public int GetAdvanceWidth(int glyphIndex) => this.hmtx.GetAdvance(glyphIndex);

    /// <summary>
    /// The kerning between two glyphs in font units: the change to the first glyph's advance when the second
    /// follows it (usually negative). Placement-only adjustments are not included.
    /// </summary>
    public int GetKerning(int leftGlyph, int rightGlyph) => this.kerning.Value.Get(leftGlyph, rightGlyph).FirstAdvance;

    /// <inheritdoc/>
    public override string ToString() => this.FullName;

    internal PairAdjustment GetPairAdjustment(int leftGlyph, int rightGlyph) => this.kerning.Value.Get(leftGlyph, rightGlyph);

    internal bool KerningIsEmpty => this.kerning.Value.IsEmpty;

    /// <summary>Returns the glyph outline, decoding it on first use. Malformed glyph data yields an empty outline.</summary>
    internal GlyphOutline GetOutline(int glyph)
    {
        if ((uint)glyph >= (uint)this.GlyphCount)
        {
            return GlyphOutline.Empty;
        }

        return this.outlineCache.GetOrAdd(glyph, static (g, self) => self.DecodeOutline(g), this);
    }

    internal static IReadOnlyList<FontFace> Create(FontSource[] sources)
    {
        var faces = new FontFace[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            faces[i] = CreateOne(sources[i]);
        }

        return faces;
    }

    private static FontFace Select(FontSource[] sources, int faceIndex)
    {
        if ((uint)faceIndex >= (uint)sources.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(faceIndex), faceIndex, $"The font has {sources.Length} face(s).");
        }

        return CreateOne(sources[faceIndex]);
    }

    private static FontFace CreateOne(FontSource source)
    {
        try
        {
            return new FontFace(source);
        }
        catch (Exception ex) when (FontContainer.IsMalformedSymptom(ex))
        {
            throw new InvalidFontException($"Malformed font: {ex.Message}", ex);
        }
    }

    private GlyphOutline DecodeOutline(int glyph)
    {
        try
        {
            return this.outlines.GetOutline(glyph);
        }
        catch (Exception ex) when (ex is InvalidFontException || FontContainer.IsMalformedSymptom(ex))
        {
            return GlyphOutline.Empty;
        }
    }
}
