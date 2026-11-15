using System.Buffers.Binary;
using System.IO.Compression;

namespace EasyImageSharp.Formats.Exr;

/// <summary>
/// The byte-oriented OpenEXR chunk codecs: RLE, ZIP/ZIPS and PXR24, plus the byte predictor and the even/odd
/// interleave that RLE and ZIP share. Every decoder writes exactly the uncompressed chunk size it is given and
/// throws <see cref="InvalidImageContentException"/> when the stream produces more or fewer bytes.
/// </summary>
internal static class ExrCodecs
{
    /// <summary>Undoes the RLE/ZIP preprocessing: the running byte predictor, then the split into even and odd bytes.</summary>
    public static void UndoPredictorAndInterleave(Span<byte> scratch, Span<byte> output)
    {
        int n = scratch.Length;
        for (int i = 1; i < n; i++)
        {
            scratch[i] = (byte)(scratch[i - 1] + scratch[i] - 128);
        }

        int half = (n + 1) / 2;
        int s = 0;
        for (int i = 0; s < n; i++)
        {
            output[s++] = scratch[i];
            if (s < n)
            {
                output[s++] = scratch[half + i];
            }
        }
    }

    /// <summary>The RLE/ZIP preprocessing: split into even and odd bytes, then a running byte predictor.</summary>
    public static void ApplyInterleaveAndPredictor(ReadOnlySpan<byte> input, Span<byte> scratch)
    {
        int n = input.Length;
        int half = (n + 1) / 2;
        for (int i = 0; i < n; i++)
        {
            scratch[(i & 1) == 0 ? i >> 1 : half + (i >> 1)] = input[i];
        }

        int previous = n > 0 ? scratch[0] : 0;
        for (int i = 1; i < n; i++)
        {
            int current = scratch[i];
            scratch[i] = (byte)(current - previous + 128 + 256);
            previous = current;
        }
    }

    /// <summary>
    /// OpenEXR run-length decoding: a signed count byte, where <c>-n</c> introduces <c>n</c> literal bytes and
    /// <c>n &gt;= 0</c> repeats the next byte <c>n + 1</c> times.
    /// </summary>
    public static void RleDecode(ReadOnlySpan<byte> packed, Span<byte> output)
    {
        int pos = 0;
        int written = 0;
        while (pos < packed.Length)
        {
            int count = (sbyte)packed[pos++];
            if (count < 0)
            {
                count = -count;
                if (pos + count > packed.Length || written + count > output.Length)
                {
                    throw new InvalidImageContentException("OpenEXR RLE chunk is malformed.");
                }

                packed.Slice(pos, count).CopyTo(output[written..]);
                pos += count;
                written += count;
            }
            else
            {
                if (pos >= packed.Length || written + count + 1 > output.Length)
                {
                    throw new InvalidImageContentException("OpenEXR RLE chunk is malformed.");
                }

                output.Slice(written, count + 1).Fill(packed[pos++]);
                written += count + 1;
            }
        }

        if (written != output.Length)
        {
            throw new InvalidImageContentException("OpenEXR RLE chunk decodes to fewer bytes than its layout requires.");
        }
    }

    /// <summary>
    /// OpenEXR run-length encoding into <paramref name="output"/>, which must hold at least
    /// <c>input.Length + input.Length / 127 + 1</c> bytes; returns the encoded length. Runs of three or more equal
    /// bytes become run records (up to 128 copies), everything else literal records (up to 127 bytes).
    /// </summary>
    public static int RleEncode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int n = input.Length;
        int i = 0;
        int p = 0;
        while (i < n)
        {
            int run = 1;
            while (i + run < n && run < 128 && input[i + run] == input[i])
            {
                run++;
            }

            if (run >= 3)
            {
                output[p++] = (byte)(run - 1);
                output[p++] = input[i];
                i += run;
                continue;
            }

            // Collect literals until the next run of three or the record limit.
            int start = i;
            while (i < n && i - start < 127)
            {
                if (i + 2 < n && input[i] == input[i + 1] && input[i] == input[i + 2])
                {
                    break;
                }

                i++;
            }

            int length = i - start;
            output[p++] = (byte)(-length);
            input.Slice(start, length).CopyTo(output[p..]);
            p += length;
        }

        return p;
    }

    /// <summary>Inflates a complete zlib stream into exactly <paramref name="destination"/>.</summary>
    public static void InflateExact(ReadOnlySpan<byte> source, Span<byte> destination, string label)
    {
        var inflater = new Inflater(destination.Length, label);
        try
        {
            inflater.SetInput(source);
            int written = inflater.ReadInto(destination);
            if (written != destination.Length)
            {
                throw new InvalidImageContentException($"{label} decompresses to fewer bytes than its layout requires.");
            }

            inflater.Fill(1);
            if (inflater.Available > 0)
            {
                throw new InvalidImageContentException($"{label} decompresses to more bytes than its layout requires.");
            }

            if (!inflater.Finished)
            {
                throw new InvalidImageContentException($"{label} compressed data is truncated.");
            }
        }
        finally
        {
            inflater.Dispose();
        }
    }

    /// <summary>Deflates <paramref name="input"/> as a zlib stream.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> input, CompressionLevel level)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// PXR24 decoding of one chunk. The inflated data holds, per scanline and channel, the samples' byte planes
    /// (most significant first) of running differences: two planes for HALF, three for FLOAT (the low byte of
    /// the 32-bit pattern was dropped) and four for UINT. The reconstruction is written to
    /// <paramref name="output"/> in the ordinary little-endian chunk layout.
    /// </summary>
    public static void Pxr24Decode(
        ReadOnlySpan<byte> packed, Span<byte> output, ExrChannel[] channels, int minX, int maxX, int minY, int maxY, byte[] scratch)
    {
        int planesSize = 0;
        for (int y = minY; y <= maxY; y++)
        {
            foreach (ExrChannel channel in channels)
            {
                if (ExrHeader.FloorMod(y, channel.YSampling) == 0)
                {
                    int n = ExrHeader.SampleCount(channel.XSampling, minX, maxX);
                    planesSize += n * (channel.PixelType == ExrChannel.Half ? 2 : channel.PixelType == ExrChannel.Float ? 3 : 4);
                }
            }
        }

        Span<byte> planes = scratch.AsSpan(0, planesSize);
        InflateExact(packed, planes, "OpenEXR PXR24 chunk");

        int read = 0;
        int write = 0;
        for (int y = minY; y <= maxY; y++)
        {
            foreach (ExrChannel channel in channels)
            {
                if (ExrHeader.FloorMod(y, channel.YSampling) != 0)
                {
                    continue;
                }

                int n = ExrHeader.SampleCount(channel.XSampling, minX, maxX);
                uint pixel = 0;
                switch (channel.PixelType)
                {
                    case ExrChannel.Half:
                        for (int j = 0; j < n; j++)
                        {
                            pixel += (uint)((planes[read + j] << 8) | planes[read + n + j]);
                            BinaryPrimitives.WriteUInt16LittleEndian(output[write..], (ushort)pixel);
                            write += 2;
                        }

                        read += 2 * n;
                        break;
                    case ExrChannel.Float:
                        for (int j = 0; j < n; j++)
                        {
                            pixel += ((uint)planes[read + j] << 24) | ((uint)planes[read + n + j] << 16) | ((uint)planes[read + (2 * n) + j] << 8);
                            BinaryPrimitives.WriteUInt32LittleEndian(output[write..], pixel);
                            write += 4;
                        }

                        read += 3 * n;
                        break;
                    default:
                        for (int j = 0; j < n; j++)
                        {
                            pixel += ((uint)planes[read + j] << 24) | ((uint)planes[read + n + j] << 16)
                                | ((uint)planes[read + (2 * n) + j] << 8) | planes[read + (3 * n) + j];
                            BinaryPrimitives.WriteUInt32LittleEndian(output[write..], pixel);
                            write += 4;
                        }

                        read += 4 * n;
                        break;
                }
            }
        }

        if (write != output.Length)
        {
            throw new InvalidImageContentException("OpenEXR PXR24 chunk does not match its layout.");
        }
    }
}
