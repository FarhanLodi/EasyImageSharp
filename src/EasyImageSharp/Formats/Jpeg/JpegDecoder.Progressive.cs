namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Progressive (SOF2) scan decoding per ITU-T T.81 Annex G: DC-first, DC-refinement, AC-first and
/// AC-refinement scans accumulate quantized coefficients per block; the frame is reconstructed once
/// every scan has been read.
/// </summary>
internal sealed partial class JpegDecoderCore
{
    /// <summary>Validates the scan header of a progressive scan and resolves the Huffman tables it needs.</summary>
    private void PrepareProgressiveScan(JpegComponent[] scanComponents, int ss, int se, int ah, int al)
    {
        if (al > 13 || (ah != 0 && al != ah - 1))
        {
            throw new InvalidImageContentException(
                $"Invalid JPEG successive approximation parameters (Ah={ah}, Al={al}).");
        }

        if (ss == 0)
        {
            // DC scan: may be interleaved; a first scan needs the DC Huffman tables, a refinement scan reads raw bits.
            if (se != 0)
            {
                throw new InvalidImageContentException("Invalid JPEG progressive DC scan: Se must be 0.");
            }

            this.scanKind = ah == 0 ? ScanKind.DcFirst : ScanKind.DcRefine;
            if (ah == 0)
            {
                foreach (JpegComponent component in scanComponents)
                {
                    component.DcTable = this.dcTables[component.DcTableId]
                        ?? throw new InvalidImageContentException($"JPEG DC Huffman table {component.DcTableId} is undefined.");
                }
            }
        }
        else
        {
            // AC scan: a single component and a band 1 <= Ss <= Se <= 63.
            if (ss > se || se > 63)
            {
                throw new InvalidImageContentException(
                    $"Invalid JPEG progressive AC scan spectral selection (Ss={ss}, Se={se}).");
            }

            if (scanComponents.Length != 1)
            {
                throw new InvalidImageContentException("JPEG progressive AC scans must contain exactly one component.");
            }

            this.scanKind = ah == 0 ? ScanKind.AcFirst : ScanKind.AcRefine;
            JpegComponent component = scanComponents[0];
            component.AcTable = this.acTables[component.AcTableId]
                ?? throw new InvalidImageContentException($"JPEG AC Huffman table {component.AcTableId} is undefined.");
        }

        this.spectralStart = ss;
        this.spectralEnd = se;
        this.approxLow = al;
    }

    /// <summary>DC first scan (Ah = 0): the DC difference is decoded and stored shifted by Al (T.81 G.1.2.1).</summary>
    private void DecodeDcFirst(JpegComponent component, int bx, int by)
    {
        int t = this.DecodeHuffman(component.DcTable!);
        int diff = t == 0 ? 0 : Extend(this.Receive(t), t);
        component.Pred += diff;
        component.Coefficients[component.CoefficientOffset(bx, by)] = (short)(component.Pred << this.approxLow);
    }

    /// <summary>DC refinement scan (Ah > 0): one raw bit per block supplies bit Al of the DC coefficient.</summary>
    private void DecodeDcRefine(JpegComponent component, int bx, int by)
    {
        if (this.ReadBit() != 0)
        {
            int offset = component.CoefficientOffset(bx, by);
            int coef = component.Coefficients[offset];
            component.Coefficients[offset] = (short)(coef | (1 << this.approxLow));
        }
    }

    /// <summary>AC first scan (Ah = 0): coefficients Ss..Se are decoded with EOB-run support (T.81 G.1.2.2).</summary>
    private void DecodeAcFirst(JpegComponent component, int bx, int by)
    {
        if (this.eobRun > 0)
        {
            this.eobRun--;
            return;
        }

        short[] coefficients = component.Coefficients;
        int offset = component.CoefficientOffset(bx, by);
        HuffmanTable ac = component.AcTable!;
        int al = this.approxLow;
        int se = this.spectralEnd;

        for (int k = this.spectralStart; k <= se; k++)
        {
            int rs = this.DecodeHuffman(ac);
            int r = rs >> 4;
            int s = rs & 0x0F;
            if (s != 0)
            {
                k += r;
                if (k > se)
                {
                    throw new InvalidImageContentException("JPEG AC coefficient index out of range.");
                }

                coefficients[offset + k] = (short)(Extend(this.Receive(s), s) << al);
            }
            else if (r == 15)
            {
                k += 15; // ZRL: sixteen zero coefficients (the loop increment supplies the last one).
            }
            else
            {
                // EOBn: this block and the next (2^r + bits) - 1 blocks have no more coefficients in the band.
                int run = 1 << r;
                if (r > 0)
                {
                    run += this.Receive(r);
                }

                this.eobRun = run - 1;
                return;
            }
        }
    }

    /// <summary>
    /// AC refinement scan (Ah > 0): adds bit Al to coefficients that are already nonzero (correction bits) and
    /// introduces new coefficients of magnitude 1 &lt;&lt; Al (T.81 G.1.2.3).
    /// </summary>
    private void DecodeAcRefine(JpegComponent component, int bx, int by)
    {
        short[] coefficients = component.Coefficients;
        int offset = component.CoefficientOffset(bx, by);
        int p1 = 1 << this.approxLow;
        int m1 = -1 << this.approxLow;
        int se = this.spectralEnd;
        int k = this.spectralStart;

        if (this.eobRun == 0)
        {
            HuffmanTable ac = component.AcTable!;
            for (; k <= se; k++)
            {
                int rs = this.DecodeHuffman(ac);
                int r = rs >> 4;
                int s = rs & 0x0F;
                if (s != 0)
                {
                    // A newly nonzero coefficient; its size is always 1 in a valid stream, so the following bit
                    // is its sign (like libjpeg we tolerate other sizes rather than failing the whole image).
                    s = this.ReadBit() != 0 ? p1 : m1;
                }
                else if (r != 15)
                {
                    // EOBn: the remainder of this block (and the following blocks) only carries correction bits.
                    this.eobRun = 1 << r;
                    if (r > 0)
                    {
                        this.eobRun += this.Receive(r);
                    }

                    break;
                }

                // Advance over already-nonzero coefficients (appending a correction bit to each) and r
                // still-zero coefficients; stop at the zero coefficient that receives the new value (if any).
                do
                {
                    int index = offset + k;
                    int coef = coefficients[index];
                    if (coef != 0)
                    {
                        if (this.ReadBit() != 0 && (coef & p1) == 0)
                        {
                            coefficients[index] = (short)(coef >= 0 ? coef + p1 : coef + m1);
                        }
                    }
                    else
                    {
                        if (--r < 0)
                        {
                            break;
                        }
                    }

                    k++;
                }
                while (k <= se);

                if (s != 0 && k <= se)
                {
                    coefficients[offset + k] = (short)s;
                }
            }
        }

        if (this.eobRun > 0)
        {
            // Inside an EOB run: only correction bits for the already-nonzero coefficients remain.
            for (; k <= se; k++)
            {
                int index = offset + k;
                int coef = coefficients[index];
                if (coef != 0 && this.ReadBit() != 0 && (coef & p1) == 0)
                {
                    coefficients[index] = (short)(coef >= 0 ? coef + p1 : coef + m1);
                }
            }

            this.eobRun--;
        }
    }

    /// <summary>Dequantizes and inverse-transforms every block of every component into the sample planes.</summary>
    private void ReconstructProgressiveFrame()
    {
        Span<float> block = this.blockScratch;
        Span<float> temp = this.tempScratch;
        int[] zigzag = JpegTables.ZigZag;

        foreach (JpegComponent component in this.components)
        {
            ushort[]? quant = component.QuantTable;
            if (quant is null)
            {
                // The component never appeared in a scan: all coefficients are zero, which reconstructs to mid-gray.
                Array.Fill(component.Plane, (byte)128);
                continue;
            }

            short[] coefficients = component.Coefficients;
            int offset = 0;
            for (int by = 0; by < component.BlocksPerColumnTotal; by++)
            {
                for (int bx = 0; bx < component.BlocksPerLineTotal; bx++)
                {
                    for (int k = 0; k < 64; k++)
                    {
                        int natural = zigzag[k];
                        block[natural] = coefficients[offset + k] * quant[natural];
                    }

                    JpegTables.InverseDct(block, temp);
                    WriteBlock(component, bx, by, block);
                    offset += 64;
                }
            }
        }
    }
}
