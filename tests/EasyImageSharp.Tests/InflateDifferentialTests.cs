using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The gate the managed inflater has to pass: every stream <see cref="InflateTestStreams"/> can build, decoded
/// by <see cref="Inflater"/> and compared byte for byte with <see cref="ZLibStream"/> - native zlib on net8.0,
/// zlib-ng on net10.0, an implementation entirely outside this repository. No expected byte in this file comes
/// from EasyImageSharp: generated cases carry the payload that was handed to zlib's compressor, hand-assembled
/// cases carry zlib's own decode of the bitstream, and the truncation and malformed suites compare verdicts and
/// partial output against zlib directly.
/// <para>
/// <b>What is in the suite and what is not.</b> An exhaustive differential run - every stream split at every
/// input offset, crossed with every output chunk size - is a nightly-fuzz job, not a unit test. What is pinned
/// here is a deliberately chosen subset that keeps the whole file inside a couple of seconds per framework:
/// </para>
/// <list type="bullet">
/// <item><b>Whole buffer: every stream in the corpus - 412 of them, 408 on net10.0.</b> One decode each is
/// cheap (the corpus is 32 MB of output in total, a tenth of a second), so there is no reason to sample. This
/// is the tier that would catch a wrong Huffman table, a wrong length or distance base, or a broken
/// overlapping copy. The four missing on net10.0 are the empty payloads, which zlib-ng's compressor turns into
/// no bytes at all rather than into the six-byte empty stream RFC 1950 defines; see
/// <see cref="CorpusNames"/>.</item>
/// <item><b>Input segmentation: every stream, with the pattern count scaled by stream size.</b> A stream of
/// at most <see cref="ExhaustiveSplitLimit"/> bytes is split at <em>every</em> offset, which is the only way to
/// prove that no symbol, stored-block header, dynamic-header cursor or pending match is corrupted by a segment
/// boundary; that covers the whole hand-built corpus, where the exotic block shapes live. Larger streams get a
/// fixed number of seeded random patterns (<see cref="SegmentationPatterns"/>) mixing zero-length, one-byte and
/// bulk segments, because their boundaries are not structurally different - only more numerous.</item>
/// <item><b>Output chunking: every stream, crossed with the segmentation patterns.</b> Pulling one byte at a
/// time is what forces a match to be suspended and resumed and a window rewind to land mid-match, so it is
/// applied to every stream whose output fits <see cref="ByteAtATimeOutputLimit"/>; bigger streams are pulled in
/// seeded random chunks instead, since a million single-byte <see cref="Inflater.Fill"/> calls would cost more
/// time than the coverage is worth.</item>
/// <item><b>Malformed streams: all 62,</b> each inside a hang guard and an allocation budget, with its
/// accept/reject verdict compared against zlib's.</item>
/// </list>
/// <para>
/// The author of <see cref="Inflater"/> ran the unsampled version of all of this outside the suite - 412
/// whole-buffer streams, 25,701 segmentation feeds, 39,192 output-chunking runs, all 519 PNG fixture IDATs, 62
/// malformed streams and 40,000 fuzz inputs, every one byte-identical to <see cref="ZLibStream"/>. This file is
/// the part of that run which is cheap enough to keep forever.
/// </para>
/// </summary>
public class InflateDifferentialTests
{
    /// <summary>Streams at or below this compressed size are split at every single input offset.</summary>
    private const int ExhaustiveSplitLimit = 512;

    /// <summary>Streams whose output is at or below this are also pulled one byte at a time.</summary>
    private const int ByteAtATimeOutputLimit = 16 * 1024;

    /// <summary>Above this compressed size a stream gets the reduced pattern count; see the class remarks.</summary>
    private const int LargeStreamLimit = 256 * 1024;

    /// <summary>
    /// Payloads larger than this are left out of the ADLER-32 sweep. Both references it runs - the from-the-
    /// specification loop, which takes a modulo per byte, and the checksum itself - are linear in the payload,
    /// and the corpus is 32 MB of payload, most of it in a handful of streams.
    /// </summary>
    private const int TrailerComparisonLimit = 64 * 1024;

    /// <summary>The largest distance DEFLATE can express, and the period of the window-rewind payload.</summary>
    private const int MaxDistance = 32768;

    /// <summary>Hang guard for the whole malformed table, which decodes in well under a tenth of a second.</summary>
    private static readonly TimeSpan MalformedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Allocation budget for a single malformed stream; the observed worst case is under 9 KB.</summary>
    private const long MalformedAllocationBudget = 8L * 1024 * 1024;

    /// <summary>
    /// Every stream in the corpus, by name, for the whole-buffer and incremental theories - minus the ones
    /// that are zero bytes long, which are not zlib streams at all. Compressing an empty payload through
    /// <see cref="ZLibStream"/> produces no bytes rather than the six-byte empty stream RFC 1950 describes, so
    /// those cases are the framework quirk rather than a decode to compare;
    /// <see cref="EmptyPayloadStreams_AreNotZlibStreamsAndAreTreatedAsTruncated"/> owns them.
    /// </summary>
    public static IEnumerable<object[]> CorpusNames()
        => InflateTestStreams.Corpus.Where(entry => entry.Compressed.Length > 0).Select(entry => new object[] { entry.Name });

    /// <summary>
    /// The streams whose every truncation is compared with zlib's short read. Six small ones, one per block
    /// shape that matters, because the comparison is quadratic in the stream length.
    /// </summary>
    public static IEnumerable<object[]> TruncationNames()
        => new[]
        {
            "size-259-nocompression",
            "size-511-optimal",
            "two-symbol-300-optimal",
            "periodic-3-fastest",
            "png-like-13x7x1-optimal",
            "text-700-smallestsize",
        }.Select(name => new object[] { name });

    // =============================================================================================
    // Whole buffer
    // =============================================================================================

    /// <summary>
    /// <see cref="Inflate.Decompress"/> over the complete stream must reproduce zlib's bytes exactly.
    /// </summary>
    /// <param name="name">The <see cref="InflateTestStreams.Corpus"/> entry to decode.</param>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void WholeBuffer_MatchesZLibStream(string name)
    {
        InflateCase entry = InflateTestStreams.ByName(name);

        byte[] actual = Inflate.Decompress(entry.Compressed, int.MaxValue, "test");

        AssertBytesEqual(entry.Expected, actual, entry, "whole buffer");
    }

    // =============================================================================================
    // Input segmentation
    // =============================================================================================

    /// <summary>
    /// The same stream pushed through <see cref="Inflater.SetInput"/> in pieces. The decoder is re-entered at
    /// every one of those boundaries, so anything it forgot to save - a partly read symbol, the dynamic-header
    /// cursor, the remaining length of a stored block, a match it had started copying - shows up as a wrong
    /// byte here and nowhere else.
    /// </summary>
    /// <param name="name">The <see cref="InflateTestStreams.Corpus"/> entry to decode.</param>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void Segmented_MatchesZLibStream(string name)
    {
        InflateCase entry = InflateTestStreams.ByName(name);

        foreach (int[] pattern in SegmentationPatterns(entry.Compressed.Length))
        {
            byte[] actual = RunIncremental(entry.Compressed, pattern, WholeOutput, strideHint: 0);
            AssertBytesEqual(entry.Expected, actual, entry, $"segments [{Describe(pattern)}]");
        }
    }

    // =============================================================================================
    // Output chunking, crossed with segmentation
    // =============================================================================================

    /// <summary>
    /// Output pulled in small pieces, with the input segmented at the same time. Taking one byte at a time is
    /// what suspends a match halfway through its copy and what makes a window rewind land inside one, and doing
    /// it while the input is also arriving in fragments is the only shape that exercises both at once.
    /// </summary>
    /// <param name="name">The <see cref="InflateTestStreams.Corpus"/> entry to decode.</param>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void OutputChunking_MatchesZLibStream(string name)
    {
        InflateCase entry = InflateTestStreams.ByName(name);
        int[][] segmentations = ChunkingSegmentations(entry.Compressed.Length);

        foreach (int[] chunks in OutputChunkPolicies(entry.Expected.Length))
        {
            foreach (int[] pattern in segmentations)
            {
                byte[] actual = RunIncremental(entry.Compressed, pattern, chunks, strideHint: 0);
                AssertBytesEqual(entry.Expected, actual, entry, $"segments [{Describe(pattern)}], output chunks [{Describe(chunks)}]");
            }
        }

        if (entry.Expected.Length <= ByteAtATimeOutputLimit)
        {
            // One byte in, one byte out: the harshest shape there is, and the one that leaves the decoder
            // suspended inside a match on nearly every call. It is run once rather than crossed with the other
            // segmentations, because at this granularity a second run costs as much as all the others together.
            byte[] actual = RunIncremental(entry.Compressed, SingleByteSegments(entry.Compressed.Length), SingleByteOutput, strideHint: 0);
            AssertBytesEqual(entry.Expected, actual, entry, "one-byte segments, one-byte output");
        }
    }

    /// <summary>
    /// The over-wide-row path: <see cref="Inflater.ReadInto"/> serves a request larger than the emit region by
    /// looping internally, and must produce the same bytes as taking spans.
    /// </summary>
    [Fact]
    public void ReadInto_MatchesZLibStream()
    {
        (string Name, int[] Destinations)[] streams =
        {
            ("text-30000-fastest", new[] { 1, 3, 4096, 100_000 }),
            ("sparse-1in17-smallestsize", new[] { 4096, 100_000, 1_500_000 }),
            ("long-distance-200000-optimal", new[] { 4096, 100_000, 1_500_000 }),
            ("huge-4mib-optimal", new[] { 100_000, 1_500_000 }),
        };

        foreach ((string name, int[] destinations) in streams)
        {
            InflateCase entry = InflateTestStreams.ByName(name);
            foreach (int destination in destinations)
            {
                byte[] actual = RunReadInto(entry.Compressed, destination, strideHint: 0);
                AssertBytesEqual(entry.Expected, actual, entry, $"ReadInto in {destination} byte pieces");
            }
        }
    }

    // =============================================================================================
    // Hand-assembled block shapes
    // =============================================================================================

    /// <summary>
    /// The block shapes zlib's compressor never emits but a conforming decoder has to accept: degenerate and
    /// single-symbol trees, incomplete distance trees, the minimum HCLEN, the code-length repeat codes at both
    /// extremes, every length and distance code at its extra-bit limits, and every block-type transition. Each
    /// stream was decoded through <see cref="ZLibStream"/> when it was built, so the expectation here is still
    /// zlib's.
    /// </summary>
    [Fact]
    public void HandBuiltShapes_MatchZLibStream()
    {
        int checkedStreams = 0;
        foreach (InflateCase entry in InflateTestStreams.HandBuilt())
        {
            AssertBytesEqual(entry.Expected, Inflate.Decompress(entry.Compressed, int.MaxValue, "test"), entry, "whole buffer");

            // The same bits one byte at a time, because a hand-built header is exactly where a resume bug hides.
            byte[] segmented = RunIncremental(entry.Compressed, SingleByteSegments(entry.Compressed.Length), SingleByteOutput, strideHint: 0);
            AssertBytesEqual(entry.Expected, segmented, entry, "one-byte segments, one-byte output");
            checkedStreams++;
        }

        Assert.True(checkedStreams >= 40, $"The hand-built corpus shrank to {checkedStreams} streams; it is meant to cover every exotic block shape.");
    }

    // =============================================================================================
    // Malformed streams
    // =============================================================================================

    /// <summary>
    /// Broken input must raise <see cref="InvalidImageContentException"/> and nothing else, promptly and
    /// without allocating, and the accept/reject verdict must agree with zlib's - except on the one group where
    /// the divergence is deliberate.
    /// <para>
    /// That group is <see cref="InflateTestStreams.TruncatedPrefix"/>. <see cref="ZLibStream"/> reports a
    /// truncated stream as end-of-stream and hands back whatever it decoded; a PNG decoder cannot, because a
    /// short IDAT is a short image, so <see cref="Inflate.Decompress"/> - which is asked for a whole stream -
    /// turns the short read into a decode failure. The incremental interface keeps zlib's behaviour exactly;
    /// see <see cref="Truncated_ReportsEndOfStreamRatherThanAnError"/>.
    /// </para>
    /// <para>
    /// The whole table runs on one background thread rather than a thread per stream: the hang guard is the
    /// point, and starting a thread costs far more here than decoding all 62 of these streams does. A decoder
    /// that spins therefore fails this test - naming the stream it was working on - instead of hanging the
    /// suite, and because the thread is a background one it cannot keep the process alive either.
    /// </para>
    /// </summary>
    [Fact]
    public void Malformed_IsRejectedWithinBudget()
    {
        IReadOnlyList<(string Name, byte[] Compressed)> table = InflateTestStreams.MalformedCorpus;
        var verdicts = new string[table.Count];
        var allocations = new long[table.Count];
        string running = "(none)";

        var worker = new Thread(() =>
        {
            for (int i = 0; i < table.Count; i++)
            {
                Volatile.Write(ref running, table[i].Name);
                long before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    verdicts[i] = $"accepted {Inflate.Decompress(table[i].Compressed, int.MaxValue, "test").Length} bytes";
                }
                catch (InvalidImageContentException)
                {
                    verdicts[i] = "rejected";
                }
                catch (Exception leaked)
                {
                    // The decoder contract is that no framework exception ever escapes it.
                    verdicts[i] = $"leaked {leaked.GetType().FullName}: {leaked.Message}";
                }

                allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        })
        {
            IsBackground = true,
            Name = "inflate-malformed-table",
        };

        var timer = Stopwatch.StartNew();
        worker.Start();
        Assert.True(
            worker.Join(MalformedTimeout),
            $"The malformed table did not finish within {MalformedTimeout.TotalSeconds:0} seconds; it was decoding '{Volatile.Read(ref running)}'.");

        for (int i = 0; i < table.Count; i++)
        {
            (string name, byte[] compressed) = table[i];

            Assert.True(verdicts[i] == "rejected", $"'{name}' should have been rejected but was {verdicts[i]}.");
            Assert.True(
                allocations[i] <= MalformedAllocationBudget,
                $"'{name}' allocated {allocations[i]:N0} bytes, over the {MalformedAllocationBudget:N0} byte budget.");

            bool zlibRejects = ZlibRejects(compressed);
            bool zlibShouldReject = !name.StartsWith(InflateTestStreams.TruncatedPrefix, StringComparison.Ordinal);
            Assert.True(
                zlibRejects == zlibShouldReject,
                zlibShouldReject
                    ? $"'{name}' is structurally invalid but ZLibStream accepted it, so this test no longer proves the two agree."
                    : $"'{name}' is a truncation, which ZLibStream is expected to report as end-of-stream rather than reject.");
        }

        Assert.True(table.Count >= 60, $"The malformed table shrank to {table.Count} streams.");
        Assert.True(timer.Elapsed < MalformedTimeout, $"The malformed table took {timer.Elapsed.TotalSeconds:0.0} seconds.");
    }

    // =============================================================================================
    // The three behaviours InflateCharacterisationTests pinned
    // =============================================================================================

    /// <summary>
    /// Requirement one: a stream that simply stops is end-of-stream, not an error. Cut every one of these
    /// streams at every offset and the incremental interface must hand back a prefix of the payload - the same
    /// prefix, byte for byte, that <see cref="ZLibStream"/> hands back - without throwing. That is what lets a
    /// PNG whose IDAT ends exactly after the last scanline keep decoding, which
    /// <c>InflateCharacterisationTests.TruncatedAfterLastScanline_*</c> recorded as today's behaviour.
    /// </summary>
    /// <param name="name">The <see cref="InflateTestStreams.Corpus"/> entry to truncate at every offset.</param>
    [Theory]
    [MemberData(nameof(TruncationNames))]
    public void Truncated_ReportsEndOfStreamRatherThanAnError(string name)
    {
        InflateCase entry = InflateTestStreams.ByName(name);

        for (int cut = 0; cut <= entry.Compressed.Length; cut++)
        {
            byte[] truncated = entry.Compressed[..cut];

            byte[] actual = RunIncremental(truncated, Array.Empty<int>(), WholeOutput, strideHint: 0);

            Assert.True(
                actual.Length <= entry.Expected.Length && entry.Expected.AsSpan(0, actual.Length).SequenceEqual(actual),
                $"'{name}' truncated to {cut} of {entry.Compressed.Length} bytes produced {actual.Length} bytes that are not a prefix of the payload.");
            AssertBytesEqual(ZlibShortRead(truncated), actual, entry, $"truncated to {cut} of {entry.Compressed.Length} bytes");
        }
    }

    /// <summary>
    /// Requirement two: the ADLER-32 is checked when a whole four-byte trailer is there, and is not required to
    /// be there at all. A stream missing one, two, three or all four trailer bytes still yields every payload
    /// byte and raises nothing - it just never reports <see cref="Inflater.Finished"/> - while flipping a bit
    /// in any of the four bytes of a complete trailer is rejected.
    /// </summary>
    [Fact]
    public void AdlerTrailer_IsCheckedWhenCompleteAndNotRequiredToExist()
    {
        foreach (string name in new[] { "size-259-nocompression", "text-30000-optimal", "png-like-320x64x4-fastest", "two-symbol-5000-smallestsize" })
        {
            InflateCase entry = InflateTestStreams.ByName(name);

            (byte[] complete, bool finished) = RunIncrementalWithState(entry.Compressed, strideHint: 0);
            AssertBytesEqual(entry.Expected, complete, entry, "complete stream");
            Assert.True(finished, $"'{name}' has a valid trailer, so the decoder should have reported the stream finished.");

            for (int missing = 1; missing <= 4; missing++)
            {
                byte[] withoutTrailer = entry.Compressed[..^missing];
                (byte[] actual, bool done) = RunIncrementalWithState(withoutTrailer, strideHint: 0);

                AssertBytesEqual(entry.Expected, actual, entry, $"last {missing} trailer byte(s) removed");
                Assert.False(done, $"'{name}' is missing {missing} trailer byte(s), so the stream is not finished - it is out of input.");
            }

            for (int position = 1; position <= 4; position++)
            {
                byte[] corrupt = (byte[])entry.Compressed.Clone();
                corrupt[^position] ^= 0x01;

                InvalidImageContentException failure = Assert.Throws<InvalidImageContentException>(
                    () => Inflate.Decompress(corrupt, int.MaxValue, "test"));
                Assert.Contains("ADLER-32", failure.Message, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Requirement three, and the one deliberate divergence from the oracle on well-formed-looking input.
    /// RFC 1950 needs a two-byte header and a four-byte trailer, so zero bytes is not a zlib stream; both
    /// <see cref="ZLibStream"/> builds nevertheless decompress an empty input - and a one, two or three byte
    /// one - to zero bytes without complaint. <see cref="Inflate.Decompress"/> treats them all as truncated,
    /// which is what a PNG whose IDAT is missing needs. The assertion is on the documented behaviour, not on
    /// the framework's quirk; <c>truncated-empty-input</c> in the malformed table is the same case seen from
    /// the verdict-comparison side.
    /// </summary>
    [Fact]
    public void ZeroAndPartialHeaderInput_IsTreatedAsTruncated()
    {
        byte[] source = InflateTestStreams.ByName("text-700-optimal").Compressed;

        for (int length = 0; length <= 3; length++)
        {
            byte[] head = source[..length];

            InvalidImageContentException failure = Assert.Throws<InvalidImageContentException>(
                () => Inflate.Decompress(head, int.MaxValue, "test"));
            Assert.Contains("truncated", failure.Message, StringComparison.Ordinal);

            // And through the incremental interface it is simply no output and not finished, which is the same
            // thing said without an exception.
            (byte[] actual, bool finished) = RunIncrementalWithState(head, strideHint: 0);
            Assert.Empty(actual);
            Assert.False(finished);
        }
    }

    /// <summary>
    /// The other half of that quirk, from the corpus side. The generator hands an empty payload to
    /// <see cref="ZLibStream"/>'s compressor and gets zero bytes back, on both frameworks - not the six-byte
    /// stream RFC 1950 defines for no data - so those cases carry nothing for a decoder to decode. They are
    /// excluded from <see cref="CorpusNames"/> and pinned here instead: zero bytes in, truncated out.
    /// </summary>
    [Fact]
    public void EmptyPayloadStreams_AreNotZlibStreamsAndAreTreatedAsTruncated()
    {
        foreach (InflateCase entry in InflateTestStreams.Corpus.Where(candidate => candidate.Compressed.Length == 0))
        {
            Assert.Empty(entry.Expected);
            Assert.Throws<InvalidImageContentException>(() => Inflate.Decompress(entry.Compressed, int.MaxValue, "test"));
        }
    }

    // =============================================================================================
    // Window rewind
    // =============================================================================================

    /// <summary>
    /// A payload built entirely out of matches at the maximum distance, long enough that the window is rewound
    /// a dozen times, pulled out in pieces that do not divide the window. Every rewind moves history under a
    /// match that is still being copied, so a rewind that dropped or misplaced a byte of history shows here.
    /// </summary>
    [Fact]
    public void Inflater_RewindsWindowWithoutLosingHistory()
    {
        // 768 KiB of output through a 64 KiB emit region inside a 96 KiB window buffer is at least a dozen
        // rewinds, and every byte past the first 32 KiB is a back-reference that spans one of them.
        byte[] payload = LongDistancePayload(768 * 1024, seed: 20260903);
        byte[] compressed = ZlibCompress(payload, CompressionLevel.Optimal);

        byte[] reference = InflateTestStreams.ZlibReference(compressed);
        Assert.Equal(payload.Length, reference.Length);

        var entry = new InflateCase("window-rewind", compressed, reference, $"family=window-rewind length={payload.Length} (every byte a match at distance {MaxDistance})");

        foreach (int[] chunks in new[] { new[] { 997 }, new[] { MaxDistance - 1 }, new[] { 1, 258, 65_535 } })
        {
            byte[] actual = RunIncremental(compressed, RandomPattern(new Random(7), compressed.Length), chunks, strideHint: 0);
            AssertBytesEqual(reference, actual, entry, $"output chunks [{Describe(chunks)}]");
        }
    }

    // =============================================================================================
    // ADLER-32
    // =============================================================================================

    /// <summary>
    /// <see cref="Adler32"/> against two independent references: the trailer zlib's own compressor wrote for
    /// every stream in the corpus, and the from-the-specification loop in <see cref="InflateTestStreams"/>.
    /// </summary>
    [Fact]
    public void Adler32_MatchesTheTrailerZlibWrote()
    {
        int compared = 0;
        foreach (InflateCase entry in InflateTestStreams.GeneratedCorpus(InflateTestStreams.DefaultSeed, 0))
        {
            if (entry.Compressed.Length < 6 || entry.Expected.Length > TrailerComparisonLimit)
            {
                // Skipped: the empty-payload cases, which one of the two frameworks compresses to nothing at
                // all, and the payloads big enough that checksumming them here would cost more than the rest of
                // this file put together. The large ones are not left unchecked - the decoder verifies the same
                // trailer itself on every whole-buffer run, which is what makes a corrupt one a failure there.
                continue;
            }

            uint trailer = BinaryPrimitives.ReadUInt32BigEndian(entry.Compressed.AsSpan(entry.Compressed.Length - 4));
            uint computed = Adler32.Compute(entry.Expected);

            Assert.True(
                trailer == computed,
                $"'{entry.Name}': zlib wrote ADLER-32 0x{trailer:x8} for {entry.Expected.Length:N0} bytes, Adler32.Compute returned 0x{computed:x8}.");
            Assert.Equal(InflateTestStreams.Adler32(entry.Expected), computed);
            compared++;
        }

        Assert.True(compared >= 250, $"Only {compared} streams were compared; the generated corpus is meant to be several hundred.");
    }

    /// <summary>
    /// The checksum is folded in over whatever slices the window happens to hand it, so appending in pieces has
    /// to give the same answer as one call - and the vectorised path has to give the same answer as the scalar
    /// one, on every length either side of its block boundaries.
    /// </summary>
    [Fact]
    public void Adler32_ScalarAndVectorAndPiecewiseAgree()
    {
        var random = new Random(20260903);
        byte[] data = new byte[70_000];
        random.NextBytes(data);

        foreach (int length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 5551, 5552, 5553, 11104, 65_535, 65_536, 70_000 })
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            uint specification = InflateTestStreams.Adler32(slice);

            uint vectorized = RunWithScalarFallback(false, () => Adler32.Compute(data.AsSpan(0, length)));
            uint scalar = RunWithScalarFallback(true, () => Adler32.Compute(data.AsSpan(0, length)));

            Assert.True(scalar == specification, $"Scalar Adler32 over {length} bytes returned 0x{scalar:x8}, RFC 1950 says 0x{specification:x8}.");
            Assert.True(vectorized == specification, $"Vectorised Adler32 over {length} bytes returned 0x{vectorized:x8}, RFC 1950 says 0x{specification:x8}.");

            uint piecewise = 1;
            for (int position = 0; position < length; position += 997)
            {
                piecewise = Adler32.Append(piecewise, data.AsSpan(position, Math.Min(997, length - position)));
            }

            Assert.True(piecewise == specification, $"Adler32 appended in 997-byte pieces over {length} bytes returned 0x{piecewise:x8}, RFC 1950 says 0x{specification:x8}.");
        }
    }

    // =============================================================================================
    // Drivers
    // =============================================================================================

    /// <summary>Take everything the decoder has each time; the emit region caps it.</summary>
    private static readonly int[] WholeOutput = { int.MaxValue };

    /// <summary>Take exactly one byte each time.</summary>
    private static readonly int[] SingleByteOutput = { 1 };

    /// <summary>
    /// Pushes <paramref name="compressed"/> through <see cref="Inflater"/> in the given input segments, pulling
    /// output in the given repeating cycle of chunk sizes, and returns everything it produced. A stream that
    /// runs out of input simply stops, which is what makes this the driver for the truncation suite as well.
    /// </summary>
    /// <param name="compressed">The stream, whole; <paramref name="segmentLengths"/> decides how it is fed.</param>
    /// <param name="segmentLengths">
    /// Lengths of the successive input segments. Once the list is spent the rest of the stream is fed as one
    /// final segment, so an empty list means "hand it everything at once".
    /// </param>
    /// <param name="chunkSizes">Repeating cycle of byte counts to ask <see cref="Inflater.Fill"/> for.</param>
    /// <param name="strideHint">Passed straight to the constructor, where it sizes the emit region.</param>
    private static byte[] RunIncremental(byte[] compressed, IReadOnlyList<int> segmentLengths, IReadOnlyList<int> chunkSizes, int strideHint)
        => RunIncrementalCore(compressed, segmentLengths, chunkSizes, strideHint).Output;

    /// <summary>As <see cref="RunIncremental"/> over the whole stream at once, also reporting whether it ended.</summary>
    /// <param name="compressed">The stream, whole.</param>
    /// <param name="strideHint">Passed straight to the constructor, where it sizes the emit region.</param>
    private static (byte[] Output, bool Finished) RunIncrementalWithState(byte[] compressed, int strideHint)
        => RunIncrementalCore(compressed, Array.Empty<int>(), WholeOutput, strideHint);

    private static (byte[] Output, bool Finished) RunIncrementalCore(byte[] compressed, IReadOnlyList<int> segmentLengths, IReadOnlyList<int> chunkSizes, int strideHint)
    {
        // Tens of thousands of runs go through here, so the two buffers are reused rather than reallocated -
        // a fresh 128 KB scratch per run on its own costs more than all the decoding does.
        byte[] scratch = TakeScratch;
        MemoryStream output = TakeOutput;
        var inflater = new Inflater(strideHint, "test");
        try
        {
            int position = 0;
            int segment = 0;
            int chunk = 0;
            inflater.SetInput(NextSegment(compressed, segmentLengths, ref segment, ref position));

            while (true)
            {
                int wanted = Math.Max(1, Math.Min(chunkSizes[chunk % chunkSizes.Count], inflater.EmitCapacity));
                chunk++;

                InflateStatus status = inflater.Fill(wanted);

                int take = Math.Min(inflater.Available, wanted);
                Append(output, scratch, inflater.Take(take));

                if (inflater.Finished)
                {
                    // Whatever the last pass produced past the request still belongs to the caller.
                    Append(output, scratch, inflater.Take(inflater.Available));
                    return (output.ToArray(), true);
                }

                if (status == InflateStatus.NeedInput)
                {
                    if (position >= compressed.Length && segment > segmentLengths.Count)
                    {
                        return (output.ToArray(), false);
                    }

                    inflater.SetInput(NextSegment(compressed, segmentLengths, ref segment, ref position));
                }
            }
        }
        finally
        {
            inflater.Dispose();
        }
    }

    /// <summary>Drains the stream through <see cref="Inflater.ReadInto"/> in fixed-size destinations.</summary>
    /// <param name="compressed">The stream, whole.</param>
    /// <param name="destinationSize">Size of the buffer handed to <c>ReadInto</c> each time.</param>
    /// <param name="strideHint">Passed straight to the constructor, where it sizes the emit region.</param>
    private static byte[] RunReadInto(byte[] compressed, int destinationSize, int strideHint)
    {
        var output = new MemoryStream();
        byte[] destination = new byte[destinationSize];
        var inflater = new Inflater(strideHint, "test");
        try
        {
            inflater.SetInput(compressed);
            while (true)
            {
                int read = inflater.ReadInto(destination);
                output.Write(destination, 0, read);
                if (read < destination.Length)
                {
                    return output.ToArray();
                }
            }
        }
        finally
        {
            inflater.Dispose();
        }
    }

    /// <summary>A reusable copy buffer, wide enough for a full emit region plus the fast loop's overrun.</summary>
    private static byte[] TakeScratch => scratchBuffer ??= new byte[1 << 17];

    /// <summary>A reusable output sink, rewound on every hand-out.</summary>
    private static MemoryStream TakeOutput
    {
        get
        {
            MemoryStream stream = outputBuffer ??= new MemoryStream();
            stream.SetLength(0);
            return stream;
        }
    }

    [ThreadStatic]
    private static byte[]? scratchBuffer;

    [ThreadStatic]
    private static MemoryStream? outputBuffer;

    private static void Append(MemoryStream output, byte[] scratch, ReadOnlySpan<byte> taken)
    {
        if (taken.IsEmpty)
        {
            return;
        }

        taken.CopyTo(scratch);
        output.Write(scratch, 0, taken.Length);
    }

    /// <summary>
    /// Hands out the next input segment and advances the cursor. Segment <c>segmentLengths.Count</c> is the
    /// remainder of the stream, and everything after that is empty, which is what terminates the driver.
    /// </summary>
    /// <param name="compressed">The stream being fed.</param>
    /// <param name="segmentLengths">The pattern; see <see cref="RunIncremental"/>.</param>
    /// <param name="segment">Index of the segment about to be handed out; incremented.</param>
    /// <param name="position">Offset the next segment starts at; advanced.</param>
    private static ReadOnlySpan<byte> NextSegment(byte[] compressed, IReadOnlyList<int> segmentLengths, scoped ref int segment, scoped ref int position)
    {
        int length = segment < segmentLengths.Count
            ? Math.Min(segmentLengths[segment], compressed.Length - position)
            : compressed.Length - position;

        segment++;
        ReadOnlySpan<byte> slice = compressed.AsSpan(position, length);
        position += length;
        return slice;
    }

    // =============================================================================================
    // Pattern selection
    // =============================================================================================

    /// <summary>
    /// The input-split patterns a stream of this size is worth. Every pattern ends with an implicit "and the
    /// rest", so a single number is a two-way split. See the class remarks for why the counts are what they are.
    /// </summary>
    /// <param name="length">Compressed length of the stream.</param>
    private static int[][] SegmentationPatterns(int length)
    {
        var patterns = new List<int[]>();

        if (length <= ExhaustiveSplitLimit)
        {
            for (int cut = 0; cut <= length; cut++)
            {
                patterns.Add(new[] { cut });
            }

            patterns.Add(SingleByteSegments(length));
            patterns.Add(WithEmptySegments(SingleByteSegments(length)));
        }
        else
        {
            int count = length <= LargeStreamLimit ? 6 : 3;
            var random = new Random(length);
            for (int i = 0; i < count; i++)
            {
                patterns.Add(RandomPattern(random, length));
            }

            if (length <= 8192)
            {
                patterns.Add(SingleByteSegments(length));
            }
        }

        // Always finish with "everything at once", so the list is never empty and the cheapest shape is covered.
        patterns.Add(Array.Empty<int>());
        return patterns.ToArray();
    }

    /// <summary>
    /// The segmentations the output-chunking sweep is crossed with. Deliberately fewer than
    /// <see cref="SegmentationPatterns"/> returns - the point of this tier is the output side, and the input
    /// side is already swept on its own.
    /// </summary>
    /// <param name="length">Compressed length of the stream.</param>
    private static int[][] ChunkingSegmentations(int length)
    {
        var random = new Random(length + 1);
        if (length <= ExhaustiveSplitLimit)
        {
            return new[] { Array.Empty<int>(), SingleByteSegments(length), RandomPattern(random, length) };
        }

        return new[] { Array.Empty<int>(), RandomPattern(random, length) };
    }

    /// <summary>
    /// The output chunk cycles a stream of this output size is worth: one byte at a time only while that is
    /// affordable, then progressively coarser cycles.
    /// </summary>
    /// <param name="expandedLength">Number of bytes the stream decodes to.</param>
    private static int[][] OutputChunkPolicies(int expandedLength)
    {
        if (expandedLength <= ByteAtATimeOutputLimit)
        {
            return new[] { new[] { 3, 1, 7, 2, 258, 1 }, new[] { 1021 }, WholeOutput };
        }

        if (expandedLength <= 1 << 20)
        {
            return new[] { new[] { 7, 4093, 1, 65_536 }, new[] { 1021 }, WholeOutput };
        }

        return new[] { new[] { 7, 4093, 1, 65_536 }, WholeOutput };
    }

    private static int[] SingleByteSegments(int length)
    {
        int[] pattern = new int[length];
        pattern.AsSpan().Fill(1);
        return pattern;
    }

    /// <summary>Doubles a pattern up with zero-length segments, which a caller with an empty IDAT really does hand over.</summary>
    /// <param name="pattern">The pattern to interleave.</param>
    private static int[] WithEmptySegments(int[] pattern)
    {
        int[] doubled = new int[pattern.Length * 2];
        for (int i = 0; i < pattern.Length; i++)
        {
            doubled[i * 2] = 0;
            doubled[(i * 2) + 1] = pattern[i];
        }

        return doubled;
    }

    /// <summary>A seeded split pattern mixing zero-length, one-byte and bulk segments.</summary>
    /// <param name="random">Seeded from the stream length, so the pattern is the same on every run.</param>
    /// <param name="length">Compressed length of the stream.</param>
    private static int[] RandomPattern(Random random, int length)
    {
        var pattern = new List<int>();
        int covered = 0;
        while (covered < length)
        {
            int piece = random.Next(10) switch
            {
                0 => 0,
                1 or 2 => 1,
                3 or 4 => random.Next(2, 9),
                5 or 6 => random.Next(9, 129),
                _ => random.Next(129, Math.Max(130, length / 4)),
            };

            piece = Math.Min(piece, length - covered);
            pattern.Add(piece);
            covered += piece;
        }

        return pattern.ToArray();
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    /// <summary>Compresses through the framework, so the stream under test is always zlib's own output.</summary>
    /// <param name="payload">The bytes to compress.</param>
    /// <param name="level">The compression level to use.</param>
    private static byte[] ZlibCompress(byte[] payload, CompressionLevel level)
    {
        var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, level, leaveOpen: true))
        {
            zlib.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Drains <see cref="ZLibStream"/>, keeping whatever it produced if it stops or fails part way.</summary>
    /// <param name="compressed">The stream, which may be truncated.</param>
    private static byte[] ZlibShortRead(byte[] compressed)
    {
        var output = new MemoryStream();
        try
        {
            using var source = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            zlib.CopyTo(output);
        }
        catch (InvalidDataException)
        {
            // Whatever reached the output before it gave up is still what it decoded.
        }

        return output.ToArray();
    }

    /// <summary>Whether <see cref="ZLibStream"/> refuses the stream outright.</summary>
    /// <param name="compressed">The stream to decode.</param>
    private static bool ZlibRejects(byte[] compressed)
    {
        try
        {
            _ = InflateTestStreams.ZlibReference(compressed);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
        catch (IOException)
        {
            // The preset-dictionary case raises ZLibException, which derives from IOException rather than from
            // InvalidDataException and is not surfaced by the reference assembly, so its base type is the arm.
            // The source is a MemoryStream, so nothing else here can raise one.
            return true;
        }
    }

    /// <summary>
    /// A payload whose every byte past the first window is a copy of the byte exactly
    /// <see cref="MaxDistance"/> back, with a fresh byte now and then so the compressor keeps emitting matches
    /// rather than collapsing the whole thing into one run.
    /// </summary>
    /// <param name="length">Bytes to produce.</param>
    /// <param name="seed">Seed for the random prefix, so the payload is the same on every run.</param>
    private static byte[] LongDistancePayload(int length, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[length];
        random.NextBytes(data.AsSpan(0, Math.Min(MaxDistance, length)));

        for (int i = MaxDistance; i < length; i++)
        {
            data[i] = i % 4096 == 0 ? (byte)random.Next(256) : data[i - MaxDistance];
        }

        return data;
    }

    private static T RunWithScalarFallback<T>(bool forceScalar, Func<T> body)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            return body();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    /// <summary>Renders a pattern short enough to read in a failure message.</summary>
    /// <param name="pattern">Segment lengths or output chunk sizes.</param>
    private static string Describe(IReadOnlyList<int> pattern)
    {
        if (pattern.Count == 0)
        {
            return "all at once";
        }

        if (pattern.Count > 8)
        {
            return $"{string.Join(",", pattern.Take(8))},... ({pattern.Count} pieces)";
        }

        return string.Join(",", pattern);
    }

    /// <summary>
    /// Compares against zlib's bytes and, on a mismatch, says exactly where and how the stream was built - the
    /// recipe is enough to rebuild it without a debugger.
    /// </summary>
    /// <param name="expected">What zlib produced.</param>
    /// <param name="actual">What <see cref="Inflater"/> produced.</param>
    /// <param name="entry">The case under test, for its name and recipe.</param>
    /// <param name="how">How this decode was driven.</param>
    private static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, InflateCase entry, string how)
    {
        if (expected.SequenceEqual(actual))
        {
            return;
        }

        int shared = Math.Min(expected.Length, actual.Length);
        int offset = 0;
        while (offset < shared && expected[offset] == actual[offset])
        {
            offset++;
        }

        string detail = offset < shared
            ? $"first difference at byte {offset:N0}: expected 0x{expected[offset]:x2}, got 0x{actual[offset]:x2}"
            : $"identical for all {shared:N0} shared bytes, then the lengths differ";

        Assert.Fail(
            $"'{entry.Name}' decoded by {how} does not match ZLibStream.{Environment.NewLine}" +
            $"  recipe:   {entry.Recipe}{Environment.NewLine}" +
            $"  expected: {expected.Length:N0} bytes{Environment.NewLine}" +
            $"  actual:   {actual.Length:N0} bytes{Environment.NewLine}" +
            $"  {detail}{Environment.NewLine}" +
            $"  expected around it: {Hex(expected, offset)}{Environment.NewLine}" +
            $"  actual around it:   {Hex(actual, offset)}");
    }

    /// <summary>Sixteen bytes either side of an offset, as hex.</summary>
    /// <param name="data">The buffer to sample.</param>
    /// <param name="offset">Where the difference is.</param>
    private static string Hex(ReadOnlySpan<byte> data, int offset)
    {
        int start = Math.Max(0, offset - 8);
        int end = Math.Min(data.Length, offset + 8);
        return start >= end ? "(none)" : Convert.ToHexString(data[start..end]);
    }
}
