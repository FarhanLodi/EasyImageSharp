using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The row-streaming PNG decoder, checked differentially against <see cref="PngDecoder"/> - itself checked
/// against Pillow's ground truth by <c>FixtureDecodeTests</c> - over the whole fixture corpus, truncations and
/// mutations, plus the limits, cancellation and stream-error contract.
/// </summary>
public class PngScanlineSourceTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<object[]> PngFixtures()
        => Directory.EnumerateFiles(FixtureRoot, "*.png", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".expected.png", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(FixtureRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(PngFixtures))]
    public void EveryPngFixture_StreamsExactlyAsTheBufferedDecoderDecodes(string fixture)
    {
        byte[] bytes = FixturePath.Read(fixture);
        AssertMatchesDecoder<Rgba32>(bytes, seekable: true);
        AssertMatchesDecoder<Rgba32>(bytes, seekable: false);
        AssertMatchesDecoder<Rgba64>(bytes, seekable: true);
        AssertMatchesDecoder<L8>(bytes, seekable: false);
    }

    [Theory]
    [InlineData("png/rgb8.png")]
    [InlineData("png/pal4.png")]
    [InlineData("png/gray16_trns_key.png")]
    [InlineData("png/rgb8_idat3.png")]
    [InlineData("png/rgb8_ancillary.png")]
    [InlineData("png/rgb8_idat_many_empty.png")]
    [InlineData("metadata/exif_pillow.png")]
    public void EveryTruncation_FailsOrSucceedsExactlyAsTheBufferedDecoder(string fixture)
    {
        byte[] bytes = FixturePath.Read(fixture);
        int step = Math.Max(1, bytes.Length / 400);
        for (int length = 0; length <= bytes.Length; length += step)
        {
            byte[] truncated = bytes.AsSpan(0, length).ToArray();
            AssertMatchesDecoder<Rgba32>(truncated, seekable: true);
            AssertMatchesDecoder<Rgba32>(truncated, seekable: false);
        }
    }

    [Theory]
    [InlineData("png/rgb8_97x61.png", 1)]
    [InlineData("png/pal8_trns.png", 2)]
    [InlineData("png/graya16.png", 3)]
    [InlineData("png/rgb8_ancillary.png", 4)]
    [InlineData("metadata/icc.png", 5)]
    public void MutatedInput_MatchesTheBufferedDecoderOrKeepsTheExceptionContract(string fixture, int seed)
    {
        byte[] bytes = FixturePath.Read(fixture);
        var random = new Random(seed);
        var options = new DecoderOptions { MaxPixels = 1 << 20 };
        for (int i = 0; i < 300; i++)
        {
            byte[] mutated = (byte[])bytes.Clone();
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
            {
                // Half of the flips land in the header region, where lengths, sizes and chunk types live.
                int position = random.Next(2) == 0 ? random.Next(8, Math.Min(200, mutated.Length)) : random.Next(8, mutated.Length);
                mutated[position] ^= (byte)(1 << random.Next(8));
            }

            AssertMatchesDecoder<Rgba32>(mutated, seekable: true, options);
            AssertContract<Rgba32>(mutated, options);
        }
    }

    [Fact]
    public void InterlacedAndAnimatedPngs_AreDeclinedSoTheCallerFallsBack()
    {
        foreach (string fixture in new[] { "png/rgb8_adam7.png", "apng/single_frame.png", "apng/loop_forever.png" })
        {
            using var stream = new MemoryStream(FixturePath.Read(fixture));
            Assert.False(PngScanlineSource.TryCreate(stream, DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
            Assert.Null(source);
        }
    }

    [Fact]
    public void NotAPng_IsDeclinedWithoutThrowing()
    {
        using var stream = new MemoryStream(FixturePath.Read("bmp/" + Directory.GetFiles(Path.Combine(FixtureRoot, "bmp"), "*.bmp")
            .Select(Path.GetFileName).First()));
        Assert.False(PngScanlineSource.TryCreate(stream, DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
        Assert.Null(source);
    }

    [Fact]
    public void SeekableStream_NotAtItsStart_DecodesRelativeToItsPosition()
    {
        byte[] png = FixturePath.Read("png/rgb8_idat3.png");
        byte[] padded = [.. Enumerable.Repeat((byte)0xAB, 123), .. png, .. Enumerable.Repeat((byte)0xCD, 50)];
        using var stream = new MemoryStream(padded) { Position = 123 };
        Assert.True(PngScanlineSource.TryCreate(stream, DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            using Image<Rgba32> expected = new PngDecoder().Decode<Rgba32>(png, DecoderOptions.Default);
            Assert.Equal(StreamingTestSupport.Pixels(expected), StreamingTestSupport.ReadAll(source!));
        }
    }

    [Fact]
    public void MetadataAfterTheImageData_IsKnownBeforeTheFirstRowOnASeekableStream_AndAfterTheLastOnAForwardOnlyOne()
    {
        byte[] text = Encoding.Latin1.GetBytes("Comment\0written after IDAT");
        byte[] png = StreamingTestSupport.AssemblePng(
            9, 7, 8, 6, StreamingTestSupport.RgbaScanlines(9, 7, seed: 11), after: [("tEXt", text)], idatChunkBytes: 40);

        using (var seekable = new MemoryStream(png))
        {
            Assert.True(PngScanlineSource.TryCreate(seekable, DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
            using (source)
            {
                Assert.Equal("written after IDAT", Assert.Single(source!.Metadata.GetPngMetadata().TextData).Value);
            }
        }

        var forwardOnly = new ForwardOnlyStream(png);
        Assert.True(PngScanlineSource.TryCreate(forwardOnly, DecoderOptions.Default, out IScanlineSource<Rgba32>? streamed));
        using (streamed)
        {
            Assert.Empty(streamed!.Metadata.GetPngMetadata().TextData);
            StreamingTestSupport.ReadAll(streamed);
            Assert.Equal("written after IDAT", Assert.Single(streamed.Metadata.GetPngMetadata().TextData).Value);
        }
    }

    [Fact]
    public void TransparencyAfterTheImageData_IsAppliedWhenSeekable_AndRejectedWhenItCanNoLongerBe()
    {
        // tRNS after IDAT breaks the chunk ordering rules, but the buffered decoder applies it; a forward-only
        // stream reaches it after the rows it would recolour have been produced.
        byte[] scanlines = new byte[4 * ((3 * 4) + 1)];
        for (int i = 0; i < scanlines.Length; i++)
        {
            scanlines[i] = i % 13 == 0 ? (byte)0 : (byte)(i * 7);
        }

        byte[] trns = [0, 7, 0, 14, 0, 21];
        byte[] png = StreamingTestSupport.AssemblePng(4, 4, 8, 2, scanlines, after: [("tRNS", trns)]);
        AssertMatchesDecoder<Rgba32>(png, seekable: true);

        Assert.True(PngScanlineSource.TryCreate(new ForwardOnlyStream(png), DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            InvalidImageContentException error = Assert.Throws<InvalidImageContentException>(() => StreamingTestSupport.ReadAll(source!));
            Assert.Contains("tRNS", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManySmallIdatChunksAndOddReadSizes_DecodeIdentically()
    {
        byte[] png = StreamingTestSupport.AssemblePng(
            37, 29, 8, 6, StreamingTestSupport.RgbaScanlines(37, 29, seed: 3), idatChunkBytes: 7);
        using Image<Rgba32> expected = new PngDecoder().Decode<Rgba32>(png, DecoderOptions.Default);
        foreach (int chunk in new[] { 1, 2, 3, 5, 8, 13, 4096 })
        {
            Assert.True(PngScanlineSource.TryCreate(new ForwardOnlyStream(png, chunk), DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
            using (source)
            {
                Assert.Equal(StreamingTestSupport.Pixels(expected), StreamingTestSupport.ReadAll(source!));
            }
        }
    }

    [Fact]
    public void DeclaredSizeOverMaxPixels_IsRejectedBeforeAnyRow()
    {
        byte[] png = StreamingTestSupport.AssemblePng(20, 20, 8, 6, StreamingTestSupport.RgbaScanlines(20, 20, seed: 1));
        var options = new DecoderOptions { MaxPixels = 399 };
        Assert.Throws<ImageSizeLimitExceededException>(
            () => PngScanlineSource.TryCreate(new MemoryStream(png), options, out IScanlineSource<Rgba32>? _));
        Assert.Throws<ImageSizeLimitExceededException>(
            () => PngScanlineSource.TryCreate(new ForwardOnlyStream(png), options, out IScanlineSource<Rgba32>? _));
    }

    [Fact]
    public void EncodedBytesOverMaxEncodedBytes_AreRejectedUpFrontWhenSeekable_AndAsTheyArriveOtherwise()
    {
        byte[] png = StreamingTestSupport.AssemblePng(64, 64, 8, 6, StreamingTestSupport.RgbaScanlines(64, 64, seed: 2));
        var options = new DecoderOptions { MaxEncodedBytes = png.Length - 1 };
        Assert.Throws<ImageSizeLimitExceededException>(
            () => PngScanlineSource.TryCreate(new MemoryStream(png), options, out IScanlineSource<Rgba32>? _));

        var forwardOnly = new ForwardOnlyStream(png, maxChunk: 100);
        Assert.Throws<ImageSizeLimitExceededException>(() =>
        {
            Assert.True(PngScanlineSource.TryCreate(forwardOnly, options, out IScanlineSource<Rgba32>? source));
            using (source)
            {
                StreamingTestSupport.ReadAll(source!);
            }
        });

        // Exactly at the limit is fine.
        var exact = new DecoderOptions { MaxEncodedBytes = png.Length };
        Assert.True(PngScanlineSource.TryCreate(new ForwardOnlyStream(png, 100), exact, out IScanlineSource<Rgba32>? fits));
        using (fits)
        {
            StreamingTestSupport.ReadAll(fits!);
        }
    }

    [Fact]
    public void Cancellation_StopsTheHeaderOrTheRows()
    {
        byte[] png = StreamingTestSupport.AssemblePng(50, 200, 8, 6, StreamingTestSupport.RgbaScanlines(50, 200, seed: 4), idatChunkBytes: 512);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var options = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token } };
            Assert.ThrowsAny<OperationCanceledException>(
                () => PngScanlineSource.TryCreate(new MemoryStream(png), options, out IScanlineSource<Rgba32>? _));
        }

        using var midway = new CancellationTokenSource();
        var stream = new ForwardOnlyStream(png, maxChunk: 256) { CancelAfterSource = midway, CancelAfter = png.Length / 2 };
        var midwayOptions = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = midway.Token } };
        Assert.True(PngScanlineSource.TryCreate(stream, midwayOptions, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            var row = new Rgba32[50];
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                while (source!.ReadRow(row))
                {
                }
            });

            Assert.InRange(source!.CurrentRow, 1, 199);

            // The token stays cancelled, so every later call is cancelled as well.
            Assert.ThrowsAny<OperationCanceledException>(() => source.ReadRow(row));
        }
    }

    [Fact]
    public void TheCallersStreamErrors_SurfaceUnwrapped_AndAFailedSourceRefusesMoreRows()
    {
        byte[] png = StreamingTestSupport.AssemblePng(40, 40, 8, 6, StreamingTestSupport.RgbaScanlines(40, 40, seed: 5), idatChunkBytes: 300);
        var failure = new IOException("network down");
        var stream = new ForwardOnlyStream(png, maxChunk: 64) { FailAfter = png.Length / 2, FailWith = failure };
        Assert.True(PngScanlineSource.TryCreate(stream, DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            var row = new Rgba32[40];
            IOException thrown = Assert.Throws<IOException>(() =>
            {
                while (source!.ReadRow(row))
                {
                }
            });

            Assert.Same(failure, thrown);

            // The failure struck inside a row, so the inflater is unusable: the source refuses to go on.
            Assert.Throws<InvalidOperationException>(() => source!.ReadRow(row));
        }

        var early = new ForwardOnlyStream(png) { FailAfter = 20, FailWith = failure };
        Assert.Same(failure, Assert.Throws<IOException>(() => PngScanlineSource.TryCreate(early, DecoderOptions.Default, out IScanlineSource<Rgba32>? _)));
    }

    [Fact]
    public void ADestinationShorterThanARow_IsAnArgumentError()
    {
        byte[] png = StreamingTestSupport.AssemblePng(8, 2, 8, 6, StreamingTestSupport.RgbaScanlines(8, 2, seed: 6));
        Assert.True(PngScanlineSource.TryCreate(new MemoryStream(png), DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            Assert.Throws<ArgumentException>(() => source!.ReadRow(new Rgba32[7]));
            Assert.True(source!.ReadRow(new Rgba32[8]));
        }

        Assert.Throws<ObjectDisposedException>(() => source!.ReadRow(new Rgba32[8]));
    }

    [Fact]
    public void DecodingRowByRow_AllocatesAFractionOfTheFrame()
    {
        const int Size = 1500;
        byte[] png = StreamingTestSupport.AssemblePng(Size, Size, 8, 6, StreamingTestSupport.RgbaScanlines(Size, Size, seed: 7), idatChunkBytes: 65536);
        var row = new Rgba32[Size];

        // Warm up the pools and the JIT once, then measure.
        for (int pass = 0; pass < 2; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(PngScanlineSource.TryCreate(new MemoryStream(png), DecoderOptions.Default, out IScanlineSource<Rgba32>? source));
            using (source)
            {
                while (source!.ReadRow(row))
                {
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (pass == 1)
            {
                // The frame alone is 9 000 000 bytes; the streamed decode holds a few scanlines.
                Assert.True(allocated < 1_000_000, $"Allocated {allocated:N0} bytes.");
            }
        }
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> both ways and requires the same outcome: the same pixels and metadata, or
    /// the same exception type. The streamed decode may decline (return false) only for data that is not a PNG or
    /// that is interlaced or animated, which the caller then hands to the buffered decoder anyway.
    /// </summary>
    internal static void AssertMatchesDecoder<TPixel>(byte[] bytes, bool seekable, DecoderOptions? options = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options ??= DecoderOptions.Default;
        Image<TPixel>? expected = null;
        Exception? expectedError = null;
        try
        {
            expected = new PngDecoder().Decode<TPixel>(bytes, options);
        }
        catch (Exception ex)
        {
            expectedError = ex;
        }

        using (expected)
        {
            bool created = false;
            TPixel[]? actual = null;
            ImageMetadata? metadata = null;
            Exception? actualError = null;
            try
            {
                Stream stream = seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes, maxChunk: 977);
                created = PngScanlineSource.TryCreate(stream, options, out IScanlineSource<TPixel>? source);
                if (created)
                {
                    using (source)
                    {
                        Assert.Equal(expected?.Width ?? source!.Width, source!.Width);
                        actual = StreamingTestSupport.ReadAll(source);
                        metadata = source.Metadata;
                    }
                }
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                actualError = ex;
            }

            if (actualError is not null)
            {
                Assert.True(
                    actualError is ImageFormatException or NotSupportedException,
                    $"Streaming threw {actualError.GetType().Name}: {actualError.Message}");
                if (!seekable && actualError.Message.Contains("tRNS chunk follows the image data", StringComparison.Ordinal))
                {
                    return; // The one documented difference of a forward-only stream.
                }

                Assert.True(expectedError is not null, $"Streaming threw '{actualError.Message}' where the decoder succeeded.");
                Assert.Equal(expectedError.GetType(), actualError.GetType());
                return;
            }

            if (!created)
            {
                Assert.True(
                    !HasPngSignature(bytes) || IsInterlaced(bytes) || HasAnimationControl(bytes),
                    "Only non-PNG, interlaced or animated data may be declined.");
                return;
            }

            Assert.True(expectedError is null, $"The decoder threw '{expectedError?.Message}' but streaming succeeded.");
            Assert.Equal(StreamingTestSupport.Pixels(expected!), actual);
            AssertSameMetadata(expected!.Metadata, metadata!);
        }
    }

    /// <summary>Streams on a forward-only, trickling stream and only checks the exception contract.</summary>
    private static void AssertContract<TPixel>(byte[] bytes, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        try
        {
            if (PngScanlineSource.TryCreate(new ForwardOnlyStream(bytes, maxChunk: 61), options, out IScanlineSource<TPixel>? source))
            {
                using (source)
                {
                    StreamingTestSupport.ReadAll(source!);
                }
            }
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
        }
    }

    private static void AssertSameMetadata(ImageMetadata expected, ImageMetadata actual)
    {
        Assert.Equal(expected.HorizontalResolution, actual.HorizontalResolution);
        Assert.Equal(expected.VerticalResolution, actual.VerticalResolution);
        Assert.Equal(expected.ResolutionUnits, actual.ResolutionUnits);
        Assert.Equal(expected.IccProfile?.ToByteArray(), actual.IccProfile?.ToByteArray());
        Assert.Equal(expected.XmpProfile?.ToByteArray(), actual.XmpProfile?.ToByteArray());
        Assert.Equal(expected.ExifProfile?.ToByteArray(), actual.ExifProfile?.ToByteArray());
        Assert.Same(expected.DecodedImageFormat, actual.DecodedImageFormat);
        PngMetadata left = expected.GetPngMetadata();
        PngMetadata right = actual.GetPngMetadata();
        Assert.Equal(left.ColorType, right.ColorType);
        Assert.Equal(left.BitDepth, right.BitDepth);
        Assert.Equal(left.Interlaced, right.Interlaced);
        Assert.Equal(left.Gamma, right.Gamma);
        Assert.Equal(left.IsAnimated, right.IsAnimated);
        Assert.Equal(left.TextData, right.TextData);
    }

    private static bool HasPngSignature(byte[] bytes)
        => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static bool IsInterlaced(byte[] bytes) => bytes.Length > 28 && bytes[28] == 1;

    private static bool HasAnimationControl(byte[] bytes) => bytes.AsSpan().IndexOf("acTL"u8) >= 0;
}
