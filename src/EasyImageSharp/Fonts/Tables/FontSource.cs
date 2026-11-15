namespace EasyImageSharp.Fonts.Tables;

/// <summary>The location of one table inside <see cref="FontSource.Data"/>.</summary>
internal readonly record struct TableRecord(int Offset, int Length);

/// <summary>
/// The tables of one font face, ready for parsing: a buffer plus the location of every table in it. For a
/// plain sfnt file the buffer is the file itself; for WOFF and WOFF2 it is the decompressed table data,
/// laid out back to back (the sfnt header is never rebuilt because nothing reads it).
/// </summary>
internal sealed class FontSource
{
    private readonly Dictionary<uint, TableRecord> tables;

    public FontSource(byte[] data, Dictionary<uint, TableRecord> tables, uint flavor)
    {
        this.Data = data;
        this.tables = tables;
        this.Flavor = flavor;
    }

    /// <summary>The buffer every <see cref="TableRecord"/> points into.</summary>
    public byte[] Data { get; }

    /// <summary>The sfnt version tag: <c>0x00010000</c> or <c>true</c> for TrueType outlines, <c>OTTO</c> for CFF.</summary>
    public uint Flavor { get; }

    /// <summary>
    /// When set, loca is in the long format whatever <c>head.indexToLocFormat</c> says (WOFF2 reconstructs loca in
    /// the long format).
    /// </summary>
    public bool LongLocaOverride { get; init; }

    public bool HasTable(uint tag) => this.tables.ContainsKey(tag);

    /// <summary>Returns the bytes of a table, or an empty span when the font does not have it.</summary>
    public ReadOnlySpan<byte> GetTable(uint tag)
        => this.tables.TryGetValue(tag, out TableRecord record) ? this.Data.AsSpan(record.Offset, record.Length) : default;

    /// <summary>Returns the bytes of a table the font must have.</summary>
    public ReadOnlySpan<byte> GetRequiredTable(uint tag)
    {
        if (!this.tables.TryGetValue(tag, out TableRecord record))
        {
            throw new InvalidFontException($"Malformed font: the required '{FontReader.TagToString(tag)}' table is missing.");
        }

        return this.Data.AsSpan(record.Offset, record.Length);
    }

    /// <summary>Returns the location of a table, or <see langword="null"/> when the font does not have it.</summary>
    public TableRecord? GetRecord(uint tag) => this.tables.TryGetValue(tag, out TableRecord record) ? record : null;
}
