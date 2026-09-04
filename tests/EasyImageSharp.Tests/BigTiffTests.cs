using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// BigTIFF (version 43) container coverage: the 16-byte header, the 8-byte directory entry count, the 20-byte
/// entries, the 8-byte value fields and the LONG8/SLONG8/IFD8 field types, in both byte orders.
/// </summary>
/// <remarks>
/// <para>
/// The strongest check available is the <em>twin</em>: every hand-assembled BigTIFF fixture under
/// <c>Fixtures/tiff/</c> has a classic-TIFF sibling built from the same raster by the same generator and named
/// in the manifest's <c>twin</c> field. Asserting the pair decodes pixel-identically isolates the container
/// change from every other decoder behaviour, so a regression in the IFD layer cannot hide behind a shared
/// pixel path. The ground truth for both halves is the Pillow-written <c>.rgba</c> dump that
/// <see cref="FixtureDecodeTests"/> already compares against; nothing here is derived from this library.
/// </para>
/// <para>
/// The remaining tests name the individual container facts the twin comparison would only catch indirectly:
/// which field type a fixture actually uses, whether a value sits in the widened value field or in an external
/// block, and that the high word of a LONG8 offset is read rather than truncated away.
/// </para>
/// </remarks>
public class BigTiffTests
{
    /// <summary>The BigTIFF fixtures that have a classic twin, driven off the manifest so a new pair is picked up.</summary>
    public static IEnumerable<object[]> Pairs
        => ContainerManifest.BigTiffEntries().Select(e => new object[] { e.Name });

    /// <summary>The BigTIFF arms of the advanced corpus, which are strip- and tile-compressed 40x28 pages.</summary>
    public static IEnumerable<object[]> AdvancedArms => new[]
    {
        new object[] { "bigtiff_none", (int)TiffCompressionMethod.None, false, ByteOrder.LittleEndian },
        new object[] { "bigtiff_deflate", (int)TiffCompressionMethod.Deflate, false, ByteOrder.LittleEndian },
        new object[] { "bigtiff_lzw", (int)TiffCompressionMethod.Lzw, false, ByteOrder.LittleEndian },
        new object[] { "bigtiff_packbits", (int)TiffCompressionMethod.PackBits, false, ByteOrder.LittleEndian },
        new object[] { "bigtiff_mm_tiled", (int)TiffCompressionMethod.Deflate, true, ByteOrder.BigEndian },
    };

    // =====================================================================================================
    // The corpus itself
    // =====================================================================================================

    /// <summary>
    /// The pairing is the whole basis of this file, so it is checked before anything relies on it: a
    /// half-generated corpus must fail loudly here rather than quietly shrink every theory below.
    /// </summary>
    [Fact]
    public void Corpus_PairsEveryBigTiffFixtureWithAClassicTwin()
    {
        ContainerEntry[] bigTiff = ContainerManifest.BigTiffEntries().ToArray();
        Assert.True(
            bigTiff.Length >= 7,
            $"Only {bigTiff.Length} paired BigTIFF fixture(s) are listed in tiff/manifest.json; run Fixtures/generate.py.");

        foreach (ContainerEntry entry in bigTiff)
        {
            ContainerEntry twin = ContainerManifest.Get(entry.Twin);
            Assert.True(twin.Container == "classic", $"{entry.Name}: twin '{twin.Name}' is not a classic-TIFF fixture.");
            Assert.True(twin.Twin == entry.Name, $"{entry.Name}: twin '{twin.Name}' points back at '{twin.Twin}'.");
            Assert.True(
                twin.Width == entry.Width && twin.Height == entry.Height && twin.Frames == entry.Frames,
                $"{entry.Name}: the twin is {twin.Width}x{twin.Height} x{twin.Frames}, not {entry.Width}x{entry.Height} x{entry.Frames}.");
            Assert.True(twin.ByteOrder == entry.ByteOrder, $"{entry.Name}: the twin's byte order is '{twin.ByteOrder}', not '{entry.ByteOrder}'.");

            // A "BigTIFF" fixture that is not actually version 43 would make every assertion below vacuous.
            Assert.True(IsBigTiff(FixturePath.Read($"tiff/{entry.File}")), $"{entry.Name}: the file does not carry a version-43 header.");
            Assert.False(IsBigTiff(FixturePath.Read($"tiff/{twin.File}")), $"{twin.Name}: the file does not carry a version-42 header.");
        }
    }

    // =====================================================================================================
    // Cross-container equivalence
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_DecodesPixelIdentically(string name)
    {
        ContainerEntry entry = ContainerManifest.Get(name);
        using Image<Rgba32> big = Image.Load<Rgba32>(FixturePath.Read($"tiff/{entry.File}"));
        using Image<Rgba32> classic = Image.Load<Rgba32>(FixturePath.Read($"tiff/{ContainerManifest.Get(entry.Twin).File}"));

        Assert.True(big.Frames.Count == classic.Frames.Count, $"{name}: {big.Frames.Count} frame(s) against the twin's {classic.Frames.Count}.");
        for (int f = 0; f < big.Frames.Count; f++)
        {
            ImageFrame<Rgba32> bigFrame = big.Frames[f];
            ImageFrame<Rgba32> classicFrame = classic.Frames[f];
            Assert.True(
                bigFrame.Width == classicFrame.Width && bigFrame.Height == classicFrame.Height,
                $"{name} frame {f}: {bigFrame.Width}x{bigFrame.Height} against the twin's {classicFrame.Width}x{classicFrame.Height}.");

            ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(bigFrame.PixelSpan);
            ReadOnlySpan<byte> want = MemoryMarshal.AsBytes(classicFrame.PixelSpan);
            if (!got.SequenceEqual(want))
            {
                int i = got.CommonPrefixLength(want) / 4;
                int x = i % bigFrame.Width;
                int y = i / bigFrame.Width;
                Assert.Fail(
                    $"{name} frame {f}: first mismatch at pixel #{i} ({x},{y}): BigTIFF decoded {bigFrame[x, y]}, "
                    + $"the classic twin decoded {classicFrame[x, y]}. [{entry.Notes}]");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_IdentifyReportsTheSameImage(string name)
    {
        ContainerEntry entry = ContainerManifest.Get(name);
        ImageInfo big = Image.Identify(FixturePath.Read($"tiff/{entry.File}"));
        ImageInfo classic = Image.Identify(FixturePath.Read($"tiff/{ContainerManifest.Get(entry.Twin).File}"));

        Assert.Equal(ImageFormat.Tiff, big.Format);
        Assert.Equal(classic.Width, big.Width);
        Assert.Equal(classic.Height, big.Height);
        Assert.Equal(classic.FrameCount, big.FrameCount);
        Assert.Equal(classic.PixelType.BitsPerPixel, big.PixelType.BitsPerPixel);
        Assert.True(big.Width == entry.Width && big.Height == entry.Height, $"{name}: Identify disagrees with the manifest.");
        Assert.Equal(entry.Frames, big.FrameCount);
    }

    /// <summary>The container version and the byte order are independent, and both must be reported.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_ReportsTheContainerVersionAndByteOrder(string name)
    {
        ContainerEntry entry = ContainerManifest.Get(name);
        ByteOrder expectedOrder = entry.ByteOrder == "MM" ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
        byte[] bigBytes = FixturePath.Read($"tiff/{entry.File}");
        byte[] classicBytes = FixturePath.Read($"tiff/{ContainerManifest.Get(entry.Twin).File}");

        using Image<Rgba32> big = Image.Load<Rgba32>(bigBytes);
        TiffMetadata bigTiff = big.Metadata.GetTiffMetadata();
        Assert.True(bigTiff.BigTiff, $"{name}: TiffMetadata.BigTiff is false for a version-43 file.");
        Assert.Equal(expectedOrder, bigTiff.ByteOrder);

        using Image<Rgba32> classic = Image.Load<Rgba32>(classicBytes);
        TiffMetadata classicTiff = classic.Metadata.GetTiffMetadata();
        Assert.False(classicTiff.BigTiff, $"{entry.Twin}: TiffMetadata.BigTiff is true for a version-42 file.");
        Assert.Equal(expectedOrder, classicTiff.ByteOrder);

        // Identify must reach the same conclusion without decoding a single pixel.
        Assert.True(Image.Identify(bigBytes).Metadata.GetTiffMetadata().BigTiff, $"{name}: Identify does not report BigTiff.");
        Assert.False(Image.Identify(classicBytes).Metadata.GetTiffMetadata().BigTiff, $"{entry.Twin}: Identify reports BigTiff.");
    }

    /// <summary>Widening the directory reader must not change how a page describes itself.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pair_CarriesTheSameFrameMetadata(string name)
    {
        ContainerEntry entry = ContainerManifest.Get(name);
        using Image<Rgba32> big = Image.Load<Rgba32>(FixturePath.Read($"tiff/{entry.File}"));
        using Image<Rgba32> classic = Image.Load<Rgba32>(FixturePath.Read($"tiff/{ContainerManifest.Get(entry.Twin).File}"));

        for (int f = 0; f < big.Frames.Count; f++)
        {
            TiffFrameMetadata got = big.Frames[f].Metadata.GetTiffMetadata();
            TiffFrameMetadata want = classic.Frames[f].Metadata.GetTiffMetadata();
            Assert.True(
                got.BitsPerSample is null
                    ? want.BitsPerSample is null
                    : want.BitsPerSample is not null && got.BitsPerSample.SequenceEqual(want.BitsPerSample),
                $"{name} frame {f}: BitsPerSample differs from the twin's.");
            Assert.Equal(want.SamplesPerPixel, got.SamplesPerPixel);
            Assert.Equal(want.Compression, got.Compression);
            Assert.Equal(want.PhotometricInterpretation, got.PhotometricInterpretation);
            Assert.Equal(want.Predictor, got.Predictor);
            Assert.Equal(want.PlanarConfiguration, got.PlanarConfiguration);
            Assert.Equal(want.RowsPerStrip, got.RowsPerStrip);
            Assert.Equal(want.Tiled, got.Tiled);
        }
    }

    // =====================================================================================================
    // The individual container facts
    // =====================================================================================================

    /// <summary>
    /// The strip and tile tables of these fixtures are LONG8 arrays too large for the 8-byte value field, so
    /// they are read from an external block. The assertion is on the file's own bytes: if a regenerated fixture
    /// ever stored them inline or as classic LONG, the pair test above would still pass while covering nothing.
    /// </summary>
    [Theory]
    [InlineData("bigtiff_long8_offsets", 273, 6)]
    [InlineData("bigtiff_long8_offsets", 279, 6)]
    [InlineData("bigtiff_tiled", 324, 2)]
    [InlineData("bigtiff_tiled", 325, 2)]
    public void BigTiff_SegmentTableIsAnExternalLong8Block(string name, int tag, int count)
    {
        DirectoryEntry entry = FirstDirectoryEntry(FixturePath.Read($"tiff/{name}.tif"), tag);
        Assert.True(entry.Type == 16, $"{name}: tag {tag} has type {entry.Type}, not LONG8 (16).");
        Assert.Equal(count, entry.Count);
        Assert.False(entry.Inline, $"{name}: tag {tag} is {count} LONG8s and cannot fit the 8-byte value field.");
    }

    /// <summary>A single LONG8 offset is exactly eight bytes and therefore lives inside the widened value field.</summary>
    [Theory]
    [InlineData("bigtiff_le_rgb", 273)]
    [InlineData("bigtiff_le_rgb", 279)]
    [InlineData("bigtiff_be_rgb", 273)]
    [InlineData("bigtiff_multipage", 273)]
    public void BigTiff_SingleLong8OffsetSitsInTheValueField(string name, int tag)
    {
        DirectoryEntry entry = FirstDirectoryEntry(FixturePath.Read($"tiff/{name}.tif"), tag);
        Assert.True(entry.Type == 16, $"{name}: tag {tag} has type {entry.Type}, not LONG8 (16).");
        Assert.Equal(1L, entry.Count);
        Assert.True(entry.Inline, $"{name}: tag {tag} should be read straight out of the 8-byte value field.");
    }

    /// <summary>
    /// Values exactly eight bytes wide - four SHORTs, one RATIONAL - move into the value field in BigTIFF while
    /// the classic twin has to spill them into an external block. Both must read back the same.
    /// </summary>
    [Fact]
    public void BigTiff_EightByteValuesMoveIntoTheValueField()
    {
        byte[] big = FixturePath.Read("tiff/bigtiff_inline8.tif");
        byte[] classic = FixturePath.Read("tiff/classic_inline8.tif");

        foreach (int tag in new[] { 258, 282, 283 })
        {
            Assert.True(FirstDirectoryEntry(big, tag).Inline, $"BigTIFF tag {tag} should sit in the 8-byte value field.");
            Assert.False(FirstDirectoryEntry(classic, tag).Inline, $"Classic tag {tag} cannot fit a 4-byte value field.");
        }

        using Image<Rgba32> image = Image.Load<Rgba32>(big);
        Assert.Equal(new ushort[] { 8, 8, 8, 8 }, image.Frames.RootFrame.Metadata.GetTiffMetadata().BitsPerSample);
    }

    /// <summary>
    /// A BigTIFF may keep using classic LONG (type 4) for its offsets: the container widens the fields a writer
    /// is allowed to use, it does not mandate them. Retyping the fixture's two LONG8 entries in place is the
    /// exact test, because everything else in the file, the raster included, stays byte for byte where it was.
    /// </summary>
    [Theory]
    [InlineData("bigtiff_le_rgb")]
    [InlineData("bigtiff_be_rgb")]
    public void BigTiff_ClassicLongOffsetsDecodeIdentically(string name)
    {
        byte[] original = FixturePath.Read($"tiff/{name}.tif");
        byte[] retyped = RetypeInlineLong8AsLong(RetypeInlineLong8AsLong(original, 273), 279);
        Assert.NotEqual(original, retyped);
        Assert.Equal(4, FirstDirectoryEntry(retyped, 273).Type);

        using Image<Rgba32> want = Image.Load<Rgba32>(original);
        using Image<Rgba32> got = Image.Load<Rgba32>(retyped);
        Assert.Equal(want.Width, got.Width);
        Assert.True(
            MemoryMarshal.AsBytes(got.Frames.RootFrame.PixelSpan).SequenceEqual(MemoryMarshal.AsBytes(want.Frames.RootFrame.PixelSpan)),
            $"{name}: LONG strip offsets inside a BigTIFF decoded differently from the LONG8 original.");
        Assert.True(got.Metadata.GetTiffMetadata().BigTiff, $"{name}: retyping the offsets changed the container version.");
    }

    /// <summary>
    /// The LONG8 regression, named by its cause. The malformed fixture differs from the well-formed one in a
    /// single byte, and that byte is in the <em>high</em> half of the strip offset's 8-byte value field. A
    /// decoder that dispatched its value loop on element size rather than on element type would read only the
    /// low 32 bits, find the strip exactly where the good file keeps it, and decode this file without a murmur.
    /// </summary>
    [Fact]
    public void BigTiff_HighWordOfALong8OffsetIsNotTruncatedAway()
    {
        byte[] wellFormed = FixturePath.Read("tiff/bigtiff_le_rgb.tif");
        byte[] malformed = FixturePath.Read("tiff/bigtiff_long8_offset_past_eof.tif");
        Assert.Equal(wellFormed.Length, malformed.Length);

        int valueField = FirstDirectoryEntry(wellFormed, 273).ValueFieldOffset;
        int[] differing = Enumerable.Range(0, wellFormed.Length).Where(i => wellFormed[i] != malformed[i]).ToArray();
        Assert.True(
            differing.Length == 1 && differing[0] >= valueField + 4 && differing[0] < valueField + 8,
            $"The fixtures differ in {differing.Length} byte(s) at [{string.Join(", ", differing)}]; exactly one difference "
            + $"was expected, in the high half of the strip offset's value field at {valueField + 4}..{valueField + 7}.");

        using (Image<Rgba32> image = Image.Load<Rgba32>(wellFormed))
        {
            Assert.Equal(24, image.Width);
        }

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(malformed).Dispose());
    }

    /// <summary>
    /// The EXIF sub-directory hangs off tag 34665 as IFD8, and the ICC and XMP blocks beside it are reached
    /// through the same widened directory reader. Equality with the classic twin is the only check that catches
    /// the second IFD parser being left classic-only, which decodes the pixels correctly and the profiles not.
    /// </summary>
    [Fact]
    public void BigTiff_ExifIccAndXmpMatchTheClassicTwin()
    {
        Assert.Equal(18, FirstDirectoryEntry(FixturePath.Read("tiff/bigtiff_exif.tif"), 34665).Type);
        Assert.Equal(4, FirstDirectoryEntry(FixturePath.Read("tiff/classic_exif.tif"), 34665).Type);

        using Image<Rgba32> big = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_exif.tif"));
        using Image<Rgba32> classic = Image.Load<Rgba32>(FixturePath.Read("tiff/classic_exif.tif"));

        Assert.NotNull(classic.Metadata.ExifProfile);
        Assert.NotNull(big.Metadata.ExifProfile);
        Assert.Equal(classic.Metadata.ExifProfile!.ToByteArray(), big.Metadata.ExifProfile!.ToByteArray());

        Assert.NotNull(classic.Metadata.IccProfile);
        Assert.NotNull(big.Metadata.IccProfile);
        Assert.Equal(classic.Metadata.IccProfile!.ToByteArray(), big.Metadata.IccProfile!.ToByteArray());

        Assert.NotNull(classic.Metadata.XmpProfile);
        Assert.NotNull(big.Metadata.XmpProfile);
        Assert.Equal(classic.Metadata.XmpProfile!.ToByteArray(), big.Metadata.XmpProfile!.ToByteArray());
    }

    /// <summary>Two pages of different sizes chained through 8-byte next-directory pointers.</summary>
    [Fact]
    public void BigTiff_MultipageChainsThroughEightByteNextPointers()
    {
        byte[] bytes = FixturePath.Read("tiff/bigtiff_multipage.tif");
        Assert.Equal(2, Image.Identify(bytes).FrameCount);

        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.Equal(2, image.Frames.Count);
        Assert.True(image.Frames[0].Width == 12 && image.Frames[0].Height == 9, "page 0 is not 12x9.");
        Assert.True(image.Frames[1].Width == 10 && image.Frames[1].Height == 7, "page 1 is not 10x7.");

        // The second directory really is reached through the first one's 8-byte pointer, and it ends the chain.
        long next = NextDirectoryOffset(bytes, FirstDirectoryOffset(bytes));
        Assert.True(next > 0 && next < bytes.Length, $"The first page's next-directory pointer is {next}.");
        Assert.Equal(0L, NextDirectoryOffset(bytes, (int)next));
    }

    [Fact]
    public void BigTiff_TiledPageIsReportedAsTiled()
    {
        using Image<Rgba32> tiled = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_tiled.tif"));
        TiffFrameMetadata tiledFrame = tiled.Frames.RootFrame.Metadata.GetTiffMetadata();
        Assert.True(tiledFrame.Tiled, "bigtiff_tiled is not reported as tiled.");
        Assert.Null(tiledFrame.RowsPerStrip);

        using Image<Rgba32> striped = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_le_rgb.tif"));
        Assert.False(striped.Frames.RootFrame.Metadata.GetTiffMetadata().Tiled, "bigtiff_le_rgb is reported as tiled.");
    }

    // =====================================================================================================
    // The compressed arms of the advanced corpus
    // =====================================================================================================

    /// <summary>
    /// The 40x28 BigTIFF arms under <c>Fixtures/tiffadv/</c> carry external LONG8 segment tables through all
    /// four strip codecs and one big-endian tiled page. Their pixels are checked against Pillow and tifffile by
    /// <see cref="TiffAdvancedTests"/>; what is asserted here is that the container reaches the codec intact.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdvancedArms))]
    public void AdvancedArm_DecodesAndReportsItsContainer(string name, int compression, bool tiled, ByteOrder byteOrder)
    {
        byte[] bytes = FixturePath.Read($"tiffadv/{name}.tif");
        Assert.True(IsBigTiff(bytes), $"{name}: the fixture does not carry a version-43 header.");

        DirectoryEntry table = FirstDirectoryEntry(bytes, tiled ? 324 : 273);
        Assert.True(table.Type == 16, $"{name}: the segment table has type {table.Type}, not LONG8 (16).");
        Assert.False(table.Inline, $"{name}: the segment table should be an external block.");

        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.True(image.Width == 40 && image.Height == 28, $"{name}: decoded {image.Width}x{image.Height}, expected 40x28.");

        TiffMetadata tiff = image.Metadata.GetTiffMetadata();
        Assert.True(tiff.BigTiff, $"{name}: TiffMetadata.BigTiff is false.");
        Assert.Equal(byteOrder, tiff.ByteOrder);

        TiffFrameMetadata frame = image.Frames.RootFrame.Metadata.GetTiffMetadata();
        Assert.Equal((TiffCompressionMethod)compression, frame.Compression);
        Assert.Equal(tiled, frame.Tiled);
    }

    // =====================================================================================================
    // Format detection and metadata plumbing
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Detector_RecognisesBothContainerVersions(string name)
    {
        ContainerEntry entry = ContainerManifest.Get(name);
        Assert.Equal(ImageFormat.Tiff, Image.DetectFormat(FixturePath.Read($"tiff/{entry.File}")));
        Assert.Equal(ImageFormat.Tiff, Image.DetectFormat(FixturePath.Read($"tiff/{ContainerManifest.Get(entry.Twin).File}")));
    }

    /// <summary>
    /// The detector reads the offset size and the reserved word as well as the version, so a stray 0x2B cannot
    /// let an unrelated file claim to be TIFF. Those four bytes are all that separate the two cases.
    /// </summary>
    [Theory]
    [InlineData(4, 0)] // an offset size this decoder does not implement
    [InlineData(0, 0)]
    [InlineData(8, 1)] // reserved word not zero
    public void Detector_RejectsAVersion43HeaderWithABadOffsetSizeOrReservedWord(int offsetSize, int reserved)
    {
        byte[] header = FixturePath.Read("tiff/bigtiff_le_rgb.tif");
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)offsetSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)reserved);
        Assert.Throws<UnknownImageFormatException>(() => Image.DetectFormat(header));
    }

    /// <summary>
    /// <see cref="TiffMetadata"/> has a hand-written copy constructor, which is exactly the shape that loses a
    /// newly added field. Both the metadata's own clone and a whole-image clone are checked.
    /// </summary>
    [Fact]
    public void BigTiff_MetadataSurvivesDeepClone()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_be_rgb.tif"));
        TiffMetadata source = image.Metadata.GetTiffMetadata();
        Assert.True(source.BigTiff);
        Assert.Equal(ByteOrder.BigEndian, source.ByteOrder);

        TiffMetadata clone = source.DeepClone();
        Assert.True(clone.BigTiff, "TiffMetadata.DeepClone dropped BigTiff.");
        Assert.Equal(ByteOrder.BigEndian, clone.ByteOrder);

        using Image<Rgba32> copy = image.Clone();
        TiffMetadata copied = copy.Metadata.GetTiffMetadata();
        Assert.True(copied.BigTiff, "Image.Clone dropped BigTiff.");
        Assert.Equal(ByteOrder.BigEndian, copied.ByteOrder);
    }

    /// <summary>
    /// The decoder reads BigTIFF; the encoder always writes classic TIFF. Re-encoding a decoded BigTIFF must
    /// therefore produce a version-42 file whose flag has flipped back, with the pixels untouched.
    /// </summary>
    [Fact]
    public void BigTiff_ReEncodesAsClassicTiff()
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read("tiff/bigtiff_le_rgb.tif"));
        using var stream = new MemoryStream();
        source.Save(stream, new TiffEncoder { Compression = TiffCompression.None });
        byte[] encoded = stream.ToArray();

        Assert.False(IsBigTiff(encoded), "The encoder wrote a version-43 header.");
        using Image<Rgba32> reloaded = Image.Load<Rgba32>(encoded);
        Assert.False(reloaded.Metadata.GetTiffMetadata().BigTiff);
        Assert.True(
            MemoryMarshal.AsBytes(reloaded.Frames.RootFrame.PixelSpan).SequenceEqual(MemoryMarshal.AsBytes(source.Frames.RootFrame.PixelSpan)),
            "Re-encoding a BigTIFF page as classic TIFF changed its pixels.");
    }

    // =====================================================================================================
    // Byte-level readers used by the assertions above
    // =====================================================================================================

    /// <summary>One entry of a file's first directory, as that file lays it out.</summary>
    /// <param name="Offset">Where the entry starts, i.e. at its 2-byte tag number.</param>
    /// <param name="ValueFieldOffset">Where the value field starts: 4 bytes wide in classic TIFF, 8 in BigTIFF.</param>
    /// <param name="Type">The TIFF field type number, e.g. 4 for LONG, 16 for LONG8 and 18 for IFD8.</param>
    /// <param name="Count">The element count.</param>
    /// <param name="Inline">True when the value is small enough to sit in the value field itself.</param>
    private readonly record struct DirectoryEntry(int Offset, int ValueFieldOffset, int Type, long Count, bool Inline);

    private static bool IsBigEndian(byte[] file) => file[0] == 0x4D;

    private static bool IsBigTiff(byte[] file) => ReadU16(file, 2) == 43;

    private static ushort ReadU16(byte[] file, int offset)
        => IsBigEndian(file)
            ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset));

    private static uint ReadU32(byte[] file, int offset)
        => IsBigEndian(file)
            ? BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));

    private static ulong ReadU64(byte[] file, int offset)
        => IsBigEndian(file)
            ? BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset))
            : BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(offset));

    /// <summary>Reads an offset-sized field: 4 bytes in classic TIFF, 8 in BigTIFF.</summary>
    private static long ReadOffset(byte[] file, int offset)
        => IsBigTiff(file) ? (long)ReadU64(file, offset) : ReadU32(file, offset);

    private static int FirstDirectoryOffset(byte[] file) => (int)ReadOffset(file, IsBigTiff(file) ? 8 : 4);

    private static long NextDirectoryOffset(byte[] file, int directoryOffset)
    {
        (int countBytes, int entryBytes) = IsBigTiff(file) ? (8, 20) : (2, 12);
        long entries = IsBigTiff(file) ? (long)ReadU64(file, directoryOffset) : ReadU16(file, directoryOffset);
        return ReadOffset(file, directoryOffset + countBytes + (int)(entries * entryBytes));
    }

    /// <summary>Finds a tag in the file's first directory and reports how that file stores it.</summary>
    private static DirectoryEntry FirstDirectoryEntry(byte[] file, int tag)
    {
        bool big = IsBigTiff(file);
        (int countBytes, int entryBytes, int offsetBytes) = big ? (8, 20, 8) : (2, 12, 4);
        int directory = FirstDirectoryOffset(file);
        long entries = big ? (long)ReadU64(file, directory) : ReadU16(file, directory);
        for (int i = 0; i < entries; i++)
        {
            int entry = directory + countBytes + (i * entryBytes);
            if (ReadU16(file, entry) != tag)
            {
                continue;
            }

            int type = ReadU16(file, entry + 2);
            long count = big ? (long)ReadU64(file, entry + 4) : ReadU32(file, entry + 4);
            long size = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                _ => 8,
            };

            return new DirectoryEntry(entry, entry + 4 + offsetBytes, type, count, size * count <= offsetBytes);
        }

        throw new Xunit.Sdk.XunitException($"Tag {tag} is not in the file's first directory.");
    }

    /// <summary>
    /// Rewrites a single inline LONG8 entry as a classic LONG, keeping its value. Only the type word and the
    /// eight value bytes change, so the rest of the file - the raster included - stays exactly where it was. A
    /// big-endian file needs the value moved to the front of the field, which is where a 4-byte type reads it.
    /// </summary>
    private static byte[] RetypeInlineLong8AsLong(byte[] file, int tag)
    {
        DirectoryEntry entry = FirstDirectoryEntry(file, tag);
        Assert.True(
            entry.Inline && entry.Type == 16 && entry.Count == 1,
            $"Tag {tag} is type {entry.Type} x{entry.Count} ({(entry.Inline ? "inline" : "external")}); only one inline LONG8 can be retyped.");

        long value = ReadOffset(file, entry.ValueFieldOffset);
        Assert.True(value >= 0 && value <= uint.MaxValue, $"Tag {tag}'s value {value} does not fit a LONG.");

        byte[] copy = (byte[])file.Clone();
        int typeOffset = entry.Offset + 2; // The type word follows the 2-byte tag number.
        Span<byte> field = copy.AsSpan(entry.ValueFieldOffset, 8);
        field.Clear();
        if (IsBigEndian(file))
        {
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(typeOffset), 4);
            BinaryPrimitives.WriteUInt32BigEndian(field, (uint)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(typeOffset), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)value);
        }

        return copy;
    }

    // =====================================================================================================
    // Manifest
    // =====================================================================================================

    /// <summary>The <c>tiff/manifest.json</c> fields this file needs: the container version and the twin link.</summary>
    public sealed class ContainerEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("frames")]
        public int Frames { get; set; } = 1;

        /// <summary><c>bigtiff</c>, <c>classic</c>, or empty for a fixture that is not part of a container pair.</summary>
        [JsonPropertyName("container")]
        public string Container { get; set; } = string.Empty;

        /// <summary>The sibling fixture built from the same raster in the other container version.</summary>
        [JsonPropertyName("twin")]
        public string Twin { get; set; } = string.Empty;

        /// <summary><c>II</c> or <c>MM</c>: independent of the container version.</summary>
        [JsonPropertyName("byte_order")]
        public string ByteOrder { get; set; } = "II";

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;
    }

    internal static class ContainerManifest
    {
        private static ContainerEntry[]? cached;

        public static ContainerEntry[] Load()
            => cached ??= JsonSerializer.Deserialize<ContainerEntry[]>(System.IO.File.ReadAllBytes(FixturePath.Get("tiff/manifest.json")))
                ?? Array.Empty<ContainerEntry>();

        /// <summary>The BigTIFF fixtures that name a classic twin, in manifest order.</summary>
        public static IEnumerable<ContainerEntry> BigTiffEntries()
            => Load().Where(e => e.Container == "bigtiff" && e.Twin.Length > 0);

        public static ContainerEntry Get(string name)
            => Load().SingleOrDefault(e => e.Name == name)
                ?? throw new Xunit.Sdk.XunitException($"Fixture 'tiff/{name}' is not listed in manifest.json; run Fixtures/generate.py.");
    }
}
