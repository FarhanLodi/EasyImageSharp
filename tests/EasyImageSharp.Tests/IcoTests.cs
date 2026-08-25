using System.Buffers.Binary;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Ico;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// ICO/CUR: fixtures under <c>Fixtures/smallformats/ico/</c> (Pillow-written and hand-assembled DIB/PNG
/// entries with AND masks) decode exactly, every entry becomes a frame, and the encoder round-trips both the
/// BMP and the PNG entry paths including 256-pixel entries and cursor hotspots.
/// </summary>
public class IcoTests
{
    private const string Folder = "smallformats/ico";

    public static IEnumerable<object[]> Fixtures => SmallFormatFixtures.Names(Folder);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DecodesToReference(string name) => SmallFormatFixtures.Verify(Folder, name);

    [Fact]
    public void Manifest_IsPresentAndNonEmpty() => SmallFormatFixtures.AssertManifest(Folder, minimumEntries: 18);

    [Theory]
    [InlineData("pil_png_multi")]
    [InlineData("hand_bmp4_pal_andmask")]
    [InlineData("hand_cur_hotspot")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, name);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Frames.Count, bgra.Frames.Count);
    }

    // ----- Encoder round trips -----

    [Fact]
    public void MultiFrame_Auto_UsesBmpUpTo48AndPngAbove()
    {
        using Image<Rgba32> image = MultiSizeImage(16, 48, 64);
        byte[] bytes = Encode(image, new IcoEncoder());
        Assert.Equal(ImageFormat.Ico, Image.DetectFormat(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(new[] { false, false, true }, EntryIsPng(bytes));

        ImageInfo info = Image.Identify(bytes);
        Assert.Equal((16, 16, 32, 3), (info.Width, info.Height, info.PixelType.BitsPerPixel, info.FrameCount));
        AssertFramesEqual(image, bytes);
    }

    [Theory]
    [InlineData(IcoEntryFormat.Bmp)]
    [InlineData(IcoEntryFormat.Png)]
    public void MultiFrame_ForcedEntryFormat_RoundTrips(IcoEntryFormat format)
    {
        using Image<Rgba32> image = MultiSizeImage(24, 33, 100);
        byte[] bytes = Encode(image, new IcoEncoder { EntryFormat = format });
        Assert.All(EntryIsPng(bytes), isPng => Assert.Equal(format == IcoEntryFormat.Png, isPng));
        AssertFramesEqual(image, bytes);
    }

    [Fact]
    public void Entry256_IsStoredAsZeroAndDecodesTo256()
    {
        using var image = new Image<Rgba32>(256, 256, new Rgba32(30, 60, 90, 200));
        image[255, 255] = new Rgba32(1, 2, 3, 4);
        byte[] bytes = Encode(image, new IcoEncoder());
        Assert.Equal(0, bytes[6]);
        Assert.Equal(0, bytes[7]);
        Assert.Equal(new[] { true }, EntryIsPng(bytes));
        using Image<Rgba32> decoded = Image.Load<Rgba32>(bytes);
        Assert.Equal((256, 256), (decoded.Width, decoded.Height));
        Assert.Equal(new Rgba32(1, 2, 3, 4), decoded[255, 255]);
        Assert.Equal(256, Image.Identify(bytes).Width);

        using var bmp256 = new Image<Rgb24>(256, 20, new Rgb24(5, 6, 7));
        byte[] bmpBytes = Encode(bmp256, new IcoEncoder { EntryFormat = IcoEntryFormat.Bmp });
        Assert.Equal((0, 20), (bmpBytes[6], bmpBytes[7]));
        using Image<Rgb24> bmpDecoded = Image.Load<Rgb24>(bmpBytes);
        Assert.Equal((256, 20), (bmpDecoded.Width, bmpDecoded.Height));
        Assert.Equal(new Rgb24(5, 6, 7), bmpDecoded[255, 19]);
    }

    [Fact]
    public void FullyTransparentAndOpaqueFrames_RoundTripThroughBmpEntries()
    {
        using var image = new Image<Rgba32>(20, 13, new Rgba32(200, 100, 50, 0)); // All alpha zero -> AND mask carries transparency.
        image.Frames.CreateFrame(9, 7).PixelSpan.Fill(new Rgba32(1, 2, 3, 255));
        byte[] bytes = Encode(image, new IcoEncoder { EntryFormat = IcoEntryFormat.Bmp });
        AssertFramesEqual(image, bytes);
    }

    [Fact]
    public void Cursor_WritesHotspotsAndTypeTwo()
    {
        using Image<Rgba32> image = MultiSizeImage(32, 48);
        byte[] bytes = Encode(image, new IcoEncoder { EncodeAsCursor = true, Hotspots = new[] { new Point(3, 5) } });
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        Assert.Equal((3, 5), (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6 + 4)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6 + 6))));
        Assert.Equal((0, 0), (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22 + 4)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22 + 6))));
        Assert.Equal(ImageFormat.Ico, Image.DetectFormat(bytes));
        AssertFramesEqual(image, bytes);
    }

    [Fact]
    public void Limits_AreEnforced()
    {
        using var tooBig = new Image<Rgb24>(257, 10);
        Assert.Throws<NotSupportedException>(() => Encode(tooBig, new IcoEncoder()));

        using var tooMany = new Image<Rgb24>(4, 4);
        for (int i = 0; i < 64; i++)
        {
            tooMany.Frames.CreateFrame(4, 4);
        }

        Assert.Throws<NotSupportedException>(() => Encode(tooMany, new IcoEncoder()));
    }

    [Fact]
    public void MaxFrames_LimitsDecodedEntries()
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, "pil_png_multi");
        Assert.Equal(3, Image.Identify(bytes).FrameCount);
        using Image<Rgba32> two = Image.Load<Rgba32>(bytes, new DecoderOptions { MaxFrames = 2 });
        Assert.Equal(2, two.Frames.Count);
    }

    [Fact]
    public void SizeLimit_AppliesPerEntry()
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, "pil_bmp_multi"); // 16, 24, 32 px entries
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load(bytes, new DecoderOptions { MaxPixels = 24 * 24 }));
        using Image<Rgba32> ok = Image.Load<Rgba32>(bytes, new DecoderOptions { MaxPixels = 32 * 32 });
        Assert.Equal(3, ok.Frames.Count);
    }

    /// <summary>
    /// The encoder output is structurally stable and round-trips exactly. Byte-for-byte length is deliberately
    /// not asserted for PNG entries: the deflate implementation differs between target frameworks.
    /// </summary>
    [Theory]
    [InlineData(IcoEntryFormat.Bmp)]
    [InlineData(IcoEntryFormat.Png)]
    public void Encoder_OutputIsStable(IcoEntryFormat format)
    {
        using Image<Rgba32> image = TestImages.AlphaGradient(37, 29);
        byte[] bytes = Encode(image, new IcoEncoder { EntryFormat = format });

        // ICONDIR: reserved 0, type 1 (icon), one entry describing a 37x29 image.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(37, bytes[6]);
        Assert.Equal(29, bytes[7]);

        using Image<Rgba32> reloaded = Image.Load<Rgba32>(bytes);
        Assert.Equal(image.Width, reloaded.Width);
        Assert.Equal(image.Height, reloaded.Height);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(image[x, y], reloaded[x, y]);
            }
        }

        // Deterministic within a run: encoding twice yields identical bytes.
        Assert.Equal(bytes, Encode(image, new IcoEncoder { EntryFormat = format }));
    }

    [Fact]
    public async Task SaveByExtension_And_Async_WriteIco()
    {
        using Image<Rgba32> image = TestImages.AlphaGradient(32, 32);
        string path = Path.Combine(Path.GetTempPath(), $"eis-{Guid.NewGuid():N}.ico");
        try
        {
            image.Save(path);
            using Image<Rgba32> reloaded = Image.Load<Rgba32>(path);
            Assert.Equal(image[7, 9], reloaded[7, 9]);
        }
        finally
        {
            File.Delete(path);
        }

        using var ms = new MemoryStream();
        await image.SaveAsIcoAsync(ms, new IcoEncoder { EncodeAsCursor = true });
        Assert.Equal(2, ms.ToArray()[2]);
    }

    // ----- Identify / detection / malformed -----

    [Fact]
    public void Identify_ReportsFirstEntry()
    {
        ImageInfo info = Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_mixed_png_bmp"));
        Assert.Equal((24, 24, 32, 2, "ICO"), (info.Width, info.Height, info.PixelType.BitsPerPixel, info.FrameCount, info.Format.Name));
        Assert.Equal(24, Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_bmp24_andmask")).PixelType.BitsPerPixel);
        Assert.Equal(4, Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_bmp4_pal_andmask")).PixelType.BitsPerPixel);
        Assert.Equal(20, Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_dir_size_mismatch")).Width);
    }

    [Fact]
    public void Detection_ChecksDirectoryPlausibility()
    {
        byte[] header = new byte[22];
        header[2] = 1;
        header[4] = 1;
        header[6 + 8] = 40;
        header[6 + 12] = 22;
        Assert.True(ImageFormat.Ico.Matches(header));
        header[2] = 2;
        Assert.True(ImageFormat.Ico.Matches(header));
        header[2] = 3;
        Assert.False(ImageFormat.Ico.Matches(header));
        header[2] = 1;
        header[4] = 0;
        Assert.False(ImageFormat.Ico.Matches(header));
        header[4] = 65;
        Assert.False(ImageFormat.Ico.Matches(header));
        header[4] = 1;
        header[6 + 12] = 10; // offset inside the directory
        Assert.False(ImageFormat.Ico.Matches(header));
        header[6 + 12] = 22;
        header[0] = 1; // reserved
        Assert.False(ImageFormat.Ico.Matches(header));

        Assert.Contains("cur", ImageFormat.Ico.FileExtensions);
        Assert.False(ImageFormat.Ico.Matches(FixturePath.Read("bmp/" + FixtureDecodeTests.Manifest.Load("bmp")[0].File)));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 1, 0, 2, 0, 8, 8, 0, 0, 1, 0, 32, 0, 40, 0, 0, 0, 38, 0, 0, 0 })]                     // directory declares 2 entries but holds 1
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0, 8, 8, 0, 0, 1, 0, 32, 0, 40, 0, 0, 0, 22, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7 })] // DIB header truncated
    public void Malformed_Files_AreInvalid(byte[] data)
    {
        Assert.Throws<InvalidImageContentException>(() => Image.Load(data));
        Assert.Throws<InvalidImageContentException>(() => Image.Identify(data));
    }

    // ----- Helpers -----

    private static Image<Rgba32> MultiSizeImage(params int[] sizes)
    {
        using Image<Rgba32> first = TestImages.AlphaGradient(sizes[0], sizes[0]);
        Image<Rgba32> image = first.Clone();
        for (int i = 1; i < sizes.Length; i++)
        {
            using Image<Rgba32> frame = TestImages.AlphaGradient(sizes[i], sizes[i]);
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }

        return image;
    }

    private static void AssertFramesEqual(Image<Rgba32> original, byte[] encoded)
    {
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(original.Frames.Count, decoded.Frames.Count);
        for (int f = 0; f < original.Frames.Count; f++)
        {
            ImageFrame<Rgba32> a = original.Frames[f];
            ImageFrame<Rgba32> b = decoded.Frames[f];
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
            Assert.True(a.PixelSpan.SequenceEqual(b.PixelSpan), $"frame {f} differs");
        }
    }

    private static bool[] EntryIsPng(byte[] ico)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + (i * 16) + 12));
            result[i] = ImageFormat.Png.Matches(ico.AsSpan(offset));
        }

        return result;
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, IcoEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
