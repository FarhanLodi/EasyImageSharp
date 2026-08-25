using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// GIF decoding: Pillow-generated fixtures under <c>Fixtures/gif/</c> (see <c>EXPECTED.md</c> there) plus
/// hand-crafted byte-level files built by <see cref="GifBuilder"/> for the corners an encoder rarely emits.
/// </summary>
public class GifTests
{
    private static readonly string[] AllFixtures =
    {
        "gif/static_rgb.gif",
        "gif/static_interlaced.gif",
        "gif/transparent.gif",
        "gif/animated_3frames.gif",
        "gif/animated_disposal_none.gif",
        "gif/local_palette.gif",
    };

    // The classic 43-byte 1x1 transparent GIF.
    private static readonly byte[] MinimalTransparentGif =
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    };

    private static readonly Rgba32 Transparent = default;
    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Green = new(0, 255, 0);
    private static readonly Rgba32 Blue = new(0, 0, 255);
    private static readonly Rgba32 White = new(255, 255, 255);
    private static readonly Rgba32 Yellow = new(255, 255, 0);
    private static readonly Rgba32 Gray = new(128, 128, 128);

    // ----- Expected fixture content (mirrors Fixtures/generate.py) -----

    private static Rgba32 StaticColor(int x, int y)
    {
        int i = (x + y) % 64;
        return new Rgba32((byte)(i * 4), (byte)(255 - (i * 4)), (byte)((i * 37) % 256));
    }

    private static Rgba32 TransparentFixtureColor(int x, int y)
        => x >= 10 && x < 30 && y >= 8 && y < 22 ? Transparent : x < 20 ? Red : Green;

    private static Rgba32 Animated3FramesColor(int frame, int x, int y) => frame switch
    {
        0 => x >= 4 && x < 12 && y >= 4 && y < 12 ? Blue : Red,
        1 => x >= 12 && x < 20 && y >= 12 && y < 20 ? White : Green,
        _ => x >= 20 && x < 28 && y >= 20 && y < 28 ? Yellow : Blue,
    };

    private static Rgba32 DisposalNoneColor(int frame, int x, int y)
    {
        if (frame >= 2 && x >= 32 && y >= 16)
        {
            return Blue;
        }

        if (frame >= 1 && x >= 16 && x < 32 && y >= 8 && y < 24)
        {
            return Green;
        }

        return x < 16 && y < 16 ? Red : Gray;
    }

    private static Rgba32 LocalPaletteColor(int frame, int x, int y)
        => frame == 0
            ? (x < 12 ? new Rgba32(10, 20, 30) : new Rgba32(200, 100, 50))
            : (x < 12 ? new Rgba32(0, 128, 255) : new Rgba32(255, 0, 128));

    // ----- Fixture decoding -----

    [Theory]
    [InlineData("gif/static_rgb.gif")]
    [InlineData("gif/static_interlaced.gif")]
    public void Static_DecodesEveryPixel_Rgba32(string fixture)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read(fixture));
        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        Assert.Single(image.Frames);
        AssertFrame(image, 0, (x, y) => StaticColor(x, y));
    }

    [Theory]
    [InlineData("gif/static_rgb.gif")]
    [InlineData("gif/static_interlaced.gif")]
    public void Static_DecodesEveryPixel_Rgb24(string fixture)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(FixturePath.Read(fixture));
        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 expected = StaticColor(x, y);
                Assert.Equal(new Rgb24(expected.R, expected.G, expected.B), image[x, y]);
            }
        }
    }

    [Fact]
    public void Static_DecodesToL8()
    {
        using Image<L8> image = Image.Load<L8>(FixturePath.Read("gif/static_rgb.gif"));
        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        Assert.Equal(L8.FromRgba32(StaticColor(5, 9)).PackedValue, image[5, 9].PackedValue);
        Assert.Equal(L8.FromRgba32(StaticColor(63, 47)).PackedValue, image[63, 47].PackedValue);
    }

    [Fact]
    public void Interlaced_DecodesIdenticallyToNonInterlaced()
    {
        using Image<Rgba32> plain = Image.Load<Rgba32>(FixturePath.Read("gif/static_rgb.gif"));
        using Image<Rgba32> interlaced = Image.Load<Rgba32>(FixturePath.Read("gif/static_interlaced.gif"));
        Assert.Equal(plain.Width, interlaced.Width);
        Assert.Equal(plain.Height, interlaced.Height);
        for (int y = 0; y < plain.Height; y++)
        {
            Assert.True(plain.Frames.RootFrame.GetRowSpan(y).SequenceEqual(interlaced.Frames.RootFrame.GetRowSpan(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void Transparent_LeavesTransparentPixelsUntouched()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("gif/transparent.gif"));
        Assert.Equal(40, image.Width);
        Assert.Equal(30, image.Height);
        Assert.Single(image.Frames);
        AssertFrame(image, 0, TransparentFixtureColor);
    }

    [Fact]
    public void Transparent_Rgb24_DropsAlphaToBlack()
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(FixturePath.Read("gif/transparent.gif"));
        Assert.Equal(new Rgb24(255, 0, 0), image[2, 2]);
        Assert.Equal(new Rgb24(0, 255, 0), image[35, 2]);
        Assert.Equal(new Rgb24(0, 0, 0), image[20, 15]);
    }

    [Fact]
    public void Animated_Disposal2_EachFrameIsItsOwnContent()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("gif/animated_3frames.gif"));
        Assert.Equal(32, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(3, image.Frames.Count);
        for (int frame = 0; frame < 3; frame++)
        {
            int f = frame;
            AssertFrame(image, frame, (x, y) => Animated3FramesColor(f, x, y));
        }
    }

    [Fact]
    public void Animated_Disposal2_Rgb24()
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(FixturePath.Read("gif/animated_3frames.gif"));
        Assert.Equal(3, image.Frames.Count);
        Assert.Equal(new Rgb24(0, 0, 255), image.Frames[0][6, 6]);
        Assert.Equal(new Rgb24(0, 255, 0), image.Frames[1][6, 6]);
        Assert.Equal(new Rgb24(255, 255, 0), image.Frames[2][24, 24]);
    }

    [Fact]
    public void Animated_DisposalNone_RetainsEarlierContentOutsidePartialFrames()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("gif/animated_disposal_none.gif"));
        Assert.Equal(48, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(3, image.Frames.Count);
        for (int frame = 0; frame < 3; frame++)
        {
            int f = frame;
            AssertFrame(image, frame, (x, y) => DisposalNoneColor(f, x, y));
        }
    }

    [Fact]
    public void LocalPalette_UsesTheFramesOwnColorTable()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("gif/local_palette.gif"));
        Assert.Equal(24, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(2, image.Frames.Count);
        AssertFrame(image, 0, (x, y) => LocalPaletteColor(0, x, y));
        AssertFrame(image, 1, (x, y) => LocalPaletteColor(1, x, y));
    }

    // ----- Identify & limits -----

    [Theory]
    [InlineData("gif/static_rgb.gif", 64, 48, 1, 6)]
    [InlineData("gif/static_interlaced.gif", 64, 48, 1, 6)]
    [InlineData("gif/transparent.gif", 40, 30, 1, 2)]
    [InlineData("gif/animated_3frames.gif", 32, 32, 3, 2)]
    [InlineData("gif/animated_disposal_none.gif", 48, 32, 3, 2)]
    [InlineData("gif/local_palette.gif", 24, 16, 2, 2)]
    public void Identify_ReportsHeaderInfo(string fixture, int width, int height, int frames, int bitsPerPixel)
    {
        ImageInfo info = Image.Identify(FixturePath.Read(fixture));
        Assert.Equal("GIF", info.Format.Name);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(frames, info.FrameCount);
        Assert.Equal(bitsPerPixel, info.PixelType.BitsPerPixel);
    }

    [Fact]
    public void Identify_IsNotSubjectToSizeLimits()
    {
        ImageInfo info = Image.Identify(FixturePath.Read("gif/static_rgb.gif"), new DecoderOptions { MaxPixels = 1 });
        Assert.Equal(64, info.Width);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public void MaxFrames_LimitsDecodedFramesButNotIdentify()
    {
        byte[] data = FixturePath.Read("gif/animated_3frames.gif");
        var options = new DecoderOptions { MaxFrames = 2 };
        using Image<Rgba32> image = Image.Load<Rgba32>(data, options);
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(3, Image.Identify(data, options).FrameCount);
        AssertFrame(image, 1, (x, y) => Animated3FramesColor(1, x, y));
    }

    [Fact]
    public void MaxPixels_Tiny_ThrowsSizeLimitException()
    {
        var options = new DecoderOptions { MaxPixels = 100 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(FixturePath.Read("gif/static_rgb.gif"), options));
    }

    [Fact]
    public void Load_ByPath_UsesGifDecoder()
    {
        using Image image = Image.Load(FixturePath.Get("gif/animated_3frames.gif"));
        Assert.Equal(32, image.Width);
        using Image<Rgb24> typed = Image.Load<Rgb24>(FixturePath.Get("gif/static_rgb.gif"));
        Assert.Equal(48, typed.Height);
        Assert.Equal("GIF", Image.DetectFormat(FixturePath.Read("gif/static_rgb.gif")).Name);
    }

    // ----- Robustness -----

    [Fact]
    public void Truncated_Animated_At60Percent_YieldsFramesOrInvalidContent()
    {
        byte[] data = FixturePath.Read("gif/animated_3frames.gif");
        byte[] cut = data[..(data.Length * 6 / 10)];
        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(cut);
            Assert.True(image.Frames.Count >= 1);
            AssertFrame(image, 0, (x, y) => Animated3FramesColor(0, x, y));
        }
        catch (InvalidImageContentException)
        {
            // Acceptable outcome for a truncated file.
        }
    }

    [Fact]
    public void Truncation_AtEveryLength_OnlyThrowsFormatExceptions()
    {
        foreach (string fixture in AllFixtures)
        {
            byte[] data = FixturePath.Read(fixture);
            for (int length = 0; length < data.Length; length++)
            {
                byte[] cut = data[..length];
                try
                {
                    using Image<Rgba32> image = Image.Load<Rgba32>(cut);
                    Assert.True(image.Frames.Count >= 1);
                }
                catch (ImageFormatException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{fixture} cut at {length}: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    Image.Identify(cut);
                }
                catch (ImageFormatException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{fixture} cut at {length} (Identify): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void Fuzz_RandomByteFlips_OnlyThrowFormatOrNotSupportedExceptions()
    {
        var random = new Random(20260816);
        var options = new DecoderOptions { MaxPixels = 1 << 22 };
        byte[][] sources = new byte[AllFixtures.Length][];
        for (int i = 0; i < AllFixtures.Length; i++)
        {
            sources[i] = FixturePath.Read(AllFixtures[i]);
        }

        for (int iteration = 0; iteration < 200; iteration++)
        {
            int which = random.Next(sources.Length);
            byte[] data = (byte[])sources[which].Clone();
            int flips = 1 + random.Next(8);
            for (int f = 0; f < flips; f++)
            {
                int position = random.Next(data.Length);
                data[position] ^= (byte)(1 << random.Next(8));
                if (random.Next(4) == 0)
                {
                    data[position] = (byte)random.Next(256);
                }
            }

            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(data, options);
                Assert.True(image.Frames.Count >= 1);
            }
            catch (ImageFormatException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"Iteration {iteration} ({AllFixtures[which]}): {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                Image.Identify(data, options);
            }
            catch (ImageFormatException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"Iteration {iteration} ({AllFixtures[which]}, Identify): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ----- Hand-crafted files -----

    [Fact]
    public void MinimalTransparentGif_Decodes()
    {
        Assert.Equal(43, MinimalTransparentGif.Length);
        using Image<Rgba32> image = Image.Load<Rgba32>(MinimalTransparentGif);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Single(image.Frames);
        Assert.Equal(Transparent, image[0, 0]);

        ImageInfo info = Image.Identify(MinimalTransparentGif);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal(1, info.PixelType.BitsPerPixel);
        Assert.Equal("GIF", info.Format.Name);
    }

    [Fact]
    public void Gif87a_WithLocalTableOnly_Decodes()
    {
        byte[] indices = { 0, 1, 2, 3, 3, 2, 1, 0 };
        byte[] data = new GifBuilder(4, 2, globalTable: null, version: "87a")
            .Image(0, 0, 4, 2, indices, localTable: new[] { Red, Green, Blue, White })
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(Red, image[0, 0]);
        Assert.Equal(Green, image[1, 0]);
        Assert.Equal(Blue, image[2, 0]);
        Assert.Equal(White, image[3, 0]);
        Assert.Equal(White, image[0, 1]);
        Assert.Equal(Red, image[3, 1]);
        Assert.Equal(8, Image.Identify(data).PixelType.BitsPerPixel);
    }

    [Fact]
    public void NoColorTable_ThrowsInvalidContent()
    {
        byte[] data = new GifBuilder(2, 2, globalTable: null)
            .Image(0, 0, 2, 2, new byte[] { 0, 1, 1, 0 })
            .ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
    }

    [Fact]
    public void FrameLargerThanScreen_IsClipped()
    {
        byte[] indices = new byte[8 * 8];
        Array.Fill(indices, (byte)1);
        byte[] data = new GifBuilder(4, 4, new[] { Red, Green })
            .Image(2, 2, 8, 8, indices)
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        AssertFrame(image, 0, (x, y) => x >= 2 && y >= 2 ? Green : Transparent);
    }

    [Fact]
    public void FrameEntirelyOutsideScreen_LeavesCanvasTransparent()
    {
        byte[] data = new GifBuilder(3, 3, new[] { Red, Green })
            .Image(10, 10, 2, 2, new byte[] { 1, 1, 1, 1 })
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Single(image.Frames);
        AssertFrame(image, 0, (_, _) => Transparent);
    }

    [Theory]
    [InlineData(5, 7)]
    [InlineData(1, 1)]
    [InlineData(3, 9)]
    [InlineData(2, 16)]
    [InlineData(4, 17)]
    public void Interlaced_HandCrafted_OddSizes(int width, int height)
    {
        byte[] indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i % 4);
        }

        Rgba32[] palette = { Red, Green, Blue, White };
        byte[] plain = new GifBuilder(width, height, palette).Image(0, 0, width, height, indices).ToArray();
        byte[] interlaced = new GifBuilder(width, height, palette).Image(0, 0, width, height, indices, interlaced: true).ToArray();

        using Image<Rgba32> a = Image.Load<Rgba32>(plain);
        using Image<Rgba32> b = Image.Load<Rgba32>(interlaced);
        AssertFrame(a, 0, (x, y) => palette[((y * width) + x) % 4]);
        AssertFrame(b, 0, (x, y) => palette[((y * width) + x) % 4]);
    }

    [Fact]
    public void InterlacedRow_MapsEveryRowExactlyOnce()
    {
        foreach (int height in new[] { 1, 2, 3, 4, 5, 8, 9, 16, 17, 100 })
        {
            var seen = new bool[height];
            for (int streamRow = 0; streamRow < height; streamRow++)
            {
                int row = GifDecoder.InterlacedRow(streamRow, height);
                Assert.InRange(row, 0, height - 1);
                Assert.False(seen[row], $"Row {row} of height {height} produced twice.");
                seen[row] = true;
            }
        }
    }

    [Fact]
    public void MinCodeSize1_IsAcceptedAsTwo()
    {
        byte[] indices = { 0, 1, 1, 0, 1, 0, 0, 1 };
        byte[] data = new GifBuilder(4, 2, new[] { Red, Green })
            .Image(0, 0, 4, 2, indices, minCodeSizeByte: 1)
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        AssertFrame(image, 0, (x, y) => indices[(y * 4) + x] == 0 ? Red : Green);
    }

    [Fact]
    public void MinCodeSizeAbove8_ThrowsInvalidContent()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Image(0, 0, 2, 1, new byte[] { 0, 1 }, minCodeSizeByte: 9)
            .ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
    }

    [Fact]
    public void CorruptLzw_InFirstFrame_ThrowsInvalidContent()
    {
        // Clear code (4) followed by code 7, which is neither a literal nor a defined table entry.
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .RawImage(0, 0, 2, 1, minCodeSize: 2, lzwData: new byte[] { 0x3C })
            .ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
    }

    [Fact]
    public void CorruptLzw_AfterCompleteFrame_ReturnsFramesSoFar()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Image(0, 0, 2, 1, new byte[] { 1, 1 })
            .RawImage(0, 0, 2, 1, minCodeSize: 2, lzwData: new byte[] { 0x3C })
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Single(image.Frames);
        Assert.Equal(Green, image[0, 0]);
    }

    [Fact]
    public void Disposal3_RestoresPreviousCanvas()
    {
        byte[] full = new byte[16];
        byte[] data = new GifBuilder(4, 4, new[] { Red, Green, Blue })
            .Image(0, 0, 4, 4, full)                                            // Frame 0: all red.
            .GraphicControl(disposal: 3)
            .Image(1, 1, 2, 2, new byte[] { 2, 2, 2, 2 })                       // Frame 1: blue square, restore after.
            .Image(0, 0, 1, 1, new byte[] { 1 })                                // Frame 2: green dot.
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(3, image.Frames.Count);
        AssertFrame(image, 0, (_, _) => Red);
        AssertFrame(image, 1, (x, y) => x >= 1 && x < 3 && y >= 1 && y < 3 ? Blue : Red);
        AssertFrame(image, 2, (x, y) => x == 0 && y == 0 ? Green : Red);
    }

    [Fact]
    public void Disposal2_ClearsToTransparentBlackNotBackgroundColor()
    {
        byte[] full = new byte[9];
        byte[] data = new GifBuilder(3, 3, new[] { Red, Green, Blue }, backgroundIndex: 2)
            .GraphicControl(disposal: 2)
            .Image(0, 0, 3, 3, full)                                            // Frame 0: all red, then cleared.
            .Image(0, 0, 1, 1, new byte[] { 1 })                                // Frame 1: green dot on cleared canvas.
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(2, image.Frames.Count);
        AssertFrame(image, 0, (_, _) => Red);
        AssertFrame(image, 1, (x, y) => x == 0 && y == 0 ? Green : Transparent);
    }

    [Fact]
    public void Disposal2_OnlyClearsTheFramesOwnRectangle()
    {
        byte[] full = new byte[16];
        byte[] data = new GifBuilder(4, 4, new[] { Red, Green, Blue })
            .Image(0, 0, 4, 4, full)                                            // Frame 0: all red.
            .GraphicControl(disposal: 2)
            .Image(2, 2, 2, 2, new byte[] { 2, 2, 2, 2 })                       // Frame 1: blue corner, cleared after.
            .Image(0, 0, 1, 1, new byte[] { 1 })                                // Frame 2.
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        AssertFrame(image, 1, (x, y) => x >= 2 && y >= 2 ? Blue : Red);
        AssertFrame(image, 2, (x, y) => x >= 2 && y >= 2 ? Transparent : x == 0 && y == 0 ? Green : Red);
    }

    [Fact]
    public void TransparentIndex_AndOutOfRangeIndex_LeaveCanvasUntouched()
    {
        byte[] data = new GifBuilder(4, 1, new[] { Red, Green })
            .Image(0, 0, 4, 1, new byte[] { 1, 1, 1, 1 })
            .GraphicControl(disposal: 1, transparentIndex: 1)
            .Image(0, 0, 4, 1, new byte[] { 0, 1, 3, 2 })                       // 1 = transparent, 2/3 = beyond palette.
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(2, image.Frames.Count);
        AssertFrame(image, 1, (x, _) => x == 0 ? Red : Green);
    }

    [Fact]
    public void Extensions_AreSkipped()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Comment("hello")
            .Application("NETSCAPE2.0", new byte[] { 1, 0, 0 })
            .PlainText()
            .RawExtension(0xAB, new byte[] { 1, 2, 3 })
            .Image(0, 0, 2, 1, new byte[] { 0, 1 })
            .Comment("trailing")
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Single(image.Frames);
        Assert.Equal(Red, image[0, 0]);
        Assert.Equal(Green, image[1, 0]);
        Assert.Equal(1, Image.Identify(data).FrameCount);
    }

    [Fact]
    public void UnknownBlock_BeforeFirstImage_Throws()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Raw(new byte[] { 0x7F, 0x00, 0x00 })
            .Image(0, 0, 2, 1, new byte[] { 0, 1 })
            .ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
    }

    [Fact]
    public void UnknownBlock_AfterFirstImage_ReturnsFramesSoFar()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Image(0, 0, 2, 1, new byte[] { 0, 1 })
            .Raw(new byte[] { 0x7F, 0x00, 0x00 })
            .Image(0, 0, 2, 1, new byte[] { 1, 0 })
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Single(image.Frames);
        Assert.Equal(1, Image.Identify(data).FrameCount);
    }

    [Fact]
    public void MissingTrailer_IsTolerated()
    {
        byte[] data = new GifBuilder(2, 1, new[] { Red, Green })
            .Image(0, 0, 2, 1, new byte[] { 0, 1 })
            .ToArray(trailer: false);

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(Green, image[1, 0]);
    }

    [Fact]
    public void ZeroLogicalScreen_UsesFirstImageSize()
    {
        byte[] data = new GifBuilder(0, 0, new[] { Red, Green })
            .Image(0, 0, 3, 2, new byte[] { 0, 1, 0, 1, 0, 1 })
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(Green, image[1, 0]);
        Assert.Equal(Red, image[1, 1]);

        ImageInfo info = Image.Identify(data);
        Assert.Equal(3, info.Width);
        Assert.Equal(2, info.Height);
    }

    [Fact]
    public void NoImageBlocks_ThrowsInvalidContent()
    {
        byte[] data = new GifBuilder(2, 2, new[] { Red, Green }).Comment("empty").ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
        Assert.Equal(0, Image.Identify(data).FrameCount);
    }

    [Fact]
    public void ShortLzwStream_LeavesRemainingPixelsTransparent()
    {
        // 2 of 4 pixels encoded, then EOI: rows the code stream never reached stay transparent.
        byte[] data = new GifBuilder(2, 2, new[] { Red, Green })
            .RawImage(0, 0, 2, 2, minCodeSize: 2, lzwData: GifBuilder.LzwEncode(new byte[] { 1, 1 }, 2))
            .ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        AssertFrame(image, 0, (_, y) => y == 0 ? Green : Transparent);
    }

    [Fact]
    public void LargeFrame_ExercisesCodeSizeGrowthAndTableReset()
    {
        // 256 colors, 300x200 noise: forces the LZW table to fill and the encoder to emit clear codes.
        var palette = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            palette[i] = new Rgba32((byte)i, (byte)(255 - i), (byte)((i * 7) % 256));
        }

        var random = new Random(7);
        byte[] indices = new byte[300 * 200];
        random.NextBytes(indices);
        byte[] data = new GifBuilder(300, 200, palette).Image(0, 0, 300, 200, indices).ToArray();

        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        AssertFrame(image, 0, (x, y) => palette[indices[(y * 300) + x]]);
    }

    [Fact]
    public void GifDecoder_DirectUse_RejectsNonGifData()
    {
        var decoder = new GifDecoder();
        Assert.Throws<InvalidImageContentException>(() => decoder.Decode<Rgba32>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
        Assert.Throws<InvalidImageContentException>(() => decoder.Identify(new byte[] { 0x47, 0x49, 0x46 }));
    }

    // ----- Helpers -----

    private static void AssertFrame(Image<Rgba32> image, int frameIndex, Func<int, int, Rgba32> expected)
    {
        ImageFrame<Rgba32> frame = image.Frames[frameIndex];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Rgba32 want = expected(x, y);
                Rgba32 got = frame[x, y];
                if (want != got)
                {
                    Assert.Fail($"Frame {frameIndex} pixel ({x}, {y}): expected {want}, got {got}.");
                }
            }
        }
    }
}

/// <summary>Builds hand-crafted GIF87a/89a byte streams, including a small GIF-flavoured LZW encoder.</summary>
internal sealed class GifBuilder
{
    private readonly MemoryStream stream = new();

    public GifBuilder(int width, int height, Rgba32[]? globalTable, byte backgroundIndex = 0, string version = "89a")
    {
        this.WriteAscii("GIF" + version);
        this.WriteUInt16(width);
        this.WriteUInt16(height);
        if (globalTable is null)
        {
            this.stream.WriteByte(0x00);
        }
        else
        {
            int depth = TableDepth(globalTable.Length);
            this.stream.WriteByte((byte)(0x80 | ((depth - 1) << 4) | (depth - 1)));
        }

        this.stream.WriteByte(backgroundIndex);
        this.stream.WriteByte(0); // Pixel aspect ratio.
        if (globalTable is not null)
        {
            this.WriteColorTable(globalTable);
        }
    }

    public GifBuilder GraphicControl(int disposal = 0, int? transparentIndex = null, int delayCentiseconds = 0)
    {
        this.stream.WriteByte(0x21);
        this.stream.WriteByte(0xF9);
        this.stream.WriteByte(4);
        this.stream.WriteByte((byte)((disposal << 2) | (transparentIndex.HasValue ? 1 : 0)));
        this.WriteUInt16(delayCentiseconds);
        this.stream.WriteByte((byte)(transparentIndex ?? 0));
        this.stream.WriteByte(0);
        return this;
    }

    public GifBuilder Comment(string text) => this.RawExtension(0xFE, System.Text.Encoding.ASCII.GetBytes(text));

    public GifBuilder Application(string identifier, byte[] payload)
    {
        this.stream.WriteByte(0x21);
        this.stream.WriteByte(0xFF);
        this.stream.WriteByte(11);
        this.WriteAscii(identifier);
        this.WriteSubBlocks(payload);
        return this;
    }

    public GifBuilder PlainText()
    {
        this.stream.WriteByte(0x21);
        this.stream.WriteByte(0x01);
        this.stream.WriteByte(12);
        this.stream.Write(new byte[12]);
        this.WriteSubBlocks(System.Text.Encoding.ASCII.GetBytes("text"));
        return this;
    }

    public GifBuilder RawExtension(byte label, byte[] payload)
    {
        this.stream.WriteByte(0x21);
        this.stream.WriteByte(label);
        this.WriteSubBlocks(payload);
        return this;
    }

    public GifBuilder Raw(byte[] bytes)
    {
        this.stream.Write(bytes);
        return this;
    }

    public GifBuilder Image(
        int left, int top, int width, int height, byte[] indices, Rgba32[]? localTable = null,
        bool interlaced = false, int? minCodeSizeByte = null)
    {
        int colors = localTable?.Length ?? 0;
        int max = 0;
        foreach (byte index in indices)
        {
            max = Math.Max(max, index);
        }

        int minCodeSize = Math.Max(2, Math.Max(TableDepth(colors), BitsFor(max + 1)));
        byte[] payload = interlaced ? InterlaceRows(indices, width, height) : indices;
        return this.RawImage(left, top, width, height, minCodeSizeByte ?? minCodeSize, LzwEncode(payload, minCodeSize), localTable, interlaced);
    }

    public GifBuilder RawImage(
        int left, int top, int width, int height, int minCodeSize, byte[] lzwData, Rgba32[]? localTable = null, bool interlaced = false)
    {
        this.stream.WriteByte(0x2C);
        this.WriteUInt16(left);
        this.WriteUInt16(top);
        this.WriteUInt16(width);
        this.WriteUInt16(height);
        int flags = interlaced ? 0x40 : 0;
        if (localTable is not null)
        {
            flags |= 0x80 | (TableDepth(localTable.Length) - 1);
        }

        this.stream.WriteByte((byte)flags);
        if (localTable is not null)
        {
            this.WriteColorTable(localTable);
        }

        this.stream.WriteByte((byte)minCodeSize);
        this.WriteSubBlocks(lzwData);
        return this;
    }

    public byte[] ToArray(bool trailer = true)
    {
        if (trailer)
        {
            this.stream.WriteByte(0x3B);
        }

        return this.stream.ToArray();
    }

    /// <summary>GIF LZW encoder: LSB-first, leading clear code, trailing end-of-information code.</summary>
    public static byte[] LzwEncode(ReadOnlySpan<byte> input, int minCodeSize)
    {
        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = eoiCode + 1;
        var table = new Dictionary<(int Prefix, byte Value), int>();
        var output = new MemoryStream();
        uint bitBuffer = 0;
        int bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= (uint)code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clearCode);
        int prefix = -1;
        foreach (byte value in input)
        {
            if (prefix == -1)
            {
                prefix = value;
                continue;
            }

            if (table.TryGetValue((prefix, value), out int existing))
            {
                prefix = existing;
                continue;
            }

            Emit(prefix);
            if (nextCode < 4096)
            {
                table[(prefix, value)] = nextCode++;
                if (nextCode - 1 == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                Emit(clearCode);
                table.Clear();
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
            }

            prefix = value;
        }

        if (prefix != -1)
        {
            Emit(prefix);
        }

        Emit(eoiCode);
        if (bitCount > 0)
        {
            output.WriteByte((byte)bitBuffer);
        }

        return output.ToArray();
    }

    private static byte[] InterlaceRows(byte[] indices, int width, int height)
    {
        var result = new byte[indices.Length];
        int streamRow = 0;
        foreach ((int start, int step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (int row = start; row < height; row += step)
            {
                Array.Copy(indices, row * width, result, streamRow * width, width);
                streamRow++;
            }
        }

        return result;
    }

    private static int TableDepth(int entries)
    {
        int depth = 1;
        while ((1 << depth) < entries)
        {
            depth++;
        }

        return depth;
    }

    private static int BitsFor(int values)
    {
        int bits = 1;
        while ((1 << bits) < values)
        {
            bits++;
        }

        return bits;
    }

    private void WriteColorTable(Rgba32[] table)
    {
        int entries = 1 << TableDepth(table.Length);
        for (int i = 0; i < entries; i++)
        {
            Rgba32 color = i < table.Length ? table[i] : default;
            this.stream.WriteByte(color.R);
            this.stream.WriteByte(color.G);
            this.stream.WriteByte(color.B);
        }
    }

    private void WriteSubBlocks(byte[] payload)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            int count = Math.Min(255, payload.Length - offset);
            this.stream.WriteByte((byte)count);
            this.stream.Write(payload, offset, count);
            offset += count;
        }

        this.stream.WriteByte(0);
    }

    private void WriteUInt16(int value)
    {
        this.stream.WriteByte((byte)(value & 0xFF));
        this.stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private void WriteAscii(string text) => this.stream.Write(System.Text.Encoding.ASCII.GetBytes(text));
}
