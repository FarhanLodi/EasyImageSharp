namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// WOFF 1.0 (W3C Recommendation, 2012): an sfnt whose tables are individually zlib-compressed. Decompression
/// uses the library's own managed inflater, so no native code is involved.
/// </summary>
internal static class Woff
{
    private const string Label = "WOFF font";
    private const int HeaderSize = 44;

    public static FontSource Decode(ReadOnlySpan<byte> data)
    {
        var r = new FontReader(data, Label);
        r.ReadTag();
        uint flavor = r.ReadTag();
        r.ReadUInt32();
        int numTables = r.ReadUInt16();
        r.ReadUInt16();
        if (numTables == 0 || numTables > 1024)
        {
            throw FontReader.Fail(Label, $"table count {numTables} is outside 1-1024");
        }

        if (flavor != FontContainer.TrueTypeFlavor && flavor != FontContainer.AppleTrueTypeFlavor && flavor != FontContainer.CffFlavor)
        {
            throw FontReader.Fail(Label, $"flavor 0x{flavor:X8} is not a TrueType or OpenType signature");
        }

        r.Position = HeaderSize;
        var entries = new (uint Tag, int Offset, int CompLength, int OrigLength)[numTables];
        long total = 0;
        for (int i = 0; i < numTables; i++)
        {
            uint tag = r.ReadTag();
            int offset = r.ReadOffset32();
            int compLength = r.ReadOffset32();
            int origLength = r.ReadOffset32();
            r.ReadUInt32();
            if ((long)offset + compLength > data.Length)
            {
                throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' runs past the end of the file");
            }

            if (compLength > origLength)
            {
                throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' is longer compressed than uncompressed");
            }

            entries[i] = (tag, offset, compLength, origLength);
            total += (origLength + 3L) & ~3L;
            if (total > FontContainer.MaxFontBytes)
            {
                throw FontReader.Fail(Label, $"decompressed tables exceed the {FontContainer.MaxFontBytes} byte limit");
            }
        }

        // Decompress first and assemble afterwards, so the buffer is sized by data that actually decompressed
        // rather than by lengths a malformed header merely declares.
        var parts = new byte[numTables][];
        for (int i = 0; i < entries.Length; i++)
        {
            (uint tag, int offset, int compLength, int origLength) = entries[i];
            ReadOnlySpan<byte> source = data.Slice(offset, compLength);
            if (compLength == origLength)
            {
                parts[i] = source.ToArray();
                continue;
            }

            byte[] inflated;
            try
            {
                inflated = Inflate.Decompress(source, origLength, "WOFF table");
            }
            catch (ImageFormatException ex)
            {
                throw new InvalidFontException($"Malformed {Label}: table '{FontReader.TagToString(tag)}' does not decompress: {ex.Message}", ex);
            }

            if (inflated.Length != origLength)
            {
                throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' decompressed to {inflated.Length} bytes instead of {origLength}");
            }

            parts[i] = inflated;
        }

        byte[] output = new byte[total];
        var tables = new Dictionary<uint, TableRecord>(numTables);
        int position = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] part = parts[i];
            part.CopyTo(output, position);
            tables.TryAdd(entries[i].Tag, new TableRecord(position, part.Length));
            position += (part.Length + 3) & ~3;
        }

        return new FontSource(output, tables, flavor);
    }
}
