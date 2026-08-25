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
