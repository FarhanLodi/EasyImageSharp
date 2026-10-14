using EasyImageSharp.Formats;
using Xunit;

namespace EasyImageSharp.Tests;

public class HeaderStressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManySmallJpegSegmentsAreScannedOnceAndStopAtDimensions(bool asynchronous)
    {
        using var stream = BuildHeader(20_000, 0);
        long expectedPosition = stream.Length;
        stream.Write(new byte[256]); // Pixel payload must not be consumed by header inspection.
        stream.Position = 0;
        ImageHeaderInfo info = asynchronous ? await Image.IdentifyHeaderAsync(stream) : Image.IdentifyHeader(stream);
        Assert.Equal(ImageFormat.Jpeg, info.Format);
        Assert.Equal(new Size(41, 23), info.Size);
        Assert.Equal(expectedPosition, stream.Position);
    }

    [Fact]
    public void JpegFillerBytesAcrossReadBoundariesPreserveMarkerState()
    {
        using var stream = BuildHeader(23, 257);
        stream.Position = 0;
        Assert.Equal(new Size(41, 23), Image.IdentifyHeader(stream).Size);
    }

    [Fact]
    public void ManySegmentsRemainBoundedByConfiguredHeaderBytes()
    {
        using var stream = BuildHeader(20_000, 0);
        stream.Position = 0;
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.IdentifyHeader(stream, 1024));
        Assert.InRange(stream.Position, 0, 1024);
    }

    private static MemoryStream BuildHeader(int segments, int filler)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[] { 255, 216 });
        for (int i = 0; i < segments; i++)
            stream.Write(new byte[] { 255, 254, 0, 2 }); // Empty COM segment.
        for (int i = 0; i < filler; i++) stream.WriteByte(255);
        // SOF0, minimum dimensional prefix (the API deliberately does not validate the pixel payload).
        stream.Write(new byte[] { 255, 192, 0, 8, 8, 0, 23, 0, 41, 1 });
        return stream;
    }
}
