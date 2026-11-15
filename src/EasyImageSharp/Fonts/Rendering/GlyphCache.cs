namespace EasyImageSharp.Fonts.Rendering;

/// <summary>
/// A process-wide, thread-safe, size-bounded cache of rendered glyph bitmaps keyed by face, glyph, pixel size,
/// subpixel x offset and outline width. Least recently used bitmaps are evicted once the cached coverage exceeds
/// <see cref="MaxBytes"/>; bitmaps larger than an eighth of that are rendered on every use instead of cached.
/// </summary>
internal static class GlyphCache
{
    /// <summary>The number of horizontal subpixel positions glyphs are rendered at (quarter pixels).</summary>
    public const int SubpixelSteps = 4;

    /// <summary>Upper bound on the coverage bytes held by the cache.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Map = new();
    private static readonly LinkedList<Entry> Recency = new();
    private static long bytes;

    /// <summary>The coverage bytes currently cached (for tests).</summary>
    internal static long CachedBytes
    {
        get
        {
            lock (Gate)
            {
                return bytes;
            }
        }
    }

    /// <summary>The number of bitmaps currently cached (for tests).</summary>
    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Map.Count;
            }
        }
    }

    /// <summary>Returns the bitmap of a glyph, rendering and caching it on a miss.</summary>
    /// <param name="face">The face.</param>
    /// <param name="glyph">The glyph index.</param>
    /// <param name="sizeInPixels">The em size in pixels.</param>
    /// <param name="subpixel">The horizontal subpixel position, 0 to <see cref="SubpixelSteps"/> - 1.</param>
    /// <param name="outlineWidth">0 for the glyph itself, or the halo radius in pixels for an outline.</param>
    public static GlyphBitmap Get(FontFace face, int glyph, float sizeInPixels, int subpixel, float outlineWidth)
    {
        var key = new Key(face.Id, glyph, sizeInPixels, (byte)subpixel, outlineWidth);
        lock (Gate)
        {
            if (Map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                Recency.Remove(node);
                Recency.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        GlyphOutline outline = face.GetOutline(glyph);
        float scale = sizeInPixels / face.UnitsPerEm;
        GlyphBitmap bitmap = GlyphRasterizer.Render(outline, scale, (float)subpixel / SubpixelSteps, outlineWidth);
        long size = bitmap.Coverage.Length + 64L;
        if (size > MaxBytes / 8)
        {
            return bitmap;
        }

        lock (Gate)
        {
            if (Map.TryGetValue(key, out LinkedListNode<Entry>? raced))
            {
                return raced.Value.Bitmap;
            }

            Map[key] = Recency.AddFirst(new Entry(key, bitmap, size));
            bytes += size;
            while (bytes > MaxBytes && Recency.Last is LinkedListNode<Entry> last)
            {
                Recency.RemoveLast();
                Map.Remove(last.Value.Key);
                bytes -= last.Value.Size;
            }
        }

        return bitmap;
    }

    /// <summary>Empties the cache (for tests).</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Recency.Clear();
            bytes = 0;
        }
    }

    private readonly record struct Key(int FaceId, int Glyph, float SizeInPixels, byte Subpixel, float OutlineWidth);

    private sealed record Entry(Key Key, GlyphBitmap Bitmap, long Size);
}
