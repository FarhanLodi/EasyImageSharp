using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Connected-component labelling of binary images with a two-pass union-find algorithm. Foreground pixels are
/// the dark ones (luminance below 128, i.e. ink on a white page); the background is label 0 and components are
/// numbered 1..count in scan order of their first pixel.
/// </summary>
public static class ConnectedComponents
{
    /// <summary>
    /// Labels the dark pixels of <paramref name="binary"/>. Returns a row-major label map (0 = background)
    /// and the number of components.
    /// </summary>
    public static int[] Label(ImageFrame<L8> binary, Connectivity connectivity, out int count)
    {
        ArgumentNullException.ThrowIfNull(binary);
        int[] labels = LabelMask(DocumentPlanes.InkMask(binary), binary.Width, binary.Height, connectivity, out ComponentStats[] stats);
        count = stats.Length;
        return labels;
    }

    /// <summary>
    /// Labels the dark pixels of <paramref name="binary"/>. Returns a row-major label map (0 = background)
    /// and per-component statistics (index <c>label - 1</c>).
    /// </summary>
    public static int[] Label(ImageFrame<L8> binary, Connectivity connectivity, out ComponentStats[] components)
    {
        ArgumentNullException.ThrowIfNull(binary);
        return LabelMask(DocumentPlanes.InkMask(binary), binary.Width, binary.Height, connectivity, out components);
    }

    /// <summary>Labels the dark pixels (luminance below 128) of any frame; see <see cref="Label(ImageFrame{L8}, Connectivity, out ComponentStats[])"/>.</summary>
    public static int[] Label<TPixel>(ImageFrame<TPixel> frame, Connectivity connectivity, out ComponentStats[] components)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(frame);
        return LabelMask(DocumentPlanes.InkMask(frame), frame.Width, frame.Height, connectivity, out components);
    }

    /// <summary>Labels the root frame of an image; see <see cref="Label(ImageFrame{L8}, Connectivity, out ComponentStats[])"/>.</summary>
    public static int[] Label<TPixel>(Image<TPixel> image, Connectivity connectivity, out ComponentStats[] components)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        return Label(image.Frames.RootFrame, connectivity, out components);
    }

    /// <summary>
    /// Two-pass union-find labelling of a mask (non-zero = foreground). Labels are 1-based and dense; the
    /// returned statistics array is indexed by <c>label - 1</c>.
    /// </summary>
    internal static int[] LabelMask(byte[] mask, int width, int height, Connectivity connectivity, out ComponentStats[] components)
    {
        if (connectivity != Connectivity.Four && connectivity != Connectivity.Eight)
        {
            throw new ArgumentOutOfRangeException(nameof(connectivity), connectivity, "Connectivity must be Four or Eight.");
        }

        var labels = new int[width * height];
        var parent = new List<int> { 0 };
        bool eight = connectivity == Connectivity.Eight;

        // Pass 1: provisional labels from the already-visited neighbours (W, NW, N, NE).
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0)
                {
                    continue;
                }

                int best = 0;
                int west = x > 0 ? labels[idx - 1] : 0;
                int north = y > 0 ? labels[idx - width] : 0;
                int northWest = eight && y > 0 && x > 0 ? labels[idx - width - 1] : 0;
                int northEast = eight && y > 0 && x < width - 1 ? labels[idx - width + 1] : 0;

                best = MinNonZero(best, west);
                best = MinNonZero(best, north);
                best = MinNonZero(best, northWest);
                best = MinNonZero(best, northEast);

                if (best == 0)
                {
                    parent.Add(parent.Count);
                    labels[idx] = parent.Count - 1;
                    continue;
                }

                labels[idx] = best;
                Union(parent, best, west);
                Union(parent, best, north);
                Union(parent, best, northWest);
                Union(parent, best, northEast);
            }
        }

        // Flatten and renumber densely.
        var dense = new int[parent.Count];
        int count = 0;
        for (int i = 1; i < parent.Count; i++)
        {
            int root = Find(parent, i);
            if (root == i)
            {
                dense[i] = ++count;
            }
        }

        for (int i = 1; i < parent.Count; i++)
        {
            dense[i] = dense[Find(parent, i)];
        }

        // Pass 2: final labels + statistics.
        var area = new int[count + 1];
        var minX = new int[count + 1];
        var minY = new int[count + 1];
        var maxX = new int[count + 1];
        var maxY = new int[count + 1];
        var sumX = new long[count + 1];
        var sumY = new long[count + 1];
        Array.Fill(minX, int.MaxValue);
        Array.Fill(minY, int.MaxValue);
        Array.Fill(maxX, int.MinValue);
        Array.Fill(maxY, int.MinValue);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                int label = labels[idx];
                if (label == 0)
                {
                    continue;
                }

                label = dense[label];
                labels[idx] = label;
                area[label]++;
                sumX[label] += x;
                sumY[label] += y;
                if (x < minX[label]) minX[label] = x;
                if (x > maxX[label]) maxX[label] = x;
                if (y < minY[label]) minY[label] = y;
                if (y > maxY[label]) maxY[label] = y;
            }
        }

        components = new ComponentStats[count];
        for (int label = 1; label <= count; label++)
        {
            components[label - 1] = new ComponentStats(
                label,
                area[label],
                Rectangle.FromLTRB(minX[label], minY[label], maxX[label] + 1, maxY[label] + 1),
                new PointF((float)((double)sumX[label] / area[label]), (float)((double)sumY[label] / area[label])));
        }

        return labels;
    }

    private static int MinNonZero(int current, int candidate)
        => candidate == 0 ? current : current == 0 ? candidate : Math.Min(current, candidate);

    private static int Find(List<int> parent, int i)
    {
        int root = i;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[i] != root)
        {
            int next = parent[i];
            parent[i] = root;
            i = next;
        }

        return root;
    }

    private static void Union(List<int> parent, int a, int b)
    {
        if (b == 0 || a == b)
        {
            return;
        }

        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra == rb)
        {
            return;
        }

        if (ra < rb)
        {
            parent[rb] = ra;
        }
        else
        {
            parent[ra] = rb;
        }
    }
}
