using System.Buffers.Binary;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Locks the decode-time resource guards and two crafted-input regressions found during a hardening audit:
/// a PNG chunk with a negative length must not stall <c>Identify</c>, and a JPEG declaring 65535x65535
/// must be rejected before any plane memory is allocated.
/// </summary>
public class DecoderLimitsTests
{
    [Fact]
    public async Task Png_Identify_NegativeChunkLength_ThrowsInsteadOfLooping()
    {
        // Signature followed by a chunk whose length field is -12: the old scanner added 12 + length and never advanced.
        byte[] data = new byte[8 + 12 + 32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), -12);
        "tEXt"u8.CopyTo(data.AsSpan(12));

        Task task = Task.Run(() => Assert.ThrowsAny<ImageFormatException>(() => Image.Identify(data)));
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, task), "Identify did not terminate on a negative PNG chunk length.");
        await task;
    }

    [Fact]
    public void Png_Decode_NegativeChunkLength_Throws()
    {
        byte[] data = new byte[8 + 12 + 32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), -12);
        "IHDR"u8.CopyTo(data.AsSpan(12));

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(data));
    }

    [Fact]
    public void Png_HugeDeclaredDimensions_RejectedByDefaultLimit()
    {
        byte[] png = BuildPngHeaderOnly(100_000, 100_000);
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(png));
        Assert.Contains("MaxPixels", ex.Message);

        // Identify is deliberately unlimited so callers can inspect the declared size first.
        ImageInfo info = Image.Identify(png);
        Assert.Equal(100_000, info.Width);
    }

    [Fact]
    public void Jpeg_HugeDeclaredDimensions_RejectedBeforeAllocation()
    {
        // SOI + SOF0 claiming 65535x65535 with three components: previously allocated multi-GB planes (or overflowed).
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x03,
            0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9,
        };

        // Thread-local, so allocations made by tests running in parallel do not count towards this budget.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgb24>(jpeg));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"Rejecting the header allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Jpeg_InvalidQuantTableSelector_ThrowsInvalidContent()
    {
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01,
            0x01, 0x11, 0x07, // Tq = 7 is out of range.
            0xFF, 0xD9,
        };

        Assert.Throws<InvalidImageContentException>(() => Image.Load<L8>(jpeg));
    }

    [Fact]
    public void CustomLimit_IsHonouredAndDefaultIsNot()
    {
        using Image<Rgb24> source = TestImages.Gradient(64, 64);
        using var ms = new MemoryStream();
        source.SaveAsPng(ms);
        byte[] png = ms.ToArray();

        var tight = new DecoderOptions { MaxPixels = 1000 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgb24>(png, tight));

        using Image<Rgb24> ok = Image.Load<Rgb24>(png, new DecoderOptions { MaxPixels = 64 * 64 });
        Assert.Equal(64, ok.Width);

        using Image<Rgb24> viaStream = Image.Load<Rgb24>(new MemoryStream(png), DecoderOptions.Default);
        Assert.Equal(64, viaStream.Height);
    }

    [Fact]
    public void Tiff_MaxFrames_TruncatesDecodeButNotIdentify()
    {
        using var multi = new Image<L8>(4, 4);
        multi.Frames.AddFrame(new Image<L8>(4, 4).Frames.RootFrame);
        multi.Frames.AddFrame(new Image<L8>(4, 4).Frames.RootFrame);
        using var ms = new MemoryStream();
        multi.SaveAsTiff(ms);
        byte[] tiff = ms.ToArray();

        Assert.Equal(3, Image.Identify(tiff).FrameCount);
        using Image<L8> limited = Image.Load<L8>(tiff, new DecoderOptions { MaxFrames = 2 });
        Assert.Equal(2, limited.Frames.Count);
    }

    [Fact]
    public void Bmp_TruncatedBitfieldMasks_ThrowsInvalidContent()
    {
        // 14-byte file header + 40-byte DIB header claiming BI_BITFIELDS (3) at 32 bpp, but no mask bytes follow.
        byte[] bmp = new byte[54];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 66);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30), 3);

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bmp));
    }

    [Fact]
    public void JpegEncoder_RejectsDimensionsTheFormatCannotRepresent()
    {
        using var tall = new Image<L8>(1, 65_536);
        Assert.Throws<NotSupportedException>(() => tall.SaveAsJpeg(new MemoryStream()));
    }

    private static byte[] BuildPngHeaderOnly(int width, int height)
    {
        var data = new byte[8 + 8 + 13 + 4];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        data[24] = 8; // bit depth
        data[25] = 6; // RGBA
        return data;
    }
}
