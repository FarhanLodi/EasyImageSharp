using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Octree colour quantizer (Gervautz–Purgathofer): colours are inserted into an eight-level tree indexed by one
/// bit of each channel per level; whenever the leaf count exceeds the colour budget the deepest reducible node
/// with the fewest pixels merges its children. Images with no more colours than the budget are reproduced
/// exactly. Alpha is handled by thresholding: pixels below <see cref="QuantizerOptions.TransparencyThreshold"/>
/// share one transparent entry.
/// </summary>
public sealed class OctreeQuantizer : IQuantizer
{
    /// <summary>Creates an octree quantizer with default options (256 colours, Floyd–Steinberg dithering).</summary>
    public OctreeQuantizer()
        : this(new QuantizerOptions())
    {
    }

    /// <summary>Creates an octree quantizer with the given options.</summary>
    public OctreeQuantizer(QuantizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Options = options;
    }

    public QuantizerOptions Options { get; }

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => new OctreeFrameQuantizer<TPixel>(this.Options);

    public IQuantizer<TPixel> CreatePixelSpecificQuantizer<TPixel>(QuantizerOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        return new OctreeFrameQuantizer<TPixel>(options);
    }

    private sealed class OctreeFrameQuantizer<TPixel> : QuantizerBase<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly Octree tree;

        public OctreeFrameQuantizer(QuantizerOptions options)
            : base(options, fixedPalette: false)
            => this.tree = new Octree(options.MaxColors);

        protected override bool AccumulateColors(ImageFrame<TPixel> frame, Rectangle bounds)
        {
            byte cutoff = this.AlphaCutoff;
            var row = new Rgba32[bounds.Width];
            bool transparent = false;
            for (int y = bounds.Y; y < bounds.Bottom; y++)
            {
                ConvertRow(frame, y, bounds, row);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    if (p.A < cutoff)
                    {
                        transparent = true;
                        continue;
                    }

                    this.tree.AddColor(p.R, p.G, p.B);
                }
            }

            return transparent;
        }

        protected override Rgba32[] BuildPaletteCore(int maxColors) => this.tree.BuildPalette(maxColors);
    }

    /// <summary>The tree itself: eight levels below the root, reducible-node lists per level, running leaf count.</summary>
    private sealed class Octree
    {
        private const int MaxDepth = 8;

        private readonly Node root = new(0);
        private readonly Node?[] reducible = new Node?[MaxDepth];
        private readonly int maxLeaves;
        private int leafCount;

        public Octree(int maxLeaves) => this.maxLeaves = maxLeaves;

        public void AddColor(int r, int g, int b)
        {
            Node node = this.root;
            node.PixelCount++;
            for (int level = 0; level < MaxDepth; level++)
            {
                int shift = 7 - level;
                int index = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
                Node? child = node.Children[index];
                if (child is null)
                {
                    child = new Node(level + 1);
                    node.Children[index] = child;
                    if (level + 1 == MaxDepth)
                    {
                        child.IsLeaf = true;
                        this.leafCount++;
                    }
                    else
                    {
                        child.NextReducible = this.reducible[level + 1];
                        this.reducible[level + 1] = child;
                    }
                }

                child.PixelCount++;
                if (child.IsLeaf)
                {
                    child.SumR += r;
                    child.SumG += g;
                    child.SumB += b;
                    break;
                }

                node = child;
            }

            while (this.leafCount > this.maxLeaves && this.Reduce())
            {
            }
        }

        public Rgba32[] BuildPalette(int maxColors)
        {
            while (this.leafCount > maxColors && this.Reduce())
            {
            }

            var palette = new List<Rgba32>(this.leafCount);
            Collect(this.root, palette);
            return palette.ToArray();
        }

        /// <summary>
        /// Merges the children of the deepest reducible node with the fewest pixels into that node. Returns false
        /// when nothing can be merged any more: every node below the root is already a leaf, so the tree cannot
        /// hold fewer colours than the number of occupied top-level octants (at most eight) without collapsing
        /// entirely. The caller then keeps what it has and the palette is trimmed to the requested budget.
        /// </summary>
        private bool Reduce()
        {
            int level = MaxDepth - 1;
            while (level > 0 && this.reducible[level] is null)
            {
                level--;
            }

            Node? previousOfBest = null;
            Node? best = null;
            Node? previous = null;
            for (Node? candidate = this.reducible[level]; candidate is not null; candidate = candidate.NextReducible)
            {
                if (best is null || candidate.PixelCount < best.PixelCount)
                {
                    best = candidate;
                    previousOfBest = previous;
                }

                previous = candidate;
            }

            if (best is null)
            {
                return false; // Only the root remains: nothing left to merge.
            }

            if (previousOfBest is null)
            {
                this.reducible[level] = best.NextReducible;
            }
            else
            {
                previousOfBest.NextReducible = best.NextReducible;
            }

            best.NextReducible = null;
            int merged = 0;
            for (int i = 0; i < best.Children.Length; i++)
            {
                Node? child = best.Children[i];
                if (child is null)
                {
                    continue;
                }

                best.SumR += child.SumR;
                best.SumG += child.SumG;
                best.SumB += child.SumB;
                merged++;
                best.Children[i] = null;
            }

            best.IsLeaf = true;
            this.leafCount -= merged - 1;
            return true;
        }

        private static void Collect(Node node, List<Rgba32> palette)
        {
            if (node.IsLeaf)
            {
                long count = Math.Max(1, node.PixelCount);
                palette.Add(new Rgba32(
                    (byte)((node.SumR + (count / 2)) / count),
                    (byte)((node.SumG + (count / 2)) / count),
                    (byte)((node.SumB + (count / 2)) / count)));
                return;
            }

            foreach (Node? child in node.Children)
            {
                if (child is not null)
                {
                    Collect(child, palette);
                }
            }
        }

        private sealed class Node
        {
            public readonly Node?[] Children = new Node?[8];
            public readonly int Level;
            public bool IsLeaf;
            public long PixelCount;
            public long SumR;
            public long SumG;
            public long SumB;
            public Node? NextReducible;

            public Node(int level) => this.Level = level;
        }
    }
}
