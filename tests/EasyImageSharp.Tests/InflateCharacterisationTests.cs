using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Pins the PNG decoder's behaviour on the deflate edge cases that a replacement inflater could silently
/// change. Every expectation here was recorded by running the decoder as it stands on top of
/// <see cref="ZLibStream"/> (native zlib/zlib-ng), on net8.0 and net10.0, and both frameworks agreed on
/// every case; none of it is derived from the specification or from what the code looks like it should do.
/// </summary>
/// <remarks>
/// The five behaviours recorded, and where they come from today:
/// <list type="bullet">
/// <item>An IDAT that stops once the last scanline byte has been produced decodes. ZLibStream reports a
/// truncated stream as end-of-stream rather than an error, and the decoder never reads past the byte
/// count the header implies, so a missing final-block terminator and a missing Adler-32 trailer both
/// pass unnoticed. Truncation one byte earlier - anywhere inside the scanlines - is rejected, because
/// the decoder's own <c>ReadExactly</c> turns the short read into a decode failure.</item>
/// <item>A corrupt Adler-32 trailer is rejected. The decoder's trailing "is there more data?" probe read
/// is what drives the inflater over the trailer, and ZLibStream raises a framework exception that
/// <c>DecoderGuard</c> wraps. The assertion is therefore on the exception type only: the message is the
/// framework's, and a managed inflater will word its own differently.</item>
/// <item>Bytes after Z_STREAM_END are ignored - one byte, arbitrary garbage, or an entire second zlib
/// stream - because nothing ever reads the compressed input past the end of the first stream.</item>
/// <item>Deflate output longer than the image needs is rejected by the decoder's explicit probe read, not
/// by the inflater. This is the one case of the five with a message the decoder owns.</item>
/// <item>Zero-length IDAT chunks are concatenated away and have no effect, in any position; a PNG whose
/// IDAT chunks are all empty is rejected as having no IDAT at all.</item>
/// </list>
/// Chunk CRCs are deliberately not verified on decode, which is what lets every crafted input here reach
/// the inflater at all; <see cref="ChunkCrcsAreNotVerified"/> pins that too, because a decoder that
/// started checking them would reject most of this file's inputs before the deflate behaviour mattered.
/// </remarks>
public class InflateCharacterisationTests
{
    private const int Width = 4;
    private const int Height = 4;

    /// <summary>Colour type 2 at bit depth 8: three bytes per pixel, one filter byte per scanline.</summary>
    private const int BytesPerPixel = 3;

    private const int ScanlineStride = 1 + (Width * BytesPerPixel);

    /// <summary>The exact number of inflated bytes the header calls for: 4 rows of 1 + 12 bytes.</summary>
    private const int InflatedSize = ScanlineStride * Height;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // =====================================================================================================
    // 1. IDAT truncated after the last scanline - accepted, and it decodes to the right pixels
    // =====================================================================================================

    /// <summary>
    /// The complete stream minus its four-byte Adler-32 trailer. The trailer's length is fixed by the zlib
    /// container, so this truncation is exact whatever the deflate encoder did with the payload.
    /// </summary>
    [Fact]
    public void TruncatedAfterLastScanline_AdlerTrailerMissing_Decodes()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Scanlines(), final: true, withAdler: false)));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>Half a trailer is as tolerated as none: the decoder stops before it is ever needed.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TruncatedAfterLastScanline_AdlerTrailerPartial_Decodes(int missingTrailerBytes)
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);
        byte[] png = Png(Ihdr(), Chunk("IDAT", zlib[..^missingTrailerBytes]));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>
    /// The block that carries the scanlines is not marked final and nothing follows it, so a conforming
    /// inflater is still waiting for another block when the input runs out. Accepted today.
    /// </summary>
    [Fact]
    public void TruncatedAfterLastScanline_NoFinalBlockTerminator_Decodes()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Scanlines(), final: false, withAdler: false)));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>The same truncation on a Huffman-coded stream rather than stored blocks.</summary>
    [Fact]
    public void TruncatedAfterLastScanline_CompressedStreamWithoutItsTrailer_Decodes()
    {
        byte[] compressed = Deflate(Scanlines());
        byte[] png = Png(Ihdr(), Chunk("IDAT", compressed[..^4]));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>Interlaced images take the same path seven times over; truncation is tolerated there too.</summary>
    [Fact]
    public void TruncatedAfterLastScanline_Interlaced_Decodes()
    {
        byte[] control = Png(Ihdr(interlace: 1), Chunk("IDAT", Stored(InterlacedScanlines(), final: true, withAdler: true)));
        byte[] png = Png(Ihdr(interlace: 1), Chunk("IDAT", Stored(InterlacedScanlines(), final: false, withAdler: false)));

        AssertDecodesLike(control, png);
    }

    // =====================================================================================================
    // 1b. Truncation that loses a scanline byte - rejected, through the decoder's own short-read guard
    // =====================================================================================================

    /// <summary>
    /// One byte earlier than <see cref="TruncatedAfterLastScanline_AdlerTrailerMissing_Decodes"/>: the last
    /// scanline byte is gone, so ZLibStream's end-of-stream arrives mid-row.
    /// </summary>
    [Fact]
    public void TruncatedInsideTheScanlines_OneByteShort_Throws()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: false);
        byte[] png = Png(Ihdr(), Chunk("IDAT", zlib[..^1]));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("ended unexpectedly", ex.Message);
    }

    [Fact]
    public void TruncatedInsideTheScanlines_HalfTheCompressedStream_Throws()
    {
        byte[] compressed = Deflate(Scanlines());
        byte[] png = Png(Ihdr(), Chunk("IDAT", compressed[..(compressed.Length / 2)]));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("ended unexpectedly", ex.Message);
    }

    /// <summary>A stream too short to hold even a complete zlib header still fails as a short read, not as a header error.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TruncatedInsideTheScanlines_ZlibHeaderOnly_Throws(int prefixLength)
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Deflate(Scanlines())[..prefixLength]));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("ended unexpectedly", ex.Message);
    }

    /// <summary>A complete, valid zlib stream that inflates to nothing is a short read, not an empty image.</summary>
    [Fact]
    public void TruncatedInsideTheScanlines_StreamInflatesToNothing_Throws()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Array.Empty<byte>(), final: true, withAdler: true)));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("ended unexpectedly", ex.Message);
    }

    // =====================================================================================================
    // 2. Corrupted ADLER-32 trailer - rejected
    // =====================================================================================================

    /// <summary>
    /// The trailer is checked even though the decoder has already produced every pixel it needs, because the
    /// probe read that looks for surplus data pulls the inflater over the trailer first. Only the exception
    /// type is asserted: today the message is the framework's, surfaced through <c>DecoderGuard.Wrap</c>
    /// ("Malformed PNG data: ...") rather than a message the decoder owns.
    /// </summary>
    /// <param name="trailerByte">Index into the four-byte trailer, counted from its start.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CorruptAdlerTrailer_Stored_Throws(int trailerByte)
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);
        zlib[zlib.Length - 4 + trailerByte] ^= 0xFF;
        byte[] png = Png(Ihdr(), Chunk("IDAT", zlib));

        Assert.Throws<InvalidImageContentException>(() => Decode(png));
    }

    [Fact]
    public void CorruptAdlerTrailer_Compressed_Throws()
    {
        byte[] compressed = Deflate(Scanlines());
        compressed[^1] ^= 0xFF;
        byte[] png = Png(Ihdr(), Chunk("IDAT", compressed));

        Assert.Throws<InvalidImageContentException>(() => Decode(png));
    }

    // =====================================================================================================
    // 3. Bytes after Z_STREAM_END - ignored
    // =====================================================================================================

    [Fact]
    public void AfterStreamEnd_SingleTrailingByte_Ignored()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Concat(Stored(Scanlines(), final: true, withAdler: true), new byte[] { 0x00 })));

        AssertDecodesLike(ControlPng(), png);
    }

    [Fact]
    public void AfterStreamEnd_TrailingGarbage_Ignored()
    {
        byte[] garbage = new byte[16];
        for (int i = 0; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(i * 37);
        }

        byte[] png = Png(Ihdr(), Chunk("IDAT", Concat(Stored(Scanlines(), final: true, withAdler: true), garbage)));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>A whole second zlib stream is surplus input, not a second image and not an error.</summary>
    [Fact]
    public void AfterStreamEnd_SecondCompleteZlibStream_Ignored()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);
        byte[] png = Png(Ihdr(), Chunk("IDAT", Concat(zlib, zlib)));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>Surplus bytes in a later IDAT chunk are the same thing once the chunks are concatenated.</summary>
    [Fact]
    public void AfterStreamEnd_GarbageInALaterIdatChunk_Ignored()
    {
        byte[] png = Png(
            Ihdr(),
            Chunk("IDAT", Stored(Scanlines(), final: true, withAdler: true)),
            Chunk("IDAT", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));

        AssertDecodesLike(ControlPng(), png);
    }

    // =====================================================================================================
    // 4. Deflate output longer than the image needs - rejected
    // =====================================================================================================

    /// <summary>
    /// Surplus inflated bytes are the decoder's own check, not the inflater's: after the last scanline it
    /// reads one more byte and fails if anything comes back. The message is therefore the decoder's.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(ScanlineStride)]
    [InlineData(4096)]
    public void OutputLongerThanTheImage_Stored_Throws(int surplusBytes)
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Concat(Scanlines(), new byte[surplusBytes]), final: true, withAdler: true)));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("longer than the image dimensions allow", ex.Message);
    }

    [Fact]
    public void OutputLongerThanTheImage_Compressed_Throws()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Deflate(Concat(Scanlines(), new byte[64]))));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("longer than the image dimensions allow", ex.Message);
    }

    /// <summary>
    /// Surplus data wins over a missing trailer: the surplus is found by the probe read, which is the same
    /// read that would otherwise have validated the trailer.
    /// </summary>
    [Fact]
    public void OutputLongerThanTheImage_AndTrailerMissing_ThrowsForTheLength()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Concat(Scanlines(), new byte[64]), final: true, withAdler: false)));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("longer than the image dimensions allow", ex.Message);
    }

    [Fact]
    public void OutputLongerThanTheImage_Interlaced_Throws()
    {
        byte[] png = Png(
            Ihdr(interlace: 1),
            Chunk("IDAT", Stored(Concat(InterlacedScanlines(), new byte[8]), final: true, withAdler: true)));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("longer than the image dimensions allow", ex.Message);
    }

    // =====================================================================================================
    // 5. Zero-length IDAT chunks
    // =====================================================================================================

    [Fact]
    public void EmptyIdat_BeforeTheData_Decodes()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Array.Empty<byte>()), Chunk("IDAT", Stored(Scanlines(), final: true, withAdler: true)));

        AssertDecodesLike(ControlPng(), png);
    }

    [Fact]
    public void EmptyIdat_AfterTheData_Decodes()
    {
        byte[] png = Png(Ihdr(), Chunk("IDAT", Stored(Scanlines(), final: true, withAdler: true)), Chunk("IDAT", Array.Empty<byte>()));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>An empty chunk splitting the stream mid-deflate is invisible once the payloads are concatenated.</summary>
    [Fact]
    public void EmptyIdat_BetweenTwoHalvesOfTheStream_Decodes()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);
        byte[] png = Png(Ihdr(), Chunk("IDAT", zlib[..5]), Chunk("IDAT", Array.Empty<byte>()), Chunk("IDAT", zlib[5..]));

        AssertDecodesLike(ControlPng(), png);
    }

    [Fact]
    public void EmptyIdat_AroundEveryFragment_Decodes()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);
        byte[] png = Png(
            Ihdr(),
            Chunk("IDAT", Array.Empty<byte>()),
            Chunk("IDAT", zlib[..2]),
            Chunk("IDAT", Array.Empty<byte>()),
            Chunk("IDAT", zlib[2..7]),
            Chunk("IDAT", Array.Empty<byte>()),
            Chunk("IDAT", zlib[7..]),
            Chunk("IDAT", Array.Empty<byte>()));

        AssertDecodesLike(ControlPng(), png);
    }

    /// <summary>
    /// IDAT chunks that are all empty are counted by their total payload length, so the image reads as
    /// having no IDAT at all - it never reaches the inflater.
    /// </summary>
    [Fact]
    public void EmptyIdat_NothingElse_ThrowsForAMissingIdat()
    {
        byte[] empty = Chunk("IDAT", Array.Empty<byte>());
        byte[] png = Png(Ihdr(), empty, empty, Chunk("IEND", Array.Empty<byte>()));

        InvalidImageContentException ex = Assert.Throws<InvalidImageContentException>(() => Decode(png));
        Assert.Contains("missing its IHDR or IDAT chunks", ex.Message);
    }

    // =====================================================================================================
    // Chunk CRCs
    // =====================================================================================================

    /// <summary>
    /// The decoder does not verify chunk CRCs, so corruption is only ever caught by the deflate stream or by
    /// the chunk contents disagreeing with the header. Recorded because it is the reason the cases above
    /// reach the inflater at all, and because a CRC check added later would reject them first.
    /// </summary>
    [Fact]
    public void ChunkCrcsAreNotVerified()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);

        AssertDecodesLike(ControlPng(), Png(Ihdr(), BreakCrc(Chunk("IDAT", zlib))));
        AssertDecodesLike(ControlPng(), Png(BreakCrc(Ihdr()), Chunk("IDAT", zlib)));
        AssertDecodesLike(ControlPng(), Png(Ihdr(), BreakCrc(Chunk("IDAT", Array.Empty<byte>())), Chunk("IDAT", zlib)));
    }

    /// <summary>The control itself: a well-formed image, with and without IEND, decodes.</summary>
    [Fact]
    public void ControlImageDecodes()
    {
        byte[] zlib = Stored(Scanlines(), final: true, withAdler: true);

        AssertDecodesLike(ControlPng(), Png(Ihdr(), Chunk("IDAT", zlib), Chunk("IEND", Array.Empty<byte>())));
        AssertDecodesLike(ControlPng(), Png(Ihdr(), Chunk("IDAT", Deflate(Scanlines()))));
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>A well-formed PNG carrying <see cref="Scanlines"/>, used as the reference decode.</summary>
    private static byte[] ControlPng() => Png(Ihdr(), Chunk("IDAT", Stored(Scanlines(), final: true, withAdler: true)));

    private static void AssertDecodesLike(byte[] control, byte[] png) => Assert.Equal(Decode(control), Decode(png));

    private static Rgba32[] Decode(byte[] png)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(png);
        var pixels = new Rgba32[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = image[x, y];
            }
        }

        return pixels;
    }

    private static byte[] Png(params byte[][] chunks)
    {
        int length = PngSignature.Length;
        foreach (byte[] chunk in chunks)
        {
            length += chunk.Length;
        }

        byte[] file = new byte[length];
        PngSignature.CopyTo(file, 0);
        int offset = PngSignature.Length;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(file, offset);
            offset += chunk.Length;
        }

        return file;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Append(0, chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static byte[] BreakCrc(byte[] chunk)
    {
        byte[] broken = (byte[])chunk.Clone();
        broken[^1] ^= 0xFF;
        return broken;
    }

    private static byte[] Ihdr(byte interlace = 0)
    {
        byte[] payload = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(payload, Width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), Height);
        payload[8] = 8;  // bit depth
        payload[9] = 2;  // colour type: truecolor
        payload[12] = interlace;
        return Chunk("IHDR", payload);
    }

    /// <summary>The filtered scanlines of a non-interlaced 4x4 truecolor image: <see cref="InflatedSize"/> bytes.</summary>
    private static byte[] Scanlines()
    {
        byte[] raw = new byte[InflatedSize];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % ScanlineStride == 0 ? 0 : (i * 7) & 0xFF);
        }

        return raw;
    }

    /// <summary>The same image laid out as the seven Adam7 passes, each row prefixed with filter type 0.</summary>
    private static byte[] InterlacedScanlines()
    {
        int[] xStart = { 0, 4, 0, 2, 0, 1, 0 };
        int[] yStart = { 0, 0, 4, 0, 2, 0, 1 };
        int[] xStep = { 8, 8, 4, 4, 2, 2, 1 };
        int[] yStep = { 8, 8, 8, 4, 4, 2, 2 };

        var bytes = new List<byte>();
        for (int pass = 0; pass < 7; pass++)
        {
            int passWidth = (Width - xStart[pass] + xStep[pass] - 1) / xStep[pass];
            int passHeight = (Height - yStart[pass] + yStep[pass] - 1) / yStep[pass];
            if (passWidth <= 0 || passHeight <= 0)
            {
                continue;
            }

            for (int row = 0; row < passHeight; row++)
            {
                bytes.Add(0);
                for (int i = 0; i < passWidth * BytesPerPixel; i++)
                {
                    bytes.Add((byte)((pass * 31) + (row * 7) + i));
                }
            }
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// A zlib container holding <paramref name="raw"/> in stored (uncompressed) deflate blocks, so that every
    /// byte of the framing is placed deliberately and a truncation can be expressed exactly.
    /// </summary>
    /// <param name="raw">The bytes to store.</param>
    /// <param name="final">Whether the last block is marked BFINAL; false leaves the stream unterminated.</param>
    /// <param name="withAdler">Whether the four-byte Adler-32 trailer is appended.</param>
    private static byte[] Stored(byte[] raw, bool final, bool withAdler)
    {
        var bytes = new List<byte> { 0x78, 0x01 }; // CM=8, CINFO=7, FLEVEL=0, no preset dictionary, check bits valid.
        int offset = 0;
        do
        {
            int length = Math.Min(0xFFFF, raw.Length - offset);
            bool last = offset + length >= raw.Length;
            bytes.Add((byte)(last && final ? 1 : 0));
            bytes.Add((byte)(length & 0xFF));
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)(~length & 0xFF));
            bytes.Add((byte)((~length >> 8) & 0xFF));
            for (int i = 0; i < length; i++)
            {
                bytes.Add(raw[offset + i]);
            }

            offset += length;
        }
        while (offset < raw.Length);

        if (withAdler)
        {
            uint adler = Adler32Of(raw);
            bytes.Add((byte)(adler >> 24));
            bytes.Add((byte)(adler >> 16));
            bytes.Add((byte)(adler >> 8));
            bytes.Add((byte)adler);
        }

        return bytes.ToArray();
    }

    /// <summary>The same bytes as a Huffman-coded zlib stream, produced by the framework rather than by this library.</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return buffer.ToArray();
    }

    /// <summary>Adler-32, computed here rather than taken from the library so the fixtures stay independent of it.</summary>
    private static uint Adler32Of(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
