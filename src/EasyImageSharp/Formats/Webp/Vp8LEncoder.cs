namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Encoder for the VP8L lossless bitstream (RFC 9649 section 3), the exact counterpart of
/// <see cref="Vp8LDecoder"/>. It picks between the four reversible transforms (colour indexing with pixel
/// bundling, subtract-green, a per-tile spatial predictor and cross-colour) by encoding the shortlisted
/// combinations and keeping the smallest result, finds LZ77 backward references with a hash chain, sizes the
/// colour cache by measured cost, and splits the image into meta prefix code groups wherever that pays for
/// the extra code descriptions.
/// </summary>
internal static class Vp8LEncoder
{
    private const int MinTransformBits = 2;
    private const int MaxTransformBits = 6;
    private const int MinHuffmanBits = 2;
    private const int MaxHuffmanBits = 9;
    private const int MaxHuffmanImageSize = 2400;
    private const int MaxPaletteSize = 256;
    private const int NumPredictors = 14;

    /// <summary>Below this many pixels every shortlisted candidate is encoded in full rather than probed.</summary>
    private const int FullComparisonPixels = 1 << 17;

    /// <summary>Encodes a complete 'VP8L' chunk payload, five-byte header included.</summary>
    /// <param name="argb">The image as packed 0xAARRGGBB pixels, row-major.</param>
    /// <param name="width">Image width in pixels, at most 16383.</param>
    /// <param name="height">Image height in pixels, at most 16383.</param>
    /// <param name="hasAlpha">The value of the header's "alpha is used" hint.</param>
    /// <param name="quality">1..100; controls how hard the backward-reference search works.</param>
    /// <param name="method">0..6 effort level.</param>
    public static byte[] Encode(uint[] argb, int width, int height, bool hasAlpha, int quality, int method)
    {
        var writer = new Vp8LBitWriter(Math.Max(1024, argb.Length));
        writer.PutBits(0x2f, 8);
        writer.PutBits((uint)(width - 1), 14);
        writer.PutBits((uint)(height - 1), 14);
        writer.PutBits(hasAlpha ? 1u : 0u, 1);
        writer.PutBits(0, 3);
        WriteStream(writer, argb, width, height, quality, method);
        return writer.ToArray();
    }

    /// <summary>
    /// Encodes a header-less VP8L image stream, the form an ALPH chunk carries. The alpha values must already
    /// sit in the green channel of <paramref name="argb"/>.
    /// </summary>
    public static byte[] EncodeStreamOnly(uint[] argb, int width, int height, int quality, int method)
    {
        var writer = new Vp8LBitWriter(Math.Max(256, argb.Length / 2));
        WriteStream(writer, argb, width, height, quality, method);
        return writer.ToArray();
    }

    /// <summary>Rounds a dimension up to the number of tiles of size <c>1 &lt;&lt; bits</c> that cover it.</summary>
    public static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    // ----- Top level: choose a transform set by encoding the shortlist -----

    private static void WriteStream(Vp8LBitWriter writer, uint[] argb, int width, int height, int quality, int method)
    {
        uint[]? palette = BuildPalette(argb);
        int histoBits = GetHistoBits(method, palette is not null, width, height);
        int transformBits = GetTransformBits(method, histoBits);
        List<Candidate> candidates = ChooseCandidates(argb, width, height, palette, transformBits, method);
        var cache = new TransformCache();

        Candidate chosen = candidates[0];
        if (candidates.Count == 1)
        {
            writer.Append(EncodeCandidate(argb, width, height, palette, chosen, histoBits, quality, method, method, method >= 2, cache));
            return;
        }

        // Small images are cheap enough to encode every candidate in full and keep the smallest byte for byte.
        // Larger ones are shortlisted with a deliberately cheap parse instead: which transforms suit an image
        // is a robust decision, and running the full search on every candidate would multiply the encoding time.
        bool exhaustive = (long)width * height <= FullComparisonPixels;
        int probeEffort = exhaustive ? method : 0;
        bool probeMeta = exhaustive && method >= 2;

        var probes = new Vp8LBitWriter[candidates.Count];
        int parallelism = Math.Min(Configuration.Default.MaxDegreeOfParallelism, candidates.Count);
        if (parallelism > 1)
        {
            // The candidates are independent of one another, so they are measured side by side. The winner is
            // still chosen by size with list order breaking ties, which keeps the output byte-for-byte stable.
            Parallel.For(
                0,
                candidates.Count,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                i => probes[i] = EncodeCandidate(argb, width, height, palette, candidates[i], histoBits, quality, method, probeEffort, probeMeta, cache));
        }
        else
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                probes[i] = EncodeCandidate(argb, width, height, palette, candidates[i], histoBits, quality, method, probeEffort, probeMeta, cache);
            }
        }

        int best = 0;
        for (int i = 1; i < probes.Length; i++)
        {
            if (probes[i].BitPosition < probes[best].BitPosition)
            {
                best = i;
            }
        }

        chosen = candidates[best];
        writer.Append(exhaustive
            ? probes[best]
            : EncodeCandidate(argb, width, height, palette, chosen, histoBits, quality, method, method, method >= 2, cache));
    }

    [Flags]
    private enum TransformSet
    {
        None = 0,
        SubtractGreen = 1,
        Predictor = 2,
        CrossColor = 4,
        Palette = 8,
    }

    /// <summary>One combination of transforms, together with the tile size its per-tile transforms use.</summary>
    private readonly record struct Candidate(TransformSet Set, int TransformBits);

    /// <summary>
    /// Remembers the per-tile predictor modes and cross-colour multipliers across the shortlisting probes and
    /// the final encode: they depend only on the transforms applied before them, not on the parsing effort.
    /// </summary>
    private sealed class TransformCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(TransformSet Prefix, int Bits), byte[]> predictors = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(TransformSet Prefix, int Bits), uint[]> crossColors = new();

        public byte[] Predictors(TransformSet prefix, int bits, Func<byte[]> create) => this.predictors.GetOrAdd((prefix, bits), _ => create());

        public uint[] CrossColors(TransformSet prefix, int bits, Func<uint[]> create) => this.crossColors.GetOrAdd((prefix, bits), _ => create());
    }

    private static Vp8LBitWriter EncodeCandidate(
        uint[] source, int width, int height, uint[]? palette, Candidate candidate, int histoBits, int quality, int method,
        int parseEffort, bool allowMeta, TransformCache cache)
    {
        TransformSet set = candidate.Set;
        int bits = Math.Clamp(candidate.TransformBits, MinTransformBits, MaxTransformBits);
        var writer = new Vp8LBitWriter(Math.Max(1024, source.Length / 2));
        uint[] data = source;
        int codedWidth = width;

        if ((set & TransformSet.Palette) != 0)
        {
            data = WritePaletteTransform(writer, source, width, height, palette!, quality, parseEffort, out codedWidth);
        }
        else if (set != TransformSet.None)
        {
            data = (uint[])source.Clone();
        }

        TransformSet prefix = set & TransformSet.Palette;
        if ((set & TransformSet.SubtractGreen) != 0)
        {
            writer.PutBits(1, 1);
            writer.PutBits(2, 2);
            SubtractGreen(data);
            prefix |= TransformSet.SubtractGreen;
        }

        if ((set & TransformSet.Predictor) != 0)
        {
            uint[] snapshot = data;
            int snapshotWidth = codedWidth;
            byte[] modes = cache.Predictors(prefix, bits, () => SelectPredictors(snapshot, snapshotWidth, height, bits));
            writer.PutBits(1, 1);
            writer.PutBits(0, 2);
            writer.PutBits((uint)(bits - 2), 3);
            WriteSubImage(writer, ModeImage(modes), SubSampleSize(codedWidth, bits), SubSampleSize(height, bits), quality, parseEffort);
            ApplyPredictor(data, codedWidth, height, bits, modes);
            prefix |= TransformSet.Predictor;
        }

        if ((set & TransformSet.CrossColor) != 0)
        {
            uint[] snapshot = data;
            int snapshotWidth = codedWidth;
            uint[] multipliers = cache.CrossColors(prefix, bits, () => SelectCrossColor(snapshot, snapshotWidth, height, bits));
            writer.PutBits(1, 1);
            writer.PutBits(1, 2);
            writer.PutBits((uint)(bits - 2), 3);
            WriteSubImage(writer, multipliers, SubSampleSize(codedWidth, bits), SubSampleSize(height, bits), quality, parseEffort);
            ApplyCrossColor(data, codedWidth, height, bits, multipliers);
        }

        writer.PutBits(0, 1);
        WriteSpatialImage(writer, data, codedWidth, height, histoBits, quality, method, parseEffort, allowMeta);
        return writer;
    }

    /// <summary>
    /// Ranks the transform combinations with a single cheap pass over the pixels (the left and top neighbours
    /// stand in for the full predictor search) and returns the shortlist this effort level may try.
    /// </summary>
    private static List<Candidate> ChooseCandidates(uint[] argb, int width, int height, uint[]? palette, int transformBits, int method)
    {
        var shortlist = new List<Candidate>();
        if (palette is not null)
        {
            shortlist.Add(new Candidate(TransformSet.Palette, transformBits));
            if (palette.Length <= 16 && method <= 2)
            {
                // Bundled indices pack two to eight pixels into a byte; nothing else comes close.
                return shortlist;
            }

            // An index image is often as predictable as the picture it came from, so the spatial predictor is
            // worth a try on top of the palette; whichever of the two is smaller wins.
            if (method >= 3)
            {
                shortlist.Add(new Candidate(TransformSet.Palette | TransformSet.Predictor, transformBits));
            }
        }

        int budget = method <= 1 ? 1 : method <= 3 ? 2 : method <= 5 ? 3 : 4;
        (TransformSet Set, double Cost)[] ranked = RankTransformSets(argb, width, height, transformBits);
        Array.Sort(ranked, static (a, b) => a.Cost.CompareTo(b.Cost));

        TransformSet favourite = ranked[0].Set;
        for (int i = 0; i < ranked.Length && i < budget; i++)
        {
            shortlist.Add(new Candidate(ranked[i].Set, transformBits));
        }

        // Cross-colour does not deserve its own analysis pass; it is tried on top of the favourite instead.
        if (method >= 3 && (favourite & TransformSet.Predictor) != 0)
        {
            shortlist.Add(new Candidate(favourite | TransformSet.CrossColor, transformBits));
        }

        // The tile size the heuristics pick shrinks with the effort level, which on a small image can cost
        // more in transform data than it saves. At the effort levels that can afford it, try a coarser grid.
        if (method >= 5)
        {
            int coarse = Math.Min(transformBits + 2, MaxTransformBits);
            if (coarse != transformBits)
            {
                foreach (Candidate candidate in shortlist.ToArray())
                {
                    if ((candidate.Set & (TransformSet.Predictor | TransformSet.CrossColor)) != 0)
                    {
                        shortlist.Add(candidate with { TransformBits = coarse });
                    }
                }
            }
        }

        return shortlist;
    }

    private static (TransformSet Set, double Cost)[] RankTransformSets(uint[] argb, int width, int height, int transformBits)
    {
        var direct = new uint[4 * 256];
        var spatial = new uint[4 * 256];
        var subGreen = new uint[4 * 256];
        var spatialSubGreen = new uint[4 * 256];

        for (int y = 1; y < height; y++)
        {
            int row = y * width;
            int previousRow = row - width;
            for (int x = 1; x < width; x++)
            {
                uint pixel = argb[row + x];
                uint left = argb[row + x - 1];
                uint top = argb[previousRow + x];

                // Runs are handled by the backward references, so they must not sway the entropy estimate.
                if (pixel == left || pixel == top)
                {
                    continue;
                }

                AddChannels(direct, pixel);
                AddChannels(spatial, SubtractPixels(pixel, top));
                AddChannels(subGreen, ToSubtractGreen(pixel));
                AddChannels(spatialSubGreen, ToSubtractGreen(SubtractPixels(pixel, top)));
            }
        }

        double directCost = ChannelEntropy(direct, 0) + ChannelEntropy(direct, 1) + ChannelEntropy(direct, 2) + ChannelEntropy(direct, 3);
        double spatialCost = ChannelEntropy(spatial, 0) + ChannelEntropy(spatial, 1) + ChannelEntropy(spatial, 2) + ChannelEntropy(spatial, 3);
        double subGreenCost = ChannelEntropy(subGreen, 0) + ChannelEntropy(subGreen, 1) + ChannelEntropy(subGreen, 2) + ChannelEntropy(subGreen, 3);
        double bothCost = ChannelEntropy(spatialSubGreen, 0) + ChannelEntropy(spatialSubGreen, 1)
            + ChannelEntropy(spatialSubGreen, 2) + ChannelEntropy(spatialSubGreen, 3);

        // Storing the predictor image is not free: one of fourteen modes per tile, plus its own prefix code.
        double predictorOverhead = SubSampleSize(width, transformBits) * (double)SubSampleSize(height, transformBits) * Math.Log2(NumPredictors);
        spatialCost += predictorOverhead;
        bothCost += predictorOverhead;

        return new[]
        {
            (TransformSet.None, directCost),
            (TransformSet.Predictor, spatialCost),
            (TransformSet.SubtractGreen, subGreenCost),
            (TransformSet.SubtractGreen | TransformSet.Predictor, bothCost),
        };
    }

    private static void AddChannels(uint[] histogram, uint pixel)
    {
        histogram[(pixel >> 24) & 0xff]++;
        histogram[256 + ((pixel >> 16) & 0xff)]++;
        histogram[512 + ((pixel >> 8) & 0xff)]++;
        histogram[768 + (pixel & 0xff)]++;
    }

    private static double ChannelEntropy(uint[] histogram, int channel) => Vp8LHistogram.Entropy(histogram.AsSpan(channel * 256, 256));

    // ----- Transforms -----

    /// <summary>
    /// Subtracts two pixels channel by channel, modulo 256. The two channels of each half are padded with a
    /// full byte in the gap between them, so a borrow out of one channel is absorbed by the padding instead of
    /// corrupting the channel above it.
    /// </summary>
    private static uint SubtractPixels(uint a, uint b)
    {
        uint alphaGreen = 0x00ff00ffu + (a & 0xff00ff00u) - (b & 0xff00ff00u);
        uint redBlue = 0xff00ff00u + (a & 0x00ff00ffu) - (b & 0x00ff00ffu);
        return (alphaGreen & 0xff00ff00u) | (redBlue & 0x00ff00ffu);
    }

    private static uint ToSubtractGreen(uint pixel)
    {
        uint green = (pixel >> 8) & 0xff;
        uint red = ((pixel >> 16) - green) & 0xff;
        uint blue = (pixel - green) & 0xff;
        return (pixel & 0xff00ff00u) | (red << 16) | blue;
    }

    private static void SubtractGreen(uint[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = ToSubtractGreen(data[i]);
        }
    }

    private static uint[] ModeImage(byte[] modes)
    {
        var image = new uint[modes.Length];
        for (int i = 0; i < modes.Length; i++)
        {
            image[i] = 0xff000000u | ((uint)modes[i] << 8);
        }

        return image;
    }

    /// <summary>Evaluates all fourteen predictors on every tile and keeps the cheapest per tile.</summary>
    private static byte[] SelectPredictors(uint[] data, int width, int height, int bits)
    {
        int tilesX = SubSampleSize(width, bits);
        int tilesY = SubSampleSize(height, bits);
        var modes = new byte[tilesX * tilesY];
        int tileSize = 1 << bits;

        ParallelRowIterator.IterateRows(
            width * tileSize,
            tilesY,
            () => new uint[NumPredictors * 4 * 9],
            (startTileRow, endTileRow, buckets) =>
            {
                for (int ty = startTileRow; ty < endTileRow; ty++)
                {
                    int y0 = ty * tileSize;
                    int y1 = Math.Min(y0 + tileSize, height);
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        int x0 = tx * tileSize;
                        int x1 = Math.Min(x0 + tileSize, width);
                        modes[(ty * tilesX) + tx] = BestPredictorForTile(data, width, x0, y0, x1, y1, buckets);
                    }
                }
            });

        return modes;
    }

    private static byte BestPredictorForTile(uint[] data, int width, int x0, int y0, int x1, int y1, uint[] buckets)
    {
        int startY = Math.Max(y0, 1);
        int startX = Math.Max(x0, 1);
        if (startY >= y1 || startX >= x1)
        {
            return 0;
        }

        Array.Clear(buckets);
        for (int y = startY; y < y1; y++)
        {
            int row = y * width;
            int above = row - width;
            for (int x = startX; x < x1; x++)
            {
                int index = row + x;
                uint left = data[index - 1];
                uint top = data[above + x];
                uint topLeft = data[above + x - 1];

                // The format wraps the top-right of the last column onto the first pixel of the current row.
                uint topRight = data[index - width + 1];
                uint pixel = data[index];
                for (int mode = 0; mode < NumPredictors; mode++)
                {
                    uint residual = SubtractPixels(pixel, Predict(mode, left, top, topLeft, topRight));
                    int offset = mode * 36;
                    buckets[offset + Bucket(residual >> 24)]++;
                    buckets[offset + 9 + Bucket((residual >> 16) & 0xff)]++;
                    buckets[offset + 18 + Bucket((residual >> 8) & 0xff)]++;
                    buckets[offset + 27 + Bucket(residual & 0xff)]++;
                }
            }
        }

        byte best = 0;
        double bestCost = double.MaxValue;
        for (int mode = 0; mode < NumPredictors; mode++)
        {
            double cost = BucketCost(buckets, mode * 36);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = (byte)mode;
            }
        }

        return best;
    }

    /// <summary>Groups a residual byte by the magnitude of its signed value, which is what the entropy coder sees.</summary>
    private static int Bucket(uint value)
    {
        int magnitude = value > 128 ? (int)(256 - value) : (int)value;
        return magnitude == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)magnitude);
    }

    /// <summary>The entropy of four bucketed channels plus the bits the buckets themselves hide.</summary>
    private static double BucketCost(uint[] buckets, int offset)
    {
        double cost = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            int start = offset + (channel * 9);
            long total = 0;
            double sum = 0;
            for (int b = 0; b < 9; b++)
            {
                uint count = buckets[start + b];
                if (count == 0)
                {
                    continue;
                }

                total += count;
                sum += count * Vp8LHistogram.FastLog2(count);
                if (b > 1)
                {
                    cost += count * (b - 1);
                }
            }

            if (total != 0)
            {
                cost += (total * Vp8LHistogram.FastLog2(total)) - sum;
            }
        }

        return cost;
    }

    private static uint Predict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        0 => 0xff000000u,
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(top, left, topLeft),
        12 => ClampedAddSubtractFull(left, top, topLeft),
        13 => ClampedAddSubtractHalf(left, top, topLeft),
        _ => 0xff000000u,
    };

    /// <summary>
    /// Replaces every pixel with its residual against the chosen predictor. The image is walked backwards so
    /// that the left, top, top-left and top-right neighbours a residual needs are still the original pixels.
    /// </summary>
    private static void ApplyPredictor(uint[] data, int width, int height, int bits, byte[] modes)
    {
        int tilesX = SubSampleSize(width, bits);
        for (int y = height - 1; y >= 1; y--)
        {
            int row = y * width;
            int above = row - width;
            int modeRow = (y >> bits) * tilesX;
            for (int x = width - 1; x >= 1; x--)
            {
                int index = row + x;
                uint left = data[index - 1];
                uint top = data[above + x];
                uint topLeft = data[above + x - 1];
                uint topRight = data[index - width + 1];
                data[index] = SubtractPixels(data[index], Predict(modes[modeRow + (x >> bits)], left, top, topLeft, topRight));
            }

            data[row] = SubtractPixels(data[row], data[above]);
        }

        for (int x = width - 1; x >= 1; x--)
        {
            data[x] = SubtractPixels(data[x], data[x - 1]);
        }

        data[0] = SubtractPixels(data[0], 0xff000000u);
    }

    /// <summary>Fits the three cross-colour multipliers of every tile by regression and refines them locally.</summary>
    private static uint[] SelectCrossColor(uint[] data, int width, int height, int bits)
    {
        int tilesX = SubSampleSize(width, bits);
        int tilesY = SubSampleSize(height, bits);
        var result = new uint[tilesX * tilesY];
        int tileSize = 1 << bits;

        ParallelRowIterator.IterateRows(
            width * tileSize,
            tilesY,
            () => new uint[9],
            (startTileRow, endTileRow, buckets) =>
            {
                for (int ty = startTileRow; ty < endTileRow; ty++)
                {
                    int y0 = ty * tileSize;
                    int y1 = Math.Min(y0 + tileSize, height);
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        int x0 = tx * tileSize;
                        int x1 = Math.Min(x0 + tileSize, width);
                        result[(ty * tilesX) + tx] = BestCrossColorForTile(data, width, x0, y0, x1, y1, buckets);
                    }
                }
            });

        return result;
    }

    private static uint BestCrossColorForTile(uint[] data, int width, int x0, int y0, int x1, int y1, uint[] buckets)
    {
        long sumGreenGreen = 0;
        long sumGreenRed = 0;
        long sumGreenBlue = 0;
        long sumRedRed = 0;
        long sumRedBlue = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            for (int x = x0; x < x1; x++)
            {
                uint pixel = data[row + x];
                int green = (sbyte)(pixel >> 8);
                int red = (sbyte)(pixel >> 16);
                int blue = (sbyte)pixel;
                sumGreenGreen += green * green;
                sumGreenRed += green * red;
                sumGreenBlue += green * blue;
                sumRedRed += red * red;
                sumRedBlue += red * blue;
            }
        }

        int greenToRed = Refine(data, width, x0, y0, x1, y1, buckets, Guess(sumGreenRed, sumGreenGreen), CrossColorChannel.GreenToRed, 0, 0);
        int greenToBlue = Refine(data, width, x0, y0, x1, y1, buckets, Guess(sumGreenBlue, sumGreenGreen), CrossColorChannel.GreenToBlue, 0, 0);
        int redToBlue = Refine(data, width, x0, y0, x1, y1, buckets, Guess(sumRedBlue, sumRedRed), CrossColorChannel.RedToBlue, greenToBlue, 0);
        greenToBlue = Refine(data, width, x0, y0, x1, y1, buckets, greenToBlue, CrossColorChannel.GreenToBlue, 0, redToBlue);

        return 0xff000000u | ((uint)(redToBlue & 0xff) << 16) | ((uint)(greenToBlue & 0xff) << 8) | (uint)(greenToRed & 0xff);
    }

    private static int Guess(long covariance, long variance)
    {
        if (variance == 0)
        {
            return 0;
        }

        long scaled = (32 * covariance) / variance;
        return (int)Math.Clamp(scaled, -128, 127);
    }

    private enum CrossColorChannel
    {
        GreenToRed,
        GreenToBlue,
        RedToBlue,
    }

    private static readonly int[] CrossColorOffsets = { 0, 1, -1, 2, -2, 4, -4, 8, -8, 16, -16 };

    private static int Refine(
        uint[] data, int width, int x0, int y0, int x1, int y1, uint[] buckets, int guess, CrossColorChannel channel, int otherGreenToBlue, int otherRedToBlue)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        foreach (int offset in CrossColorOffsets)
        {
            int candidate = Math.Clamp(guess + offset, -128, 127);
            double cost = CrossColorCost(data, width, x0, y0, x1, y1, buckets, channel, candidate, otherGreenToBlue, otherRedToBlue);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = candidate;
            }
        }

        // Leaving the channel alone must always be on the table; it costs nothing to store and often wins.
        double zeroCost = CrossColorCost(data, width, x0, y0, x1, y1, buckets, channel, 0, otherGreenToBlue, otherRedToBlue);
        return zeroCost <= bestCost ? 0 : best;
    }

    private static double CrossColorCost(
        uint[] data, int width, int x0, int y0, int x1, int y1, uint[] buckets, CrossColorChannel channel, int multiplier, int greenToBlue, int redToBlue)
    {
        Array.Clear(buckets);
        long total = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            for (int x = x0; x < x1; x++)
            {
                uint pixel = data[row + x];
                int green = (sbyte)(pixel >> 8);
                int red = (sbyte)(pixel >> 16);
                int value = channel switch
                {
                    CrossColorChannel.GreenToRed => (int)((pixel >> 16) & 0xff) - ((multiplier * green) >> 5),
                    CrossColorChannel.GreenToBlue => (int)(pixel & 0xff) - ((multiplier * green) >> 5) - ((redToBlue * red) >> 5),
                    _ => (int)(pixel & 0xff) - ((greenToBlue * green) >> 5) - ((multiplier * red) >> 5),
                };

                buckets[Bucket((uint)(value & 0xff))]++;
                total++;
            }
        }

        double sum = 0;
        double cost = 0;
        for (int b = 0; b < 9; b++)
        {
            uint count = buckets[b];
            if (count == 0)
            {
                continue;
            }

            sum += count * Vp8LHistogram.FastLog2(count);
            if (b > 1)
            {
                cost += count * (b - 1);
            }
        }

        return cost + (total == 0 ? 0 : (total * Vp8LHistogram.FastLog2(total)) - sum);
    }

    private static void ApplyCrossColor(uint[] data, int width, int height, int bits, uint[] multipliers)
    {
        int tilesX = SubSampleSize(width, bits);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int tileRow = (y >> bits) * tilesX;
            for (int x = 0; x < width; x++)
            {
                uint m = multipliers[tileRow + (x >> bits)];
                int greenToRed = (sbyte)(m & 0xff);
                int greenToBlue = (sbyte)((m >> 8) & 0xff);
                int redToBlue = (sbyte)((m >> 16) & 0xff);

                uint pixel = data[row + x];
                int green = (sbyte)(pixel >> 8);
                int red = (sbyte)(pixel >> 16);
                int newRed = (int)((pixel >> 16) & 0xff) - ((greenToRed * green) >> 5);
                newRed &= 0xff;
                int newBlue = (int)(pixel & 0xff) - ((greenToBlue * green) >> 5) - ((redToBlue * red) >> 5);
                newBlue &= 0xff;
                data[row + x] = (pixel & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
            }
        }
    }

    // ----- Colour indexing (palette) -----

    /// <summary>Returns the sorted palette of the image, or <see langword="null"/> when it uses more than 256 colours.</summary>
    private static uint[]? BuildPalette(uint[] argb)
    {
        var colors = new HashSet<uint>();
        foreach (uint pixel in argb)
        {
            if (colors.Add(pixel) && colors.Count > MaxPaletteSize)
            {
                return null;
            }
        }

        uint[] palette = colors.ToArray();
        Array.Sort(palette);
        return palette;
    }

    private static int PaletteBundleBits(int colors) => colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;

    private static uint[] WritePaletteTransform(
        Vp8LBitWriter writer, uint[] argb, int width, int height, uint[] palette, int quality, int method, out int codedWidth)
    {
        int bits = PaletteBundleBits(palette.Length);
        writer.PutBits(1, 1);
        writer.PutBits(3, 2);
        writer.PutBits((uint)(palette.Length - 1), 8);

        // The palette itself is stored as a one-row image of successive differences.
        var deltas = new uint[palette.Length];
        deltas[0] = palette[0];
        for (int i = 1; i < palette.Length; i++)
        {
            deltas[i] = SubtractPixels(palette[i], palette[i - 1]);
        }

        WriteSubImage(writer, deltas, palette.Length, 1, quality, method);

        var lookup = new Dictionary<uint, int>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i]] = i;
        }

        codedWidth = SubSampleSize(width, bits);
        var packed = new uint[codedWidth * height];
        int bitsPerPixel = 8 >> bits;
        int mask = (1 << bits) - 1;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * width;
            int targetRow = y * codedWidth;
            uint code = 0xff000000u;
            for (int x = 0; x < width; x++)
            {
                if ((x & mask) == 0)
                {
                    code = 0xff000000u;
                }

                code |= (uint)lookup[argb[sourceRow + x]] << (8 + (bitsPerPixel * (x & mask)));
                packed[targetRow + (x >> bits)] = code;
            }
        }

        return packed;
    }

    // ----- Image streams -----

    /// <summary>Writes an entropy-coded sub-image: transform data, a palette or a meta prefix code image.</summary>
    private static void WriteSubImage(Vp8LBitWriter writer, uint[] pixels, int width, int height, int quality, int parseEffort)
    {
        int effort = Math.Min(parseEffort, 4);
        Vp8LTokenList refs = Vp8LBackwardReferences.Compute(pixels, width, quality, effort, out int cacheBits);
        WriteCacheInfo(writer, cacheBits);
        var histogram = new Vp8LHistogram(cacheBits);
        AddAll(refs, histogram);
        Vp8LPrefixCode[] codes = BuildGroup(histogram);
        StoreGroup(writer, codes);
        WriteTokens(writer, refs, width, 0, null, new[] { codes });
    }

    /// <summary>Writes the spatially coded image: the colour cache, the meta prefix codes and the symbol stream.</summary>
    private static void WriteSpatialImage(
        Vp8LBitWriter writer, uint[] pixels, int width, int height, int histoBits, int quality, int method, int parseEffort, bool allowMeta)
    {
        Vp8LTokenList refs = Vp8LBackwardReferences.Compute(pixels, width, quality, parseEffort, out int cacheBits);
        WriteCacheInfo(writer, cacheBits);

        var single = new Vp8LHistogram(cacheBits);
        AddAll(refs, single);
        Vp8LPrefixCode[] singleCodes = BuildGroup(single);
        long singleCost = GroupBitCount(singleCodes) + DataBitCount(refs, width, 0, null, new[] { singleCodes });

        if (allowMeta && histoBits > 0)
        {
            MetaCoding? meta = BuildMetaCoding(refs, width, height, histoBits, cacheBits, quality, method, parseEffort);
            if (meta is not null && meta.TotalCost + 5 < singleCost)
            {
                writer.PutBits(1, 1);
                writer.PutBits((uint)(histoBits - 2), 3);
                writer.Append(meta.Image);
                foreach (Vp8LPrefixCode[] group in meta.Groups)
                {
                    StoreGroup(writer, group);
                }

                WriteTokens(writer, refs, width, histoBits, meta.BlockGroups, meta.Groups);
                return;
            }
        }

        writer.PutBits(0, 1);
        StoreGroup(writer, singleCodes);
        WriteTokens(writer, refs, width, 0, null, new[] { singleCodes });
    }

    private static void WriteCacheInfo(Vp8LBitWriter writer, int cacheBits)
    {
        if (cacheBits > 0)
        {
            writer.PutBits(1, 1);
            writer.PutBits((uint)cacheBits, 4);
        }
        else
        {
            writer.PutBits(0, 1);
        }
    }

    private static void AddAll(Vp8LTokenList refs, Vp8LHistogram histogram)
    {
        for (int i = 0; i < refs.Count; i++)
        {
            histogram.Add(refs.Items[i]);
        }
    }

    private static Vp8LPrefixCode[] BuildGroup(Vp8LHistogram histogram) => new[]
    {
        Vp8LPrefixCode.Build(histogram.Literal),
        Vp8LPrefixCode.Build(histogram.Red),
        Vp8LPrefixCode.Build(histogram.Blue),
        Vp8LPrefixCode.Build(histogram.Alpha),
        Vp8LPrefixCode.Build(histogram.Distance),
    };

    private static void StoreGroup(Vp8LBitWriter writer, Vp8LPrefixCode[] group)
    {
        foreach (Vp8LPrefixCode code in group)
        {
            code.Store(writer);
        }
    }

    private static long GroupBitCount(Vp8LPrefixCode[] group)
    {
        long bits = 0;
        foreach (Vp8LPrefixCode code in group)
        {
            bits += code.StoredBitCount();
        }

        return bits;
    }

    /// <summary>Walks the symbol stream, tracking the position so the meta prefix group matches the decoder's choice.</summary>
    private static void WriteTokens(Vp8LBitWriter writer, Vp8LTokenList refs, int width, int histoBits, int[]? blockGroups, Vp8LPrefixCode[][] groups)
    {
        int tilesX = histoBits > 0 ? SubSampleSize(width, histoBits) : 0;
        int column = 0;
        int row = 0;
        Vp8LToken[] items = refs.Items;
        for (int i = 0; i < refs.Count; i++)
        {
            ref Vp8LToken token = ref items[i];
            Vp8LPrefixCode[] group = blockGroups is null
                ? groups[0]
                : groups[blockGroups[((row >> histoBits) * tilesX) + (column >> histoBits)]];

            switch (token.Kind)
            {
                case Vp8LTokenKind.Literal:
                {
                    uint argb = token.Value;
                    group[0].Emit(writer, (int)((argb >> 8) & 0xff));
                    group[1].Emit(writer, (int)((argb >> 16) & 0xff));
                    group[2].Emit(writer, (int)(argb & 0xff));
                    group[3].Emit(writer, (int)(argb >> 24));
                    break;
                }

                case Vp8LTokenKind.Copy:
                {
                    Vp8LPrefix.Encode(token.Length, out int lengthCode, out int lengthExtra, out int lengthValue);
                    group[0].Emit(writer, Vp8LHistogram.NumLiteralCodes + lengthCode);
                    writer.PutBits((uint)lengthValue, lengthExtra);
                    Vp8LPrefix.Encode(token.PlaneCode, out int distanceCode, out int distanceExtra, out int distanceValue);
                    group[4].Emit(writer, distanceCode);
                    writer.PutBits((uint)distanceValue, distanceExtra);
                    break;
                }

                default:
                    group[0].Emit(writer, Vp8LHistogram.NumLiteralCodes + Vp8LHistogram.NumLengthCodes + (int)token.Value);
                    break;
            }

            Advance(ref column, ref row, token.PixelCount, width);
        }
    }

    private static long DataBitCount(Vp8LTokenList refs, int width, int histoBits, int[]? blockGroups, Vp8LPrefixCode[][] groups)
    {
        int tilesX = histoBits > 0 ? SubSampleSize(width, histoBits) : 0;
        int column = 0;
        int row = 0;
        long bits = 0;
        Vp8LToken[] items = refs.Items;
        for (int i = 0; i < refs.Count; i++)
        {
            ref Vp8LToken token = ref items[i];
            Vp8LPrefixCode[] group = blockGroups is null
                ? groups[0]
                : groups[blockGroups[((row >> histoBits) * tilesX) + (column >> histoBits)]];

            switch (token.Kind)
            {
                case Vp8LTokenKind.Literal:
                {
                    uint argb = token.Value;
                    bits += group[0].BitLength((int)((argb >> 8) & 0xff));
                    bits += group[1].BitLength((int)((argb >> 16) & 0xff));
                    bits += group[2].BitLength((int)(argb & 0xff));
                    bits += group[3].BitLength((int)(argb >> 24));
                    break;
                }

                case Vp8LTokenKind.Copy:
                {
                    Vp8LPrefix.Encode(token.Length, out int lengthCode, out int lengthExtra, out _);
                    bits += group[0].BitLength(Vp8LHistogram.NumLiteralCodes + lengthCode) + lengthExtra;
                    Vp8LPrefix.Encode(token.PlaneCode, out int distanceCode, out int distanceExtra, out _);
                    bits += group[4].BitLength(distanceCode) + distanceExtra;
                    break;
                }

                default:
                    bits += group[0].BitLength(Vp8LHistogram.NumLiteralCodes + Vp8LHistogram.NumLengthCodes + (int)token.Value);
                    break;
            }

            Advance(ref column, ref row, token.PixelCount, width);
        }

        return bits;
    }

    private static void Advance(ref int column, ref int row, int pixels, int width)
    {
        column += pixels;
        while (column >= width)
        {
            column -= width;
            row++;
        }
    }

    // ----- Meta prefix codes -----

    private sealed class MetaCoding
    {
        public Vp8LBitWriter Image = null!;

        public int[] BlockGroups = null!;

        public Vp8LPrefixCode[][] Groups = null!;

        public long TotalCost;
    }

    /// <summary>
    /// Splits the image into tiles, clusters the tiles whose symbol statistics are alike, and reports what the
    /// resulting set of prefix code groups would cost, entropy image included.
    /// </summary>
    private static MetaCoding? BuildMetaCoding(
        Vp8LTokenList refs, int width, int height, int histoBits, int cacheBits, int quality, int method, int parseEffort)
    {
        int tilesX = SubSampleSize(width, histoBits);
        int tilesY = SubSampleSize(height, histoBits);
        int blocks = tilesX * tilesY;
        if (blocks <= 1 || blocks > MaxHuffmanImageSize)
        {
            return null;
        }

        var perBlock = new Vp8LHistogram[blocks];
        for (int i = 0; i < blocks; i++)
        {
            perBlock[i] = new Vp8LHistogram(cacheBits);
        }

        int column = 0;
        int row = 0;
        for (int i = 0; i < refs.Count; i++)
        {
            ref Vp8LToken token = ref refs.Items[i];
            perBlock[((row >> histoBits) * tilesX) + (column >> histoBits)].Add(token);
            Advance(ref column, ref row, token.PixelCount, width);
        }

        int[] cluster = ClusterBlocks(perBlock, cacheBits, method, out List<Vp8LHistogram> clusters);
        if (clusters.Count <= 1)
        {
            return null;
        }

        var groups = new Vp8LPrefixCode[clusters.Count][];
        long cost = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            groups[i] = BuildGroup(clusters[i]);
            cost += GroupBitCount(groups[i]);
        }

        var entropyImage = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            uint id = (uint)cluster[i];
            entropyImage[i] = 0xff000000u | ((id & 0xff00) << 8) | ((id & 0xff) << 8);
        }

        var imageWriter = new Vp8LBitWriter(256);
        WriteSubImage(imageWriter, entropyImage, tilesX, tilesY, quality, Math.Min(parseEffort, 3));

        cost += imageWriter.BitPosition + 4;
        cost += DataBitCount(refs, width, histoBits, cluster, groups);

        return new MetaCoding { Image = imageWriter, BlockGroups = cluster, Groups = groups, TotalCost = cost };
    }

    /// <summary>
    /// Groups the per-tile histograms: first into coarse bins by their literal, red and blue entropies, then by
    /// greedily merging the pairs whose combined code is cheaper than keeping them apart.
    /// </summary>
    private static int[] ClusterBlocks(Vp8LHistogram[] blocks, int cacheBits, int method, out List<Vp8LHistogram> clusters)
    {
        const int partitions = 4;
        int count = blocks.Length;
        var literalCost = new double[count];
        var redCost = new double[count];
        var blueCost = new double[count];
        for (int i = 0; i < count; i++)
        {
            literalCost[i] = Vp8LHistogram.Entropy(blocks[i].Literal);
            redCost[i] = Vp8LHistogram.Entropy(blocks[i].Red);
            blueCost[i] = Vp8LHistogram.Entropy(blocks[i].Blue);
        }

        MinAndRange(literalCost, out double literalMin, out double literalRange);
        MinAndRange(redCost, out double redMin, out double redRange);
        MinAndRange(blueCost, out double blueMin, out double blueRange);

        var binOf = new Dictionary<int, int>();
        var assignment = new int[count];
        clusters = new List<Vp8LHistogram>();
        for (int i = 0; i < count; i++)
        {
            int bin = Partition(literalCost[i], literalMin, literalRange, partitions);
            bin = (bin * partitions) + Partition(redCost[i], redMin, redRange, partitions);
            bin = (bin * partitions) + Partition(blueCost[i], blueMin, blueRange, partitions);
            if (!binOf.TryGetValue(bin, out int index))
            {
                index = clusters.Count;
                binOf[bin] = index;
                clusters.Add(new Vp8LHistogram(cacheBits));
            }

            assignment[i] = index;
            clusters[index].AddFrom(blocks[i]);
        }

        if (method >= 3)
        {
            MergeClusters(clusters, assignment, cacheBits);
        }

        return assignment;
    }

    private static void MinAndRange(double[] values, out double min, out double range)
    {
        min = double.MaxValue;
        double max = double.MinValue;
        foreach (double candidate in values)
        {
            min = Math.Min(min, candidate);
            max = Math.Max(max, candidate);
        }

        range = max - min;
    }

    private static int Partition(double value, double min, double range, int partitions)
        => range > 0 ? (int)((partitions - 1e-6) * (value - min) / range) : 0;

    /// <summary>
    /// Repeatedly merges the pair of clusters whose combined prefix codes are cheapest, stopping as soon as no
    /// pair pays for itself. Pair costs are cached and only the row and column of the surviving cluster are
    /// recomputed, so the whole loop stays linear in the number of merges.
    /// </summary>
    private static void MergeClusters(List<Vp8LHistogram> clusters, int[] assignment, int cacheBits)
    {
        int count = clusters.Count;
        var costs = new double[count];
        for (int i = 0; i < count; i++)
        {
            costs[i] = clusters[i].EstimatedCost();
        }

        var gain = new double[count, count];
        for (int a = 0; a < count; a++)
        {
            for (int b = a + 1; b < count; b++)
            {
                gain[a, b] = costs[a] + costs[b] - Vp8LHistogram.MergedCost(clusters[a], clusters[b]);
            }
        }

        var alive = new bool[count];
        Array.Fill(alive, true);
        var mapping = new int[count];
        for (int i = 0; i < count; i++)
        {
            mapping[i] = i;
        }

        int living = count;
        while (living > 1)
        {
            double bestGain = 0;
            int bestA = -1;
            int bestB = -1;
            for (int a = 0; a < count; a++)
            {
                if (!alive[a])
                {
                    continue;
                }

                for (int b = a + 1; b < count; b++)
                {
                    if (alive[b] && gain[a, b] > bestGain)
                    {
                        bestGain = gain[a, b];
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (bestA < 0)
            {
                break;
            }

            clusters[bestA].AddFrom(clusters[bestB]);
            costs[bestA] = clusters[bestA].EstimatedCost();
            alive[bestB] = false;
            living--;
            for (int i = 0; i < count; i++)
            {
                if (mapping[i] == bestB)
                {
                    mapping[i] = bestA;
                }
            }

            for (int other = 0; other < count; other++)
            {
                if (!alive[other] || other == bestA)
                {
                    continue;
                }

                double merged = Vp8LHistogram.MergedCost(clusters[bestA], clusters[other]);
                double value = costs[bestA] + costs[other] - merged;
                if (other < bestA)
                {
                    gain[other, bestA] = value;
                }
                else
                {
                    gain[bestA, other] = value;
                }
            }
        }

        // Renumber the survivors so the group indices the entropy image carries are contiguous.
        var renumber = new int[count];
        Array.Fill(renumber, -1);
        var survivors = new List<Vp8LHistogram>();
        for (int i = 0; i < count; i++)
        {
            if (alive[i])
            {
                renumber[i] = survivors.Count;
                survivors.Add(clusters[i]);
            }
        }

        for (int i = 0; i < assignment.Length; i++)
        {
            assignment[i] = renumber[mapping[assignment[i]]];
        }

        clusters.Clear();
        clusters.AddRange(survivors);
    }

    // ----- Tile size heuristics -----

    private static int GetHistoBits(int method, bool usePalette, int width, int height)
    {
        int bits = (usePalette ? 9 : 7) - method;
        while (true)
        {
            long size = (long)SubSampleSize(width, bits) * SubSampleSize(height, bits);
            if (size <= MaxHuffmanImageSize)
            {
                break;
            }

            bits++;
        }

        return Math.Clamp(bits, MinHuffmanBits, MaxHuffmanBits);
    }

    private static int GetTransformBits(int method, int histoBits)
    {
        int max = method < 4 ? 6 : method > 4 ? 4 : 5;
        return Math.Clamp(Math.Min(histoBits, max), MinTransformBits, MaxTransformBits);
    }

    // ----- Pixel arithmetic shared with the decoder's inverse transforms -----

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);

    private static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24))
            + Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff))
            + Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff))
            + Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return paMinusPb <= 0 ? a : b;
    }

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        uint a = Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24));
        uint r = Clip255((int)((c0 >> 16) & 0xff) + (int)((c1 >> 16) & 0xff) - (int)((c2 >> 16) & 0xff));
        uint g = Clip255((int)((c0 >> 8) & 0xff) + (int)((c1 >> 8) & 0xff) - (int)((c2 >> 8) & 0xff));
        uint b = Clip255((int)(c0 & 0xff) + (int)(c1 & 0xff) - (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint average = Average2(c0, c1);
        uint a = AddSubtractHalf((int)(average >> 24), (int)(c2 >> 24));
        uint r = AddSubtractHalf((int)((average >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        uint g = AddSubtractHalf((int)((average >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        uint b = AddSubtractHalf((int)(average & 0xff), (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint AddSubtractHalf(int a, int b) => Clip255(a + ((a - b) / 2));

    private static uint Clip255(int v) => (uint)Math.Clamp(v, 0, 255);
}
