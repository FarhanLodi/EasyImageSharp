using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Grey-level morphology on byte planes. Rectangular elements run as two separable van Herk / Gil-Werman
/// passes (three comparisons per pixel regardless of size); ellipses and other elements whose rows are single
/// runs use one van Herk pass per element row; arbitrary masks fall back to a direct scan. Pixels outside the
/// image are ignored (they never contribute to a minimum or maximum), so the result equals a scan over the
/// in-bounds part of the neighbourhood.
/// </summary>
internal static class MorphologyOps
{
    public static byte[] Erode(byte[] plane, int width, int height, StructuringElement element)
        => Filter(plane, width, height, element, isMin: true);

    public static byte[] Dilate(byte[] plane, int width, int height, StructuringElement element)
        => Filter(plane, width, height, element, isMin: false);

    public static byte[] Open(byte[] plane, int width, int height, StructuringElement element)
        => Dilate(Erode(plane, width, height, element), width, height, element);

    public static byte[] Close(byte[] plane, int width, int height, StructuringElement element)
        => Erode(Dilate(plane, width, height, element), width, height, element);

    /// <summary>Applies one of the six operators to a frame's luminance and writes the grey result back.</summary>
    public static void ApplyToFrame<TPixel>(ImageFrame<TPixel> frame, StructuringElement element, MorphologyKind kind)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] plane = DocumentPlanes.Luminance(frame);
        byte[] result;
        switch (kind)
        {
            case MorphologyKind.Erode:
                result = Erode(plane, width, height, element);
                break;
            case MorphologyKind.Dilate:
                result = Dilate(plane, width, height, element);
                break;
            case MorphologyKind.Open:
                result = Open(plane, width, height, element);
                break;
            case MorphologyKind.Close:
                result = Close(plane, width, height, element);
                break;
            case MorphologyKind.TopHat:
            {
                byte[] opened = Open(plane, width, height, element);
                result = new byte[plane.Length];
                for (int i = 0; i < plane.Length; i++)
                {
                    result[i] = (byte)(plane[i] - opened[i]);
                }

                break;
            }

            default:
            {
                byte[] closed = Close(plane, width, height, element);
                result = new byte[plane.Length];
                for (int i = 0; i < plane.Length; i++)
                {
                    result[i] = (byte)(closed[i] - plane[i]);
                }

                break;
            }
        }

        DocumentPlanes.WriteGray(frame, result);
    }

    public enum MorphologyKind
    {
        Erode,
        Dilate,
        Open,
        Close,
        TopHat,
        BlackHat,
    }

    private static byte[] Filter(byte[] src, int width, int height, StructuringElement element, bool isMin)
    {
        if (element.Width == 1 && element.Height == 1)
        {
            return (byte[])src.Clone();
        }

        if (element.Shape == StructuringElementShape.Rectangle)
        {
            byte[] horizontal = FilterRows(src, width, height, element.AnchorX, element.AnchorX, isMin);
            return FilterColumns(horizontal, width, height, element.AnchorY, element.AnchorY, isMin);
        }

        if (element.Shape == StructuringElementShape.Cross)
        {
            byte[] horizontal = FilterRows(src, width, height, element.AnchorX, element.AnchorX, isMin);
            byte[] vertical = FilterColumns(src, width, height, element.AnchorY, element.AnchorY, isMin);
            var combined = new byte[src.Length];
            for (int i = 0; i < combined.Length; i++)
            {
                combined[i] = isMin ? Math.Min(horizontal[i], vertical[i]) : Math.Max(horizontal[i], vertical[i]);
            }

            return combined;
        }

        if (element.TryGetRowRuns(out int[] starts, out int[] ends))
        {
            return FilterRowRuns(src, width, height, element, starts, ends, isMin);
        }

        return FilterNaive(src, width, height, element, isMin);
    }

    /// <summary>1D filter of every row with the asymmetric window [x - left, x + right].</summary>
    public static byte[] FilterRows(byte[] src, int width, int height, int left, int right, bool isMin)
    {
        var dst = new byte[src.Length];
        int k = left + right + 1;
        int padded = PaddedLength(width, k);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var f = new byte[padded];
            var g = new byte[padded];
            var h = new byte[padded];
            for (int y = start; y < end; y++)
            {
                Filter1D(src.AsSpan(y * width, width), dst.AsSpan(y * width, width), left, right, isMin, f, g, h);
            }
        });
        return dst;
    }

    /// <summary>1D filter of every column with the asymmetric window [y - up, y + down].</summary>
    public static byte[] FilterColumns(byte[] src, int width, int height, int up, int down, bool isMin)
    {
        var dst = new byte[src.Length];
        int k = up + down + 1;
        int padded = PaddedLength(height, k);
        ParallelRowIterator.IterateRows(height, width, (start, end) =>
        {
            var column = new byte[height];
            var result = new byte[height];
            var f = new byte[padded];
            var g = new byte[padded];
            var h = new byte[padded];
            for (int x = start; x < end; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    column[y] = src[(y * width) + x];
                }

                Filter1D(column, result, up, down, isMin, f, g, h);
                for (int y = 0; y < height; y++)
                {
                    dst[(y * width) + x] = result[y];
                }
            }
        });
        return dst;
    }

    private static byte[] FilterRowRuns(byte[] src, int width, int height, StructuringElement element, int[] starts, int[] ends, bool isMin)
    {
        var dst = new byte[src.Length];
        int anchorX = element.AnchorX;
        int anchorY = element.AnchorY;
        int kh = element.Height;
        byte identity = isMin ? byte.MaxValue : byte.MinValue;

        // PaddedLength is not monotonic in k, so the widest element row does not necessarily need the largest
        // scratch buffer: size it from the longest buffer any row will actually ask for.
        int padded = width;
        for (int j = 0; j < kh; j++)
        {
            if (starts[j] >= 0)
            {
                padded = Math.Max(padded, PaddedLength(width, ends[j] - starts[j] + 1));
            }
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var f = new byte[padded];
            var g = new byte[padded];
            var h = new byte[padded];
            var temp = new byte[width];
            for (int y = start; y < end; y++)
            {
                Span<byte> outRow = dst.AsSpan(y * width, width);
                outRow.Fill(identity);
                for (int j = 0; j < kh; j++)
                {
                    if (starts[j] < 0)
                    {
                        continue;
                    }

                    int sy = y + j - anchorY;
                    if ((uint)sy >= (uint)height)
                    {
                        continue;
                    }

                    int left = anchorX - starts[j];
                    int right = ends[j] - anchorX;
                    Filter1D(src.AsSpan(sy * width, width), temp, left, right, isMin, f, g, h);
                    if (isMin)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (temp[x] < outRow[x])
                            {
                                outRow[x] = temp[x];
                            }
                        }
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (temp[x] > outRow[x])
                            {
                                outRow[x] = temp[x];
                            }
                        }
                    }
                }
            }
        });
        return dst;
    }

    private static byte[] FilterNaive(byte[] src, int width, int height, StructuringElement element, bool isMin)
    {
        var dst = new byte[src.Length];
        int anchorX = element.AnchorX;
        int anchorY = element.AnchorY;
        int kw = element.Width;
        int kh = element.Height;
        var offsets = new List<(int Dx, int Dy)>();
        for (int j = 0; j < kh; j++)
        {
            for (int i = 0; i < kw; i++)
            {
                if (element[i, j])
                {
                    offsets.Add((i - anchorX, j - anchorY));
                }
            }
        }

        (int Dx, int Dy)[] taps = offsets.ToArray();
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int best = isMin ? 255 : 0;
                    foreach ((int dx, int dy) in taps)
                    {
                        int sx = x + dx;
                        int sy = y + dy;
                        if ((uint)sx < (uint)width && (uint)sy < (uint)height)
                        {
                            int v = src[(sy * width) + sx];
                            best = isMin ? Math.Min(best, v) : Math.Max(best, v);
                        }
                    }

                    dst[(y * width) + x] = (byte)best;
                }
            }
        });
        return dst;
    }

    private static int PaddedLength(int n, int k) => ((n + k - 1 + k - 1) / k) * k;

    /// <summary>
    /// van Herk / Gil-Werman running min/max with window [i - left, i + right]. Out-of-range positions hold
    /// the identity element so they never win. Scratch buffers must be at least <see cref="PaddedLength"/> long.
    /// </summary>
    private static void Filter1D(ReadOnlySpan<byte> src, Span<byte> dst, int left, int right, bool isMin, byte[] f, byte[] g, byte[] h)
    {
        int n = src.Length;
        int k = left + right + 1;
        if (k == 1)
        {
            src.CopyTo(dst);
            return;
        }

        int m = PaddedLength(n, k);
        byte identity = isMin ? byte.MaxValue : byte.MinValue;

        // f: source shifted right by `left`, padded with the identity.
        f.AsSpan(0, m).Fill(identity);
        src.CopyTo(f.AsSpan(left, n));

        // g: prefix op within blocks of k; h: suffix op within blocks of k.
        for (int b = 0; b < m; b += k)
        {
            g[b] = f[b];
            for (int j = b + 1; j < b + k; j++)
            {
                byte v = f[j];
                byte prev = g[j - 1];
                g[j] = isMin ? (v < prev ? v : prev) : (v > prev ? v : prev);
            }

            int last = b + k - 1;
            h[last] = f[last];
            for (int j = last - 1; j >= b; j--)
            {
                byte v = f[j];
                byte next = h[j + 1];
                h[j] = isMin ? (v < next ? v : next) : (v > next ? v : next);
            }
        }

        // Output i covers padded [i, i + k - 1].
        for (int i = 0; i < n; i++)
        {
            byte a = h[i];
            byte c = g[i + k - 1];
            dst[i] = isMin ? (a < c ? a : c) : (a > c ? a : c);
        }
    }

    // ----- Binary helpers used by the cleanup operators -----

    /// <summary>Zhang-Suen thinning of an ink mask (1 = foreground); returns a new mask.</summary>
    public static byte[] ZhangSuenThin(byte[] mask, int width, int height)
    {
        var img = (byte[])mask.Clone();
        var toClear = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int pass = 0; pass < 2; pass++)
            {
                toClear.Clear();
                for (int y = 1; y < height - 1; y++)
                {
                    int row = y * width;
                    for (int x = 1; x < width - 1; x++)
                    {
                        int idx = row + x;
                        if (img[idx] == 0)
                        {
                            continue;
                        }

                        int p2 = img[idx - width];
                        int p3 = img[idx - width + 1];
                        int p4 = img[idx + 1];
                        int p5 = img[idx + width + 1];
                        int p6 = img[idx + width];
                        int p7 = img[idx + width - 1];
                        int p8 = img[idx - 1];
                        int p9 = img[idx - width - 1];
                        int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                        if (b < 2 || b > 6)
                        {
                            continue;
                        }

                        int a = 0;
                        if (p2 == 0 && p3 == 1) a++;
                        if (p3 == 0 && p4 == 1) a++;
                        if (p4 == 0 && p5 == 1) a++;
                        if (p5 == 0 && p6 == 1) a++;
                        if (p6 == 0 && p7 == 1) a++;
                        if (p7 == 0 && p8 == 1) a++;
                        if (p8 == 0 && p9 == 1) a++;
                        if (p9 == 0 && p2 == 1) a++;
                        if (a != 1)
                        {
                            continue;
                        }

                        bool condition = pass == 0
                            ? (p2 * p4 * p6 == 0) && (p4 * p6 * p8 == 0)
                            : (p2 * p4 * p8 == 0) && (p2 * p6 * p8 == 0);
                        if (condition)
                        {
                            toClear.Add(idx);
                        }
                    }
                }

                if (toClear.Count > 0)
                {
                    changed = true;
                    foreach (int idx in toClear)
                    {
                        img[idx] = 0;
                    }
                }
            }
        }

        // Pixels on the outermost row/column have no full 8-neighbourhood and are left as they are.
        return img;
    }
}
