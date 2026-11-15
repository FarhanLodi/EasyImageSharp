using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Exr;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Hand-crafted malformed Radiance HDR and OpenEXR inputs, plus the fuzzer-found files the OpenEXR project keeps
/// in openexr-images/Damaged. Every case must end in the documented exception (or a successful decode), never in
/// a framework exception, a hang or an outsized allocation.
/// </summary>
public class HdrExrCorruptInputTests
{
    // ----- OpenEXR: header -----

    [Fact]
    public void Exr_ValidHandBuiltFile_Decodes()
    {
        using Image<RgbaVector> image = Image.Load<RgbaVector>(ExrFile());
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(new RgbaVector(1f, 0.5f, 0.25f, 1f), image[2, 1]);
    }

    [Fact]
    public void Exr_BadMagic_IsInvalid()
    {
        byte[] bytes = ExrFile();
        bytes[1] = 0;
        Assert.Throws<InvalidImageContentException>(() => new ExrDecoder().Decode<Rgba32>(bytes));
        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>(bytes));
    }

    [Theory]
    [InlineData(1, "version")]
    [InlineData(2 | 0x1000, "Multi-part")]
    [InlineData(2 | 0x800, "deep")]
    [InlineData(2 | 0x10000, "flags")]
    public void Exr_UnsupportedVersionOrFlags_AreNotSupported(int version, string message)
    {
        byte[] bytes = ExrFile();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), version);
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(bytes));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => Image.Identify(bytes));
    }

    [Fact]
    public void Exr_DeepTypeAttribute_IsNotSupported()
        => Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(ExrFile(extra: Attribute("type", "string", Encoding.ASCII.GetBytes("deepscanline")))));

    [Fact]
    public void Exr_MissingChannels_IsInvalid()
        => Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(omit: "channels")));

    [Fact]
    public void Exr_AttributeLongerThanTheFile_IsInvalid()
    {
        byte[] bytes = ExrFile();
        int size = IndexOf(bytes, "box2i") + 6;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(size), 1 << 30);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Theory]
    [InlineData(7, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, -2)]
    public void Exr_InvalidChannelDescription_IsInvalid(int pixelType, int xSampling, int ySampling)
        => Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(channels: new[] { ("R", pixelType, xSampling, ySampling) })));

    [Fact]
    public void Exr_InvertedDataWindow_IsInvalid()
        => Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(dataWindow: (5, 0, 2, 1), chunks: 0)));

    [Fact]
    public void Exr_HugeDataWindow_IsRejectedBeforeAllocating()
    {
        byte[] bytes = ExrFile(dataWindow: (0, 0, 999_999, 999_999), chunks: 0);
        Assert.Equal(1_000_000, Image.Identify(bytes).Width);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(bytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024);
    }

    [Fact]
    public void Exr_UnknownCompression_IsInvalid()
        => Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(compression: 200)));

    [Fact]
    public void Exr_LuminanceChroma_IsNotSupported()
    {
        byte[] bytes = ExrFile(channels: new[] { ("BY", 1, 2, 2), ("RY", 1, 2, 2), ("Y", 1, 1, 1) }, dataWindow: (0, 0, 3, 1), chunks: 0);
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(bytes));
        Assert.Contains("luminance/chroma", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exr_SubsampledColourChannel_IsNotSupported()
        => Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(
            ExrFile(channels: new[] { ("R", 1, 2, 1) }, dataWindow: (0, 0, 3, 1), chunks: 0)));

    [Fact]
    public void Exr_SamplingThatDoesNotDivideTheDataWindow_IsInvalid()
        => Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(
            ExrFile(channels: new[] { ("R", 1, 1, 1), ("Z", 1, 2, 2) }, dataWindow: (0, 0, 2, 1), chunks: 0)));

    // ----- OpenEXR: offsets and chunks -----

    [Fact]
    public void Exr_MissingOffsetTable_IsInvalid()
    {
        byte[] bytes = ExrFile();
        Array.Resize(ref bytes, ExrHeaderLength() + 4);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(5UL)]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    public void Exr_OutOfRangeOffset_IsInvalid(ulong offset)
    {
        byte[] bytes = ExrFile();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ExrHeaderLength()), offset);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Fact]
    public void Exr_ChunkForTheWrongLine_IsInvalid()
    {
        byte[] bytes = ExrFile();
        int chunk = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(ExrHeaderLength()));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(chunk), 7);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Fact]
    public void Exr_ChunkSizeBeyondTheFile_IsInvalid()
    {
        byte[] bytes = ExrFile();
        int chunk = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(ExrHeaderLength()));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(chunk + 4), 1_000_000);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void Exr_GarbageCompressedChunk_IsInvalid(int compression)
    {
        // Every chunk payload is shorter than its raw size, so the codec has to run on bytes it cannot decode.
        byte[] bytes = ExrFile(compression: compression, payload: new byte[] { 0x7F, 0x00, 0xFF, 0x13 });
        Exception? ex = HdrExrFixtures.Catch(() => Image.Load<Rgba32>(bytes).Dispose());
        Assert.True(ex is InvalidImageContentException, $"{ExrHeader.CompressionName(compression)}: {ex}");
    }

    [Fact]
    public void Exr_ChunkClaimingAnImpossibleRatio_IsRejectedBeforeAllocating()
    {
        byte[] bytes = ExrFile(compression: 2, dataWindow: (0, 0, 1_999_999, 0), payload: new byte[] { 0x78, 0x9C });
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(bytes, new DecoderOptions { MaxPixels = 4_000_000 }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024 + (2_000_000 * 4));
    }

    [Fact]
    public void Exr_PizWithAnInvalidBitmapRange_IsInvalid()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 9000);
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(compression: 4, payload: payload)));
    }

    [Fact]
    public void Exr_TiledFileWithZeroTileSize_IsInvalid()
    {
        byte[] tiles = new byte[9];
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(ExrFile(version: 2 | 0x200, extra: Attribute("tiles", "tiledesc", tiles))));
    }

    [Fact]
    public void Exr_DamagedFilesFromTheOpenExrProject_KeepTheContract()
    {
        JsonElement[] entries = HdrExrFixtures.Manifest("exr/damaged");
        Assert.True(entries.Length >= 100, "Fixtures/exr/damaged/manifest.json is missing entries.");
        var options = new DecoderOptions { MaxPixels = 4_000_000 };
        var failures = new List<string>();
        foreach (JsonElement entry in entries)
        {
            byte[] bytes = FixturePath.Read($"exr/damaged/{entry.GetProperty("file").GetString()}");
            foreach (Action call in new Action[]
            {
                () => new ExrDecoder().Decode<RgbaVector>(bytes, options).Dispose(),
                () => new ExrDecoder().Decode<Rgba32>(bytes, options).Dispose(),
                () => new ExrDecoder().Identify(bytes, options),
                () => new ExrDecoder { UseDisplayWindow = true }.Decode<RgbaVector>(bytes, options).Dispose(),
            })
            {
                var watch = Stopwatch.StartNew();
                long before = GC.GetAllocatedBytesForCurrentThread();
                Exception? ex = HdrExrFixtures.Catch(call);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (ex is not (null or ImageFormatException or NotSupportedException))
                {
                    failures.Add($"{entry.GetProperty("file").GetString()}: {ex.GetType().Name}: {ex.Message}");
                }

                if (allocated > 100L * 1024 * 1024 || watch.Elapsed > TimeSpan.FromSeconds(30))
                {
                    failures.Add($"{entry.GetProperty("file").GetString()}: {allocated / 1048576.0:F1} MB in {watch.Elapsed.TotalSeconds:F1} s");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    // ----- Radiance HDR -----

    [Fact]
    public void Hdr_OverlongHeaderLine_IsInvalid()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("#?RADIANCE\n" + new string('A', 70_000));
        Assert.Throws<InvalidImageContentException>(() => Image.Load<RgbaVector>(bytes));
    }

    [Theory]
    [InlineData("-Y 99999999999 +X 1")]
    [InlineData("-Y 4 +X")]
    [InlineData("Y 4 X 4")]
    [InlineData("-Y 4 +X 4 extra")]
    [InlineData("-Y -4 +X 4")]
    public void Hdr_MalformedResolutionLine_IsInvalid(string resolution)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n{resolution}\n").Concat(new byte[64]).ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<RgbaVector>(bytes));
        Assert.Throws<InvalidImageContentException>(() => Image.Identify(bytes));
    }

    [Fact]
    public void Hdr_ConflictingFormatLines_AreInvalid()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\nFORMAT=32-bit_rle_xyze\n\n-Y 1 +X 1\n\x80\x80\x80\x80");
        Assert.Throws<InvalidImageContentException>(() => Image.Load<RgbaVector>(bytes));
    }

    [Fact]
    public void Hdr_ExtremeOldStyleRunShift_IsInvalid()
    {
        // Consecutive zero-count run markers raise the shift by 8 each time. After eight of them a 64-bit shift
        // would wrap to zero and turn the ninth marker into a one-pixel run that completes the scanline; the
        // count must instead be treated as too large.
        var data = new List<byte>(Encoding.ASCII.GetBytes("#?RADIANCE\n\n-Y 1 +X 2\n"));
        data.AddRange(new byte[] { 9, 9, 9, 130 });
        for (int i = 0; i < 8; i++)
        {
            data.AddRange(new byte[] { 1, 1, 1, 0 });
        }

        data.AddRange(new byte[] { 1, 1, 1, 1 });

        Assert.Throws<InvalidImageContentException>(() => Image.Load<RgbaVector>(data.ToArray()));
    }

    [Fact]
    public void Hdr_TruncatedAtEveryLength_KeepsTheContract()
    {
        byte[] bytes = HdrExrFixtures.Bytes("hdr", HdrExrFixtures.Entry("hdr", "rle_rgbe"));
        for (int length = 0; length < bytes.Length; length += 7)
        {
            Exception? ex = HdrExrFixtures.Catch(() => new HdrDecoder().Decode<Rgba32>(bytes.AsSpan(0, length)).Dispose());
            Assert.True(ex is InvalidImageContentException, $"length {length}: {ex?.GetType().Name ?? "success"}");
        }
    }

    // ----- A minimal OpenEXR writer for the crafted cases -----

    private static int ExrHeaderLength() => ExrFile(chunks: 0).Length;

    private static byte[] Attribute(string name, string type, byte[] value)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(name));
        bytes.Add(0);
        bytes.AddRange(Encoding.ASCII.GetBytes(type));
        bytes.Add(0);
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, value.Length);
        bytes.AddRange(size);
        bytes.AddRange(value);
        return bytes.ToArray();
    }

    /// <summary>
    /// Builds a scanline OpenEXR file whose HALF R, G, B channels hold (x / 2, 0.5, 0.25) per pixel; with
    /// <paramref name="payload"/> every chunk carries those bytes instead of raw samples.
    /// </summary>
    private static byte[] ExrFile(
        int version = 2,
        int compression = 0,
        (int MinX, int MinY, int MaxX, int MaxY)? dataWindow = null,
        (string Name, int Type, int XSampling, int YSampling)[]? channels = null,
        string? omit = null,
        byte[]? extra = null,
        int? chunks = null,
        byte[]? payload = null)
    {
        (int minX, int minY, int maxX, int maxY) = dataWindow ?? (0, 0, 2, 1);
        channels ??= new[] { ("B", 1, 1, 1), ("G", 1, 1, 1), ("R", 1, 1, 1) };
        var header = new List<byte>();
        var word = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, 20000630);
        header.AddRange(word);
        BinaryPrimitives.WriteInt32LittleEndian(word, version);
        header.AddRange(word);

        var list = new List<byte>();
        foreach ((string name, int type, int xs, int ys) in channels)
        {
            list.AddRange(Encoding.ASCII.GetBytes(name));
            list.Add(0);
            var entry = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(entry, type);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), xs);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), ys);
            list.AddRange(entry);
        }

        list.Add(0);
        var box = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(box, minX);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(4), minY);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(8), maxX);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(12), maxY);
        var attributes = new List<(string Name, byte[] Bytes)>
        {
            ("channels", Attribute("channels", "chlist", list.ToArray())),
            ("compression", Attribute("compression", "compression", new[] { (byte)compression })),
            ("dataWindow", Attribute("dataWindow", "box2i", box)),
            ("displayWindow", Attribute("displayWindow", "box2i", box)),
            ("lineOrder", Attribute("lineOrder", "lineOrder", new byte[] { 0 })),
        };

        foreach ((string name, byte[] bytes) in attributes)
        {
            if (name != omit)
            {
                header.AddRange(bytes);
            }
        }

        if (extra is not null)
        {
            header.AddRange(extra);
        }

        header.Add(0);
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        int lines = compression switch { 3 or 5 => 16, 4 => 32, _ => 1 };
        int count = chunks ?? ((height + lines - 1) / lines);
        long tableEnd = header.Count + (8L * count);
        var body = new List<byte>();
        var offsets = new List<byte>();
        for (int chunk = 0; chunk < count; chunk++)
        {
            var offset = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(offset, (ulong)(tableEnd + body.Count));
            offsets.AddRange(offset);
            int y0 = minY + (chunk * lines);
            int rows = Math.Min(lines, maxY - y0 + 1);
            byte[] data = payload ?? RawLines(width, rows, channels.Length);
            BinaryPrimitives.WriteInt32LittleEndian(word, y0);
            body.AddRange(word);
            BinaryPrimitives.WriteInt32LittleEndian(word, data.Length);
            body.AddRange(word);
            body.AddRange(data);
        }

        return header.Concat(offsets).Concat(body).ToArray();
    }

    private static byte[] RawLines(int width, int rows, int channelCount)
    {
        var data = new byte[width * rows * channelCount * 2];
        int p = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int c = 0; c < channelCount; c++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Channels are B, G, R in storage order.
                    float value = c switch { 0 => 0.25f, 1 => 0.5f, _ => x / 2f };
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p), BitConverter.HalfToUInt16Bits((Half)value));
                    p += 2;
                }
            }
        }

        return data;
    }

    private static int IndexOf(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle));
}
