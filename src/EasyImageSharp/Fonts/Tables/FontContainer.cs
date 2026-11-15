namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// Opens the container formats a font can arrive in - a bare sfnt (TrueType or OpenType), a TrueType/OpenType
/// collection, WOFF and WOFF2 - and splits them into one <see cref="FontSource"/> per face.
/// </summary>
internal static class FontContainer
{
    /// <summary>Largest font file, or decompressed WOFF/WOFF2 table data, accepted.</summary>
    public const int MaxFontBytes = 256 * 1024 * 1024;

    /// <summary>Largest number of faces accepted from one collection.</summary>
    public const int MaxFaces = 1024;

    /// <summary>Largest number of tables accepted in one table directory.</summary>
    private const int MaxTables = 1024;

    public const uint TrueTypeFlavor = 0x00010000;
    public static readonly uint AppleTrueTypeFlavor = FontReader.Tag("true");
    public static readonly uint CffFlavor = FontReader.Tag("OTTO");
    public static readonly uint CollectionTag = FontReader.Tag("ttcf");
    public static readonly uint WoffTag = FontReader.Tag("wOFF");
    public static readonly uint Woff2Tag = FontReader.Tag("wOF2");
    private static readonly uint Type1Flavor = FontReader.Tag("typ1");

    /// <summary>Whether the first bytes look like any font container this library reads.</summary>
    public static bool IsRecognized(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        uint tag = FontReader.UInt32At(header, 0, "font header");
        return tag == TrueTypeFlavor || tag == AppleTrueTypeFlavor || tag == CffFlavor || tag == CollectionTag
            || tag == WoffTag || tag == Woff2Tag || tag == Type1Flavor;
    }

    /// <summary>Opens every face in <paramref name="data"/>.</summary>
    /// <exception cref="InvalidFontException">The data is not a font, or the container is malformed.</exception>
    /// <exception cref="NotSupportedException">The data is a font format this library does not read.</exception>
    public static FontSource[] Open(byte[] data)
    {
        try
        {
            if (data.Length < 12)
            {
                throw new InvalidFontException("Malformed font: the data is too short to hold a font header.");
            }

            if (data.Length > MaxFontBytes)
            {
                throw new InvalidFontException($"Malformed font: the data exceeds the {MaxFontBytes} byte limit.");
            }

            uint tag = FontReader.UInt32At(data, 0, "font header");
            if (tag == CollectionTag)
            {
                int[] offsets = ReadCollectionOffsets(data);
                var faces = new FontSource[offsets.Length];
                for (int i = 0; i < offsets.Length; i++)
                {
                    Dictionary<uint, TableRecord> tables = ReadTableDirectory(data, offsets[i], out uint flavor);
                    faces[i] = new FontSource(data, tables, flavor);
                }

                return faces;
            }

            if (tag == WoffTag)
            {
                return [Woff.Decode(data)];
            }

            if (tag == Woff2Tag)
            {
                return Woff2.Decode(data);
            }

            Dictionary<uint, TableRecord> single = ReadTableDirectory(data, 0, out uint singleFlavor);
            return [new FontSource(data, single, singleFlavor)];
        }
        catch (Exception ex) when (IsMalformedSymptom(ex))
        {
            throw new InvalidFontException($"Malformed font: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the face offsets of a <c>ttcf</c> collection header.</summary>
    public static int[] ReadCollectionOffsets(ReadOnlySpan<byte> data)
    {
        var r = new FontReader(data, "font collection header");
        r.ReadTag();
        ushort major = r.ReadUInt16();
        r.ReadUInt16();
        if (major is not (1 or 2))
        {
            throw FontReader.Fail("font collection header", $"version {major} is not 1 or 2");
        }

        uint count = r.ReadUInt32();
        if (count == 0 || count > MaxFaces)
        {
            throw FontReader.Fail("font collection header", $"face count {count} is outside 1-{MaxFaces}");
        }

        int[] offsets = new int[count];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = r.ReadOffset32();
        }

        return offsets;
    }

    /// <summary>
    /// Parses the table directory of one sfnt face at <paramref name="offset"/>. Table offsets are relative
    /// to the start of <paramref name="data"/> (the start of the file, also for collections).
    /// </summary>
    public static Dictionary<uint, TableRecord> ReadTableDirectory(ReadOnlySpan<byte> data, int offset, out uint flavor)
    {
        const string Label = "font table directory";
        var r = new FontReader(data, Label);
        r.Position = offset;
        flavor = r.ReadTag();
        if (flavor == Type1Flavor)
        {
            throw new NotSupportedException("PostScript Type 1 fonts wrapped in sfnt ('typ1') are not supported; use a TrueType or OpenType font.");
        }

        if (flavor != TrueTypeFlavor && flavor != AppleTrueTypeFlavor && flavor != CffFlavor)
        {
            throw FontReader.Fail(Label, $"sfnt version 0x{flavor:X8} is not a TrueType or OpenType signature");
        }

        int numTables = r.ReadUInt16();
        if (numTables == 0 || numTables > MaxTables)
        {
            throw FontReader.Fail(Label, $"table count {numTables} is outside 1-{MaxTables}");
        }

        r.Skip(6);
        var tables = new Dictionary<uint, TableRecord>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            uint tag = r.ReadTag();
            r.ReadUInt32();
            uint tableOffset = r.ReadUInt32();
            uint length = r.ReadUInt32();
            if (tableOffset > (uint)data.Length)
            {
                throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' starts at {tableOffset}, past the end of the {data.Length}-byte font");
            }

            // A table that runs past the end of the file (usually only its padding) is clamped; a parser that
            // needs the missing bytes reports the truncation itself.
            long available = data.Length - (long)tableOffset;
            int clamped = (int)Math.Min(length, available);
            tables.TryAdd(tag, new TableRecord((int)tableOffset, clamped));
        }

        return tables;
    }

    /// <summary>Exceptions that, raised while parsing font data, can only mean the data is malformed.</summary>
    public static bool IsMalformedSymptom(Exception ex)
        => ex is IndexOutOfRangeException
            or ArgumentException
            or ArithmeticException
            or InvalidDataException
            or IOException
            or ImageFormatException;
}
