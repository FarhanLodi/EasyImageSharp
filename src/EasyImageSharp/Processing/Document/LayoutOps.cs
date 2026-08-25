using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Projection-profile layout analysis of a (deskewed) page. Straight runs longer than a quarter of the page
/// (rules, borders) are ignored. Text lines are runs of the horizontal ink profile (short fragments such as
/// dots and accents closer than a third of the median run height are attached to their neighbour);
/// words come from a horizontal run-length smoothing (RLSA) of each line: gaps narrower than 40 % of the line
/// height are filled, and the remaining runs of the column profile are the words.
/// </summary>
internal static class LayoutOps
{
    public static TextRegions SegmentText<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        return SegmentText(mask, width, height);
    }

    public static TextRegions SegmentText(byte[] mask, int width, int height)
    {
        // Rules and borders (straight runs longer than a quarter of the page) are not text; ignore them so a
        // vertical rule cannot fuse every line into one.
        int minRun = Math.Max(40, Math.Min(width, height) / 4);
        byte[] horizontalRuns = CleanupOps.LongRuns(mask, width, height, minRun, horizontal: true);
        byte[] verticalRuns = CleanupOps.LongRuns(mask, width, height, minRun, horizontal: false);
        var text = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            text[i] = (byte)(mask[i] & ~(horizontalRuns[i] | verticalRuns[i]) & 1);
        }

        mask = text;
        var rows = new int[height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int count = 0;
            for (int x = 0; x < width; x++)
            {
                count += mask[row + x];
            }

            rows[y] = count;
        }

        // Ink rows -> raw runs.
        var raw = new List<(int Y0, int Y1)>();
        int start = -1;
        for (int y = 0; y <= height; y++)
        {
            bool inkRow = y < height && rows[y] > 0;
            if (inkRow && start < 0)
            {
                start = y;
            }
            else if (!inkRow && start >= 0)
            {
                raw.Add((start, y - 1));
                start = -1;
            }
        }

        if (raw.Count == 0)
        {
            return new TextRegions(Array.Empty<TextLine>());
        }

        // Fragments (dots, accents, underlines: runs shorter than half the median run height) closer than a third
        // of the median height to their neighbour are merged into it; full-height runs stay separate lines.
        var heights = raw.Select(r => r.Y1 - r.Y0 + 1).OrderBy(h => h).ToArray();
        int median = heights[heights.Length / 2];
        int mergeGap = Math.Max(1, median / 3);
        var merged = new List<(int Y0, int Y1)>();
        foreach ((int y0, int y1) in raw)
        {
            if (merged.Count > 0)
            {
                (int py0, int py1) = merged[^1];
                bool fragment = (y1 - y0 + 1) * 2 < median || (py1 - py0 + 1) * 2 < median;
                if (fragment && y0 - py1 - 1 <= mergeGap)
                {
                    merged[^1] = (py0, y1);
                    continue;
                }
            }

            merged.Add((y0, y1));
        }

        var lines = new List<TextLine>();
        foreach ((int y0, int y1) in merged)
        {
            int lineHeight = y1 - y0 + 1;
            if (lineHeight < 2)
            {
                continue;
            }

            // Column profile of the line.
            var cols = new int[width];
            int left = int.MaxValue;
            int right = -1;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (mask[row + x] != 0)
                    {
                        cols[x]++;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            if (right < 0)
            {
                continue;
            }

            // RLSA: fill gaps narrower than 40 % of the line height, then split into words.
            int wordGap = Math.Max(2, (int)Math.Round(lineHeight * 0.4));
            var words = new List<Rectangle>();
            int wordStart = -1;
            int lastInk = -1;
            for (int x = left; x <= right + 1; x++)
            {
                bool inkCol = x <= right && cols[x] > 0;
                if (inkCol)
                {
                    if (wordStart < 0)
                    {
                        wordStart = x;
                    }
                    else if (x - lastInk - 1 >= wordGap)
                    {
                        words.Add(TightBox(mask, width, wordStart, lastInk, y0, y1));
                        wordStart = x;
                    }

                    lastInk = x;
                }
                else if (x > right && wordStart >= 0)
                {
                    words.Add(TightBox(mask, width, wordStart, lastInk, y0, y1));
                }
            }

            lines.Add(new TextLine(Rectangle.FromLTRB(left, y0, right + 1, y1 + 1), words));
        }

        return new TextRegions(lines);
    }

    private static Rectangle TightBox(byte[] mask, int width, int x0, int x1, int y0, int y1)
    {
        int top = int.MaxValue;
        int bottom = -1;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * width;
            for (int x = x0; x <= x1; x++)
            {
                if (mask[row + x] != 0)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    break;
                }
            }
        }

        if (bottom < 0)
        {
            top = y0;
            bottom = y1;
        }

        return Rectangle.FromLTRB(x0, top, x1 + 1, bottom + 1);
    }
}
