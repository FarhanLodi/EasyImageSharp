using System.Buffers.Binary;
using System.Globalization;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Locks the decode-time resource guards and two crafted-input regressions found during a hardening audit:
/// a PNG chunk with a negative length must not stall <c>Identify</c>, and a JPEG declaring 65535x65535
/// must be rejected before any plane memory is allocated.
/// </summary>
/// <remarks>
/// It also locks the two limits added in 1.1: dimensions whose product overflows an <see cref="int"/> are
/// rejected as a size limit rather than escaping as an <see cref="OverflowException"/> once a caller raises
/// <see cref="DecoderOptions.MaxPixels"/>, and <see cref="DecoderOptions.MaxTotalPixels"/> bounds the sum of
/// every frame a multi-frame decode allocates, which <see cref="DecoderOptions.MaxPixels"/> alone never did.
/// </remarks>
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

    /// <summary>
    /// BMP, TGA and ICO size their pixel or index buffers straight from the header, so the product must be
    /// known to fit an <see cref="int"/> before the first allocation. The default MaxPixels implies that, but
    /// the README tells callers to raise MaxPixels for large images, and a crafted header would then overflow
    /// the multiplication and surface an OverflowException or an ArgumentOutOfRangeException instead.
    /// </summary>
    [Theory]
    [InlineData("BMP")]
    [InlineData("TGA")]
    [InlineData("ICO")]
    [InlineData("PNG")]
    public void DimensionsOverflowingInt_ThrowSizeLimitEvenWhenMaxPixelsAllowsThem(string format)
    {
        byte[] data = HeaderOnly(format);
        var options = new DecoderOptions { MaxPixels = long.MaxValue };

        // Thread-local, so allocations made by tests running in parallel do not count towards this budget.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(data, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("largest buffer this library can address", ex.Message);
        Assert.DoesNotContain("MaxPixels", ex.Message);
        Assert.True(allocated < 1_000_000, $"Rejecting the {format} header allocated {allocated:N0} bytes.");
    }

    /// <summary>Identify is exempt from every size limit, including the new int-capacity guard.</summary>
    [Theory]
    [InlineData("BMP", 100_000, 100_000)]
    [InlineData("TGA", 65_535, 65_535)]
    [InlineData("ICO", 100_000, 50_000)] // The DIB stacks the XOR bitmap and the AND mask, so the height halves.
    [InlineData("PNG", 100_000, 100_000)]
    public void Identify_IsExemptFromTheIntCapacityGuard(string format, int width, int height)
    {
        byte[] data = HeaderOnly(format);
        ImageInfo info = Image.Identify(data, new DecoderOptions { MaxPixels = long.MaxValue });
        Assert.Equal((width, height), (info.Width, info.Height));

        // ... and under the default limits too, which is the published security property callers rely on.
        ImageInfo underDefaults = Image.Identify(data);
        Assert.Equal(width, underDefaults.Width);
    }

    /// <summary>
    /// MaxPixels is per frame and MaxFrames defaults to unlimited, so before MaxTotalPixels a GIF of a few
    /// hundred bytes could force gigabytes: every frame is snapshotted at the full logical screen size, and
    /// an image descriptor plus a one-pixel LZW stream costs fifteen bytes.
    /// </summary>
    [Fact]
    public void MaxTotalPixels_BoundsAGifThatDeclaresManyFrames()
    {
        byte[] gif = BuildAnimatedGif(500, 500, 40);
        Assert.True(gif.Length < 1024, $"The amplification fixture grew to {gif.Length} bytes.");

        using (Image<Rgba32> full = Image.Load<Rgba32>(gif))
        {
            // 40 x 250 000 pixels is far below the default budget, so nothing that decodes today regresses.
            Assert.Equal(40, full.Frames.Count);
            Assert.Equal((500, 500), (full.Width, full.Height));
        }

        var tight = new DecoderOptions { MaxTotalPixels = 500 * 500 * 8 };
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(gif, tight));
        Assert.Contains("MaxTotalPixels", ex.Message);
        Assert.Contains("across all frames", ex.Message);
    }

    /// <summary>A concatenated Netpbm stream costs about fifteen bytes per extra declared image.</summary>
    [Fact]
    public void MaxTotalPixels_BoundsAConcatenatedNetpbmStream()
    {
        byte[] pgm = BuildConcatenatedPgm(50, 50, 32);
        using (Image<L8> full = Image.Load<L8>(pgm))
        {
            Assert.Equal(32, full.Frames.Count);
        }

        var tight = new DecoderOptions { MaxTotalPixels = 50 * 50 * 4 };
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<L8>(pgm, tight));
        Assert.Contains("MaxTotalPixels", ex.Message);
    }

    /// <summary>Animated WebP materialises every frame at the full canvas size, so it amplifies the same way.</summary>
    [Fact]
    public void MaxTotalPixels_BoundsAnAnimatedWebp()
    {
        byte[] webp = FixturePath.Read("webp/anim_lossless.webp");
        long canvas;
        int frameCount;
        using (Image<Rgba32> full = Image.Load<Rgba32>(webp))
        {
            canvas = (long)full.Width * full.Height;
            frameCount = full.Frames.Count;
        }

        Assert.True(frameCount >= 2, $"The animated fixture decodes {frameCount} frames.");
        var tight = new DecoderOptions { MaxTotalPixels = canvas };
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(webp, tight));
        Assert.Contains("MaxTotalPixels", ex.Message);
    }

    /// <summary>ICO charges every directory entry it decodes, DIB or embedded PNG.</summary>
    [Fact]
    public void MaxTotalPixels_BoundsAMultiEntryIcon()
    {
        byte[] bytes = SmallFormatFixtures.Bytes("smallformats/ico", "pil_bmp_multi"); // 16, 24 and 32 pixel entries.
        using (Image<Rgba32> full = Image.Load<Rgba32>(bytes))
        {
            Assert.Equal(3, full.Frames.Count);
        }

        var tight = new DecoderOptions { MaxTotalPixels = (16 * 16) + (24 * 24) };
        var ex = Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(bytes, tight));
        Assert.Contains("MaxTotalPixels", ex.Message);

        using Image<Rgba32> exact = Image.Load<Rgba32>(
            bytes, new DecoderOptions { MaxTotalPixels = (16 * 16) + (24 * 24) + (32 * 32) });
        Assert.Equal(3, exact.Frames.Count);
    }

    /// <summary>The budget is a sum, not a per-frame limit: one frame of exactly the budget still decodes.</summary>
    [Fact]
    public void MaxTotalPixels_IsExclusiveAtTheBoundaryAndLeavesSingleFramesAlone()
    {
        byte[] gif = BuildAnimatedGif(64, 64, 1);
        using (Image<Rgba32> exact = Image.Load<Rgba32>(gif, new DecoderOptions { MaxTotalPixels = 64 * 64 }))
        {
            Assert.Single(exact.Frames);
        }

        Assert.Throws<ImageSizeLimitExceededException>(
            () => Image.Load<Rgba32>(gif, new DecoderOptions { MaxTotalPixels = (64 * 64) - 1 }));

        // Single-frame formats never consult the budget at all.
        using Image<Rgb24> source = TestImages.Gradient(64, 64);
        using var ms = new MemoryStream();
        source.SaveAsPng(ms);
        using Image<Rgb24> png = Image.Load<Rgb24>(ms.ToArray(), new DecoderOptions { MaxTotalPixels = 1 });
        Assert.Equal(64, png.Width);
    }

    [Fact]
    public void MaxTotalPixels_Init_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxTotalPixels = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxTotalPixels = -1 });
        Assert.Equal(1L << 30, DecoderOptions.DefaultMaxTotalPixels);
        Assert.Equal(DecoderOptions.DefaultMaxTotalPixels, DecoderOptions.Default.MaxTotalPixels);
        Assert.Equal(4L * DecoderOptions.DefaultMaxPixels, DecoderOptions.DefaultMaxTotalPixels);
    }

    private static byte[] HeaderOnly(string format) => format switch
    {
        "BMP" => BuildBmpHeaderOnly(100_000, 100_000),
        "TGA" => BuildTgaHeaderOnly(65_535, 65_535),
        "ICO" => BuildIcoHeaderOnly(100_000, 100_000),
        _ => BuildPngHeaderOnly(100_000, 100_000),
    };

    /// <summary>A 54-byte BMP: file header plus a BITMAPINFOHEADER, with no pixel data at all.</summary>
    private static byte[] BuildBmpHeaderOnly(int width, int height)
    {
        byte[] data = new byte[54];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), 24);
        return data;
    }

    /// <summary>
    /// An 18-byte TGA. Its dimension fields are unsigned 16-bit, so 65535x65535 - over four billion pixels -
    /// is reachable from a header with no pixel data behind it; the run-length image type is what makes such
    /// a file plausible to the detector.
    /// </summary>
    private static byte[] BuildTgaHeaderOnly(int width, int height)
    {
        byte[] data = new byte[18];
        data[2] = 10; // Run-length encoded truecolor.
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)height);
        data[16] = 24; // Pixel depth.
        return data;
    }

    /// <summary>A 62-byte icon: ICONDIR, one directory entry and a BITMAPINFOHEADER with no bitmap behind it.</summary>
    private static byte[] BuildIcoHeaderOnly(int width, int storedHeight)
    {
        byte[] data = new byte[6 + 16 + 40];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1); // Type: icon.
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1); // One entry.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40); // Entry byte count.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(18), 22); // Entry offset.

        Span<byte> dib = data.AsSpan(22);
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], storedHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[12..], 1); // Planes.
        BinaryPrimitives.WriteUInt16LittleEndian(dib[14..], 32); // Bits per pixel.
        return data;
    }

    /// <summary>
    /// A GIF whose logical screen is <paramref name="screenWidth"/> x <paramref name="screenHeight"/> and which
    /// declares <paramref name="frameCount"/> one-pixel image descriptors. Each frame costs fifteen bytes in
    /// the file and a full screen-sized frame in memory.
    /// </summary>
    private static byte[] BuildAnimatedGif(int screenWidth, int screenHeight, int frameCount)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8);
        bytes.Add((byte)screenWidth);
        bytes.Add((byte)(screenWidth >> 8));
        bytes.Add((byte)screenHeight);
        bytes.Add((byte)(screenHeight >> 8));
        bytes.Add(0x80); // Global colour table present, two entries.
        bytes.Add(0);    // Background colour index.
        bytes.Add(0);    // Pixel aspect ratio.
        bytes.AddRange(new byte[] { 0, 0, 0, 255, 255, 255 });

        for (int i = 0; i < frameCount; i++)
        {
            bytes.Add(0x2C);                                // Image separator.
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });       // Left, top.
            bytes.AddRange(new byte[] { 1, 0, 1, 0 });       // One pixel wide and tall.
            bytes.Add(0);                                   // No local table, not interlaced.
            bytes.Add(2);                                   // LZW minimum code size.
            bytes.AddRange(new byte[] { 2, 0x44, 0x01, 0 }); // CLEAR, index 0, end-of-information.
        }

        bytes.Add(0x3B); // Trailer.
        return bytes.ToArray();
    }

    /// <summary>Concatenated binary graymaps, the cheapest Netpbm amplification: one header per extra image.</summary>
    private static byte[] BuildConcatenatedPgm(int width, int height, int count)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < count; i++)
        {
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"P5\n{width} {height}\n255\n")));
            bytes.AddRange(new byte[width * height]);
        }

        return bytes.ToArray();
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
