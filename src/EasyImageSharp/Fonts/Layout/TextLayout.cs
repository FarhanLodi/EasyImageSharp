using System.Text;
using EasyImageSharp.Drawing;

namespace EasyImageSharp.Fonts.Layout;

/// <summary>A glyph placed by the layout: its pen position on the baseline, in pixels.</summary>
internal readonly record struct PositionedGlyph(FontFace Face, int Glyph, float X, float Baseline);

/// <summary>One laid-out line: the text it holds, where it starts, how wide it is, and its baseline.</summary>
internal readonly record struct LineBox(int TextStart, int TextLength, float X, float Width, float Baseline);

/// <summary>The result of laying out a text: positioned glyphs, lines and the layout box.</summary>
internal sealed class LaidOutText
{
    public LaidOutText(PositionedGlyph[] glyphs, LineBox[] lines, RectangleF bounds, float lineAdvance, float sizeInPixels)
    {
        this.Glyphs = glyphs;
        this.Lines = lines;
        this.Bounds = bounds;
        this.LineAdvance = lineAdvance;
        this.SizeInPixels = sizeInPixels;
    }

    public PositionedGlyph[] Glyphs { get; }

    public LineBox[] Lines { get; }

    /// <summary>The layout box: line advances horizontally, first ascent to last descent vertically.</summary>
    public RectangleF Bounds { get; }

    public float LineAdvance { get; }

    public float SizeInPixels { get; }

    /// <summary>The union of the glyphs' outline control boxes, or an empty rectangle when nothing is drawn.</summary>
    public RectangleF ComputeInkBounds()
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (PositionedGlyph g in this.Glyphs)
        {
            Rendering.GlyphOutline outline = g.Face.GetOutline(g.Glyph);
            if (outline.IsEmpty)
            {
                continue;
            }

            float scale = this.SizeInPixels / g.Face.UnitsPerEm;
            left = Math.Min(left, g.X + (outline.XMin * scale));
            right = Math.Max(right, g.X + (outline.XMax * scale));
            top = Math.Min(top, g.Baseline - (outline.YMax * scale));
            bottom = Math.Max(bottom, g.Baseline - (outline.YMin * scale));
        }

        return left > right ? RectangleF.Empty : RectangleF.FromLTRB(left, top, right, bottom);
    }
}

/// <summary>
/// Lays text out in lines: one glyph per code point (no shaping), font fallback per character, pair kerning,
/// tab stops, greedy word wrapping on <see cref="LineBreaking"/> opportunities, and alignment.
/// </summary>
internal static class TextLayout
{
    private enum Kind : byte
    {
        Glyph,
        Space,
        Tab,
        Ignorable,
    }

    public static LaidOutText Layout(string text, Font font, TextLayoutOptions options)
    {
        FontFace primary = font.Face;
        float size = font.SizeInPixels;
        if (text.Length == 0)
        {
            return new LaidOutText([], [], new RectangleF(options.Origin.X, options.Origin.Y, 0, 0), font.LineHeight * options.LineSpacing, size);
        }

        float scale = font.Scale;
        float ascent = font.Ascent;
        float descent = font.Descent;
        float lineAdvance = font.LineHeight * options.LineSpacing;
        int spaceGlyph = primary.GetGlyphIndex(' ');
        float spaceAdvance = primary.GetAdvanceWidth(spaceGlyph) * scale;
        if (spaceAdvance <= 0)
        {
            spaceAdvance = size / 4;
        }

        float tabStop = options.TabWidth * spaceAdvance;
        float wrap = options.WrappingWidth;

        var glyphs = new List<PositionedGlyph>();
        var lines = new List<(int Start, int Length, float Width, int GlyphStart, int GlyphEnd)>();
        var items = new List<Item>();
        int paragraphStart = 0;
        while (true)
        {
            OperationExecution.Checkpoint();
            int paragraphEnd = FindHardBreak(text, paragraphStart, out int breakLength);
            items.Clear();
            BuildItems(text, paragraphStart, paragraphEnd, primary, options, size, items);
            if (options.Kerning)
            {
                ApplyKerning(items, size);
            }

            MarkBreaks(items);
            LayoutParagraph(items, wrap, tabStop, primary, scale, glyphs, lines, paragraphStart, paragraphEnd);
            if (breakLength == 0)
            {
                break;
            }

            paragraphStart = paragraphEnd + breakLength;
        }

        // Vertical placement of the block.
        float blockHeight = ascent + descent + ((lines.Count - 1) * lineAdvance);
        float firstBaseline = options.VerticalAlignment switch
        {
            VerticalAlignment.Center => options.Origin.Y - (blockHeight / 2) + ascent,
            VerticalAlignment.Bottom => options.Origin.Y - blockHeight + ascent,
            VerticalAlignment.Baseline => options.Origin.Y,
            _ => options.Origin.Y + ascent,
        };

        var positioned = new PositionedGlyph[glyphs.Count];
        var boxes = new LineBox[lines.Count];
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < lines.Count; i++)
        {
            (int start, int length, float width, int glyphStart, int glyphEnd) = lines[i];
            float lineX = options.HorizontalAlignment switch
            {
                HorizontalAlignment.Center => options.Origin.X - (width / 2),
                HorizontalAlignment.Right => options.Origin.X - width,
                _ => options.Origin.X,
            };

            float baseline = firstBaseline + (i * lineAdvance);
            boxes[i] = new LineBox(start, length, lineX, width, baseline);
            minX = Math.Min(minX, lineX);
            maxX = Math.Max(maxX, lineX + width);
            for (int g = glyphStart; g < glyphEnd; g++)
            {
                PositionedGlyph glyph = glyphs[g];
                positioned[g] = glyph with { X = glyph.X + lineX, Baseline = baseline };
            }
        }

        var bounds = RectangleF.FromLTRB(minX, firstBaseline - ascent, maxX, firstBaseline + ((lines.Count - 1) * lineAdvance) + descent);
        return new LaidOutText(positioned, boxes, bounds, lineAdvance, size);
    }

    /// <summary>Finds the next hard line break at or after <paramref name="start"/>; returns its index (or the text length).</summary>
    private static int FindHardBreak(string text, int start, out int length)
    {
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                length = i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                return i;
            }

            if (c is '\n' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')
            {
                length = 1;
                return i;
            }
        }

        length = 0;
        return text.Length;
    }

    private static void BuildItems(string text, int start, int end, FontFace primary, TextLayoutOptions options, float size, List<Item> items)
    {
        int index = start;
        while (index < end)
        {
            OperationStatusDecode(text.AsSpan(index, end - index), out Rune rune, out int consumed);
            int cp = rune.Value;
            var item = new Item { CodePoint = cp, TextIndex = index, TextLength = consumed };
            if (cp == '\t')
            {
                item.Kind = Kind.Tab;
            }
            else if (LineBreaking.IsIgnorable(cp))
            {
                item.Kind = Kind.Ignorable;
            }
            else
            {
                FontFace face = primary;
                int glyph = primary.GetGlyphIndex(cp);
                if (glyph == 0)
                {
                    foreach (FontFace fallback in options.FallbackFaces)
                    {
                        int candidate = fallback.GetGlyphIndex(cp);
                        if (candidate != 0)
                        {
                            face = fallback;
                            glyph = candidate;
                            break;
                        }
                    }
                }

                item.Face = face;
                item.Glyph = glyph;
                item.Advance = face.GetAdvanceWidth(glyph) * (size / face.UnitsPerEm);
                item.Kind = LineBreaking.IsBreakingSpace(cp) ? Kind.Space : Kind.Glyph;
            }

            items.Add(item);
            index += consumed;
        }
    }

    private static void OperationStatusDecode(ReadOnlySpan<char> span, out Rune rune, out int consumed)
    {
        if (Rune.DecodeFromUtf16(span, out rune, out consumed) != System.Buffers.OperationStatus.Done)
        {
            rune = Rune.ReplacementChar;
            consumed = Math.Max(consumed, 1);
        }
    }

    /// <summary>Applies pair adjustments between adjacent glyphs of the same face.</summary>
    private static void ApplyKerning(List<Item> items, float size)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            Item left = items[i];
            Item right = items[i + 1];
            if (left.Face is null || right.Face is null || !ReferenceEquals(left.Face, right.Face) || left.Face.KerningIsEmpty)
            {
                continue;
            }

            Tables.PairAdjustment adjustment = left.Face.GetPairAdjustment(left.Glyph, right.Glyph);
            if (adjustment.IsZero)
            {
                continue;
            }

            float scale = size / left.Face.UnitsPerEm;
            left.KernAfter += adjustment.FirstAdvance * scale;
            left.Offset += adjustment.FirstPlacement * scale;
            right.Offset += adjustment.SecondPlacement * scale;
            right.Advance += adjustment.SecondAdvance * scale;
            items[i] = left;
            items[i + 1] = right;
        }
    }

    /// <summary>Marks the items a line may start with when it wraps.</summary>
    private static void MarkBreaks(List<Item> items)
    {
        int previous = -1;
        int beforePrevious = -1;
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            bool ignorable = item.Kind == Kind.Ignorable;
            bool breakCarrier = item.CodePoint is LineBreaking.ZeroWidthSpace or LineBreaking.SoftHyphen;
            if (!ignorable && previous >= 0 && LineBreaking.CanBreakBetween(previous, item.CodePoint, beforePrevious))
            {
                item.BreakBefore = true;
                items[i] = item;
            }

            // Ignorable characters attach to what precedes them, except the two that exist to offer a break.
            if (!ignorable || breakCarrier)
            {
                beforePrevious = previous;
                previous = item.CodePoint;
            }
        }
    }

    private static void LayoutParagraph(
        List<Item> items,
        float wrap,
        float tabStop,
        FontFace primary,
        float scale,
        List<PositionedGlyph> glyphs,
        List<(int Start, int Length, float Width, int GlyphStart, int GlyphEnd)> lines,
        int paragraphStart,
        int paragraphEnd)
    {
        if (items.Count == 0)
        {
            lines.Add((paragraphStart, 0, 0, glyphs.Count, glyphs.Count));
            return;
        }

        int lineStart = 0;
        while (lineStart < items.Count)
        {
            int lineEnd = FindLineEnd(items, lineStart, wrap, tabStop);
            EmitLine(items, lineStart, lineEnd, tabStop, primary, scale, glyphs, lines, paragraphEnd);

            // Breaks never fall before a space, so the spaces at a break hang at the end of the earlier line.
            lineStart = lineEnd;
        }
    }

    /// <summary>Returns the exclusive end of the line starting at <paramref name="start"/>.</summary>
    private static int FindLineEnd(List<Item> items, int start, float wrap, float tabStop)
    {
        if (wrap <= 0)
        {
            return items.Count;
        }

        float x = 0;
        int lastBreak = -1;
        bool hasVisible = false;
        for (int i = start; i < items.Count; i++)
        {
            Item item = items[i];
            if (i > start)
            {
                x += items[i - 1].KernAfter;
                if (item.BreakBefore)
                {
                    lastBreak = i;
                }
            }

            float advance = Advance(item, x, tabStop);
            bool whitespace = item.Kind is Kind.Space or Kind.Tab or Kind.Ignorable;
            if (!whitespace && hasVisible && x + advance > wrap + 0.001f)
            {
                return lastBreak > start ? lastBreak : i;
            }

            hasVisible |= !whitespace;
            x += advance;
        }

        return items.Count;
    }

    private static float Advance(Item item, float x, float tabStop)
    {
        switch (item.Kind)
        {
            case Kind.Tab:
                if (tabStop <= 0)
                {
                    return 0;
                }

                float next = (MathF.Floor((x + 0.001f) / tabStop) + 1) * tabStop;
                return next - x;
            case Kind.Ignorable:
                return 0;
            default:
                return item.Advance;
        }
    }

    private static void EmitLine(
        List<Item> items,
        int start,
        int end,
        float tabStop,
        FontFace primary,
        float scale,
        List<PositionedGlyph> glyphs,
        List<(int Start, int Length, float Width, int GlyphStart, int GlyphEnd)> lines,
        int paragraphEnd)
    {
        int glyphStart = glyphs.Count;
        float x = 0;
        float width = 0;
        for (int i = start; i < end; i++)
        {
            Item item = items[i];
            if (i > start)
            {
                x += items[i - 1].KernAfter;
            }

            float advance = Advance(item, x, tabStop);
            if (item.Kind == Kind.Glyph && item.Face is not null)
            {
                glyphs.Add(new PositionedGlyph(item.Face, item.Glyph, x + item.Offset, 0));
                width = x + advance;
            }

            x += advance;
        }

        // A line that wraps at a soft hyphen shows a hyphen there.
        if (end < items.Count && end > start && items[end - 1].CodePoint == LineBreaking.SoftHyphen)
        {
            int hyphen = primary.GetGlyphIndex(0x2010);
            if (hyphen == 0)
            {
                hyphen = primary.GetGlyphIndex('-');
            }

            if (hyphen != 0)
            {
                glyphs.Add(new PositionedGlyph(primary, hyphen, x, 0));
                width = x + (primary.GetAdvanceWidth(hyphen) * scale);
            }
        }

        int textStart = items[start].TextIndex;
        int textEnd = end < items.Count ? items[end].TextIndex : paragraphEnd;
        lines.Add((textStart, textEnd - textStart, width, glyphStart, glyphs.Count));
    }

    private struct Item
    {
        public int CodePoint;
        public int TextIndex;
        public int TextLength;
        public FontFace? Face;
        public int Glyph;
        public float Advance;
        public float KernAfter;
        public float Offset;
        public Kind Kind;
        public bool BreakBefore;
    }
}
