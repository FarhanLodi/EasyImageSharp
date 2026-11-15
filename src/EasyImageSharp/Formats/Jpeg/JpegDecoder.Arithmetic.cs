namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Arithmetic entropy decoding for sequential DCT frames (SOF9, ITU-T T.81 Annex D and F.2.4): the adaptive binary
/// QM decoder, the DC difference and AC coefficient models with their statistics bins, and the DAC conditioning
/// tables. The procedures follow the figures of T.81 in the arrangement of libjpeg-turbo's <c>jdarith.c</c>, which
/// is also the reference the decoder is checked against. Decoded coefficients feed the same dequantization, inverse
/// transform, upsampling and colour conversion as Huffman-coded blocks. Progressive (SOF10) and lossless (SOF11)
/// arithmetic frames are rejected as unsupported.
/// </summary>
internal sealed partial class JpegDecoderCore
{
    /// <summary>Statistics bins per DC conditioning table (Table F.4 uses 49; libjpeg allocates 64).</summary>
    private const int DcStatBins = 64;

    /// <summary>Statistics bins per AC conditioning table (Table F.5 uses 245; libjpeg allocates 256).</summary>
    private const int AcStatBins = 256;

    /// <summary>
    /// T.81 Table D.2, one entry per state: Qe in bits 16-31, Next_Index_MPS in bits 8-15, Switch_MPS in bit 7 and
    /// Next_Index_LPS in bits 0-6. The last entry is the fixed 0.5 estimate T.851 recommends for the AC sign bin.
    /// </summary>
    private static readonly int[] ArithmeticStates =
    {
        V(0x5a1d, 1, 1, 1), V(0x2586, 14, 2, 0), V(0x1114, 16, 3, 0), V(0x080b, 18, 4, 0),
        V(0x03d8, 20, 5, 0), V(0x01da, 23, 6, 0), V(0x00e5, 25, 7, 0), V(0x006f, 28, 8, 0),
        V(0x0036, 30, 9, 0), V(0x001a, 33, 10, 0), V(0x000d, 35, 11, 0), V(0x0006, 9, 12, 0),
        V(0x0003, 10, 13, 0), V(0x0001, 12, 13, 0), V(0x5a7f, 15, 15, 1), V(0x3f25, 36, 16, 0),
        V(0x2cf2, 38, 17, 0), V(0x207c, 39, 18, 0), V(0x17b9, 40, 19, 0), V(0x1182, 42, 20, 0),
        V(0x0cef, 43, 21, 0), V(0x09a1, 45, 22, 0), V(0x072f, 46, 23, 0), V(0x055c, 48, 24, 0),
        V(0x0406, 49, 25, 0), V(0x0303, 51, 26, 0), V(0x0240, 52, 27, 0), V(0x01b1, 54, 28, 0),
        V(0x0144, 56, 29, 0), V(0x00f5, 57, 30, 0), V(0x00b7, 59, 31, 0), V(0x008a, 60, 32, 0),
        V(0x0068, 62, 33, 0), V(0x004e, 63, 34, 0), V(0x003b, 32, 35, 0), V(0x002c, 33, 9, 0),
        V(0x5ae1, 37, 37, 1), V(0x484c, 64, 38, 0), V(0x3a0d, 65, 39, 0), V(0x2ef1, 67, 40, 0),
        V(0x261f, 68, 41, 0), V(0x1f33, 69, 42, 0), V(0x19a8, 70, 43, 0), V(0x1518, 72, 44, 0),
        V(0x1177, 73, 45, 0), V(0x0e74, 74, 46, 0), V(0x0bfb, 75, 47, 0), V(0x09f8, 77, 48, 0),
        V(0x0861, 78, 49, 0), V(0x0706, 79, 50, 0), V(0x05cd, 48, 51, 0), V(0x04de, 50, 52, 0),
        V(0x040f, 50, 53, 0), V(0x0363, 51, 54, 0), V(0x02d4, 52, 55, 0), V(0x025c, 53, 56, 0),
        V(0x01f8, 54, 57, 0), V(0x01a4, 55, 58, 0), V(0x0160, 56, 59, 0), V(0x0125, 57, 60, 0),
        V(0x00f6, 58, 61, 0), V(0x00cb, 59, 62, 0), V(0x00ab, 61, 63, 0), V(0x008f, 61, 32, 0),
        V(0x5b12, 65, 65, 1), V(0x4d04, 80, 66, 0), V(0x412c, 81, 67, 0), V(0x37d8, 82, 68, 0),
        V(0x2fe8, 83, 69, 0), V(0x293c, 84, 70, 0), V(0x2379, 86, 71, 0), V(0x1edf, 87, 72, 0),
        V(0x1aa9, 87, 73, 0), V(0x174e, 72, 74, 0), V(0x1424, 72, 75, 0), V(0x119c, 74, 76, 0),
        V(0x0f6b, 74, 77, 0), V(0x0d51, 75, 78, 0), V(0x0bb6, 77, 79, 0), V(0x0a40, 77, 48, 0),
        V(0x5832, 80, 81, 1), V(0x4d1c, 88, 82, 0), V(0x438e, 89, 83, 0), V(0x3bdd, 90, 84, 0),
        V(0x34ee, 91, 85, 0), V(0x2eae, 92, 86, 0), V(0x299a, 93, 87, 0), V(0x2516, 86, 71, 0),
        V(0x5570, 88, 89, 1), V(0x4ca9, 95, 90, 0), V(0x44d9, 96, 91, 0), V(0x3e22, 97, 92, 0),
        V(0x3824, 99, 93, 0), V(0x32b4, 99, 94, 0), V(0x2e17, 93, 86, 0), V(0x56a8, 95, 96, 1),
        V(0x4f46, 101, 97, 0), V(0x47e5, 102, 98, 0), V(0x41cf, 103, 99, 0), V(0x3c3d, 104, 100, 0),
        V(0x375e, 99, 93, 0), V(0x5231, 105, 102, 0), V(0x4c0f, 106, 103, 0), V(0x4639, 107, 104, 0),
        V(0x415e, 103, 99, 0), V(0x5627, 105, 106, 1), V(0x50e7, 108, 107, 0), V(0x4b85, 109, 103, 0),
        V(0x5597, 110, 109, 0), V(0x504f, 111, 107, 0), V(0x5a10, 110, 111, 1), V(0x5522, 112, 109, 0),
        V(0x59eb, 112, 111, 1), V(0x5a1d, 113, 113, 0),
    };

    /// <summary>The state index of the fixed 0.5 estimate used for AC signs.</summary>
    private const byte FixedHalfState = 113;

    private readonly byte[][] dcStats = new byte[4][];
    private readonly byte[][] acStats = new byte[4][];

    /// <summary>The AC sign bin: always the fixed 0.5 estimate, never adapted.</summary>
    private readonly byte[] fixedBin = [FixedHalfState];

    /// <summary>DAC conditioning: the L and U bounds per DC table (defaults 0 and 1) and Kx per AC table (default 5).</summary>
    private readonly byte[] arithmeticDcL = new byte[4];
    private readonly byte[] arithmeticDcU = [1, 1, 1, 1];
    private readonly byte[] arithmeticAcK = [5, 5, 5, 5];

    private bool arithmetic;
    private long arithmeticC;
    private long arithmeticA;
    private int arithmeticCt;

    /// <summary>
    /// Set once a marker interrupts the coded data. Unlike Huffman decoding this is legal (T.81 D.2.6): the decoder
    /// continues with zero bytes, and <see cref="pos"/> is left on the marker for the restart or marker loop.
    /// </summary>
    private bool arithmeticMarkerHit;

    private static int V(int qe, int nextLps, int nextMps, int switchMps) => (qe << 16) | (nextMps << 8) | (switchMps << 7) | nextLps;

    /// <summary>Reads a DAC segment (T.81 B.2.4.3): conditioning values for DC tables (Tc 0) and AC tables (Tc 1).</summary>
    private void ReadArithmeticConditioning()
    {
        int end = this.ReadTableSegmentEnd("DAC");
        while (this.pos < end)
        {
            int index = this.ReadByte();
            int value = this.ReadByte();
            int tc = index >> 4;
            int tb = index & 0x0F;
            if (tb > 3 || tc > 1)
            {
                throw new InvalidImageContentException($"Invalid JPEG DAC table selector 0x{index:X2}.");
            }

            if (tc == 1)
            {
                if (value is < 1 or > 63)
                {
                    throw new InvalidImageContentException($"Invalid JPEG DAC AC conditioning value {value}.");
                }

                this.arithmeticAcK[tb] = (byte)value;
            }
            else
            {
                byte l = (byte)(value & 0x0F);
                byte u = (byte)(value >> 4);
                if (l > u)
                {
                    throw new InvalidImageContentException($"Invalid JPEG DAC DC conditioning bounds L={l} U={u}.");
                }

                this.arithmeticDcL[tb] = l;
                this.arithmeticDcU[tb] = u;
            }
        }
    }

    /// <summary>
    /// Starts arithmetic decoding at the beginning of a scan or after a restart marker: clears the statistics of the
    /// tables the scan uses, the DC predictions and contexts, and the C, A and CT registers (T.81 D.2.7, F.2.4).
    /// </summary>
    private void ResetArithmeticDecoder(JpegComponent[] scanComponents)
    {
        foreach (JpegComponent component in scanComponents)
        {
            (this.dcStats[component.DcTableId] ??= new byte[DcStatBins]).AsSpan().Clear();
            (this.acStats[component.AcTableId] ??= new byte[AcStatBins]).AsSpan().Clear();
            component.Pred = 0;
            component.DcContext = 0;
        }

        this.arithmeticC = 0;
        this.arithmeticA = 0;
        this.arithmeticCt = -16; // Forces two initial bytes into C.
        this.arithmeticMarkerHit = false;
    }

    /// <summary>Decodes one binary decision with the adaptive estimate in <c>stats[index]</c> (T.81 D.2.4 to D.2.6).</summary>
    private int ArithmeticDecode(byte[] stats, int index)
    {
        // Renormalization and data input (D.2.6).
        while (this.arithmeticA < 0x8000)
        {
            if (--this.arithmeticCt < 0)
            {
                this.arithmeticC = (this.arithmeticC << 8) | (uint)this.ArithmeticNextByte();
                if ((this.arithmeticCt += 8) < 0 && ++this.arithmeticCt == 0)
                {
                    // Two initial bytes are in C: start with A = 0x10000 (doubled below).
                    this.arithmeticA = 0x8000;
                }
            }

            this.arithmeticA <<= 1;
        }

        int sv = stats[index];
        int entry = ArithmeticStates[sv & 0x7F];
        int nextLps = entry & 0xFF; // Includes the Switch_MPS flag in bit 7.
        int nextMps = (entry >> 8) & 0xFF;
        long qe = entry >> 16;

        // Decoding and probability estimation (D.2.4, D.2.5).
        long temp = this.arithmeticA - qe;
        this.arithmeticA = temp;
        temp <<= this.arithmeticCt;
        if (this.arithmeticC >= temp)
        {
            this.arithmeticC -= temp;

            // Conditional LPS exchange.
            if (this.arithmeticA < qe)
            {
                this.arithmeticA = qe;
                stats[index] = (byte)((sv & 0x80) ^ nextMps);
            }
            else
            {
                this.arithmeticA = qe;
                stats[index] = (byte)((sv & 0x80) ^ nextLps);
                sv ^= 0x80;
            }
        }
        else if (this.arithmeticA < 0x8000)
        {
            // Conditional MPS exchange.
            if (this.arithmeticA < qe)
            {
                stats[index] = (byte)((sv & 0x80) ^ nextLps);
                sv ^= 0x80;
            }
            else
            {
                stats[index] = (byte)((sv & 0x80) ^ nextMps);
            }
        }

        return sv >> 7;
    }

    /// <summary>
    /// The next byte of entropy-coded data, with <c>FF 00</c> unstuffed and fill bytes skipped. A marker (or the end of
    /// the data) yields zero from then on and leaves <see cref="pos"/> on the marker's <c>FF</c>.
    /// </summary>
    private int ArithmeticNextByte()
    {
        if (this.arithmeticMarkerHit)
        {
            return 0;
        }

        if (!this.EnsureBuffered(1))
        {
            this.arithmeticMarkerHit = true;
            return 0;
        }

        byte b = this.data[this.pos];
        if (b != 0xFF)
        {
            this.pos++;
            return b;
        }

        for (int k = 1; ; k++)
        {
            if (!this.EnsureBuffered(k + 1))
            {
                this.arithmeticMarkerHit = true;
                return 0;
            }

            byte next = this.data[this.pos + k];
            if (next == 0xFF)
            {
                continue; // Fill byte.
            }

            if (next == 0x00)
            {
                this.pos += k + 1;
                return 0xFF;
            }

            this.pos += k - 1; // On the FF that introduces the marker.
            this.arithmeticMarkerHit = true;
            return 0;
        }
    }

    /// <summary>
    /// Decodes one DC difference (F.2.4.1, Figures F.19 and F.21 to F.24) and updates the component's prediction and
    /// conditioning context.
    /// </summary>
    private int ArithmeticDecodeDcDifference(JpegComponent component)
    {
        byte[] stats = this.dcStats[component.DcTableId];
        int st = component.DcContext;
        if (this.ArithmeticDecode(stats, st) == 0)
        {
            component.DcContext = 0;
            return 0;
        }

        int sign = this.ArithmeticDecode(stats, st + 1);
        st += 2 + sign;
        int m = this.ArithmeticDecode(stats, st);
        if (m != 0)
        {
            st = 20; // X1, Table F.4.
            while (this.ArithmeticDecode(stats, st) != 0)
            {
                if ((m <<= 1) == 0x8000)
                {
                    throw new InvalidImageContentException("Corrupt arithmetic-coded JPEG data: DC magnitude overflow.");
                }

                st++;
            }
        }

        // F.1.4.4.1.2: conditioning category of this difference for the next one.
        int table = component.DcTableId;
        if (m < ((1 << this.arithmeticDcL[table]) >> 1))
        {
            component.DcContext = 0;
        }
        else if (m > ((1 << this.arithmeticDcU[table]) >> 1))
        {
            component.DcContext = 12 + (sign * 4);
        }
        else
        {
            component.DcContext = 4 + (sign * 4);
        }

        int v = m;
        st += 14;
        while ((m >>= 1) != 0)
        {
            if (this.ArithmeticDecode(stats, st) != 0)
            {
                v |= m;
            }
        }

        v++;
        return sign != 0 ? -v : v;
    }

    /// <summary>Decodes one block of a sequential arithmetic scan (F.2.4.1 and F.2.4.2) and writes it out.</summary>
    private void DecodeSequentialBlockArithmetic(JpegComponent component, int bx, int by)
    {
        float[] dequant = component.Dequant!;
        Span<float> block = this.blockScratch;
        block.Clear();

        // DC predictions wrap at 16 bits, as libjpeg's masked arithmetic does.
        component.Pred = (short)(component.Pred + this.ArithmeticDecodeDcDifference(component));
        block[0] = component.Pred * dequant[0];

        // AC coefficients, Figure F.20.
        byte[] stats = this.acStats[component.AcTableId];
        int kx = this.arithmeticAcK[component.AcTableId];
        bool anyAc = false;
        for (int k = 1; k <= 63; k++)
        {
            int st = 3 * (k - 1);
            if (this.ArithmeticDecode(stats, st) != 0)
            {
                break; // End of block.
            }

            while (this.ArithmeticDecode(stats, st + 1) == 0)
            {
                st += 3;
                if (++k > 63)
                {
                    throw new InvalidImageContentException("Corrupt arithmetic-coded JPEG data: AC coefficient index out of range.");
                }
            }

            int sign = this.ArithmeticDecode(this.fixedBin, 0);
            st += 2;
            int m = this.ArithmeticDecode(stats, st);
            if (m != 0 && this.ArithmeticDecode(stats, st) != 0)
            {
                m <<= 1;
                st = k <= kx ? 189 : 217;
                while (this.ArithmeticDecode(stats, st) != 0)
                {
                    if ((m <<= 1) == 0x8000)
                    {
                        throw new InvalidImageContentException("Corrupt arithmetic-coded JPEG data: AC magnitude overflow.");
                    }

                    st++;
                }
            }

            int v = m;
            st += 14;
            while ((m >>= 1) != 0)
            {
                if (this.ArithmeticDecode(stats, st) != 0)
                {
                    v |= m;
                }
            }

            v++;
            int natural = JpegTables.ZigZag[k];
            block[natural] = (sign != 0 ? -v : v) * dequant[natural];
            anyAc = true;
        }

        if (!anyAc)
        {
            this.WriteFlatBlock(component, bx, by, FlatValue(component, block[0]));
            return;
        }

        this.WriteBlock(component, bx, by, block);
    }

    /// <summary>
    /// After an arithmetic segment the coder may not have consumed every byte the encoder flushed; like libjpeg's
    /// marker reader, skip to the next marker before expecting a restart.
    /// </summary>
    private void SkipToMarkerAfterArithmeticSegment()
    {
        while (this.EnsureBuffered(2))
        {
            if (this.data[this.pos] == 0xFF && this.data[this.pos + 1] is not 0x00 and not 0xFF)
            {
                return;
            }

            this.pos++;
        }
    }
}
