namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// Pair kerning from the GPOS table: the pair adjustment lookups (type 2, formats 1 and 2, also behind type 9
/// extension lookups) of every <c>kern</c> feature reachable from a script's default language system. There is
/// no shaping, so the lookups are applied to adjacent glyph pairs in lookup order and their values add up;
/// lookup flags (mark skipping) and device tables are ignored.
/// </summary>
internal sealed class GposKerning : Kerning
{
    private const string Label = "GPOS table";

    /// <summary>Most pair subtables read, so a hostile table cannot make the parser loop for long.</summary>
    private const int MaxSubtables = 4096;

    private readonly PairSubtable[][] lookups;

    private GposKerning(PairSubtable[][] lookups) => this.lookups = lookups;

    public override PairAdjustment Get(int left, int right)
    {
        PairAdjustment total = default;
        foreach (PairSubtable[] lookup in this.lookups)
        {
            // Within a lookup the first subtable that covers the pair decides.
            foreach (PairSubtable subtable in lookup)
            {
                if (subtable.TryGet(left, right, out PairAdjustment value))
                {
                    total += value;
                    break;
                }
            }
        }

        return total;
    }

    /// <summary>Parses the kerning lookups, or returns <see langword="null"/> when the font has none.</summary>
    public static GposKerning? Parse(ReadOnlySpan<byte> table)
    {
        var r = new FontReader(table, Label);
        int major = r.ReadUInt16();
        r.ReadUInt16();
        if (major != 1)
        {
            return null;
        }

        int scriptList = r.ReadUInt16();
        int featureList = r.ReadUInt16();
        int lookupList = r.ReadUInt16();

        // Feature indices of 'kern' features referenced by any script's default language system; when no
        // script references one (or the script list is empty), every 'kern' feature counts.
        var kernFeatures = new HashSet<int>();
        var features = new FontReader(table, Label) { Position = featureList };
        int featureCount = features.ReadUInt16();
        var kernTagged = new List<(int Index, int Offset)>();
        uint kern = FontReader.Tag("kern");
        for (int i = 0; i < featureCount; i++)
        {
            uint tag = features.ReadTag();
            int offset = features.ReadUInt16();
            if (tag == kern)
            {
                kernTagged.Add((i, offset));
            }
        }

        if (kernTagged.Count == 0)
        {
            return null;
        }

        var scripts = new FontReader(table, Label) { Position = scriptList };
        int scriptCount = scripts.ReadUInt16();
        for (int i = 0; i < scriptCount; i++)
        {
            scripts.ReadTag();
            int scriptOffset = scriptList + scripts.ReadUInt16();
            int defaultLangSys = FontReader.UInt16At(table, scriptOffset, Label);
            if (defaultLangSys == 0)
            {
                continue;
            }

            var langSys = new FontReader(table, Label) { Position = scriptOffset + defaultLangSys };
            langSys.ReadUInt16();
            int required = langSys.ReadUInt16();
            int indexCount = langSys.ReadUInt16();
            for (int k = 0; k < indexCount; k++)
            {
                kernFeatures.Add(langSys.ReadUInt16());
            }

            if (required != 0xFFFF)
            {
                kernFeatures.Add(required);
            }
        }

        var lookupIndices = new SortedSet<int>();
        bool anyReferenced = kernTagged.Exists(f => kernFeatures.Contains(f.Index));
        foreach ((int index, int offset) in kernTagged)
        {
            if (anyReferenced && !kernFeatures.Contains(index))
            {
                continue;
            }

            var feature = new FontReader(table, Label) { Position = featureList + offset };
            feature.ReadUInt16();
            int count = feature.ReadUInt16();
            for (int k = 0; k < count; k++)
            {
                lookupIndices.Add(feature.ReadUInt16());
            }
        }

        var lookupReader = new FontReader(table, Label) { Position = lookupList };
        int lookupCount = lookupReader.ReadUInt16();
        var lookups = new List<PairSubtable[]>();
        int budget = MaxSubtables;
        foreach (int lookupIndex in lookupIndices)
        {
            if (lookupIndex >= lookupCount)
            {
                continue;
            }

            int lookupOffset = lookupList + FontReader.UInt16At(table, lookupList + 2 + (lookupIndex * 2), Label);
            var lookup = new FontReader(table, Label) { Position = lookupOffset };
            int type = lookup.ReadUInt16();
            lookup.ReadUInt16();
            int subtableCount = lookup.ReadUInt16();
            var subtables = new List<PairSubtable>();
            for (int s = 0; s < subtableCount; s++)
            {
                if (--budget < 0)
                {
                    throw FontReader.Fail(Label, "it has too many subtables");
                }

                int subtableOffset = lookupOffset + lookup.ReadUInt16();
                int subtableType = type;
                if (type == 9)
                {
                    // Extension: format, wrapped lookup type, 32-bit offset from the extension subtable.
                    var extension = new FontReader(table, Label) { Position = subtableOffset };
                    extension.ReadUInt16();
                    subtableType = extension.ReadUInt16();
                    subtableOffset += extension.ReadOffset32();
                }

                if (subtableType == 2)
                {
                    subtables.Add(PairSubtable.Parse(table, subtableOffset));
                }
            }

            if (subtables.Count > 0)
            {
                lookups.Add(subtables.ToArray());
            }
        }

        return lookups.Count == 0 ? null : new GposKerning(lookups.ToArray());
    }

    /// <summary>One PairPos subtable, fully parsed.</summary>
    private sealed class PairSubtable
    {
        private readonly Coverage coverage;

        // Format 1: per coverage index, the second glyphs (ascending) and their adjustments.
        private readonly ushort[][]? secondGlyphs;
        private readonly PairAdjustment[][]? pairValues;

        // Format 2: class definitions and a class1 x class2 matrix of adjustments.
        private readonly ClassDef? classDef1;
        private readonly ClassDef? classDef2;
        private readonly int class2Count;
        private readonly PairAdjustment[]? matrix;

        private PairSubtable(Coverage coverage, ushort[][] secondGlyphs, PairAdjustment[][] pairValues)
        {
            this.coverage = coverage;
            this.secondGlyphs = secondGlyphs;
            this.pairValues = pairValues;
        }

        private PairSubtable(Coverage coverage, ClassDef classDef1, ClassDef classDef2, int class2Count, PairAdjustment[] matrix)
        {
            this.coverage = coverage;
            this.classDef1 = classDef1;
            this.classDef2 = classDef2;
            this.class2Count = class2Count;
            this.matrix = matrix;
        }

        public bool TryGet(int left, int right, out PairAdjustment value)
        {
            value = default;
            int index = this.coverage.IndexOf(left);
            if (index < 0)
            {
                return false;
            }

            if (this.secondGlyphs is not null)
            {
                if (index >= this.secondGlyphs.Length)
                {
                    return false;
                }

                int found = Array.BinarySearch(this.secondGlyphs[index], (ushort)right);
                if (found < 0 || right > ushort.MaxValue)
                {
                    return false;
                }

                value = this.pairValues![index][found];
                return true;
            }

            int class1 = this.classDef1!.ClassOf(left);
            int class2 = this.classDef2!.ClassOf(right);
            long cell = ((long)class1 * this.class2Count) + class2;
            if (class2 >= this.class2Count || cell >= this.matrix!.Length)
            {
                return false;
            }

            value = this.matrix[cell];
            return true;
        }

        public static PairSubtable Parse(ReadOnlySpan<byte> table, int offset)
        {
            var r = new FontReader(table, Label) { Position = offset };
            int format = r.ReadUInt16();
            Coverage coverage = Coverage.Parse(table, offset + r.ReadUInt16());
            int valueFormat1 = r.ReadUInt16();
            int valueFormat2 = r.ReadUInt16();
            if (format == 1)
            {
                int pairSetCount = r.ReadUInt16();
                var seconds = new ushort[pairSetCount][];
                var values = new PairAdjustment[pairSetCount][];
                int recordSize = 2 + ValueSize(valueFormat1) + ValueSize(valueFormat2);
                for (int i = 0; i < pairSetCount; i++)
                {
                    var set = new FontReader(table, Label) { Position = offset + r.ReadUInt16() };
                    int count = set.ReadUInt16();
                    if (count > set.Remaining / recordSize)
                    {
                        throw FontReader.Fail(Label, "a PairSet runs past the end of the table");
                    }

                    var glyphs = new ushort[count];
                    var adjustments = new PairAdjustment[count];
                    for (int k = 0; k < count; k++)
                    {
                        glyphs[k] = set.ReadUInt16();
                        adjustments[k] = ReadPair(ref set, valueFormat1, valueFormat2);
                    }

                    // Records are sorted by glyph id in valid fonts; sorting tolerates the ones that are not.
                    Array.Sort(glyphs, adjustments);
                    seconds[i] = glyphs;
                    values[i] = adjustments;
                }

                return new PairSubtable(coverage, seconds, values);
            }

            if (format == 2)
            {
                ClassDef classDef1 = ClassDef.Parse(table, offset + r.ReadUInt16());
                ClassDef classDef2 = ClassDef.Parse(table, offset + r.ReadUInt16());
                int class1Count = r.ReadUInt16();
                int class2Count = r.ReadUInt16();
                int recordSize = ValueSize(valueFormat1) + ValueSize(valueFormat2);
                // With both value formats empty every cell is zero, and no matrix is stored at all.
                long cells = recordSize == 0 ? 0 : (long)class1Count * class2Count;
                if (recordSize > 0 && cells > r.Remaining / recordSize)
                {
                    throw FontReader.Fail(Label, "a class-pair matrix runs past the end of the table");
                }

                var matrix = new PairAdjustment[cells];
                for (long i = 0; i < cells; i++)
                {
                    matrix[i] = ReadPair(ref r, valueFormat1, valueFormat2);
                }

                return new PairSubtable(coverage, classDef1, classDef2, class2Count, matrix);
            }

            throw FontReader.Fail(Label, $"PairPos format {format} is not 1 or 2");
        }

        private static int ValueSize(int valueFormat) => System.Numerics.BitOperations.PopCount((uint)valueFormat & 0xFF) * 2;

        private static PairAdjustment ReadPair(ref FontReader r, int format1, int format2)
        {
            (int place1, int advance1) = ReadValue(ref r, format1);
            (int place2, int advance2) = ReadValue(ref r, format2);
            return new PairAdjustment(advance1, place1, place2, advance2);
        }

        /// <summary>Reads a ValueRecord, returning its horizontal placement and advance; other fields are skipped.</summary>
        private static (int Placement, int Advance) ReadValue(ref FontReader r, int format)
        {
            int placement = 0;
            int advance = 0;
            if ((format & 0x0001) != 0)
            {
                placement = r.ReadInt16();
            }

            if ((format & 0x0002) != 0)
            {
                r.ReadInt16();
            }

            if ((format & 0x0004) != 0)
            {
                advance = r.ReadInt16();
            }

            // YAdvance and the four device/variation offsets.
            for (int bit = 0x0008; bit <= 0x0080; bit <<= 1)
            {
                if ((format & bit) != 0)
                {
                    r.ReadUInt16();
                }
            }

            return (placement, advance);
        }
    }

    /// <summary>A Coverage table: glyph id to coverage index.</summary>
    private sealed class Coverage
    {
        private readonly ushort[] glyphs;
        private readonly ushort[] rangeStarts;
        private readonly ushort[] rangeEnds;
        private readonly int[] rangeIndices;

        private Coverage(ushort[] glyphs, ushort[] rangeStarts, ushort[] rangeEnds, int[] rangeIndices)
        {
            this.glyphs = glyphs;
            this.rangeStarts = rangeStarts;
            this.rangeEnds = rangeEnds;
            this.rangeIndices = rangeIndices;
        }

        public int IndexOf(int glyph)
        {
            if ((uint)glyph > ushort.MaxValue)
            {
                return -1;
            }

            if (this.glyphs.Length > 0)
            {
                int found = Array.BinarySearch(this.glyphs, (ushort)glyph);
                return found >= 0 ? found : -1;
            }

            int lo = 0;
            int hi = this.rangeStarts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (this.rangeEnds[mid] < glyph)
                {
                    lo = mid + 1;
                }
                else if (this.rangeStarts[mid] > glyph)
                {
                    hi = mid - 1;
                }
                else
                {
                    return this.rangeIndices[mid] + (glyph - this.rangeStarts[mid]);
                }
            }

            return -1;
        }

        public static Coverage Parse(ReadOnlySpan<byte> table, int offset)
        {
            var r = new FontReader(table, Label) { Position = offset };
            int format = r.ReadUInt16();
            int count = r.ReadUInt16();
            if (format == 1)
            {
                ushort[] glyphs = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    glyphs[i] = r.ReadUInt16();
                }

                // Binary search needs ascending ids; the index of each glyph is its position in the original
                // order, so an unsorted table is rejected rather than silently re-indexed.
                for (int i = 1; i < count; i++)
                {
                    if (glyphs[i] <= glyphs[i - 1])
                    {
                        throw FontReader.Fail(Label, "a Coverage table is not sorted");
                    }
                }

                return new Coverage(glyphs, [], [], []);
            }

            if (format == 2)
            {
                ushort[] starts = new ushort[count];
                ushort[] ends = new ushort[count];
                int[] indices = new int[count];
                for (int i = 0; i < count; i++)
                {
                    starts[i] = r.ReadUInt16();
                    ends[i] = r.ReadUInt16();
                    indices[i] = r.ReadUInt16();
                    if (ends[i] < starts[i] || (i > 0 && starts[i] <= ends[i - 1]))
                    {
                        throw FontReader.Fail(Label, "a Coverage range table is not sorted");
                    }
                }

                return new Coverage([], starts, ends, indices);
            }

            throw FontReader.Fail(Label, $"Coverage format {format} is not 1 or 2");
        }
    }

    /// <summary>A ClassDef table: glyph id to class (0 for glyphs it does not list).</summary>
    private sealed class ClassDef
    {
        private readonly int startGlyph;
        private readonly ushort[] classes;
        private readonly ushort[] rangeStarts;
        private readonly ushort[] rangeEnds;
        private readonly ushort[] rangeClasses;

        private ClassDef(int startGlyph, ushort[] classes, ushort[] rangeStarts, ushort[] rangeEnds, ushort[] rangeClasses)
        {
            this.startGlyph = startGlyph;
            this.classes = classes;
            this.rangeStarts = rangeStarts;
            this.rangeEnds = rangeEnds;
            this.rangeClasses = rangeClasses;
        }

        public int ClassOf(int glyph)
        {
            int index = glyph - this.startGlyph;
            if (this.classes.Length > 0)
            {
                return (uint)index < (uint)this.classes.Length ? this.classes[index] : 0;
            }

            int lo = 0;
            int hi = this.rangeStarts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (this.rangeEnds[mid] < glyph)
                {
                    lo = mid + 1;
                }
                else if (this.rangeStarts[mid] > glyph)
                {
                    hi = mid - 1;
                }
                else
                {
                    return this.rangeClasses[mid];
                }
            }

            return 0;
        }

        public static ClassDef Parse(ReadOnlySpan<byte> table, int offset)
        {
            var r = new FontReader(table, Label) { Position = offset };
            int format = r.ReadUInt16();
            if (format == 1)
            {
                int start = r.ReadUInt16();
                int count = r.ReadUInt16();
                ushort[] classes = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    classes[i] = r.ReadUInt16();
                }

                return new ClassDef(start, classes, [], [], []);
            }

            if (format == 2)
            {
                int count = r.ReadUInt16();
                var ranges = new (ushort Start, ushort End, ushort Class)[count];
                for (int i = 0; i < count; i++)
                {
                    ranges[i] = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
                }

                Array.Sort(ranges, static (a, b) => a.Start.CompareTo(b.Start));
                var starts = new ushort[count];
                var ends = new ushort[count];
                var classes = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    (starts[i], ends[i], classes[i]) = ranges[i];
                }

                return new ClassDef(0, [], starts, ends, classes);
            }

            throw FontReader.Fail(Label, $"ClassDef format {format} is not 1 or 2");
        }
    }
}
