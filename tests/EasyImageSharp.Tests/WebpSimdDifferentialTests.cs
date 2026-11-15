using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Randomised differential tests of the WebP vector kernels against their scalar reference implementations.
/// The kernels are called directly rather than through <see cref="SimdConfig.ForceScalarFallback"/>, so the
/// comparison cannot be disturbed by other tests flipping that global switch. Every kernel must be
/// bit-identical to its reference for every input, including values a valid bitstream never produces.
/// </summary>
public class WebpSimdDifferentialTests
{
    private const int Bps = Vp8Dsp.Bps;

    /// <summary>True when this machine can run the 128-bit kernels at all (the switch itself is ignored here).</summary>
    private static bool VectorHardware
        => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    // ----- Inverse transforms -----

    [Theory]
    [InlineData(1, 64)]
    [InlineData(2, 2048)]
    [InlineData(3, 32768)]
    public void TransformOneMatchesScalar(int seed, int magnitude)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(seed);
        var coefficients = new short[16 * 3];
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            int inOff = random.Next(3) * 16;
            for (int i = 0; i < 16; i++)
            {
                coefficients[inOff + i] = RandomCoefficient(random, magnitude);
            }

            int off = Bps + 1 + random.Next(8);
            byte[] expected = RandomBlockBuffer(random);
            byte[] actual = (byte[])expected.Clone();
            Vp8Dsp.TransformOneScalar(coefficients, inOff, expected, off);
            Vp8DspSimd.TransformOne(coefficients, inOff, actual, off);
            AssertSame(expected, actual, $"TransformOne iteration {iteration}");
        }
    }

    [Theory]
    [InlineData(4, 64)]
    [InlineData(5, 32768)]
    public void TransformAc3MatchesScalarAndTheFullTransform(int seed, int magnitude)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(seed);
        var coefficients = new short[16];
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            Array.Clear(coefficients);
            coefficients[0] = RandomCoefficient(random, magnitude);
            coefficients[1] = RandomCoefficient(random, magnitude);
            coefficients[4] = RandomCoefficient(random, magnitude);

            int off = Bps + 1 + random.Next(8);
            byte[] expected = RandomBlockBuffer(random);
            byte[] full = (byte[])expected.Clone();
            byte[] actual = (byte[])expected.Clone();
            Vp8Dsp.TransformAc3Scalar(coefficients, 0, expected, off);
            Vp8Dsp.TransformOneScalar(coefficients, 0, full, off);
            Vp8DspSimd.TransformAc3(coefficients, 0, actual, off);
            AssertSame(expected, actual, $"TransformAc3 iteration {iteration}");
            AssertSame(full, actual, $"TransformAc3 vs TransformOne iteration {iteration}");
        }
    }

    [Fact]
    public void TransformDcMatchesScalarForEveryDcValue()
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(6);
        var coefficients = new short[16];
        for (int dc = short.MinValue; dc <= short.MaxValue; dc += 7)
        {
            coefficients[0] = (short)dc;
            int off = Bps + 1 + random.Next(8);
            byte[] expected = RandomBlockBuffer(random);
            byte[] actual = (byte[])expected.Clone();
            Vp8Dsp.TransformDcScalar(coefficients, 0, expected, off);
            Vp8DspSimd.TransformDc(coefficients, 0, actual, off);
            AssertSame(expected, actual, $"TransformDc dc={dc}");
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void TrueMotionMatchesScalar(int size)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(7 + size);
        for (int iteration = 0; iteration < 5_000; iteration++)
        {
            byte[] expected = new byte[Bps * (size + 2)];
            random.NextBytes(expected);
            byte[] actual = (byte[])expected.Clone();
            int off = Bps + 1 + random.Next(Bps - size - 1);
            Vp8Dsp.TrueMotionScalar(expected, off, size);
            Vp8DspSimd.TrueMotion(actual, off, size);
            AssertSame(expected, actual, $"TrueMotion{size} iteration {iteration}");
        }
    }

    // ----- Loop filters -----

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    public void SimpleFiltersMatchScalar(int seed, bool inner)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(seed);
        const int Stride = 48;
        int changed = 0;
        for (int iteration = 0; iteration < 3_000; iteration++)
        {
            byte[] plane = RandomFilterPlane(random, Stride, 40);
            int off = (16 * Stride) + 16;
            int thresh = RandomThreshold(random);

            byte[] expected = (byte[])plane.Clone();
            byte[] actual = (byte[])plane.Clone();
            if (inner)
            {
                for (int k = 1; k <= 3; k++)
                {
                    Vp8Dsp.SimpleVFilter16Scalar(expected, off + (4 * k * Stride), Stride, thresh);
                    Vp8DspSimd.SimpleVFilter16(actual, off + (4 * k * Stride), Stride, thresh);
                }

                for (int k = 1; k <= 3; k++)
                {
                    Vp8Dsp.SimpleHFilter16Scalar(expected, off + (4 * k), Stride, thresh);
                    Vp8DspSimd.SimpleHFilter16(actual, off + (4 * k), Stride, thresh);
                }
            }
            else
            {
                Vp8Dsp.SimpleVFilter16Scalar(expected, off, Stride, thresh);
                Vp8DspSimd.SimpleVFilter16(actual, off, Stride, thresh);
                Vp8Dsp.SimpleHFilter16Scalar(expected, off, Stride, thresh);
                Vp8DspSimd.SimpleHFilter16(actual, off, Stride, thresh);
            }

            AssertSame(expected, actual, $"simple filter (inner={inner}) iteration {iteration}, thresh {thresh}");
            changed += expected.AsSpan().SequenceEqual(plane) ? 0 : 1;
        }

        // The planes must actually exercise the filter, or the comparison above proves nothing.
        Assert.True(changed > 1_000, $"Only {changed} of 3000 planes were modified by the filter.");
    }

    [Theory]
    [InlineData(21, 16, true)]
    [InlineData(22, 16, false)]
    [InlineData(23, 8, true)]
    [InlineData(24, 8, false)]
    public void NormalFiltersMatchScalar(int seed, int size, bool macroblockEdge)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(seed);
        const int Stride = 40;
        int changed = 0;
        for (int iteration = 0; iteration < 3_000; iteration++)
        {
            byte[] plane = RandomFilterPlane(random, Stride, 36);
            int off = (16 * Stride) + 12;
            int thresh = RandomThreshold(random);
            int ithresh = random.Next(4) == 0 ? random.Next(256) : random.Next(64);
            int hevThresh = random.Next(4) == 0 ? random.Next(256) : random.Next(3);

            byte[] expected = (byte[])plane.Clone();
            byte[] actual = (byte[])plane.Clone();

            // Vertical-edge (H) filter first, then the horizontal-edge (V) filter, as the decoder orders them.
            int edges = macroblockEdge ? 1 : (size == 16 ? 3 : 1);
            for (int k = 0; k < edges; k++)
            {
                int hOff = macroblockEdge ? off : off + (4 * (k + 1));
                if (macroblockEdge)
                {
                    Vp8Dsp.FilterLoop26(expected, hOff, 1, Stride, size, thresh, ithresh, hevThresh);
                }
                else
                {
                    Vp8Dsp.FilterLoop24(expected, hOff, 1, Stride, size, thresh, ithresh, hevThresh);
                }

                Vp8DspSimd.HFilter(actual, hOff, Stride, size, macroblockEdge, thresh, ithresh, hevThresh);
            }

            for (int k = 0; k < edges; k++)
            {
                int vOff = macroblockEdge ? off : off + (4 * (k + 1) * Stride);
                if (macroblockEdge)
                {
                    Vp8Dsp.FilterLoop26(expected, vOff, Stride, 1, size, thresh, ithresh, hevThresh);
                }
                else
                {
                    Vp8Dsp.FilterLoop24(expected, vOff, Stride, 1, size, thresh, ithresh, hevThresh);
                }

                Vp8DspSimd.VFilter(actual, vOff, Stride, size, macroblockEdge, thresh, ithresh, hevThresh);
            }

            AssertSame(expected, actual, $"normal filter size {size} mb={macroblockEdge} iteration {iteration} ({thresh}/{ithresh}/{hevThresh})");
            changed += expected.AsSpan().SequenceEqual(plane) ? 0 : 1;
        }

        Assert.True(changed > 1_000, $"Only {changed} of 3000 planes were modified by the filter.");
    }

    // ----- YUV to RGBA -----

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void FancyUpsamplingMatchesScalar(int seed)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int width = iteration < 40 ? iteration + 1 : random.Next(1, 300);
            int height = random.Next(1, 24);
            var planes = new Vp8Planes(width, height, (width + 15) / 16, (height + 15) / 16);

            // Mostly random samples, sometimes saturated ones, so every clamp of the conversion is reached.
            FillPlane(random, planes.Y);
            FillPlane(random, planes.U);
            FillPlane(random, planes.V);

            var expected = new Rgba32[width * height];
            var actual128 = new Rgba32[width * height];
            var actual256 = new Rgba32[width * height];
            WebpYuv.ToRgba(planes, expected, vector: false, allow256: false);
            WebpYuv.ToRgba(planes, actual128, vector: WebpYuvSimd.IsSupported, allow256: false);
            WebpYuv.ToRgba(planes, actual256, vector: WebpYuvSimd.IsSupported, allow256: true);
            for (int i = 0; i < expected.Length; i++)
            {
                if (!expected[i].Equals(actual128[i]) || !expected[i].Equals(actual256[i]))
                {
                    Assert.Fail($"{width}x{height}: pixel {i} scalar {expected[i]}, 128-bit {actual128[i]}, 256-bit {actual256[i]}.");
                }
            }
        }
    }

    // ----- Encoder kernels -----

    [Fact]
    public void ForwardTransformMatchesScalar()
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(41);
        var expected = new short[32];
        var actual = new short[32];
        for (int iteration = 0; iteration < 50_000; iteration++)
        {
            byte[] source = RandomBlockBuffer(random);
            byte[] prediction = iteration % 5 == 0 ? (byte[])source.Clone() : RandomBlockBuffer(random);
            if (iteration % 7 == 0)
            {
                // Extreme residuals: one side saturated high, the other low.
                Array.Fill(source, (byte)255);
                Array.Fill(prediction, (byte)0);
            }

            int srcOff = random.Next(Bps + 4);
            int predOff = random.Next(Bps + 4);
            int outOff = random.Next(17);
            Vp8EncoderDsp.FTransformScalar(source, srcOff, prediction, predOff, expected, outOff);
            Vp8EncoderDspSimd.FTransform(source, srcOff, prediction, predOff, actual, outOff);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"FTransform iteration {iteration} differs.");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void SumOfSquaredErrorsMatchesScalar(int size)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(42 + size);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            var a = new byte[Bps * (size + 1)];
            var b = new byte[Bps * (size + 1)];
            random.NextBytes(a);
            random.NextBytes(b);
            if (iteration % 9 == 0)
            {
                Array.Fill(a, (byte)255);
                Array.Fill(b, (byte)0);
            }

            int aOff = random.Next(Bps - size + 1);
            int bOff = random.Next(Bps - size + 1);
            Assert.Equal(Vp8EncoderDsp.SseScalar(a, aOff, b, bOff, size), Vp8EncoderDspSimd.Sse(a, aOff, b, bOff, size));
        }
    }

    [Theory]
    [InlineData(51, false)]
    [InlineData(52, true)]
    public void BlockQuantizerMatchesScalar(int seed, bool arbitraryTables)
    {
        if (!VectorHardware)
        {
            return;
        }

        var random = new Random(seed);
        var q = new ushort[16];
        var iq = new int[16];
        var bias = new int[16];
        var zeroThreshold = new int[16];
        var sharpen = new int[16];
        var coefficients = new short[48];
        var expectedLevels = new short[48];
        var actualLevels = new short[48];
        for (int iteration = 0; iteration < 30_000; iteration++)
        {
            if (iteration % 50 == 0)
            {
                FillQuantizerTables(random, arbitraryTables, q, iq, bias, zeroThreshold, sharpen);
            }

            int coeffOff = random.Next(3) * 16;
            int levelOff = random.Next(33);
            int magnitude = random.Next(4) switch { 0 => 32, 1 => 512, 2 => 4096, _ => 32768 };
            for (int i = 0; i < 16; i++)
            {
                coefficients[coeffOff + i] = random.Next(3) == 0 ? (short)0 : RandomCoefficient(random, magnitude);
            }

            short[] expectedCoefficients = (short[])coefficients.Clone();
            short[] actualCoefficients = (short[])coefficients.Clone();
            random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expectedLevels.AsSpan()));
            expectedLevels.CopyTo(actualLevels, 0);

            int expectedLast = Vp8EncoderDsp.QuantizeBlockScalar(expectedCoefficients, coeffOff, expectedLevels, levelOff, q, iq, bias, zeroThreshold, sharpen);
            int actualLast = Vp8EncoderDspSimd.QuantizeBlock(actualCoefficients, coeffOff, actualLevels, levelOff, q, iq, bias, zeroThreshold, sharpen);
            Assert.Equal(expectedLast, actualLast);
            Assert.True(expectedCoefficients.AsSpan().SequenceEqual(actualCoefficients), $"Quantized coefficients differ at iteration {iteration}.");
            Assert.True(expectedLevels.AsSpan().SequenceEqual(actualLevels), $"Levels differ at iteration {iteration}.");
        }
    }

    [Fact]
    public void LossyEncodeIsIdenticalWithAndWithoutVectorKernels()
    {
        using Image<Rgba32> source = CreateSyntheticImage(181, 97);
        foreach (int quality in new[] { 5, 60, 100 })
        {
            byte[] scalar = EncodeWith(source, quality, forceScalar: true);
            byte[] vector = EncodeWith(source, quality, forceScalar: false);
            Assert.True(scalar.AsSpan().SequenceEqual(vector), $"Quality {quality}: the encoded streams differ.");
        }
    }

    // ----- VP8L transforms -----

    [Fact]
    public void GreenTransformsMatchScalar()
    {
        var random = new Random(61);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            uint[] pixels = RandomArgb(random, random.Next(1, 300));
            int count = random.Next(pixels.Length + 1);

            uint[] expected = (uint[])pixels.Clone();
            uint[] actual = (uint[])pixels.Clone();
            Vp8LDecoder.AddGreenToBlueAndRed(expected, count, vector: false);
            Vp8LDecoder.AddGreenToBlueAndRed(actual, count, vector: true);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"AddGreen iteration {iteration} differs.");

            expected = (uint[])pixels.Clone();
            actual = (uint[])pixels.Clone();
            Vp8LEncoder.SubtractGreen(expected, vector: false);
            Vp8LEncoder.SubtractGreen(actual, vector: true);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"SubtractGreen iteration {iteration} differs.");
        }
    }

    [Fact]
    public void CrossColorTransformsMatchScalar()
    {
        var random = new Random(62);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int width = random.Next(1, 150);
            int height = random.Next(1, 12);
            int bits = random.Next(2, 10);
            int tiles = ((width + (1 << bits) - 1) >> bits) * ((height + (1 << bits) - 1) >> bits);
            uint[] multipliers = RandomArgb(random, tiles);
            uint[] pixels = RandomArgb(random, width * height);

            uint[] expected = (uint[])pixels.Clone();
            uint[] actual = (uint[])pixels.Clone();
            Vp8LDecoder.CrossColorInverse(expected, width, height, bits, multipliers, vector: false);
            Vp8LDecoder.CrossColorInverse(actual, width, height, bits, multipliers, vector: true);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Inverse cross colour {width}x{height} bits {bits} differs.");

            expected = (uint[])pixels.Clone();
            actual = (uint[])pixels.Clone();
            Vp8LEncoder.ApplyCrossColor(expected, width, height, bits, multipliers, vector: false);
            Vp8LEncoder.ApplyCrossColor(actual, width, height, bits, multipliers, vector: true);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Forward cross colour {width}x{height} bits {bits} differs.");
        }
    }

    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    public void InversePredictorMatchesScalar(int seed, bool onlyTopRowModes)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int width = random.Next(1, 120);
            int height = random.Next(1, 12);
            int bits = random.Next(2, 6);
            int tiles = ((width + (1 << bits) - 1) >> bits) * ((height + (1 << bits) - 1) >> bits);
            var modes = new uint[tiles];
            int[] topRowModes = { 0, 2, 3, 4, 8, 9 };
            for (int i = 0; i < tiles; i++)
            {
                int mode = onlyTopRowModes ? topRowModes[random.Next(topRowModes.Length)] : random.Next(16);
                modes[i] = (uint)random.Next() & 0xffff00ffu | ((uint)mode << 8);
            }

            uint[] pixels = RandomArgb(random, width * height);
            uint[] expected = (uint[])pixels.Clone();
            uint[] actual = (uint[])pixels.Clone();
            Vp8LDecoder.PredictorInverse(expected, width, height, bits, modes, vector: false);
            Vp8LDecoder.PredictorInverse(actual, width, height, bits, modes, vector: true);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Inverse predictor {width}x{height} bits {bits} differs.");
        }
    }

    [Fact]
    public void LosslessRoundTripIsIdenticalWithAndWithoutVectorKernels()
    {
        using Image<Rgba32> source = CreateSyntheticImage(157, 83);
        byte[] scalar = EncodeLosslessWith(source, forceScalar: true);
        byte[] vector = EncodeLosslessWith(source, forceScalar: false);
        Assert.True(scalar.AsSpan().SequenceEqual(vector), "The lossless streams differ.");

        Rgba32[] decodedScalar = DecodeWith(scalar, forceScalar: true);
        Rgba32[] decodedVector = DecodeWith(scalar, forceScalar: false);
        Assert.True(decodedScalar.AsSpan().SequenceEqual(decodedVector), "The lossless decodes differ.");
        var original = new Rgba32[source.Width * source.Height];
        source.CopyPixelDataTo(original);
        Assert.True(original.AsSpan().SequenceEqual(decodedVector), "Lossless is not lossless.");
    }

    // ----- Whole-decoder check -----

    [Fact]
    public void LossyDecodeIsIdenticalWithAndWithoutVectorKernels()
    {
        // A synthetic image with gradients, edges and noise, so every predictor, transform and filter strength runs.
        using var source = new Image<Rgba32>(203, 117);
        var random = new Random(99);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int noise = random.Next(-12, 13);
                byte r = (byte)Math.Clamp((x * 255 / source.Width) + noise, 0, 255);
                byte g = (byte)Math.Clamp(((x / 16) + (y / 16)) % 2 == 0 ? 40 + noise : 210 + noise, 0, 255);
                byte b = (byte)Math.Clamp((y * 255 / source.Height) - noise, 0, 255);
                source[x, y] = new Rgba32(r, g, b, 255);
            }
        }

        foreach (int quality in new[] { 10, 50, 90 })
        {
            using var stream = new MemoryStream();
            source.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = quality });
            byte[] encoded = stream.ToArray();

            Rgba32[] scalar = DecodeWith(encoded, forceScalar: true);
            Rgba32[] vector = DecodeWith(encoded, forceScalar: false);
            Assert.Equal(scalar.Length, vector.Length);
            for (int i = 0; i < scalar.Length; i++)
            {
                if (!scalar[i].Equals(vector[i]))
                {
                    Assert.Fail($"Quality {quality}: pixel {i} differs, scalar {scalar[i]} vs vector {vector[i]}.");
                }
            }
        }
    }

    // ----- Helpers -----

    private static Image<Rgba32> CreateSyntheticImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        var random = new Random(width * height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int noise = random.Next(-20, 21);
                byte r = (byte)Math.Clamp((x * 255 / width) + noise, 0, 255);
                byte g = (byte)Math.Clamp(((x / 8) + (y / 8)) % 2 == 0 ? 20 + noise : 230 + noise, 0, 255);
                byte b = (byte)Math.Clamp(((x * y) % 256) - noise, 0, 255);
                image[x, y] = new Rgba32(r, g, b, 255);
            }
        }

        return image;
    }

    private static byte[] EncodeWith(Image<Rgba32> image, int quality, bool forceScalar)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            using var stream = new MemoryStream();
            image.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = quality });
            return stream.ToArray();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    private static byte[] EncodeLosslessWith(Image<Rgba32> image, bool forceScalar)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            using var stream = new MemoryStream();
            image.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
            return stream.ToArray();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    private static uint[] RandomArgb(Random random, int count)
    {
        var pixels = new uint[count];
        for (int i = 0; i < count; i++)
        {
            pixels[i] = random.Next(6) == 0 ? (random.Next(2) == 0 ? 0u : 0xffffffffu) : (uint)random.NextInt64(0, 1L << 32);
        }

        return pixels;
    }

    /// <summary>
    /// Quantiser tables either derived the way a quantisation matrix derives them (a step, its 17-bit reciprocal, a
    /// byte bias and the resulting zero threshold), or arbitrary values in the ranges the arithmetic must survive.
    /// </summary>
    private static void FillQuantizerTables(Random random, bool arbitrary, ushort[] q, int[] iq, int[] bias, int[] zeroThreshold, int[] sharpen)
    {
        for (int i = 0; i < 16; i++)
        {
            if (arbitrary)
            {
                q[i] = (ushort)random.Next(1, 65536);
                iq[i] = random.Next(1, 1 << 15);
                bias[i] = random.Next(0, 1 << 17);
                zeroThreshold[i] = random.Next(-8, 40_000);
                sharpen[i] = random.Next(0, 300);
            }
            else
            {
                int step = i == 0 ? random.Next(4, 315) : random.Next(4, 285);
                q[i] = (ushort)step;
                iq[i] = (1 << 17) / step;
                bias[i] = random.Next(96, 128) << 9;
                zeroThreshold[i] = ((1 << 17) - 1 - bias[i]) / iq[i];
                sharpen[i] = random.Next(2) == 0 ? 0 : (random.Next(0, 25) * step) >> 11;
            }
        }
    }

    private static Rgba32[] DecodeWith(byte[] encoded, bool forceScalar)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(encoded);
            var pixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixels);
            return pixels;
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    /// <summary>Mostly small coefficients, sometimes the extremes, so both typical and overflowing values are covered.</summary>
    private static short RandomCoefficient(Random random, int magnitude)
    {
        return random.Next(8) switch
        {
            0 => 0,
            1 => short.MinValue,
            2 => short.MaxValue,
            _ => (short)Math.Clamp(random.Next(-magnitude, magnitude + 1), short.MinValue, short.MaxValue),
        };
    }

    /// <summary>A work buffer of 6 rows with random prediction samples, wide enough for a 4x4 block at any test offset.</summary>
    private static byte[] RandomBlockBuffer(Random random)
    {
        var buffer = new byte[Bps * 6];
        random.NextBytes(buffer);
        return buffer;
    }

    /// <summary>Mostly the decoder's real range (the strongest filter level gives 193), sometimes anything a byte can hold.</summary>
    private static int RandomThreshold(Random random) => random.Next(4) == 0 ? random.Next(256) : random.Next(194);

    /// <summary>
    /// A plane of smooth regions with small noise and occasional steps: random bytes alone would almost never pass
    /// the filters' activity tests, so the filtering branches would go untested.
    /// </summary>
    private static byte[] RandomFilterPlane(Random random, int stride, int rows)
    {
        var plane = new byte[stride * rows];
        int baseValue = random.Next(256);
        int noise = random.Next(1, 24);
        for (int y = 0; y < rows; y++)
        {
            if (random.Next(6) == 0)
            {
                baseValue = random.Next(256);
            }

            for (int x = 0; x < stride; x++)
            {
                int step = random.Next(40) == 0 ? random.Next(-120, 121) : 0;
                plane[(y * stride) + x] = (byte)Math.Clamp(baseValue + step + random.Next(-noise, noise + 1), 0, 255);
            }
        }

        return plane;
    }

    private static void FillPlane(Random random, byte[] plane)
    {
        random.NextBytes(plane);
        int mode = random.Next(4);
        for (int i = 0; i < plane.Length && mode != 0; i++)
        {
            if (random.Next(4) == 0)
            {
                plane[i] = mode == 1 ? (byte)0 : mode == 2 ? (byte)255 : (byte)(random.Next(2) * 255);
            }
        }
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"{what}: byte {i} differs, scalar {expected[i]} vs vector {actual[i]}.");
            }
        }
    }
}
