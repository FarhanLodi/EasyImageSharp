using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Records the distinct colours of an image up to a capacity, in first-seen order. Images that stay within the
/// capacity are quantized losslessly by using their colours as the palette; the tracker gives up (and stops
/// costing anything) as soon as one more colour is seen.
/// </summary>
internal sealed class DistinctColorTracker
{
    private readonly int capacity;
    private readonly HashSet<uint> seen;
    private readonly List<Rgba32> colors;

    public DistinctColorTracker(int capacity)
    {
        this.capacity = capacity;
        this.seen = new HashSet<uint>(capacity);
        this.colors = new List<Rgba32>(capacity);
    }

    /// <summary>True once more than <c>capacity</c> distinct colours were seen; the tracker then ignores further input.</summary>
    public bool Overflowed { get; private set; }

    public IReadOnlyList<Rgba32> Colors => this.colors;

    /// <summary>Adds the colours of a row; pixels with alpha below <paramref name="alphaCutoff"/> are skipped (they share one transparent entry).</summary>
    public void Add(ReadOnlySpan<Rgba32> row, byte alphaCutoff)
    {
        if (this.Overflowed)
        {
            return;
        }

        uint last = 0;
        bool haveLast = false;
        for (int i = 0; i < row.Length; i++)
        {
            Rgba32 p = row[i];
            if (p.A < alphaCutoff)
            {
                continue;
            }

            uint key = Pack(p);
            if (haveLast && key == last)
            {
                continue; // Runs of one colour are common; skip the hash probe.
            }

            last = key;
            haveLast = true;
            if (this.seen.Add(key))
            {
                if (this.colors.Count == this.capacity)
                {
                    this.Overflowed = true;
                    this.seen.Clear();
                    this.colors.Clear();
                    return;
                }

                this.colors.Add(p);
            }
        }
    }

    private static uint Pack(Rgba32 p) => (uint)(p.R | (p.G << 8) | (p.B << 16) | (p.A << 24));
}
