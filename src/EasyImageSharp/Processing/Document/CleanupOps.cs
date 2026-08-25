using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Component- and morphology-based cleanup of document images. All operators classify ink as luminance below
/// 128, paint the pixels they remove white (or black when filling) and leave every other pixel untouched, so
/// they work on grey scans as well as on binary images.
/// </summary>
internal static class CleanupOps
{
    // ----- Component filters -----

    public static void RemoveSmallObjects<TPixel>(ImageFrame<TPixel> frame, int minArea)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (minArea <= 1)
        {
            return;
        }

        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] remove = SelectComponents(mask, frame.Width, frame.Height, Connectivity.Eight, stats => stats.Area < minArea);
        DocumentPlanes.PaintWhere(frame, remove, Rgba32.White);
    }

    public static void KeepLargestComponent<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] mask = DocumentPlanes.InkMask(frame);
        int[] labels = ConnectedComponents.LabelMask(mask, frame.Width, frame.Height, Connectivity.Eight, out ComponentStats[] stats);
        if (stats.Length == 0)
        {
            return;
        }

        int largest = 0;
        for (int i = 1; i < stats.Length; i++)
        {
            if (stats[i].Area > stats[largest].Area)
            {
                largest = i;
            }
        }

        int keep = stats[largest].Label;
        var remove = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            remove[i] = labels[i] != 0 && labels[i] != keep ? (byte)1 : (byte)0;
        }

        DocumentPlanes.PaintWhere(frame, remove, Rgba32.White);
    }

    /// <summary>Fills background regions (4-connected) that do not reach the image border with black.</summary>
    public static void FillHoles<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] holes = Holes(mask, width, height);
        DocumentPlanes.PaintWhere(frame, holes, Rgba32.Black);
    }

    /// <summary>Mask of background pixels not 4-connected to the border.</summary>
    public static byte[] Holes(byte[] mask, int width, int height)
    {
        var background = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            background[i] = mask[i] == 0 ? (byte)1 : (byte)0;
        }

        int[] labels = ConnectedComponents.LabelMask(background, width, height, Connectivity.Four, out ComponentStats[] stats);
        var touchesBorder = new bool[stats.Length + 1];
        foreach (ComponentStats s in stats)
        {
            Rectangle b = s.Bounds;
            touchesBorder[s.Label] = b.X == 0 || b.Y == 0 || b.Right == width || b.Bottom == height;
        }

        var holes = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            int label = labels[i];
            holes[i] = label != 0 && !touchesBorder[label] ? (byte)1 : (byte)0;
        }

        return holes;
    }

    /// <summary>Removes ink components and background components (holes) with at most <paramref name="maxArea"/> pixels.</summary>
    public static void Despeckle<TPixel>(ImageFrame<TPixel> frame, int maxArea)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (maxArea <= 0)
        {
            return;
        }

        int width = frame.Width;
        int height = frame.Height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] blackSpecks = SelectComponents(mask, width, height, Connectivity.Eight, stats => stats.Area <= maxArea);
        var inverse = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            inverse[i] = mask[i] == 0 ? (byte)1 : (byte)0;
        }

        byte[] whiteSpecks = SelectComponents(inverse, width, height, Connectivity.Four, stats => stats.Area <= maxArea);
        DocumentPlanes.PaintWhere(frame, blackSpecks, Rgba32.White);
        DocumentPlanes.PaintWhere(frame, whiteSpecks, Rgba32.Black);
    }

    /// <summary>Returns a mask of the pixels of every component accepted by <paramref name="predicate"/>.</summary>
    public static byte[] SelectComponents(byte[] mask, int width, int height, Connectivity connectivity, Func<ComponentStats, bool> predicate)
    {
        int[] labels = ConnectedComponents.LabelMask(mask, width, height, connectivity, out ComponentStats[] stats);
        var selected = new bool[stats.Length + 1];
        bool any = false;
        foreach (ComponentStats s in stats)
        {
            if (predicate(s))
            {
                selected[s.Label] = true;
                any = true;
            }
        }

        var result = new byte[mask.Length];
        if (!any)
        {
            return result;
        }

        for (int i = 0; i < mask.Length; i++)
        {
            result[i] = selected[labels[i]] ? (byte)1 : (byte)0;
        }

        return result;
    }

    // ----- Rules -----

    /// <summary>
    /// Removes long horizontal and/or vertical rules: the ink mask is closed across the rule direction by one
    /// pixel (so slightly stair-stepped rules survive), opened with a 1 x <paramref name="minLength"/> (or
    /// <paramref name="minLength"/> x 1) run so only long straight runs remain, and those pixels are erased.
    /// Text strokes crossing a rule are repaired: within a rule, columns (rows) that carry ink immediately
    /// above and below (left and right of) the rule band keep their pixels.
    /// </summary>
    public static void RemoveLines<TPixel>(ImageFrame<TPixel> frame, int minLength, LineOrientation orientation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(minLength, nameof(minLength));
        int width = frame.Width;
        int height = frame.Height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        var erase = new byte[mask.Length];

        if (orientation is LineOrientation.Horizontal or LineOrientation.Both)
        {
            byte[] lines = LongRuns(mask, width, height, minLength, horizontal: true);
            RepairCrossings(mask, lines, width, height, horizontal: true);
            Or(erase, lines);
        }

        if (orientation is LineOrientation.Vertical or LineOrientation.Both)
        {
            byte[] lines = LongRuns(mask, width, height, minLength, horizontal: false);
            RepairCrossings(mask, lines, width, height, horizontal: false);
            Or(erase, lines);
        }

        DocumentPlanes.PaintWhere(frame, erase, Rgba32.White);
    }

    /// <summary>Mask of ink pixels that belong to straight runs of at least <paramref name="minLength"/> pixels.</summary>
    internal static byte[] LongRuns(byte[] mask, int width, int height, int minLength, bool horizontal)
    {
        int half = minLength / 2;
        int rest = minLength - 1 - half;

        // Bridge one-pixel stair steps across the run direction, then keep only long runs.
        byte[] bridged = horizontal
            ? MorphologyOps.FilterColumns(mask, width, height, 1, 1, isMin: false)
            : MorphologyOps.FilterRows(mask, width, height, 1, 1, isMin: false);
        byte[] eroded = horizontal
            ? MorphologyOps.FilterRows(bridged, width, height, half, rest, isMin: true)
            : MorphologyOps.FilterColumns(bridged, width, height, half, rest, isMin: true);
        byte[] opened = horizontal
            ? MorphologyOps.FilterRows(eroded, width, height, rest, half, isMin: false)
            : MorphologyOps.FilterColumns(eroded, width, height, rest, half, isMin: false);

        // Only original ink counts as a rule pixel.
        var lines = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            lines[i] = (byte)(opened[i] & mask[i]);
        }

        TrimToCore(lines, width, height, horizontal);
        return lines;
    }

    /// <summary>
    /// The bridged band is one pixel wider than the rule on each side, so ink touching a rule from one side is
    /// flagged too. Per rule component, rows (columns) that carry fewer than half of the component's densest
    /// row (column) are not part of the rule and are released.
    /// </summary>
    private static void TrimToCore(byte[] lines, int width, int height, bool horizontal)
    {
        int[] labels = ConnectedComponents.LabelMask(lines, width, height, Connectivity.Eight, out ComponentStats[] stats);
        foreach (ComponentStats s in stats)
        {
            Rectangle b = s.Bounds;
            int bins = horizontal ? b.Height : b.Width;
            if (bins <= 1)
            {
                continue;
            }

            var counts = new int[bins];
            for (int y = b.Y; y < b.Bottom; y++)
            {
                int row = y * width;
                for (int x = b.X; x < b.Right; x++)
                {
                    if (labels[row + x] == s.Label)
                    {
                        counts[horizontal ? y - b.Y : x - b.X]++;
                    }
                }
            }

            int max = counts.Max();
            for (int y = b.Y; y < b.Bottom; y++)
            {
                int row = y * width;
                for (int x = b.X; x < b.Right; x++)
                {
                    int bin = horizontal ? y - b.Y : x - b.X;
                    if (labels[row + x] == s.Label && counts[bin] * 2 < max)
                    {
                        lines[row + x] = 0;
                    }
                }
            }
        }
    }

    /// <summary>Clears rule pixels where a text stroke crosses the rule so the stroke stays intact.</summary>
    private static void RepairCrossings(byte[] mask, byte[] lines, int width, int height, bool horizontal)
    {
        const int Reach = 2;
        if (horizontal)
        {
            for (int x = 0; x < width; x++)
            {
                int y = 0;
                while (y < height)
                {
                    if (lines[(y * width) + x] == 0)
                    {
                        y++;
                        continue;
                    }

                    int y0 = y;
                    while (y < height && lines[(y * width) + x] != 0)
                    {
                        y++;
                    }

                    int y1 = y - 1;
                    if (HasInk(mask, width, x, y0 - Reach, y0 - 1, vertical: true) && HasInk(mask, width, x, y1 + 1, y1 + Reach, vertical: true, limit: height))
                    {
                        for (int yy = y0; yy <= y1; yy++)
                        {
                            lines[(yy * width) + x] = 0;
                        }
                    }
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int x = 0;
                while (x < width)
                {
                    if (lines[row + x] == 0)
                    {
                        x++;
                        continue;
                    }

                    int x0 = x;
                    while (x < width && lines[row + x] != 0)
                    {
                        x++;
                    }

                    int x1 = x - 1;
                    if (HasInk(mask, width, y, x0 - Reach, x0 - 1, vertical: false) && HasInk(mask, width, y, x1 + 1, x1 + Reach, vertical: false, limit: width))
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            lines[row + xx] = 0;
                        }
                    }
                }
            }
        }
    }

    private static bool HasInk(byte[] mask, int width, int fixedCoord, int from, int to, bool vertical, int limit = int.MaxValue)
    {
        for (int i = Math.Max(0, from); i <= to && i < limit; i++)
        {
            int idx = vertical ? (i * width) + fixedCoord : (fixedCoord * width) + i;
            if (idx >= 0 && idx < mask.Length && mask[idx] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void Or(byte[] target, byte[] source)
    {
        for (int i = 0; i < target.Length; i++)
        {
            target[i] |= source[i];
        }
    }

    // ----- Hole punches, margins, borders -----

    /// <summary>
    /// Removes filled disks that look like hole punches: 8-connected ink components whose diameter is 1-8 % of
    /// the smaller image dimension (at least 6 px), whose bounding box is nearly square (aspect 0.75-1.33), whose
    /// area is 80-115 % of the inscribed disk's area (a filled square is 127 %, a ring far less), whose four
    /// bounding-box corners are background, and whose centre lies within 20 % of an image edge.
    /// </summary>
    public static void RemoveHolePunches<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int minDim = Math.Min(width, height);
        double minDiameter = Math.Max(6, minDim * 0.01);
        double maxDiameter = Math.Max(minDiameter + 2, minDim * 0.08);
        double marginX = width * 0.20;
        double marginY = height * 0.20;

        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] remove = SelectComponents(mask, width, height, Connectivity.Eight, stats =>
        {
            Rectangle b = stats.Bounds;
            double diameter = Math.Max(b.Width, b.Height);
            if (diameter < minDiameter || diameter > maxDiameter)
            {
                return false;
            }

            double aspect = (double)b.Width / b.Height;
            if (aspect < 0.75 || aspect > 1.33)
            {
                return false;
            }

            double diskArea = Math.PI * (b.Width / 2.0) * (b.Height / 2.0);
            double fill = stats.Area / diskArea;
            if (fill < 0.8 || fill > 1.15)
            {
                return false;
            }

            if (mask[(b.Y * width) + b.X] != 0 || mask[(b.Y * width) + b.Right - 1] != 0
                || mask[((b.Bottom - 1) * width) + b.X] != 0 || mask[((b.Bottom - 1) * width) + b.Right - 1] != 0)
            {
                return false;
            }

            PointF c = stats.Centroid;
            return c.X < marginX || c.X > width - marginX || c.Y < marginY || c.Y > height - marginY;
        });
        DocumentPlanes.PaintWhere(frame, remove, Rgba32.White);
    }

    /// <summary>
    /// Removes ink components that touch the image border, or that lie entirely inside a thin margin band
    /// (3 % of each dimension, 4-40 px) and are not text-sized (larger than 0.05 % of the image or longer than
    /// 20 % of a dimension); small specks fully inside the band are removed as well.
    /// </summary>
    public static void RemoveMarginNoise<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int bandX = Math.Clamp((int)Math.Round(width * 0.03), 4, 40);
        int bandY = Math.Clamp((int)Math.Round(height * 0.03), 4, 40);
        double bigArea = 0.0005 * width * height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] remove = SelectComponents(mask, width, height, Connectivity.Eight, stats =>
        {
            Rectangle b = stats.Bounds;
            bool touches = b.X == 0 || b.Y == 0 || b.Right == width || b.Bottom == height;
            bool insideBand = b.Right <= bandX || b.X >= width - bandX || b.Bottom <= bandY || b.Y >= height - bandY;
            bool big = stats.Area >= bigArea || b.Width >= width * 0.2 || b.Height >= height * 0.2;
            bool speck = stats.Area <= 4;
            return (touches && (big || speck)) || (insideBand && (big || speck || touches));
        });
        DocumentPlanes.PaintWhere(frame, remove, Rgba32.White);
    }

    /// <summary>
    /// Removes dark scanner borders: ink components that touch the image border and are large (at least 1 % of
    /// the image) or span at least half of a dimension are painted white; text touching the border is kept.
    /// </summary>
    public static void RemoveBorders<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        double bigArea = 0.01 * width * height;
        byte[] mask = DocumentPlanes.InkMask(frame);
        byte[] remove = SelectComponents(mask, width, height, Connectivity.Eight, stats =>
        {
            Rectangle b = stats.Bounds;
            bool touches = b.X == 0 || b.Y == 0 || b.Right == width || b.Bottom == height;
            return touches && (stats.Area >= bigArea || b.Width >= width * 0.5 || b.Height >= height * 0.5);
        });
        DocumentPlanes.PaintWhere(frame, remove, Rgba32.White);
    }

    /// <summary>
    /// Bounds of the page content after trimming rows/columns from each side that are mostly dark
    /// (at least 50 % ink), scanning inwards at most 15 % of each dimension.
    /// </summary>
    public static Rectangle TrimDarkMargins(byte[] luminance, int width, int height)
    {
        int maxTrimX = width * 15 / 100;
        int maxTrimY = height * 15 / 100;
        int left = 0;
        int right = width;
        int top = 0;
        int bottom = height;

        while (left < maxTrimX && ColumnInkFraction(luminance, width, height, left, top, bottom) >= 0.5)
        {
            left++;
        }

        while (right - 1 > width - maxTrimX && ColumnInkFraction(luminance, width, height, right - 1, top, bottom) >= 0.5)
        {
            right--;
        }

        while (top < maxTrimY && RowInkFraction(luminance, width, top, left, right) >= 0.5)
        {
            top++;
        }

        while (bottom - 1 > height - maxTrimY && RowInkFraction(luminance, width, bottom - 1, left, right) >= 0.5)
        {
            bottom--;
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static double ColumnInkFraction(byte[] luminance, int width, int height, int x, int y0, int y1)
    {
        int ink = 0;
        for (int y = y0; y < y1; y++)
        {
            if (luminance[(y * width) + x] < DocumentPlanes.InkThreshold)
            {
                ink++;
            }
        }

        return (double)ink / Math.Max(1, y1 - y0);
    }

    private static double RowInkFraction(byte[] luminance, int width, int y, int x0, int x1)
    {
        int ink = 0;
        int row = y * width;
        for (int x = x0; x < x1; x++)
        {
            if (luminance[row + x] < DocumentPlanes.InkThreshold)
            {
                ink++;
            }
        }

        return (double)ink / Math.Max(1, x1 - x0);
    }
}
