using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>The transcoder's stream plumbing and its small helpers.</summary>
public class StreamingInputTests
{
    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();

    [Fact]
    public void AForwardOnlyInput_IsRecordedUntilReleased_SoItCanBeRewound()
    {
        byte[] data = Data(100_000);
        using var input = new StreamingInput(new ForwardOnlyStream(data, maxChunk: 777));
        Assert.False(input.CanSeek);

        var peeked = new byte[5000];
        Assert.Equal(5000, input.Peek(peeked));
        Assert.Equal(data.AsSpan(0, 5000).ToArray(), peeked);

        var first = new byte[30_000];
        input.ReadExactly(first);
        Assert.Equal(data.AsSpan(0, 30_000).ToArray(), first);

        input.Rewind();
        input.StopRecording();
        var all = new MemoryStream();
        input.CopyTo(all);
        Assert.Equal(data, all.ToArray());
        Assert.True(input.Exhausted);
        Assert.Throws<InvalidOperationException>(() => input.Rewind());
    }

    [Fact]
    public void ASeekableInput_KeepsItsOwnPositions_AndRewindsToWhereItStarted()
    {
        byte[] data = Data(200_000);
        using var inner = new MemoryStream(data) { Position = 1234 };
        using var input = new StreamingInput(inner);
        Assert.True(input.CanSeek);
        Assert.Equal(data.Length, input.Length);
        Assert.Equal(1234, input.Position);

        var buffer = new byte[10];
        input.ReadExactly(buffer);
        Assert.Equal(data.AsSpan(1234, 10).ToArray(), buffer);
        Assert.Equal(1244, input.Position);

        input.Position = 150_000;
        input.ReadExactly(buffer);
        Assert.Equal(data.AsSpan(150_000, 10).ToArray(), buffer);

        input.Seek(-5, SeekOrigin.Current);
        input.ReadExactly(buffer);
        Assert.Equal(data.AsSpan(150_005, 10).ToArray(), buffer);

        input.Rewind();
        Assert.Equal(1234, input.Position);
        var big = new byte[50_000];
        input.ReadExactly(big);
        Assert.Equal(data.AsSpan(1234, 50_000).ToArray(), big);
    }

    [Fact]
    public async Task ReadingAhead_ServesTheNextSynchronousReadsFromTheBuffer()
    {
        byte[] data = Data(300_000);
        var source = new ForwardOnlyStream(data, maxChunk: 10_000);
        using var input = new StreamingInput(source);
        await input.FillAsync(100_000, CancellationToken.None);
        Assert.True(input.Buffered >= 100_000);
        int asyncReads = source.AsyncReads;

        var chunk = new byte[1000];
        for (int i = 0; i < 100; i++)
        {
            input.ReadExactly(chunk);
            Assert.Equal(data.AsSpan(i * 1000, 1000).ToArray(), chunk);
        }

        Assert.Equal(0, source.SyncReads);
        Assert.Equal(0, input.SynchronousReads);
        Assert.Equal(asyncReads, source.AsyncReads);

        // Past the read-ahead the input falls back to reading synchronously.
        input.StopRecording();
        var rest = new MemoryStream();
        input.CopyTo(rest);
        Assert.Equal(data.AsSpan(100_000).ToArray(), rest.ToArray());
        Assert.True(input.SynchronousReads > 0);
    }

    [Fact]
    public async Task WriteBehind_HoldsWritesUntilDrained()
    {
        var output = new RecordingOutputStream();
        using var behind = new WriteBehindStream(output);
        byte[] data = Data(150_000);
        behind.Write(data, 0, 100);
        behind.WriteByte(7);
        await behind.DrainAsync(1000, CancellationToken.None);
        Assert.Empty(output.ToArray());
        behind.Write(data.AsSpan(100));
        Assert.Equal(150_001, behind.Buffered);
        await behind.DrainAsync(1000, CancellationToken.None);
        Assert.Equal(0, behind.Buffered);
        Assert.Equal(0, output.SyncWrites);
        Assert.Equal([.. data.AsSpan(0, 100), 7, .. data.AsSpan(100)], output.ToArray());
    }

    [Theory]
    [InlineData(6000, 4000, 300, 200, 8)]
    [InlineData(6000, 4000, 400, 300, 4)]  // 1/8 would be 750x500, under twice 400x300.
    [InlineData(1000, 1000, 300, 300, 1)]  // 1/2 would be 500x500, under twice 300x300.
    [InlineData(1001, 1001, 250, 250, 2)]  // ceil(1001 / 2) = 501.
    [InlineData(100, 100, 100, 100, 1)]
    [InlineData(100, 100, 200, 200, 1)]
    [InlineData(8000, 100, 100, 10, 4)]    // The tighter axis decides.
    public void ReducedDecode_KeepsTwiceTheTargetInBothAxes(int width, int height, int targetWidth, int targetHeight, int expected)
        => Assert.Equal(expected, ReducedDecode.ChooseDenominator(width, height, targetWidth, targetHeight));

    [Fact]
    public void ResizeTargets_AreComputedUprightAndMappedBackToTheStoredOrientation()
    {
        // The hook the JPEG row decoder uses to pick a reduced-IDCT scale before it decodes anything.
        var resize = new ResizeOptions { Size = new Size(100, 100), Mode = ResizeMode.Max };
        var rotated = new ImageMetadata { ExifProfile = new ExifProfile() };
        rotated.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);

        Assert.Equal((100, 50), StoredContentSize(new TranscodeOptions { Resize = resize }, new ImageMetadata()));

        // Stored 400x200 turned a quarter is 200x400 upright, which fits 100x100 as 50x100: stored, 100x50 again.
        Assert.Equal((100, 50), StoredContentSize(new TranscodeOptions { Resize = resize }, rotated));
        var square = new ResizeOptions { Size = new Size(100, 100), Mode = ResizeMode.Stretch };
        Assert.Equal((100, 100), StoredContentSize(new TranscodeOptions { Resize = square }, rotated));
        var wide = new ResizeOptions { Size = new Size(100, 0) };
        Assert.Equal((200, 100), StoredContentSize(new TranscodeOptions { Resize = wide }, rotated));
        Assert.Equal((100, 50), StoredContentSize(new TranscodeOptions { Resize = wide, AutoOrient = false }, rotated));

        // No resize, or one ShrinkOnly skips, keeps the stored size.
        Assert.Equal((400, 200), StoredContentSize(new TranscodeOptions(), rotated));
        var roomy = new ResizeOptions { Size = new Size(500, 500), Mode = ResizeMode.Max };
        Assert.Equal((400, 200), StoredContentSize(new TranscodeOptions { Resize = roomy, ShrinkOnly = true }, rotated));
        Assert.Equal((500, 250), StoredContentSize(new TranscodeOptions { Resize = roomy }, new ImageMetadata()));
    }

    private static (int Width, int Height) StoredContentSize(TranscodeOptions options, ImageMetadata metadata)
    {
        using var input = new StreamingInput(new MemoryStream());
        using var pipeline = new TranscodePipeline<Rgba32>(options, input, Stream.Null);
        return pipeline.StoredContentSize(400, 200, metadata);
    }
}
