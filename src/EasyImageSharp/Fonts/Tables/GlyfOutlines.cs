using System.Buffers.Binary;
using EasyImageSharp.Fonts.Rendering;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>A source of glyph outlines in font units.</summary>
internal interface IOutlineSource
{
    /// <summary>Decodes the outline of <paramref name="glyph"/>; throws <see cref="InvalidFontException"/> when its data is malformed.</summary>
    GlyphOutline GetOutline(int glyph);
}

/// <summary>
/// TrueType outlines from the <c>glyf</c> and <c>loca</c> tables: simple glyphs of quadratic contours and
/// composite glyphs assembled from transformed components (offsets or point matching). Hinting instructions
/// are not executed; outlines are rendered as designed.
/// </summary>
internal sealed class GlyfOutlines : IOutlineSource
{
    /// <summary>Deepest component nesting accepted; real fonts stay in single digits.</summary>
    private const int MaxDepth = 16;

    /// <summary>Most components expanded for one glyph, which bounds fonts whose components fan out exponentially.</summary>
    private const int MaxComponents = 4096;

    /// <summary>Most points one glyph may expand to.</summary>
    private const int MaxPoints = 1 << 20;

    private const string Label = "glyf table";

    private readonly byte[] data;
    private readonly int glyfOffset;
    private readonly int glyfLength;
    private readonly int[] offsets;

    public GlyfOutlines(FontSource source, HeadTable head, int numGlyphs)
    {
        TableRecord glyf = source.GetRecord(FontReader.Tag("glyf"))
            ?? throw new InvalidFontException("Malformed font: the required 'glyf' table is missing.");
        ReadOnlySpan<byte> loca = source.GetRequiredTable(FontReader.Tag("loca"));
        this.data = source.Data;
        this.glyfOffset = glyf.Offset;
        this.glyfLength = glyf.Length;

        bool longFormat = source.LongLocaOverride || head.IndexToLocFormat == 1;
        if (!longFormat && head.IndexToLocFormat != 0)
        {
            throw new InvalidFontException($"Malformed head table: indexToLocFormat {head.IndexToLocFormat} is not 0 or 1.");
        }

        // A loca shorter than numGlyphs + 1 entries is tolerated: the glyphs it does not cover are empty.
        int entrySize = longFormat ? 4 : 2;
        int entries = Math.Min(numGlyphs + 1, loca.Length / entrySize);
        this.offsets = new int[entries];
        for (int i = 0; i < entries; i++)
        {
            long offset = longFormat
                ? BinaryPrimitives.ReadUInt32BigEndian(loca.Slice(i * 4, 4))
                : BinaryPrimitives.ReadUInt16BigEndian(loca.Slice(i * 2, 2)) * 2L;
            this.offsets[i] = (int)Math.Min(offset, int.MaxValue);
        }
    }

    public GlyphOutline GetOutline(int glyph)
    {
        var points = new PointList();
        int budget = MaxComponents;
        this.Decode(glyph, 0, points, ref budget);
        return points.ToOutline();
    }

    /// <summary>Returns the glyph's bytes, or an empty span for a glyph without outline data.</summary>
    private ReadOnlySpan<byte> GetGlyphData(int glyph)
    {
        if ((uint)glyph + 1 >= (uint)this.offsets.Length)
        {
            return default;
        }

        int start = this.offsets[glyph];
        int end = this.offsets[glyph + 1];
        if (end <= start)
        {
            // Equal offsets mark an empty glyph; decreasing ones are malformed, and are treated the same way.
            return default;
        }

        if (end > this.glyfLength)
        {
            throw FontReader.Fail(Label, $"glyph {glyph} runs past the end of the table");
        }

        return this.data.AsSpan(this.glyfOffset + start, end - start);
    }

    private void Decode(int glyph, int depth, PointList output, ref int budget)
    {
        ReadOnlySpan<byte> glyphData = this.GetGlyphData(glyph);
        if (glyphData.Length < 10)
        {
            return;
        }

        var r = new FontReader(glyphData, Label);
        short contours = r.ReadInt16();
        r.Skip(8);
        if (contours >= 0)
        {
            DecodeSimple(ref r, contours, output);
        }
        else
        {
            this.DecodeComposite(ref r, depth, output, ref budget);
        }
    }

    private static void DecodeSimple(ref FontReader r, int contourCount, PointList output)
    {
        if (contourCount == 0)
        {
            return;
        }

        int[] ends = new int[contourCount];
        int previous = -1;
        for (int i = 0; i < contourCount; i++)
        {
            int end = r.ReadUInt16();
            if (end <= previous)
            {
                throw FontReader.Fail(Label, "contour end points are not increasing");
            }

            ends[i] = end;
            previous = end;
        }

        int pointCount = previous + 1;
        if (output.Count + pointCount > MaxPoints)
        {
            throw FontReader.Fail(Label, "a glyph has too many points");
        }

        int instructionLength = r.ReadUInt16();
        r.Skip(instructionLength);

        byte[] flags = new byte[pointCount];
        for (int i = 0; i < pointCount;)
        {
            byte flag = r.ReadUInt8();
            flags[i++] = flag;
            if ((flag & 0x08) != 0)
            {
                int repeat = r.ReadUInt8();
                if (repeat > pointCount - i)
                {
                    throw FontReader.Fail(Label, "a flag repeat runs past the last point");
                }

                flags.AsSpan(i, repeat).Fill(flag);
                i += repeat;
            }
        }

        int[] xs = new int[pointCount];
        int x = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x02) != 0)
            {
                int dx = r.ReadUInt8();
                x += (flag & 0x10) != 0 ? dx : -dx;
            }
            else if ((flag & 0x10) == 0)
            {
                x += r.ReadInt16();
            }

            xs[i] = x;
        }

        int y = 0;
        int contour = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x04) != 0)
            {
                int dy = r.ReadUInt8();
                y += (flag & 0x20) != 0 ? dy : -dy;
            }
            else if ((flag & 0x20) == 0)
            {
                y += r.ReadInt16();
            }

            output.Add(xs[i], y, (flag & 0x01) != 0);
            if (i == ends[contour])
            {
                output.EndContour();
                contour++;
            }
        }
    }

    private void DecodeComposite(ref FontReader r, int depth, PointList output, ref int budget)
    {
        if (depth >= MaxDepth)
        {
            throw FontReader.Fail(Label, $"composite glyphs nest deeper than {MaxDepth} levels");
        }

        ushort flags;
        do
        {
            if (--budget < 0)
            {
                throw FontReader.Fail(Label, $"a composite glyph expands to more than {MaxComponents} components");
            }

            flags = r.ReadUInt16();
            int component = r.ReadUInt16();
            int arg1, arg2;
            bool words = (flags & 0x0001) != 0;
            bool xyValues = (flags & 0x0002) != 0;
            if (words)
            {
                arg1 = xyValues ? r.ReadInt16() : r.ReadUInt16();
                arg2 = xyValues ? r.ReadInt16() : r.ReadUInt16();
            }
            else
            {
                arg1 = xyValues ? r.ReadInt8() : r.ReadUInt8();
                arg2 = xyValues ? r.ReadInt8() : r.ReadUInt8();
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & 0x0008) != 0)
            {
                a = d = r.ReadF2Dot14();
            }
            else if ((flags & 0x0040) != 0)
            {
                a = r.ReadF2Dot14();
                d = r.ReadF2Dot14();
            }
            else if ((flags & 0x0080) != 0)
            {
                a = r.ReadF2Dot14();
                b = r.ReadF2Dot14();
                c = r.ReadF2Dot14();
                d = r.ReadF2Dot14();
            }

            var child = new PointList();
            this.Decode(component, depth + 1, child, ref budget);
            if (output.Count + child.Count > MaxPoints)
            {
                throw FontReader.Fail(Label, "a composite glyph has too many points");
            }

            child.Transform(a, b, c, d);
            float dx, dy;
            if (xyValues)
            {
                dx = arg1;
                dy = arg2;

                // Offsets are unscaled unless the font asks otherwise (the Microsoft default, also followed by
                // the common rasterisers); a scaled offset is multiplied by the length of each transform column.
                if ((flags & 0x0800) != 0 && (flags & 0x1000) == 0)
                {
                    dx *= MathF.Sqrt((a * a) + (b * b));
                    dy *= MathF.Sqrt((c * c) + (d * d));
                }
            }
            else
            {
                // Point matching: move the component so its point arg2 lands on the parent's point arg1.
                if ((uint)arg1 >= (uint)output.Count || (uint)arg2 >= (uint)child.Count)
                {
                    throw FontReader.Fail(Label, "a composite glyph matches a point that does not exist");
                }

                (float px, float py) = output.GetPoint(arg1);
                (float cx, float cy) = child.GetPoint(arg2);
                dx = px - cx;
                dy = py - cy;
            }

            output.Append(child, dx, dy);
        }
        while ((flags & 0x0020) != 0);
    }

    /// <summary>Growable point storage for one glyph, with the TrueType on/off-curve flags and contour ends.</summary>
    private sealed class PointList
    {
        private readonly List<float> xs = new();
        private readonly List<float> ys = new();
        private readonly List<bool> onCurve = new();
        private readonly List<int> ends = new();

        public int Count => this.xs.Count;

        public void Add(float x, float y, bool on)
        {
            this.xs.Add(x);
            this.ys.Add(y);
            this.onCurve.Add(on);
        }

        public void EndContour() => this.ends.Add(this.xs.Count - 1);

        public (float X, float Y) GetPoint(int index) => (this.xs[index], this.ys[index]);

        public void Transform(float a, float b, float c, float d)
        {
            if (a == 1 && b == 0 && c == 0 && d == 1)
            {
                return;
            }

            for (int i = 0; i < this.xs.Count; i++)
            {
                float x = this.xs[i];
                float y = this.ys[i];
                this.xs[i] = (a * x) + (c * y);
                this.ys[i] = (b * x) + (d * y);
            }
        }

        public void Append(PointList other, float dx, float dy)
        {
            int baseIndex = this.xs.Count;
            for (int i = 0; i < other.xs.Count; i++)
            {
                this.Add(other.xs[i] + dx, other.ys[i] + dy, other.onCurve[i]);
            }

            foreach (int end in other.ends)
            {
                this.ends.Add(baseIndex + end);
            }
        }

        /// <summary>Converts the quadratic B-spline contours to explicit path segments (implied on-curve midpoints).</summary>
        public GlyphOutline ToOutline()
        {
            var builder = new GlyphOutline.Builder();
            int start = 0;
            foreach (int end in this.ends)
            {
                if (end - start >= 1)
                {
                    this.AddContour(builder, start, end);
                }

                start = end + 1;
            }

            return builder.Build();
        }

        private void AddContour(GlyphOutline.Builder builder, int start, int end)
        {
            float sx, sy;
            int first;
            int last;
            if (this.onCurve[start])
            {
                (sx, sy) = (this.xs[start], this.ys[start]);
                first = start + 1;
                last = end;
            }
            else if (this.onCurve[end])
            {
                (sx, sy) = (this.xs[end], this.ys[end]);
                first = start;
                last = end - 1;
            }
            else
            {
                sx = (this.xs[start] + this.xs[end]) * 0.5f;
                sy = (this.ys[start] + this.ys[end]) * 0.5f;
                first = start;
                last = end;
            }

            builder.MoveTo(sx, sy);
            bool pending = false;
            float cx = 0, cy = 0;
            for (int i = first; i <= last; i++)
            {
                float x = this.xs[i];
                float y = this.ys[i];
                if (this.onCurve[i])
                {
                    if (pending)
                    {
                        builder.QuadTo(cx, cy, x, y);
                        pending = false;
                    }
                    else
                    {
                        builder.LineTo(x, y);
                    }
                }
                else
                {
                    if (pending)
                    {
                        builder.QuadTo(cx, cy, (cx + x) * 0.5f, (cy + y) * 0.5f);
                    }

                    cx = x;
                    cy = y;
                    pending = true;
                }
            }

            if (pending)
            {
                builder.QuadTo(cx, cy, sx, sy);
            }

            builder.Close();
        }
    }
}
