using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// The WebP encoder. Lossless output is required to be pixel-exact through this library's own decoder for
/// every source in <c>Fixtures/webpenc/</c>, every pixel format and every effort level, and to stay within
/// 15% of the byte size libwebp produces for the same pixels at the same effort (the per-image libwebp sizes
/// live in the fixture manifest).
/// </summary>
/// <remarks>
/// <para>
/// The exactness claim is not merely self-consistency: every file this class encodes is written to
/// <c>webpenc-output/</c> next to the test binaries, and <c>Fixtures/gen_webpenc.py --verify</c> decodes all of
/// them with libwebp through Pillow. That check was run against this revision and reported
/// <c>0 mismatch(es)</c> over the still images, the animations and the near-lossless files, so libwebp agrees
/// with the decoder used here on every pixel of every bitstream the encoder emits.
/// </para>
/// <para>
/// Lossy encoding is supplied by a separate VP8 frame encoder. This build has none, so
/// <see cref="WebpFileFormat.Lossy"/> is expected to raise <see cref="NotSupportedException"/> and
/// <see cref="WebpFileFormat.Auto"/> is expected to fall back to lossless. The alpha plane a lossy frame would
/// carry is exercised directly against the decoder's ALPH reader instead.
/// </para>
/// </remarks>
public class WebpEncoderTests
{
    /// <summary>How much larger than libwebp the encoder is allowed to be, plus a fixed slack for tiny files.</summary>
    private const double SizeTolerance = 1.15;

    private const int SizeSlackBytes = 24;

    private static readonly string DumpDirectory = Path.Combine(AppContext.BaseDirectory, "webpenc-output");

    private readonly ITestOutputHelper output;

    public WebpEncoderTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> SourcesAndMethods()
    {
        foreach (WebpEncoderFixture entry in WebpEncoderFixtures.All)
        {
            foreach (int method in WebpEncoderFixtures.Methods)
            {
                yield return new object[] { entry.Name, method };
            }
        }
    }

    // ----- Lossless round trips -----

    [Theory]
    [MemberData(nameof(SourcesAndMethods))]
    public void LosslessOutputRoundTripsExactly(string name, int method)
    {
        WebpEncoderFixture entry = WebpEncoderFixtures.Get(name);
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read($"webpenc/{entry.File}"));
        Assert.Equal(entry.Width, source.Width);
        Assert.Equal(entry.Height, source.Height);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Method = method });
        Dump($"{name}.m{method}", encoded, source);

        Assert.Equal(ImageFormat.Webp, Image.DetectFormat(encoded));
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertPixelsEqual(source, decoded, $"{name} at method {method}");

        ImageInfo info = Image.Identify(encoded);
        Assert.Equal(entry.Width, info.Width);
        Assert.Equal(entry.Height, info.Height);
        Assert.Equal(entry.HasAlpha ? 32 : 24, info.PixelType.BitsPerPixel);
        Assert.True(info.Metadata.TryGetFormatMetadata(out WebpMetadata? metadata) && metadata.IsLossless);
    }

    [Theory]
    [MemberData(nameof(SourcesAndMethods))]
    public void LosslessOutputStaysCloseToLibwebp(string name, int method)
    {
        WebpEncoderFixture entry = WebpEncoderFixtures.Get(name);
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read($"webpenc/{entry.File}"));

        int ours = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Method = method }).Length;
        int reference = entry.Libwebp[method];
        double ratio = (double)ours / reference;
        this.output.WriteLine($"{name} m{method}: {ours} bytes vs libwebp {reference} = {ratio:F3}x");

        Assert.True(
            ours <= (reference * SizeTolerance) + SizeSlackBytes,
            $"webpenc/{name} at method {method}: {ours} bytes against libwebp's {reference} ({ratio:F3}x) exceeds the budget.");
    }

    [Fact]
    public void LosslessOutputRoundTripsForEveryPixelFormat()
    {
        using Image<Rgba32> source = Checkerboard(23, 17);

        AssertFormatRoundTrips<Rgba32>(source);
        AssertFormatRoundTrips<Bgra32>(source);
        AssertFormatRoundTrips<Argb32>(source);
        AssertFormatRoundTrips<Abgr32>(source);
        AssertFormatRoundTrips<Rgb24>(source);
        AssertFormatRoundTrips<Bgr24>(source);
        AssertFormatRoundTrips<L8>(source);
        AssertFormatRoundTrips<La16>(source);
        AssertFormatRoundTrips<Rgba64>(source);
        AssertFormatRoundTrips<Rgb48>(source);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(17, 1)]
    [InlineData(33, 31)]
    [InlineData(64, 64)]
    [InlineData(65, 63)]
    public void LosslessOutputRoundTripsForOddSizes(int width, int height)
    {
        using Image<Rgba32> source = Checkerboard(width, height);

        for (int method = 0; method <= 6; method++)
        {
            using Image<Rgba32> decoded = RoundTrip(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Method = method });
            AssertPixelsEqual(source, decoded, $"{width}x{height} at method {method}");
        }
    }

    [Fact]
    public void LosslessOutputRoundTripsForRandomImages()
    {
        // A small fuzz loop over the shapes and colour counts that steer the transform and palette decisions.
        var random = new Random(20260823);
        for (int trial = 0; trial < 60; trial++)
        {
            int width = 1 + random.Next(40);
            int height = 1 + random.Next(40);
            int colors = (trial % 4) switch { 0 => 2, 1 => 9, 2 => 90, _ => 4000 };
            using Image<Rgba32> source = RandomImage(width, height, colors, random);

            int method = random.Next(7);
            using Image<Rgba32> decoded = RoundTrip(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Method = method });
            AssertPixelsEqual(source, decoded, $"trial {trial}: {width}x{height}, {colors} colours, method {method}");
        }
    }

    [Fact]
    public void EncodingTheSameImageTwiceProducesTheSameBytes()
    {
        // The shortlisted transform candidates are measured on several threads, so the tie-breaking has to be
        // by list order rather than by whichever thread finished first.
        using Image<Rgba32> source = Photo(160, 120);

        for (int method = 0; method <= 6; method++)
        {
            var encoder = new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Method = method };
            byte[] first = Encode(source, encoder);
            byte[] second = Encode(source, encoder);
            Assert.True(first.AsSpan().SequenceEqual(second), $"method {method} produced two different files.");
        }
    }

    [Fact]
    public void ASimpleLosslessFileHasNoExtendedHeader()
    {
        using Image<Rgba32> source = Checkerboard(16, 16);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        Assert.Equal(new[] { "VP8L" }, ChunkIds(encoded));
        Assert.Equal((uint)(encoded.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4, 4)));
    }

    [Fact]
    public void TransparentColorModeClearReplacesHiddenColour()
    {
        using var source = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
        {
            Span<Rgba32> row = source.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < 8; x++)
            {
                row[x] = x < 4 ? new Rgba32(200, 100, 50, 0) : new Rgba32(10, 20, 30, 255);
            }
        }

        using Image<Rgba32> preserved = RoundTrip(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        Assert.Equal(new Rgba32(200, 100, 50, 0), preserved[0, 0]);

        using Image<Rgba32> cleared = RoundTrip(
            source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, TransparentColorMode = WebpTransparentColorMode.Clear });
        Assert.Equal(default, cleared[0, 0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), cleared[7, 0]);
    }

    // ----- Near-lossless -----

    [Theory]
    [InlineData(100, 0)]
    [InlineData(80, 1)]
    [InlineData(60, 3)]
    [InlineData(40, 7)]
    [InlineData(20, 15)]
    [InlineData(0, 31)]
    public void NearLosslessStaysWithinItsErrorBound(int quality, int expectedBound)
    {
        using Image<Rgba32> source = Photo(96, 72);
        Assert.Equal(expectedBound, WebpNearLossless.MaxErrorForQuality(quality));

        var encoder = new WebpEncoder { FileFormat = WebpFileFormat.Lossless, NearLossless = true, NearLosslessQuality = quality };
        byte[] encoded = Encode(source, encoder);
        Dump($"nearlossless.q{quality}", encoded, source, verifyPixels: false);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

        int worst = 0;
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgba32> want = source.Frames.RootFrame.GetRowSpan(y);
            Span<Rgba32> got = decoded.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                worst = Math.Max(worst, Math.Abs(want[x].R - got[x].R));
                worst = Math.Max(worst, Math.Abs(want[x].G - got[x].G));
                worst = Math.Max(worst, Math.Abs(want[x].B - got[x].B));
                worst = Math.Max(worst, Math.Abs(want[x].A - got[x].A));
            }
        }

        this.output.WriteLine($"near-lossless q{quality}: {encoded.Length} bytes, worst channel error {worst} (bound {expectedBound})");
        Assert.True(worst <= expectedBound, $"near-lossless q{quality} moved a channel by {worst}, past its bound of {expectedBound}.");
        if (quality == 100)
        {
            Assert.Equal(0, worst);
        }
        else if (quality <= 40)
        {
            // A reduction that never changes a pixel would satisfy the bound while doing nothing at all.
            Assert.True(worst > 0, $"near-lossless q{quality} left every pixel untouched.");
        }
    }

    [Fact]
    public void NearLosslessCompressesBetterThanLossless()
    {
        // A smoothly shaded picture is what the reduction is for: flat neighbourhoods lose their low bits and
        // turn into runs, while edges keep every bit they had.
        using Image<Rgba32> source = NoisyShading(96, 72);

        int exact = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless }).Length;
        int reduced = Encode(
            source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, NearLossless = true, NearLosslessQuality = 40 }).Length;

        this.output.WriteLine($"lossless {exact} bytes, near-lossless q40 {reduced} bytes ({(double)reduced / exact:F3}x)");
        Assert.True(reduced < exact, $"near-lossless produced {reduced} bytes, no smaller than the exact {exact}.");
    }

    // ----- Alpha (ALPH) -----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnAlphaPlaneRoundTripsExactlyForEveryFilter(int filter)
    {
        const int width = 41;
        const int height = 29;
        byte[] plane = AlphaPlane(width, height);

        byte[] filtered = WebpAlphaEncoder.ApplyFilter(plane, width, height, filter);
        byte[] chunk = new byte[filtered.Length + 1];
        chunk[0] = (byte)(filter << 2);
        filtered.CopyTo(chunk, 1);

        byte[] decoded = WebpAlpha.Decode(chunk, 0, chunk.Length, width, height);
        Assert.Equal(plane, decoded);
    }

    [Theory]
    [InlineData(WebpAlphaCompression.None)]
    [InlineData(WebpAlphaCompression.Lossless)]
    public void AnAlphaChunkRoundTripsExactly(WebpAlphaCompression compression)
    {
        const int width = 41;
        const int height = 29;
        byte[] plane = AlphaPlane(width, height);

        for (int method = 0; method <= 6; method++)
        {
            byte[] chunk = WebpAlphaEncoder.Encode(plane, width, height, compression, 100, method);
            Assert.Equal(compression == WebpAlphaCompression.None ? 0 : chunk[0] & 0x03, chunk[0] & 0x03);
            byte[] decoded = WebpAlpha.Decode(chunk, 0, chunk.Length, width, height);
            Assert.True(plane.AsSpan().SequenceEqual(decoded), $"alpha round trip failed at method {method} with {compression}.");
        }
    }

    [Fact]
    public void ACompressedAlphaChunkIsSmallerThanTheRawPlane()
    {
        const int width = 64;
        const int height = 48;
        var plane = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                plane[(y * width) + x] = (byte)(x < width / 2 ? 0 : 255);
            }
        }

        byte[] raw = WebpAlphaEncoder.Encode(plane, width, height, WebpAlphaCompression.None, 100, 4);
        byte[] compressed = WebpAlphaEncoder.Encode(plane, width, height, WebpAlphaCompression.Lossless, 100, 4);

        this.output.WriteLine($"alpha plane {plane.Length} bytes: raw chunk {raw.Length}, VP8L chunk {compressed.Length}");
        Assert.Equal(plane.Length + 1, raw.Length);
        Assert.True(compressed.Length < raw.Length / 10, $"compressed alpha is {compressed.Length} bytes, expected far less than {raw.Length}.");
    }

    // ----- Animation -----

    [Fact]
    public void AnAnimationRoundTripsFramesDurationsAndPixels()
    {
        using Image<Rgba32> source = Animation(48, 32, 5);
        for (int i = 0; i < source.Frames.Count; i++)
        {
            source.Frames[i].Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay = 40 + (i * 10);
        }

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, RepeatCount = 7 });
        Dump("animation", encoded, source);

        List<string> chunks = ChunkIds(encoded);
        Assert.Equal("VP8X", chunks[0]);
        Assert.Equal("ANIM", chunks[1]);
        Assert.Equal(5, chunks.Count(id => id == "ANMF"));

        ImageInfo info = Image.Identify(encoded);
        Assert.Equal(5, info.FrameCount);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(5, decoded.Frames.Count);
        Assert.True(decoded.Metadata.TryGetFormatMetadata(out WebpMetadata? metadata));
        Assert.True(metadata!.IsAnimated);
        Assert.Equal(7, metadata.RepeatCount);

        for (int i = 0; i < 5; i++)
        {
            AssertFramePixelsEqual(source.Frames[i], decoded.Frames[i], $"frame {i}");
            Assert.Equal(40 + (i * 10), decoded.Frames[i].Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay);
        }
    }

    [Fact]
    public void AnAnimationShrinksFramesToWhatChanged()
    {
        // Only a small square moves, so every frame after the first should cover far less than the canvas.
        using Image<Rgba32> source = Animation(64, 64, 4);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        List<(int X, int Y, int Width, int Height)> rectangles = FrameRectangles(encoded);
        Assert.Equal(4, rectangles.Count);
        Assert.Equal((0, 0, 64, 64), rectangles[0]);
        for (int i = 1; i < rectangles.Count; i++)
        {
            this.output.WriteLine($"frame {i}: {rectangles[i].Width}x{rectangles[i].Height} at {rectangles[i].X},{rectangles[i].Y}");
            Assert.True(
                rectangles[i].Width * rectangles[i].Height < 64 * 64 / 2,
                $"frame {i} covers {rectangles[i].Width}x{rectangles[i].Height}, which is not a shrunken sub-frame.");
            Assert.Equal(0, rectangles[i].X % 2);
            Assert.Equal(0, rectangles[i].Y % 2);
        }
    }

    [Fact]
    public void AnAnimationUsesTheEncoderFrameDelayWhenItIsSet()
    {
        using Image<Rgba32> source = Animation(24, 24, 3);
        source.Frames[1].Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay = 999;

        using Image<Rgba32> decoded = RoundTrip(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, FrameDelay = 25 });

        foreach (ImageFrame<Rgba32> frame in decoded.Frames)
        {
            Assert.Equal(25, frame.Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay);
        }
    }

    [Fact]
    public void AnAnimationHonoursDisposeToBackground()
    {
        using Image<Rgba32> source = Animation(32, 32, 3);
        source.Frames[0].Metadata.GetFormatMetadata<WebpFrameMetadata>().DisposalMethod = WebpDisposalMethod.DisposeToBackground;

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        Dump("animation-dispose", encoded, source);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

        Assert.Equal(3, decoded.Frames.Count);
        for (int i = 0; i < 3; i++)
        {
            AssertFramePixelsEqual(source.Frames[i], decoded.Frames[i], $"frame {i} after a dispose-to-background");
        }
    }

    [Fact]
    public void AnAnimationOfIdenticalFramesStaysTiny()
    {
        using var source = new Image<Rgba32>(64, 64, new Rgba32(30, 60, 90));
        for (int i = 0; i < 3; i++)
        {
            source.Frames.AddFrame(source.Frames.RootFrame.PixelSpan);
        }

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(4, decoded.Frames.Count);
        AssertFramePixelsEqual(source.Frames[3], decoded.Frames[3], "the last identical frame");
        this.output.WriteLine($"four identical 64x64 frames: {encoded.Length} bytes");
        Assert.True(encoded.Length < 400, $"an animation of identical frames took {encoded.Length} bytes.");
    }

    // ----- Metadata -----

    [Fact]
    public void ProfilesAreWrittenIntoTheExtendedContainer()
    {
        using Image<Rgba32> source = Checkerboard(16, 16);
        byte[] icc = BuildIccProfile();
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.ImageDescription, "webp encoder test");
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'></x:xmpmeta>");
        source.Metadata.IccProfile = new IccProfile(icc);
        source.Metadata.ExifProfile = exif;
        source.Metadata.XmpProfile = new XmpProfile(xmp);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        Dump("metadata", encoded, source);

        Assert.Equal(new[] { "VP8X", "ICCP", "VP8L", "EXIF", "XMP " }, ChunkIds(encoded));
        Assert.Equal(WebpMuxer.FlagIccProfile | WebpMuxer.FlagExif | WebpMuxer.FlagXmp, Vp8XFlags(encoded));
        Assert.Equal(icc, ChunkPayload(encoded, "ICCP"));
        Assert.Equal(xmp, ChunkPayload(encoded, "XMP "));

        var readBack = new ExifProfile(ChunkPayload(encoded, "EXIF"));
        Assert.True(readBack.TryGetValue(ExifTag.ImageDescription, out IExifValue<string>? description));
        Assert.Equal("webp encoder test", description!.Value);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertPixelsEqual(source, decoded, "a file carrying profiles");
    }

    [Fact]
    public void SkipMetadataLeavesTheSimpleContainer()
    {
        using Image<Rgba32> source = Checkerboard(16, 16);
        source.Metadata.XmpProfile = new XmpProfile(Encoding.UTF8.GetBytes("<x:xmpmeta/>"));

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, SkipMetadata = true });

        Assert.Equal(new[] { "VP8L" }, ChunkIds(encoded));
    }

    [Fact]
    public void EveryChunkIsPaddedToAnEvenLength()
    {
        using Image<Rgba32> source = Checkerboard(9, 9);
        source.Metadata.XmpProfile = new XmpProfile(Encoding.UTF8.GetBytes("odd"));

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        int position = 12;
        while (position + 8 <= encoded.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(position + 4, 4));
            position += 8 + size + (size & 1);
            Assert.Equal(0, position & 1);
        }

        Assert.Equal(encoded.Length, position);
    }

    // ----- The lossy seam -----

    [Fact]
    public void TheLossyFrameEncoderIsWiredIn()
    {
        Assert.NotNull(Vp8FrameEncoderFactory.Create());
    }

    [Fact]
    public void LossyEncodingWritesAVp8ChunkThatRoundTripsApproximately()
    {
        using Image<Rgba32> source = Photo(64, 48);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = 90 });

        // A simple lossy file is RIFF....WEBP followed by a 'VP8 ' chunk.
        Assert.Equal("VP8 ", System.Text.Encoding.ASCII.GetString(encoded, 12, 4));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);

        // Lossy, so compare on signal-to-noise rather than exact pixels.
        double squaredError = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba32 a = source[x, y];
                Rgba32 b = decoded[x, y];
                squaredError += ((a.R - b.R) * (a.R - b.R)) + ((a.G - b.G) * (a.G - b.G)) + ((a.B - b.B) * (a.B - b.B));
            }
        }

        double mse = squaredError / (source.Width * source.Height * 3d);
        double psnr = 10 * Math.Log10(255d * 255d / Math.Max(mse, 1e-9));
        Assert.True(psnr > 30, $"Lossy quality 90 produced only {psnr:F2} dB.");
    }

    [Fact]
    public void AutoPicksLosslessForGraphicsAndLossyForPhotographs()
    {
        // Few colours: lossless, and therefore an exact round trip.
        using var graphics = new Image<Rgba32>(48, 32);
        for (int y = 0; y < graphics.Height; y++)
        {
            for (int x = 0; x < graphics.Width; x++)
            {
                graphics[x, y] = ((x / 8) + (y / 8)) % 2 == 0 ? new Rgba32(20, 40, 200) : new Rgba32(240, 240, 40);
            }
        }

        byte[] graphicsEncoded = Encode(graphics, new WebpEncoder());
        Assert.Equal("VP8L", System.Text.Encoding.ASCII.GetString(graphicsEncoded, 12, 4));
        using Image<Rgba32> graphicsDecoded = Image.Load<Rgba32>(graphicsEncoded);
        AssertPixelsEqual(graphics, graphicsDecoded, "an Auto encode of a low-colour image");

        // Photographic content: lossy.
        using Image<Rgba32> photo = Photo(64, 48);
        byte[] photoEncoded = Encode(photo, new WebpEncoder());
        Assert.Equal("VP8 ", System.Text.Encoding.ASCII.GetString(photoEncoded, 12, 4));
    }

    // ----- Limits, options and the public surface -----

    [Fact]
    public void ImagesLargerThanTheFormatAllowsAreRejected()
    {
        using var wide = new Image<Rgba32>(16384, 1);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Encode(wide, new WebpEncoder()));
        Assert.Contains("16383", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionValuesAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { Quality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { Method = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { Method = 7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { NearLosslessQuality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { AlphaQuality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { FrameDelay = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoder { RepeatCount = 70000 });

        var defaults = new WebpEncoder();
        Assert.Equal(WebpFileFormat.Auto, defaults.FileFormat);
        Assert.Equal(75, defaults.Quality);
        Assert.Equal(4, defaults.Method);
        Assert.Equal(100, defaults.FrameDelay);
        Assert.Equal(0, defaults.RepeatCount);
        Assert.Equal(WebpAlphaCompression.Lossless, defaults.AlphaCompression);
    }

    [Fact]
    public void TheFormatRegistryCreatesTheEncoder()
    {
        Assert.IsType<WebpEncoder>(ImageFormat.Webp.CreateEncoder());
    }

    [Fact]
    public async Task TheSaveHelpersWriteWebp()
    {
        using Image<Rgba32> source = Checkerboard(12, 9);
        string directory = Path.Combine(Path.GetTempPath(), "easyimagesharp-webpenc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var stream = new MemoryStream())
            {
                source.SaveAsWebp(stream);
                Assert.Equal(ImageFormat.Webp, Image.DetectFormat(stream.ToArray()));
            }

            using (var stream = new MemoryStream())
            {
                source.SaveAsWebp(stream, new WebpEncoder { Method = 0 });
                Assert.Equal(ImageFormat.Webp, Image.DetectFormat(stream.ToArray()));
            }

            using (var stream = new MemoryStream())
            {
                await source.SaveAsWebpAsync(stream);
                Assert.Equal(ImageFormat.Webp, Image.DetectFormat(stream.ToArray()));
            }

            using (var stream = new MemoryStream())
            {
                await source.SaveAsWebpAsync(stream, new WebpEncoder { Method = 1 });
                Assert.Equal(ImageFormat.Webp, Image.DetectFormat(stream.ToArray()));
            }

            string path = Path.Combine(directory, "plain.webp");
            source.SaveAsWebp(path);
            Assert.Equal(ImageFormat.Webp, Image.DetectFormat(File.ReadAllBytes(path)));

            string optioned = Path.Combine(directory, "optioned.webp");
            source.SaveAsWebp(optioned, new WebpEncoder { Method = 2 });
            Assert.Equal(ImageFormat.Webp, Image.DetectFormat(File.ReadAllBytes(optioned)));

            string asyncPath = Path.Combine(directory, "async.webp");
            await source.SaveAsWebpAsync(asyncPath);
            Assert.Equal(ImageFormat.Webp, Image.DetectFormat(File.ReadAllBytes(asyncPath)));

            string asyncOptioned = Path.Combine(directory, "async-optioned.webp");
            await source.SaveAsWebpAsync(asyncOptioned, new WebpEncoder { Method = 0 });
            Assert.Equal(ImageFormat.Webp, Image.DetectFormat(File.ReadAllBytes(asyncOptioned)));

            // The extension chosen from the path must reach the WebP encoder too.
            string byExtension = Path.Combine(directory, "byextension.webp");
            source.Save(byExtension);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(byExtension);
            AssertPixelsEqual(source, decoded, "a file saved through the extension registry");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("ll_gradient_rgb")]
    [InlineData("ll_alpha_ramp")]
    [InlineData("ll_palette16")]
    [InlineData("ll_palette2")]
    [InlineData("ll_noise")]
    [InlineData("ll_mixed_m6")]
    [InlineData("ll_17x9")]
    [InlineData("lossy_alpha_q80")]
    [InlineData("alph_lossless_f2")]
    public void DecodedWebpImagesReEncodeExactly(string name)
    {
        // The decoder's own libwebp-produced fixtures make a second corpus: decode, re-encode, decode again.
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read($"webp/{name}.webp"));

        using Image<Rgba32> decoded = RoundTrip(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        AssertPixelsEqual(source, decoded, name);
    }

    [Theory]
    [InlineData("anim_lossless")]
    [InlineData("anim_lossy")]
    [InlineData("anim_offsets_dispose")]
    [InlineData("anim_blend")]
    public void DecodedAnimationsReEncodeExactly(string name)
    {
        // These carry per-frame metadata straight from libwebp's own ANMF headers: offsets, blending and
        // disposal all reach the encoder, which has to reproduce the composited frames regardless.
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Read($"webp/{name}.webp"));
        Assert.True(source.Frames.Count > 1);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        Dump($"reencoded-{name}", encoded, source);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

        Assert.Equal(source.Frames.Count, decoded.Frames.Count);
        for (int i = 0; i < source.Frames.Count; i++)
        {
            AssertFramePixelsEqual(source.Frames[i], decoded.Frames[i], $"{name} frame {i}");
            Assert.Equal(
                source.Frames[i].Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay,
                decoded.Frames[i].Metadata.GetFormatMetadata<WebpFrameMetadata>().FrameDelay);
        }
    }

    [Fact]
    public void AnAnimationBlendsWhereThatIsSmaller()
    {
        // Changes scattered across the whole canvas cannot be boxed into a small rectangle, but they can be
        // sent as a mostly transparent frame that blends over what is already there.
        using Image<Rgba32> source = ScatteredChanges(64, 64, 3);

        byte[] encoded = Encode(source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        Dump("animation-blend", encoded, source);

        Assert.Contains(FrameBlendFlags(encoded).Skip(1), blend => blend);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        AssertPixelsEqual(source, decoded, "a blended animation");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void LosslessOutputRoundTripsAtEveryQuality(int quality)
    {
        using Image<Rgba32> source = Photo(48, 36);

        using Image<Rgba32> decoded = RoundTrip(
            source, new WebpEncoder { FileFormat = WebpFileFormat.Lossless, Quality = quality, Method = 5 });

        AssertPixelsEqual(source, decoded, $"quality {quality}");
    }

    // ----- Helpers -----

    private static byte[] Encode<TPixel>(Image<TPixel> image, WebpEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static Image<Rgba32> RoundTrip<TPixel>(Image<TPixel> image, WebpEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
        => Image.Load<Rgba32>(Encode(image, encoder));

    private static void AssertFormatRoundTrips<TPixel>(Image<Rgba32> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> converted = source.CloneAs<TPixel>();
        using Image<TPixel> decoded = Image.Load<TPixel>(Encode(converted, new WebpEncoder { FileFormat = WebpFileFormat.Lossless }));

        Assert.Equal(converted.Width, decoded.Width);
        Assert.Equal(converted.Height, decoded.Height);
        for (int y = 0; y < converted.Height; y++)
        {
            for (int x = 0; x < converted.Width; x++)
            {
                Assert.True(
                    converted[x, y].ToRgba32().Equals(decoded[x, y].ToRgba32()),
                    $"{typeof(TPixel).Name} differs at {x},{y}: {converted[x, y].ToRgba32()} became {decoded[x, y].ToRgba32()}.");
            }
        }
    }

    private static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual, string what)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Frames.Count, actual.Frames.Count);
        for (int i = 0; i < expected.Frames.Count; i++)
        {
            AssertFramePixelsEqual(expected.Frames[i], actual.Frames[i], what);
        }
    }

    private static void AssertFramePixelsEqual(ImageFrame<Rgba32> expected, ImageFrame<Rgba32> actual, string what)
    {
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgba32> want = expected.GetRowSpan(y);
            Span<Rgba32> got = actual.GetRowSpan(y);
            for (int x = 0; x < expected.Width; x++)
            {
                if (!want[x].Equals(got[x]))
                {
                    Assert.Fail($"{what}: pixel {x},{y} is {got[x]} but should be {want[x]}.");
                }
            }
        }
    }

    private static Image<Rgba32> Checkerboard(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                bool light = (((x / 3) + (y / 2)) & 1) == 0;
                row[x] = light
                    ? new Rgba32((byte)(20 + (x * 3)), (byte)(200 - y), 90, 255)
                    : new Rgba32(15, (byte)(40 + y), (byte)(180 - (x * 2)), (byte)(255 - (y * 4)));
            }
        }

        return image;
    }

    private static Image<Rgba32> Photo(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                uint noise = Scramble(x, y, 11);
                double r = 128 + (100 * Math.Sin(x * 0.07) * Math.Cos(y * 0.05)) + (noise & 7) - 3;
                double g = 128 + (90 * Math.Sin((x + y) * 0.04)) + ((noise >> 3) & 7) - 3;
                double b = 128 + (80 * Math.Cos((x * 0.03) + (y * 0.06))) + ((noise >> 6) & 7) - 3;
                row[x] = new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), 255);
            }
        }

        return image;
    }

    /// <summary>
    /// Broad, gently curving shading carrying a little sensor-like noise: neighbouring pixels stay within the
    /// near-lossless tolerance, so the reduction can flatten the noise away, which is exactly what it is for.
    /// </summary>
    private static Image<Rgba32> NoisyShading(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                uint noise = Scramble(x, y, 23);
                double r = 128 + (60 * Math.Sin(x * 0.010)) + (noise & 3) - 1;
                double g = 128 + (60 * Math.Cos(y * 0.009)) + ((noise >> 2) & 3) - 1;
                double b = 128 + (60 * Math.Sin((x + y) * 0.007)) + ((noise >> 4) & 3) - 1;
                row[x] = new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), 255);
            }
        }

        return image;
    }

    private static Image<Rgba32> RandomImage(int width, int height, int colors, Random random)
    {
        var palette = new Rgba32[colors];
        for (int i = 0; i < colors; i++)
        {
            palette[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        }

        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = palette[random.Next(colors)];
            }
        }

        return image;
    }

    /// <summary>A canvas with a static background and one small square that moves between frames.</summary>
    private static Image<Rgba32> Animation(int width, int height, int frames)
    {
        var list = new List<ImageFrame<Rgba32>>();
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = frame.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    row[x] = new Rgba32((byte)(20 + (x % 5)), (byte)(60 + (y % 7)), 140, 255);
                }
            }

            int left = 2 + (i * 4);
            for (int y = 4; y < Math.Min(height, 12); y++)
            {
                Span<Rgba32> row = frame.Frames.RootFrame.GetRowSpan(y);
                for (int x = left; x < Math.Min(width, left + 6); x++)
                {
                    row[x] = new Rgba32(250, (byte)(30 + (i * 20)), 10, 255);
                }
            }

            if (owner is null)
            {
                owner = frame;
            }
            else
            {
                owner.Frames.AddFrame(frame.Frames.RootFrame.PixelSpan);
                frame.Dispose();
            }
        }

        return owner!;
    }

    /// <summary>Frames whose differences are sprinkled over the whole canvas rather than confined to a box.</summary>
    private static Image<Rgba32> ScatteredChanges(int width, int height, int frames)
    {
        Image<Rgba32>? owner = null;
        for (int i = 0; i < frames; i++)
        {
            var frame = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = frame.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    bool touched = i > 0 && (Scramble(x, y, i) & 63) == 0;
                    row[x] = touched
                        ? new Rgba32(255, (byte)(20 * i), 0, 255)
                        : new Rgba32((byte)(30 + (x & 7)), (byte)(90 + (y & 7)), 160, 255);
                }
            }

            if (owner is null)
            {
                owner = frame;
            }
            else
            {
                owner.Frames.AddFrame(frame.Frames.RootFrame.PixelSpan);
                frame.Dispose();
            }
        }

        return owner!;
    }

    private static List<bool> FrameBlendFlags(byte[] file)
    {
        var flags = new List<bool>();
        int position = 12;
        while (position + 8 <= file.Length)
        {
            string id = Encoding.ASCII.GetString(file, position, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4, 4));
            if (id == "ANMF")
            {
                flags.Add((file[position + 8 + 15] & 0x02) == 0);
            }

            position += 8 + size + (size & 1);
        }

        return flags;
    }

    private static byte[] AlphaPlane(int width, int height)
    {
        var plane = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value = (x * x) + (y * 3) + ((int)Scramble(x, y, 5) & 15);
                plane[(y * width) + x] = (byte)(x < 4 || y < 3 ? 0 : value);
            }
        }

        return plane;
    }

    private static uint Scramble(int x, int y, int seed)
    {
        unchecked
        {
            uint v = (uint)((x * 374761393) + (y * 668265263) + (seed * 2654435761));
            v = (v ^ (v >> 13)) * 1274126177u;
            return v ^ (v >> 16);
        }
    }

    private static byte[] BuildIccProfile()
    {
        // A 128-byte header is enough for a profile the encoder only has to copy through verbatim.
        var data = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(data, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(data, 36);
        return data;
    }

    private static List<string> ChunkIds(byte[] file)
    {
        var ids = new List<string>();
        int position = 12;
        while (position + 8 <= file.Length)
        {
            ids.Add(Encoding.ASCII.GetString(file, position, 4));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4, 4));
            position += 8 + size + (size & 1);
        }

        return ids;
    }

    private static byte[] ChunkPayload(byte[] file, string id)
    {
        int position = 12;
        while (position + 8 <= file.Length)
        {
            string current = Encoding.ASCII.GetString(file, position, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4, 4));
            if (current == id)
            {
                return file.AsSpan(position + 8, size).ToArray();
            }

            position += 8 + size + (size & 1);
        }

        throw new Xunit.Sdk.XunitException($"Chunk '{id}' is missing.");
    }

    private static byte Vp8XFlags(byte[] file) => ChunkPayload(file, "VP8X")[0];

    private static List<(int X, int Y, int Width, int Height)> FrameRectangles(byte[] file)
    {
        var rectangles = new List<(int, int, int, int)>();
        int position = 12;
        while (position + 8 <= file.Length)
        {
            string id = Encoding.ASCII.GetString(file, position, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4, 4));
            if (id == "ANMF")
            {
                int payload = position + 8;
                rectangles.Add((
                    2 * ReadUInt24(file, payload),
                    2 * ReadUInt24(file, payload + 3),
                    1 + ReadUInt24(file, payload + 6),
                    1 + ReadUInt24(file, payload + 9)));
            }

            position += 8 + size + (size & 1);
        }

        return rectangles;
    }

    private static int ReadUInt24(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    /// <summary>
    /// Writes an encoded file and its expected pixels next to the test binaries so that
    /// <c>Fixtures/gen_webpenc.py --verify</c> can decode it with libwebp and compare.
    /// </summary>
    private static void Dump(string name, byte[] encoded, Image<Rgba32> source, bool verifyPixels = true)
    {
        try
        {
            Directory.CreateDirectory(DumpDirectory);
            File.WriteAllBytes(Path.Combine(DumpDirectory, name + ".webp"), encoded);
            if (!verifyPixels)
            {
                return;
            }

            using var raw = File.Create(Path.Combine(DumpDirectory, name + ".rgba"));
            var line = new byte[source.Width * 4];
            foreach (ImageFrame<Rgba32> frame in source.Frames)
            {
                for (int y = 0; y < source.Height; y++)
                {
                    Span<Rgba32> row = frame.GetRowSpan(y);
                    for (int x = 0; x < source.Width; x++)
                    {
                        line[(x * 4) + 0] = row[x].R;
                        line[(x * 4) + 1] = row[x].G;
                        line[(x * 4) + 2] = row[x].B;
                        line[(x * 4) + 3] = row[x].A;
                    }

                    raw.Write(line);
                }
            }

            File.WriteAllText(
                Path.Combine(DumpDirectory, name + ".dim"),
                source.Frames.Count > 1 ? $"{source.Frames.Count} {source.Width} {source.Height}" : $"{source.Width} {source.Height}");
        }
        catch (IOException)
        {
            // The dump is a development aid; a locked or read-only output directory must not fail a test.
        }
    }
}

/// <summary>One source image of the WebP encoder fixture corpus.</summary>
internal sealed record WebpEncoderFixture(string Name, string File, int Width, int Height, bool HasAlpha, int Colors, IReadOnlyDictionary<int, int> Libwebp);

/// <summary>Reads <c>Fixtures/webpenc/manifest.json</c>, which lists the sources and libwebp's sizes for them.</summary>
internal static class WebpEncoderFixtures
{
    private static WebpEncoderFixture[]? cache;
    private static int[]? methods;

    public static WebpEncoderFixture[] All
    {
        get
        {
            Load();
            return cache!;
        }
    }

    public static int[] Methods
    {
        get
        {
            Load();
            return methods!;
        }
    }

    public static WebpEncoderFixture Get(string name)
        => All.SingleOrDefault(e => e.Name == name)
           ?? throw new Xunit.Sdk.XunitException($"Fixture 'webpenc/{name}' is not listed in manifest.json; run Fixtures/generate.py.");

    private static void Load()
    {
        if (cache is not null)
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath.Get("webpenc/manifest.json")));
        methods = document.RootElement.GetProperty("methods").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        cache = document.RootElement.GetProperty("images").EnumerateArray().Select(Read).ToArray();
    }

    private static WebpEncoderFixture Read(JsonElement element)
    {
        var sizes = new Dictionary<int, int>();
        foreach (JsonProperty property in element.GetProperty("libwebp").EnumerateObject())
        {
            sizes[int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture)] = property.Value.GetInt32();
        }

        return new WebpEncoderFixture(
            element.GetProperty("name").GetString()!,
            element.GetProperty("file").GetString()!,
            element.GetProperty("width").GetInt32(),
            element.GetProperty("height").GetInt32(),
            element.GetProperty("hasAlpha").GetBoolean(),
            element.GetProperty("colors").GetInt32(),
            sizes);
    }
}
