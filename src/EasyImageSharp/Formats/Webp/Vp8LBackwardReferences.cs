namespace EasyImageSharp.Formats.Webp;

/// <summary>A growable list of <see cref="Vp8LToken"/> values, kept as a flat array to avoid per-symbol overhead.</summary>
internal sealed class Vp8LTokenList
{
    private Vp8LToken[] items;

    public Vp8LTokenList(int capacity) => this.items = new Vp8LToken[Math.Max(16, capacity)];

    /// <summary>The number of symbols in the list.</summary>
    public int Count { get; private set; }

    /// <summary>The backing array; only the first <see cref="Count"/> entries are meaningful.</summary>
    public Vp8LToken[] Items => this.items;

    public void Add(in Vp8LToken token)
    {
        if (this.Count == this.items.Length)
        {
            Array.Resize(ref this.items, this.items.Length * 2);
        }

        this.items[this.Count++] = token;
    }

    /// <summary>Builds the histogram of every symbol in the list.</summary>
    public Vp8LHistogram BuildHistogram(int cacheBits)
    {
        var histogram = new Vp8LHistogram(cacheBits);
        for (int i = 0; i < this.Count; i++)
        {
            histogram.Add(this.items[i]);
        }

        return histogram;
    }
}

/// <summary>
/// Finds the LZ77 backward references of a VP8L image: a hash chain over pairs of pixels feeds a greedy
/// (optionally lazy) match search, an alternative run-length-only parse, and — at higher effort levels — a
/// dynamic-programming parse that is driven by the bit costs measured on a first pass. A colour cache is
/// layered on afterwards, which is exact because the cache state depends only on the pixels already emitted.
/// </summary>
internal static class Vp8LBackwardReferences
{
    /// <summary>Shortest backward reference worth coding instead of the equivalent literals.</summary>
    public const int MinCopyLength = 4;

    /// <summary>Longest backward reference the length codes can express.</summary>
    public const int MaxCopyLength = 4096;

    /// <summary>Largest distance the distance codes can express.</summary>
    public const int WindowSize = (1 << 20) - 120;

    private const int HashBits = 18;
    private const int HashSize = 1 << HashBits;

    private static readonly int[] FastCacheSizes = { 0, 8 };
    private static readonly int[] MediumCacheSizes = { 0, 6, 8, 10 };
    private static readonly int[] AllCacheSizes = { 0, 5, 6, 7, 8, 9, 10 };

    /// <summary>Produces the best symbol stream this effort level can find, colour cache included.</summary>
    /// <param name="argb">The transformed pixels of the image.</param>
    /// <param name="width">The coded width, which the distance plane codes are relative to.</param>
    /// <param name="quality">1..100; below 25 the cost-driven parse is skipped even at high effort.</param>
    /// <param name="method">0..6 effort level.</param>
    /// <param name="cacheBits">Receives the colour cache size that was chosen, in bits (0 = no cache).</param>
    public static Vp8LTokenList Compute(uint[] argb, int width, int quality, int method, out int cacheBits)
    {
        int n = argb.Length;
        var chain = new HashChain(n);
        chain.Fill(argb);

        int maxChain = method <= 0 ? 8 : method <= 2 ? 24 : method <= 4 ? 64 : 512;
        bool trace = method >= 4 && quality >= 25;

        // When the shortest-path search follows, the greedy pass only has to supply a cost model, so it can
        // run with a short chain and leave the deep search to the pass that actually decides the parse.
        Vp8LTokenList best = Lz77(argb, width, chain, trace ? Math.Min(maxChain, 16) : maxChain, lazy: method >= 1);
        double bestCost = EstimateCost(best);

        if (method >= 3)
        {
            Vp8LTokenList rle = RunLength(argb, width);
            double rleCost = EstimateCost(rle);
            if (rleCost < bestCost)
            {
                best = rle;
                bestCost = rleCost;
            }
        }

        if (trace)
        {
            Vp8LTokenList traced = CostDrivenParse(argb, width, chain, maxChain, best);
            if (EstimateCost(traced) < bestCost)
            {
                best = traced;
            }
        }

        cacheBits = ChooseCacheBits(best, argb, method);
        return cacheBits > 0 ? ApplyCache(best, argb, cacheBits) : best;
    }

    /// <summary>Rewrites the literals of <paramref name="refs"/> that a colour cache of the given size would hit.</summary>
    public static Vp8LTokenList ApplyCache(Vp8LTokenList refs, uint[] argb, int cacheBits)
    {
        var result = new Vp8LTokenList(refs.Count);
        var cache = new uint[1 << cacheBits];
        var present = new bool[1 << cacheBits];
        int shift = 32 - cacheBits;
        int position = 0;
        Vp8LToken[] items = refs.Items;
        for (int i = 0; i < refs.Count; i++)
        {
            ref Vp8LToken token = ref items[i];
            if (token.Kind == Vp8LTokenKind.Literal)
            {
                uint value = token.Value;
                int key = (int)(unchecked(0x1e35a7bdu * value) >> shift);
                result.Add(present[key] && cache[key] == value ? Vp8LToken.Cache(key) : token);
                cache[key] = value;
                present[key] = true;
                position++;
            }
            else
            {
                result.Add(token);
                int end = position + token.Length;
                for (; position < end; position++)
                {
                    uint value = argb[position];
                    int key = (int)(unchecked(0x1e35a7bdu * value) >> shift);
                    cache[key] = value;
                    present[key] = true;
                }
            }
        }

        return result;
    }

    private static double EstimateCost(Vp8LTokenList refs) => refs.BuildHistogram(0).EstimatedCost();

    /// <summary>Measures the cost of every candidate cache size against the already-chosen symbol stream.</summary>
    private static int ChooseCacheBits(Vp8LTokenList refs, uint[] argb, int method)
    {
        int[] candidates = method <= 1 ? FastCacheSizes : method <= 4 ? MediumCacheSizes : AllCacheSizes;
        int bestBits = 0;
        double bestCost = double.MaxValue;
        foreach (int bits in candidates)
        {
            double cost = MeasureWithCache(refs, argb, bits);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestBits = bits;
            }
        }

        return bestBits;
    }

    private static double MeasureWithCache(Vp8LTokenList refs, uint[] argb, int cacheBits)
    {
        var histogram = new Vp8LHistogram(cacheBits);
        if (cacheBits == 0)
        {
            for (int i = 0; i < refs.Count; i++)
            {
                histogram.Add(refs.Items[i]);
            }

            return histogram.EstimatedCost();
        }

        var cache = new uint[1 << cacheBits];
        var present = new bool[1 << cacheBits];
        int shift = 32 - cacheBits;
        int position = 0;
        Vp8LToken[] items = refs.Items;
        for (int i = 0; i < refs.Count; i++)
        {
            ref Vp8LToken token = ref items[i];
            if (token.Kind == Vp8LTokenKind.Literal)
            {
                uint value = token.Value;
                int key = (int)(unchecked(0x1e35a7bdu * value) >> shift);
                histogram.Add(present[key] && cache[key] == value ? Vp8LToken.Cache(key) : token);
                cache[key] = value;
                present[key] = true;
                position++;
            }
            else
            {
                histogram.Add(token);
                int end = position + token.Length;
                for (; position < end; position++)
                {
                    uint value = argb[position];
                    int key = (int)(unchecked(0x1e35a7bdu * value) >> shift);
                    cache[key] = value;
                    present[key] = true;
                }
            }
        }

        return histogram.EstimatedCost();
    }

    /// <summary>The classic greedy parse with an optional one-pixel lookahead.</summary>
    private static Vp8LTokenList Lz77(uint[] argb, int width, HashChain chain, int maxChain, bool lazy)
    {
        int n = argb.Length;
        var refs = new Vp8LTokenList(n / 2);
        int i = 0;
        while (i < n)
        {
            int maxLength = Math.Min(MaxCopyLength, n - i);
            chain.FindBest(argb, i, maxLength, maxChain, out int length, out int distance);
            if (length >= MinCopyLength)
            {
                if (lazy && i + 1 < n && length < maxLength)
                {
                    chain.FindBest(argb, i + 1, Math.Min(MaxCopyLength, n - i - 1), maxChain, out int nextLength, out int nextDistance);
                    if (nextLength > length + 1)
                    {
                        refs.Add(Vp8LToken.Literal(argb[i]));
                        i++;
                        refs.Add(Vp8LToken.Copy(nextLength, nextDistance, Vp8LPrefix.DistanceToPlaneCode(width, nextDistance)));
                        i += nextLength;
                        continue;
                    }
                }

                refs.Add(Vp8LToken.Copy(length, distance, Vp8LPrefix.DistanceToPlaneCode(width, distance)));
                i += length;
            }
            else
            {
                refs.Add(Vp8LToken.Literal(argb[i]));
                i++;
            }
        }

        return refs;
    }

    /// <summary>A parse that only ever copies from the immediately preceding pixel, which keeps the distance code trivial.</summary>
    private static Vp8LTokenList RunLength(uint[] argb, int width)
    {
        int n = argb.Length;
        var refs = new Vp8LTokenList(n / 2);
        int planeCode = Vp8LPrefix.DistanceToPlaneCode(width, 1);
        int i = 0;
        while (i < n)
        {
            if (i > 0 && argb[i] == argb[i - 1])
            {
                int run = 1;
                while (i + run < n && run < MaxCopyLength && argb[i + run] == argb[i])
                {
                    run++;
                }

                if (run >= MinCopyLength)
                {
                    refs.Add(Vp8LToken.Copy(run, 1, planeCode));
                    i += run;
                    continue;
                }
            }

            refs.Add(Vp8LToken.Literal(argb[i]));
            i++;
        }

        return refs;
    }

    /// <summary>
    /// Re-parses the image with a shortest-path search over the bit costs measured on <paramref name="seed"/>.
    /// Every position may start a literal or one of a handful of truncations of the matches the hash chain
    /// offers, and the cheapest path through the image wins.
    /// </summary>
    private static Vp8LTokenList CostDrivenParse(uint[] argb, int width, HashChain chain, int maxChain, Vp8LTokenList seed)
    {
        int n = argb.Length;
        var model = new CostModel(seed.BuildHistogram(0));
        var cost = new float[n + 1];
        var opLength = new int[n + 1];
        var opDistance = new int[n + 1];
        Array.Fill(cost, float.MaxValue);
        cost[0] = 0;

        Span<int> lengths = stackalloc int[8];
        Span<int> distances = stackalloc int[8];

        for (int i = 0; i < n; i++)
        {
            float here = cost[i];
            if (here == float.MaxValue)
            {
                continue;
            }

            float literal = here + model.Literal(argb[i]);
            if (literal < cost[i + 1])
            {
                cost[i + 1] = literal;
                opLength[i + 1] = 1;
                opDistance[i + 1] = 0;
            }

            int maxLength = Math.Min(MaxCopyLength, n - i);
            if (maxLength < MinCopyLength)
            {
                continue;
            }

            int found = chain.Collect(argb, i, maxLength, maxChain, lengths, distances);
            int previous = MinCopyLength - 1;
            for (int k = 0; k < found; k++)
            {
                int length = lengths[k];
                int distance = distances[k];
                int planeCode = Vp8LPrefix.DistanceToPlaneCode(width, distance);
                int low = Math.Max(MinCopyLength, previous + 1);
                previous = length;
                for (int candidate = low; candidate <= length; candidate++)
                {
                    // Only a few truncations per match are worth testing; the full length always is.
                    if (candidate > low + 2 && candidate != length)
                    {
                        candidate = length - 1;
                        continue;
                    }

                    float total = here + model.Copy(candidate, planeCode);
                    int target = i + candidate;
                    if (total < cost[target])
                    {
                        cost[target] = total;
                        opLength[target] = candidate;
                        opDistance[target] = distance;
                    }
                }
            }
        }

        // Walk the chosen operations back to the start, then replay them forwards.
        var reversed = new List<(int Length, int Distance)>();
        for (int position = n; position > 0;)
        {
            int length = opLength[position];
            int distance = opDistance[position];
            reversed.Add((length, distance));
            position -= length;
        }

        var refs = new Vp8LTokenList(reversed.Count);
        int pixel = 0;
        for (int k = reversed.Count - 1; k >= 0; k--)
        {
            (int length, int distance) = reversed[k];
            if (distance == 0)
            {
                refs.Add(Vp8LToken.Literal(argb[pixel]));
                pixel++;
            }
            else
            {
                refs.Add(Vp8LToken.Copy(length, distance, Vp8LPrefix.DistanceToPlaneCode(width, distance)));
                pixel += length;
            }
        }

        return refs;
    }

    /// <summary>Per-symbol bit costs measured on a first parse, used to drive the shortest-path search.</summary>
    private sealed class CostModel
    {
        private readonly float[] literal;
        private readonly float[] red;
        private readonly float[] blue;
        private readonly float[] alpha;
        private readonly float[] distance;

        public CostModel(Vp8LHistogram histogram)
        {
            this.literal = Estimate(histogram.Literal);
            this.red = Estimate(histogram.Red);
            this.blue = Estimate(histogram.Blue);
            this.alpha = Estimate(histogram.Alpha);
            this.distance = Estimate(histogram.Distance);
        }

        public float Literal(uint argb)
            => this.alpha[(int)(argb >> 24)]
                + this.red[(int)((argb >> 16) & 0xff)]
                + this.literal[(int)((argb >> 8) & 0xff)]
                + this.blue[(int)(argb & 0xff)];

        public float Copy(int length, int planeCode)
        {
            Vp8LPrefix.Encode(length, out int lengthCode, out int lengthExtra, out _);
            Vp8LPrefix.Encode(planeCode, out int distanceCode, out int distanceExtra, out _);
            return this.literal[Vp8LHistogram.NumLiteralCodes + lengthCode] + lengthExtra
                + this.distance[distanceCode] + distanceExtra;
        }

        private static float[] Estimate(uint[] population)
        {
            var costs = new float[population.Length];
            long sum = 0;
            int nonZero = 0;
            foreach (uint value in population)
            {
                sum += value;
                if (value != 0)
                {
                    nonZero++;
                }
            }

            if (nonZero <= 1)
            {
                return costs;
            }

            double logSum = Vp8LHistogram.FastLog2(sum);
            for (int i = 0; i < population.Length; i++)
            {
                costs[i] = (float)(logSum - Vp8LHistogram.FastLog2(population[i]));
            }

            return costs;
        }
    }

    /// <summary>A chain of the earlier positions that start with the same pair of pixels.</summary>
    private sealed class HashChain
    {
        private readonly int[] head;
        private readonly int[] chain;
        private readonly int shift;

        public HashChain(int count)
        {
            // A table much larger than the image only costs time to clear.
            int bits = 8;
            while (bits < HashBits && (1 << bits) < count)
            {
                bits++;
            }

            this.shift = 32 - bits;
            this.head = new int[1 << bits];
            this.chain = new int[Math.Max(1, count)];
            Array.Fill(this.head, -1);
        }

        public void Fill(uint[] argb)
        {
            int n = argb.Length;
            for (int i = 0; i + 1 < n; i++)
            {
                int key = this.Hash(argb[i], argb[i + 1]);
                this.chain[i] = this.head[key];
                this.head[key] = i;
            }

            if (n > 0)
            {
                this.chain[n - 1] = -1;
            }
        }

        /// <summary>Finds the longest match at <paramref name="position"/>, preferring the closest one.</summary>
        public void FindBest(uint[] argb, int position, int maxLength, int maxChain, out int bestLength, out int bestDistance)
        {
            bestLength = 0;
            bestDistance = 0;
            if (maxLength < MinCopyLength || position + 1 >= argb.Length)
            {
                return;
            }

            int limit = Math.Max(0, position - WindowSize);
            int remaining = maxChain;
            for (int candidate = this.chain[position]; candidate >= limit && remaining-- > 0; candidate = this.chain[candidate])
            {
                if (bestLength >= MinCopyLength && argb[candidate + bestLength - 1] != argb[position + bestLength - 1])
                {
                    continue;
                }

                int length = 0;
                while (length < maxLength && argb[candidate + length] == argb[position + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = position - candidate;
                    if (length >= maxLength)
                    {
                        break;
                    }
                }
            }

            if (bestLength < MinCopyLength)
            {
                bestLength = 0;
                bestDistance = 0;
            }
        }

        /// <summary>Collects the successively longer matches the chain offers, closest first.</summary>
        public int Collect(uint[] argb, int position, int maxLength, int maxChain, Span<int> lengths, Span<int> distances)
        {
            int found = 0;
            if (maxLength < MinCopyLength || position + 1 >= argb.Length)
            {
                return 0;
            }

            int limit = Math.Max(0, position - WindowSize);
            int remaining = maxChain;
            int bestLength = MinCopyLength - 1;
            for (int candidate = this.chain[position]; candidate >= limit && remaining-- > 0; candidate = this.chain[candidate])
            {
                if (argb[candidate + bestLength] != argb[position + bestLength])
                {
                    continue;
                }

                int length = 0;
                while (length < maxLength && argb[candidate + length] == argb[position + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    lengths[found] = length;
                    distances[found] = position - candidate;
                    found++;
                    if (length >= maxLength || found == lengths.Length)
                    {
                        break;
                    }
                }
            }

            return found;
        }

        private int Hash(uint a, uint b)
        {
            uint key = unchecked((b * 0xc6a4a793u) + (a * 0x5bd1e996u));
            return (int)(key >> this.shift);
        }
    }
}
