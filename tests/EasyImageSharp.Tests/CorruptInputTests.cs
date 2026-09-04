using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Deterministic crafted inputs that violate each format's invariants. The contract under test: every
/// decoder must finish quickly and fail only through <see cref="ImageFormatException"/> (malformed data,
/// declared limits) or <see cref="NotSupportedException"/> (recognized but unimplemented features) -
/// never a framework exception, never a hang and never a runaway allocation.
/// </summary>
public class CorruptInputTests
{
    // A hang detector, not a speed limit: these decodes take milliseconds, so anything approaching this
    // budget is an infinite loop. Generous so a loaded CI machine cannot turn slowness into a false failure.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // =====================================================================================================
    // PNG
    // =====================================================================================================

    [Fact]
    public Task Png_ChunkLengthBeyondFile()
        => AssertRejected(Png(Ihdr(4, 4, 8, 2), ChunkWithLength("IDAT", new byte[16], 0x7FFFFFF0)));

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(0)]
    public Task Png_IhdrWrongLength(int length)
    {
        byte[] ihdr = Ihdr(4, 4, 8, 2);
        byte[] payload = new byte[length];
        Array.Copy(ihdr, 8, payload, 0, Math.Min(length, 13));
        return AssertRejected(Png(Chunk("IHDR", payload), Idat(4, 4, 3)));
    }

    [Theory]
    [InlineData(1, 8)]  // colour type 1 does not exist
    [InlineData(5, 8)]  // colour type 5 does not exist
    [InlineData(7, 8)]
    [InlineData(2, 4)]  // truecolor only allows 8/16
    [InlineData(3, 16)] // palette only allows 1/2/4/8
    [InlineData(0, 3)]  // grayscale allows 1/2/4/8/16
    [InlineData(6, 1)]
    [InlineData(4, 32)]
    public Task Png_InvalidColorTypeDepthCombination(byte colorType, byte depth)
        => AssertRejected(Png(Ihdr(4, 4, depth, colorType), Idat(4, 4, 4)));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    [InlineData(4, int.MinValue)]
    public Task Png_ZeroOrNegativeDimensions(int width, int height)
        => AssertRejected(Png(Ihdr(width, height, 8, 2), Idat(4, 4, 3)));

    [Fact]
    public Task Png_MissingIdat() => AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IEND", Array.Empty<byte>())));

    [Fact]
    public Task Png_IdatGarbageZlib()
        => AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 })));

    /// <summary>
    /// Fuzz regression: a zlib FLG byte requesting a preset dictionary (or failing its check bits) makes the
    /// managed inflater raise ZLibException rather than InvalidDataException; both must surface as InvalidImageContentException.
    /// </summary>
    [Theory]
    [InlineData(0x20)]
    [InlineData(0xBF)]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public Task Png_IdatZlibHeaderCorrupt(byte flg)
    {
        byte[] idat = Zlib(RawScanlines(4, 4, 3));
        idat[1] = flg;
        return AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", idat)));
    }

    [Fact]
    public Task Png_InflatedDataTooShort()
        => AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(RawScanlines(4, 2, 3)))));

    [Fact]
    public Task Png_InflatedDataTooLong()
        => AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(Concat(RawScanlines(4, 4, 3), new byte[100])))));

    [Fact]
    public Task Png_PaletteIndexBeyondPlte()
    {
        byte[] raw = RawScanlines(4, 4, 1);
        raw[1] = 5; // index 5 with a 2-entry palette
        return AssertRejected(Png(Ihdr(4, 4, 8, 3), Chunk("PLTE", new byte[6]), Chunk("IDAT", Zlib(raw))));
    }

    [Fact]
    public Task Png_TrnsLongerThanPlte()
        => AssertRejected(Png(Ihdr(4, 4, 8, 3), Chunk("PLTE", new byte[6]), Chunk("tRNS", new byte[3]), Idat(4, 4, 1)));

    [Fact]
    public Task Png_TrnsBeforePlte()
        => AssertRejected(Png(Ihdr(4, 4, 8, 3), Chunk("tRNS", new byte[2]), Chunk("PLTE", new byte[6]), Idat(4, 4, 1)));

    [Theory]
    [InlineData(0, 3)] // grayscale key must be 2 bytes
    [InlineData(2, 5)] // truecolor key must be 6 bytes
    public Task Png_TrnsWrongLengthForColorKey(byte colorType, int length)
        => AssertRejected(Png(Ihdr(4, 4, 8, colorType), Chunk("tRNS", new byte[length]), Idat(4, 4, colorType == 0 ? 1 : 3)));

    [Theory]
    [InlineData(4)]   // not a multiple of 3
    [InlineData(0)]   // empty
    [InlineData(771)] // 257 entries
    public Task Png_PlteInvalidLength(int length)
        => AssertRejected(Png(Ihdr(4, 4, 8, 3), Chunk("PLTE", new byte[length]), Idat(4, 4, 1)));

    [Fact]
    public Task Png_PaletteImageWithoutPlte() => AssertRejected(Png(Ihdr(4, 4, 8, 3), Idat(4, 4, 1)));

    [Fact]
    public Task Png_Adam7SmallImageTruncatedData()
        => AssertRejected(Png(Ihdr(3, 3, 8, 2, interlace: 1), Chunk("IDAT", Zlib(new byte[3]))));

    [Fact]
    public Task Png_Adam7OneByOneWithTooMuchData()
        => AssertRejected(Png(Ihdr(1, 1, 8, 0, interlace: 1), Chunk("IDAT", Zlib(new byte[40]))));

    [Fact]
    public Task Png_IhdrNotFirst()
        => AssertRejected(Png(Chunk("tEXt", "a\0b"u8.ToArray()), Ihdr(4, 4, 8, 2), Idat(4, 4, 3)));

    [Fact]
    public Task Png_DuplicateIhdr() => AssertRejected(Png(Ihdr(4, 4, 8, 2), Ihdr(2, 2, 8, 2), Idat(4, 4, 3)));

    [Theory]
    [InlineData(1, 0, 0)] // compression method
    [InlineData(0, 1, 0)] // filter method
    [InlineData(0, 0, 2)] // interlace method
    public Task Png_InvalidMethods(byte compression, byte filter, byte interlace)
    {
        byte[] ihdr = Ihdr(4, 4, 8, 2);
        ihdr[8 + 10] = compression;
        ihdr[8 + 11] = filter;
        ihdr[8 + 12] = interlace;
        return AssertRejected(Png(FixCrc(ihdr), Idat(4, 4, 3)));
    }

    [Fact]
    public Task Png_InvalidFilterTypeByte()
    {
        byte[] raw = RawScanlines(4, 4, 3);
        raw[13] = 9;
        return AssertRejected(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(raw))));
    }

    [Fact]
    public Task Png_TruncatedInsideIhdr() => AssertRejected(Png(Ihdr(4, 4, 8, 2))[..20]);

    [Fact]
    public Task Png_OnlySignature() => AssertRejected(PngSignature.ToArray());

    // =====================================================================================================
    // PNG deflate region
    //
    // These are the only tests in this file where the two target frameworks run different code: net8.0
    // inflates IDAT and fdAT payloads with this library's managed Inflater, net10.0 with the runtime's
    // ZLibStream over zlib-ng. Both must reject every malformed stream below through
    // InvalidImageContentException with no framework exception escaping, so each case is really two.
    //
    // The streams are written bit by bit rather than produced by corrupting a compressor's output, so each
    // one names the DEFLATE rule it breaks. PngDeflate_HandBuiltControlStreamsDecode is what keeps them
    // honest: it builds well-formed stored and fixed-Huffman blocks with the same writer and requires them
    // to decode to the same pixels as a ZLibStream-compressed IDAT, so a case below cannot pass merely
    // because the bit writer emits nonsense.
    // =====================================================================================================

    /// <summary>The control: the bit writer really does produce streams both backends accept.</summary>
    [Fact]
    public void PngDeflate_HandBuiltControlStreamsDecode()
    {
        byte[] raw = RawScanlines(4, 4, 3);
        using Image<Rgba32> reference = Image.Load<Rgba32>(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(raw))));

        var stored = new DeflateWriter().Field(1, 1).Field(0, 2).Align()
            .Field(raw.Length, 16).Field(~raw.Length & 0xFFFF, 16).Bytes(raw);

        var fixedHuffman = new DeflateWriter().Field(1, 1).Field(1, 2);
        foreach (byte value in raw)
        {
            fixedHuffman.Literal(value);
        }

        fixedHuffman.Code(0, 7); // End of block.

        foreach ((string name, DeflateWriter writer) in new[] { ("stored", stored), ("fixed", fixedHuffman) })
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(writer, raw))));
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Assert.True(image[x, y].Equals(reference[x, y]), $"{name}: pixel ({x},{y}) differs from the ZLibStream-compressed IDAT.");
                }
            }
        }
    }

    /// <summary>Block type 3 is reserved and has never been assigned a meaning.</summary>
    [Fact]
    public Task PngDeflate_ReservedBlockType()
        => AssertInvalidContent(DeflatePng(new DeflateWriter().Field(1, 1).Field(3, 2).Field(0, 16)));

    /// <summary>A stored block's NLEN must be the ones' complement of its LEN.</summary>
    [Fact]
    public Task PngDeflate_StoredBlockLengthComplementMismatch()
        => AssertInvalidContent(DeflatePng(new DeflateWriter().Field(1, 1).Field(0, 2).Align()
            .Field(4, 16).Field(0x1234, 16).Bytes(new byte[] { 1, 2, 3, 4 })));

    /// <summary>A stored block declaring more bytes than the chunk holds: the stream ends inside the block.</summary>
    [Fact]
    public Task PngDeflate_StoredBlockRunsPastTheEndOfTheChunk()
        => AssertInvalidContent(DeflatePng(new DeflateWriter().Field(1, 1).Field(0, 2).Align()
            .Field(4096, 16).Field(~4096 & 0xFFFF, 16).Bytes(new byte[] { 1, 2, 3, 4 })));

    /// <summary>HLIT counts 257..286 literal/length codes; 30 and 31 declare 287 and 288, which do not exist.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public Task PngDeflate_TooManyLiteralLengthCodes(int hlit)
        => AssertInvalidContent(DeflatePng(DynamicHeader(hlit, 0).Field(0, 3 * 4)));

    /// <summary>HDIST counts 1..30 distance codes; 30 and 31 declare 31 and 32.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public Task PngDeflate_TooManyDistanceCodes(int hdist)
        => AssertInvalidContent(DeflatePng(DynamicHeader(0, hdist).Field(0, 3 * 4)));

    /// <summary>Three code-length symbols of length 1: a one-bit code can only carry two, so the tree is over-subscribed.</summary>
    [Fact]
    public Task PngDeflate_OverSubscribedCodeLengthCode()
        => AssertInvalidContent(DeflatePng(CodeLengthCode(DynamicHeader(0, 0), new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 1 })));

    /// <summary>Code-length symbol 16 repeats the previous length, so it may not be the first symbol of the run.</summary>
    [Fact]
    public Task PngDeflate_CodeLengthRepeatWithNothingToRepeat()
        => AssertInvalidContent(DeflatePng(
            CodeLengthCode(DynamicHeader(0, 0), new Dictionary<int, int> { [0] = 1, [16] = 1 })
                .Code(1, 1)     // Symbol 16: the canonical one-bit code for the higher of the two symbols.
                .Field(0, 2))); // Repeat the previous length three times, except that there is no previous length.

    /// <summary>
    /// A literal/length alphabet whose only symbol is literal 0. The code is incomplete - one one-bit code
    /// where two are needed - and, symbol 256 having no code at all, the block can never end.
    /// </summary>
    [Fact]
    public Task PngDeflate_IncompleteLiteralLengthCode()
    {
        DeflateWriter writer = CodeLengthCode(DynamicHeader(0, 0), new Dictionary<int, int> { [0] = 1, [1] = 1 });
        writer.Code(1, 1); // Literal 0 gets a code length of 1 ...
        for (int i = 0; i < 257; i++)
        {
            writer.Code(0, 1); // ... and the remaining 256 literals and the single distance code get none.
        }

        return AssertInvalidContent(DeflatePng(writer));
    }

    /// <summary>Fixed-Huffman distance codes 30 and 31 are undefined.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public Task PngDeflate_UndefinedDistanceCode(int distanceCode)
        => AssertInvalidContent(DeflatePng(new DeflateWriter().Field(1, 1).Field(1, 2)
            .Literal('A')
            .Code(1, 7)                 // Length code 257: a three-byte match.
            .Code(distanceCode, 5)));

    /// <summary>A match whose distance reaches back further than the bytes produced so far.</summary>
    [Fact]
    public Task PngDeflate_DistanceReachesBeforeTheStartOfTheOutput()
        => AssertInvalidContent(DeflatePng(new DeflateWriter().Field(1, 1).Field(1, 2)
            .Literal('A')               // One byte of history ...
            .Code(1, 7)                 // ... then a three-byte match ...
            .Code(3, 5)));              // ... at distance code 3, which is a distance of 4.

    /// <summary>
    /// The zlib wrapper's own fields. Both bytes are chosen so the header's modulo-31 check still passes,
    /// which is what forces the inflater to reach the compression-method and window-size checks behind it.
    /// </summary>
    [Theory]
    [InlineData(0x79, 0x18)] // Compression method 9; only 8 (DEFLATE) is defined.
    [InlineData(0x88, 0x1C)] // CINFO 8: a 64 KiB window, above the 32 KiB maximum.
    public Task PngDeflate_UnusableZlibHeader(byte cmf, byte flg)
    {
        byte[] stream = Zlib(RawScanlines(4, 4, 3));
        stream[0] = cmf;
        stream[1] = flg;
        Assert.Equal(0, ((cmf * 256) + flg) % 31);
        return AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", stream)));
    }

    // =====================================================================================================
    // APNG
    //
    // Hand-assembled animations, one malformed chunk at a time. Apng_HandBuiltControlDecodes proves the
    // builders below produce an animation the decoder accepts, so every rejection here is caused by the
    // single field the test name gives and not by a broken scaffold.
    // =====================================================================================================

    /// <summary>The control: acTL, a whole-canvas fcTL before IDAT, and one further fcTL/fdAT frame.</summary>
    [Fact]
    public void Apng_HandBuiltControlDecodes()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(Png(
            Ihdr(4, 4, 8, 2),
            Actl(2, 0),
            Fctl(0, 4, 4),
            Idat(4, 4, 3),
            Fctl(1, 2, 2, 1, 1),
            Fdat(2, Zlib(RawScanlines(2, 2, 3)))));

        Assert.Equal(2, image.Frames.Count);
        Assert.True(image.Metadata.GetPngMetadata().IsAnimated);
    }

    /// <summary>
    /// fcTL and fdAT share one sequence series that must run 0, 1, 2, ... with no gap, repeat or reorder.
    /// </summary>
    [Fact]
    public Task Apng_SequenceNumberGap()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(2, 2, 2), Fdat(3, Zlib(RawScanlines(2, 2, 3)))));

    [Fact]
    public Task Apng_SequenceNumberRepeated()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2), Fdat(1, Zlib(RawScanlines(2, 2, 3)))));

    [Fact]
    public Task Apng_SequenceNumberDoesNotStartAtZero()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Fctl(1, 4, 4), Idat(4, 4, 3)));

    /// <summary>The series is compared for equality, never subtracted, so a wrapped number is still an error.</summary>
    [Fact]
    public Task Apng_SequenceNumberWrapsToUintMaxValue()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Fctl(uint.MaxValue, 4, 4), Idat(4, 4, 3)));

    /// <summary>An fcTL beyond the frame count acTL declared: the file has more frames than it admits to.</summary>
    [Fact]
    public Task Apng_FrameControlAfterTheLastDeclaredFrame()
        => AssertInvalidContent(Png(
            Ihdr(4, 4, 8, 2), Actl(1, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2), Fdat(2, Zlib(RawScanlines(2, 2, 3)))));

    /// <summary>An acTL frame count that no walk of the file can ever match.</summary>
    [Theory]
    [InlineData(0u)]                // Zero frames.
    [InlineData(2u)]                // One more frame than the file carries.
    [InlineData(0x80000000u)]       // Above int.MaxValue: rejected by acTL itself.
    [InlineData(uint.MaxValue)]
    public Task Apng_FrameCountDoesNotMatchTheFile(uint declared)
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(declared, 0), Fctl(0, 4, 4), Idat(4, 4, 3)), maxAllocatedBytes: 4_000_000);

    /// <summary>
    /// The regression this case exists for: an acTL declaring int.MaxValue frames must be caught by counting
    /// the frames actually found, not by sizing anything from the declared number. The allocation budget is
    /// the assertion - a single pre-sized frame list would be gigabytes.
    /// </summary>
    [Fact]
    public Task Apng_FrameCountOfIntMaxValueIsNotPreSized()
        => AssertInvalidContent(
            Png(Ihdr(4, 4, 8, 2), Actl(int.MaxValue, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2), Fdat(2, Zlib(RawScanlines(2, 2, 3)))),
            maxAllocatedBytes: 4_000_000);

    /// <summary>Every fcTL rectangle must lie inside the canvas IHDR sized, and no side may be zero.</summary>
    [Theory]
    [InlineData(5u, 4u, 0u, 0u)]                        // Wider than the canvas.
    [InlineData(4u, 5u, 0u, 0u)]                        // Taller than the canvas.
    [InlineData(2u, 2u, 3u, 0u)]                        // Fits, but not at that x offset.
    [InlineData(2u, 2u, 0u, 3u)]                        // Fits, but not at that y offset.
    [InlineData(0u, 2u, 0u, 0u)]                        // Zero width.
    [InlineData(2u, 0u, 0u, 0u)]                        // Zero height.
    [InlineData(uint.MaxValue, 1u, 0u, 0u)]             // Would overflow a signed rectangle.
    [InlineData(1u, 1u, uint.MaxValue, 0u)]
    [InlineData(0x80000000u, 0x80000000u, 0u, 0u)]      // Both sides above int.MaxValue.
    public Task Apng_FrameRectangleOutsideTheCanvas(uint width, uint height, uint xOffset, uint yOffset)
        => AssertInvalidContent(Png(
            Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3),
            Fctl(1, width, height, xOffset, yOffset), Fdat(2, Zlib(RawScanlines(2, 2, 3)))));

    /// <summary>
    /// The fcTL before IDAT introduces the IDAT image itself, which IHDR has already sized, so it must
    /// describe the whole canvas: a smaller or offset rectangle would describe an image that is not there.
    /// </summary>
    [Theory]
    [InlineData(2u, 4u, 0u, 0u)]
    [InlineData(4u, 2u, 0u, 0u)]
    [InlineData(3u, 3u, 1u, 1u)]
    public Task Apng_FirstFrameControlDoesNotCoverTheCanvas(uint width, uint height, uint xOffset, uint yOffset)
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Fctl(0, width, height, xOffset, yOffset), Idat(4, 4, 3)));

    /// <summary>Only one fcTL may precede IDAT, because IDAT is only ever one frame.</summary>
    [Fact]
    public Task Apng_TwoFrameControlsBeforeTheImageData()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Fctl(1, 4, 4), Idat(4, 4, 3)));

    /// <summary>An fdAT chunk carries one frame's data and is meaningless without the fcTL that opened the frame.</summary>
    [Fact]
    public Task Apng_FrameDataWithoutAPrecedingFrameControl()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fdat(1, Zlib(RawScanlines(2, 2, 3)))));

    /// <summary>The same, with no fcTL anywhere in the file: the IDAT image is not an fdAT frame either.</summary>
    [Fact]
    public Task Apng_FrameDataWithNoFrameControlAtAll()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Idat(4, 4, 3), Fdat(0, Zlib(RawScanlines(2, 2, 3)))));

    /// <summary>An fcTL that opens a frame no fdAT chunk ever fills.</summary>
    [Fact]
    public Task Apng_FrameControlWithNoFrameData()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2)));

    /// <summary>An fdAT chunk shorter than the four-byte sequence number it must begin with.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public Task Apng_FrameDataTooShortForItsSequenceNumber(int length)
        => AssertInvalidContent(Png(
            Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2), Chunk("fdAT", new byte[length])));

    /// <summary>acTL declares the file animated and must therefore precede the image data it is talking about.</summary>
    [Fact]
    public Task Apng_AnimationControlAfterTheImageData()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Idat(4, 4, 3), Actl(1, 0), Fctl(0, 4, 4)));

    [Fact]
    public Task Apng_TwoAnimationControlChunks()
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Actl(1, 0), Fctl(0, 4, 4), Idat(4, 4, 3)));

    /// <summary>Dispose operations run 0..2 and blend operations 0..1.</summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(255, 0)]
    [InlineData(0, 2)]
    [InlineData(0, 255)]
    public Task Apng_UndefinedDisposeOrBlendOperation(int dispose, int blend)
        => AssertInvalidContent(Png(
            Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3),
            Fctl(1, 2, 2, 0, 0, (byte)dispose, (byte)blend), Fdat(2, Zlib(RawScanlines(2, 2, 3)))));

    /// <summary>An acTL payload that is not exactly the eight bytes of num_frames and num_plays.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    public Task Apng_AnimationControlWrongLength(int length)
        => AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Chunk("acTL", new byte[length]), Fctl(0, 4, 4), Idat(4, 4, 3)));

    /// <summary>An fcTL payload that is not exactly the 26 bytes of the frame control record.</summary>
    [Theory]
    [InlineData(25)]
    [InlineData(27)]
    public Task Apng_FrameControlWrongLength(int length)
    {
        byte[] payload = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0);
        return AssertInvalidContent(Png(Ihdr(4, 4, 8, 2), Actl(1, 0), Chunk("fcTL", payload), Idat(4, 4, 3)));
    }

    /// <summary>
    /// A frame whose own deflate stream is malformed. This is the animation entry point into the inflate
    /// seam, so like the still cases above it runs the managed inflater on net8.0 and ZLibStream on net10.0.
    /// </summary>
    [Fact]
    public Task Apng_FrameDeflateStreamIsMalformed()
        => AssertInvalidContent(Png(
            Ihdr(4, 4, 8, 2), Actl(2, 0), Fctl(0, 4, 4), Idat(4, 4, 3), Fctl(1, 2, 2),
            Fdat(2, Zlib(new DeflateWriter().Field(1, 1).Field(3, 2).Field(0, 16), Array.Empty<byte>()))));

    // =====================================================================================================
    // BMP
    // =====================================================================================================

    [Fact]
    public Task Bmp_DataOffsetPastEof() => AssertRejected(Bmp(4, 4, 24, 0, new byte[64], dataOffset: 100_000));

    [Fact]
    public Task Bmp_NegativeDataOffset() => AssertRejected(Bmp(4, 4, 24, 0, new byte[64], dataOffset: -54));

    [Fact]
    public Task Bmp_DataOffsetInsideHeader() => AssertRejected(Bmp(4, 4, 24, 0, new byte[64], dataOffset: 20));

    [Fact]
    public Task Bmp_HeightIntMinValue() => AssertRejected(Bmp(4, int.MinValue, 24, 0, new byte[64]));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public Task Bmp_ZeroOrNegativeWidth(int width) => AssertRejected(Bmp(width, 4, 24, 0, new byte[64]));

    [Fact]
    public Task Bmp_ZeroHeight() => AssertRejected(Bmp(4, 0, 24, 0, new byte[64]));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(65535)]
    public Task Bmp_UnsupportedBitDepth(int bpp) => AssertRejected(Bmp(4, 4, (ushort)bpp, 0, new byte[64]));

    [Theory]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(257)]
    public Task Bmp_PaletteCountHuge(int colorsUsed)
        => AssertRejected(Bmp(4, 4, 8, 0, new byte[16], palette: new byte[256 * 4], colorsUsed: colorsUsed));

    [Fact]
    public Task Bmp_PaletteTruncated() => AssertRejected(Bmp(4, 4, 8, 0, Array.Empty<byte>(), palette: new byte[8 * 4]));

    [Fact]
    public Task Bmp_PixelDataTruncated() => AssertRejected(Bmp(10, 10, 24, 0, new byte[20]));

    [Fact]
    public Task Bmp_Rle8RunPastRowEnd()
        => AssertRejected(Bmp(4, 2, 8, 1, new byte[] { 10, 1, 0, 0, 4, 1, 0, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8AbsoluteRunPastRowEnd()
        => AssertRejected(Bmp(4, 2, 8, 1, new byte[] { 0, 6, 1, 2, 3, 4, 5, 6, 0, 0, 0, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8DeltaPastBitmap()
        => AssertRejected(Bmp(4, 2, 8, 1, new byte[] { 2, 1, 0, 2, 1, 50, 2, 1, 0, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8DeltaPastRowThenPixels()
        => AssertRejected(Bmp(4, 2, 8, 1, new byte[] { 0, 2, 4, 0, 1, 1, 0, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8TruncatedInsideAbsoluteRun()
        => AssertRejected(Bmp(8, 2, 8, 1, new byte[] { 0, 6, 1, 2 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8TruncatedInsideDelta()
        => AssertRejected(Bmp(8, 2, 8, 1, new byte[] { 0, 2, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle8RunAfterLastRow()
        => AssertRejected(Bmp(4, 1, 8, 1, new byte[] { 4, 1, 0, 0, 4, 1, 0, 1 }, palette: new byte[256 * 4]));

    [Fact]
    public Task Bmp_Rle4RunPastRowEnd()
        => AssertRejected(Bmp(4, 2, 4, 2, new byte[] { 9, 0x12, 0, 0, 0, 1 }, palette: new byte[16 * 4]));

    [Fact]
    public Task Bmp_Rle4AbsolutePastRowEnd()
        => AssertRejected(Bmp(4, 2, 4, 2, new byte[] { 0, 5, 0x12, 0x34, 0x50, 0, 0, 0, 1 }, palette: new byte[16 * 4]));

    [Fact]
    public Task Bmp_RlePaletteIndexOutOfRange()
        => AssertRejected(Bmp(4, 1, 8, 1, new byte[] { 4, 200, 0, 1 }, palette: new byte[16 * 4], colorsUsed: 16));

    [Fact]
    public Task Bmp_RleTopDownIsInvalid()
        => AssertRejected(Bmp(4, -2, 8, 1, new byte[] { 4, 1, 0, 0, 4, 1, 0, 1 }, palette: new byte[256 * 4]));

    [Theory]
    [InlineData(1, 24)] // RLE8 must be 8 bpp
    [InlineData(2, 8)]  // RLE4 must be 4 bpp
    [InlineData(3, 24)] // BITFIELDS needs 16/32
    [InlineData(3, 8)]
    public Task Bmp_CompressionBitDepthMismatch(int compression, int bpp)
        => AssertRejected(Bmp(4, 4, (ushort)bpp, compression, new byte[64], palette: new byte[256 * 4]));

    [Theory]
    [InlineData(4)]  // BI_JPEG
    [InlineData(5)]  // BI_PNG
    [InlineData(11)] // CMYK
    [InlineData(99)]
    [InlineData(-1)]
    public Task Bmp_UnknownOrUnsupportedCompression(int compression)
        => AssertRejected(Bmp(4, 4, 24, compression, new byte[64]));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(39)]
    [InlineData(-40)]
    [InlineData(int.MaxValue)]
    public Task Bmp_BadDibHeaderSize(int headerSize) => AssertRejected(Bmp(4, 4, 24, 0, new byte[64], headerSize: headerSize));

    [Fact]
    public Task Bmp_HeaderSizeLargerThanFile() => AssertRejected(Bmp(4, 4, 24, 0, new byte[64], headerSize: 4000));

    [Fact]
    public Task Bmp_OnlyMagic() => AssertRejected("BM"u8.ToArray());

    [Fact]
    public Task Bmp_TruncatedFileHeader() => AssertRejected(Bmp(4, 4, 24, 0, new byte[64])[..20]);

    [Fact]
    public Task Bmp_CoreHeaderInvalidDepth()
    {
        byte[] core = new byte[14 + 12 + 16];
        core[0] = (byte)'B';
        core[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(core.AsSpan(10), 26);
        BinaryPrimitives.WriteInt32LittleEndian(core.AsSpan(14), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(24), 16); // 16 bpp is not valid for a core header
        return AssertRejected(core);
    }

    [Fact]
    public Task Bmp_HugeDimensionsRejectedBeforeAllocation()
    {
        byte[] bmp = Bmp(60_000, 60_000, 24, 0, new byte[64]);
        return AssertRejected(bmp, maxAllocatedBytes: 1_000_000);
    }

    // =====================================================================================================
    // TIFF
    // =====================================================================================================

    [Fact]
    public Task Tiff_IfdOffsetPastEof() => AssertRejected(new TiffBuilder().Build(firstIfdOffset: 100_000));

    [Fact]
    public Task Tiff_IfdOffsetNegative() => AssertRejected(new TiffBuilder().Build(firstIfdOffset: unchecked((int)0xFFFFFFF0)));

    [Fact]
    public Task Tiff_IfdOffsetZero() => AssertRejected(new TiffBuilder().Build(firstIfdOffset: 0));

    [Fact]
    public Task Tiff_IfdEntryCountHuge()
    {
        byte[] tiff = ValidTiff().Build();
        int ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifd), 65535);
        return AssertRejected(tiff);
    }

    [Fact]
    public Task Tiff_StripOffsetsPastEof() => AssertRejected(ValidTiff().Tag(273, 4, 100_000).Build());

    [Fact]
    public Task Tiff_StripByteCountsHuge() => AssertRejected(ValidTiff().Tag(279, 4, 0xFFFFFFF0).Build());

    [Fact]
    public Task Tiff_StripDataShorterThanRows() => AssertRejected(ValidTiff().Tag(279, 4, 5).Build());

    [Fact]
    public Task Tiff_NegativeRowsPerStripWithBadStrips()
        => AssertRejected(ValidTiff().Tag(278, 4, 0xFFFFFFFF).Tag(279, 4, 0x7FFFFFFF).Build());

    [Fact]
    public Task Tiff_FewerStripsThanRows() => AssertRejected(ValidTiff().Tag(278, 4, 1).Build()); // 4 rows, 1 strip declared

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(32)]
    [InlineData(65535)]
    public Task Tiff_BitsPerSampleWeird(int bits) => AssertRejected(ValidTiff().Tag(258, 3, bits).Build());

    [Fact]
    public Task Tiff_BitsPerSampleMixed() => AssertRejected(ValidTiff().Tag(277, 3, 3).Tag(258, 3, 8, 8, 16).Build());

    [Fact]
    public Task Tiff_LzwGarbage()
        => AssertRejected(ValidTiff(new byte[] { 0x80, 0xFF, 0xFF, 0xFF, 0xFF, 0x12, 0x34, 0x56 }).Tag(259, 3, 5).Build());

    [Fact]
    public Task Tiff_LzwTruncated()
        => AssertRejected(ValidTiff(new byte[] { 0x80, 0x00 }).Tag(259, 3, 5).Build());

    [Fact]
    public Task Tiff_DeflateGarbage()
        => AssertRejected(ValidTiff(new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0x00, 0x00, 0x11, 0x22 }).Tag(259, 3, 8).Build());

    [Fact]
    public Task Tiff_DeflateTruncated()
    {
        byte[] full = Zlib(new byte[16]);
        return AssertRejected(ValidTiff(full[..(full.Length / 2)]).Tag(259, 3, 8).Build());
    }

    [Fact]
    public Task Tiff_PackBitsTruncated()
        => AssertRejected(ValidTiff(new byte[] { 0x03, 1, 2, 3, 4, 0xFE }).Tag(259, 3, 32773).Build());

    [Fact]
    public Task Tiff_PaletteMissingForPhotometric3() => AssertRejected(ValidTiff().Tag(262, 3, 3).Build());

    [Fact]
    public Task Tiff_ColorMapTooShort() => AssertRejected(ValidTiff().Tag(262, 3, 3).Tag(320, 3, new long[100]).Build());

    [Fact]
    public async Task Tiff_IfdChainCycle_Terminates()
    {
        // The next-IFD pointer of the only page points back at itself; decoding must stop after one page.
        byte[] tiff = ValidTiff().Build(nextIfdOffset: -1);
        Exception? ex = await CaptureAsync(() =>
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(tiff);
            Assert.Single(image.Frames);
            Assert.Equal(1, Image.Identify(tiff).FrameCount);
        });
        Assert.Null(ex);
    }

    [Fact]
    public Task Tiff_TwoPageCycle_Terminates()
    {
        // Page 2's next pointer goes back to page 1: three distinct pages must never be produced.
        TiffBuilder page = ValidTiff();
        byte[] first = page.Build(nextIfdOffset: 0);
        byte[] combined = new byte[first.Length * 2];
        first.CopyTo(combined, 0);
        first.CopyTo(combined, first.Length);
        int ifd0 = BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(4));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(ifd0));
        int nextPtr0 = ifd0 + 2 + (entries * 12);
        BinaryPrimitives.WriteInt32LittleEndian(combined.AsSpan(nextPtr0), first.Length + ifd0);
        BinaryPrimitives.WriteInt32LittleEndian(combined.AsSpan(first.Length + nextPtr0), ifd0);
        // Fix page 2's strip offset to point into its own copy.
        return AssertTerminates(combined, image => Assert.True(image.Frames.Count <= 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task Tiff_ZeroOrHugeWidth(long width) => AssertRejected(ValidTiff().Tag(256, 4, width & 0xFFFFFFFF).Build());

    [Fact]
    public Task Tiff_MissingStripOffsets() => AssertRejected(ValidTiff().Remove(273).Build());

    [Fact]
    public Task Tiff_MissingWidth() => AssertRejected(ValidTiff().Remove(256).Build());

    [Fact]
    public Task Tiff_TileSizeZero() => AssertRejected(ValidTiff().Remove(273).Tag(322, 4, 0).Tag(323, 4, 0).Tag(324, 4, 8).Tag(325, 4, 16).Build());

    [Fact]
    public Task Tiff_TileOffsetsPastEof()
        => AssertRejected(ValidTiff().Remove(273).Tag(322, 4, 16).Tag(323, 4, 16).Tag(324, 4, 90_000).Tag(325, 4, 256).Build());

    [Fact]
    public Task Tiff_TileByteCountsMissing()
        => AssertRejected(ValidTiff().Remove(273).Tag(322, 4, 16).Tag(323, 4, 16).Tag(324, 4, 8).Build());

    [Fact]
    public Task Tiff_TileDataShorterThanTile()
        => AssertRejected(ValidTiff().Remove(273).Remove(279).Tag(322, 4, 16).Tag(323, 4, 16).Tag(324, 4, 8).Tag(325, 4, 16).Build());

    /// <summary>Fuzz regression: a 13x10 page with an 8-million-pixel-wide tile must not allocate a 258 MB tile buffer.</summary>
    [Fact]
    public Task Tiff_HugeTileWidthIsBoundedByPixelLimit()
    {
        byte[] tiff = ValidTiff(new byte[64]).Remove(273).Remove(279)
            .Tag(322, 4, 8_060_944).Tag(323, 4, 16).Tag(324, 4, TiffBuilder.PayloadOffset).Tag(325, 4, 64).Build();
        return AssertRejected(tiff, requiredType: typeof(ImageSizeLimitExceededException), maxAllocatedBytes: 4_000_000,
            options: new DecoderOptions { MaxPixels = 4_000_000 });
    }

    [Theory]
    [InlineData(259, 2)]     // CCITT
    [InlineData(259, 6)]     // old-style JPEG
    [InlineData(259, 34712)] // JPEG 2000
    [InlineData(262, 6)]     // YCbCr on a single-sample page
    [InlineData(262, 5)]     // CMYK on a single-sample page
    [InlineData(262, 8)]     // CIELab on a single-sample page
    [InlineData(284, 3)]     // undefined planar configuration
    [InlineData(277, 5)]     // 5 samples
    [InlineData(339, 3)]     // float
    [InlineData(317, 3)]     // floating-point predictor
    public Task Tiff_UnsupportedFeatureThrowsNotSupported(int tag, long value)
    {
        byte[] tiff = ValidTiff().Tag(tag, 3, value).Build();
        return AssertRejected(tiff, requiredType: typeof(NotSupportedException));
    }

    [Fact]
    public Task Tiff_WrongMagic()
    {
        byte[] tiff = ValidTiff().Build();
        tiff[2] = 43;
        return AssertRejected(tiff);
    }

    [Fact]
    public Task Tiff_ByteOrderMismatch()
    {
        byte[] tiff = ValidTiff().Build();
        tiff[0] = (byte)'M';
        tiff[1] = (byte)'M';
        return AssertRejected(tiff);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    public Task Tiff_TruncatedHeader(int length) => AssertRejected(ValidTiff().Build()[..length]);

    [Fact]
    public Task Tiff_HugeDimensionsRejectedBeforeAllocation()
        => AssertRejected(ValidTiff().Tag(256, 4, 60_000).Tag(257, 4, 60_000).Build(), maxAllocatedBytes: 1_000_000);

    [Fact]
    public Task Tiff_ManyIfdEntriesReferencingLargeArrays_DoesNotBalloonAllocation()
    {
        // 2000 unknown entries and 500 duplicates of a real tag, all pointing at the same 32 KB region:
        // nothing may be materialized for them, so decoding stays well under a few MB of allocation.
        byte[] payload = new byte[32 * 1024];
        payload[0] = 0x10; // first 16 bytes double as the 4x4 strip
        TiffBuilder b = ValidTiff(payload).Tag(279, 4, 16);
        for (int i = 0; i < 2000; i++)
        {
            b.RawEntry(40_000 + (i % 7), 1, payload.Length, TiffBuilder.PayloadOffset);
        }

        for (int i = 0; i < 500; i++)
        {
            b.RawEntry(273, 4, payload.Length / 4, TiffBuilder.PayloadOffset);
        }

        return AssertTerminates(b.Build(), image => Assert.Equal(4, image.Width), maxAllocatedBytes: 4_000_000);
    }

    // =====================================================================================================
    // BigTIFF
    //
    // The version-43 container widens every offset in the file to 64 bits, which is exactly the width at
    // which a range check can be made to wrap. BigTiff_HandBuiltControlDecodes proves the builder produces
    // a file the decoder accepts, so each rejection below is caused by the one field the name gives.
    // =====================================================================================================

    /// <summary>The control: a hand-assembled version-43 file the decoder accepts.</summary>
    [Fact]
    public void BigTiff_HandBuiltControlDecodes()
    {
        byte[] file = ValidBigTiff().Build();
        Assert.Equal(43, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)));

        using Image<Rgba32> image = Image.Load<Rgba32>(file);
        Assert.True(image.Width == 4 && image.Height == 4, $"the control file decoded to {image.Width}x{image.Height}.");
        Assert.Equal(4, Image.Identify(file).Width);
    }

    /// <summary>The header's 8-byte pointer to the first directory, given every shape that is not a directory.</summary>
    [Theory]
    [InlineData(100_000L)]
    [InlineData(1L)]                    // Inside the header itself.
    [InlineData(0L)]                    // The end-of-chain marker, so there is no first page at all.
    [InlineData(-1L)]                   // 0xFFFFFFFFFFFFFFFF: beyond long.MaxValue, so it never becomes an index.
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public Task BigTiff_FirstDirectoryOffsetOutOfRange(long offset)
        => AssertInvalidContent(ValidBigTiff().Build(firstIfdOffset: offset));

    /// <summary>
    /// The truncation trap: the low 32 bits of this offset are the real directory, and only the high word
    /// puts it beyond the file. A decoder that narrowed the offset before checking it would decode happily.
    /// </summary>
    [Fact]
    public Task BigTiff_FirstDirectoryOffsetHasANonZeroHighWord()
    {
        byte[] file = ValidBigTiff().Build();
        long real = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(8));
        Assert.True(real > 0 && real < file.Length, $"the control file's first directory is at {real}.");
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(8), real | (1L << 32));
        return AssertInvalidContent(file);
    }

    /// <summary>
    /// A directory offset that leaves fewer than the eight bytes its entry count needs. Classic TIFF only
    /// needs two bytes there, so this is the case the offset-width-aware bound exists for: at these offsets a
    /// classic file would still have a readable count.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public Task BigTiff_EntryCountStraddlesTheEndOfTheFile(int bytesLeft)
    {
        byte[] file = ValidBigTiff().Build();
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(8), file.Length - bytesLeft);
        return AssertInvalidContent(file);
    }

    /// <summary>An 8-byte entry count no file could satisfy, including one that is negative as a signed value.</summary>
    [Theory]
    [InlineData(1_000_000UL)]
    [InlineData((ulong)long.MaxValue)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000_0000_0000_0000UL)]
    public Task BigTiff_DirectoryEntryCountIsAbsurd(ulong entryCount)
        => AssertInvalidContent(ValidBigTiff().Build(entryCount: entryCount), maxAllocatedBytes: 4_000_000);

    /// <summary>A LONG8 strip offset past the end of the file, inline in the widened value field.</summary>
    [Theory]
    [InlineData(100_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public Task BigTiff_Long8StripOffsetOutOfRange(long offset)
        => AssertInvalidContent(ValidBigTiff().Tag(273, 16, offset).Build());

    /// <summary>
    /// The LONG8 truncation trap for a segment offset: the low word is the real strip, the high word puts it
    /// over 4 GiB away. Reading only the low 32 bits would decode this file without a murmur.
    /// </summary>
    [Fact]
    public Task BigTiff_Long8StripOffsetHasANonZeroHighWord()
        => AssertInvalidContent(ValidBigTiff().Tag(273, 16, BigTiffBuilder.PayloadOffset | (1L << 32)).Build());

    /// <summary>A LONG8 value above long.MaxValue can never be narrowed to an index and must be rejected outright.</summary>
    [Theory]
    [InlineData(273)]
    [InlineData(279)]
    [InlineData(256)]
    public Task BigTiff_Long8ValueAboveLongMaxValue(int tag)
        => AssertInvalidContent(ValidBigTiff().RawEntry(tag, 16, 1, ulong.MaxValue).Build());

    /// <summary>
    /// A strip offset and byte count whose sum overflows a signed 64-bit range check. Today the sum wraps and
    /// the segment table's own bound lets it through, so what stops the decode is the slice that follows and
    /// the exception it raises is one <c>DecoderGuard</c> translates; the contract is met either way, which is
    /// what this case pins. A range check written as a subtraction would reject it by its own message instead.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue, 16L)]
    [InlineData(long.MaxValue - 8, long.MaxValue - 8)]
    [InlineData(9_223_372_036_854_775_800L, 32L)]
    public Task BigTiff_StripOffsetAndByteCountWrapTheRangeCheck(long offset, long byteCount)
        => AssertInvalidContent(ValidBigTiff().Tag(273, 16, offset).Tag(279, 16, byteCount).Build());

    /// <summary>An external LONG8 segment table whose block lies outside the file.</summary>
    [Theory]
    [InlineData(100_000UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0xFFFF_FFFF_0000_0010UL)]
    public Task BigTiff_ExternalSegmentTableOutsideTheFile(ulong blockOffset)
        => AssertInvalidContent(ValidBigTiff().RawEntry(273, 16, 6, blockOffset).Tag(278, 4, 1).Build());

    /// <summary>Tiled pages take the same 64-bit offsets, and their tables are external at any realistic size.</summary>
    [Theory]
    [InlineData(90_000L)]
    [InlineData(long.MaxValue)]
    public Task BigTiff_TileOffsetsOutOfRange(long offset)
        => AssertInvalidContent(ValidBigTiff().Remove(273).Remove(278)
            .Tag(322, 4, 16).Tag(323, 4, 16).Tag(324, 16, offset).Tag(325, 16, 256).Build());

    // The next three cases never reach the TIFF decoder. A version-43 file is recognized on its first eight
    // bytes - "II"/"MM", the version word, the offset size and the reserved word - so a header that fails any
    // of those is not TIFF at all and Load reports UnknownImageFormatException rather than a decoder message.
    // What matters is that the two layers agree: the four bytes the detector insists on are exactly the four
    // the decoder would have rejected, so no file can slip between them.

    /// <summary>The header's offset-size word: this container reads 8-byte offsets and nothing else.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(ushort.MaxValue)]
    public Task BigTiff_HeaderOffsetSizeIsNotEight(int offsetSize)
        => AssertNotRecognised(ValidBigTiff().Build(offsetSize: offsetSize));

    /// <summary>The header's reserved word must be zero; a writer using it means a container this is not.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(ushort.MaxValue)]
    public Task BigTiff_HeaderReservedWordIsNotZero(int reserved)
        => AssertNotRecognised(ValidBigTiff().Build(reserved: reserved));

    /// <summary>Only 42 and 43 are container versions; a 16-byte header is not implied by anything else.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    [InlineData(44)]
    [InlineData(ushort.MaxValue)]
    public Task BigTiff_UnknownContainerVersion(int version)
        => AssertNotRecognised(ValidBigTiff().Build(version: version));

    /// <summary>A version-43 header needs all sixteen of its bytes before the first directory offset is readable.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    public Task BigTiff_TruncatedHeader(int length) => AssertInvalidContent(ValidBigTiff().Build()[..length]);

    [Fact]
    public Task BigTiff_NextDirectoryOffsetPastEof()
        => AssertInvalidContent(ValidBigTiff().Build(nextIfdOffset: 100_000));

    /// <summary>A page whose 8-byte next-directory pointer points back at itself must still terminate.</summary>
    [Fact]
    public Task BigTiff_DirectoryChainCycleTerminates()
        => AssertTerminates(ValidBigTiff().Build(nextIfdOffset: -1), image => Assert.Single(image.Frames));

    /// <summary>The pixel-count limit applies whatever the offset width is, and before any buffer is taken.</summary>
    [Fact]
    public Task BigTiff_HugeDimensionsRejectedBeforeAllocation()
        => AssertRejected(
            ValidBigTiff().Tag(256, 4, 60_000).Tag(257, 4, 60_000).Build(),
            requiredType: typeof(ImageSizeLimitExceededException),
            maxAllocatedBytes: 1_000_000);

    /// <summary>
    /// 2,000 unknown entries and 500 duplicates of a real tag, every one of them a 32 KB LONG8 array: at
    /// twenty bytes an entry a BigTIFF directory is cheaper to inflate than a classic one, so none of it may
    /// be materialized.
    /// </summary>
    [Fact]
    public Task BigTiff_ManyDirectoryEntriesReferencingLargeArrays_DoesNotBalloonAllocation()
    {
        byte[] payload = new byte[32 * 1024];
        payload[0] = 0x10; // The first 16 bytes double as the 4x4 strip.
        BigTiffBuilder builder = ValidBigTiff(payload).Tag(279, 16, 16);
        for (int i = 0; i < 2000; i++)
        {
            builder.RawEntry(40_000 + i, 16, payload.Length / 8, BigTiffBuilder.PayloadOffset);
        }

        for (int i = 0; i < 500; i++)
        {
            builder.RawEntry(60_000 + i, 16, payload.Length / 8, BigTiffBuilder.PayloadOffset);
        }

        return AssertTerminates(builder.Build(), image => Assert.Equal(4, image.Width), maxAllocatedBytes: 4_000_000);
    }

    // =====================================================================================================
    // JPEG (the decoder is being hardened on a separate branch; these lock the target behaviour)
    // =====================================================================================================

    [Fact]
    public Task Jpeg_SosBeforeSof()
    {
        byte[] jpeg = BaselineJpeg();
        (int sofStart, int sofEnd) = FindSegment(jpeg, 0xC0);
        (int sosStart, int sosEnd) = FindSegment(jpeg, 0xDA);
        byte[] sof = jpeg[sofStart..sofEnd];
        byte[] rest = jpeg[sosStart..];
        return AssertRejected(Concat(jpeg[..sofStart], rest[..(sosEnd - sosStart)], sof, jpeg[sofEnd..sosStart], rest[(sosEnd - sosStart)..]));
    }

    [Fact]
    public Task Jpeg_DhtCountsSumOver256()
    {
        byte[] jpeg = BaselineJpeg();
        (int start, int end) = FindSegment(jpeg, 0xC4);
        byte[] counts = new byte[16];
        counts[7] = 255;
        counts[8] = 255; // 510 codes
        byte[] payload = Concat(new byte[] { 0x00 }, counts, new byte[510]);
        byte[] dht = Concat(new byte[] { 0xFF, 0xC4 }, U16((ushort)(payload.Length + 2)), payload);
        return AssertRejected(Concat(jpeg[..start], dht, jpeg[end..]));
    }

    [Fact]
    public Task Jpeg_DqtLengthShort()
    {
        byte[] jpeg = BaselineJpeg();
        (int start, _) = FindSegment(jpeg, 0xDB);
        jpeg[start + 2] = 0;
        jpeg[start + 3] = 3; // declares 1 payload byte for a 65-byte table
        return AssertRejected(jpeg);
    }

    [Fact]
    public Task Jpeg_EobBeforeAnyDc()
    {
        // Replace the entropy-coded data with all-ones bits: every symbol decodes to the longest code or fails.
        byte[] jpeg = BaselineJpeg();
        (_, int sosEnd) = FindSegment(jpeg, 0xDA);
        byte[] scan = Enumerable.Repeat((byte)0xFF, 64).Select((b, i) => i % 2 == 0 ? b : (byte)0x00).ToArray();
        return AssertRejected(Concat(jpeg[..sosEnd], scan, new byte[] { 0xFF, 0xD9 }));
    }

    [Fact]
    public Task Jpeg_HugeRestartInterval()
    {
        byte[] jpeg = BaselineJpeg();
        (int sofStart, _) = FindSegment(jpeg, 0xC0);
        byte[] dri = { 0xFF, 0xDD, 0x00, 0x04, 0xFF, 0xFF };
        return AssertTerminates(Concat(jpeg[..sofStart], dri, jpeg[sofStart..]), _ => { });
    }

    [Fact]
    public Task Jpeg_SofWithTwoComponents()
    {
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0E, 0x08, 0x00, 0x08, 0x00, 0x08, 0x02,
            0x01, 0x11, 0x00, 0x02, 0x11, 0x00,
            0xFF, 0xD9,
        };
        return AssertRejected(jpeg);
    }

    [Fact]
    public Task Jpeg_SamplingFactorZero()
    {
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x08, 0x00, 0x08, 0x03,
            0x01, 0x00, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
            0xFF, 0xD9,
        };
        return AssertRejected(jpeg);
    }

    [Fact]
    public Task Jpeg_ScanReferencesUnknownComponent()
    {
        byte[] jpeg = BaselineJpeg();
        (int sosStart, _) = FindSegment(jpeg, 0xDA);
        jpeg[sosStart + 5] = 9; // component selector
        return AssertRejected(jpeg);
    }

    [Fact]
    public Task Jpeg_TruncatedInsideScan()
    {
        byte[] jpeg = BaselineJpeg();
        (_, int sosEnd) = FindSegment(jpeg, 0xDA);
        return AssertTerminates(jpeg[..(sosEnd + 3)], _ => { });
    }

    // =====================================================================================================
    // Assertion helpers
    // =====================================================================================================

    /// <summary>
    /// Load and Identify must both finish within <see cref="Timeout"/>; Load must throw an
    /// <see cref="ImageFormatException"/> or <see cref="NotSupportedException"/> (optionally a specific one),
    /// and Identify may only fail through the same contract.
    /// </summary>
    private static async Task AssertRejected(
        byte[] data, Type? requiredType = null, long maxAllocatedBytes = long.MaxValue, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        (Exception? loadEx, long allocated) = await CaptureWithAllocationAsync(() => Image.Load<Rgba32>(data, options).Dispose());

        Assert.True(loadEx is not null, "Load accepted the crafted input instead of rejecting it.");
        Assert.True(
            loadEx is ImageFormatException or NotSupportedException,
            $"Load failed with {loadEx!.GetType().Name} instead of ImageFormatException/NotSupportedException: {loadEx.Message}");
        if (requiredType is not null)
        {
            Assert.True(loadEx.GetType() == requiredType, $"Load failed with {loadEx.GetType().Name}, expected {requiredType.Name}: {loadEx.Message}");
        }

        Assert.True(allocated <= maxAllocatedBytes, $"Rejecting the input allocated {allocated:N0} bytes.");

        Exception? identifyEx = await CaptureAsync(() => Image.Identify(data, options));
        Assert.True(
            identifyEx is null or ImageFormatException or NotSupportedException,
            $"Identify failed with {identifyEx?.GetType().Name}: {identifyEx?.Message}");
    }

    /// <summary>
    /// The tighter contract the deflate, APNG and BigTIFF sections hold to: the failure must be
    /// <see cref="InvalidImageContentException"/> itself, not merely something inside the documented set.
    /// </summary>
    private static Task AssertInvalidContent(byte[] data, long maxAllocatedBytes = long.MaxValue)
        => AssertRejected(data, requiredType: typeof(InvalidImageContentException), maxAllocatedBytes: maxAllocatedBytes);

    /// <summary>
    /// For inputs whose very signature is wrong: the format detector must refuse them before any decoder is
    /// chosen, so Load reports <see cref="UnknownImageFormatException"/> and no decoder message can appear.
    /// </summary>
    private static Task AssertNotRecognised(byte[] data)
        => AssertRejected(data, requiredType: typeof(UnknownImageFormatException));

    /// <summary>Load must finish in time and either succeed (then <paramref name="onSuccess"/> runs) or fail through the contract.</summary>
    private static async Task AssertTerminates(byte[] data, Action<Image<Rgba32>> onSuccess, long maxAllocatedBytes = long.MaxValue)
    {
        (Exception? ex, long allocated) = await CaptureWithAllocationAsync(() =>
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(data);
            onSuccess(image);
        });
        Assert.True(
            ex is null or ImageFormatException or NotSupportedException,
            $"Load failed with {ex?.GetType().Name}: {ex?.Message}");
        Assert.True(allocated <= maxAllocatedBytes, $"Decoding allocated {allocated:N0} bytes.");
    }

    private static async Task<Exception?> CaptureAsync(Action action) => (await CaptureWithAllocationAsync(action)).Exception;

    /// <summary>
    /// Runs <paramref name="action"/> on a worker thread with a timeout and reports the bytes that thread allocated
    /// (per-thread accounting keeps concurrently running test classes from skewing the number).
    /// </summary>
    private static async Task<(Exception? Exception, long AllocatedBytes)> CaptureWithAllocationAsync(Action action)
    {
        Task<(Exception?, long)> task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Exception? captured = null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            return (captured, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        try
        {
            return await task.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"The decoder did not finish within {Timeout.TotalSeconds:F0} s (potential infinite loop).");
            return (null, 0);
        }
    }

    // =====================================================================================================
    // Byte-level builders
    // =====================================================================================================

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Png(params byte[][] chunks) => Concat(new[] { PngSignature.ToArray() }.Concat(chunks).ToArray());

    private static byte[] Chunk(string type, byte[] data) => ChunkWithLength(type, data, data.Length);

    private static byte[] ChunkWithLength(string type, byte[] data, int declaredLength)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, declaredLength);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Append(0, chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static byte[] FixCrc(byte[] chunk)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(chunk);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + length), Crc32.Append(0, chunk.AsSpan(4, 4 + length)));
        return chunk;
    }

    private static byte[] Ihdr(int width, int height, byte depth, byte colorType, byte interlace = 0)
    {
        byte[] payload = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(payload, width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), height);
        payload[8] = depth;
        payload[9] = colorType;
        payload[12] = interlace;
        return Chunk("IHDR", payload);
    }

    private static byte[] RawScanlines(int width, int height, int bytesPerPixel)
    {
        byte[] raw = new byte[(1 + (width * bytesPerPixel)) * height];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % (1 + (width * bytesPerPixel)) == 0 ? 0 : (i * 7) & 0xFF);
        }

        return raw;
    }

    private static byte[] Idat(int width, int height, int bytesPerPixel) => Chunk("IDAT", Zlib(RawScanlines(width, height, bytesPerPixel)));

    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        return ms.ToArray();
    }

    /// <summary>Wraps a hand-written DEFLATE stream in a zlib container over the data it should have produced.</summary>
    private static byte[] Zlib(DeflateWriter deflate, byte[] inflated)
    {
        byte[] body = deflate.ToArray();
        byte[] stream = new byte[2 + body.Length + 4];
        stream[0] = 0x78; // CM 8 (DEFLATE), CINFO 7 (32 KiB window).
        stream[1] = 0x01; // FLEVEL 0, no preset dictionary, check bits chosen so (CMF * 256 + FLG) % 31 == 0.
        body.CopyTo(stream, 2);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(2 + body.Length), Adler32(inflated));
        return stream;
    }

    /// <summary>A 4x4 truecolor PNG whose single IDAT chunk carries the given hand-written DEFLATE stream.</summary>
    private static byte[] DeflatePng(DeflateWriter deflate) => Png(Ihdr(4, 4, 8, 2), Chunk("IDAT", Zlib(deflate, Array.Empty<byte>())));

    /// <summary>Opens a final dynamic-Huffman block with the given HLIT and HDIST fields and an HCLEN of 19.</summary>
    private static DeflateWriter DynamicHeader(int hlit, int hdist)
        => new DeflateWriter().Field(1, 1).Field(2, 2).Field(hlit, 5).Field(hdist, 5).Field(15, 4);

    /// <summary>
    /// Writes the nineteen 3-bit code lengths of a dynamic block's code-length alphabet, in the permuted
    /// order the format stores them in. Symbols absent from <paramref name="lengths"/> get a length of zero.
    /// </summary>
    private static DeflateWriter CodeLengthCode(DeflateWriter writer, Dictionary<int, int> lengths)
    {
        foreach (int symbol in new[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 })
        {
            writer.Field(lengths.TryGetValue(symbol, out int length) ? length : 0, 3);
        }

        return writer;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>An acTL (animation control) chunk: the declared frame count and play count.</summary>
    private static byte[] Actl(uint frames, uint plays)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, frames);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), plays);
        return Chunk("acTL", payload);
    }

    /// <summary>
    /// An fcTL (frame control) chunk: the shared sequence number, the frame's rectangle, its delay and its
    /// dispose and blend operations. Every rectangle field is unsigned on the wire, so all four take uints.
    /// </summary>
    private static byte[] Fctl(
        uint sequence, uint width, uint height, uint xOffset = 0, uint yOffset = 0, byte dispose = 0, byte blend = 0)
    {
        byte[] payload = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), width);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), height);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12), xOffset);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), yOffset);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(20), 1);  // Delay numerator.
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(22), 10); // Delay denominator.
        payload[24] = dispose;
        payload[25] = blend;
        return Chunk("fcTL", payload);
    }

    /// <summary>An fdAT (frame data) chunk: the shared sequence number followed by the frame's zlib stream.</summary>
    private static byte[] Fdat(uint sequence, byte[] compressed)
    {
        byte[] payload = new byte[4 + compressed.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sequence);
        compressed.CopyTo(payload, 4);
        return Chunk("fdAT", payload);
    }

    /// <summary>
    /// Writes a DEFLATE bit stream by hand. Plain fields go in least significant bit first; Huffman codes go
    /// in most significant bit of the code first, which is the one asymmetry the format has.
    /// </summary>
    private sealed class DeflateWriter
    {
        private readonly List<byte> bytes = new();
        private int accumulator;
        private int bitCount;

        /// <summary>Writes a plain field of <paramref name="bits"/> bits, least significant bit first.</summary>
        public DeflateWriter Field(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                this.WriteBit((value >> i) & 1);
            }

            return this;
        }

        /// <summary>Writes a Huffman code of <paramref name="bits"/> bits, most significant bit of the code first.</summary>
        public DeflateWriter Code(int code, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                this.WriteBit((code >> i) & 1);
            }

            return this;
        }

        /// <summary>Writes one fixed-Huffman literal: eight bits below 144, nine above.</summary>
        public DeflateWriter Literal(int value)
            => value < 144 ? this.Code(0x30 + value, 8) : this.Code(0x190 + value - 144, 9);

        /// <summary>Pads with zero bits up to the next byte boundary, as a stored block's header requires.</summary>
        public DeflateWriter Align()
        {
            while (this.bitCount != 0)
            {
                this.WriteBit(0);
            }

            return this;
        }

        /// <summary>Appends whole bytes, which is only meaningful on a byte boundary.</summary>
        public DeflateWriter Bytes(byte[] raw)
        {
            Assert.Equal(0, this.bitCount);
            this.bytes.AddRange(raw);
            return this;
        }

        /// <summary>The stream so far, zero-padded to the next byte boundary.</summary>
        public byte[] ToArray()
        {
            var copy = new List<byte>(this.bytes);
            if (this.bitCount != 0)
            {
                copy.Add((byte)this.accumulator);
            }

            return copy.ToArray();
        }

        private void WriteBit(int bit)
        {
            this.accumulator |= bit << this.bitCount;
            if (++this.bitCount == 8)
            {
                this.bytes.Add((byte)this.accumulator);
                this.accumulator = 0;
                this.bitCount = 0;
            }
        }
    }

    private static byte[] Bmp(
        int width, int height, ushort bpp, int compression, byte[] pixels, byte[]? palette = null, int? dataOffset = null,
        int headerSize = 40, int colorsUsed = 0)
    {
        palette ??= Array.Empty<byte>();
        int dib = Math.Max(headerSize, 40);
        if (headerSize > 1000)
        {
            dib = 40; // Only the declared size is bogus; do not actually allocate it.
        }

        byte[] file = new byte[14 + dib + palette.Length + pixels.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), dataOffset ?? 14 + dib + palette.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), bpp);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(30), compression);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(34), pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(46), colorsUsed);
        palette.CopyTo(file, 14 + dib);
        pixels.CopyTo(file, 14 + dib + palette.Length);
        return file;
    }

    /// <summary>A valid 4x4 8-bit grayscale little-endian TIFF whose tags can be overridden.</summary>
    private static TiffBuilder ValidTiff(byte[]? strip = null)
    {
        strip ??= Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray();
        return new TiffBuilder(strip)
            .Tag(256, 4, 4)
            .Tag(257, 4, 4)
            .Tag(258, 3, 8)
            .Tag(259, 3, 1)
            .Tag(262, 3, 1)
            .Tag(273, 4, TiffBuilder.PayloadOffset)
            .Tag(277, 3, 1)
            .Tag(278, 4, 4)
            .Tag(279, 4, strip.Length);
    }

    private sealed class TiffBuilder
    {
        public const int PayloadOffset = 8;

        private readonly List<(int Tag, int Type, long[] Values, (long Count, int Offset)? Raw)> entries = new();
        private readonly byte[] payload;

        public TiffBuilder(byte[]? payload = null) => this.payload = payload ?? Array.Empty<byte>();

        public TiffBuilder Tag(int tag, int type, params long[] values)
        {
            this.entries.RemoveAll(e => e.Tag == tag);
            this.entries.Add((tag, type, values, null));
            return this;
        }

        /// <summary>Adds an entry (possibly a duplicate) whose value field is a raw count/offset pair.</summary>
        public TiffBuilder RawEntry(int tag, int type, long count, int offset)
        {
            this.entries.Add((tag, type, Array.Empty<long>(), (count, offset)));
            return this;
        }

        public TiffBuilder Remove(int tag)
        {
            this.entries.RemoveAll(e => e.Tag == tag);
            return this;
        }

        public byte[] Build(int? firstIfdOffset = null, int nextIfdOffset = 0)
        {
            var ordered = this.entries.OrderBy(e => e.Tag).ToList();
            var file = new MemoryStream();
            file.Write(new byte[] { (byte)'I', (byte)'I', 42, 0 });
            int ifdOffset = PayloadOffset + this.payload.Length;
            ifdOffset += ifdOffset & 1;
            file.Write(U32LE(firstIfdOffset ?? ifdOffset));
            file.Write(this.payload);
            while (file.Length < ifdOffset)
            {
                file.WriteByte(0);
            }

            int externalBase = ifdOffset + 2 + (ordered.Count * 12) + 4;
            var external = new MemoryStream();
            file.Write(U16LE((ushort)ordered.Count));
            foreach ((int tag, int type, long[] values, (long Count, int Offset)? raw) in ordered)
            {
                if (raw is { } r)
                {
                    file.Write(U16LE((ushort)tag));
                    file.Write(U16LE((ushort)type));
                    file.Write(U32LE((int)r.Count));
                    file.Write(U32LE(r.Offset));
                    continue;
                }

                int size = type is 3 ? 2 : type is 1 or 2 ? 1 : 4;
                byte[] packed = new byte[size * values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    switch (size)
                    {
                        case 1:
                            packed[i] = (byte)values[i];
                            break;
                        case 2:
                            BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(i * 2), (ushort)values[i]);
                            break;
                        default:
                            BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(i * 4), (uint)values[i]);
                            break;
                    }
                }

                file.Write(U16LE((ushort)tag));
                file.Write(U16LE((ushort)type));
                file.Write(U32LE(values.Length));
                if (packed.Length <= 4)
                {
                    file.Write(packed);
                    file.Write(new byte[4 - packed.Length]);
                }
                else
                {
                    if (external.Length % 2 == 1)
                    {
                        external.WriteByte(0);
                    }

                    file.Write(U32LE(externalBase + (int)external.Length));
                    external.Write(packed);
                }
            }

            file.Write(U32LE(nextIfdOffset == -1 ? ifdOffset : nextIfdOffset));
            file.Write(external.ToArray());
            return file.ToArray();
        }
    }

    /// <summary>A valid 4x4 8-bit grayscale little-endian BigTIFF whose header and tags can be overridden.</summary>
    private static BigTiffBuilder ValidBigTiff(byte[]? strip = null)
    {
        strip ??= Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray();
        return new BigTiffBuilder(strip)
            .Tag(256, 4, 4)                                  // ImageWidth, as a classic LONG inside a BigTIFF.
            .Tag(257, 4, 4)                                  // ImageLength.
            .Tag(258, 3, 8)                                  // BitsPerSample.
            .Tag(259, 3, 1)                                  // Compression: none.
            .Tag(262, 3, 1)                                  // PhotometricInterpretation: BlackIsZero.
            .Tag(273, 16, BigTiffBuilder.PayloadOffset)      // StripOffsets, as a LONG8.
            .Tag(277, 3, 1)                                  // SamplesPerPixel.
            .Tag(278, 4, 4)                                  // RowsPerStrip.
            .Tag(279, 16, strip.Length);                     // StripByteCounts, as a LONG8.
    }

    /// <summary>
    /// Assembles a little-endian BigTIFF: a 16-byte header, an 8-byte directory entry count, 20-byte entries
    /// and 8-byte value fields. Every header field and the entry count itself can be given a value no writer
    /// would produce, which is what the rejection tests need.
    /// </summary>
    private sealed class BigTiffBuilder
    {
        /// <summary>The payload starts straight after the 16-byte header.</summary>
        public const int PayloadOffset = 16;

        /// <summary>A BigTIFF value field holds eight bytes before it has to point elsewhere.</summary>
        private const int InlineMax = 8;

        private readonly List<Entry> entries = new();
        private readonly byte[] payload;

        public BigTiffBuilder(byte[]? payload = null) => this.payload = payload ?? Array.Empty<byte>();

        public BigTiffBuilder Tag(int tag, int type, params long[] values)
        {
            this.entries.RemoveAll(e => e.Tag == tag);
            this.entries.Add(new Entry(tag, type, values.Length, values, null));
            return this;
        }

        /// <summary>
        /// Adds an entry whose element count and 8-byte value field are written verbatim, for the offsets and
        /// counts that no <see cref="long"/> can express.
        /// </summary>
        public BigTiffBuilder RawEntry(int tag, int type, long count, ulong valueField)
        {
            this.entries.RemoveAll(e => e.Tag == tag);
            this.entries.Add(new Entry(tag, type, count, null, valueField));
            return this;
        }

        public BigTiffBuilder Remove(int tag)
        {
            this.entries.RemoveAll(e => e.Tag == tag);
            return this;
        }

        /// <summary>Writes the file. Passing -1 as <paramref name="nextIfdOffset"/> points the page at itself.</summary>
        public byte[] Build(
            long? firstIfdOffset = null,
            long nextIfdOffset = 0,
            int version = 43,
            int offsetSize = 8,
            int reserved = 0,
            ulong? entryCount = null)
        {
            List<Entry> ordered = this.entries.OrderBy(e => e.Tag).ToList();
            var file = new MemoryStream();
            file.Write(new byte[] { (byte)'I', (byte)'I' });
            file.Write(U16LE((ushort)version));
            file.Write(U16LE((ushort)offsetSize));
            file.Write(U16LE((ushort)reserved));

            int ifdOffset = PayloadOffset + this.payload.Length;
            ifdOffset += ifdOffset & 1;
            file.Write(U64LE((ulong)(firstIfdOffset ?? ifdOffset)));
            file.Write(this.payload);
            while (file.Length < ifdOffset)
            {
                file.WriteByte(0);
            }

            int externalBase = ifdOffset + 8 + (ordered.Count * 20) + 8;
            var external = new MemoryStream();
            file.Write(U64LE(entryCount ?? (ulong)ordered.Count));
            foreach (Entry entry in ordered)
            {
                file.Write(U16LE((ushort)entry.Tag));
                file.Write(U16LE((ushort)entry.Type));
                file.Write(U64LE((ulong)entry.Count));
                if (entry.RawValueField is { } raw)
                {
                    file.Write(U64LE(raw));
                    continue;
                }

                int size = entry.Type switch
                {
                    1 or 2 or 6 or 7 => 1,
                    3 or 8 => 2,
                    4 or 9 or 11 or 13 => 4,
                    _ => 8,
                };

                long[] values = entry.Values!;
                byte[] packed = new byte[size * values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    switch (size)
                    {
                        case 1:
                            packed[i] = (byte)values[i];
                            break;
                        case 2:
                            BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(i * 2), (ushort)values[i]);
                            break;
                        case 4:
                            BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(i * 4), (uint)values[i]);
                            break;
                        default:
                            BinaryPrimitives.WriteInt64LittleEndian(packed.AsSpan(i * 8), values[i]);
                            break;
                    }
                }

                if (packed.Length <= InlineMax)
                {
                    file.Write(packed);
                    file.Write(new byte[InlineMax - packed.Length]);
                }
                else
                {
                    while (external.Length % 8 != 0)
                    {
                        external.WriteByte(0);
                    }

                    file.Write(U64LE((ulong)(externalBase + external.Length)));
                    external.Write(packed);
                }
            }

            file.Write(U64LE((ulong)(nextIfdOffset == -1 ? ifdOffset : nextIfdOffset)));
            file.Write(external.ToArray());
            return file.ToArray();
        }

        /// <summary>One directory entry: either a value list to pack, or a verbatim count and value field.</summary>
        private sealed record Entry(int Tag, int Type, long Count, long[]? Values, ulong? RawValueField);
    }

    private static byte[] BaselineJpeg()
    {
        using var image = new Image<L8>(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                image[x, y] = new L8((byte)(x * 16));
            }
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, 90);
        return ms.ToArray();
    }

    /// <summary>Returns [start, end) of the first segment with the given marker (start points at 0xFF).</summary>
    private static (int Start, int End) FindSegment(byte[] jpeg, byte marker)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                throw new InvalidOperationException("Lost marker sync.");
            }

            byte m = jpeg[pos + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (m == marker)
            {
                return (pos, pos + 2 + length);
            }

            pos += 2 + length;
        }

        throw new InvalidOperationException($"Marker 0x{marker:X2} not found.");
    }

    private static byte[] U16(ushort value)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return b;
    }

    private static byte[] U16LE(ushort value)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return b;
    }

    private static byte[] U32LE(int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] U64LE(ulong value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
