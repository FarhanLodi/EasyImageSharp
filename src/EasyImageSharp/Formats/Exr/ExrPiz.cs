using System.Buffers.Binary;

namespace EasyImageSharp.Formats.Exr;

/// <summary>
/// The OpenEXR PIZ codec: the chunk's 16-bit words are range-compressed through a bitmap of the values that
/// occur, transformed with a two-dimensional Haar wavelet per channel and Huffman coded with a run-length
/// symbol. One instance holds the scratch tables for every chunk of one image; it is not thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The layout, the 14- and 16-bit wavelet lifting steps, the packed code-length table with its zero-run markers,
/// the canonical code assignment and the run-length symbol all follow the OpenEXR file layout specification and
/// the reference implementation's behaviour, so that files written by the reference library decode bit-exactly.
/// </para>
/// <para>
/// Long Huffman codes (more than 14 bits) are resolved canonically, by length, rather than by scanning a list
/// per table slot; for a prefix-free table the two are the same, and the canonical search bounds the work per
/// symbol even for a hostile table. Every table conflict the reference implementation rejects is rejected here.
/// </para>
/// </remarks>
internal sealed class ExrPiz
{
    private const int UShortRange = 1 << 16;
    private const int BitmapSize = UShortRange >> 3;
    private const int HufEncSize = (1 << 16) + 1;
    private const int HufDecBits = 14;
    private const int HufDecSize = 1 << HufDecBits;
    private const int HufDecMask = HufDecSize - 1;
    private const int ShortZeroCodeRun = 59;
    private const int LongZeroCodeRun = 63;
    private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;
    private const int LongestLongRun = 255 + ShortestLongRun;
    private const int MaxCodeLength = 58;

    private readonly byte[] bitmap = new byte[BitmapSize];
    private readonly ushort[] lut = new ushort[UShortRange];
    private readonly long[] hcode = new long[HufEncSize];

    /// <summary>Per 14-bit prefix: the length of the short code filling it, or 0.</summary>
    private readonly byte[] decLength = new byte[HufDecSize];

    /// <summary>Per 14-bit prefix: the symbol of the short code filling it.</summary>
    private readonly int[] decSymbol = new int[HufDecSize];

    /// <summary>Per 14-bit prefix: whether some long code starts with it.</summary>
    private readonly bool[] decHasLong = new bool[HufDecSize];

    /// <summary>Per code length: the numerically first code, the number of codes and where their symbols start.</summary>
    private readonly long[] firstCode = new long[MaxCodeLength + 1];
    private readonly int[] codeCount = new int[MaxCodeLength + 1];
    private readonly int[] symbolStart = new int[MaxCodeLength + 2];
    private readonly int[] longSymbols = new int[HufEncSize];

    private ushort[] words = Array.Empty<ushort>();

    /// <summary>Decodes one PIZ chunk into <paramref name="output"/>, the chunk's little-endian sample layout.</summary>
    public void Decompress(ReadOnlySpan<byte> packed, Span<byte> output, ExrChannel[] channels, int minX, int maxX, int minY, int maxY)
    {
        int total = output.Length / 2;
        if (packed.IsEmpty)
        {
            if (total != 0)
            {
                throw new InvalidImageContentException("OpenEXR PIZ chunk is empty.");
            }

            return;
        }

        if (this.words.Length < total)
        {
            this.words = new ushort[total];
        }

        Span<ushort> values = this.words.AsSpan(0, total);
        if (packed.Length < 4)
        {
            throw new InvalidImageContentException("OpenEXR PIZ chunk is truncated.");
        }

        int minNonZero = BinaryPrimitives.ReadUInt16LittleEndian(packed);
        int maxNonZero = BinaryPrimitives.ReadUInt16LittleEndian(packed[2..]);
        int pos = 4;
        if (maxNonZero >= BitmapSize)
        {
            throw new InvalidImageContentException("OpenEXR PIZ chunk has an invalid bitmap size.");
        }

        Array.Clear(this.bitmap);
        if (minNonZero <= maxNonZero)
        {
            int count = maxNonZero - minNonZero + 1;
            if (pos + count > packed.Length)
            {
                throw new InvalidImageContentException("OpenEXR PIZ bitmap is truncated.");
            }

            packed.Slice(pos, count).CopyTo(this.bitmap.AsSpan(minNonZero));
            pos += count;
        }

        int maxValue = this.ReverseLutFromBitmap();
        if (pos + 4 > packed.Length)
        {
            throw new InvalidImageContentException("OpenEXR PIZ chunk is truncated.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(packed[pos..]);
        pos += 4;
        if (length < 0 || length > packed.Length - pos)
        {
            throw new InvalidImageContentException("OpenEXR PIZ chunk has an invalid Huffman data length.");
        }

        this.HufUncompress(packed.Slice(pos, length), values);

        int start = 0;
        foreach (ExrChannel channel in channels)
        {
            int nx = ExrHeader.SampleCount(channel.XSampling, minX, maxX);
            int ny = ExrHeader.SampleCount(channel.YSampling, minY, maxY);
            int size = channel.BytesPerSample / 2;
            for (int j = 0; j < size; j++)
            {
                Wav2Decode(values, start + j, nx, size, ny, nx * size, (ushort)maxValue);
            }

            start += nx * ny * size;
        }

        ushort[] table = this.lut;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = table[values[i]];
        }

        // The words are stored channel by channel; the chunk layout interleaves channels line by line.
        Span<int> cursor = channels.Length <= 64 ? stackalloc int[channels.Length] : new int[channels.Length];
        start = 0;
        for (int c = 0; c < channels.Length; c++)
        {
            cursor[c] = start;
            start += ExrHeader.SampleCount(channels[c].XSampling, minX, maxX) * ExrHeader.SampleCount(channels[c].YSampling, minY, maxY)
                * (channels[c].BytesPerSample / 2);
        }

        int write = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                ExrChannel channel = channels[c];
                if (ExrHeader.FloorMod(y, channel.YSampling) != 0)
                {
                    continue;
                }

                int count = ExrHeader.SampleCount(channel.XSampling, minX, maxX) * (channel.BytesPerSample / 2);
                for (int k = 0; k < count; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output[write..], values[cursor[c]++]);
                    write += 2;
                }
            }
        }
    }

    /// <summary>The 14-bit wavelet lifting step for data whose range fits in 14 bits.</summary>
    internal static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
    {
        short ls = (short)l;
        short hs = (short)h;
        int hi = hs;
        int ai = ls + (hi & 1) + (hi >> 1);
        a = (ushort)(short)ai;
        b = (ushort)(short)(ai - hi);
    }

    /// <summary>The 16-bit modular wavelet lifting step for data that needs the full range.</summary>
    internal static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
    {
        int m = l;
        int d = h;
        int bb = (m - (d >> 1)) & 0xFFFF;
        int aa = (d + bb - 0x8000) & 0xFFFF;
        b = (ushort)bb;
        a = (ushort)aa;
    }

    /// <summary>Inverse of <see cref="Wdec14"/>.</summary>
    internal static void Wenc14(ushort a, ushort b, out ushort l, out ushort h)
    {
        short @as = (short)a;
        short bs = (short)b;
        short ms = (short)((@as + bs) >> 1);
        short ds = (short)(@as - bs);
        l = (ushort)ms;
        h = (ushort)ds;
    }

    /// <summary>Inverse of <see cref="Wdec16"/>.</summary>
    internal static void Wenc16(ushort a, ushort b, out ushort l, out ushort h)
    {
        int ao = (a + 0x8000) & 0xFFFF;
        int m = (ao + b) >> 1;
        int d = ao - b;
        if (d < 0)
        {
            m = (m + 0x8000) & 0xFFFF;
        }

        d &= 0xFFFF;
        l = (ushort)m;
        h = (ushort)d;
    }

    /// <summary>
    /// The inverse two-dimensional Haar transform over an <paramref name="nx"/> by <paramref name="ny"/> grid of
    /// words starting at <paramref name="start"/>, with element stride <paramref name="ox"/> and row stride
    /// <paramref name="oy"/>, coarsest level first.
    /// </summary>
    internal static void Wav2Decode(Span<ushort> buffer, int start, int nx, int ox, int ny, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1;
        while (p <= n)
        {
            p <<= 1;
        }

        p >>= 1;
        int p2 = p;
        p >>= 1;

        while (p >= 1)
        {
            int py = start;
            int ey = start + (oy * (ny - p2));
            int oy1 = oy * p;
            int oy2 = oy * p2;
            int ox1 = ox * p;
            int ox2 = ox * p2;
            ushort i00, i01, i10, i11;

            for (; py <= ey; py += oy2)
            {
                int px = py;
                int ex = py + (ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    int p10 = px + oy1;
                    int p11 = p10 + ox1;
                    if (w14)
                    {
                        Wdec14(buffer[px], buffer[p10], out i00, out i10);
                        Wdec14(buffer[p01], buffer[p11], out i01, out i11);
                        Wdec14(i00, i01, out buffer[px], out buffer[p01]);
                        Wdec14(i10, i11, out buffer[p10], out buffer[p11]);
                    }
                    else
                    {
                        Wdec16(buffer[px], buffer[p10], out i00, out i10);
                        Wdec16(buffer[p01], buffer[p11], out i01, out i11);
                        Wdec16(i00, i01, out buffer[px], out buffer[p01]);
                        Wdec16(i10, i11, out buffer[p10], out buffer[p11]);
                    }
                }

                // The odd column left over at this level.
                if ((nx & p) != 0)
                {
                    int p10 = px + oy1;
                    if (w14)
                    {
                        Wdec14(buffer[px], buffer[p10], out i00, out buffer[p10]);
                    }
                    else
                    {
                        Wdec16(buffer[px], buffer[p10], out i00, out buffer[p10]);
                    }

                    buffer[px] = i00;
                }
            }

            // The odd line left over at this level.
            if ((ny & p) != 0)
            {
                int px = py;
                int ex = py + (ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    if (w14)
                    {
                        Wdec14(buffer[px], buffer[p01], out i00, out buffer[p01]);
                    }
                    else
                    {
                        Wdec16(buffer[px], buffer[p01], out i00, out buffer[p01]);
                    }

                    buffer[px] = i00;
                }
            }

            p2 = p;
            p >>= 1;
        }
    }

    /// <summary>The forward transform, finest level first; <see cref="Wav2Decode"/> inverts it exactly.</summary>
    internal static void Wav2Encode(Span<ushort> buffer, int start, int nx, int ox, int ny, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1;
        int p2 = 2;

        while (p2 <= n)
        {
            int py = start;
            int ey = start + (oy * (ny - p2));
            int oy1 = oy * p;
            int oy2 = oy * p2;
            int ox1 = ox * p;
            int ox2 = ox * p2;
            ushort i00, i01, i10, i11;

            for (; py <= ey; py += oy2)
            {
                int px = py;
                int ex = py + (ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    int p10 = px + oy1;
                    int p11 = p10 + ox1;
                    if (w14)
                    {
                        Wenc14(buffer[px], buffer[p01], out i00, out i01);
                        Wenc14(buffer[p10], buffer[p11], out i10, out i11);
                        Wenc14(i00, i10, out buffer[px], out buffer[p10]);
                        Wenc14(i01, i11, out buffer[p01], out buffer[p11]);
                    }
                    else
                    {
                        Wenc16(buffer[px], buffer[p01], out i00, out i01);
                        Wenc16(buffer[p10], buffer[p11], out i10, out i11);
                        Wenc16(i00, i10, out buffer[px], out buffer[p10]);
                        Wenc16(i01, i11, out buffer[p01], out buffer[p11]);
                    }
                }

                if ((nx & p) != 0)
                {
                    int p10 = px + oy1;
                    if (w14)
                    {
                        Wenc14(buffer[px], buffer[p10], out i00, out buffer[p10]);
                    }
                    else
                    {
                        Wenc16(buffer[px], buffer[p10], out i00, out buffer[p10]);
                    }

                    buffer[px] = i00;
                }
            }

            if ((ny & p) != 0)
            {
                int px = py;
                int ex = py + (ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    if (w14)
                    {
                        Wenc14(buffer[px], buffer[p01], out i00, out buffer[p01]);
                    }
                    else
                    {
                        Wenc16(buffer[px], buffer[p01], out i00, out buffer[p01]);
                    }

                    buffer[px] = i00;
                }
            }

            p = p2;
            p2 <<= 1;
        }
    }

    /// <summary>Builds the dense-index-to-value table from the bitmap and returns the largest dense index.</summary>
    private int ReverseLutFromBitmap()
    {
        int k = 0;
        for (int i = 0; i < UShortRange; i++)
        {
            if (i == 0 || (this.bitmap[i >> 3] & (1 << (i & 7))) != 0)
            {
                this.lut[k++] = (ushort)i;
            }
        }

        int n = k - 1;
        while (k < UShortRange)
        {
            this.lut[k++] = 0;
        }

        return n;
    }

    private void HufUncompress(ReadOnlySpan<byte> compressed, Span<ushort> raw)
    {
        if (compressed.IsEmpty)
        {
            if (!raw.IsEmpty)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman data is empty.");
            }

            return;
        }

        if (compressed.Length < 20)
        {
            throw new InvalidImageContentException("OpenEXR PIZ Huffman header is truncated.");
        }

        uint im = BinaryPrimitives.ReadUInt32LittleEndian(compressed);
        uint iM = BinaryPrimitives.ReadUInt32LittleEndian(compressed[4..]);
        uint nBits = BinaryPrimitives.ReadUInt32LittleEndian(compressed[12..]);
        if (im >= HufEncSize || iM >= HufEncSize)
        {
            throw new InvalidImageContentException("OpenEXR PIZ Huffman table has an invalid size.");
        }

        ReadOnlySpan<byte> table = compressed[20..];
        int used = this.UnpackEncTable(table, (int)im, (int)iM);
        ReadOnlySpan<byte> bits = table[used..];
        if (nBits > 8L * bits.Length)
        {
            throw new InvalidImageContentException("OpenEXR PIZ Huffman bit count exceeds the data.");
        }

        this.BuildDecTable((int)im, (int)iM);
        this.Decode(bits, (int)nBits, (int)iM, raw);
    }

    /// <summary>Reads the packed 6-bit code lengths for symbols im..iM, expanding the zero-run markers.</summary>
    private int UnpackEncTable(ReadOnlySpan<byte> table, int im, int iM)
    {
        Array.Clear(this.hcode);
        ulong c = 0;
        int lc = 0;
        int pos = 0;
        for (; im <= iM; im++)
        {
            if (pos >= table.Length)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman table is truncated.");
            }

            long l = this.hcode[im] = GetBits(6, ref c, ref lc, table, ref pos);
            if (l == LongZeroCodeRun)
            {
                int zerun = (int)GetBits(8, ref c, ref lc, table, ref pos) + ShortestLongRun;
                if (im + zerun > iM + 1)
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman table is too long.");
                }

                for (int k = 0; k < zerun; k++)
                {
                    this.hcode[im + k] = 0;
                }

                im += zerun - 1;
            }
            else if (l >= ShortZeroCodeRun)
            {
                int zerun = (int)l - ShortZeroCodeRun + 2;
                if (im + zerun > iM + 1)
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman table is too long.");
                }

                for (int k = 0; k < zerun; k++)
                {
                    this.hcode[im + k] = 0;
                }

                im += zerun - 1;
            }
        }

        CanonicalCodeTable(this.hcode);
        return pos;
    }

    private static long GetBits(int count, ref ulong c, ref int lc, ReadOnlySpan<byte> data, ref int pos)
    {
        while (lc < count)
        {
            if (pos >= data.Length)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman table is truncated.");
            }

            c = (c << 8) | data[pos++];
            lc += 8;
        }

        lc -= count;
        return (long)((c >> lc) & ((1UL << count) - 1));
    }

    /// <summary>
    /// Turns code lengths into canonical codes stored as <c>length | code &lt;&lt; 6</c>: per length, codes are
    /// consecutive in symbol order, and longer codes take the numerically lower values.
    /// </summary>
    internal static void CanonicalCodeTable(long[] hcode)
    {
        Span<long> n = stackalloc long[MaxCodeLength + 1];
        n.Clear();
        for (int i = 0; i < HufEncSize; i++)
        {
            n[(int)hcode[i]]++;
        }

        long c = 0;
        for (int i = MaxCodeLength; i > 0; i--)
        {
            long nc = (c + n[i]) >> 1;
            n[i] = c;
            c = nc;
        }

        for (int i = 0; i < HufEncSize; i++)
        {
            int l = (int)hcode[i];
            if (l > 0)
            {
                hcode[i] = (long)l | (n[l]++ << 6);
            }
        }
    }

    private void BuildDecTable(int im, int iM)
    {
        Array.Clear(this.decLength);
        Array.Clear(this.decSymbol);
        Array.Clear(this.decHasLong);
        Array.Clear(this.firstCode);
        Array.Clear(this.codeCount);

        for (int i = im; i <= iM; i++)
        {
            long code = this.hcode[i] >> 6;
            int l = (int)(this.hcode[i] & 63);
            if ((code >> l) != 0)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman table has an invalid entry.");
            }

            if (l > HufDecBits)
            {
                int prefix = (int)(code >> (l - HufDecBits));
                if (this.decLength[prefix] != 0)
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman table has an invalid entry.");
                }

                this.decHasLong[prefix] = true;
                if (this.codeCount[l] == 0)
                {
                    this.firstCode[l] = code;
                }

                this.codeCount[l]++;
            }
            else if (l > 0)
            {
                int index = (int)(code << (HufDecBits - l));
                int count = 1 << (HufDecBits - l);
                for (int k = 0; k < count; k++)
                {
                    if (this.decLength[index + k] != 0 || this.decHasLong[index + k])
                    {
                        throw new InvalidImageContentException("OpenEXR PIZ Huffman table has an invalid entry.");
                    }

                    this.decLength[index + k] = (byte)l;
                    this.decSymbol[index + k] = i;
                }
            }
        }

        // Long codes of one length are consecutive in symbol order (the canonical assignment above), so the
        // symbols can be listed per length and indexed by code - firstCode.
        int next = 0;
        for (int l = HufDecBits + 1; l <= MaxCodeLength; l++)
        {
            this.symbolStart[l] = next;
            next += this.codeCount[l];
        }

        Span<int> fill = stackalloc int[MaxCodeLength + 1];
        fill.Clear();
        for (int i = im; i <= iM; i++)
        {
            int l = (int)(this.hcode[i] & 63);
            if (l > HufDecBits)
            {
                this.longSymbols[this.symbolStart[l] + fill[l]++] = i;
            }
        }
    }

    private void Decode(ReadOnlySpan<byte> bits, int nBits, int rlc, Span<ushort> raw)
    {
        ulong c = 0;
        int lc = 0;
        int pos = 0;
        int end = (int)(((long)nBits + 7) / 8);
        int output = 0;

        while (pos < end)
        {
            c = (c << 8) | bits[pos++];
            lc += 8;
            while (lc >= HufDecBits)
            {
                int index = (int)((c >> (lc - HufDecBits)) & HufDecMask);
                int length = this.decLength[index];
                if (length != 0)
                {
                    lc -= length;
                    this.Emit(this.decSymbol[index], rlc, ref c, ref lc, bits, ref pos, raw, ref output);
                    continue;
                }

                if (!this.decHasLong[index])
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman data contains an invalid code.");
                }

                int symbol = -1;
                for (int l = HufDecBits + 1; l <= MaxCodeLength; l++)
                {
                    while (lc < l && pos < end)
                    {
                        if (lc > 56)
                        {
                            throw new InvalidImageContentException("OpenEXR PIZ Huffman data contains an invalid code.");
                        }

                        c = (c << 8) | bits[pos++];
                        lc += 8;
                    }

                    if (lc < l)
                    {
                        break;
                    }

                    if (this.codeCount[l] == 0)
                    {
                        continue;
                    }

                    long value = (long)((c >> (lc - l)) & ((1UL << l) - 1));
                    long rank = value - this.firstCode[l];
                    if (rank >= 0 && rank < this.codeCount[l])
                    {
                        symbol = this.longSymbols[this.symbolStart[l] + (int)rank];
                        lc -= l;
                        break;
                    }
                }

                if (symbol < 0)
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman data contains an invalid code.");
                }

                this.Emit(symbol, rlc, ref c, ref lc, bits, ref pos, raw, ref output);
            }
        }

        // The last byte's padding bits are not part of the stream.
        int padding = (8 - nBits) & 7;
        c >>= padding;
        lc -= padding;
        while (lc > 0)
        {
            int index = (int)((c << (HufDecBits - lc)) & HufDecMask);
            int length = this.decLength[index];
            if (length == 0 || length > lc)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman data contains an invalid code.");
            }

            lc -= length;
            this.Emit(this.decSymbol[index], rlc, ref c, ref lc, bits, ref pos, raw, ref output);
        }

        if (output != raw.Length)
        {
            throw new InvalidImageContentException("OpenEXR PIZ Huffman data decodes to fewer values than the chunk holds.");
        }
    }

    private void Emit(int symbol, int rlc, ref ulong c, ref int lc, ReadOnlySpan<byte> bits, ref int pos, Span<ushort> raw, ref int output)
    {
        if (symbol == rlc)
        {
            if (lc < 8)
            {
                if (pos >= bits.Length)
                {
                    throw new InvalidImageContentException("OpenEXR PIZ Huffman data is truncated.");
                }

                c = (c << 8) | bits[pos++];
                lc += 8;
            }

            lc -= 8;
            int repeat = (int)((c >> lc) & 0xFF);
            if (output + repeat > raw.Length || output == 0)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman run is invalid.");
            }

            raw.Slice(output, repeat).Fill(raw[output - 1]);
            output += repeat;
        }
        else
        {
            if (output >= raw.Length)
            {
                throw new InvalidImageContentException("OpenEXR PIZ Huffman data decodes to more values than the chunk holds.");
            }

            raw[output++] = (ushort)symbol;
        }
    }

    /// <summary>
    /// Encodes one chunk's little-endian sample layout with PIZ and returns the packed bytes. The output is a
    /// valid PIZ stream for any input; the caller stores the chunk raw instead when it is not smaller.
    /// </summary>
    public byte[] Compress(ReadOnlySpan<byte> raw, ExrChannel[] channels, int minX, int maxX, int minY, int maxY)
    {
        int total = raw.Length / 2;
        if (total == 0)
        {
            return Array.Empty<byte>();
        }

        if (this.words.Length < total)
        {
            this.words = new ushort[total];
        }

        Span<ushort> values = this.words.AsSpan(0, total);

        // Gather the line-interleaved layout into one plane of words per channel.
        Span<int> cursor = channels.Length <= 64 ? stackalloc int[channels.Length] : new int[channels.Length];
        int start = 0;
        for (int c = 0; c < channels.Length; c++)
        {
            cursor[c] = start;
            start += ExrHeader.SampleCount(channels[c].XSampling, minX, maxX) * ExrHeader.SampleCount(channels[c].YSampling, minY, maxY)
                * (channels[c].BytesPerSample / 2);
        }

        int read = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                if (ExrHeader.FloorMod(y, channels[c].YSampling) != 0)
                {
                    continue;
                }

                int count = ExrHeader.SampleCount(channels[c].XSampling, minX, maxX) * (channels[c].BytesPerSample / 2);
                for (int k = 0; k < count; k++)
                {
                    values[cursor[c]++] = BinaryPrimitives.ReadUInt16LittleEndian(raw[read..]);
                    read += 2;
                }
            }
        }

        // Range compression: a bitmap of the values present (zero is implied), and a dense forward table.
        Array.Clear(this.bitmap);
        foreach (ushort value in values)
        {
            this.bitmap[value >> 3] |= (byte)(1 << (value & 7));
        }

        this.bitmap[0] &= 0xFE;
        int minNonZero = BitmapSize - 1;
        int maxNonZero = 0;
        for (int i = 0; i < BitmapSize; i++)
        {
            if (this.bitmap[i] != 0)
            {
                minNonZero = Math.Min(minNonZero, i);
                maxNonZero = Math.Max(maxNonZero, i);
            }
        }

        int k2 = 0;
        for (int i = 0; i < UShortRange; i++)
        {
            this.lut[i] = i == 0 || (this.bitmap[i >> 3] & (1 << (i & 7))) != 0 ? (ushort)k2++ : (ushort)0;
        }

        ushort maxValue = (ushort)(k2 - 1);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = this.lut[values[i]];
        }

        start = 0;
        foreach (ExrChannel channel in channels)
        {
            int nx = ExrHeader.SampleCount(channel.XSampling, minX, maxX);
            int ny = ExrHeader.SampleCount(channel.YSampling, minY, maxY);
            int size = channel.BytesPerSample / 2;
            for (int j = 0; j < size; j++)
            {
                Wav2Encode(values, start + j, nx, size, ny, nx * size, maxValue);
            }

            start += nx * ny * size;
        }

        byte[] huffman = this.HufCompress(values);
        int bitmapBytes = minNonZero <= maxNonZero ? maxNonZero - minNonZero + 1 : 0;
        var packed = new byte[4 + bitmapBytes + 4 + huffman.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packed, (ushort)minNonZero);
        BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(2), (ushort)maxNonZero);
        this.bitmap.AsSpan(minNonZero, bitmapBytes).CopyTo(packed.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4 + bitmapBytes), huffman.Length);
        huffman.CopyTo(packed.AsSpan(8 + bitmapBytes));
        return packed;
    }

    /// <summary>
    /// Huffman-codes 16-bit words in the PIZ layout: a 20-byte header (smallest and largest symbol, table
    /// length, bit count), the packed code-length table, then the codes, with runs of one word sent as the word,
    /// the run-length pseudo-symbol and an 8-bit repeat count whenever that is shorter.
    /// </summary>
    private byte[] HufCompress(ReadOnlySpan<ushort> raw)
    {
        long[] code = this.hcode;
        Array.Clear(code);
        foreach (ushort value in raw)
        {
            code[value]++;
        }

        int im = 0;
        while (code[im] == 0)
        {
            im++;
        }

        int iM = im;
        for (int i = im; i < HufEncSize; i++)
        {
            if (code[i] != 0)
            {
                iM = i;
            }
        }

        // The run-length pseudo-symbol sits just above the largest real symbol.
        iM++;
        code[iM] = 1;
        BuildCodeLengths(code, im, iM);
        CanonicalCodeTable(code);

        int maxLength = 0;
        for (int i = im; i <= iM; i++)
        {
            maxLength = Math.Max(maxLength, (int)(code[i] & 63));
        }

        // Header, a table of at most six bits per symbol (plus a final byte), and at most maxLength bits per word.
        var output = new byte[20 + (((iM - im + 1) * 6) / 8) + 2 + (int)((((long)raw.Length * maxLength) + 7) / 8) + 8];
        int p = 20;
        ulong c = 0;
        int lc = 0;
        for (int i = im; i <= iM; i++)
        {
            int l = (int)(code[i] & 63);
            if (l == 0)
            {
                int zerun = 1;
                while (i < iM && zerun < LongestLongRun && (code[i + 1] & 63) == 0)
                {
                    i++;
                    zerun++;
                }

                if (zerun >= 2)
                {
                    if (zerun >= ShortestLongRun)
                    {
                        OutputBits(6, LongZeroCodeRun, ref c, ref lc, output, ref p);
                        OutputBits(8, (ulong)(zerun - ShortestLongRun), ref c, ref lc, output, ref p);
                    }
                    else
                    {
                        OutputBits(6, (ulong)(ShortZeroCodeRun + zerun - 2), ref c, ref lc, output, ref p);
                    }

                    continue;
                }
            }

            OutputBits(6, (ulong)l, ref c, ref lc, output, ref p);
        }

        if (lc > 0)
        {
            output[p++] = (byte)(c << (8 - lc));
        }

        int tableLength = p - 20;
        int dataStart = p;
        c = 0;
        lc = 0;
        long runCode = code[iM];
        int s = raw[0];
        int runCount = 0;
        for (int i = 1; i < raw.Length; i++)
        {
            if (s == raw[i] && runCount < 255)
            {
                runCount++;
            }
            else
            {
                SendCode(code[s], runCount, runCode, ref c, ref lc, output, ref p);
                runCount = 0;
            }

            s = raw[i];
        }

        SendCode(code[s], runCount, runCode, ref c, ref lc, output, ref p);
        long nBits = ((long)(p - dataStart) * 8) + lc;
        if (lc > 0)
        {
            output[p++] = (byte)(c << (8 - lc));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)im);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)iM);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)tableLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)nBits);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0);
        Array.Resize(ref output, p);
        return output;
    }

    /// <summary>
    /// Replaces the frequencies in <paramref name="frequencies"/>[im..iM] by Huffman code lengths. Ties are broken
    /// by symbol, so the result is deterministic; any prefix code decodes, so the exact tree shape is free.
    /// </summary>
    private static void BuildCodeLengths(long[] frequencies, int im, int iM)
    {
        var queue = new PriorityQueue<int, (long Frequency, int Order)>();
        var parent = new List<int>();
        var leafOf = new List<int>();
        for (int i = im; i <= iM; i++)
        {
            if (frequencies[i] > 0)
            {
                queue.Enqueue(parent.Count, (frequencies[i], parent.Count));
                parent.Add(-1);
                leafOf.Add(i);
            }
        }

        int leaves = parent.Count;
        int order = leaves;
        while (queue.Count > 1)
        {
            queue.TryDequeue(out int a, out (long Frequency, int Order) fa);
            queue.TryDequeue(out int b, out (long Frequency, int Order) fb);
            int node = parent.Count;
            parent.Add(-1);
            parent[a] = node;
            parent[b] = node;
            queue.Enqueue(node, (fa.Frequency + fb.Frequency, order++));
        }

        for (int i = im; i <= iM; i++)
        {
            frequencies[i] = 0;
        }

        for (int leaf = 0; leaf < leaves; leaf++)
        {
            int depth = 0;
            for (int n = leaf; parent[n] >= 0; n = parent[n])
            {
                depth++;
            }

            frequencies[leafOf[leaf]] = Math.Max(depth, 1);
        }
    }

    private static void SendCode(long symbolCode, int runCount, long runCode, ref ulong c, ref int lc, byte[] output, ref int p)
    {
        int symbolLength = (int)(symbolCode & 63);
        int runLength = (int)(runCode & 63);
        if (symbolLength + runLength + 8 < symbolLength * runCount)
        {
            OutputBits(symbolLength, (ulong)(symbolCode >> 6), ref c, ref lc, output, ref p);
            OutputBits(runLength, (ulong)(runCode >> 6), ref c, ref lc, output, ref p);
            OutputBits(8, (ulong)runCount, ref c, ref lc, output, ref p);
        }
        else
        {
            for (int i = 0; i <= runCount; i++)
            {
                OutputBits(symbolLength, (ulong)(symbolCode >> 6), ref c, ref lc, output, ref p);
            }
        }
    }

    private static void OutputBits(int count, ulong bits, ref ulong c, ref int lc, byte[] output, ref int p)
    {
        c = (c << count) | bits;
        lc += count;
        while (lc >= 8)
        {
            lc -= 8;
            output[p++] = (byte)(c >> lc);
        }
    }
}
