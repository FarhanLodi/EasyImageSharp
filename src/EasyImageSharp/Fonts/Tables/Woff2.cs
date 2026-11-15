using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// WOFF 2.0 (W3C Recommendation, 2024): a Brotli-compressed table stream with the optional glyf/loca and
/// hmtx transforms, for single fonts and collections. The transformed tables are reconstructed into
/// ordinary TrueType tables; the rebuilt loca is always in the long format (see
/// <see cref="FontSource.LongLocaOverride"/>), because re-encoded glyphs need not fit a short loca.
/// </summary>
internal static class Woff2
{
    private const string Label = "WOFF2 font";

    private static readonly uint GlyfTag = FontReader.Tag("glyf");
    private static readonly uint LocaTag = FontReader.Tag("loca");
    private static readonly uint HmtxTag = FontReader.Tag("hmtx");
    private static readonly uint HheaTag = FontReader.Tag("hhea");
    private static readonly uint MaxpTag = FontReader.Tag("maxp");

    /// <summary>The 63 tags a table directory entry can name by index (WOFF2 section 5.1).</summary>
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ",
        "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS",
        "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc",
        "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
        "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    public static FontSource[] Decode(byte[] data)
    {
        var r = new FontReader(data, Label);
        r.ReadTag();
        uint flavor = r.ReadTag();
        r.ReadUInt32();
        int numTables = r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt32();
        int totalCompressedSize = r.ReadOffset32();
        r.Position = 48;
        if (numTables == 0 || numTables > 1024)
        {
            throw FontReader.Fail(Label, $"table count {numTables} is outside 1-1024");
        }

        var entries = new Entry[numTables];
        long streamTotal = 0;
        for (int i = 0; i < numTables; i++)
        {
            byte flags = r.ReadUInt8();
            int tagIndex = flags & 0x3F;
            uint tag = tagIndex == 63 ? r.ReadTag() : FontReader.Tag(KnownTags[tagIndex]);
            int transformVersion = flags >> 6;
            uint origLength = ReadUIntBase128(ref r);
            bool transformed = tag == GlyfTag || tag == LocaTag ? transformVersion == 0 : transformVersion != 0;
            uint streamLength = origLength;
            if (transformed)
            {
                if (tag != GlyfTag && tag != LocaTag && !(tag == HmtxTag && transformVersion == 1))
                {
                    throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' uses unknown transform {transformVersion}");
                }

                streamLength = ReadUIntBase128(ref r);
            }
            else if ((tag == GlyfTag || tag == LocaTag) && transformVersion != 3)
            {
                throw FontReader.Fail(Label, $"table '{FontReader.TagToString(tag)}' uses unknown transform {transformVersion}");
            }

            if (origLength > FontContainer.MaxFontBytes || streamLength > FontContainer.MaxFontBytes)
            {
                throw FontReader.Fail(Label, "a table exceeds the size limit");
            }

            entries[i] = new Entry(tag, (int)origLength, (int)streamLength, transformed, (int)streamTotal);
            streamTotal += streamLength;
            if (streamTotal > FontContainer.MaxFontBytes)
            {
                throw FontReader.Fail(Label, $"decompressed tables exceed the {FontContainer.MaxFontBytes} byte limit");
            }
        }

        // Collections list, per font, which of the shared tables belong to it.
        int[][] fontTables;
        uint[] fontFlavors;
        if (flavor == FontContainer.CollectionTag)
        {
            r.ReadUInt32();
            int numFonts = Read255UInt16(ref r);
            if (numFonts == 0 || numFonts > FontContainer.MaxFaces)
            {
                throw FontReader.Fail(Label, $"collection font count {numFonts} is outside 1-{FontContainer.MaxFaces}");
            }

            fontTables = new int[numFonts][];
            fontFlavors = new uint[numFonts];
            for (int f = 0; f < numFonts; f++)
            {
                int count = Read255UInt16(ref r);
                fontFlavors[f] = r.ReadTag();
                fontTables[f] = new int[count];
                for (int t = 0; t < count; t++)
                {
                    int index = Read255UInt16(ref r);
                    if (index >= numTables)
                    {
                        throw FontReader.Fail(Label, $"collection table index {index} is out of range");
                    }

                    fontTables[f][t] = index;
                }
            }
        }
        else
        {
            fontTables = [Enumerable.Range(0, numTables).ToArray()];
            fontFlavors = [flavor];
        }

        ReadOnlySpan<byte> compressed = r.ReadBytes(totalCompressedSize);
        byte[] stream = DecompressStream(compressed, (int)streamTotal);

        // Reconstructed tables are appended after the decompressed stream so every table lives in one buffer.
        var output = new List<byte[]>();
        long appended = stream.Length;
        var rebuilt = new Dictionary<int, (TableRecord Glyf, TableRecord Loca, short[] XMins)>();
        var rebuiltHmtx = new Dictionary<int, TableRecord>();
        var pending = new List<(Dictionary<uint, TableRecord> Tables, uint Flavor)>();
        bool[] rebuiltFaces = new bool[fontTables.Length];

        for (int f = 0; f < fontTables.Length; f++)
        {
            var tables = new Dictionary<uint, TableRecord>();
            int glyfIndex = -1;
            int locaIndex = -1;
            int hmtxIndex = -1;
            foreach (int index in fontTables[f])
            {
                Entry e = entries[index];
                if (e.Tag == GlyfTag)
                {
                    glyfIndex = index;
                }
                else if (e.Tag == LocaTag)
                {
                    locaIndex = index;
                }
                else if (e.Tag == HmtxTag)
                {
                    hmtxIndex = index;
                }

                if (!e.Transformed)
                {
                    tables.TryAdd(e.Tag, new TableRecord(e.StreamOffset, e.StreamLength));
                }
            }

            short[]? xMins = null;
            if (glyfIndex >= 0 && entries[glyfIndex].Transformed)
            {
                if (locaIndex < 0 || !entries[locaIndex].Transformed)
                {
                    throw FontReader.Fail(Label, "a transformed glyf table needs a transformed loca table");
                }

                if (!rebuilt.TryGetValue(glyfIndex, out var glyfLoca))
                {
                    Entry g = entries[glyfIndex];
                    (byte[] glyf, byte[] loca, short[] mins) = ReconstructGlyf(stream.AsSpan(g.StreamOffset, g.StreamLength));
                    var glyfRecord = new TableRecord(checked((int)appended), glyf.Length);
                    output.Add(glyf);
                    appended += glyf.Length;
                    var locaRecord = new TableRecord(checked((int)appended), loca.Length);
                    output.Add(loca);
                    appended += loca.Length;
                    if (appended > FontContainer.MaxFontBytes)
                    {
                        throw FontReader.Fail(Label, "reconstructed tables exceed the size limit");
                    }

                    glyfLoca = (glyfRecord, locaRecord, mins);
                    rebuilt[glyfIndex] = glyfLoca;
                }

                tables[GlyfTag] = glyfLoca.Glyf;
                tables[LocaTag] = glyfLoca.Loca;
                xMins = glyfLoca.XMins;
                rebuiltFaces[f] = true;
            }
            else if (locaIndex >= 0 && entries[locaIndex].Transformed)
            {
                throw FontReader.Fail(Label, "a transformed loca table needs a transformed glyf table");
            }

            if (hmtxIndex >= 0 && entries[hmtxIndex].Transformed)
            {
                if (xMins is null)
                {
                    throw FontReader.Fail(Label, "the hmtx transform needs a transformed glyf table");
                }

                if (!rebuiltHmtx.TryGetValue(hmtxIndex, out TableRecord hmtxRecord))
                {
                    Entry h = entries[hmtxIndex];
                    int numHMetrics = FontReader.UInt16At(GetTable(stream, tables, HheaTag), 34, "hhea table");
                    int numGlyphs = FontReader.UInt16At(GetTable(stream, tables, MaxpTag), 4, "maxp table");
                    byte[] hmtx = ReconstructHmtx(stream.AsSpan(h.StreamOffset, h.StreamLength), numHMetrics, numGlyphs, xMins);
                    hmtxRecord = new TableRecord(checked((int)appended), hmtx.Length);
                    output.Add(hmtx);
                    appended += hmtx.Length;
                    rebuiltHmtx[hmtxIndex] = hmtxRecord;
                }

                tables[HmtxTag] = hmtxRecord;
            }

            pending.Add((tables, fontFlavors[f]));
        }

        byte[] buffer = stream;
        if (output.Count > 0)
        {
            buffer = new byte[appended];
            stream.CopyTo(buffer, 0);
            int position = stream.Length;
            foreach (byte[] part in output)
            {
                part.CopyTo(buffer, position);
                position += part.Length;
            }
        }

        var faces = new FontSource[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            (Dictionary<uint, TableRecord> tables, uint fontFlavor) = pending[i];
            if (fontFlavor != FontContainer.TrueTypeFlavor && fontFlavor != FontContainer.AppleTrueTypeFlavor && fontFlavor != FontContainer.CffFlavor)
            {
                throw FontReader.Fail(Label, $"flavor 0x{fontFlavor:X8} is not a TrueType or OpenType signature");
            }

            faces[i] = new FontSource(buffer, tables, fontFlavor) { LongLocaOverride = rebuiltFaces[i] };
        }

        return faces;
    }

    /// <summary>
    /// Decompresses the Brotli table stream into a buffer that grows as output actually arrives, so a header that
    /// declares a huge table size cannot make the decoder allocate it up front.
    /// </summary>
    private static byte[] DecompressStream(ReadOnlySpan<byte> compressed, int expected)
    {
        byte[] output = new byte[Math.Min(expected, Math.Max(4096, compressed.Length * 4))];
        int written = 0;
        using var decoder = new BrotliDecoder();
        while (true)
        {
            OperationStatus status = decoder.Decompress(compressed, output.AsSpan(written), out int consumed, out int produced);
            compressed = compressed[consumed..];
            written += produced;
            if (status == OperationStatus.Done)
            {
                break;
            }

            if (status == OperationStatus.DestinationTooSmall && written < expected)
            {
                Array.Resize(ref output, (int)Math.Min(expected, Math.Max(written + 1L, output.Length * 2L)));
                continue;
            }

            throw FontReader.Fail(Label, status == OperationStatus.DestinationTooSmall
                ? "the Brotli table stream is longer than the declared table sizes"
                : "the Brotli table stream is corrupt or truncated");
        }

        if (written != expected)
        {
            throw FontReader.Fail(Label, $"the Brotli table stream holds {written} bytes instead of the declared {expected}");
        }

        return output;
    }

    private static ReadOnlySpan<byte> GetTable(byte[] stream, Dictionary<uint, TableRecord> tables, uint tag)
    {
        if (!tables.TryGetValue(tag, out TableRecord record))
        {
            throw FontReader.Fail(Label, $"the '{FontReader.TagToString(tag)}' table is missing");
        }

        return stream.AsSpan(record.Offset, record.Length);
    }

    /// <summary>Rebuilds glyf and a long-format loca from the transformed glyf stream (WOFF2 section 5.1).</summary>
    private static (byte[] Glyf, byte[] Loca, short[] XMins) ReconstructGlyf(ReadOnlySpan<byte> table)
    {
        const string GlyfLabel = "WOFF2 transformed glyf table";
        var header = new FontReader(table, GlyfLabel);
        header.ReadUInt16();
        int optionFlags = header.ReadUInt16();
        int numGlyphs = header.ReadUInt16();
        header.ReadUInt16();
        int[] sizes = new int[7];
        long sum = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = header.ReadOffset32();
            sum += sizes[i];
        }

        if (header.Position + sum > table.Length)
        {
            throw FontReader.Fail(GlyfLabel, "its sub-streams run past the end of the table");
        }

        int offset = header.Position;
        var nContourStream = new FontReader(Take(table, ref offset, sizes[0]), GlyfLabel);
        var nPointsStream = new FontReader(Take(table, ref offset, sizes[1]), GlyfLabel);
        var flagStream = new FontReader(Take(table, ref offset, sizes[2]), GlyfLabel);
        var glyphStream = new FontReader(Take(table, ref offset, sizes[3]), GlyfLabel);
        var compositeStream = new FontReader(Take(table, ref offset, sizes[4]), GlyfLabel);
        ReadOnlySpan<byte> bboxAll = Take(table, ref offset, sizes[5]);
        var instructionStream = new FontReader(Take(table, ref offset, sizes[6]), GlyfLabel);
        ReadOnlySpan<byte> overlapBitmap = default;
        if ((optionFlags & 1) != 0)
        {
            overlapBitmap = FontReader.SliceOf(table, offset, (numGlyphs + 7) >> 3, GlyfLabel);
        }

        int bitmapLength = ((numGlyphs + 31) >> 5) << 2;
        ReadOnlySpan<byte> bboxBitmap = FontReader.SliceOf(bboxAll, 0, bitmapLength, GlyfLabel);
        var bboxStream = new FontReader(bboxAll[bitmapLength..], GlyfLabel);

        var glyf = new GrowableBuffer((int)Math.Clamp(table.Length * 2L, 1024, 16 * 1024 * 1024));
        byte[] loca = new byte[(numGlyphs + 1) * 4];
        short[] xMins = new short[numGlyphs];
        var points = new List<(int X, int Y, bool OnCurve)>();
        var endPoints = new List<int>();
        for (int glyph = 0; glyph < numGlyphs; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyph * 4), (uint)glyf.Length);
            int nContours = nContourStream.ReadInt16();
            bool hasBbox = (bboxBitmap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0;
            if (nContours == 0)
            {
                if (hasBbox)
                {
                    throw FontReader.Fail(GlyfLabel, $"empty glyph {glyph} has an explicit bounding box");
                }

                continue;
            }

            if (nContours > 0)
            {
                endPoints.Clear();
                points.Clear();
                int total = 0;
                for (int c = 0; c < nContours; c++)
                {
                    total += Read255UInt16(ref nPointsStream);
                    if (total > 0xFFFF)
                    {
                        throw FontReader.Fail(GlyfLabel, $"glyph {glyph} has more than 65535 points");
                    }

                    endPoints.Add(total - 1);
                }

                DecodeTriplets(ref flagStream, ref glyphStream, total, points);
                int instructionLength = Read255UInt16(ref glyphStream);
                ReadOnlySpan<byte> instructions = instructionStream.ReadBytes(instructionLength);
                short xMin, yMin, xMax, yMax;
                if (hasBbox)
                {
                    xMin = bboxStream.ReadInt16();
                    yMin = bboxStream.ReadInt16();
                    xMax = bboxStream.ReadInt16();
                    yMax = bboxStream.ReadInt16();
                }
                else
                {
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    foreach ((int x, int y, _) in points)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }

                    if (points.Count == 0)
                    {
                        minX = minY = maxX = maxY = 0;
                    }

                    xMin = ClampShort(minX);
                    yMin = ClampShort(minY);
                    xMax = ClampShort(maxX);
                    yMax = ClampShort(maxY);
                }

                xMins[glyph] = xMin;
                bool overlap = !overlapBitmap.IsEmpty && (overlapBitmap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0;
                WriteSimpleGlyph(glyf, nContours, xMin, yMin, xMax, yMax, endPoints, instructions, points, overlap);
            }
            else if (nContours == -1)
            {
                if (!hasBbox)
                {
                    throw FontReader.Fail(GlyfLabel, $"composite glyph {glyph} has no explicit bounding box");
                }

                short xMin = bboxStream.ReadInt16();
                short yMin = bboxStream.ReadInt16();
                short xMax = bboxStream.ReadInt16();
                short yMax = bboxStream.ReadInt16();
                xMins[glyph] = xMin;
                int start = compositeStream.Position;
                bool haveInstructions = false;
                ushort flags;
                do
                {
                    flags = compositeStream.ReadUInt16();
                    compositeStream.ReadUInt16();
                    int argBytes = (flags & 0x0001) != 0 ? 4 : 2;
                    int transformBytes = (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
                    compositeStream.Skip(argBytes + transformBytes);
                    haveInstructions |= (flags & 0x0100) != 0;
                }
                while ((flags & 0x0020) != 0);

                ReadOnlySpan<byte> components = compositeStream.Slice(start, compositeStream.Position - start);
                glyf.WriteInt16(-1);
                glyf.WriteInt16(xMin);
                glyf.WriteInt16(yMin);
                glyf.WriteInt16(xMax);
                glyf.WriteInt16(yMax);
                glyf.Write(components);
                if (haveInstructions)
                {
                    int instructionLength = Read255UInt16(ref glyphStream);
                    glyf.WriteUInt16((ushort)instructionLength);
                    glyf.Write(instructionStream.ReadBytes(instructionLength));
                }
            }
            else
            {
                throw FontReader.Fail(GlyfLabel, $"glyph {glyph} has invalid contour count {nContours}");
            }

            glyf.PadTo4();
            if (glyf.Length > FontContainer.MaxFontBytes)
            {
                throw FontReader.Fail(GlyfLabel, "the reconstructed glyf table exceeds the size limit");
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(numGlyphs * 4), (uint)glyf.Length);
        return (glyf.ToArray(), loca, xMins);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> table, ref int offset, int length)
    {
        ReadOnlySpan<byte> slice = FontReader.SliceOf(table, offset, length, "WOFF2 transformed glyf table");
        offset += length;
        return slice;
    }

    private static short ClampShort(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    /// <summary>Decodes the WOFF2 triplet encoding of a simple glyph's point coordinates (WOFF2 section 5.2).</summary>
    private static void DecodeTriplets(ref FontReader flagStream, ref FontReader glyphStream, int count, List<(int X, int Y, bool OnCurve)> points)
    {
        ReadOnlySpan<byte> flags = flagStream.ReadBytes(count);
        int x = 0;
        int y = 0;
        for (int i = 0; i < count; i++)
        {
            int flag = flags[i];
            bool onCurve = (flag & 0x80) == 0;
            flag &= 0x7F;
            int dx, dy;
            if (flag < 10)
            {
                dx = 0;
                dy = WithSign(flag, ((flag & 14) << 7) + glyphStream.ReadUInt8());
            }
            else if (flag < 20)
            {
                dx = WithSign(flag, (((flag - 10) & 14) << 7) + glyphStream.ReadUInt8());
                dy = 0;
            }
            else if (flag < 84)
            {
                int b0 = flag - 20;
                int b1 = glyphStream.ReadUInt8();
                dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
                dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            }
            else if (flag < 120)
            {
                int b0 = flag - 84;
                int b1 = glyphStream.ReadUInt8();
                int b2 = glyphStream.ReadUInt8();
                dx = WithSign(flag, 1 + ((b0 / 12) << 8) + b1);
                dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2);
            }
            else if (flag < 124)
            {
                int b1 = glyphStream.ReadUInt8();
                int b2 = glyphStream.ReadUInt8();
                int b3 = glyphStream.ReadUInt8();
                dx = WithSign(flag, (b1 << 4) + (b2 >> 4));
                dy = WithSign(flag >> 1, ((b2 & 0x0F) << 8) + b3);
            }
            else
            {
                int b1 = glyphStream.ReadUInt8();
                int b2 = glyphStream.ReadUInt8();
                int b3 = glyphStream.ReadUInt8();
                int b4 = glyphStream.ReadUInt8();
                dx = WithSign(flag, (b1 << 8) + b2);
                dy = WithSign(flag >> 1, (b3 << 8) + b4);
            }

            x += dx;
            y += dy;
            points.Add((x, y, onCurve));
        }
    }

    private static int WithSign(int flag, int value) => (flag & 1) != 0 ? value : -value;

    /// <summary>Writes a simple glyph in the plain glyf encoding (no flag repeats, which is valid if larger).</summary>
    private static void WriteSimpleGlyph(
        GrowableBuffer glyf, int nContours, short xMin, short yMin, short xMax, short yMax, List<int> endPoints,
        ReadOnlySpan<byte> instructions, List<(int X, int Y, bool OnCurve)> points, bool overlap)
    {
        glyf.WriteInt16((short)nContours);
        glyf.WriteInt16(xMin);
        glyf.WriteInt16(yMin);
        glyf.WriteInt16(xMax);
        glyf.WriteInt16(yMax);
        foreach (int end in endPoints)
        {
            glyf.WriteUInt16((ushort)Math.Max(end, 0));
        }

        glyf.WriteUInt16((ushort)instructions.Length);
        glyf.Write(instructions);

        int previousX = 0;
        int previousY = 0;
        for (int i = 0; i < points.Count; i++)
        {
            int dx = points[i].X - previousX;
            int dy = points[i].Y - previousY;
            int flag = points[i].OnCurve ? 0x01 : 0x00;
            flag |= dx == 0 ? 0x10 : Math.Abs(dx) < 256 ? 0x02 | (dx > 0 ? 0x10 : 0) : 0;
            flag |= dy == 0 ? 0x20 : Math.Abs(dy) < 256 ? 0x04 | (dy > 0 ? 0x20 : 0) : 0;
            if (i == 0 && overlap)
            {
                flag |= 0x40;
            }

            glyf.WriteByte((byte)flag);
            previousX = points[i].X;
            previousY = points[i].Y;
        }

        previousX = 0;
        foreach ((int x, _, _) in points)
        {
            WriteCoordinate(glyf, x - previousX);
            previousX = x;
        }

        previousY = 0;
        foreach ((_, int y, _) in points)
        {
            WriteCoordinate(glyf, y - previousY);
            previousY = y;
        }
    }

    private static void WriteCoordinate(GrowableBuffer glyf, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        if (Math.Abs(delta) < 256)
        {
            glyf.WriteByte((byte)Math.Abs(delta));
        }
        else
        {
            glyf.WriteInt16((short)delta);
        }
    }

    /// <summary>Rebuilds hmtx from the transformed form, taking omitted side bearings from the glyph bounding boxes.</summary>
    private static byte[] ReconstructHmtx(ReadOnlySpan<byte> table, int numHMetrics, int numGlyphs, short[] xMins)
    {
        const string HmtxLabel = "WOFF2 transformed hmtx table";
        if (numHMetrics < 1 || numHMetrics > numGlyphs || numGlyphs > xMins.Length)
        {
            throw FontReader.Fail(HmtxLabel, "its metric counts disagree with the glyph count");
        }

        var r = new FontReader(table, HmtxLabel);
        int flags = r.ReadUInt8();
        if ((flags & 0xFC) != 0 || (flags & 0x03) == 0)
        {
            throw FontReader.Fail(HmtxLabel, $"flags 0x{flags:X2} are invalid");
        }

        ushort[] advances = new ushort[numHMetrics];
        for (int i = 0; i < numHMetrics; i++)
        {
            advances[i] = r.ReadUInt16();
        }

        short[] lsbs = new short[numGlyphs];
        for (int i = 0; i < numHMetrics; i++)
        {
            lsbs[i] = (flags & 1) != 0 ? xMins[i] : r.ReadInt16();
        }

        for (int i = numHMetrics; i < numGlyphs; i++)
        {
            lsbs[i] = (flags & 2) != 0 ? xMins[i] : r.ReadInt16();
        }

        byte[] output = new byte[(numHMetrics * 4) + ((numGlyphs - numHMetrics) * 2)];
        int position = 0;
        for (int i = 0; i < numGlyphs; i++)
        {
            if (i < numHMetrics)
            {
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(position), advances[i]);
                position += 2;
            }

            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(position), lsbs[i]);
            position += 2;
        }

        return output;
    }

    private static uint ReadUIntBase128(ref FontReader r)
    {
        uint accumulator = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = r.ReadUInt8();
            if (i == 0 && b == 0x80)
            {
                throw FontReader.Fail(Label, "a UIntBase128 value has a leading zero");
            }

            if ((accumulator & 0xFE000000) != 0)
            {
                throw FontReader.Fail(Label, "a UIntBase128 value overflows 32 bits");
            }

            accumulator = (accumulator << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return accumulator;
            }
        }

        throw FontReader.Fail(Label, "a UIntBase128 value is longer than 5 bytes");
    }

    private static int Read255UInt16(ref FontReader r)
    {
        byte code = r.ReadUInt8();
        return code switch
        {
            253 => r.ReadUInt16(),
            254 => r.ReadUInt8() + 506,
            255 => r.ReadUInt8() + 253,
            _ => code,
        };
    }

    private readonly record struct Entry(uint Tag, int OrigLength, int StreamLength, bool Transformed, int StreamOffset);

    /// <summary>A minimal append-only big-endian byte buffer.</summary>
    private sealed class GrowableBuffer
    {
        private byte[] buffer;

        public GrowableBuffer(int capacity) => this.buffer = new byte[capacity];

        public int Length { get; private set; }

        public void WriteByte(byte value)
        {
            this.Reserve(1);
            this.buffer[this.Length++] = value;
        }

        public void WriteUInt16(ushort value)
        {
            this.Reserve(2);
            BinaryPrimitives.WriteUInt16BigEndian(this.buffer.AsSpan(this.Length), value);
            this.Length += 2;
        }

        public void WriteInt16(short value) => this.WriteUInt16((ushort)value);

        public void Write(ReadOnlySpan<byte> bytes)
        {
            this.Reserve(bytes.Length);
            bytes.CopyTo(this.buffer.AsSpan(this.Length));
            this.Length += bytes.Length;
        }

        public void PadTo4()
        {
            while ((this.Length & 3) != 0)
            {
                this.WriteByte(0);
            }
        }

        public byte[] ToArray() => this.buffer.AsSpan(0, this.Length).ToArray();

        private void Reserve(int count)
        {
            if (this.Length + (long)count > this.buffer.Length)
            {
                long grown = Math.Max(this.Length + (long)count, this.buffer.Length * 2L);
                if (grown > Array.MaxLength)
                {
                    throw FontReader.Fail(Label, "a reconstructed table is too large");
                }

                Array.Resize(ref this.buffer, (int)grown);
            }
        }
    }
}
