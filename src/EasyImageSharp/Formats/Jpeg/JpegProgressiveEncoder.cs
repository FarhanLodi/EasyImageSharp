using System.Numerics;
using Component = EasyImageSharp.Formats.Jpeg.JpegEncoderCore.Component;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>One scan of a progressive (or multi-scan sequential) frame: its components and spectral/approximation parameters.</summary>
internal sealed class JpegScanDescriptor
{
    public JpegScanDescriptor(Component[] components, int ss, int se, int ah, int al)
    {
        this.Components = components;
        this.Ss = ss;
        this.Se = se;
        this.Ah = ah;
        this.Al = al;
    }

    public Component[] Components { get; }

    public int Ss { get; }

    public int Se { get; }

    public int Ah { get; }

    public int Al { get; }
}

/// <summary>
/// Builds progressive scan scripts. With no explicit scan count the script is the "simple progression" libjpeg
/// uses: an interleaved DC scan with one bit of successive approximation, spectral selection of the luma AC band
/// (1-5, then 6-63) at two bits of approximation, chroma at one bit, and refinement scans in order of visual
/// importance. An explicit count adds or removes scans from that template one step at a time (spectral splits
/// and approximation levels), so any count from <c>1 + components</c> up to 64 is honoured exactly.
/// </summary>
internal static class JpegScanScript
{
    private readonly record struct Band(int Start, int End);

    public static JpegScanDescriptor[] Create(Component[] components, bool isYCbCr, bool interleavedDc, int requestedScans)
    {
        int count = components.Length;
        bool lumaFirst = isYCbCr && count == 3;
        int dcScans = interleavedDc || count == 1 ? 1 : count;

        // Template: DC approximation depth, per-component AC bands and AC approximation depth.
        int dcRefines = 0;
        var bands = new List<Band>[count];
        var acRefines = new int[count];
        for (int i = 0; i < count; i++)
        {
            bands[i] = new List<Band> { new(1, 63) };
        }

        int Total()
        {
            int total = dcScans * (1 + dcRefines);
            for (int i = 0; i < count; i++)
            {
                total += bands[i].Count + acRefines[i];
            }

            return total;
        }

        // The ordered refinements that grow the minimal script into the libjpeg-style default; each step adds
        // the given number of scans.
        var steps = new List<(int Cost, Action Apply)>();
        void SplitStep(int c) => steps.Add((1, () => bands[c] = new List<Band> { new(1, 5), new(6, 63) }));
        void RefineStep(int c, int level) => steps.Add((1, () => acRefines[c] = level));
        void DcRefineStep() => steps.Add((dcScans, () => dcRefines = 1));

        if (lumaFirst)
        {
            SplitStep(0);
            RefineStep(0, 1);
            RefineStep(2, 1);
            RefineStep(1, 1);
            DcRefineStep();
            RefineStep(0, 2);
        }
        else
        {
            for (int c = 0; c < count; c++)
            {
                SplitStep(c);
            }

            for (int c = 0; c < count; c++)
            {
                RefineStep(c, 1);
            }

            DcRefineStep();
            for (int c = 0; c < count; c++)
            {
                RefineStep(c, 2);
            }
        }

        if (requestedScans <= 0)
        {
            foreach ((_, Action apply) in steps)
            {
                apply();
            }
        }
        else
        {
            int target = Math.Max(requestedScans, Total());
            foreach ((int cost, Action apply) in steps)
            {
                if (Total() + cost <= target)
                {
                    apply();
                }
            }

            // Spend the remaining budget on spectral splits: the component with the fewest bands (luma first on
            // ties) has its widest band cut in half.
            while (Total() < target)
            {
                int best = -1;
                for (int i = 0; i < count; i++)
                {
                    if (bands[i].Exists(b => b.End > b.Start) && (best < 0 || bands[i].Count < bands[best].Count))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break; // Every band is a single coefficient already.
                }

                List<Band> list = bands[best];
                int widest = 0;
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i].End - list[i].Start > list[widest].End - list[widest].Start)
                    {
                        widest = i;
                    }
                }

                Band band = list[widest];
                int mid = (band.Start + band.End) / 2;
                list[widest] = new Band(band.Start, mid);
                list.Insert(widest + 1, new Band(mid + 1, band.End));
            }
        }

        // ----- Emit the scans in libjpeg's order -----
        var scans = new List<JpegScanDescriptor>();
        void Dc(int ah, int al)
        {
            if (dcScans == 1)
            {
                scans.Add(new JpegScanDescriptor(components, 0, 0, ah, al));
            }
            else
            {
                foreach (Component c in components)
                {
                    scans.Add(new JpegScanDescriptor(new[] { c }, 0, 0, ah, al));
                }
            }
        }

        void AcBands(int c, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                scans.Add(new JpegScanDescriptor(new[] { components[c] }, bands[c][i].Start, bands[c][i].End, 0, acRefines[c]));
            }
        }

        void AcRefine(int c, int ah) => scans.Add(new JpegScanDescriptor(new[] { components[c] }, 1, 63, ah, ah - 1));

        Dc(0, dcRefines);
        if (lumaFirst)
        {
            AcBands(0, 0, 1);                  // First luma band.
            AcBands(2, 0, bands[2].Count);     // Cr
            AcBands(1, 0, bands[1].Count);     // Cb
            AcBands(0, 1, bands[0].Count);     // Remaining luma bands.
            for (int a = acRefines[0]; a >= 2; a--)
            {
                AcRefine(0, a);
            }

            for (int d = dcRefines; d >= 1; d--)
            {
                Dc(d, d - 1);
            }

            for (int a = acRefines[2]; a >= 1; a--)
            {
                AcRefine(2, a);
            }

            for (int a = acRefines[1]; a >= 1; a--)
            {
                AcRefine(1, a);
            }

            if (acRefines[0] >= 1)
            {
                AcRefine(0, 1);
            }
        }
        else
        {
            for (int c = 0; c < count; c++)
            {
                AcBands(c, 0, bands[c].Count);
            }

            for (int c = 0; c < count; c++)
            {
                for (int a = acRefines[c]; a >= 2; a--)
                {
                    AcRefine(c, a);
                }
            }

            for (int d = dcRefines; d >= 1; d--)
            {
                Dc(d, d - 1);
            }

            for (int c = 0; c < count; c++)
            {
                if (acRefines[c] >= 1)
                {
                    AcRefine(c, 1);
                }
            }
        }

        return scans.ToArray();
    }
}

/// <summary>
/// Entropy coding of progressive scans per ITU-T T.81 Annex G: DC first/refinement scans, AC first scans with
/// EOB runs (G.1.2.2) and AC refinement scans with buffered correction bits (G.1.2.3). The same routines gather
/// symbol statistics when no writer is supplied, which is how optimised tables are built per scan.
/// </summary>
internal sealed class JpegProgressiveEncoder
{
    private const int MaxCorrectionBits = 1000;

    private readonly int mcusX;
    private readonly int mcusY;
    private readonly int restartInterval;
    private readonly int maxEobRun;

    private JpegBitWriter? writer;
    private long[][]? dcFreq;
    private long[][]? acFreq;

    // AC scan state.
    private int eobRun;
    private readonly byte[] correctionBits = new byte[MaxCorrectionBits + 64];
    private int bufferedBits;   // Correction bits belonging to the pending EOB run (BE in T.81 G.1.2.3).

    public JpegProgressiveEncoder(int mcusX, int mcusY, int restartInterval, int maxEobRun)
    {
        this.mcusX = mcusX;
        this.mcusY = mcusY;
        this.restartInterval = restartInterval;
        this.maxEobRun = maxEobRun;
    }

    /// <summary>
    /// Codes one scan. With a <paramref name="writer"/> the entropy-coded segment is emitted; with a null writer
    /// the DC/AC symbol frequencies of the tables the scan uses are accumulated into <paramref name="dcFreq"/> /
    /// <paramref name="acFreq"/> (indexed by table number) instead.
    /// </summary>
    public void EncodeScan(JpegBitWriter? writer, JpegScanDescriptor scan, long[][]? dcFreq, long[][]? acFreq)
    {
        this.writer = writer;
        this.dcFreq = dcFreq;
        this.acFreq = acFreq;
        this.eobRun = 0;
        this.bufferedBits = 0;
        foreach (Component c in scan.Components)
        {
            c.Predictor = 0;
        }

        if (scan.Ss == 0)
        {
            this.EncodeDcScan(scan);
        }
        else
        {
            this.EncodeAcScan(scan);
        }
    }

    // ----- Scan drivers -----

    private void EncodeDcScan(JpegScanDescriptor scan)
    {
        Component[] comps = scan.Components;
        bool refine = scan.Ah != 0;
        int al = scan.Al;
        if (comps.Length == 1)
        {
            // Non-interleaved: the component's own block grid, one block per restart unit.
            Component c = comps[0];
            int total = c.BlocksPerLine * c.BlocksPerColumn;
            int done = 0;
            for (int by = 0; by < c.BlocksPerColumn; by++)
            {
                for (int bx = 0; bx < c.BlocksPerLine; bx++)
                {
                    this.EncodeDcBlock(c, c.BlockOffset(bx, by), refine, al);
                    this.AfterUnit(++done, total, comps);
                }
            }

            return;
        }

        int totalMcus = this.mcusX * this.mcusY;
        int mcus = 0;
        for (int my = 0; my < this.mcusY; my++)
        {
            for (int mx = 0; mx < this.mcusX; mx++)
            {
                foreach (Component c in comps)
                {
                    for (int v = 0; v < c.V; v++)
                    {
                        for (int h = 0; h < c.H; h++)
                        {
                            this.EncodeDcBlock(c, c.BlockOffset((mx * c.H) + h, (my * c.V) + v), refine, al);
                        }
                    }
                }

                this.AfterUnit(++mcus, totalMcus, comps);
            }
        }
    }

    private void EncodeAcScan(JpegScanDescriptor scan)
    {
        Component c = scan.Components[0];
        bool refine = scan.Ah != 0;
        int total = c.BlocksPerLine * c.BlocksPerColumn;
        int done = 0;
        for (int by = 0; by < c.BlocksPerColumn; by++)
        {
            for (int bx = 0; bx < c.BlocksPerLine; bx++)
            {
                int offset = c.BlockOffset(bx, by);
                if (refine)
                {
                    this.EncodeAcRefineBlock(c, offset, scan.Ss, scan.Se, scan.Al);
                }
                else
                {
                    this.EncodeAcFirstBlock(c, offset, scan.Ss, scan.Se, scan.Al);
                }

                this.AfterUnit(++done, total, scan.Components);
            }
        }

        this.EmitEobRun(c);
    }

    /// <summary>Handles restart intervals after each MCU (or block, in non-interleaved scans).</summary>
    private void AfterUnit(int done, int total, Component[] comps)
    {
        if (this.restartInterval <= 0 || done >= total || done % this.restartInterval != 0)
        {
            return;
        }

        if (comps.Length == 1)
        {
            this.EmitEobRun(comps[0]);
        }

        this.writer?.WriteMarker((byte)(0xD0 + (((done / this.restartInterval) - 1) & 7)));
        foreach (Component c in comps)
        {
            c.Predictor = 0;
        }

        this.eobRun = 0;
        this.bufferedBits = 0;
    }

    // ----- Block coders -----

    /// <summary>DC first scan (G.1.2.1): the point-transformed DC difference; DC refinement: one bit per block.</summary>
    private void EncodeDcBlock(Component c, int offset, bool refine, int al)
    {
        int coef = c.Coefficients[offset];
        if (refine)
        {
            this.EmitBits((uint)(coef >> al) & 1, 1);
            return;
        }

        int value = coef >> al; // Arithmetic shift, as the decoder reconstructs value << Al.
        int diff = value - c.Predictor;
        c.Predictor = value;
        int magnitude = diff;
        int bits = diff;
        if (diff < 0)
        {
            magnitude = -diff;
            bits = diff - 1;
        }

        int nbits = 32 - BitOperations.LeadingZeroCount((uint)magnitude);
        this.EmitSymbol(c.DcLookup, this.dcFreq, c.TableIndex, nbits);
        this.EmitBits((uint)bits, nbits);
    }

    /// <summary>AC first scan (G.1.2.2): run/size coding of the point-transformed band with EOB runs.</summary>
    private void EncodeAcFirstBlock(Component c, int offset, int ss, int se, int al)
    {
        short[] coefficients = c.Coefficients;
        int run = 0;
        for (int k = ss; k <= se; k++)
        {
            int temp = coefficients[offset + k];
            if (temp == 0)
            {
                run++;
                continue;
            }

            // Point transform by Al: shift the magnitude, keep the sign; negative values are sent in one's complement.
            int bits;
            if (temp < 0)
            {
                temp = -temp >> al;
                bits = ~temp;
            }
            else
            {
                temp >>= al;
                bits = temp;
            }

            if (temp == 0)
            {
                run++;
                continue;
            }

            this.EmitEobRun(c);
            while (run > 15)
            {
                this.EmitSymbol(c.AcLookup, this.acFreq, c.TableIndex, 0xF0);
                run -= 16;
            }

            int nbits = 32 - BitOperations.LeadingZeroCount((uint)temp);
            this.EmitSymbol(c.AcLookup, this.acFreq, c.TableIndex, (run << 4) | nbits);
            this.EmitBits((uint)bits, nbits);
            run = 0;
        }

        if (run > 0)
        {
            this.eobRun++;
            if (this.eobRun >= this.maxEobRun)
            {
                this.EmitEobRun(c);
            }
        }
    }

    /// <summary>
    /// AC refinement scan (G.1.2.3): correction bits for coefficients that are already nonzero, run/size symbols
    /// (size always 1) plus a sign bit for newly nonzero ones, and EOB runs whose correction bits are buffered.
    /// </summary>
    private void EncodeAcRefineBlock(Component c, int offset, int ss, int se, int al)
    {
        short[] coefficients = c.Coefficients;
        Span<int> absValues = stackalloc int[64];

        // Pass 1: point-transformed magnitudes and the position of the last newly-nonzero coefficient.
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            int temp = coefficients[offset + k];
            if (temp < 0)
            {
                temp = -temp;
            }

            temp >>= al;
            absValues[k] = temp;
            if (temp == 1)
            {
                eob = k;
            }
        }

        // Pass 2: emit. This block's correction bits are collected right after the pending EOB run's buffered
        // bits; whenever the run is flushed they are emitted from where they were stored and collection restarts
        // at the front of the buffer.
        int run = 0;
        int blockBits = 0;
        byte[] buffer = this.correctionBits;
        int blockBitsStart = this.bufferedBits;
        for (int k = ss; k <= se; k++)
        {
            int temp = absValues[k];
            if (temp == 0)
            {
                run++;
                continue;
            }

            // Emit any required ZRLs, but not if they can be folded into the EOB.
            while (run > 15 && k <= eob)
            {
                this.EmitEobRun(c);
                this.EmitSymbol(c.AcLookup, this.acFreq, c.TableIndex, 0xF0);
                run -= 16;
                this.EmitBufferedBits(buffer, blockBitsStart, blockBits);
                blockBitsStart = 0;
                blockBits = 0;
            }

            if (temp > 1)
            {
                // Previously nonzero: only its next magnitude bit is sent, as a correction bit.
                buffer[blockBitsStart + blockBits++] = (byte)(temp & 1);
                continue;
            }

            // Newly nonzero: flush the EOB run (with its buffered bits), then run/size 1 and the sign bit.
            this.EmitEobRun(c);
            this.EmitSymbol(c.AcLookup, this.acFreq, c.TableIndex, (run << 4) | 1);
            this.EmitBits(coefficients[offset + k] < 0 ? 0u : 1u, 1);
            this.EmitBufferedBits(buffer, blockBitsStart, blockBits);
            blockBitsStart = 0;
            blockBits = 0;
            run = 0;
        }

        if (run > 0 || blockBits > 0)
        {
            // Trailing zeros: the block ends in an EOB whose correction bits stay buffered with the run.
            this.eobRun++;
            this.bufferedBits = blockBitsStart + blockBits;
            if (this.eobRun >= this.maxEobRun || this.bufferedBits > MaxCorrectionBits - 64)
            {
                this.EmitEobRun(c);
            }
        }
    }

    // ----- Emission primitives -----

    /// <summary>Emits the pending EOB run (EOBn symbol + run bits) followed by its buffered correction bits.</summary>
    private void EmitEobRun(Component c)
    {
        int run = this.eobRun;
        if (run > 0)
        {
            int nbits = BitOperations.Log2((uint)run); // Floor(log2 run): the EOBn category.
            this.EmitSymbol(c.AcLookup, this.acFreq, c.TableIndex, nbits << 4);
            if (nbits > 0)
            {
                this.EmitBits((uint)run, nbits);
            }

            this.eobRun = 0;
            this.EmitBufferedBits(this.correctionBits, 0, this.bufferedBits);
            this.bufferedBits = 0;
        }
    }

    private void EmitBufferedBits(byte[] buffer, int start, int count)
    {
        if (this.writer is null)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            this.writer.WriteBits(buffer[start + i], 1);
        }
    }

    private void EmitSymbol(int[] lookup, long[][]? freq, int tableIndex, int symbol)
    {
        if (this.writer is null)
        {
            freq![tableIndex][symbol]++;
            return;
        }

        int entry = lookup[symbol];
        this.writer.WriteBits((uint)entry >> 8, entry & 0xFF);
    }

    private void EmitBits(uint value, int nbits)
    {
        if (nbits > 0)
        {
            this.writer?.WriteBits(value & ((1u << nbits) - 1), nbits);
        }
    }
}
