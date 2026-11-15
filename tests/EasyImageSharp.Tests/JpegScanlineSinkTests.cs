using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// <see cref="JpegScanlineSink"/> encodes a frame pushed one row at a time. For every sequential configuration it
/// must write byte for byte what <see cref="JpegEncoder"/> writes for the whole image, and it must refuse
/// progressive output instead of buffering the image behind the caller's back.
/// </summary>
public class JpegScanlineSinkTests
{
    public static TheoryData<JpegEncodingColor> AllColorTypes()
    {
        var data = new TheoryData<JpegEncodingColor>();
        foreach (JpegEncodingColor color in Enum.GetValues<JpegEncodingColor>())
        {
            data.Add(color);
        }

        return data;
    }

    public static TheoryData<int, int> AwkwardSizes() => new()
    {
        { 1, 1 },
        { 7, 9 },
        { 16, 16 },
        { 33, 17 },
        { 64, 47 },
        { 131, 70 },
    };

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void StandardTables_MatchTheImageEncoderByteForByte(JpegEncodingColor color)
    {
        using Image<Rgb24> source = TestPhotographs.Create(150, 101, seed: 21);
        AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color });
    }

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void OptimisedTables_MatchTheImageEncoderByteForByte(JpegEncodingColor color)
    {
        using Image<Rgb24> source = TestPhotographs.Create(150, 101, seed: 22);
        AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color, OptimizeHuffmanTables = true });
    }

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void OneScanPerComponent_MatchesTheImageEncoderByteForByte(JpegEncodingColor color)
    {
        using Image<Rgb24> source = TestPhotographs.Create(99, 77, seed: 23);
        AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color, Interleaved = false });
        AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color, Interleaved = false, OptimizeHuffmanTables = true });
    }

    [Theory]
    [MemberData(nameof(AwkwardSizes))]
    public void EdgePadding_MatchesTheImageEncoderForEveryLayout(int width, int height)
    {
        using Image<Rgb24> source = TestPhotographs.Create(width, height, seed: width * height);
        foreach (JpegEncodingColor color in new[]
        {
            JpegEncodingColor.YCbCrRatio444, JpegEncodingColor.YCbCrRatio420, JpegEncodingColor.YCbCrRatio411,
            JpegEncodingColor.YCbCrRatio410, JpegEncodingColor.Luminance,
        })
        {
            AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color });
            AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color, OptimizeHuffmanTables = true, Interleaved = false });
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    [InlineData(1, true)]
    public void RestartIntervals_MatchTheImageEncoder(int interval, bool optimise)
    {
        using Image<Rgb24> source = TestPhotographs.Create(120, 90, seed: 24);
        foreach (JpegEncodingColor color in new[] { JpegEncodingColor.YCbCrRatio420, JpegEncodingColor.Luminance, JpegEncodingColor.Cmyk })
        {
            AssertSinkMatchesEncoder(source, new JpegEncoder { ColorType = color, RestartInterval = interval, OptimizeHuffmanTables = optimise });
            AssertSinkMatchesEncoder(
                source,
                new JpegEncoder { ColorType = color, RestartInterval = interval, OptimizeHuffmanTables = optimise, Interleaved = false });
        }
    }

    [Fact]
    public void TuningOptions_MatchTheImageEncoder()
    {
        using Image<Rgb24> source = TestPhotographs.Create(140, 96, seed: 25);
        AssertSinkMatchesEncoder(source, new JpegEncoder { TrellisQuantization = true, ColorType = JpegEncodingColor.YCbCrRatio420 });
        AssertSinkMatchesEncoder(source, new JpegEncoder { TrellisQuantization = true, Interleaved = false, RestartInterval = 2 });
        AssertSinkMatchesEncoder(
            source,
            new JpegEncoder { Quality = 70, ChromaQuality = 40, QuantizationPreset = JpegQuantizationPreset.Robidoux });
    }

    [Fact]
    public void L8Pixels_DefaultToLuminanceAsTheEncoderDoes()
    {
        using Image<Rgb24> rgb = TestPhotographs.Create(70, 50, seed: 26);
        using Image<L8> gray = rgb.CloneAs<L8>();
        AssertSinkMatchesEncoder(gray, new JpegEncoder());
        AssertSinkMatchesEncoder(gray, new JpegEncoder { ColorType = JpegEncodingColor.YCbCrRatio420 });
    }

    [Fact]
    public void Metadata_IsWrittenAsTheEncoderWritesIt()
    {
        using Image<Rgb24> source = TestPhotographs.Create(40, 30, seed: 27);
        source.Metadata.HorizontalResolution = 300;
        source.Metadata.VerticalResolution = 300;
        source.Metadata.GetFormatMetadata<JpegMetadata>().Comments.Add("streamed");
        AssertSinkMatchesEncoder(source, new JpegEncoder());
    }

    [Fact]
    public void LongerRowSpansAreTruncatedToTheWidth()
    {
        using Image<Rgb24> source = TestPhotographs.Create(45, 20, seed: 28);
        var encoder = new JpegEncoder();
        byte[] expected = EncodeImage(source, encoder);

        using var stream = new MemoryStream();
        Assert.True(JpegScanlineSink.TryCreate(stream, encoder, out IScanlineSink<Rgb24>? sink));
        using (sink)
        {
            sink!.Begin(source.Width, source.Height, source.Metadata);
            var padded = new Rgb24[source.Width + 13];
            for (int y = 0; y < source.Height; y++)
            {
                source.Frames.RootFrame.GetRowSpan(y).CopyTo(padded);
                padded.AsSpan(source.Width).Fill(new Rgb24(255, 0, 255));
                sink.WriteRow(padded);
            }

            sink.Complete();
        }

        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void Progressive_IsRefusedWithoutThrowing()
    {
        using var stream = new MemoryStream();
        Assert.False(JpegScanlineSink.TryCreate(stream, new JpegEncoder { Progressive = true }, out IScanlineSink<Rgb24>? sink));
        Assert.Null(sink);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void TrellisWithOptimisedTables_IsRefusedWithoutThrowing()
    {
        // The image encoder runs the trellis twice over every block in this configuration.
        using var stream = new MemoryStream();
        var encoder = new JpegEncoder { TrellisQuantization = true, OptimizeHuffmanTables = true };
        Assert.False(JpegScanlineSink.TryCreate(stream, encoder, out IScanlineSink<Rgb24>? sink));
        Assert.Null(sink);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void CallOrderIsEnforced()
    {
        var metadata = new ImageMetadata();
        using var stream = new MemoryStream();
        Assert.True(JpegScanlineSink.TryCreate(stream, new JpegEncoder(), out IScanlineSink<Rgb24>? created));
        using IScanlineSink<Rgb24> sink = created!;
        var row = new Rgb24[8];

        Assert.Throws<InvalidOperationException>(() => sink.WriteRow(row));
        Assert.Throws<InvalidOperationException>(() => sink.Complete());
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Begin(0, 2, metadata));

        sink.Begin(8, 2, metadata);
        Assert.Throws<InvalidOperationException>(() => sink.Begin(8, 2, metadata));
        Assert.Throws<ArgumentException>(() => sink.WriteRow(new Rgb24[7]));
        sink.WriteRow(row);
        Assert.Throws<InvalidOperationException>(() => sink.Complete());
        sink.WriteRow(row);
        Assert.Throws<InvalidOperationException>(() => sink.WriteRow(row));
        sink.Complete();
        Assert.Throws<InvalidOperationException>(() => sink.Complete());

        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream.ToArray());
        Assert.Equal(8, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }

    [Fact]
    public void OversizedFramesAreRejectedAtBegin()
    {
        using var stream = new MemoryStream();
        Assert.True(JpegScanlineSink.TryCreate(stream, new JpegEncoder(), out IScanlineSink<Rgb24>? sink));
        using (sink)
        {
            Assert.Throws<NotSupportedException>(() => sink!.Begin(70000, 1, new ImageMetadata()));
        }
    }

    [Fact]
    public void DisposingWithoutCompletingIsSafe()
    {
        using var stream = new MemoryStream();
        Assert.True(JpegScanlineSink.TryCreate(stream, new JpegEncoder { OptimizeHuffmanTables = true }, out IScanlineSink<Rgb24>? sink));
        sink!.Begin(32, 32, new ImageMetadata());
        sink.WriteRow(new Rgb24[32]);
        sink.Dispose();
        sink.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sink.WriteRow(new Rgb24[32]));
    }

    [Fact]
    public void SymbolBuffer_ReplaysExactlyTheBitsOfTheBlockCoder()
    {
        // Enough blocks to span several 64 KiB chunks, with dense, sparse, long-run and extreme blocks.
        var random = new Random(31);
        var blocks = new List<short[]>();
        for (int i = 0; i < 6000; i++)
        {
            var block = new short[64];
            switch (i % 4)
            {
                case 0:
                    for (int k = 0; k < 64; k++)
                    {
                        block[k] = (short)random.Next(-1023, 1024);
                    }

                    break;
                case 1:
                    block[0] = (short)random.Next(-2047, 2048);
                    block[random.Next(1, 64)] = (short)random.Next(-5, 6);
                    break;
                case 2:
                    block[0] = (short)(i % 7);
                    block[40] = -1;
                    block[63] = 1023;
                    break;
                default:
                    break;
            }

            blocks.Add(block);
        }

        int[] dc = new JpegHuffmanTable(JpegTables.StdDcBits, JpegTables.StdDcValues).Lookup;
        int[] ac = JpegEncoderCore.StandardAcTable(0).Lookup;

        using var direct = new MemoryStream();
        using (var writer = new JpegBitWriter(direct))
        {
            int predictor = 0;
            foreach (short[] block in blocks)
            {
                writer.EncodeSequentialBlock(block, ref predictor, dc, ac);
            }

            writer.Flush();
        }

        var dcFrequencies = new long[257];
        var acFrequencies = new long[257];
        using var replayed = new MemoryStream();
        using (var buffer = new JpegSymbolBuffer())
        {
            int predictor = 0;
            foreach (short[] block in blocks)
            {
                buffer.AppendBlock(block, ref predictor, dcFrequencies, acFrequencies);
            }

            Assert.True(buffer.Length < blocks.Count * 64L * sizeof(short), "The record is larger than the coefficients.");
            buffer.Rewind();
            using var writer = new JpegBitWriter(replayed);
            foreach (short[] _ in blocks)
            {
                buffer.ReplayBlock(writer, dc, ac);
            }

            writer.Flush();
        }

        Assert.Equal(direct.ToArray(), replayed.ToArray());
        Assert.Equal(blocks.Count, dcFrequencies.Sum());
    }

    // ----- Helpers -----

    private static void AssertSinkMatchesEncoder<TPixel>(Image<TPixel> source, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] expected = EncodeImage(source, encoder);
        byte[] actual = EncodeRows(source, encoder);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    private static byte[] EncodeImage<TPixel>(Image<TPixel> image, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static byte[] EncodeRows<TPixel>(Image<TPixel> image, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        Assert.True(JpegScanlineSink.TryCreate(stream, encoder, out IScanlineSink<TPixel>? sink));
        using (sink)
        {
            sink!.Begin(image.Width, image.Height, image.Metadata);
            for (int y = 0; y < image.Height; y++)
            {
                sink.WriteRow(image.Frames.RootFrame.GetRowSpan(y));
            }

            sink.Complete();
        }

        return stream.ToArray();
    }
}
