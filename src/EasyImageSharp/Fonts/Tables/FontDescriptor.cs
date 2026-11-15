namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// The family, style and weight of one face in a font file, read with a handful of small seeks (table
/// directories plus the <c>name</c>, <c>OS/2</c> and <c>head</c> tables) so that indexing the system fonts does
/// not read every font file in full.
/// </summary>
internal sealed record FontDescriptor(int FaceIndex, string FamilyName, FontStyle Style, int Weight)
{
    /// <summary>Largest naming/style table read while indexing; real ones are a few kilobytes.</summary>
    private const int MaxIndexedTable = 1 << 20;

    /// <summary>Reads the descriptors of every face in a file, or returns an empty list if it is not a readable font.</summary>
    public static IReadOnlyList<FontDescriptor> TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.RandomAccess);
            byte[] header = ReadAt(stream, 0, (int)Math.Min(stream.Length, 12));
            if (!FontContainer.IsRecognized(header))
            {
                return [];
            }

            uint tag = FontReader.UInt32At(header, 0, "font header");
            if (tag == FontContainer.WoffTag || tag == FontContainer.Woff2Tag)
            {
                // Compressed web fonts must be decoded to be read at all; they are rare among installed fonts.
                IReadOnlyList<FontFace> faces = FontFace.LoadAll(path);
                return faces.Select((f, i) => new FontDescriptor(i, f.FamilyName, f.Style, f.Weight)).ToArray();
            }

            int[] faceOffsets = [0];
            if (tag == FontContainer.CollectionTag)
            {
                int count = (int)FontReader.UInt32At(ReadAt(stream, 8, 4), 0, "font collection header");
                faceOffsets = FontContainer.ReadCollectionOffsets(ReadAt(stream, 0, 12 + (Math.Clamp(count, 0, FontContainer.MaxFaces) * 4)));
            }

            var result = new List<FontDescriptor>(faceOffsets.Length);
            for (int i = 0; i < faceOffsets.Length; i++)
            {
                FontDescriptor? descriptor = ReadFace(stream, faceOffsets[i], i);
                if (descriptor is not null)
                {
                    result.Add(descriptor);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidFontException or NotSupportedException
            || FontContainer.IsMalformedSymptom(ex))
        {
            return [];
        }
    }

    private static FontDescriptor? ReadFace(FileStream stream, int offset, int faceIndex)
    {
        byte[] directoryHeader = ReadAt(stream, offset, 12);
        int numTables = FontReader.UInt16At(directoryHeader, 4, "font table directory");
        byte[] directory = ReadAt(stream, offset, 12 + (numTables * 16));

        // Table offsets in the directory are absolute, so the directory is parsed as if it sat at the file start;
        // a record's bounds are checked against the real file length below.
        var tables = new Dictionary<uint, (long Offset, int Length)>();
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (i * 16);
            uint tag = FontReader.UInt32At(directory, record, "font table directory");
            uint tableOffset = FontReader.UInt32At(directory, record + 8, "font table directory");
            uint length = FontReader.UInt32At(directory, record + 12, "font table directory");
            tables.TryAdd(tag, (tableOffset, (int)Math.Min(length, MaxIndexedTable)));
        }

        uint flavor = FontReader.UInt32At(directory, 0, "font table directory");
        bool hasOutlines = tables.ContainsKey(FontReader.Tag("glyf")) || tables.ContainsKey(FontReader.Tag("CFF "));
        if (!hasOutlines || flavor == FontReader.Tag("typ1"))
        {
            return null;
        }

        NameTable names = NameTable.Parse(ReadTable(stream, tables, "name"));
        Os2Table? os2 = Os2Table.TryParse(ReadTable(stream, tables, "OS/2"));
        byte[] headBytes = ReadTable(stream, tables, "head");
        HeadTable head = HeadTable.Parse(headBytes);

        string family = names.Get(NameTable.TypographicFamilyId) ?? names.Get(NameTable.FamilyId) ?? names.Get(NameTable.PostScriptNameId) ?? string.Empty;
        if (family.Length == 0)
        {
            return null;
        }

        (FontStyle style, int weight) = FaceStyle.From(os2, head);
        return new FontDescriptor(faceIndex, family, style, weight);
    }

    private static byte[] ReadTable(FileStream stream, Dictionary<uint, (long Offset, int Length)> tables, string tag)
    {
        if (!tables.TryGetValue(FontReader.Tag(tag), out var record))
        {
            return [];
        }

        long available = stream.Length - record.Offset;
        if (available <= 0)
        {
            return [];
        }

        return ReadAt(stream, record.Offset, (int)Math.Min(record.Length, available));
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length)
        {
            throw new InvalidFontException("Malformed font: a table lies outside the file.");
        }

        byte[] buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
