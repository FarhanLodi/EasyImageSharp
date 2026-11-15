using System.IO.Compression;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The row-streaming PNG encoder. For every option it accepts it must write exactly what <see cref="PngEncoder"/>
/// writes - the same chunks in the same order and the same compressed bytes - except that the image data is split
/// into IDAT chunks as it is produced instead of being written as one.
/// </summary>
public class PngScanlineSinkTests
{
    public static TheoryData<PngColorType?, PngBitDepth?, PngFilterMethod> Formats()
    {
        var data = new TheoryData<PngColorType?, PngBitDepth?, PngFilterMethod>();
        foreach (PngFilterMethod filter in Enum.GetValues<PngFilterMethod>())
        {
            data.Add(null, null, filter);
        }

        foreach ((PngColorType type, PngBitDepth depth) in new[]
        {
            (PngColorType.Grayscale, PngBitDepth.Bit8), (PngColorType.Grayscale, PngBitDepth.Bit16),
            (PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit8), (PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit16),
            (PngColorType.Rgb, PngBitDepth.Bit8), (PngColorType.Rgb, PngBitDepth.Bit16),
            (PngColorType.RgbWithAlpha, PngBitDepth.Bit8), (PngColorType.RgbWithAlpha, PngBitDepth.Bit16),
        })
        {
            data.Add(type, depth, PngFilterMethod.Adaptive);
            data.Add(type, depth, PngFilterMethod.Paeth);
            data.Add(type, null, PngFilterMethod.Sub);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryRowCompatibleOption_WritesWhatPngEncoderWrites(PngColorType? colorType, PngBitDepth? bitDepth, PngFilterMethod filter)
    {
        var encoder = new PngEncoder { ColorType = colorType, BitDepth = bitDepth, FilterMethod = filter };
        AssertMatchesEncoder(StreamingTestSupport.Noise<Rgba32>(61, 37, seed: 1), encoder);
        AssertMatchesEncoder(StreamingTestSupport.Noise<Rgb24>(33, 20, seed: 2, opaque: true), encoder);
        AssertMatchesEncoder(StreamingTestSupport.Noise<L8>(17, 9, seed: 3, opaque: true), encoder);
        AssertMatchesEncoder(StreamingTestSupport.Noise<Rgba64>(19, 11, seed: 4), encoder);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void CompressionLevelAndTransparentColorMode_AreHonoured(CompressionLevel level)
    {
        AssertMatchesEncoder(StreamingTestSupport.Noise<Rgba32>(70, 50, seed: 5), new PngEncoder { CompressionLevel = level });
        AssertMatchesEncoder(
            StreamingTestSupport.Noise<Rgba32>(70, 50, seed: 6),
            new PngEncoder { CompressionLevel = level, TransparentColorMode = PngTransparentColorMode.Clear });
        AssertMatchesEncoder(
            StreamingTestSupport.Noise<Rgba32>(30, 20, seed: 7),
            new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha, TransparentColorMode = PngTransparentColorMode.Clear });
    }

    [Fact]
    public void MetadataChunks_AreWrittenAsPngEncoderWritesThem()
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(25, 15, seed: 8);
        image.Metadata.SetResolution(300, 150, PixelResolutionUnit.PixelsPerInch);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Software, "streaming test");
        image.Metadata.ExifProfile = exif;
        image.Metadata.IccProfile = new IccProfile(FixturePath.Read("metadata/icc_profile.bin"));
        image.Metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray());
        PngMetadata png = image.Metadata.GetPngMetadata();
        png.Gamma = 0.45455f;
        png.TextData.Add(new PngTextData("Title", "A title"));
        png.TextData.Add(new PngTextData("Comment", new string('x', 2000)));
        png.TextData.Add(new PngTextData("Note", "Grüße – ünïcödé", "de", "Notiz"));
        AssertMatchesEncoder(image, new PngEncoder());
    }

    [Fact]
    public void LargeImages_AreWrittenInBoundedIdatChunks()
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(400, 300, seed: 9);
        byte[] streamed = Stream(image, new PngEncoder { CompressionLevel = CompressionLevel.NoCompression });
        List<(string Type, byte[] Data)> chunks = StreamingTestSupport.Chunks(streamed);
        List<byte[]> idat = chunks.Where(c => c.Type == "IDAT").Select(c => c.Data).ToList();
        Assert.True(idat.Count > 5);
        Assert.All(idat, data => Assert.InRange(data.Length, 1, PngScanlineSink.IdatChunkBytes));
        Assert.All(idat.SkipLast(1), data => Assert.Equal(PngScanlineSink.IdatChunkBytes, data.Length));
        AssertMatchesEncoder(image, new PngEncoder { CompressionLevel = CompressionLevel.NoCompression });
    }

    [Theory]
    [InlineData(PngColorType.Palette, null, PngInterlaceMethod.None)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit1, PngInterlaceMethod.None)]
    [InlineData(PngColorType.Grayscale, PngBitDepth.Bit4, PngInterlaceMethod.None)]
    [InlineData(null, null, PngInterlaceMethod.Adam7)]
    public void OptionsThatNeedTheWholeImage_AreDeclined(PngColorType? colorType, PngBitDepth? bitDepth, PngInterlaceMethod interlace)
    {
        var encoder = new PngEncoder { ColorType = colorType, BitDepth = bitDepth, InterlaceMethod = interlace };
        Assert.False(PngScanlineSink.TryCreate(new MemoryStream(), encoder, out IScanlineSink<Rgba32>? sink));
        Assert.Null(sink);
    }

    [Fact]
    public void AnInvalidBitDepth_IsReportedAsPngEncoderReportsIt()
    {
        var encoder = new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit4 };
        Assert.Throws<NotSupportedException>(() => PngScanlineSink.TryCreate(new MemoryStream(), encoder, out IScanlineSink<Rgba32>? _));
        using var image = new Image<Rgba32>(2, 2);
        Assert.Throws<NotSupportedException>(() => image.Save(new MemoryStream(), encoder));
    }

    [Fact]
    public void AnAnimatedFlagInTheMetadata_StillWritesOneStillImage()
    {
        using Image<Rgba32> image = StreamingTestSupport.Noise<Rgba32>(5, 5, seed: 10);
        image.Metadata.GetPngMetadata().IsAnimated = true;
        byte[] streamed = Stream(image, new PngEncoder());
        Assert.DoesNotContain(StreamingTestSupport.Chunks(streamed), c => c.Type is "acTL" or "fcTL" or "fdAT");
        using Image<Rgba32> decoded = Image.Load<Rgba32>(streamed);
        Assert.Equal(StreamingTestSupport.Pixels(image), StreamingTestSupport.Pixels(decoded));
    }

    [Fact]
    public void CallsOutOfOrder_AreRejected()
    {
        Assert.True(PngScanlineSink.TryCreate(new MemoryStream(), new PngEncoder(), out IScanlineSink<Rgba32>? sink));
        using (sink)
        {
            Assert.Throws<InvalidOperationException>(() => sink!.WriteRow(new Rgba32[3]));
            Assert.Throws<InvalidOperationException>(() => sink!.Complete());
            sink!.Begin(3, 2, new ImageMetadata());
            Assert.Throws<InvalidOperationException>(() => sink.Begin(3, 2, new ImageMetadata()));
            Assert.Throws<ArgumentException>(() => sink.WriteRow(new Rgba32[2]));
            sink.WriteRow(new Rgba32[3]);
            Assert.Throws<InvalidOperationException>(() => sink.Complete());
            sink.WriteRow(new Rgba32[4]); // Longer than a row: only the first three pixels are used.
            Assert.Throws<InvalidOperationException>(() => sink.WriteRow(new Rgba32[3]));
            sink.Complete();
            Assert.Throws<InvalidOperationException>(() => sink.Complete());
        }

        Assert.Throws<ObjectDisposedException>(() => sink!.WriteRow(new Rgba32[3]));
    }

    [Fact]
    public void AnAbandonedSink_WritesNothingMoreWhenDisposed()
    {
        var output = new MemoryStream();
        Assert.True(PngScanlineSink.TryCreate(output, new PngEncoder(), out IScanlineSink<Rgba32>? sink));
        sink!.Begin(10, 10, new ImageMetadata());
        sink.WriteRow(new Rgba32[10]);
        long written = output.Length;
        sink.Dispose();
        Assert.Equal(written, output.Length);
    }

    private static byte[] Stream<TPixel>(Image<TPixel> image, PngEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var output = new MemoryStream();
        Assert.True(PngScanlineSink.TryCreate(output, encoder, out IScanlineSink<TPixel>? sink));
        using (sink)
        {
            sink!.Begin(image.Width, image.Height, image.Metadata);
            for (int y = 0; y < image.Height; y++)
            {
                sink.WriteRow(image.Frames.RootFrame.GetRowSpan(y));
            }

            sink.Complete();
        }

        return output.ToArray();
    }

    private static void AssertMatchesEncoder<TPixel>(Image<TPixel> image, PngEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using (image)
        {
            byte[] expected = StreamingTestSupport.Encode(image, encoder);
            byte[] actual = Stream(image, encoder);
            StreamingTestSupport.AssertSamePngModuloIdatSplit(expected, actual);
            using Image<Rgba64> left = Image.Load<Rgba64>(expected);
            using Image<Rgba64> right = Image.Load<Rgba64>(actual);
            Assert.Equal(StreamingTestSupport.Pixels(left), StreamingTestSupport.Pixels(right));
        }
    }
}
