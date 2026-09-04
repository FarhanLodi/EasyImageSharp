using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using Xunit.Sdk;

namespace EasyImageSharp.Tests;

/// <summary>
/// One differential test vector: a complete zlib stream and the bytes it must inflate to. <see cref="Expected"/>
/// never comes from this library - for generated cases it is the payload that was handed to
/// <see cref="System.IO.Compression.ZLibStream"/>'s compressor, and for hand-assembled cases it is
/// <see cref="System.IO.Compression.ZLibStream"/>'s own decode of the stream, which the generator additionally
/// checks against the bytes it believes it encoded. <see cref="Recipe"/> is a human-readable description of how
/// the stream was built, quoted in assertion messages so a failure can be reproduced without a debugger.
/// </summary>
internal sealed record InflateCase(string Name, byte[] Compressed, byte[] Expected, string Recipe);

/// <summary>
/// The corpus the managed inflater is measured against: randomised payloads compressed by the framework
/// (<see cref="System.IO.Compression.ZLibStream"/>, i.e. native zlib/zlib-ng), block shapes hand-assembled bit by
/// bit with <see cref="DeflateBitWriter"/> that the framework compressor never emits, and a table of malformed
/// streams. Ground truth is always zlib: <see cref="ZlibReference"/> is the only oracle, and every hand-built
/// stream is decoded through it before it is yielded, so the generator cannot smuggle in its own idea of what
/// DEFLATE means. Nothing here references EasyImageSharp.
/// </summary>
internal static class InflateTestStreams
{
    /// <summary>Seed used by <see cref="Corpus"/>; any fixed value works, this one is the date it was written.</summary>
    public const int DefaultSeed = 20260903;

    /// <summary>
    /// Name prefix of the malformed streams that are truncations rather than structural errors; see
    /// <see cref="Malformed"/> for what that means for the accept/reject comparison against zlib.
    /// </summary>
    public const string TruncatedPrefix = "truncated-";

    /// <summary>The four levels a caller of <c>ZLibStream</c> can pick, each producing a different block mix.</summary>
    private static readonly CompressionLevel[] Levels =
    {
        CompressionLevel.NoCompression,
        CompressionLevel.Fastest,
        CompressionLevel.Optimal,
        CompressionLevel.SmallestSize,
    };

    private static readonly ConcurrentDictionary<(int Seed, int Count), IReadOnlyList<InflateCase>> GeneratedCache = new();

    private static readonly Lazy<IReadOnlyList<InflateCase>> HandBuiltCache =
        new(() => BuildHandBuilt(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<(string Name, byte[] Compressed)>> MalformedCache =
        new(() => BuildMalformed(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<(IReadOnlyList<InflateCase> Ordered, Dictionary<string, InflateCase> ByName)> CorpusCache =
        new(() => IndexCorpus(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Every generated and hand-built case, cached. Materialising the corpus once matters: the differential suite
    /// walks it several times (whole buffer, segmentation, output chunking) and recompressing a 4 MiB payload at
    /// <see cref="CompressionLevel.SmallestSize"/> per theory would dominate the run.
    /// </summary>
    public static IReadOnlyList<InflateCase> Corpus => CorpusCache.Value.Ordered;

    /// <summary>The malformed table, cached; see <see cref="Malformed"/>.</summary>
    public static IReadOnlyList<(string Name, byte[] Compressed)> MalformedCorpus => MalformedCache.Value;

    /// <summary>Looks a <see cref="Corpus"/> entry up by name, for <c>[MemberData]</c> theories that pass names.</summary>
    public static InflateCase ByName(string name)
        => CorpusCache.Value.ByName.TryGetValue(name, out InflateCase? found)
            ? found
            : throw new XunitException($"No inflate case named '{name}'; InflateTestStreams.Corpus has {CorpusCache.Value.Ordered.Count} entries.");

    /// <summary>
    /// The oracle. Decompresses through <see cref="System.IO.Compression.ZLibStream"/>, which is native
    /// zlib (net8.0) or zlib-ng (net10.0) - an implementation entirely outside this repository.
    /// </summary>
    public static byte[] ZlibReference(byte[] compressed)
    {
        using var source = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// ADLER-32 exactly as RFC 1950 section 9 states it: <c>a</c> starts at 1, <c>b</c> at 0, both reduced modulo
    /// 65521, result <c>b &lt;&lt; 16 | a</c>. Written from the specification rather than shared with
    /// <c>EasyImageSharp.Internal.Adler32</c> so the tests do not check an implementation against itself.
    /// </summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Randomised streams: every payload family below crossed with all four compression levels. The payload
    /// families are chosen for the block shapes they make the framework compressor emit - incompressible data
    /// gives stored blocks, tiny payloads give fixed-Huffman blocks, skewed alphabets give dynamic blocks with
    /// deep trees, periodic data gives long match runs at a known distance. <paramref name="count"/> caps the
    /// number of cases (0 or less yields all of them); families are interleaved so a truncated run stays diverse.
    /// </summary>
    public static IEnumerable<InflateCase> Generated(int seed, int count)
    {
        int emitted = 0;
        foreach ((string name, string recipe, byte[] payload) in Payloads(seed))
        {
            foreach (CompressionLevel level in Levels)
            {
                if (count > 0 && emitted >= count)
                {
                    yield break;
                }

                byte[] compressed = ZlibCompress(payload, level);
                yield return new InflateCase($"{name}-{level.ToString().ToLowerInvariant()}", compressed, payload, $"{recipe}; level={level}");
                emitted++;
            }
        }
    }

    /// <summary>
    /// The same list as <see cref="Generated"/> but materialised and cached per (seed, count).
    /// </summary>
    public static IReadOnlyList<InflateCase> GeneratedCorpus(int seed, int count)
        => GeneratedCache.GetOrAdd((seed, count), key => Generated(key.Seed, key.Count).ToArray());

    /// <summary>
    /// Block shapes assembled bit by bit, covering the encodings a conformant DEFLATE encoder is allowed to emit
    /// but zlib's own compressor never does: degenerate and single-symbol trees, incomplete distance trees, the
    /// minimum HCLEN, the code-length repeat codes at both extremes (including a repeat that spans the
    /// HLIT/HDIST boundary), every length and distance code with its minimum and maximum extra bits, matches at
    /// the maximum distance and length, all nine block-type transitions, and matches that reach back into an
    /// earlier block. Every stream is decoded through <see cref="ZlibReference"/> and compared with the bytes the
    /// generator believes it encoded before it is yielded, so zlib remains the oracle.
    /// </summary>
    public static IEnumerable<InflateCase> HandBuilt() => HandBuiltCache.Value;

    /// <summary>
    /// Broken streams for the decoder-contract test. They are not asserted to be rejected here - the caller
    /// compares its own verdict against <see cref="ZlibReference"/>'s - but every one of them is either
    /// structurally invalid per RFC 1950/1951 or truncated.
    /// <para>
    /// The two groups behave differently in the oracle, which is why they are told apart by name. Every stream
    /// whose name starts with <see cref="TruncatedPrefix"/> ends early, and <c>ZLibStream</c> reports a short read
    /// rather than throwing for all of them - it returns whatever it managed to decode and then end-of-stream,
    /// even when the missing bytes were the ADLER-32 trailer. Measured on both net8.0 (zlib) and net10.0
    /// (zlib-ng); the verdicts were identical on the two frameworks for all of these streams. Every stream
    /// without that prefix is rejected by <c>ZLibStream</c> with an <see cref="InvalidDataException"/> (a
    /// <c>ZLibException</c> for the preset-dictionary case). So an inflater that treats a truncated stream as an
    /// error - which the PNG path must, since a short IDAT means a short image - is expected to diverge from the
    /// oracle on exactly the <see cref="TruncatedPrefix"/> group and nowhere else.
    /// </para>
    /// </summary>
    public static IEnumerable<(string Name, byte[] Compressed)> Malformed() => MalformedCache.Value;

    // ---------------------------------------------------------------------------------------------------------
    // Generated payloads
    // ---------------------------------------------------------------------------------------------------------

    private static byte[] ZlibCompress(byte[] payload, CompressionLevel level)
    {
        var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, level, leaveOpen: true))
        {
            zlib.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Builds the payload families and interleaves them so any prefix of the list stays varied.</summary>
    private static List<(string Name, string Recipe, byte[] Data)> Payloads(int seed)
    {
        var families = new List<List<(string Name, string Recipe, byte[] Data)>>();

        // Sizes around every boundary an inflater cares about: empty, one byte, the 258-byte maximum match, the
        // 32 KiB window, and 64 KiB (the maximum stored-block payload).
        int[] sizes = { 0, 1, 2, 3, 5, 7, 8, 15, 16, 17, 31, 63, 127, 255, 256, 257, 258, 259, 511, 512, 1023, 4096, 16384, 32767, 32768, 32769, 65535, 65536, 65537 };
        var sizeFamily = new List<(string, string, byte[])>();
        foreach (int size in sizes)
        {
            var random = new Random(seed + size);
            sizeFamily.Add(($"size-{size}", $"family=size length={size} seed={seed + size}", MixedBytes(random, size)));
        }

        families.Add(sizeFamily);

        var uniform = new List<(string, string, byte[])>();
        foreach (int size in new[] { 17, 1024, 9973, 33000, 70000 })
        {
            var random = new Random(seed + 1000 + size);
            uniform.Add(($"uniform-{size}", $"family=uniform-random length={size} seed={seed + 1000 + size}", RandomBytes(random, size)));
        }

        families.Add(uniform);

        var constant = new List<(string, string, byte[])>();
        foreach (int size in new[] { 258, 259, 32768, 100000 })
        {
            foreach (byte value in new byte[] { 0x00, 0xFF })
            {
                byte[] data = new byte[size];
                data.AsSpan().Fill(value);
                constant.Add(($"constant-{value:x2}-{size}", $"family=constant byte=0x{value:x2} length={size}", data));
            }
        }

        families.Add(constant);

        var twoSymbol = new List<(string, string, byte[])>();
        foreach (int size in new[] { 300, 5000, 40000 })
        {
            var random = new Random(seed + 2000 + size);
            twoSymbol.Add(($"two-symbol-{size}", $"family=two-symbol alphabet={{0x00,0x01}} length={size}", TwoSymbolBytes(random, size)));
        }

        families.Add(twoSymbol);

        var singleSymbol = new List<(string, string, byte[])>();
        foreach (int size in new[] { 3, 4096, 70000 })
        {
            byte[] data = new byte[size];
            data.AsSpan().Fill(0x41);
            singleSymbol.Add(($"single-symbol-{size}", $"family=single-symbol byte=0x41 length={size}", data));
        }

        families.Add(singleSymbol);

        var zipf = new List<(string, string, byte[])>();
        foreach (double exponent in new[] { 0.7, 1.1, 1.8, 2.6 })
        {
            var random = new Random(seed + (int)(exponent * 100));
            int size = 65536;
            zipf.Add(($"zipf-{exponent:0.0}", $"family=zipf exponent={exponent:0.0} length={size} seed={seed + (int)(exponent * 100)}", ZipfBytes(random, size, exponent)));
        }

        families.Add(zipf);

        // Periods straddling every vector width and the window size; a match of length 258 at distance 1..3 is the
        // shape a PNG of flat colour produces, and periods at 32767..32769 sit on both sides of the window edge.
        var periodic = new List<(string, string, byte[])>();
        foreach (int period in new[] { 1, 2, 3, 4, 7, 8, 15, 16, 17, 255, 256, 257, 258, 32767, 32768, 32769 })
        {
            var random = new Random(seed + 3000 + period);
            int length = Math.Max(period * 4, 8192);
            periodic.Add(($"periodic-{period}", $"family=periodic period={period} length={length}", PeriodicBytes(random, period, length, 0)));
            if (period is 3 or 16 or 257 or 32768)
            {
                var noisy = new Random(seed + 4000 + period);
                periodic.Add(($"periodic-{period}-noisy", $"family=periodic period={period} length={length} noise=1/64", PeriodicBytes(noisy, period, length, 64)));
            }
        }

        families.Add(periodic);

        var pngLike = new List<(string, string, byte[])>();
        foreach ((int width, int bytesPerPixel, int height) in new[] { (1, 1, 1), (13, 1, 7), (320, 4, 64), (640, 3, 48), (1024, 2, 33) })
        {
            var random = new Random(seed + 5000 + (width * bytesPerPixel));
            byte[] data = PngLikeBytes(random, width, bytesPerPixel, height);
            pngLike.Add(($"png-like-{width}x{height}x{bytesPerPixel}", $"family=png-like width={width} bpp={bytesPerPixel} height={height} stride={(width * bytesPerPixel) + 1}", data));
        }

        families.Add(pngLike);

        var longDistance = new List<(string, string, byte[])>();
        foreach (int size in new[] { 33000, 40000, 70000, 200000 })
        {
            var random = new Random(seed + 6000 + size);
            longDistance.Add(($"long-distance-{size}", $"family=long-distance length={size} (repeats 32768 bytes back)", LongDistanceBytes(random, size)));
        }

        families.Add(longDistance);

        var mixed = new List<(string, string, byte[])>();
        foreach (int size in new[] { 2000, 20000, 90000, 300000 })
        {
            var random = new Random(seed + 7000 + size);
            mixed.Add(($"noise-and-runs-{size}", $"family=noise-and-runs length={size} (alternating incompressible and constant sections)", NoiseAndRunsBytes(random, size)));
        }

        families.Add(mixed);

        var text = new List<(string, string, byte[])>();
        foreach (int size in new[] { 700, 30000, 250000 })
        {
            var random = new Random(seed + 8000 + size);
            text.Add(($"text-{size}", $"family=text length={size} (word-like, Zipf word frequencies)", TextLikeBytes(random, size)));
        }

        families.Add(text);

        var sparse = new List<(string, string, byte[])>();
        foreach (int oneIn in new[] { 17, 97, 1009 })
        {
            var random = new Random(seed + 9000 + oneIn);
            int size = 120000;
            sparse.Add(($"sparse-1in{oneIn}", $"family=sparse length={size} density=1/{oneIn}", SparseBytes(random, size, oneIn)));
        }

        families.Add(sparse);

        // One payload past 4 MiB, so the window is rewound dozens of times and the stream contains hundreds of
        // dynamic blocks. Text-like so level 9 stays quick.
        var huge = new List<(string, string, byte[])>
        {
            ("huge-4mib", "family=huge length=4718592 (text-like, > 4 MiB)", TextLikeBytes(new Random(seed + 99), 4 * 1024 * 1024 + 524288)),
        };

        families.Add(huge);

        var interleaved = new List<(string Name, string Recipe, byte[] Data)>();
        int longest = families.Max(f => f.Count);
        for (int i = 0; i < longest; i++)
        {
            foreach (List<(string Name, string Recipe, byte[] Data)> family in families)
            {
                if (i < family.Count)
                {
                    interleaved.Add(family[i]);
                }
            }
        }

        return interleaved;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static byte[] TwoSymbolBytes(Random random, int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(random.Next(2) == 0 ? 0x00 : 0x01);
        }

        return data;
    }

    private static byte[] PeriodicBytes(Random random, int period, int length, int noiseOneIn)
    {
        byte[] pattern = RandomBytes(random, period);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = pattern[i % period];
            if (noiseOneIn > 0 && random.Next(noiseOneIn) == 0)
            {
                data[i] = (byte)random.Next(256);
            }
        }

        return data;
    }

    private static byte[] ZipfBytes(Random random, int length, double exponent)
    {
        // A skewed alphabet is what forces the compressor into dynamic blocks with codes of many different
        // lengths; the permutation keeps the popular symbols from being the low byte values.
        double[] cumulative = new double[256];
        double total = 0;
        for (int i = 0; i < 256; i++)
        {
            total += 1.0 / Math.Pow(i + 1, exponent);
            cumulative[i] = total;
        }

        byte[] permutation = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            permutation[i] = (byte)i;
        }

        for (int i = 255; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }

        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            double pick = random.NextDouble() * total;
            int symbol = Array.BinarySearch(cumulative, pick);
            if (symbol < 0)
            {
                symbol = ~symbol;
            }

            data[i] = permutation[Math.Min(symbol, 255)];
        }

        return data;
    }

    private static byte[] SparseBytes(Random random, int length, int oneIn)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (random.Next(oneIn) == 0)
            {
                data[i] = (byte)(1 + random.Next(255));
            }
        }

        return data;
    }

    private static byte[] MixedBytes(Random random, int length)
    {
        // Runs, gradients and noise in short sections, so even a 3-byte payload has structure and a 64 KiB one
        // contains every match length the compressor can pick.
        byte[] data = new byte[length];
        int position = 0;
        byte value = (byte)random.Next(256);
        while (position < length)
        {
            int kind = random.Next(3);
            int run = Math.Min(length - position, 1 + random.Next(kind == 0 ? 300 : 40));
            for (int i = 0; i < run; i++)
            {
                data[position + i] = kind switch
                {
                    0 => value,
                    1 => (byte)(value + i),
                    _ => (byte)random.Next(256),
                };
            }

            position += run;
            value = (byte)random.Next(256);
        }

        return data;
    }

    private static byte[] NoiseAndRunsBytes(Random random, int length)
    {
        // Alternating incompressible and highly compressible sections make the framework compressor switch block
        // types repeatedly, which is the cheapest way to get stored/fixed/dynamic transitions out of it.
        byte[] data = new byte[length];
        int position = 0;
        while (position < length)
        {
            int run = Math.Min(length - position, 200 + random.Next(9000));
            if (random.Next(2) == 0)
            {
                random.NextBytes(data.AsSpan(position, run));
            }
            else
            {
                data.AsSpan(position, run).Fill((byte)random.Next(256));
            }

            position += run;
        }

        return data;
    }

    private static byte[] LongDistanceBytes(Random random, int length)
    {
        // The first 32 KiB is noise; everything after it repeats a slice from exactly 32768 bytes back, so the
        // compressor emits matches at or near the largest legal distance.
        byte[] data = new byte[length];
        int prefix = Math.Min(length, 32768);
        random.NextBytes(data.AsSpan(0, prefix));
        int position = prefix;
        while (position < length)
        {
            int run = Math.Min(length - position, 1 + random.Next(400));
            int source = Math.Max(0, position - 32768);
            for (int i = 0; i < run; i++)
            {
                data[position + i] = data[source + i];
            }

            position += run;
            int gap = Math.Min(length - position, random.Next(60));
            random.NextBytes(data.AsSpan(position, gap));
            position += gap;
        }

        return data;
    }

    private static byte[] TextLikeBytes(Random random, int length)
    {
        string[] words =
        {
            "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "image", "sharp", "deflate",
            "inflate", "huffman", "window", "distance", "literal", "length", "block", "stream", "png",
        };

        byte[] data = new byte[length];
        int position = 0;
        while (position < length)
        {
            // Zipf-ish word choice: index biased towards the front of the table.
            int index = (int)(Math.Pow(random.NextDouble(), 2.0) * words.Length);
            string word = words[Math.Min(index, words.Length - 1)];
            for (int i = 0; i < word.Length && position < length; i++)
            {
                data[position++] = (byte)word[i];
            }

            if (position < length)
            {
                data[position++] = (byte)(random.Next(12) == 0 ? '\n' : ' ');
            }
        }

        return data;
    }

    private static byte[] PngLikeBytes(Random random, int width, int bytesPerPixel, int height)
    {
        // A filter byte followed by a scanline, which is exactly what the PNG decoder asks the inflater for; the
        // filter byte breaks the periodicity of the row data, so matches never line up with the stride.
        int stride = (width * bytesPerPixel) + 1;
        byte[] data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            data[row] = (byte)(y % 5);
            for (int x = 0; x < width * bytesPerPixel; x++)
            {
                data[row + 1 + x] = (byte)((x * 3) + (y * 7) + (random.Next(8) == 0 ? random.Next(64) : 0));
            }
        }

        return data;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Hand-assembled streams
    // ---------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<InflateCase> BuildHandBuilt()
    {
        var cases = new List<InflateCase>();

        // ----- stored blocks -----
        var builder = new StreamBuilder();
        builder.Stored(true, ReadOnlySpan<byte>.Empty);
        cases.Add(builder.Build("stored-empty-final", "one final stored block, LEN=0"));

        builder = new StreamBuilder();
        builder.Stored(false, ReadOnlySpan<byte>.Empty);
        builder.Stored(false, ReadOnlySpan<byte>.Empty);
        builder.Stored(true, "tail"u8);
        cases.Add(builder.Build("stored-empty-then-data", "two zero-length stored blocks then a final stored block"));

        builder = new StreamBuilder();
        for (int i = 0; i < 39; i++)
        {
            byte[] chunk = new byte[i % 5];
            chunk.AsSpan().Fill((byte)i);
            builder.Stored(false, chunk);
        }

        builder.Stored(true, "end"u8);
        cases.Add(builder.Build("stored-forty-blocks", "40 consecutive stored blocks, several of them zero-length"));

        builder = new StreamBuilder();
        byte[] maxStored = new byte[65535];
        for (int i = 0; i < maxStored.Length; i++)
        {
            maxStored[i] = (byte)(i * 7);
        }

        builder.Stored(true, maxStored);
        cases.Add(builder.Build("stored-max-length", "a single stored block with the maximum LEN of 65535"));

        // ----- fixed-Huffman blocks -----
        builder = new StreamBuilder();
        builder.Fixed(true, Array.Empty<DeflateToken>());
        cases.Add(builder.Build("fixed-eob-only", "a final fixed-Huffman block containing nothing but the end-of-block symbol"));

        builder = new StreamBuilder();
        var allLiterals = new List<DeflateToken>();
        for (int i = 0; i < 256; i++)
        {
            allLiterals.Add(DeflateToken.Byte((byte)i));
        }

        builder.Fixed(true, allLiterals);
        cases.Add(builder.Build("fixed-all-literal-values", "every literal 0..255 in a fixed block: 8-bit codes for 0..143, 9-bit codes for 144..255"));

        builder = new StreamBuilder();
        builder.Fixed(true, new[] { DeflateToken.Byte((byte)'x'), DeflateToken.Match(258, 1) });
        cases.Add(builder.Build("fixed-length-285-distance-1", "length code 285 (258 bytes, no extra bits) at distance 1: a 259-byte run"));

        builder = new StreamBuilder();
        builder.Fixed(true, new[] { DeflateToken.Byte((byte)'x'), DeflateToken.MatchWithLengthCode(258, 1, 284) });
        cases.Add(builder.Build("fixed-length-284-extra-31", "length 258 spelled as code 284 with all five extra bits set, which RFC 1951 does not generate but zlib decodes"));

        builder = new StreamBuilder();
        builder.Fixed(true, new[] { DeflateToken.Byte((byte)'x'), DeflateToken.MatchWithLengthCode(257, 1, 284) });
        cases.Add(builder.Build("fixed-length-284-extra-30", "length code 284 with 30 in its five extra bits (length 257)"));

        cases.Add(BuildLengthCodeSweep());
        cases.Add(BuildDistanceCodeSweep());
        cases.Add(BuildOverlapSweep());

        builder = new StreamBuilder();
        builder.Fixed(false, new[] { DeflateToken.Byte(65), DeflateToken.Byte(66), DeflateToken.Byte(67), DeflateToken.Byte(68), DeflateToken.Byte(69) });
        builder.Fixed(true, new[] { DeflateToken.Match(258, 5) });
        cases.Add(builder.Build("fixed-match-distance-equals-output", "a match whose distance equals the number of bytes produced so far, so the copy source starts at output offset 0"));

        // A stored block must skip to the next byte boundary; get both the 'discard 1..7 bits' and the
        // 'discard nothing' paths, the second by padding the preceding block until it ends 3 bits short of a byte.
        builder = new StreamBuilder();
        builder.Fixed(false, new[] { DeflateToken.Byte(65) });
        builder.Stored(true, "misaligned"u8);
        cases.Add(builder.Build("fixed-then-misaligned-stored", "a stored block after a fixed block that ended mid-byte: the padding bits must be discarded"));

        builder = new StreamBuilder();
        var pad = new List<DeflateToken> { DeflateToken.Byte(65) };
        while (((3 + pad.Sum(t => t.Literal <= 143 ? 8 : 9) + 7 + 3) % 8) != 0)
        {
            pad.Add(DeflateToken.Byte(200));
        }

        builder.Fixed(false, pad);
        builder.Stored(true, "aligned"u8);
        cases.Add(builder.Build("fixed-then-aligned-stored", "a stored block whose header lands exactly on a byte boundary, so zero padding bits are discarded"));

        // ----- dynamic blocks -----
        cases.AddRange(BuildDynamicCases());

        // ----- multi-block streams -----
        cases.AddRange(BuildTransitionCases());

        builder = new StreamBuilder();
        builder.Stored(false, "stored-section-"u8);
        builder.Fixed(false, new[] { DeflateToken.Byte((byte)'f'), DeflateToken.Byte((byte)'x'), DeflateToken.Match(12, 15) });
        builder.Dynamic(false, new[] { DeflateToken.Byte((byte)'d'), DeflateToken.Match(20, 30), DeflateToken.Match(3, 1) });
        builder.Stored(true, "-final-stored"u8);
        cases.Add(builder.Build("mixed-blocks-final-is-stored", "stored, fixed and dynamic blocks in one stream with BFINAL set on the trailing stored block"));

        builder = new StreamBuilder();
        builder.Stored(false, "abcdefghijklmnop"u8);
        builder.Fixed(false, new[] { DeflateToken.Match(16, 16) });
        builder.Dynamic(true, new[] { DeflateToken.Match(32, 32), DeflateToken.Match(3, 48) });
        cases.Add(builder.Build("cross-block-match-history", "every match reaches back into a previous block's output, so history must survive the block boundary"));

        cases.Add(BuildMaxDistanceAcrossBlocks());
        cases.Add(BuildLongMatchRun());
        cases.Add(BuildWindowRewindStream());

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (InflateCase built in cases)
        {
            if (!names.Add(built.Name))
            {
                throw new XunitException($"InflateTestStreams.HandBuilt produced two cases named '{built.Name}'.");
            }
        }

        return cases;
    }

    private static InflateCase BuildLengthCodeSweep()
    {
        // Every length code with its smallest and largest legal extra-bit value, so no base or extra-bit table
        // entry goes untested. Distances rotate through the copy tiers a vectorised implementation branches on.
        var builder = new StreamBuilder();
        var prologue = new List<DeflateToken>();
        for (int i = 0; i < 320; i++)
        {
            prologue.Add(DeflateToken.Byte((byte)((i * 11) + (i / 32))));
        }

        builder.Fixed(false, prologue);

        var tokens = new List<DeflateToken>();
        int[] distances = { 1, 2, 3, 4, 8, 16, 17, 33, 320 };
        int index = 0;
        for (int code = 257; code <= 285; code++)
        {
            int extraBits = DeflateBitWriter.LengthExtraBits[code - 257];
            int minLength = DeflateBitWriter.LengthBase[code - 257];
            int maxLength = Math.Min(258, minLength + (1 << extraBits) - 1);
            tokens.Add(DeflateToken.MatchWithLengthCode(minLength, distances[index++ % distances.Length], code));
            if (maxLength != minLength)
            {
                tokens.Add(DeflateToken.MatchWithLengthCode(maxLength, distances[index++ % distances.Length], code));
            }
        }

        builder.Fixed(true, tokens);
        return builder.Build(
            "fixed-length-code-sweep",
            "every length code 257..285 at its minimum and maximum extra-bit value, distances cycling through 1,2,3,4,8,16,17,33,320");
    }

    private static InflateCase BuildDistanceCodeSweep()
    {
        // 32 KiB of history first, so even distance code 29 at its maximum extra value (32768) is legal.
        var builder = new StreamBuilder();
        byte[] history = new byte[32768];
        for (int i = 0; i < history.Length; i++)
        {
            history[i] = (byte)((i * 7) + (i / 251));
        }

        builder.Stored(false, history);

        var tokens = new List<DeflateToken>();
        for (int code = 0; code < 30; code++)
        {
            int extraBits = DeflateBitWriter.DistanceExtraBits[code];
            int minDistance = DeflateBitWriter.DistanceBase[code];
            int maxDistance = Math.Min(32768, minDistance + (1 << extraBits) - 1);
            tokens.Add(DeflateToken.Match(3, minDistance));
            if (maxDistance != minDistance)
            {
                tokens.Add(DeflateToken.Match(258, maxDistance));
            }
        }

        builder.Fixed(true, tokens);
        return builder.Build(
            "fixed-distance-code-sweep",
            "32768 bytes of stored history, then every distance code 0..29 at its minimum and maximum extra-bit value (up to the 32768 maximum)");
    }

    private static InflateCase BuildOverlapSweep()
    {
        // Distances 1..17 and 31..33 are where a copy loop that reads 8 or 16 bytes at a time overlaps its own
        // output; lengths 3 and 258 are the extremes of the run it has to produce.
        var builder = new StreamBuilder();
        var seed = new List<DeflateToken>();
        for (int i = 0; i < 64; i++)
        {
            seed.Add(DeflateToken.Byte((byte)(i + 1)));
        }

        builder.Fixed(false, seed);

        var tokens = new List<DeflateToken>();
        int[] distances = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 31, 32, 33, 64 };
        foreach (int distance in distances)
        {
            tokens.Add(DeflateToken.Match(3, distance));
            tokens.Add(DeflateToken.Match(distance + 2, distance));
            tokens.Add(DeflateToken.Match(258, distance));
        }

        builder.Fixed(true, tokens);
        return builder.Build(
            "fixed-overlapping-copy-sweep",
            "matches of length 3, distance+2 and 258 at every distance in 1..17 plus 31,32,33,64 - the overlap cases a chunked copy gets wrong");
    }

    private static List<InflateCase> BuildDynamicCases()
    {
        var cases = new List<InflateCase>();

        var builder = new StreamBuilder();
        builder.Dynamic(true, new[] { DeflateToken.Byte((byte)'a'), DeflateToken.Byte((byte)'b'), DeflateToken.Match(6, 2) });
        cases.Add(builder.Build("dynamic-basic", "a dynamic block whose trees are derived from the tokens it contains"));

        // HCLEN is the number of code-length codes present, minimum 4 - but with only symbols 16, 17, 18 and 0
        // available no non-zero code length can be spelled, so the smallest usable value is 5 (adding symbol 8).
        int[] eightBitLiterals = new int[257];
        for (int i = 0; i < 255; i++)
        {
            eightBitLiterals[i] = 8;
        }

        eightBitLiterals[256] = 8;

        builder = new StreamBuilder();
        builder.Dynamic(true, new[] { DeflateToken.Byte(1), DeflateToken.Byte(2), DeflateToken.Byte(3) }, eightBitLiterals, new[] { 0 }, codeLengthCount: 5);
        cases.Add(builder.Build("dynamic-hclen-5", "HCLEN=5, the smallest value that can express a decodable block: 256 literal codes of 8 bits and an empty distance tree"));

        builder = new StreamBuilder();
        builder.Dynamic(true, new[] { DeflateToken.Byte(1), DeflateToken.Byte(2), DeflateToken.Byte(3) }, eightBitLiterals, new[] { 0 }, codeLengthCount: 19);
        cases.Add(builder.Build("dynamic-hclen-19", "HCLEN=19: all nineteen code-length code lengths written, most of them zero"));

        builder = new StreamBuilder();
        builder.Dynamic(true, new[] { DeflateToken.Byte((byte)'a'), DeflateToken.Byte((byte)'b'), DeflateToken.Match(9, 2) }, useRepeatCodes: false);
        cases.Add(builder.Build("dynamic-no-repeat-codes", "code lengths written one symbol at a time, never using the repeat codes 16, 17 or 18"));

        cases.Add(BuildRepeatCodeCase());
        cases.Add(BuildBoundarySpanningRepeatCase());

        // Degenerate trees. zlib accepts an incomplete literal/length or distance code when the longest code is a
        // single bit, and an entirely empty distance tree; a builder that insists on a complete tree rejects all
        // three, which is the single most likely source of divergence.
        int[] eobOnly = new int[257];
        eobOnly[256] = 1;
        builder = new StreamBuilder();
        builder.Dynamic(true, Array.Empty<DeflateToken>(), eobOnly, new[] { 0 });
        cases.Add(builder.Build("dynamic-eob-only-incomplete-tree", "literal/length tree holding a single one-bit code (end-of-block); incomplete, but legal for zlib, and decodes to nothing"));

        int[] literalAndEob = new int[257];
        literalAndEob['q'] = 1;
        literalAndEob[256] = 1;
        var repeated = new List<DeflateToken>();
        for (int i = 0; i < 300; i++)
        {
            repeated.Add(DeflateToken.Byte((byte)'q'));
        }

        builder = new StreamBuilder();
        builder.Dynamic(true, repeated, literalAndEob, new[] { 0 });
        cases.Add(builder.Build("dynamic-two-symbol-tree", "a complete literal/length tree of exactly two one-bit codes, one literal and end-of-block"));

        int[] withLength = new int[258];
        withLength['a'] = 2;
        withLength[256] = 2;
        withLength[257] = 1;
        builder = new StreamBuilder();
        builder.Dynamic(true, new[] { DeflateToken.Byte((byte)'a'), DeflateToken.Match(3, 1) }, withLength, new[] { 1 });
        cases.Add(builder.Build("dynamic-single-distance-code", "a distance tree holding one incomplete one-bit code, which zlib accepts and uses"));

        int[] literalsOnly = new int[257];
        literalsOnly['a'] = 1;
        literalsOnly[256] = 1;
        var nine = new List<DeflateToken>();
        for (int i = 0; i < 9; i++)
        {
            nine.Add(DeflateToken.Byte((byte)'a'));
        }

        builder = new StreamBuilder();
        builder.Dynamic(true, nine, literalsOnly, new[] { 0 });
        cases.Add(builder.Build("dynamic-empty-distance-tree", "HDIST=1 with a zero code length: no distance code at all, and no match to use one"));

        builder = new StreamBuilder();
        builder.Dynamic(true, nine, literalsOnly, new[] { 1, 1 });
        cases.Add(builder.Build("dynamic-unused-distance-tree", "a complete two-code distance tree that the block never uses"));

        // Codes longer than the root table (9 bits for literals, 6 for distances) force the second table level.
        int[] deepLiterals = new int[286];
        int[] deepOrder = { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 256, 257 };
        for (int i = 0; i < deepOrder.Length; i++)
        {
            deepLiterals[deepOrder[i]] = i < 14 ? i + 1 : 15;
        }

        builder = new StreamBuilder();
        builder.Dynamic(
            true,
            new[] { DeflateToken.Byte((byte)'a'), DeflateToken.Byte((byte)'n'), DeflateToken.Byte((byte)'g'), DeflateToken.Match(3, 1) },
            deepLiterals,
            null);
        cases.Add(builder.Build("dynamic-15-bit-literal-codes", "literal/length code lengths 1,2,3..14,15,15 - a complete tree whose longest codes need a second-level table"));

        int[] deepDistances = new int[16];
        for (int i = 0; i < 13; i++)
        {
            deepDistances[i] = i + 1;
        }

        deepDistances[13] = 14;
        deepDistances[14] = 15;
        deepDistances[15] = 15;
        builder = new StreamBuilder();
        byte[] history = new byte[1000];
        for (int i = 0; i < history.Length; i++)
        {
            history[i] = (byte)(i * 13);
        }

        builder.Stored(false, history);
        builder.Dynamic(
            true,
            new[] { DeflateToken.Match(3, 1), DeflateToken.Match(4, 5), DeflateToken.Match(5, 100), DeflateToken.Match(6, 200) },
            null,
            deepDistances);
        cases.Add(builder.Build("dynamic-15-bit-distance-codes", "distance code lengths 1,2,3..13,14,15,15, so distance decoding needs a second-level table too"));

        // The largest header the format allows.
        int[] allLiterals = new int[286];
        int[] flat286 = DeflateBitWriter.EqualWeightLengths(286);
        for (int i = 0; i < 286; i++)
        {
            allLiterals[i] = flat286[i];
        }

        int[] allDistances = new int[30];
        int[] flat30 = DeflateBitWriter.EqualWeightLengths(30);
        for (int i = 0; i < 30; i++)
        {
            allDistances[i] = flat30[i];
        }

        builder = new StreamBuilder();
        byte[] window = new byte[32768];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = (byte)(i * 31);
        }

        builder.Stored(false, window);
        builder.Dynamic(
            true,
            new[] { DeflateToken.Byte(0), DeflateToken.Byte(255), DeflateToken.Match(3, 1), DeflateToken.Match(258, 32768), DeflateToken.Match(10, 300) },
            allLiterals,
            allDistances);
        cases.Add(builder.Build("dynamic-hlit-286-hdist-30", "the maximum header: all 286 literal/length codes and all 30 distance codes present"));

        builder = new StreamBuilder();
        for (int block = 0; block < 12; block++)
        {
            var tokens = new List<DeflateToken>();
            for (int i = 0; i <= block; i++)
            {
                tokens.Add(DeflateToken.Byte((byte)('A' + ((block * 7) + i) % 26)));
            }

            if (block > 0)
            {
                tokens.Add(DeflateToken.Match(3 + block, 1 + block));
            }

            builder.Dynamic(block == 11, tokens);
        }

        cases.Add(builder.Build("dynamic-twelve-blocks", "twelve dynamic blocks back to back, each with a different tree, so the tables are rebuilt eleven times"));

        return cases;
    }

    private static InflateCase BuildRepeatCodeCase()
    {
        // Code lengths laid out as runs the greedy encoder must spell with 16 repeating 3 and 6, 17 repeating 3
        // and 10, and 18 repeating 11 and 138 - and adding up to a complete tree: two 2-bit, one 3-bit, two 4-bit,
        // four 5-bit and eight 6-bit codes is a Kraft sum of exactly 1. The assertions below fail the generator if
        // any of the six repeat shapes stops appearing.
        int[] lengths = new int[257];
        int position = 0;
        FillRun(lengths, ref position, 5, 4);      // 5, then 16 repeating 3
        FillRun(lengths, ref position, 0, 11);     // 18 with extra 0
        FillRun(lengths, ref position, 6, 8);      // 6, then 16 repeating 6, then a lone 6
        FillRun(lengths, ref position, 0, 3);      // 17 with extra 0
        FillRun(lengths, ref position, 4, 2);
        FillRun(lengths, ref position, 0, 10);     // 17 with extra 7
        FillRun(lengths, ref position, 3, 1);
        FillRun(lengths, ref position, 0, 138);    // 18 with extra 127
        FillRun(lengths, ref position, 2, 1);
        int twoBitLiteral = position - 1;
        FillRun(lengths, ref position, 0, 256 - position);
        lengths[256] = 2;

        var builder = new StreamBuilder();
        IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> sequence = builder.Dynamic(
            true,
            new[] { DeflateToken.Byte(0), DeflateToken.Byte(15), DeflateToken.Byte(26), DeflateToken.Byte(38), DeflateToken.Byte((byte)twoBitLiteral) },
            lengths,
            new[] { 0 });

        RequireRepeat(sequence, 16, 3);
        RequireRepeat(sequence, 16, 6);
        RequireRepeat(sequence, 17, 3);
        RequireRepeat(sequence, 17, 10);
        RequireRepeat(sequence, 18, 11);
        RequireRepeat(sequence, 18, 138);

        return builder.Build(
            "dynamic-code-length-repeats",
            "code-length repeats at both extremes: 16 repeating 3 and 6, 17 repeating 3 and 10, 18 repeating 11 and 138");
    }

    /// <summary>Fills <paramref name="count"/> code lengths with <paramref name="value"/> and advances the cursor.</summary>
    private static void FillRun(int[] lengths, ref int position, int value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            lengths[position + i] = value;
        }

        position += count;
    }

    private static InflateCase BuildBoundarySpanningRepeatCase()
    {
        // The literal/length and distance code lengths are one array as far as the repeat codes are concerned: a
        // repeat started on the last literal/length symbol carries on into the distance lengths. An inflater that
        // reads the two alphabets separately decodes everything else correctly and fails only here.
        int[] lengths = new int[258];
        lengths['a'] = 2;
        lengths['b'] = 2;
        lengths[256] = 2;
        lengths[257] = 2;
        int[] distances = { 2, 2, 2, 2 };

        var builder = new StreamBuilder();
        IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> sequence = builder.Dynamic(
            true,
            new[] { DeflateToken.Byte((byte)'a'), DeflateToken.Byte((byte)'b'), DeflateToken.Match(3, 1), DeflateToken.Match(3, 4) },
            lengths,
            distances);

        (int Symbol, int Extra, int ExtraBits) last = sequence[sequence.Count - 1];
        if (last.Symbol != 16)
        {
            throw new XunitException(
                $"dynamic-repeat-spans-hlit-hdist expected the code-length stream to end in a repeat that crosses into the distance lengths, but it ends with symbol {last.Symbol}.");
        }

        return builder.Build(
            "dynamic-repeat-spans-hlit-hdist",
            "a code-length repeat (16) started on the last literal/length symbol and continuing into all four distance lengths");
    }

    private static List<InflateCase> BuildTransitionCases()
    {
        var cases = new List<InflateCase>();
        char[] kinds = { 's', 'f', 'd' };
        foreach (char first in kinds)
        {
            foreach (char second in kinds)
            {
                var builder = new StreamBuilder();
                for (int i = 0; i < 2; i++)
                {
                    char kind = i == 0 ? first : second;
                    bool final = i == 1;
                    switch (kind)
                    {
                        case 's':
                            byte[] chunk = new byte[7];
                            chunk.AsSpan().Fill((byte)('A' + i));
                            builder.Stored(final, chunk);
                            break;
                        case 'f':
                            builder.Fixed(final, LiteralsThenMatch((byte)('B' + i)));
                            break;
                        default:
                            builder.Dynamic(final, LiteralsThenMatch((byte)('C' + i)));
                            break;
                    }
                }

                cases.Add(builder.Build(
                    $"transition-{Describe(first)}-then-{Describe(second)}",
                    $"a {Describe(first)} block immediately followed by a {Describe(second)} block"));
            }
        }

        return cases;

        static List<DeflateToken> LiteralsThenMatch(byte value)
        {
            var tokens = new List<DeflateToken>();
            for (int i = 0; i < 4; i++)
            {
                tokens.Add(DeflateToken.Byte(value));
            }

            tokens.Add(DeflateToken.Match(3, 4));
            return tokens;
        }

        static string Describe(char kind) => kind switch { 's' => "stored", 'f' => "fixed", _ => "dynamic" };
    }

    private static InflateCase BuildMaxDistanceAcrossBlocks()
    {
        var builder = new StreamBuilder();
        byte[] window = new byte[32768];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = (byte)((i * 3) + (i / 97));
        }

        builder.Stored(false, window);
        builder.Fixed(false, new[] { DeflateToken.Match(258, 32768), DeflateToken.Match(3, 32768), DeflateToken.Match(4, 16385) });
        builder.Dynamic(true, new[] { DeflateToken.Match(258, 32768), DeflateToken.Match(5, 24577), DeflateToken.Byte(0xFF) });
        return builder.Build(
            "max-distance-across-blocks",
            "matches at the maximum distance of 32768 reaching back past two block boundaries into a stored block");
    }

    private static InflateCase BuildLongMatchRun()
    {
        var builder = new StreamBuilder();
        var tokens = new List<DeflateToken> { DeflateToken.Byte(0x5A) };
        for (int i = 0; i < 1000; i++)
        {
            tokens.Add(DeflateToken.Match(258, 1));
        }

        builder.Fixed(true, tokens);
        return builder.Build(
            "long-match-run-distance-1",
            "one literal followed by 1000 maximum-length matches at distance 1: 258001 bytes from a 1.3 KiB stream");
    }

    private static InflateCase BuildWindowRewindStream()
    {
        // Over half a megabyte of output with a maximum-distance match every 258 bytes. Any inflater that keeps a
        // 32 KiB history in a linear buffer has to slide that buffer at least eight times here, and every slide
        // happens while matches are reaching back to the very oldest byte it still holds.
        var builder = new StreamBuilder();
        byte[] window = new byte[32768];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = (byte)((i * 5) + (i / 37));
        }

        builder.Stored(false, window);
        for (int block = 0; block < 4; block++)
        {
            var tokens = new List<DeflateToken>();
            for (int i = 0; i < 600; i++)
            {
                tokens.Add(DeflateToken.Match(258, 32768));
                if (i % 100 == 99)
                {
                    tokens.Add(DeflateToken.Byte((byte)(block + i)));
                }
            }

            builder.Dynamic(block == 3, tokens);
        }

        return builder.Build(
            "window-rewind-max-distance",
            "652 KiB of output built from 2400 length-258 matches at distance 32768, spread over four dynamic blocks");
    }

    private static void RequireRepeat(IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> sequence, int symbol, int repeat)
    {
        int wanted = symbol == 18 ? repeat - 11 : repeat - 3;
        foreach ((int Symbol, int Extra, int ExtraBits) entry in sequence)
        {
            if (entry.Symbol == symbol && entry.Extra == wanted)
            {
                return;
            }
        }

        throw new XunitException(
            $"The hand-built code-length stream no longer contains code {symbol} repeating {repeat} times; the case was written to exercise exactly that.");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Malformed streams
    // ---------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<(string Name, byte[] Compressed)> BuildMalformed()
    {
        var cases = new List<(string Name, byte[] Compressed)>
        {
            ("truncated-empty-input", Array.Empty<byte>()),
            ("truncated-zlib-header", new byte[] { 0x78 }),
            ("truncated-after-zlib-header", new byte[] { 0x78, 0x9C }),
            ("header-bad-compression-method", StreamWithHeaderByte(0x77)),
            ("header-bad-window-size", StreamWithHeaderByte(0x88)),
            ("header-fcheck-mismatch", new byte[] { 0x78, 0x9D, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }),
        };

        // FDICT set: legal zlib, but a preset dictionary is not something a PNG or this decoder supports.
        var withDictionary = new List<byte> { 0x78, MakeFlg(0x78, 0x20) };
        withDictionary.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });
        withDictionary.AddRange(new byte[] { 0x03, 0x00 });
        withDictionary.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });
        cases.Add(("header-fdict-set", withDictionary.ToArray()));

        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 2);
        cases.Add(("block-type-3-reserved", writer.ToZlibStream(Array.Empty<byte>())));

        // Stored-block framing.
        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(0, 2);
        writer.AlignToByte();
        writer.WriteBits(4, 16);
        writer.WriteBits(4, 16);
        writer.WriteAlignedBytes("data"u8);
        cases.Add(("stored-len-nlen-mismatch", writer.ToZlibStream("data"u8.ToArray())));

        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(0, 2);
        writer.AlignToByte();
        writer.WriteBits(100, 16);
        writer.WriteBits((uint)(~100 & 0xFFFF), 16);
        writer.WriteAlignedBytes("short"u8);
        cases.Add(("truncated-stored-payload", writer.ToZlibStream("short"u8.ToArray())));

        cases.Add(("truncated-stored-header", new byte[] { 0x78, 0x9C, 0x01, 0x05 }));

        writer = new DeflateBitWriter();
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 2);
        writer.AlignToByte();
        writer.WriteBits(3, 16);
        writer.WriteBits((uint)(~3 & 0xFFFF), 16);
        writer.WriteAlignedBytes("abc"u8);
        var withoutFinalBlock = new List<byte> { 0x78, 0x9C };
        withoutFinalBlock.AddRange(writer.ToDeflate());
        cases.Add(("truncated-no-final-block", withoutFinalBlock.ToArray()));

        // Fixed-Huffman blocks using codes the format reserves as invalid.
        cases.Add(("fixed-distance-code-30", FixedBlockWithRawCodes(DeflateBitWriter.FixedLiteralCodes[257], 7, 30, 5)));
        cases.Add(("fixed-distance-code-31", FixedBlockWithRawCodes(DeflateBitWriter.FixedLiteralCodes[257], 7, 31, 5)));
        cases.Add(("fixed-literal-code-286", FixedBlockWithLiteralSymbol(286)));
        cases.Add(("fixed-literal-code-287", FixedBlockWithLiteralSymbol(287)));

        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        WriteFixedLiteral(writer, (byte)'a');
        writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[257], 7);
        writer.WriteBits(0, 0);
        writer.WriteHuffman(DeflateBitWriter.FixedDistanceCodes[5], 5);
        writer.WriteBits(0, 1);
        cases.Add(("fixed-distance-too-far-back", writer.ToZlibStream("a"u8.ToArray())));

        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[257], 7);
        writer.WriteHuffman(DeflateBitWriter.FixedDistanceCodes[0], 5);
        cases.Add(("fixed-distance-with-no-output", writer.ToZlibStream(Array.Empty<byte>())));

        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        WriteFixedLiteral(writer, (byte)'a');
        WriteFixedLiteral(writer, (byte)'b');
        cases.Add(("truncated-fixed-missing-eob", writer.ToZlibStream("ab"u8.ToArray())));

        writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        WriteFixedLiteral(writer, (byte)'a');
        writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[285], 8);
        cases.Add(("truncated-fixed-mid-match", writer.ToZlibStream("a"u8.ToArray())));

        // Dynamic-header framing.
        cases.Add(("dynamic-hlit-287", DynamicHeaderBits(30, 0, 15)));
        cases.Add(("dynamic-hlit-288", DynamicHeaderBits(31, 0, 15)));
        cases.Add(("dynamic-hdist-31", DynamicHeaderBits(0, 30, 15)));
        cases.Add(("dynamic-hdist-32", DynamicHeaderBits(0, 31, 15)));
        cases.Add(("truncated-dynamic-code-lengths", DynamicHeaderBits(0, 0, 15, stopAfterCodeLengths: 5)));

        cases.Add(("dynamic-hclen-4-empty-trees", DynamicWithCodeLengthTree(new[] { (16, 2), (17, 2), (18, 2), (0, 2) }, 4)));
        cases.Add(("dynamic-code-length-tree-oversubscribed", DynamicWithCodeLengthTree(new[] { (16, 1), (17, 1), (18, 1), (0, 1), (8, 1) }, 5)));
        cases.Add(("dynamic-code-length-tree-incomplete", DynamicWithCodeLengthTree(new[] { (16, 2), (17, 2) }, 5)));

        cases.Add(("dynamic-litlen-tree-oversubscribed", DynamicWithTrees(Oversubscribed(257), new[] { 0 })));
        cases.Add(("dynamic-litlen-tree-incomplete", DynamicWithTrees(Incomplete(257), new[] { 0 })));
        cases.Add(("dynamic-distance-tree-oversubscribed", DynamicWithTrees(SimpleLiterals(), new[] { 1, 1, 1 })));
        cases.Add(("dynamic-distance-tree-incomplete", DynamicWithTrees(SimpleLiterals(), new[] { 2, 2 })));
        cases.Add(("dynamic-all-zero-litlen-tree", DynamicWithTrees(new int[257], new[] { 0 })));

        cases.Add(("dynamic-repeat-16-as-first-length", RepeatCodeFirst()));
        cases.Add(("dynamic-repeat-16-overruns", RepeatCodeOverrun(16)));
        cases.Add(("dynamic-repeat-17-overruns", RepeatCodeOverrun(17)));
        cases.Add(("dynamic-repeat-18-overruns", RepeatCodeOverrun(18)));
        cases.Add(("dynamic-match-with-empty-distance-tree", MatchWithoutDistanceTree()));

        // A valid stream with a corrupted or missing trailer.
        var valid = new StreamBuilder();
        valid.Fixed(false, new[] { DeflateToken.Byte((byte)'h'), DeflateToken.Byte((byte)'i') });
        valid.Dynamic(true, new[] { DeflateToken.Byte((byte)'!'), DeflateToken.Match(3, 3) });
        InflateCase reference = valid.Build("adler-reference", "a valid two-block stream, used as the base for trailer and truncation damage");

        byte[] badAdler = (byte[])reference.Compressed.Clone();
        badAdler[badAdler.Length - 1] ^= 0x01;
        cases.Add(("adler-corrupted", badAdler));

        byte[] shortAdler = reference.Compressed.AsSpan(0, reference.Compressed.Length - 2).ToArray();
        cases.Add(("truncated-adler-two-bytes", shortAdler));
        cases.Add(("truncated-adler-missing", reference.Compressed.AsSpan(0, reference.Compressed.Length - 4).ToArray()));

        // Truncation at every stage of a stream that contains a zlib header, a dynamic header with repeat codes,
        // literals, a match and a trailer.
        var rich = new StreamBuilder();
        rich.Stored(false, "prologue-for-truncation"u8);
        rich.Dynamic(true, new[] { DeflateToken.Byte((byte)'z'), DeflateToken.Byte((byte)'y'), DeflateToken.Match(20, 12), DeflateToken.Match(258, 1) });
        InflateCase full = rich.Build("truncation-reference", "the stream the truncated cases are cut from");
        for (int cut = 1; cut < full.Compressed.Length; cut += Math.Max(1, full.Compressed.Length / 16))
        {
            cases.Add(($"truncated-at-{cut}-of-{full.Compressed.Length}", full.Compressed.AsSpan(0, cut).ToArray()));
        }

        var garbage = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        cases.Add(("deflate-garbage", garbage));

        // Tens of thousands of blocks that produce nothing and never terminate: the shape that turns a state
        // machine with a state transition costing no input into a hang rather than an error.
        writer = new DeflateBitWriter();
        for (int i = 0; i < 20000; i++)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits(0, 2);
            writer.AlignToByte();
            writer.WriteBits(0, 16);
            writer.WriteBits(0xFFFF, 16);
        }

        cases.Add(("truncated-many-empty-stored-blocks", writer.ToZlibStream(Array.Empty<byte>())));

        writer = new DeflateBitWriter();
        for (int i = 0; i < 60000; i++)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits(1, 2);
            writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[256], 7);
        }

        cases.Add(("truncated-many-empty-fixed-blocks", writer.ToZlibStream(Array.Empty<byte>())));

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, byte[] _) in cases)
        {
            if (!names.Add(name))
            {
                throw new XunitException($"InflateTestStreams.Malformed produced two cases named '{name}'.");
            }
        }

        return cases;
    }

    /// <summary>A zlib header with the given CMF, a matching FCHECK, and a valid empty stored block behind it.</summary>
    private static byte[] StreamWithHeaderByte(byte cmf)
    {
        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(0, 2);
        writer.AlignToByte();
        writer.WriteBits(0, 16);
        writer.WriteBits(0xFFFF, 16);
        byte[] deflate = writer.ToDeflate();
        var stream = new byte[2 + deflate.Length + 4];
        stream[0] = cmf;
        stream[1] = MakeFlg(cmf, 0x80);
        deflate.CopyTo(stream, 2);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(stream.Length - 4), Adler32(ReadOnlySpan<byte>.Empty));
        return stream;
    }

    /// <summary>FLG with FCHECK chosen so <c>(CMF &lt;&lt; 8 | FLG) % 31 == 0</c>, keeping FLEVEL and FDICT.</summary>
    private static byte MakeFlg(byte cmf, byte flagBits)
    {
        int flg = flagBits & 0xE0;
        int remainder = ((cmf << 8) + flg) % 31;
        if (remainder != 0)
        {
            flg += 31 - remainder;
        }

        return (byte)flg;
    }

    private static void WriteFixedLiteral(DeflateBitWriter writer, byte value)
        => writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[value], DeflateBitWriter.FixedLiteralLengths[value]);

    private static byte[] FixedBlockWithRawCodes(int lengthCode, int lengthBits, int distanceSymbol, int distanceBits)
    {
        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        WriteFixedLiteral(writer, (byte)'a');
        WriteFixedLiteral(writer, (byte)'b');
        WriteFixedLiteral(writer, (byte)'c');
        WriteFixedLiteral(writer, (byte)'d');
        writer.WriteHuffman(lengthCode, lengthBits);
        writer.WriteHuffman(distanceSymbol, distanceBits);
        writer.WriteBits(0, 16);
        return writer.ToZlibStream("abcd"u8.ToArray());
    }

    private static byte[] FixedBlockWithLiteralSymbol(int symbol)
    {
        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(1, 2);
        writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[symbol], DeflateBitWriter.FixedLiteralLengths[symbol]);
        writer.WriteHuffman(DeflateBitWriter.FixedLiteralCodes[256], 7);
        return writer.ToZlibStream(Array.Empty<byte>());
    }

    private static byte[] DynamicHeaderBits(int hlitField, int hdistField, int hclenField, int stopAfterCodeLengths = -1)
    {
        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(2, 2);
        writer.WriteBits((uint)hlitField, 5);
        writer.WriteBits((uint)hdistField, 5);
        writer.WriteBits((uint)hclenField, 4);
        int count = hclenField + 4;
        for (int i = 0; i < count; i++)
        {
            if (stopAfterCodeLengths >= 0 && i == stopAfterCodeLengths)
            {
                return writer.ToZlibStream(Array.Empty<byte>());
            }

            writer.WriteBits(i < 2 ? 1u : 0u, 3);
        }

        return writer.ToZlibStream(Array.Empty<byte>());
    }

    /// <summary>A dynamic header carrying an explicit - and deliberately broken - code-length tree.</summary>
    private static byte[] DynamicWithCodeLengthTree((int Symbol, int Length)[] codeLengthTree, int codeLengthCount)
    {
        int[] lengths = new int[19];
        foreach ((int symbol, int length) in codeLengthTree)
        {
            lengths[symbol] = length;
        }

        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(2, 2);
        writer.WriteBits(0, 5);
        writer.WriteBits(0, 5);
        writer.WriteBits((uint)(codeLengthCount - 4), 4);
        for (int i = 0; i < codeLengthCount; i++)
        {
            writer.WriteBits((uint)lengths[DeflateBitWriter.CodeLengthOrder[i]], 3);
        }

        int[] codes = DeflateBitWriter.CanonicalCodes(lengths);

        // 258 zero lengths, enough to fill any HLIT/HDIST pair, so the failure is the tree and not truncation.
        if (lengths[0] > 0)
        {
            for (int i = 0; i < 258; i++)
            {
                writer.WriteHuffman(codes[0], lengths[0]);
            }
        }

        return writer.ToZlibStream(Array.Empty<byte>());
    }

    private static byte[] DynamicWithTrees(int[] literalLengths, int[] distanceLengths)
    {
        var writer = new DeflateBitWriter();
        DeflateBitWriter.WriteDynamicHeader(writer, true, literalLengths, distanceLengths, null, 0, true);
        writer.WriteBits(0, 8);
        return writer.ToZlibStream(Array.Empty<byte>());
    }

    private static int[] Oversubscribed(int size)
    {
        // Three one-bit codes: the tree claims more code space than exists.
        int[] lengths = new int[size];
        lengths[0] = 1;
        lengths[1] = 1;
        lengths[2] = 1;
        return lengths;
    }

    private static int[] Incomplete(int size)
    {
        // Three two-bit codes leave a quarter of the code space unused, which zlib rejects for a literal/length
        // tree whose longest code is more than one bit.
        int[] lengths = new int[size];
        lengths[0] = 2;
        lengths[1] = 2;
        lengths[256] = 2;
        return lengths;
    }

    private static int[] SimpleLiterals()
    {
        int[] lengths = new int[257];
        lengths[0] = 1;
        lengths[256] = 1;
        return lengths;
    }

    private static byte[] RepeatCodeFirst()
    {
        int[] codeLengths = new int[19];
        codeLengths[16] = 1;
        codeLengths[0] = 1;
        int[] codes = DeflateBitWriter.CanonicalCodes(codeLengths);

        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(2, 2);
        writer.WriteBits(0, 5);
        writer.WriteBits(0, 5);
        writer.WriteBits(15, 4);
        for (int i = 0; i < 19; i++)
        {
            writer.WriteBits((uint)codeLengths[DeflateBitWriter.CodeLengthOrder[i]], 3);
        }

        writer.WriteHuffman(codes[16], codeLengths[16]);
        writer.WriteBits(0, 2);
        return writer.ToZlibStream(Array.Empty<byte>());
    }

    private static byte[] RepeatCodeOverrun(int repeatSymbol)
    {
        // The repeat runs past the end of the HLIT + HDIST length array.
        int[] codeLengths = new int[19];
        codeLengths[repeatSymbol] = 1;
        codeLengths[4] = 1;
        int[] codes = DeflateBitWriter.CanonicalCodes(codeLengths);

        var writer = new DeflateBitWriter();
        writer.WriteBits(1, 1);
        writer.WriteBits(2, 2);
        writer.WriteBits(0, 5);
        writer.WriteBits(0, 5);
        writer.WriteBits(15, 4);
        for (int i = 0; i < 19; i++)
        {
            writer.WriteBits((uint)codeLengths[DeflateBitWriter.CodeLengthOrder[i]], 3);
        }

        // One real length, then repeats until the 258-entry array is nearly full, then one repeat that overruns.
        writer.WriteHuffman(codes[4], codeLengths[4]);
        int written = 1;
        while (written < 250)
        {
            writer.WriteHuffman(codes[repeatSymbol], codeLengths[repeatSymbol]);
            switch (repeatSymbol)
            {
                case 16:
                    writer.WriteBits(3, 2);
                    written += 6;
                    break;
                case 17:
                    writer.WriteBits(7, 3);
                    written += 10;
                    break;
                default:
                    writer.WriteBits(127, 7);
                    written += 138;
                    break;
            }
        }

        writer.WriteHuffman(codes[repeatSymbol], codeLengths[repeatSymbol]);
        writer.WriteBits(repeatSymbol == 16 ? 3u : repeatSymbol == 17 ? 7u : 127u, repeatSymbol == 16 ? 2 : repeatSymbol == 17 ? 3 : 7);
        return writer.ToZlibStream(Array.Empty<byte>());
    }

    private static byte[] MatchWithoutDistanceTree()
    {
        // HDIST=1 with a zero code length, then a length code: the match has no distance code to read.
        int[] literalLengths = new int[258];
        literalLengths['a'] = 2;
        literalLengths[256] = 2;
        literalLengths[257] = 1;

        var writer = new DeflateBitWriter();
        DeflateBitWriter.WriteDynamicHeader(writer, true, literalLengths, new[] { 0 }, null, 0, true);
        int[] codes = DeflateBitWriter.CanonicalCodes(literalLengths);
        writer.WriteHuffman(codes['a'], 2);
        writer.WriteHuffman(codes[257], 1);
        writer.WriteBits(0, 5);
        return writer.ToZlibStream("a"u8.ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Corpus index
    // ---------------------------------------------------------------------------------------------------------

    private static (IReadOnlyList<InflateCase> Ordered, Dictionary<string, InflateCase> ByName) IndexCorpus()
    {
        var ordered = new List<InflateCase>();
        var index = new Dictionary<string, InflateCase>(StringComparer.Ordinal);
        foreach (InflateCase entry in GeneratedCorpus(DefaultSeed, 0).Concat(HandBuilt()))
        {
            if (!index.TryAdd(entry.Name, entry))
            {
                throw new XunitException($"Two inflate cases are named '{entry.Name}'; the names index the corpus, so they have to be unique.");
            }

            ordered.Add(entry);
        }

        return (ordered, index);
    }

    /// <summary>
    /// Accumulates a whole stream - the DEFLATE blocks and the bytes they are expected to produce - and hands the
    /// finished pair to <see cref="ZlibReference"/> for confirmation.
    /// </summary>
    private sealed class StreamBuilder
    {
        private readonly DeflateBitWriter writer = new();
        private readonly List<byte> output = new();

        /// <summary>Appends a stored block; <paramref name="data"/> must not exceed the 65535-byte LEN field.</summary>
        public void Stored(bool final, ReadOnlySpan<byte> data)
        {
            if (data.Length > 65535)
            {
                throw new XunitException($"A stored block holds at most 65535 bytes, not {data.Length}.");
            }

            this.writer.WriteBits(final ? 1u : 0u, 1);
            this.writer.WriteBits(0, 2);
            this.writer.AlignToByte();
            this.writer.WriteBits((uint)data.Length, 16);
            this.writer.WriteBits((uint)(~data.Length & 0xFFFF), 16);
            this.writer.WriteAlignedBytes(data);
            foreach (byte value in data)
            {
                this.output.Add(value);
            }
        }

        /// <summary>Appends a fixed-Huffman block holding these tokens plus the end-of-block symbol.</summary>
        public void Fixed(bool final, IReadOnlyList<DeflateToken> tokens)
        {
            this.writer.WriteBits(final ? 1u : 0u, 1);
            this.writer.WriteBits(1, 2);
            this.WriteTokens(
                tokens,
                DeflateBitWriter.FixedLiteralCodes,
                DeflateBitWriter.FixedLiteralLengths,
                DeflateBitWriter.FixedDistanceCodes,
                DeflateBitWriter.FixedDistanceLengths);
        }

        /// <summary>
        /// Appends a dynamic block. Passing null for a tree derives an equal-weight canonical one covering exactly
        /// the symbols the tokens use; passing an explicit array is how the degenerate shapes are built. Returns
        /// the code-length symbol stream that was written, so a caller can assert which repeat codes it hit.
        /// </summary>
        public IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> Dynamic(
            bool final,
            IReadOnlyList<DeflateToken> tokens,
            int[]? literalLengths = null,
            int[]? distanceLengths = null,
            int[]? codeLengthLengths = null,
            int codeLengthCount = 0,
            bool useRepeatCodes = true)
        {
            literalLengths ??= DeriveLiteralLengths(tokens);
            distanceLengths ??= DeriveDistanceLengths(tokens);

            IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> sequence = DeflateBitWriter.WriteDynamicHeader(
                this.writer, final, literalLengths, distanceLengths, codeLengthLengths, codeLengthCount, useRepeatCodes);

            this.WriteTokens(
                tokens,
                DeflateBitWriter.CanonicalCodes(literalLengths),
                literalLengths,
                DeflateBitWriter.CanonicalCodes(distanceLengths),
                distanceLengths);
            return sequence;
        }

        /// <summary>Wraps the blocks in a zlib stream and confirms zlib decodes it to the expected bytes.</summary>
        public InflateCase Build(string name, string recipe)
        {
            byte[] expected = this.output.ToArray();
            byte[] compressed = this.writer.ToZlibStream(expected);

            byte[] viaZlib;
            try
            {
                viaZlib = ZlibReference(compressed);
            }
            catch (Exception ex)
            {
                throw new XunitException(
                    $"Hand-built stream '{name}' was rejected by ZLibStream ({ex.GetType().Name}: {ex.Message}). The generator is wrong, not the inflater. Recipe: {recipe}");
            }

            if (!viaZlib.AsSpan().SequenceEqual(expected))
            {
                int at = 0;
                while (at < viaZlib.Length && at < expected.Length && viaZlib[at] == expected[at])
                {
                    at++;
                }

                throw new XunitException(
                    $"Hand-built stream '{name}' decodes to {viaZlib.Length} bytes through ZLibStream but the generator expected {expected.Length}; first difference at offset {at}. Recipe: {recipe}");
            }

            return new InflateCase(name, compressed, viaZlib, recipe);
        }

        private static int[] DeriveLiteralLengths(IReadOnlyList<DeflateToken> tokens)
        {
            var symbols = new SortedSet<int> { 256 };
            foreach (DeflateToken token in tokens)
            {
                symbols.Add(token.IsMatch ? 257 + token.LengthCode : token.Literal);
            }

            int[] lengths = new int[Math.Max(257, symbols.Max + 1)];
            int[] flat = DeflateBitWriter.EqualWeightLengths(symbols.Count);
            int index = 0;
            foreach (int symbol in symbols)
            {
                lengths[symbol] = flat[index++];
            }

            return lengths;
        }

        private static int[] DeriveDistanceLengths(IReadOnlyList<DeflateToken> tokens)
        {
            var symbols = new SortedSet<int>();
            foreach (DeflateToken token in tokens)
            {
                if (token.IsMatch)
                {
                    symbols.Add(DeflateBitWriter.DistanceCodeFor(token.Distance));
                }
            }

            if (symbols.Count == 0)
            {
                return new[] { 0 };
            }

            int[] lengths = new int[symbols.Max + 1];
            int[] flat = DeflateBitWriter.EqualWeightLengths(symbols.Count);
            int index = 0;
            foreach (int symbol in symbols)
            {
                lengths[symbol] = flat[index++];
            }

            return lengths;
        }

        private void WriteTokens(
            IReadOnlyList<DeflateToken> tokens,
            int[] literalCodes,
            int[] literalLengths,
            int[] distanceCodes,
            int[] distanceLengths)
        {
            foreach (DeflateToken token in tokens)
            {
                if (!token.IsMatch)
                {
                    this.WriteSymbol(token.Literal, literalCodes, literalLengths);
                    this.output.Add((byte)token.Literal);
                    continue;
                }

                int lengthSymbol = 257 + token.LengthCode;
                this.WriteSymbol(lengthSymbol, literalCodes, literalLengths);
                this.writer.WriteBits(
                    (uint)(token.Length - DeflateBitWriter.LengthBase[token.LengthCode]),
                    DeflateBitWriter.LengthExtraBits[token.LengthCode]);

                int distanceSymbol = DeflateBitWriter.DistanceCodeFor(token.Distance);
                this.WriteSymbol(distanceSymbol, distanceCodes, distanceLengths);
                this.writer.WriteBits(
                    (uint)(token.Distance - DeflateBitWriter.DistanceBase[distanceSymbol]),
                    DeflateBitWriter.DistanceExtraBits[distanceSymbol]);

                if (token.Distance > this.output.Count)
                {
                    throw new XunitException(
                        $"A hand-built match reaches {token.Distance} bytes back with only {this.output.Count} bytes produced.");
                }

                int source = this.output.Count - token.Distance;
                for (int i = 0; i < token.Length; i++)
                {
                    this.output.Add(this.output[source + i]);
                }
            }

            this.WriteSymbol(256, literalCodes, literalLengths);
        }

        private void WriteSymbol(int symbol, int[] codes, int[] lengths)
        {
            if (symbol >= lengths.Length || lengths[symbol] == 0)
            {
                throw new XunitException($"The hand-built tree has no code for symbol {symbol}.");
            }

            this.writer.WriteHuffman(codes[symbol], lengths[symbol]);
        }
    }
}

/// <summary>
/// One literal byte, or one length/distance match. <see cref="LengthCode"/> is normally derived from
/// <see cref="Length"/>, but can be forced so the encodings RFC 1951 allows and never generates - length 258
/// spelled as code 284 with all five extra bits set, for instance - can be produced deliberately.
/// </summary>
internal readonly struct DeflateToken
{
    private DeflateToken(int literal, int length, int distance, int lengthCode)
    {
        this.Literal = literal;
        this.Length = length;
        this.Distance = distance;
        this.LengthCode = lengthCode;
    }

    /// <summary>The literal byte value, or -1 when this token is a match.</summary>
    public int Literal { get; }

    /// <summary>Match length in bytes, 3..258.</summary>
    public int Length { get; }

    /// <summary>Match distance in bytes, 1..32768.</summary>
    public int Distance { get; }

    /// <summary>Index into the length base table, so the literal/length symbol is <c>257 + LengthCode</c>.</summary>
    public int LengthCode { get; }

    /// <summary>True when this token is a length/distance pair rather than a literal.</summary>
    public bool IsMatch => this.Literal < 0;

    /// <summary>A literal byte.</summary>
    public static DeflateToken Byte(byte value) => new(value, 0, 0, 0);

    /// <summary>A match encoded with the length code an ordinary compressor would pick.</summary>
    public static DeflateToken Match(int length, int distance)
        => new(-1, length, distance, DeflateBitWriter.LengthCodeFor(length));

    /// <summary>A match encoded with a chosen length code, whose extra bits then carry the remainder.</summary>
    public static DeflateToken MatchWithLengthCode(int length, int distance, int symbol)
        => new(-1, length, distance, symbol - 257);
}

/// <summary>
/// An LSB-first bit emitter for DEFLATE: <see cref="WriteBits"/> writes a value least-significant bit first (the
/// order the format uses for header fields and extra bits) while <see cref="WriteHuffman"/> writes a Huffman code
/// most-significant bit first (the order the format uses for codes). It exists so the tests can assemble block
/// shapes no compressor emits, byte for byte, and it is deliberately naive - correctness over speed.
/// </summary>
internal sealed class DeflateBitWriter
{
    /// <summary>The order the code-length code lengths appear in a dynamic block header (RFC 1951 3.2.7).</summary>
    public static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    /// <summary>Smallest match length for length codes 257..285.</summary>
    public static readonly int[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    };

    /// <summary>Extra bits carried by length codes 257..285.</summary>
    public static readonly int[] LengthExtraBits =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    /// <summary>Smallest distance for distance codes 0..29.</summary>
    public static readonly int[] DistanceBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
        1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    };

    /// <summary>Extra bits carried by distance codes 0..29.</summary>
    public static readonly int[] DistanceExtraBits =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    };

    /// <summary>Code lengths of the fixed literal/length alphabet (RFC 1951 3.2.6).</summary>
    public static readonly int[] FixedLiteralLengths = BuildFixedLiteralLengths();

    /// <summary>Canonical codes for <see cref="FixedLiteralLengths"/>.</summary>
    public static readonly int[] FixedLiteralCodes = CanonicalCodes(FixedLiteralLengths);

    /// <summary>Code lengths of the fixed distance alphabet: 32 five-bit codes.</summary>
    public static readonly int[] FixedDistanceLengths = BuildFixedDistanceLengths();

    /// <summary>Canonical codes for <see cref="FixedDistanceLengths"/>.</summary>
    public static readonly int[] FixedDistanceCodes = CanonicalCodes(FixedDistanceLengths);

    private readonly List<byte> bytes = new();
    private uint partial;
    private int partialBits;
    private int bitLength;

    /// <summary>Total bits written so far, used to place a block header at a chosen bit alignment.</summary>
    public int BitLength => this.bitLength;

    /// <summary>Canonical Huffman codes for a code-length array, exactly as RFC 1951 3.2.2 derives them.</summary>
    public static int[] CanonicalCodes(int[] lengths)
    {
        Span<int> countPerLength = stackalloc int[16];
        foreach (int length in lengths)
        {
            if (length > 0)
            {
                countPerLength[length]++;
            }
        }

        Span<int> nextCode = stackalloc int[16];
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + countPerLength[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        int[] codes = new int[lengths.Length];
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length > 0)
            {
                codes[symbol] = nextCode[length]++;
            }
        }

        return codes;
    }

    /// <summary>
    /// Code lengths of the complete canonical tree an equal-weight Huffman run produces for
    /// <paramref name="symbolCount"/> symbols: all codes at one of two adjacent lengths. A single symbol gets a
    /// one-bit code, which leaves the tree incomplete - the degenerate shape zlib accepts.
    /// </summary>
    public static int[] EqualWeightLengths(int symbolCount)
    {
        if (symbolCount <= 0)
        {
            return Array.Empty<int>();
        }

        if (symbolCount == 1)
        {
            return new[] { 1 };
        }

        int bits = 1;
        while ((1 << bits) < symbolCount)
        {
            bits++;
        }

        int deep = 2 * (symbolCount - (1 << (bits - 1)));
        int[] lengths = new int[symbolCount];
        for (int i = 0; i < symbolCount; i++)
        {
            lengths[i] = i < symbolCount - deep ? bits - 1 : bits;
        }

        return lengths;
    }

    /// <summary>The length code (0-based index into <see cref="LengthBase"/>) an encoder would pick.</summary>
    public static int LengthCodeFor(int length)
    {
        for (int code = 28; code >= 0; code--)
        {
            if (LengthBase[code] <= length)
            {
                return code;
            }
        }

        throw new XunitException($"A DEFLATE match is at least 3 bytes long, not {length}.");
    }

    /// <summary>The distance code for a distance in 1..32768.</summary>
    public static int DistanceCodeFor(int distance)
    {
        for (int code = 29; code >= 0; code--)
        {
            if (DistanceBase[code] <= distance)
            {
                return code;
            }
        }

        throw new XunitException($"A DEFLATE match distance is at least 1, not {distance}.");
    }

    /// <summary>
    /// Writes a dynamic block header: HLIT/HDIST/HCLEN, the code-length code lengths in their permuted order, and
    /// the run-length encoded literal/length and distance code lengths as one array (a repeat started at the end
    /// of the literal/length lengths continues into the distance lengths, which is legal and load-bearing).
    /// Returns the code-length symbol stream it emitted.
    /// </summary>
    public static IReadOnlyList<(int Symbol, int Extra, int ExtraBits)> WriteDynamicHeader(
        DeflateBitWriter writer,
        bool final,
        int[] literalLengths,
        int[] distanceLengths,
        int[]? codeLengthLengths,
        int codeLengthCount,
        bool useRepeatCodes)
    {
        int[] combined = new int[literalLengths.Length + distanceLengths.Length];
        literalLengths.CopyTo(combined, 0);
        distanceLengths.CopyTo(combined, literalLengths.Length);
        List<(int Symbol, int Extra, int ExtraBits)> sequence = EncodeCodeLengths(combined, useRepeatCodes);

        if (codeLengthLengths is null)
        {
            var used = new SortedSet<int>();
            foreach ((int symbol, int _, int _) in sequence)
            {
                used.Add(symbol);
            }

            codeLengthLengths = new int[19];
            int[] flat = EqualWeightLengths(used.Count);
            int index = 0;
            foreach (int symbol in used)
            {
                codeLengthLengths[symbol] = flat[index++];
            }
        }

        if (codeLengthCount <= 0)
        {
            codeLengthCount = 19;
            while (codeLengthCount > 4 && codeLengthLengths[CodeLengthOrder[codeLengthCount - 1]] == 0)
            {
                codeLengthCount--;
            }
        }

        writer.WriteBits(final ? 1u : 0u, 1);
        writer.WriteBits(2, 2);
        writer.WriteBits((uint)(literalLengths.Length - 257), 5);
        writer.WriteBits((uint)(distanceLengths.Length - 1), 5);
        writer.WriteBits((uint)(codeLengthCount - 4), 4);
        for (int i = 0; i < codeLengthCount; i++)
        {
            writer.WriteBits((uint)codeLengthLengths[CodeLengthOrder[i]], 3);
        }

        int[] codes = CanonicalCodes(codeLengthLengths);
        foreach ((int symbol, int extra, int extraBits) in sequence)
        {
            if (codeLengthLengths[symbol] == 0)
            {
                throw new XunitException(
                    $"The code-length tree has no code for symbol {symbol}, which the run-length encoding needs; widen HCLEN or the code-length tree.");
            }

            writer.WriteHuffman(codes[symbol], codeLengthLengths[symbol]);
            writer.WriteBits((uint)extra, extraBits);
        }

        return sequence;
    }

    /// <summary>Writes <paramref name="count"/> bits of <paramref name="value"/>, least significant bit first.</summary>
    public void WriteBits(uint value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            this.WriteBit((value >> i) & 1);
        }
    }

    /// <summary>Writes a Huffman code of <paramref name="length"/> bits, most significant bit first.</summary>
    public void WriteHuffman(int code, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            this.WriteBit(((uint)code >> i) & 1);
        }
    }

    /// <summary>Pads with zero bits up to the next byte boundary, as a stored block header requires.</summary>
    public void AlignToByte()
    {
        while (this.partialBits != 0)
        {
            this.WriteBit(0);
        }
    }

    /// <summary>Appends whole bytes; only legal when the writer is already byte-aligned.</summary>
    public void WriteAlignedBytes(ReadOnlySpan<byte> data)
    {
        if (this.partialBits != 0)
        {
            throw new XunitException("WriteAlignedBytes needs a byte-aligned writer; call AlignToByte first.");
        }

        foreach (byte value in data)
        {
            this.bytes.Add(value);
            this.bitLength += 8;
        }
    }

    /// <summary>The raw DEFLATE bytes, zero-padded to a byte boundary. Does not disturb the writer.</summary>
    public byte[] ToDeflate()
    {
        byte[] result = new byte[this.bytes.Count + (this.partialBits > 0 ? 1 : 0)];
        this.bytes.CopyTo(result);
        if (this.partialBits > 0)
        {
            result[this.bytes.Count] = (byte)this.partial;
        }

        return result;
    }

    /// <summary>
    /// Wraps the blocks in an RFC 1950 stream: CMF/FLG of 0x78 0x9C (deflate, 32 KiB window, no dictionary) and a
    /// big-endian ADLER-32 of <paramref name="rawForAdler"/>, which must be the bytes the blocks decode to.
    /// </summary>
    public byte[] ToZlibStream(byte[] rawForAdler)
    {
        byte[] deflate = this.ToDeflate();
        byte[] stream = new byte[2 + deflate.Length + 4];
        stream[0] = 0x78;
        stream[1] = 0x9C;
        deflate.CopyTo(stream, 2);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(stream.Length - 4), InflateTestStreams.Adler32(rawForAdler));
        return stream;
    }

    /// <summary>
    /// Run-length encodes a code-length array into the 19-symbol code-length alphabet. With
    /// <paramref name="useRepeatCodes"/> false every length is written as its own symbol, which is legal and which
    /// no real compressor does; with it true the greedy encoding reaches both extremes of codes 16 (3-6 repeats),
    /// 17 (3-10 zeros) and 18 (11-138 zeros).
    /// </summary>
    private static List<(int Symbol, int Extra, int ExtraBits)> EncodeCodeLengths(int[] lengths, bool useRepeatCodes)
    {
        var sequence = new List<(int Symbol, int Extra, int ExtraBits)>();
        int position = 0;
        while (position < lengths.Length)
        {
            int value = lengths[position];
            int run = 1;
            while (position + run < lengths.Length && lengths[position + run] == value)
            {
                run++;
            }

            if (!useRepeatCodes)
            {
                for (int i = 0; i < run; i++)
                {
                    sequence.Add((value, 0, 0));
                }
            }
            else if (value == 0)
            {
                int remaining = run;
                while (remaining >= 11)
                {
                    int take = Math.Min(138, remaining);
                    sequence.Add((18, take - 11, 7));
                    remaining -= take;
                }

                while (remaining >= 3)
                {
                    int take = Math.Min(10, remaining);
                    sequence.Add((17, take - 3, 3));
                    remaining -= take;
                }

                for (int i = 0; i < remaining; i++)
                {
                    sequence.Add((0, 0, 0));
                }
            }
            else
            {
                sequence.Add((value, 0, 0));
                int remaining = run - 1;
                while (remaining >= 3)
                {
                    int take = Math.Min(6, remaining);
                    sequence.Add((16, take - 3, 2));
                    remaining -= take;
                }

                for (int i = 0; i < remaining; i++)
                {
                    sequence.Add((value, 0, 0));
                }
            }

            position += run;
        }

        return sequence;
    }

    private static int[] BuildFixedLiteralLengths()
    {
        int[] lengths = new int[288];
        for (int i = 0; i < 144; i++)
        {
            lengths[i] = 8;
        }

        for (int i = 144; i < 256; i++)
        {
            lengths[i] = 9;
        }

        for (int i = 256; i < 280; i++)
        {
            lengths[i] = 7;
        }

        for (int i = 280; i < 288; i++)
        {
            lengths[i] = 8;
        }

        return lengths;
    }

    private static int[] BuildFixedDistanceLengths()
    {
        int[] lengths = new int[32];
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = 5;
        }

        return lengths;
    }

    private void WriteBit(uint bit)
    {
        if (bit != 0)
        {
            this.partial |= 1u << this.partialBits;
        }

        this.partialBits++;
        this.bitLength++;
        if (this.partialBits == 8)
        {
            this.bytes.Add((byte)this.partial);
            this.partial = 0;
            this.partialBits = 0;
        }
    }
}
