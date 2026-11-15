using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// <see cref="JpegScanlineSource"/>: every row it produces must equal the same row of a full decode, at every
/// reduction, for every sequential layout, while holding only a few MCU rows of samples.
/// </summary>
public class JpegScanlineSourceTests
{
    public static IEnumerable<object[]> Streamable()
    {
        string[] files =
        {
            "jpeg/baseline_444.jpg", "jpeg/baseline_422.jpg", "jpeg/baseline_420.jpg", "jpeg/baseline_420_odd.jpg",
            "jpeg/baseline_gray.jpg", "jpeg/restart_baseline_420.jpg", "jpeg/cmyk_adobe.jpg",
            "conformance/libjpeg-turbo/testimgari.jpg", // Sequential arithmetic coding (SOF9).
            "jpeg12/monkey12.jpg", "jpeg12/ycc12_420_q85.jpg", "jpeg12/gray12_q90.jpg", "jpeg12/rgb12_q95.jpg",
            "jpeg12/ycc12_420_q85_restart.jpg", "jpeg12/gray12_q90_restart.jpg",
        };
        foreach (string file in files)
        {
            foreach (int denominator in new[] { 1, 2, 4, 8 })
            {
                yield return new object[] { file, denominator };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Streamable))]
    public void RowsEqualTheFullDecode(string file, int denominator)
    {
        byte[] data = FixturePath.Read(file);
        AssertStreamMatchesFullDecode<Rgba32>(data, denominator);
        AssertStreamMatchesFullDecode<Rgb24>(data, denominator);
        if (file.StartsWith("jpeg12/", StringComparison.Ordinal))
        {
            AssertStreamMatchesFullDecode<Rgba64>(data, denominator);
            AssertStreamMatchesFullDecode<L16>(data, denominator);
        }
        else
        {
            AssertStreamMatchesFullDecode<L8>(data, denominator);
        }
    }

    [Fact]
    public void EncoderOutputAtScaleStreamsIdentically()
    {
        // A larger frame with several MCU rows per restart interval and a width that is not a multiple of 16.
        using var source = new Image<Rgb24>(333, 211);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source[x, y] = new Rgb24((byte)(x * 3), (byte)((x + y) * 5), (byte)(y * 7 ^ x));
            }
        }

        using var encoded = new MemoryStream();
        source.Save(encoded, new JpegEncoder { Quality = 80 });
        AssertStreamMatchesFullDecode<Rgba32>(encoded.ToArray(), 1);
        AssertStreamMatchesFullDecode<Bgra32>(encoded.ToArray(), 2);
    }

    [Theory]
    [InlineData("jpeg/progressive_420.jpg")]
    [InlineData("jpeg/progressive_gray.jpg")]
    [InlineData("jpeg12/ycc12_420_q85_progressive.jpg")]
    [InlineData("jpeglossless/gray16_p4.jpg")]
    [InlineData("jpeglossless/rgb8_planar_p2.jpg")]
    public void UnstreamableFrames_ReturnFalseAndRewind(string file)
    {
        byte[] data = FixturePath.Read(file);
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 1, 2, 3 });
        stream.Write(data);
        stream.Position = 3;
        Assert.False(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, 1, out IScanlineSource<Rgba32>? source));
        Assert.Null(source);
        Assert.Equal(3, stream.Position);

        // A reduction the full decoder cannot do either still declines rather than throws.
        Assert.False(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, 4, out IScanlineSource<Rgba32>? reduced));
        Assert.Null(reduced);
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void MultiScanSequentialFile_ReturnsFalse()
    {
        // ycck_adobe is baseline with one scan per component: the first scan alone does not make a row.
        byte[] data = FixturePath.Read("jpeg/ycck_adobe.jpg");
        using var stream = new MemoryStream(data);
        Assert.False(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, 1, out IScanlineSource<Rgba32>? source));
        Assert.Null(source);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void NonSeekableStreamIsReadIncrementally()
    {
        byte[] data = FixturePath.Read("jpeg/restart_baseline_420.jpg");
        using var stream = new TricklingStream(data, 7);
        Assert.True(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, 1, out IScanlineSource<Rgba32>? source));
        using (source)
        {
            using Image<Rgba32> full = Image.Load<Rgba32>(data);
            var row = new Rgba32[source!.Width + 5];
            int y = 0;
            while (source.ReadRow(row))
            {
                Assert.Equal(full.Frames.RootFrame.GetRowSpan(y).ToArray(), row[..source.Width]);
                y++;
                Assert.Equal(y, source.CurrentRow);
            }

            Assert.Equal(source.Height, y);
            Assert.False(source.ReadRow(row));
        }
    }

    [Fact]
    public void SampleMemoryIsBoundedByMcuRows()
    {
        using var image = new Image<Rgb24>(640, 960, new Rgb24(10, 200, 30));
        using var encoded = new MemoryStream();
        image.Save(encoded, new JpegEncoder { Quality = 90 });
        encoded.Position = 0;
        Assert.True(JpegScanlineSource.TryCreate(encoded, DecoderOptions.Default, 1, out IScanlineSource<Rgb24>? source));
        using (source)
        {
            var streaming = Assert.IsType<JpegScanlineSource.Source<Rgb24>>(source);
            long fullPlanes = 640L * 960 * 3;
            Assert.True(streaming.ResidentSampleBytes * 10 < fullPlanes, $"{streaming.ResidentSampleBytes} resident sample bytes");
            var row = new Rgb24[640];
            while (source!.ReadRow(row))
            {
            }

            Assert.Equal(new Rgb24(10, 200, 30), row[17]);
        }
    }

    [Fact]
    public void MetadataIsAvailableAfterTheHeader()
    {
        byte[] data = FixturePath.Read("jpeg12/monkey12.jpg");
        using var stream = new MemoryStream(data);
        Assert.True(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, 1, out IScanlineSource<Rgb48>? source));
        using (source)
        {
            using Image<Rgb48> full = Image.Load<Rgb48>(data);
            Assert.Equal(0, source!.CurrentRow);
            Assert.Equal((full.Width, full.Height), (source.Width, source.Height));
            Assert.Equal(full.Metadata.IccProfile!.ToByteArray(), source.Metadata.IccProfile!.ToByteArray());
            Assert.Equal(12, source.Metadata.GetJpegMetadata().BitsPerSample);
            Assert.Equal(full.Metadata.GetJpegMetadata().Quality, source.Metadata.GetJpegMetadata().Quality);
        }
    }

    [Fact]
    public void LimitsAreEnforcedBeforeAllocation()
    {
        byte[] data = FixturePath.Read("jpeg/baseline_444.jpg");
        Assert.Throws<ImageSizeLimitExceededException>(
            () => JpegScanlineSource.TryCreate(new MemoryStream(data), new DecoderOptions { MaxPixels = 100 }, 1, out IScanlineSource<Rgb24>? _));
        Assert.Throws<ImageSizeLimitExceededException>(
            () => JpegScanlineSource.TryCreate(new MemoryStream(data), new DecoderOptions { MaxEncodedBytes = 64 }, 1, out IScanlineSource<Rgb24>? _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JpegScanlineSource.TryCreate(new MemoryStream(data), DecoderOptions.Default, 3, out IScanlineSource<Rgb24>? _));
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        byte[] data = FixturePath.Read("jpeg/baseline_420.jpg");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var options = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token } };
        Assert.ThrowsAny<OperationCanceledException>(
            () => JpegScanlineSource.TryCreate(new MemoryStream(data), options, 1, out IScanlineSource<Rgb24>? _));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.97)]
    public void TruncatedInput_ThrowsInvalidContent(double fraction)
    {
        byte[] full = FixturePath.Read("jpeg/baseline_420.jpg");
        byte[] data = full[..(int)(full.Length * fraction)];
        Assert.Throws<InvalidImageContentException>(() =>
        {
            Assert.True(JpegScanlineSource.TryCreate(new MemoryStream(data), DecoderOptions.Default, 1, out IScanlineSource<Rgb24>? source));
            using (source)
            {
                var row = new Rgb24[source!.Width];
                while (source.ReadRow(row))
                {
                }
            }
        });
    }

    [Fact]
    public void ByteFlipFuzz_OnlyDocumentedExceptionsEscape()
    {
        string[] files = { "jpeg/baseline_420.jpg", "jpeg/restart_baseline_420.jpg", "jpeg12/ycc12_420_q85.jpg", "jpeg/cmyk_adobe.jpg" };
        var rng = new Random(424242);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            byte[] data = FixturePath.Read(files[iteration % files.Length]);
            for (int f = 0; f < 1 + rng.Next(3); f++)
            {
                data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }

            try
            {
                if (JpegScanlineSource.TryCreate(new MemoryStream(data), DecoderOptions.Default, 1 << rng.Next(4), out IScanlineSource<Rgba32>? source))
                {
                    using (source)
                    {
                        var row = new Rgba32[source!.Width];
                        while (source.ReadRow(row))
                        {
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    private static void AssertStreamMatchesFullDecode<TPixel>(byte[] data, int denominator)
        where TPixel : unmanaged, IPixel<TPixel>, IEquatable<TPixel>
    {
        using Image<TPixel> full = new JpegDecoder().DecodeReduced<TPixel>(data, denominator);
        using var stream = new MemoryStream(data);
        Assert.True(JpegScanlineSource.TryCreate(stream, DecoderOptions.Default, denominator, out IScanlineSource<TPixel>? source));
        using (source)
        {
            Assert.Equal((full.Width, full.Height), (source!.Width, source.Height));
            var row = new TPixel[source.Width];
            for (int y = 0; y < full.Height; y++)
            {
                Assert.True(source.ReadRow(row));
                ReadOnlySpan<TPixel> expected = full.Frames.RootFrame.GetRowSpan(y);
                if (!expected.SequenceEqual(row))
                {
                    int x = 0;
                    while (expected[x].Equals(row[x]))
                    {
                        x++;
                    }

                    Assert.Fail($"{typeof(TPixel).Name} 1/{denominator}: row {y} differs at x={x}: {row[x]} vs {expected[x]}");
                }
            }

            Assert.False(source.ReadRow(row));
            Assert.Equal(full.Height, source.CurrentRow);
        }
    }

    /// <summary>A forward-only stream that hands out at most a few bytes per read.</summary>
    private sealed class TricklingStream(byte[] data, int chunk) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, chunk), data.Length - this.position);
            data.AsSpan(this.position, n).CopyTo(buffer.AsSpan(offset));
            this.position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
