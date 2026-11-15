using System.Text;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// The naming strings of a face from the <c>name</c> table: family, subfamily, full and PostScript names, and the
/// typographic family/subfamily that group more than four styles. English (United States) Windows strings are
/// preferred, then any Windows or Unicode string, then Macintosh Roman.
/// </summary>
internal sealed class NameTable
{
    public const int FamilyId = 1;
    public const int SubfamilyId = 2;
    public const int FullNameId = 4;
    public const int PostScriptNameId = 6;
    public const int TypographicFamilyId = 16;
    public const int TypographicSubfamilyId = 17;

    private const string Label = "name table";

    private readonly Dictionary<int, string> names;

    private NameTable(Dictionary<int, string> names) => this.names = names;

    public static NameTable Empty { get; } = new(new Dictionary<int, string>());

    public string? Get(int nameId) => this.names.TryGetValue(nameId, out string? value) ? value : null;

    public static NameTable Parse(ReadOnlySpan<byte> table)
    {
        if (table.IsEmpty)
        {
            return Empty;
        }

        var r = new FontReader(table, Label);
        r.ReadUInt16();
        int count = r.ReadUInt16();
        int storageOffset = r.ReadUInt16();

        // Lower rank wins: 0 = Windows English (US), 1 = other Windows/Unicode, 2 = Macintosh Roman.
        var best = new Dictionary<int, (int Rank, string Value)>();
        for (int i = 0; i < count; i++)
        {
            int platform = r.ReadUInt16();
            int encoding = r.ReadUInt16();
            int language = r.ReadUInt16();
            int nameId = r.ReadUInt16();
            int length = r.ReadUInt16();
            int offset = r.ReadUInt16();
            if (nameId is not (FamilyId or SubfamilyId or FullNameId or PostScriptNameId or TypographicFamilyId or TypographicSubfamilyId))
            {
                continue;
            }

            int rank;
            bool utf16;
            if (platform == 3 && encoding is 0 or 1 or 10)
            {
                rank = language == 0x0409 ? 0 : 1;
                utf16 = true;
            }
            else if (platform == 0)
            {
                rank = 1;
                utf16 = true;
            }
            else if (platform == 1 && encoding == 0)
            {
                rank = 2;
                utf16 = false;
            }
            else
            {
                continue;
            }

            if (best.TryGetValue(nameId, out var existing) && existing.Rank <= rank)
            {
                continue;
            }

            // A record whose string lies outside the table is skipped rather than failing the whole font: the
            // names are informational and a damaged one should not make the outlines unusable.
            long start = (long)storageOffset + offset;
            if (start + length > table.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = table.Slice((int)start, length);
            string value = utf16 ? DecodeUtf16BigEndian(bytes) : DecodeMacRoman(bytes);
            value = value.Trim('\0', ' ');
            if (value.Length > 0)
            {
                best[nameId] = (rank, value);
            }
        }

        var names = new Dictionary<int, string>(best.Count);
        foreach ((int id, (int _, string value)) in best)
        {
            names[id] = value;
        }

        return new NameTable(names);
    }

    private static string DecodeUtf16BigEndian(ReadOnlySpan<byte> bytes)
        => Encoding.BigEndianUnicode.GetString(bytes[..(bytes.Length & ~1)]);

    /// <summary>Decodes the ASCII half of Macintosh Roman; other bytes become <c>?</c> (Macintosh names are only a last resort).</summary>
    private static string DecodeMacRoman(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            builder.Append(b < 0x80 ? (char)b : '?');
        }

        return builder.ToString();
    }
}
