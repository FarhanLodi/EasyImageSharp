using System.Buffers.Binary;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

public sealed class TiffRegionTests
{
    [Theory]
    [InlineData("pil_rgb8_lzw")]
    [InlineData("pil_rgb8_deflate")]
    [InlineData("pil_gray8_raw")]
    [InlineData("pil_gray8_packbits")]
    [InlineData("pil_gray8_lzw")]
    [InlineData("pil_gray8_deflate_pred2")]
    [InlineData("pil_gray8_rps4")]
    [InlineData("hand_rgb8_tiled_raw")]
    [InlineData("hand_rgb8_tiled_lzw_pred2")]
    [InlineData("hand_gray8_tiled_packbits")]
    [InlineData("hand_gray16_tiled_deflate")]
    [InlineData("hand_rgb16_mm_lzw_pred2")]
    [InlineData("hand_gray8_deflate_mm")]
    [InlineData("hand_gray8_deflate_32946")]
    [InlineData("hand_gray8_minwhite")]
    [InlineData("hand_rgba8_lzw_pred2")]
    [InlineData("hand_rgb8_last_strip_short")]
    [InlineData("bigtiff_le_rgb")]
    [InlineData("bigtiff_be_rgb")]
    [InlineData("bigtiff_tiled")]
    [InlineData("classic_long8_offsets")]
    public void RegionsMatchIndependentFixturePixels(string name)
    {
        using Image<Rgba32> expected = Image.Load<Rgba32>(FixturePath.Get($"tiff/{name}.expected.png"));
        using var stream = File.OpenRead(FixturePath.Get($"tiff/{name}.tif"));
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream);
        Assert.Equal(expected.Width, reader.Width);
        Assert.Equal(expected.Height, reader.Height);
        TiffRegion[] regions =
        [
            new(0, 0, expected.Width, expected.Height),
            new(1, 1, expected.Width - 2, expected.Height - 2),
            new(expected.Width - 1, expected.Height - 1, 1, 1),
        ];
        foreach (TiffRegion region in regions)
        {
            using Image<Rgba32> result = reader.ReadRegion<Rgba32>(region);
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    Assert.Equal(expected[(int)region.X + x, (int)region.Y + y], result[x, y]);
                }
            }
        }
    }

    [Theory]
    [InlineData("pil_gray16_raw")]
    [InlineData("hand_gray16_mm")]
    [InlineData("hand_gray16_ii_lzw_pred2")]
    [InlineData("hand_gray16_tiled_deflate")]
    [InlineData("hand_rgb16_raw")]
    [InlineData("hand_rgb16_mm_lzw_pred2")]
    public void RegionPreservesSixteenBitSamples(string name)
    {
        using Image<Rgba64> expected = Image.Load<Rgba64>(FixturePath.Get($"tiff/{name}.tif"));
        using var stream = File.OpenRead(FixturePath.Get($"tiff/{name}.tif"));
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream);
        using Image<Rgba64> result = reader.ReadRegion<Rgba64>(new(1, 1, expected.Width - 2, expected.Height - 2));
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                Assert.Equal(expected[x + 1, y + 1], result[x, y]);
            }
        }
    }

    [Fact]
    public void BigTiff_GigapixelTileTablesAreReadLazilyBeyondTwoGiB()
    {
        const long width = 100_000, height = 100_000, across = 6250, tileCount = across * across;
        const long offsetTable = 1024, countTable = offsetTable + (tileCount * 8), payload = (3L << 30) + 4096;
        long lastIndex = tileCount - 1;
        using var stream = new SparseStream(payload + 768);
        stream.Put(0, BigHeader(
            (256, 16, 1, width), (257, 16, 1, height), (258, 3, 1, 8), (259, 3, 1, 1),
            (262, 3, 1, 2), (277, 3, 1, 3), (322, 4, 1, 16), (323, 4, 1, 16),
            (324, 16, tileCount, offsetTable), (325, 16, tileCount, countTable)));
        stream.Put(offsetTable + (lastIndex * 8), U64(payload));
        stream.Put(countTable + (lastIndex * 8), U64(768));
        byte[] tile = new byte[768];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int i = ((y * 16) + x) * 3;
                tile[i] = (byte)(x + 10);
                tile[i + 1] = (byte)(y + 20);
                tile[i + 2] = (byte)(x + y + 30);
            }
        }

        stream.Put(payload, tile);
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream, options: new() { MaxReadBytes = 4096 });
        using Image<Rgb24> result = reader.ReadRegion<Rgb24>(new(99_990, 99_990, 3, 4));
        Assert.True(reader.IsBigTiff);
        Assert.Equal(new Rgb24(16, 26, 42), result[0, 0]);
        Assert.Equal(new Rgb24(18, 29, 47), result[2, 3]);
        Assert.True(stream.BytesRead < 2048, $"Read {stream.BytesRead} bytes for a tiny ROI.");
        Assert.True(stream.MaximumReadOffset > int.MaxValue);
    }

    [Fact]
    public void HugeUncompressedStrip_ReadsOnlyRequestedBytesAtLongCoordinates()
    {
        const long width = 3_000_000_000, payload = 4L << 30;
        using var stream = new SparseStream(payload + width);
        stream.Put(0, BigHeader(
            (256, 16, 1, width), (257, 16, 1, 1), (258, 3, 1, 8), (259, 3, 1, 1), (262, 3, 1, 1),
            (273, 16, 1, payload), (277, 3, 1, 1), (278, 16, 1, 1), (279, 16, 1, width)));
        stream.Put(payload + width - 10, [41, 42, 43]);
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream, options: new()
        {
            MaxReadBytes = 4096, MaxEncodedSegmentBytes = 16, MaxDecodedSegmentBytes = 16,
        });
        using Image<L8> result = reader.ReadRegion<L8>(new(width - 10, 0, 3, 1));
        Assert.Equal(width, reader.Width);
        Assert.Equal(41, result[0, 0].PackedValue);
        Assert.Equal(43, result[2, 0].PackedValue);
        Assert.True(stream.BytesRead < 1024);
    }

    [Fact]
    public void OutputTileEnumerationIsLazyAndDoesNotLeakExecutionScope()
    {
        using var stream = File.OpenRead(FixturePath.Get("tiff/hand_rgb8_tiled_raw.tif"));
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream, options: new()
        {
            ExecutionOptions = new() { MaxPixelBufferBytes = 24 }, MaxTiles = 2,
        });
        using IEnumerator<TiffRegionTile<Rgb24>> tiles = reader.ReadTiles<Rgb24>(2, 2).GetEnumerator();
        Assert.True(tiles.MoveNext());
        using TiffRegionTile<Rgb24> first = tiles.Current;
        Assert.Equal(new TiffRegion(0, 0, 2, 2), first.Region);
        // The suspended iterator must not leave its small allocation budget active for unrelated work.
        using var unrelated = new Image<Rgba32>(20, 20);
        Assert.True(tiles.MoveNext());
        using TiffRegionTile<Rgb24> second = tiles.Current;
        Assert.Equal(new TiffRegion(2, 0, 2, 2), second.Region);
        Assert.Throws<ImageSizeLimitExceededException>(() => tiles.MoveNext());
    }

    [Theory]
    [InlineData("pil_bilevel_raw")]
    [InlineData("pil_float32")]
    [InlineData("hand_planar2_rgb8")]
    [InlineData("hand_rgba8_assoc_alpha")]
    [InlineData("pil_pal8_raw")]
    [InlineData("hand_rgb8_raw_pred2")]
    public void UnsupportedLayoutsAreExplicitlyRejected(string name)
    {
        using var stream = File.OpenRead(FixturePath.Get($"tiff/{name}.tif"));
        Assert.Throws<NotSupportedException>(() => Image.OpenTiffRegionReader(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void BudgetsCancellationBoundsAndOwnershipAreEnforced()
    {
        byte[] bytes = File.ReadAllBytes(FixturePath.Get("tiff/pil_rgb8_lzw.tif"));
        using var stream = new MemoryStream(bytes);
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream, options: new() { MaxDecodedSegmentBytes = 1 });
        Assert.Throws<ImageSizeLimitExceededException>(() => reader.ReadRegion<Rgb24>(new(0, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRegion<Rgb24>(new(long.MaxValue, 0, 2, 2)));
        Assert.Throws<OperationCanceledException>(() => reader.ReadRegion<Rgb24>(new(0, 0, 1, 1), new CancellationToken(true)));
        reader.Dispose();
        Assert.True(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRegion<Rgb24>(new(0, 0, 1, 1)));

        using var ownedStream = new MemoryStream(bytes);
        using (Image.OpenTiffRegionReader(ownedStream, leaveOpen: false)) { }
        Assert.False(ownedStream.CanRead);
        using var tooSmall = new MemoryStream(bytes);
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.OpenTiffRegionReader(tooSmall, options: new() { MaxReadBytes = 8 }, leaveOpen: false));
        Assert.True(tooSmall.CanRead);
    }

    [Fact]
    public void NonzeroStreamOriginAndIndependentPageSelectionWork()
    {
        byte[] bytes = File.ReadAllBytes(FixturePath.Get("tiff/bigtiff_multipage.tif"));
        using Image<Rgb24> all = Image.Load<Rgb24>(bytes);
        using var stream = new MemoryStream();
        stream.Write(new byte[51]);
        stream.Write(bytes);
        stream.Position = 51;
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream, pageIndex: 1);
        using Image<Rgb24> region = reader.ReadRegion<Rgb24>(new(0, 0, (int)reader.Width, (int)reader.Height));
        Assert.Equal(all.Frames[1].Width, region.Width);
        for (int y = 0; y < region.Height; y++)
        {
            Assert.True(all.Frames[1].GetRowSpan(y).SequenceEqual(region.Frames.RootFrame.GetRowSpan(y)));
        }
    }

    [Fact]
    public void DirectoryCyclesAndTruncatedSegmentsFailWithoutLargeReads()
    {
        byte[] header = BigHeader((256, 16, 1, 4), (257, 16, 1, 4));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(header.Length - 8), 16);
        using var cycle = new MemoryStream(header);
        Assert.Throws<InvalidImageContentException>(() => Image.OpenTiffRegionReader(cycle, pageIndex: 1));

        using var truncated = new SparseStream(1024);
        truncated.Put(0, BigHeader((256, 16, 1, 4), (257, 16, 1, 4), (258, 3, 1, 8), (262, 3, 1, 1),
            (273, 16, 1, 1000), (279, 16, 1, 100)));
        using TiffRegionReader reader = Image.OpenTiffRegionReader(truncated);
        Assert.Throws<InvalidImageContentException>(() => reader.ReadRegion<L8>(new(0, 0, 1, 1)));
    }

    [Fact]
    public void CompressedRegionNeverReadsAnUnselectedCorruptTile()
    {
        // Two 16x16 PackBits gray tiles. The right tile points outside the source;
        // opening and reading the left tile must not inspect that unused payload.
        using var stream = new SparseStream(4096);
        stream.Put(0, BigHeader((256, 16, 1, 32), (257, 16, 1, 16), (258, 3, 1, 8),
            (259, 3, 1, 32773), (262, 3, 1, 1), (322, 16, 1, 16), (323, 16, 1, 16),
            (324, 16, 2, 512), (325, 16, 2, 528)));
        stream.Put(512, U64(1024).Concat(U64(8192)).ToArray());
        stream.Put(528, U64(4).Concat(U64(4)).ToArray());
        stream.Put(1024, [129, 73, 129, 73]); // Two 128-byte repeats.
        using TiffRegionReader reader = Image.OpenTiffRegionReader(stream);
        using Image<L8> region = reader.ReadRegion<L8>(new(3, 5, 2, 2));
        Assert.Equal(new L8(73), region[1, 1]);
        Assert.True(stream.MaximumReadOffset < 2048);
        Assert.Throws<InvalidImageContentException>(() => reader.ReadRegion<L8>(new(16, 0, 1, 1)));
    }

    private static byte[] BigHeader(params (ushort Tag, ushort Type, long Count, long Value)[] fields)
    {
        byte[] bytes = new byte[16 + 8 + (fields.Length * 20) + 8];
        bytes[0] = bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)fields.Length);
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            Span<byte> entry = bytes.AsSpan(24 + (i * 20), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, field.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], field.Type);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[4..], (ulong)field.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[12..], (ulong)field.Value);
        }

        return bytes;
    }

    private static byte[] U64(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value);
        return bytes;
    }

    /// <summary>Huge virtual file with only selected byte ranges backed by memory; records real read volume.</summary>
    private sealed class SparseStream(long length) : Stream
    {
        private readonly List<(long Offset, byte[] Bytes)> chunks = new();
        private long position;
        public long BytesRead { get; private set; }
        public long MaximumReadOffset { get; private set; }
        public void Put(long offset, byte[] bytes) => this.chunks.Add((offset, bytes));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => this.position; set => this.position = value; }
        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - this.position);
            Span<byte> output = buffer[..count];
            output.Clear();
            foreach ((long offset, byte[] bytes) in this.chunks)
            {
                long left = Math.Max(offset, this.position), right = Math.Min(offset + bytes.Length, this.position + count);
                if (right > left)
                {
                    bytes.AsSpan((int)(left - offset), (int)(right - left)).CopyTo(output[(int)(left - this.position)..]);
                }
            }

            this.MaximumReadOffset = Math.Max(this.MaximumReadOffset, this.position);
            this.position += count;
            this.BytesRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            this.position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => this.position + offset, _ => length + offset };
            return this.position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
