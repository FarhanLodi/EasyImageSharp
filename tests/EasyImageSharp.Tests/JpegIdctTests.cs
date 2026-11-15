using EasyImageSharp.Formats.Jpeg;
using Xunit;
using Xunit.Abstractions;
using IdctPath = EasyImageSharp.Formats.Jpeg.JpegIdct.IdctPath;

namespace EasyImageSharp.Tests;

/// <summary>
/// Accuracy and consistency of the decoder's factorised inverse DCT (<see cref="JpegIdct"/>). The references are
/// written here from ITU-T T.81 A.3.3 directly: a double-precision separable transform, and the single-precision
/// matrix product the decoder used before the factorised transform replaced it.
/// </summary>
public class JpegIdctTests(ITestOutputHelper output)
{
    private static readonly IdctPath[] Paths = { IdctPath.Scalar, IdctPath.Vector128, IdctPath.Vector256 };

    // ----- Accuracy -----

    /// <summary>
    /// The IEEE 1180-1990 procedure: random integer pixel blocks, forward transformed in double precision and
    /// rounded, then inverse transformed by the reference and by the implementation under test, both rounded and
    /// clamped to -256..255. The standard's limits on peak, mean and mean-square error must hold.
    /// </summary>
    [Theory]
    [InlineData(256, 255, false)]
    [InlineData(5, 5, false)]
    [InlineData(300, 300, false)]
    [InlineData(256, 255, true)]
    [InlineData(5, 5, true)]
    [InlineData(300, 300, true)]
    public void MeetsIeee1180AccuracyLimits(int low, int high, bool negate)
    {
        const int Blocks = 10000;
        var rng = new Ieee1180Random(1);
        foreach (bool fused in FusedFlavours())
        {
            var sumError = new double[64];
            var sumSquared = new double[64];
            int peak = 0;
            var pixels = new double[64];
            var coefficients = new int[64];
            var block = new float[64];
            ushort[] unitQuant = Enumerable.Repeat((ushort)1, 64).ToArray();
            float[] dequant = JpegIdct.BuildDequantTable(unitQuant);
            for (int n = 0; n < Blocks; n++)
            {
                for (int i = 0; i < 64; i++)
                {
                    pixels[i] = rng.Next(low, high) * (negate ? -1 : 1);
                }

                double[] forward = ForwardDct(pixels);
                for (int i = 0; i < 64; i++)
                {
                    coefficients[i] = Math.Clamp((int)Math.Round(forward[i], MidpointRounding.AwayFromZero), -2048, 2047);
                }

                double[] reference = ReferenceIdct(coefficients.Select(c => (double)c).ToArray());
                for (int i = 0; i < 64; i++)
                {
                    block[i] = coefficients[i] * dequant[i];
                }

                JpegIdct.Transform(block, IdctPath.Scalar, fused);
                for (int i = 0; i < 64; i++)
                {
                    int expected = Math.Clamp((int)Math.Round(reference[i], MidpointRounding.AwayFromZero), -256, 255);
                    int actual = Math.Clamp((int)Math.Floor(block[i] + 0.5), -256, 255);
                    int error = actual - expected;
                    sumError[i] += error;
                    sumSquared[i] += error * error;
                    peak = Math.Max(peak, Math.Abs(error));
                }
            }

            double worstMean = sumError.Max(e => Math.Abs(e)) / Blocks;
            double worstMse = sumSquared.Max() / Blocks;
            double overallMean = Math.Abs(sumError.Sum()) / (64.0 * Blocks);
            double overallMse = sumSquared.Sum() / (64.0 * Blocks);
            output.WriteLine(
                $"range -{low}..{high}{(negate ? " negated" : string.Empty)} fused={fused}: peak {peak}, pmse {worstMse:F4}, " +
                $"omse {overallMse:F4}, pme {worstMean:F4}, ome {overallMean:F5}");
            Assert.True(peak <= 1, $"peak error {peak} > 1");
            Assert.True(worstMse <= 0.06, $"per-position mean square error {worstMse:F4} > 0.06");
            Assert.True(overallMse <= 0.02, $"overall mean square error {overallMse:F4} > 0.02");
            Assert.True(worstMean <= 0.015, $"per-position mean error {worstMean:F4} > 0.015");
            Assert.True(overallMean <= 0.0015, $"overall mean error {overallMean:F5} > 0.0015");
        }
    }

    /// <summary>
    /// Differential test against the single-precision matrix IDCT the decoder used to run: on blocks shaped like real
    /// quantized data the two produce the same 8-bit samples, give or take one level where a value sits on a
    /// rounding boundary.
    /// </summary>
    [Fact]
    public void AgreesWithFormerMatrixTransformWithinOneLevel()
    {
        var rng = new Random(20261104);
        ushort[] quant = JpegTables.StdLuminanceQuant.Select(q => (ushort)Math.Max(1, q / 2)).ToArray();
        float[] dequant = JpegIdct.BuildDequantTable(quant);
        var natural = new float[64];
        var aan = new float[64];
        int differing = 0;
        int worst = 0;
        const int Blocks = 20000;
        for (int n = 0; n < Blocks; n++)
        {
            FillQuantizedBlock(rng, natural, aan, quant, dequant);
            float[] matrix = MatrixIdct(natural);
            JpegIdct.Transform(aan);
            for (int i = 0; i < 64; i++)
            {
                int expected = (int)Math.Clamp(matrix[i] + 128.5f, 0f, 255f);
                int actual = (int)Math.Clamp(aan[i] + 128.5f, 0f, 255f);
                int delta = Math.Abs(actual - expected);
                worst = Math.Max(worst, delta);
                differing += delta == 0 ? 0 : 1;
            }
        }

        output.WriteLine($"{differing} of {Blocks * 64} samples differ from the matrix transform, worst by {worst}");
        Assert.True(worst <= 1, $"a sample differs by {worst} levels");
        Assert.True(differing < Blocks * 64 / 1000, $"{differing} samples differ, more than 0.1%");
    }

    /// <summary>The float transform tracks the exact double-precision result closely on realistic data.</summary>
    [Fact]
    public void TracksDoublePrecisionReference()
    {
        var rng = new Random(7);
        ushort[] quant = JpegTables.StdChrominanceQuant.Select(q => (ushort)q).ToArray();
        float[] dequant = JpegIdct.BuildDequantTable(quant);
        var natural = new float[64];
        var aan = new float[64];
        double worst = 0;
        for (int n = 0; n < 5000; n++)
        {
            FillQuantizedBlock(rng, natural, aan, quant, dequant);
            double[] reference = ReferenceIdct(natural.Select(v => (double)v).ToArray());
            JpegIdct.Transform(aan);
            for (int i = 0; i < 64; i++)
            {
                worst = Math.Max(worst, Math.Abs(aan[i] - reference[i]));
            }
        }

        output.WriteLine($"worst absolute deviation from the double-precision transform: {worst:G4}");
        Assert.True(worst < 0.01, $"deviation {worst} from the exact transform");
    }

    // ----- Path consistency -----

    [Fact]
    public void EveryPathIsBitIdenticalForTheSameArithmetic()
    {
        var rng = new Random(99);
        ushort[] quant = JpegTables.StdLuminanceQuant.Select(q => (ushort)q).ToArray();
        float[] dequant = JpegIdct.BuildDequantTable(quant);
        var natural = new float[64];
        var aan = new float[64];
        foreach (bool fused in FusedFlavours())
        {
            for (int n = 0; n < 2000; n++)
            {
                FillQuantizedBlock(rng, natural, aan, quant, dequant);
                float[] scalar = aan.ToArray();
                JpegIdct.Transform(scalar, IdctPath.Scalar, fused);
                foreach (IdctPath path in Paths.Where(p => p != IdctPath.Scalar && JpegIdct.IsSupported(p, fused)))
                {
                    float[] vector = aan.ToArray();
                    JpegIdct.Transform(vector, path, fused);
                    for (int i = 0; i < 64; i++)
                    {
                        Assert.True(
                            BitConverter.SingleToInt32Bits(scalar[i]) == BitConverter.SingleToInt32Bits(vector[i]),
                            $"{path} fused={fused}: element {i} is {vector[i]:R}, scalar {scalar[i]:R}");
                    }
                }
            }
        }
    }

    [Fact]
    public void FusedAndUnfusedArithmeticAgreeWithinOneLevel()
    {
        if (!JpegIdct.IsSupported(IdctPath.Scalar, fused: true))
        {
            return;
        }

        var rng = new Random(5);
        ushort[] quant = JpegTables.StdLuminanceQuant.Select(q => (ushort)q).ToArray();
        float[] dequant = JpegIdct.BuildDequantTable(quant);
        var natural = new float[64];
        var aan = new float[64];
        int worst = 0;
        for (int n = 0; n < 5000; n++)
        {
            FillQuantizedBlock(rng, natural, aan, quant, dequant);
            float[] fused = aan.ToArray();
            float[] unfused = aan.ToArray();
            JpegIdct.Transform(fused, IdctPath.Scalar, fused: true);
            JpegIdct.Transform(unfused, IdctPath.Scalar, fused: false);
            for (int i = 0; i < 64; i++)
            {
                int a = (int)Math.Clamp(fused[i] + 128.5f, 0f, 255f);
                int b = (int)Math.Clamp(unfused[i] + 128.5f, 0f, 255f);
                worst = Math.Max(worst, Math.Abs(a - b));
            }
        }

        Assert.True(worst <= 1, $"fused and unfused results differ by {worst} levels");
    }

    /// <summary>The DC-only shortcut the decoder takes for flat blocks must equal the full transform exactly.</summary>
    [Theory]
    [InlineData(1, 16)]
    [InlineData(-37, 11)]
    [InlineData(250, 3)]
    [InlineData(-1023, 1)]
    public void DcOnlyBlockReconstructsToItsDequantizedValue(int dc, int step)
    {
        ushort[] quant = Enumerable.Repeat((ushort)step, 64).ToArray();
        float[] dequant = JpegIdct.BuildDequantTable(quant);
        foreach (bool fused in FusedFlavours())
        {
            foreach (IdctPath path in Paths.Where(p => JpegIdct.IsSupported(p, fused)))
            {
                var block = new float[64];
                block[0] = dc * dequant[0];
                float flat = block[0];
                JpegIdct.Transform(block, path, fused);
                Assert.All(block, value => Assert.Equal(flat, value));
                Assert.Equal(dc * step / 8.0, flat, 6);
            }
        }
    }

    [Fact]
    public void ByteAndWideStoresRoundAndClampLikeTheScalarDefinition()
    {
        var rng = new Random(11);
        var stride = 13;
        var bytes = new byte[(7 * stride) + 8];
        var wide = new ushort[(7 * stride) + 8];
        for (int n = 0; n < 500; n++)
        {
            var block = new float[64];
            for (int i = 0; i < 64; i++)
            {
                block[i] = (float)((rng.NextDouble() * 5000) - 2500);
            }

            if (n % 7 == 0)
            {
                block[rng.Next(64)] = 1e9f; // Far outside any sample range: must saturate, not wrap.
            }

            float[] expected = block.ToArray();
            JpegIdct.Transform(expected);
            float[] forBytes = block.ToArray();
            JpegIdct.TransformToBytes(forBytes, bytes, stride);
            float[] forWide = block.ToArray();
            JpegIdct.TransformToWide(forWide, wide, stride, 4095);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    float value = expected[(y * 8) + x];
                    Assert.Equal((byte)(int)Math.Clamp(value + 128.5f, 0f, 255f), bytes[(y * stride) + x]);
                    Assert.Equal((ushort)(int)Math.Clamp(value + 2048.5f, 0f, 4095f), wide[(y * stride) + x]);
                }
            }
        }
    }

    // ----- Helpers -----

    private static IEnumerable<bool> FusedFlavours()
    {
        yield return false;
        if (JpegIdct.IsSupported(IdctPath.Scalar, fused: true) && JpegIdct.UsesFma)
        {
            yield return true;
        }
    }

    /// <summary>
    /// Fills a block the way entropy decoding does: a DC term and a few low-frequency AC terms, sparser towards high
    /// frequencies, both as natural dequantized values and as AAN-dequantized input.
    /// </summary>
    private static void FillQuantizedBlock(Random rng, float[] natural, float[] aan, ushort[] quant, float[] dequant)
    {
        Array.Clear(natural);
        Array.Clear(aan);
        int count = 1 + rng.Next(20);
        for (int j = 0; j < count; j++)
        {
            int zz = j == 0 ? 0 : Math.Min(63, (int)(rng.NextDouble() * rng.NextDouble() * 64));
            int index = JpegTables.ZigZag[zz];
            int magnitude = zz == 0 ? 1024 / quant[index] : 256 / Math.Max(1, quant[index] / 2);
            int value = rng.Next(-magnitude, magnitude + 1);
            natural[index] = value * quant[index];
            aan[index] = value * dequant[index];
        }
    }

    /// <summary>T.81 A.3.3 forward DCT in double precision.</summary>
    private static double[] ForwardDct(double[] pixels)
    {
        var result = new double[64];
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        sum += pixels[(y * 8) + x] * Basis(u, x) * Basis(v, y);
                    }
                }

                result[(v * 8) + u] = sum;
            }
        }

        return result;
    }

    /// <summary>T.81 A.3.3 inverse DCT in double precision.</summary>
    private static double[] ReferenceIdct(double[] coefficients)
    {
        var result = new double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    for (int u = 0; u < 8; u++)
                    {
                        sum += coefficients[(v * 8) + u] * Basis(u, x) * Basis(v, y);
                    }
                }

                result[(y * 8) + x] = sum;
            }
        }

        return result;
    }

    /// <summary>0.5 c(u) cos((2x + 1) u pi / 16): the separable DCT basis, normalised so the two passes are orthonormal.</summary>
    private static double Basis(int u, int x) => 0.5 * (u == 0 ? 1 / Math.Sqrt(2) : 1.0) * Math.Cos(((2 * x) + 1) * u * Math.PI / 16);

    /// <summary>
    /// The single-precision separable matrix product the decoder ran before the factorised transform (a row pass
    /// and a column pass over the float basis, 128 multiply-adds per output row pair), kept as a reference.
    /// </summary>
    private static float[] MatrixIdct(float[] block)
    {
        var table = new float[64];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = (float)Basis(u, x);
            }
        }

        var temp = new float[64];
        var result = new float[64];
        for (int v = 0; v < 8; v++)
        {
            for (int x = 0; x < 8; x++)
            {
                float s = 0;
                for (int u = 0; u < 8; u++)
                {
                    s += table[(u * 8) + x] * block[(v * 8) + u];
                }

                temp[(v * 8) + x] = s;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                float s = 0;
                for (int v = 0; v < 8; v++)
                {
                    s += table[(v * 8) + y] * temp[(v * 8) + x];
                }

                result[(y * 8) + x] = s;
            }
        }

        return result;
    }

    /// <summary>The linear congruential generator IEEE 1180-1990 specifies for its test blocks.</summary>
    private sealed class Ieee1180Random(int seed)
    {
        private long state = seed;

        /// <summary>A uniformly distributed integer in [-low, high], as the standard's randomizer produces it.</summary>
        public int Next(int low, int high)
        {
            this.state = ((this.state * 1103515245) + 12345) & 0xFFFFFFFF;
            long i = this.state & 0x7FFFFFFE;
            double x = i / (double)0x7FFFFFFF;
            x *= low + high + 1;
            return (int)x - low;
        }
    }
}
