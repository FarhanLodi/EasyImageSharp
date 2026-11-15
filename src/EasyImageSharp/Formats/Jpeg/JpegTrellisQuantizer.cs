using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Rate-distortion optimised ("trellis") quantisation of the AC coefficients of one block. Instead of rounding
/// every coefficient independently, a dynamic programme over the zigzag sequence picks, for every coefficient, a
/// value among the largest magnitude of each Huffman size category up to the rounded value (so 0, 1, 3, 7, ... and
/// the rounded value itself), minimising <c>bits + distortion</c>. Bits are the Huffman code lengths of the run/size
/// symbols plus the extra magnitude bits, including ZRL and EOB, so the optimiser sees that zeroing a lone
/// coefficient late in the block saves a whole symbol while zeroing one inside a dense run barely saves anything.
/// The output is an ordinary set of quantised coefficients and therefore a standard JPEG bitstream.
/// </summary>
/// <remarks>
/// <para>
/// The cost model follows the trellis quantiser of mozjpeg: distortion is the squared quantisation error measured
/// in quantiser steps (an error is weighted by the inverse square of its divisor, so the quantisation table acts
/// as a visibility model), scaled by a Lagrange multiplier that shrinks as the block's AC energy grows. Busy
/// blocks, where the eye forgives more, are therefore quantised more aggressively than flat ones. The multiplier
/// is <c>2^16 / (2^16.5 + E)</c> with <c>E</c> the mean squared AC coefficient at libjpeg's DCT scale (eight times
/// the orthonormal one). The energy offset is mozjpeg's default; the scale is 2^1.25 above mozjpeg's 2^14.75,
/// which on this library's benchmark corpus traded away so much fidelity that files came out larger than plain
/// rounding at matched PSNR. With 2^16 the trellis (with optimised Huffman tables) beats plain rounding at matched
/// SSIM and at matched PSNR alike; tools/JpegEncoderEvaluation reproduces the comparison.
/// </para>
/// <para>
/// DC coefficients are rounded as usual (their difference coding couples neighbouring blocks and is left alone),
/// and the rate model is the sequential Huffman code of the table passed in. Progressive files reuse the same
/// coefficients; their scans then code them with their own statistics.
/// </para>
/// </remarks>
internal sealed class JpegTrellisQuantizer
{
    /// <summary>Bits charged for a symbol the rate table has no code for (only possible with image-specific tables).</summary>
    private const float MissingCodePenalty = 24f;

    /// <summary>The Lagrangian's numerator, 2^16.</summary>
    private const float LambdaScale = 65536f;

    /// <summary>The energy offset in the Lagrangian's denominator, 2^16.5.</summary>
    private static readonly float LambdaOffset = (float)Math.Pow(2, 16.5);

    /// <summary>Divisor per zigzag position applied to the scaled DCT output; index 0 is DC.</summary>
    private readonly float[] reciprocalsZigzag = new float[64];

    /// <summary>Squared quantiser step per zigzag position: converts a step-domain value into libjpeg's scale (times 1/64).</summary>
    private readonly float[] stepSquaredZigzag = new float[64];

    /// <summary>Code length in bits of every AC symbol (run &lt;&lt; 4 | size).</summary>
    private readonly float[] acBits = new float[256];

    /// <summary>
    /// Creates a quantiser for one quantisation table and one AC Huffman table.
    /// </summary>
    /// <param name="quantNatural">The quantisation table in natural order.</param>
    /// <param name="reciprocalsNatural">The fused reciprocal table of <see cref="JpegForwardDct.CreateReciprocalTable"/>.</param>
    /// <param name="acTable">The AC Huffman table whose code lengths model the rate.</param>
    public JpegTrellisQuantizer(ReadOnlySpan<ushort> quantNatural, ReadOnlySpan<float> reciprocalsNatural, JpegHuffmanTable acTable)
    {
        if (quantNatural.Length < 64 || reciprocalsNatural.Length < 64)
        {
            throw new ArgumentException("Quantisation tables must hold 64 entries.");
        }

        for (int k = 0; k < 64; k++)
        {
            int natural = JpegTables.ZigZag[k];
            this.reciprocalsZigzag[k] = reciprocalsNatural[natural];
            float step = quantNatural[natural];
            this.stepSquaredZigzag[k] = step * step;
        }

        for (int symbol = 0; symbol < 256; symbol++)
        {
            int length = acTable.Lookup[symbol] & 0xFF;
            this.acBits[symbol] = length == 0 ? MissingCodePenalty : length;
        }
    }

    /// <summary>
    /// Quantises one block of scaled DCT output (natural order, as left by <see cref="JpegForwardDct.Transform(Span{float})"/>)
    /// into zigzag-ordered coefficients. Safe to call concurrently.
    /// </summary>
    public void Quantize(ReadOnlySpan<float> scaledNatural, Span<short> zigzagOut)
    {
        if (scaledNatural.Length < 64 || zigzagOut.Length < 64)
        {
            throw new ArgumentException("Block and output must hold 64 entries.");
        }

        ref float src = ref MemoryMarshal.GetReference(scaledNatural);
        ref int zz = ref MemoryMarshal.GetArrayDataReference(JpegTables.ZigZag);

        // Coefficients in quantiser steps, zigzag order. DC is rounded exactly as the plain quantiser rounds it.
        Span<float> value = stackalloc float[64];
        for (int k = 0; k < 64; k++)
        {
            value[k] = Unsafe.Add(ref src, Unsafe.Add(ref zz, k)) * this.reciprocalsZigzag[k];
        }

        zigzagOut[0] = (short)((int)(value[0] + 16384.5f) - 16384);

        // Block activity at libjpeg's DCT scale: x = 8 * step * value, so x^2 = 64 * step^2 * value^2.
        float energy = 0;
        for (int k = 1; k < 64; k++)
        {
            energy += this.stepSquaredZigzag[k] * value[k] * value[k];
        }

        energy = energy * 64f / 63f;

        // Distortion of an error of e steps costs weight * e^2 bits (64 converts libjpeg's scale back to steps).
        float weight = 64f * LambdaScale / (LambdaOffset + energy);

        Span<float> zeroDistortion = stackalloc float[64]; // Cost of coding positions 1..k all as zero.
        Span<float> bestCost = stackalloc float[64];       // Cheapest coding of 1..k ending in a nonzero at k.
        Span<int> runStart = stackalloc int[64];           // Previous nonzero position on that cheapest path.
        Span<short> chosen = stackalloc short[64];         // Magnitude chosen at k on that path (0 = never nonzero).
        Span<int> nonzero = stackalloc int[64];            // Positions that can be nonzero, in increasing order.
        int nonzeroCount = 0;

        zeroDistortion[0] = 0;
        bestCost[0] = 0;
        float zrlBits = this.acBits[0xF0];
        float eobBits = this.acBits[0x00];
        for (int i = 1; i < 64; i++)
        {
            float magnitude = MathF.Abs(value[i]);
            zeroDistortion[i] = zeroDistortion[i - 1] + (weight * magnitude * magnitude);
            chosen[i] = 0;

            // The largest candidate is exactly what plain quantisation writes (round half up, as in
            // JpegForwardDct.Quantize), so the trellis can only ever lower a magnitude, never raise one.
            int rounded = Math.Abs((int)(value[i] + 16384.5f) - 16384);
            if (rounded == 0)
            {
                continue;
            }

            // Candidates: the largest magnitude of every size category below the rounded value's, and that value.
            int categories = 32 - BitOperations.LeadingZeroCount((uint)rounded);
            float best = float.MaxValue;
            int bestStart = 0;
            int bestValue = rounded;
            for (int p = -1; p < nonzeroCount; p++)
            {
                int j = p < 0 ? 0 : nonzero[p];
                int run = i - 1 - j;
                float pathCost = bestCost[j] + (zeroDistortion[i - 1] - zeroDistortion[j]) + ((run >> 4) * zrlBits);
                int runNibble = (run & 15) << 4;
                for (int size = 1; size <= categories; size++)
                {
                    int candidate = size < categories ? (1 << size) - 1 : rounded;
                    float error = candidate - magnitude;
                    float cost = pathCost + this.acBits[runNibble | size] + size + (weight * error * error);
                    if (cost < best)
                    {
                        best = cost;
                        bestStart = j;
                        bestValue = candidate;
                    }
                }
            }

            bestCost[i] = best;
            runStart[i] = bestStart;
            chosen[i] = (short)bestValue;
            nonzero[nonzeroCount++] = i;
        }

        // Pick the last nonzero coefficient: everything after it is zero and costs an EOB (unless it is position 63).
        int last = 0;
        float lastCost = zeroDistortion[63] + eobBits;
        for (int p = 0; p < nonzeroCount; p++)
        {
            int i = nonzero[p];
            float cost = bestCost[i] + (zeroDistortion[63] - zeroDistortion[i]) + (i < 63 ? eobBits : 0);
            if (cost < lastCost)
            {
                lastCost = cost;
                last = i;
            }
        }

        // Walk the chosen path back from the last coefficient, zeroing the runs between its nonzero positions.
        zigzagOut[1..].Clear();
        for (int i = last; i > 0; i = runStart[i])
        {
            float signed = value[i];
            zigzagOut[i] = signed < 0 ? (short)-chosen[i] : chosen[i];
        }
    }
}
