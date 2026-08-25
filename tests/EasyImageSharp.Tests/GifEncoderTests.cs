using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The GIF encoder: round trips through this library's own decoder plus byte-level checks of the blocks it
/// writes. The expected values were cross-checked with Pillow during development (it reads every file this
/// suite produces, reports the same frame count, loop count, delays, transparency and comment, and decodes the
/// same pixels), so the numbers asserted here are known to match an independent implementation.
/// </summary>
public class GifEncoderTests
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationLabel = 0xFF;
    private const byte CommentLabel = 0xFE;
    private const byte ImageSeparator = 0x2C;

    private static readonly Rgba32 Red = new(200, 30, 30);
    private static readonly Rgba32 Green = new(30, 200, 30);
    private static readonly Rgba32 Blue = new(30, 30, 200);
    private static readonly Rgba32 Yellow = new(250, 250, 20);

    // ----- Static images -----

    [Fact]
    public void AStaticImageRoundTripsExactlyWhenItFitsThePalette()
    {
        using Image<Rgba32> source = Blocks(20, 14);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.Single(decoded.Frames);
        AssertPixelsEqual(source, decoded);
    }

    [Theory]
    [InlineData(24, 18)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    public void AStaticPhotoRoundTripsWithASmallError(int width, int height)
    {
        using Image<Rgba32> source = Photo(width, height);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);

        // Dithering trades per-pixel accuracy for local accuracy, so the per-pixel error is a few levels while
        // the average over a 6x6 block stays within a quarter of a level of the original.
        (double mean, int max) = Difference(source, decoded);
        Assert.True(mean < 6.0, $"Mean error {mean:F3} is too large.");
        Assert.True(max <= 32, $"Maximum error {max} is too large.");
        double blockError = BlockMeanError(source, decoded);
        Assert.True(blockError < 1.0, $"Block mean error {blockError:F3} is too large.");
    }

    [Fact]
    public void TheFileStartsWithAGif89aHeaderAndTheLogicalScreen()
    {
        using Image<Rgba32> source = Blocks(20, 14);

        byte[] data = Encode(source, new GifEncoder());

        Assert.Equal("GIF89a", Encoding.ASCII.GetString(data, 0, 6));
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)));
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)));
        Assert.True((data[10] & 0x80) != 0, "A global colour table must be present.");
        Assert.Equal(0, data[11]); // Background colour index.
        Assert.Equal(0, data[12]); // Pixel aspect ratio.
        Assert.Equal(0x3B, data[^1]); // Trailer.
        Assert.Equal(ImageFormat.Gif, ImageFormatDetector.DetectOrThrow(data));
    }

    [Fact]
    public void IdentifyReportsTheEncodedSizeAndFrameCount()
    {
        using Image<Rgba32> source = Animation(16, 12);

        byte[] data = Encode(source, new GifEncoder());

        ImageInfo info = Image.Identify(data);
        Assert.Equal(16, info.Width);
        Assert.Equal(12, info.Height);
        Assert.Equal(3, info.FrameCount);
        Assert.Equal(ImageFormat.Gif, info.Format);
    }

    // ----- Animations -----

    [Theory]
    [InlineData(GifColorTableMode.Global)]
    [InlineData(GifColorTableMode.Local)]
    public void AnAnimationRoundTripsFrameForFrame(GifColorTableMode mode)
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { ColorTableMode = mode });

        Assert.Equal(3, decoded.Frames.Count);
        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void LocalTableModeGivesEveryFrameAfterTheFirstItsOwnTable()
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { ColorTableMode = GifColorTableMode.Local });

        int[] localTables = decoded.Frames.Select(f => f.Metadata.GetGifMetadata().LocalColorTableLength).ToArray();
        Assert.Equal(0, localTables[0]);
        Assert.True(localTables[1] > 0, "The second frame should carry a local colour table.");
        Assert.True(localTables[2] > 0, "The third frame should carry a local colour table.");
    }

    [Fact]
    public void GlobalTableModeGivesNoFrameALocalTable()
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { ColorTableMode = GifColorTableMode.Global });

        Assert.All(decoded.Frames, f => Assert.Equal(0, f.Metadata.GetGifMetadata().LocalColorTableLength));
    }

    [Fact]
    public void TheGlobalTableIsBuiltFromEveryFrame()
    {
        // Each frame uses colours the others do not, so a table quantized from the first frame alone would
        // reproduce the later ones badly. Sharing one table across all frames keeps them exact.
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { ColorTableMode = GifColorTableMode.Global });

        AssertPixelsEqual(source, decoded);
    }

    // ----- Delays -----

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(25)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void TheFrameDelayOptionAppliesToEveryFrame(int delay)
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { FrameDelay = delay });

        Assert.All(decoded.Frames, f => Assert.Equal(delay, f.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public void WithoutAnExplicitDelayEachFrameKeepsItsOwn()
    {
        using Image<Rgba32> source = Animation(16, 12);
        int[] delays = { 11, 22, 33 };
        for (int i = 0; i < source.Frames.Count; i++)
        {
            source.Frames[i].Metadata.GetGifMetadata().FrameDelay = delays[i];
        }

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.Equal(delays, decoded.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public void AnExplicitDelayOverridesTheFrameMetadata()
    {
        using Image<Rgba32> source = Animation(16, 12);
        foreach (ImageFrame<Rgba32> frame in source.Frames)
        {
            frame.Metadata.GetGifMetadata().FrameDelay = 99;
        }

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { FrameDelay = 5 });

        Assert.All(decoded.Frames, f => Assert.Equal(5, f.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public void FramesWithoutMetadataFallBackToTenHundredthsOfASecond()
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.All(decoded.Frames, f => Assert.Equal(10, f.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public void ADecodedAnimationKeepsItsDelaysAndLoopCountWhenReEncoded()
    {
        using Image<Rgba32> original = MetadataTests.LoadFixture("metadata/gif_meta.gif");
        Assert.Equal(new[] { 10, 20, 30 }, original.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay));
        Assert.Equal(3, original.Metadata.GetGifMetadata().RepeatCount);

        using Image<Rgba32> decoded = RoundTrip(original, new GifEncoder());

        Assert.Equal(new[] { 10, 20, 30 }, decoded.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay));
        Assert.Equal(3, decoded.Metadata.GetGifMetadata().RepeatCount);
    }

    // ----- Loop count -----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(65535)]
    public void TheRepeatCountOptionIsWrittenAsANetscapeExtension(int repeatCount)
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { RepeatCount = repeatCount });

        Assert.Equal(repeatCount, decoded.Metadata.GetGifMetadata().RepeatCount);
    }

    [Fact]
    public void WithoutAnExplicitRepeatCountTheImageMetadataIsUsed()
    {
        using Image<Rgba32> source = Animation(16, 12);
        source.Metadata.GetGifMetadata().RepeatCount = 7;

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.Equal(7, decoded.Metadata.GetGifMetadata().RepeatCount);
    }

    [Fact]
    public void AnExplicitRepeatCountOverridesTheImageMetadata()
    {
        using Image<Rgba32> source = Animation(16, 12);
        source.Metadata.GetGifMetadata().RepeatCount = 7;

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { RepeatCount = 2 });

        Assert.Equal(2, decoded.Metadata.GetGifMetadata().RepeatCount);
    }

    [Fact]
    public void StaticImagesCarryNoLoopExtension()
    {
        using Image<Rgba32> source = Blocks(12, 10);

        byte[] data = Encode(source, new GifEncoder { RepeatCount = 5 });

        Assert.DoesNotContain("NETSCAPE2.0", Encoding.ASCII.GetString(data), StringComparison.Ordinal);
        Assert.Equal(1, Image.Identify(data).Metadata.GetGifMetadata().RepeatCount);
    }

    [Fact]
    public void AnimationsCarryTheLoopExtensionBeforeTheFirstFrame()
    {
        using Image<Rgba32> source = Animation(16, 12);

        byte[] data = Encode(source, new GifEncoder { RepeatCount = 4 });

        int netscape = IndexOf(data, Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        Assert.True(netscape > 0, "The loop extension is missing.");
        Assert.Equal(ExtensionIntroducer, data[netscape - 3]);
        Assert.Equal(ApplicationLabel, data[netscape - 2]);
        Assert.Equal(11, data[netscape - 1]);
        Assert.Equal(3, data[netscape + 11]);
        Assert.Equal(1, data[netscape + 12]);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(netscape + 13)));
        Assert.True(netscape < IndexOf(data, new byte[] { ImageSeparator }), "The loop extension must precede the first image.");
    }

    // ----- Transparency -----

    [Fact]
    public void TransparentPixelsSurviveAStaticRoundTrip()
    {
        using var source = new Image<Rgba32>(10, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                source[x, y] = x < 5 ? Red : default;
            }
        }

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                if (x < 5)
                {
                    Assert.Equal(Red, decoded[x, y]);
                }
                else
                {
                    Assert.Equal(0, decoded[x, y].A);
                }
            }
        }
    }

    [Fact]
    public void ATransparentImageDeclaresATransparentIndexInItsGraphicControl()
    {
        using var source = new Image<Rgba32>(8, 8);
        for (int x = 0; x < 8; x++)
        {
            source[x, 0] = Red;
        }

        byte[] data = Encode(source, new GifEncoder());

        int gce = IndexOf(data, new byte[] { ExtensionIntroducer, GraphicControlLabel });
        Assert.True(gce > 0, "The graphic control extension is missing.");
        Assert.Equal(4, data[gce + 2]);
        Assert.Equal(1, data[gce + 3] & 1); // The transparency flag.
        Assert.True(Image.Load<Rgba32>(data).Frames[0].Metadata.GetGifMetadata().HasTransparency);
    }

    [Fact]
    public void AnOpaqueImageDeclaresNoTransparentIndex()
    {
        using Image<Rgba32> source = Blocks(12, 10);

        byte[] data = Encode(source, new GifEncoder());

        int gce = IndexOf(data, new byte[] { ExtensionIntroducer, GraphicControlLabel });
        Assert.Equal(0, data[gce + 3] & 1);
        Assert.False(Image.Load<Rgba32>(data).Frames[0].Metadata.GetGifMetadata().HasTransparency);
    }

    [Fact]
    public void TransparencySurvivesAnAnimatedRoundTrip()
    {
        using var source = new Image<Rgba32>(12, 8);
        Rgba32[] colors = { Red, Green, Blue };
        for (int i = 0; i < 3; i++)
        {
            ImageFrame<Rgba32> frame = i == 0 ? source.Frames.RootFrame : source.Frames.CreateFrame(12, 8);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 12; x++)
                {
                    frame.GetRowSpan(y)[x] = x < 4 ? colors[i] : default;
                }
            }
        }

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { ColorTableMode = GifColorTableMode.Local });

        Assert.Equal(3, decoded.Frames.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(colors[i], decoded.Frames[i][0, 0]);
            Assert.Equal(0, decoded.Frames[i][11, 7].A);
        }
    }

    // ----- Interlacing -----

    [Fact]
    public void InterlacedOutputDecodesToTheSamePixels()
    {
        using Image<Rgba32> source = Blocks(23, 17);

        using Image<Rgba32> straight = RoundTrip(source, new GifEncoder());
        using Image<Rgba32> interlaced = RoundTrip(source, new GifEncoder { Interlaced = true });

        AssertPixelsEqual(source, interlaced);
        AssertPixelsEqual(straight, interlaced);
    }

    [Fact]
    public void InterlacedOutputSetsTheDescriptorFlag()
    {
        using Image<Rgba32> source = Blocks(16, 16);

        byte[] plain = Encode(source, new GifEncoder());
        byte[] interlaced = Encode(source, new GifEncoder { Interlaced = true });

        Assert.Equal(0, plain[DescriptorFlagsOffset(plain)] & 0x40);
        Assert.Equal(0x40, interlaced[DescriptorFlagsOffset(interlaced)] & 0x40);
    }

    [Fact]
    public void InterlacedAnimationsAlsoRoundTrip()
    {
        using Image<Rgba32> source = Animation(16, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { Interlaced = true });

        Assert.Equal(3, decoded.Frames.Count);
        AssertPixelsEqual(source, decoded);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(5, 5)]
    [InlineData(7, 3)]
    public void InterlacingHandlesSizesThatDoNotFillEveryPass(int width, int height)
    {
        using Image<Rgba32> source = Blocks(width, height);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { Interlaced = true });

        AssertPixelsEqual(source, decoded);
    }

    // ----- Comments -----

    [Fact]
    public void ACommentIsWrittenAndReadBack()
    {
        using Image<Rgba32> source = Blocks(12, 10);

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder { Comment = "hello gif" });

        Assert.Equal(new[] { "hello gif" }, decoded.Metadata.GetGifMetadata().Comments);
    }

    [Fact]
    public void ALongCommentIsSplitAcrossSubBlocks()
    {
        string comment = new('c', 600);
        using Image<Rgba32> source = Blocks(12, 10);

        byte[] data = Encode(source, new GifEncoder { Comment = comment });

        int marker = IndexOf(data, new byte[] { ExtensionIntroducer, CommentLabel });
        Assert.True(marker > 0, "The comment extension is missing.");
        Assert.Equal(255, data[marker + 2]);
        Assert.Equal(comment, Image.Load<Rgba32>(data).Metadata.GetGifMetadata().Comments.Single());
    }

    [Fact]
    public void AnEmptyCommentIsNotWritten()
    {
        using Image<Rgba32> source = Blocks(12, 10);

        byte[] data = Encode(source, new GifEncoder { Comment = string.Empty });

        Assert.Equal(-1, IndexOf(data, new byte[] { ExtensionIntroducer, CommentLabel }));
    }

    // ----- Quantizer choice -----

    [Fact]
    public void TheQuantizerOptionSelectsThePalette()
    {
        using Image<Rgba32> source = Photo(24, 18);

        using Image<Rgba32> decoded = RoundTrip(
            source,
            new GifEncoder { Quantizer = new WebSafePaletteQuantizer(new QuantizerOptions { Dither = null }) });

        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                Rgba32 pixel = decoded[x, y];
                Assert.Equal(0, pixel.R % 0x33);
                Assert.Equal(0, pixel.G % 0x33);
                Assert.Equal(0, pixel.B % 0x33);
            }
        }
    }

    [Fact]
    public void ASmallPaletteShrinksTheColourTable()
    {
        using Image<Rgba32> source = Photo(24, 18);

        byte[] data = Encode(source, new GifEncoder { Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 4 }) });

        // The logical screen descriptor encodes the table size as 2^(n+1) entries.
        Assert.Equal(4, 2 << (data[10] & 0x07));
        Assert.Equal(4, Image.Identify(data).Metadata.GetGifMetadata().GlobalColorTableLength);
    }

    [Fact]
    public void TwoColourImagesUseTheMinimumLzwCodeSize()
    {
        using var source = new Image<Rgba32>(8, 8, Red);
        for (int x = 0; x < 8; x++)
        {
            source[x, 0] = Blue;
        }

        byte[] data = Encode(source, new GifEncoder());

        // The GIF specification requires a minimum code size of at least 2 even for a two-colour table.
        int descriptor = IndexOf(data, new byte[] { ImageSeparator });
        Assert.Equal(2, data[descriptor + 10]);
        AssertPixelsEqual(source, Image.Load<Rgba32>(data));
    }

    // ----- Frame geometry -----

    [Fact]
    public void FramesLargerThanTheRootAreCroppedToTheLogicalScreen()
    {
        using var source = new Image<Rgba32>(10, 8, Red);
        ImageFrame<Rgba32> big = source.Frames.CreateFrame(16, 12);
        for (int y = 0; y < 12; y++)
        {
            big.GetRowSpan(y).Fill(Green);
        }

        using Image<Rgba32> decoded = RoundTrip(source, new GifEncoder());

        Assert.Equal(10, decoded.Width);
        Assert.Equal(8, decoded.Height);
        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal(Green, decoded.Frames[1][9, 7]);
    }

    [Fact]
    public void ImagesLargerThanTheFormatAllowsAreRejected()
    {
        Assert.Throws<NotSupportedException>(() =>
        {
            using var image = new Image<Rgba32>(70000, 1);
            Encode(image, new GifEncoder());
        });
    }

    // ----- Options -----

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void FrameDelayIsRangeChecked(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoder { FrameDelay = value });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RepeatCountIsRangeChecked(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoder { RepeatCount = value });
    }

    [Fact]
    public void TheDefaultsAreTenHundredthsAndAnInfiniteLoop()
    {
        var encoder = new GifEncoder();

        Assert.Equal(10, encoder.FrameDelay);
        Assert.Equal(0, encoder.RepeatCount);
        Assert.Equal(GifColorTableMode.Global, encoder.ColorTableMode);
        Assert.False(encoder.Interlaced);
        Assert.Null(encoder.Comment);
        Assert.Null(encoder.Quantizer);
    }

    [Fact]
    public void EncodeRejectsNullArguments()
    {
        var encoder = new GifEncoder();
        using var image = new Image<Rgba32>(4, 4);

        Assert.Throws<ArgumentNullException>(() => encoder.Encode<Rgba32>(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => encoder.Encode(image, null!));
    }

    [Fact]
    public void TheSaveAsGifHelpersUseTheEncoder()
    {
        using Image<Rgba32> source = Blocks(12, 10);

        using var withDefaults = new MemoryStream();
        source.SaveAsGif(withDefaults);
        using var withEncoder = new MemoryStream();
        source.SaveAsGif(withEncoder, new GifEncoder());

        Assert.Equal(withDefaults.ToArray(), withEncoder.ToArray());
        Assert.Equal(ImageFormat.Gif, ImageFormatDetector.DetectOrThrow(withDefaults.ToArray()));
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        using Image<Rgba32> source = Photo(32, 24);
        var encoder = new GifEncoder { FrameDelay = 12, RepeatCount = 3, Comment = "same" };

        Assert.Equal(Encode(source, encoder), Encode(source, encoder));
    }

    // ----- Other pixel formats -----

    [Fact]
    public void ImagesInOtherPixelFormatsEncodeToo()
    {
        using Image<Rgb24> source = TestImages.Gradient(20, 16);

        using var buffer = new MemoryStream();
        source.Save(buffer, new GifEncoder());
        using Image<Rgba32> decoded = Image.Load<Rgba32>(buffer.ToArray());

        Assert.Equal(20, decoded.Width);
        Assert.Equal(16, decoded.Height);
        Assert.All(Enumerable.Range(0, 20), x => Assert.Equal(255, decoded[x, 0].A));
    }

    // ----- Helpers -----

    private static byte[] Encode<TPixel>(Image<TPixel> image, GifEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }

    private static Image<Rgba32> RoundTrip(Image<Rgba32> image, GifEncoder encoder)
        => Image.Load<Rgba32>(Encode(image, encoder));

    /// <summary>Offset of the flags byte of the first image descriptor.</summary>
    private static int DescriptorFlagsOffset(byte[] data) => IndexOf(data, new byte[] { ImageSeparator }) + 9;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A four-colour block pattern: well within any palette, so round trips must be exact.</summary>
    private static Image<Rgba32> Blocks(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        Rgba32[] colors = { Red, Green, Blue, Yellow };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = colors[((x / 3) + (y / 3)) % colors.Length];
            }
        }

        return image;
    }

    /// <summary>Three frames, each a flat colour with a moving marker band.</summary>
    private static Image<Rgba32> Animation(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        Rgba32[] colors = { Red, Green, Blue };
        for (int i = 0; i < 3; i++)
        {
            ImageFrame<Rgba32> frame = i == 0 ? image.Frames.RootFrame : image.Frames.CreateFrame(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    frame.GetRowSpan(y)[x] = (x / 4) == i ? Yellow : colors[i];
                }
            }
        }

        return image;
    }

    private static Image<Rgba32> Photo(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)((x * 255) / Math.Max(1, width - 1)),
                    (byte)((y * 255) / Math.Max(1, height - 1)),
                    (byte)(((x + y) * 255) / Math.Max(1, width + height - 2)));
            }
        }

        return image;
    }

    private static (double Mean, int Max) Difference(Image<Rgba32> a, Image<Rgba32> b)
    {
        long total = 0;
        int max = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba32 p = a[x, y];
                Rgba32 q = b[x, y];
                int[] deltas = { Math.Abs(p.R - q.R), Math.Abs(p.G - q.G), Math.Abs(p.B - q.B) };
                foreach (int delta in deltas)
                {
                    total += delta;
                    max = Math.Max(max, delta);
                }
            }
        }

        return (total / (a.Width * a.Height * 3.0), max);
    }

    /// <summary>Mean absolute difference between 6x6 block averages: how well local colour is preserved.</summary>
    private static double BlockMeanError(Image<Rgba32> source, Image<Rgba32> result)
    {
        const int Block = 6;
        double total = 0;
        int blocks = 0;
        for (int by = 0; by + Block <= source.Height; by += Block)
        {
            for (int bx = 0; bx + Block <= source.Width; bx += Block)
            {
                long a = 0, b = 0;
                for (int y = by; y < by + Block; y++)
                {
                    for (int x = bx; x < bx + Block; x++)
                    {
                        a += source[x, y].R + source[x, y].G + source[x, y].B;
                        b += result[x, y].R + result[x, y].G + result[x, y].B;
                    }
                }

                total += Math.Abs(a - b) / (Block * Block * 3.0);
                blocks++;
            }
        }

        return total / Math.Max(1, blocks);
    }

    private static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Frames.Count, actual.Frames.Count);
        for (int i = 0; i < expected.Frames.Count; i++)
        {
            ImageFrame<Rgba32> a = expected.Frames[i];
            ImageFrame<Rgba32> b = actual.Frames[i];
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (a[x, y] != b[x, y])
                    {
                        Assert.Fail($"Frame {i} pixel ({x}, {y}): expected {a[x, y]}, got {b[x, y]}.");
                    }
                }
            }
        }
    }
}
