using System.Buffers.Binary;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

// Existing codec tests temporarily mutate process defaults. Keep this assertion isolated from those
// tests; its two Task.Run workers still exercise concurrent request scopes inside the test itself.
[CollectionDefinition("Execution isolation", DisableParallelization = true)]
public sealed class ExecutionIsolationCollection { }

[Collection("Execution isolation")]
public class ExecutionAndInputTests
{
    [Fact]
    public void OversizedWideImage_RejectsDimensionsBeforeByteBudgetArithmetic()
        => Assert.Throws<InvalidImageContentException>(() => new Image<RgbaVector>(int.MaxValue, int.MaxValue));

    [Fact]
    public void ParallelRowRanges_DoNotOverflowAtAddressLimit()
    {
        var ranges = new System.Collections.Concurrent.ConcurrentBag<(int Start, int End)>();
        ParallelRowIterator.IterateRows(1, int.MaxValue, (start, end) => ranges.Add((start, end)),
            new Configuration { MaxDegreeOfParallelism = 2, MinimumPixelsPerTask = 1 });
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        Assert.Equal(2, ordered.Length);
        Assert.Equal(0, ordered[0].Start);
        Assert.Equal(ordered[0].End, ordered[1].Start);
        Assert.Equal(int.MaxValue, ordered[1].End);
    }

    [Fact]
    public async Task RequestConfiguration_IsolatedAcrossConcurrentFlowsAndNestedScopes()
    {
        int original = Configuration.Default.MaxDegreeOfParallelism;
        using var barrier = new Barrier(2);
        await Task.WhenAll(new[] { 1, 3 }.Select(parallelism => Task.Run(() =>
        {
            using var image = new Image<Rgba32>(8, 8);
            image.Mutate(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = parallelism } }, _ =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                Assert.Equal(parallelism, OperationExecution.Current!.Configuration.MaxDegreeOfParallelism);
                image.Mutate(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 2 } }, _ =>
                    Assert.Equal(2, OperationExecution.Current!.Configuration.MaxDegreeOfParallelism));
                Assert.Equal(parallelism, OperationExecution.Current!.Configuration.MaxDegreeOfParallelism);
            });
            Assert.Null(OperationExecution.Current);
        })));
        Assert.Equal(original, Configuration.Default.MaxDegreeOfParallelism);
    }

    [Fact]
    public void CloneBudgetAndCancellation_ProtectSourceAndRestoreScope()
    {
        using var image = new Image<Rgba32>(16, 16, new Rgba32(30, 60, 90));
        Assert.Throws<ImageSizeLimitExceededException>(() => image.Clone(new ImageExecutionOptions { MaxPixelBufferBytes = 100 }, x => x.Resize(32, 32)));
        Assert.Equal(16, image.Width);
        Assert.Equal(new Rgba32(30, 60, 90), image[0, 0]);
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => image.Clone(new ImageExecutionOptions { CancellationToken = cancel.Token }, x =>
        {
            x.Resize(32, 32);
            cancel.Cancel();
            x.Grayscale();
        }));
        Assert.Equal(16, image.Width);
        Assert.Null(OperationExecution.Current);
    }

    [Fact]
    public void PixelBudget_IsCumulativeAndDirectDecoderHonorsIt()
    {
        using var image = new Image<Rgba32>(16, 16);
        Assert.Throws<ImageSizeLimitExceededException>(() => image.Clone(new ImageExecutionOptions { MaxPixelBufferBytes = 5000 }, x => x.Resize(32, 32).Resize(16, 16)));
        byte[] bytes = Encode(image, ImageFormat.Png);
        Assert.Throws<ImageSizeLimitExceededException>(() => new PngDecoder().Decode<Rgba32>(bytes,
            new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { MaxPixelBufferBytes = 100 } }));
    }

    [Fact]
    public async Task EncodedBudget_StopsNonSeekableInputAtLimitPlusOne()
    {
        using var stream = new CountingStream(new byte[10000]);
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(stream, new DecoderOptions { MaxEncodedBytes = 100 }));
        Assert.Equal(101, stream.BytesRead);
        using var asyncStream = new CountingStream(new byte[10000]);
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => Image.LoadAsync<Rgba32>(asyncStream, new DecoderOptions { MaxEncodedBytes = 100 }));
        Assert.Equal(101, asyncStream.BytesRead);
    }

    [Fact]
    public void TiffPages_AreLazyAndHaveIndependentDimensionsAndBudgets()
    {
        using var first = new Image<Rgba32>(8, 7, new Rgba32(200, 10, 20));
        using var second = new Image<Rgba32>(4, 5, new Rgba32(20, 10, 200));
        first.Frames.AddFrame(second.Frames.RootFrame);
        byte[] bytes = Encode(first, ImageFormat.Tiff);
        using var pages = new TiffDecoder().DecodePages<Rgba32>(bytes, new DecoderOptions { MaxTotalPixels = 60 }).GetEnumerator();
        Assert.True(pages.MoveNext());
        using (Image<Rgba32> page = pages.Current)
        {
            Assert.Equal(new Size(8, 7), page.Size);
            Assert.Equal(first[0, 0], page[0, 0]);
        }
        Assert.Throws<ImageSizeLimitExceededException>(() => pages.MoveNext());
        using var all = new TiffDecoder().DecodePages<Rgba32>(bytes).GetEnumerator();
        Assert.True(all.MoveNext());
        all.Current.Dispose();
        Assert.True(all.MoveNext());
        using (Image<Rgba32> page = all.Current) Assert.Equal(new Size(4, 5), page.Size);
        Assert.False(all.MoveNext());
        Assert.Null(OperationExecution.Current);
    }

    [Fact]
    public void TiffPages_CancellationBetweenPagesDoesNotLeakAmbientScope()
    {
        using var image = new Image<Rgba32>(2, 2);
        image.Frames.AddFrame(image.Frames.RootFrame);
        using var cancel = new CancellationTokenSource();
        using var pages = new TiffDecoder().DecodePages<Rgba32>(Encode(image, ImageFormat.Tiff),
            new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancel.Token } }).GetEnumerator();
        Assert.True(pages.MoveNext());
        pages.Current.Dispose();
        Assert.Null(OperationExecution.Current);
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => pages.MoveNext());
        Assert.Null(OperationExecution.Current);
    }

    [Fact]
    public void TiffPages_BudgetIncludesNestedJpegPixelBuffersAcrossPages()
    {
        byte[] single = FixturePath.Read("tiffadv/jpeg_selfcontained.tif");
        Assert.True(single.AsSpan(0, 2).SequenceEqual("II"u8));
        int directory = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(4)));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(single.AsSpan(directory));
        int directoryBytes = 2 + entries * 12 + 4;
        byte[] twoPages = new byte[single.Length + directoryBytes];
        single.CopyTo(twoPages, 0);
        single.AsSpan(directory, directoryBytes).CopyTo(twoPages.AsSpan(single.Length));
        // A second directory can legitimately share encoded strip storage with the first page.
        BinaryPrimitives.WriteUInt32LittleEndian(twoPages.AsSpan(directory + directoryBytes - 4), (uint)single.Length);
        long onePageBytes;
        using (var scope = OperationExecution.Enter())
        {
            using var page = new TiffDecoder().DecodePages<Rgba32>(single).Single();
            onePageBytes = scope.AllocatedPixelBytes;
            Assert.True(onePageBytes > (long)page.Width * page.Height * 4);
        }
        using var pages = new TiffDecoder().DecodePages<Rgba32>(twoPages, new DecoderOptions
        {
            ExecutionOptions = new ImageExecutionOptions { MaxPixelBufferBytes = onePageBytes * 2 - 1 },
        }).GetEnumerator();
        Assert.True(pages.MoveNext());
        pages.Current.Dispose();
        Assert.Throws<ImageSizeLimitExceededException>(() => pages.MoveNext());
    }

    [Theory]
    [InlineData("png", 24)]
    [InlineData("jpeg", 65536)]
    [InlineData("gif", 12)]
    [InlineData("bmp", 26)]
    [InlineData("qoi", 12)]
    [InlineData("webp", 30)]
    public async Task IncrementalHeader_ReadsDimensionsWithoutPixelPayload(string extension, int maximumRead)
    {
        using var image = new Image<Rgba32>(64, 45, new Rgba32(20, 90, 200));
        ImageFormat format = extension switch
        {
            "png" => ImageFormat.Png, "jpeg" => ImageFormat.Jpeg, "gif" => ImageFormat.Gif,
            "bmp" => ImageFormat.Bmp, "qoi" => ImageFormat.Qoi, _ => ImageFormat.Webp,
        };
        byte[] encoded = Encode(image, format);
        using var stream = new CountingStream(encoded);
        ImageHeaderInfo header = Image.IdentifyHeader(stream);
        Assert.Equal(image.Size, header.Size);
        Assert.Same(format, header.Format);
        Assert.InRange(stream.BytesRead, 1, maximumRead);
        Assert.True(stream.BytesRead < encoded.Length);
        using var asyncStream = new CountingStream(encoded);
        Assert.Equal(header.Size, (await Image.IdentifyHeaderAsync(asyncStream)).Size);
        Assert.Equal(stream.BytesRead, asyncStream.BytesRead);
    }

    [Fact]
    public void IncrementalHeader_RejectsBoundedOversizedAndTruncatedSegments()
    {
        byte[] bytes = new byte[200];
        bytes[0] = 255; bytes[1] = 216; bytes[2] = 255; bytes[3] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 190);
        using var stream = new CountingStream(bytes);
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.IdentifyHeader(stream, 100));
        Assert.InRange(stream.BytesRead, 1, 100);
        using var truncated = new MemoryStream(bytes.AsSpan(0, 13).ToArray());
        Assert.Throws<InvalidImageContentException>(() => Image.IdentifyHeader(truncated));
    }

    [Fact]
    public void CustomFormat_IsRequestLocalAndIdentifiable()
    {
        ImageFormat format = ImageFormat.Create("Test", "image/x-test", new[] { ".TEST" }, d => d.StartsWith("TEST"u8), () => new TestDecoder());
        var providers = new[] { format };
        var options = new DecoderOptions { AdditionalFormats = providers };
        providers[0] = ImageFormat.Png;
        using var image = Image.Load<Rgba32>("TEST"u8, options);
        Assert.Equal(new Size(2, 3), image.Size);
        Assert.Equal("test", format.FileExtensions[0]);
        Assert.Equal(new Size(2, 3), Image.Identify("TEST"u8, options).Size);
        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>("TEST"u8));
    }

    private static byte[] Encode(Image<Rgba32> image, ImageFormat format)
    {
        using var stream = new MemoryStream();
        image.Save(stream, format);
        return stream.ToArray();
    }

    private sealed class TestDecoder : IImageDecoder
    {
        public Image<T> Decode<T>(ReadOnlySpan<byte> data, DecoderOptions options) where T : unmanaged, IPixel<T> => new(2, 3);
        public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options) => ImageInfo.Create(2, 3, 32, 1, ImageFormat.Png);
    }

    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = this.inner.Read(buffer);
            this.BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(this.Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) this.inner.Dispose(); base.Dispose(disposing); }
    }
}
