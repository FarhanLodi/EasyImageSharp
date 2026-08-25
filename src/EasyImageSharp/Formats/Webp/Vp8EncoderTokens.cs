namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Receives the individual boolean decisions of the DCT token trees. The coefficient walker is written once
/// and instantiated over a value type, so the writing pass and the statistics pass share exactly the same
/// traversal without paying for virtual dispatch.
/// </summary>
internal interface IVp8TokenSink
{
    /// <summary>Consumes a bit coded with the adaptive probability at <paramref name="probaIndex"/>.</summary>
    void Adaptive(int bit, int probaIndex);

    /// <summary>Consumes a bit coded with a probability fixed by the specification.</summary>
    void Fixed(int bit, int prob);
}

/// <summary>A sink that writes the tokens into a boolean-coded partition.</summary>
internal readonly struct Vp8TokenWriter : IVp8TokenSink
{
    private readonly Vp8BoolWriter writer;
    private readonly byte[] probas;

    /// <summary>Creates a sink writing to <paramref name="writer"/> with the frame's token probabilities.</summary>
    public Vp8TokenWriter(Vp8BoolWriter writer, byte[] probas)
    {
        this.writer = writer;
        this.probas = probas;
    }

    /// <inheritdoc/>
    public void Adaptive(int bit, int probaIndex) => this.writer.PutBit(bit, this.probas[probaIndex]);

    /// <inheritdoc/>
    public void Fixed(int bit, int prob) => this.writer.PutBit(bit, prob);
}

/// <summary>
/// A sink that counts, for every adaptive probability, how many ones and how many bits were coded with it.
/// The counts are packed as <c>(total &lt;&lt; 16) | ones</c> and halved before they can overflow.
/// </summary>
internal readonly struct Vp8TokenRecorder : IVp8TokenSink
{
    private readonly uint[] stats;

    /// <summary>Creates a recorder accumulating into <paramref name="stats"/>.</summary>
    public Vp8TokenRecorder(uint[] stats) => this.stats = stats;

    /// <inheritdoc/>
    public void Adaptive(int bit, int probaIndex)
    {
        uint p = this.stats[probaIndex];
        if (p >= 0xffff0000u)
        {
            p = ((p + 1u) >> 1) & 0x7fff7fffu;
        }

        this.stats[probaIndex] = p + 0x00010000u + (uint)bit;
    }

    /// <inheritdoc/>
    public void Fixed(int bit, int prob)
    {
    }
}

/// <summary>Walks the DCT token trees of RFC 6386 section 13, feeding every decision to a sink.</summary>
internal static class Vp8EncoderTokens
{
    /// <summary>
    /// Emits the coefficients of one 4x4 block. <paramref name="levels"/> holds the quantized magnitudes in
    /// zig-zag order starting at <paramref name="off"/>, and <paramref name="last"/> is the index of the last
    /// non-zero one or -1. Returns 1 when the block carries any coefficient, which is the non-zero context
    /// its neighbours will use.
    /// </summary>
    public static int PutCoeffs<TSink>(
        ref TSink sink, int ctx, int type, int first, short[] levels, int off, int last)
        where TSink : struct, IVp8TokenSink
    {
        ReadOnlySpan<byte> bands = Vp8EncoderTables.Bands;
        int n = first;
        int p = Vp8Cost.ProbaIndex(type, bands[n], ctx);
        if (last < 0)
        {
            sink.Adaptive(0, p);
            return 0;
        }

        sink.Adaptive(1, p);
        while (n < 16)
        {
            int c = levels[off + n];
            n++;
            int sign = c < 0 ? 1 : 0;
            int v = c < 0 ? -c : c;
            if (v == 0)
            {
                sink.Adaptive(0, p + 1);
                p = Vp8Cost.ProbaIndex(type, bands[n], 0);
                continue;
            }

            sink.Adaptive(1, p + 1);
            if (v == 1)
            {
                sink.Adaptive(0, p + 2);
                p = Vp8Cost.ProbaIndex(type, bands[n], 1);
            }
            else
            {
                sink.Adaptive(1, p + 2);
                PutLargeValue(ref sink, v, p);
                p = Vp8Cost.ProbaIndex(type, bands[n], 2);
            }

            sink.Fixed(sign, 0x80);
            if (n == 16)
            {
                return 1;
            }

            int more = n <= last ? 1 : 0;
            sink.Adaptive(more, p);
            if (more == 0)
            {
                return 1;
            }
        }

        return 1;
    }

    private static void PutLargeValue<TSink>(ref TSink sink, int v, int p)
        where TSink : struct, IVp8TokenSink
    {
        if (v <= 4)
        {
            sink.Adaptive(0, p + 3);
            if (v == 2)
            {
                sink.Adaptive(0, p + 4);
            }
            else
            {
                sink.Adaptive(1, p + 4);
                sink.Adaptive(v == 4 ? 1 : 0, p + 5);
            }

            return;
        }

        sink.Adaptive(1, p + 3);
        if (v <= 10)
        {
            sink.Adaptive(0, p + 6);
            if (v <= 6)
            {
                sink.Adaptive(0, p + 7);
                sink.Fixed(v - 5, 159);
            }
            else
            {
                sink.Adaptive(1, p + 7);
                sink.Fixed((v - 7) >> 1, 165);
                sink.Fixed((v - 7) & 1, 145);
            }

            return;
        }

        sink.Adaptive(1, p + 6);
        ReadOnlySpan<byte> cat;
        int baseValue;
        if (v <= 18)
        {
            sink.Adaptive(0, p + 8);
            sink.Adaptive(0, p + 9);
            cat = Vp8EncoderTables.Cat3;
            baseValue = 11;
        }
        else if (v <= 34)
        {
            sink.Adaptive(0, p + 8);
            sink.Adaptive(1, p + 9);
            cat = Vp8EncoderTables.Cat4;
            baseValue = 19;
        }
        else if (v <= 66)
        {
            sink.Adaptive(1, p + 8);
            sink.Adaptive(0, p + 10);
            cat = Vp8EncoderTables.Cat5;
            baseValue = 35;
        }
        else
        {
            sink.Adaptive(1, p + 8);
            sink.Adaptive(1, p + 10);
            cat = Vp8EncoderTables.Cat6;
            baseValue = 67;
        }

        int extra = cat.Length - 1; // The category tables are zero terminated.
        int residual = v - baseValue;
        for (int i = 0; i < extra; i++)
        {
            sink.Fixed((residual >> (extra - 1 - i)) & 1, cat[i]);
        }
    }
}
