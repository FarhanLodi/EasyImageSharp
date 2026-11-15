using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Coverage for the JPEG encoder's tuning options and fast paths: the vectorised forward DCT and quantiser must
/// be bit-identical to the scalar reference, the quantisation presets and independent chroma quality must write
/// exactly the tables they promise, and trellis quantisation must shrink files while still decoding cleanly.
/// </summary>
public class JpegEncoderTuningTests
{
    private readonly ITestOutputHelper output;

    public JpegEncoderTuningTests(ITestOutputHelper output) => this.output = output;

    public static TheoryData<JpegQuantizationPreset> AllPresets()
    {
        var data = new TheoryData<JpegQuantizationPreset>();
        foreach (JpegQuantizationPreset preset in Enum.GetValues<JpegQuantizationPreset>())
        {
            data.Add(preset);
        }

        return data;
    }

    public static TheoryData<JpegEncodingColor> AllColorTypes()
    {
        var data = new TheoryData<JpegEncodingColor>();
        foreach (JpegEncodingColor color in Enum.GetValues<JpegEncodingColor>())
        {
            data.Add(color);
        }

        return data;
    }

    // =================================================================================================
    // (1) Vector paths are bit-identical to the scalar reference
    // =================================================================================================

    [Fact]
    public void ForwardDct_EveryVectorPathMatchesTheScalarReferenceBitForBit()
    {
        var paths = new List<JpegForwardDct.Path> { JpegForwardDct.Path.Scalar };
        if (JpegForwardDct.IsVector128Supported)
        {
            paths.Add(JpegForwardDct.Path.Vector128);
        }

        if (JpegForwardDct.IsVector256Supported)
        {
            paths.Add(JpegForwardDct.Path.Vector256);
        }

        this.output.WriteLine($"paths compared: {string.Join(", ", paths)}");
        var random = new Random(1234);
        var reference = new float[64];
        var candidate = new float[64];
        for (int trial = 0; trial < 4000; trial++)
        {
            FillBlock(reference, random, trial);
            float[] input = (float[])reference.Clone();
            JpegForwardDct.Transform(reference, JpegForwardDct.Path.Scalar);
            foreach (JpegForwardDct.Path path in paths)
            {
                input.CopyTo(candidate, 0);
                JpegForwardDct.Transform(candidate, path);
                for (int i = 0; i < 64; i++)
                {
                    if (BitConverter.SingleToInt32Bits(candidate[i]) != BitConverter.SingleToInt32Bits(reference[i]))
                    {
                        Assert.Fail($"{path} trial {trial} coefficient {i}: {candidate[i]:R} vs scalar {reference[i]:R}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void ForwardDct_UnsupportedExplicitPathsAreRejected()
    {
        var block = new float[64];
        if (!JpegForwardDct.IsVector256Supported)
        {
            Assert.Throws<NotSupportedException>(() => JpegForwardDct.Transform(block, JpegForwardDct.Path.Vector256));
        }

        if (!JpegForwardDct.IsVector128Supported)
        {
            Assert.Throws<NotSupportedException>(() => JpegForwardDct.Transform(block, JpegForwardDct.Path.Vector128));
        }

        Assert.Throws<ArgumentException>(() => JpegForwardDct.Transform(new float[63], JpegForwardDct.Path.Scalar));
    }

    [Fact]
    public void Quantize_EveryPathProducesTheSameCoefficients()
    {
        var random = new Random(99);
        var block = new float[64];
        var scalar = new short[64];
        var v128 = new short[64];
        var v256 = new short[64];
        foreach (int quality in new[] { 1, 10, 50, 75, 90, 100 })
        {
            float[] reciprocals = JpegForwardDct.CreateReciprocalTable(
                JpegQuantizationTables.Scale(JpegQuantizationTables.GetBaseTable(JpegQuantizationPreset.AnnexK, 0), quality));
            for (int trial = 0; trial < 500; trial++)
            {
                FillBlock(block, random, trial);
                JpegForwardDct.Transform(block, JpegForwardDct.Path.Scalar);
                JpegForwardDct.Quantize(block, reciprocals, scalar, JpegForwardDct.Path.Scalar);
                JpegForwardDct.Quantize(block, reciprocals, v128, JpegForwardDct.Path.Vector128);
                JpegForwardDct.Quantize(block, reciprocals, v256, JpegForwardDct.Path.Vector256);
                Assert.Equal(scalar, v128);
                Assert.Equal(scalar, v256);
            }
        }
    }

    [Fact]
    public void LoadBlock_EveryPathWidensIdentically()
    {
        var random = new Random(7);
        const int Stride = 37;
        var samples = new short[(8 * Stride) + 8];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)random.Next(-128 * JpegColorConverter.SampleScale, 128 * JpegColorConverter.SampleScale);
        }

        const float Scale = 1f / JpegColorConverter.SampleScale;
        var scalar = new float[64];
        var v128 = new float[64];
        var v256 = new float[64];
        JpegForwardDct.LoadBlock(ref samples[3], Stride, scalar, Scale, JpegForwardDct.Path.Scalar);
        JpegForwardDct.LoadBlock(ref samples[3], Stride, v128, Scale, JpegForwardDct.Path.Vector128);
        JpegForwardDct.LoadBlock(ref samples[3], Stride, v256, Scale, JpegForwardDct.Path.Vector256);
        Assert.Equal(scalar, v128);
        Assert.Equal(scalar, v256);
        Assert.Equal(samples[3 + Stride + 2] * Scale, scalar[10]);
    }

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void Encoder_SimdAndScalarFallbackWriteIdenticalBytes(JpegEncodingColor color)
    {
        using Image<Rgb24> source = TestPhotographs.Create(203, 117, seed: 5);
        foreach (JpegEncoder encoder in new[]
        {
            new JpegEncoder { ColorType = color },
            new JpegEncoder { ColorType = color, Quality = 40, Progressive = true },
            new JpegEncoder { ColorType = color, Quality = 75, TrellisQuantization = true, OptimizeHuffmanTables = true },
        })
        {
            byte[] vectorized = RunWithScalarFallback(false, () => Encode(source, encoder));
            byte[] scalar = RunWithScalarFallback(true, () => Encode(source, encoder));
            Assert.Equal(scalar, vectorized);
        }
    }

    // =================================================================================================
    // (2) Quantisation presets and chroma quality
    // =================================================================================================

    [Fact]
    public void Defaults_AreTheAnnexKTablesAtOneQualityWithoutTrellis()
    {
        var encoder = new JpegEncoder();
        Assert.Equal(JpegQuantizationPreset.AnnexK, encoder.QuantizationPreset);
        Assert.Null(encoder.ChromaQuality);
        Assert.False(encoder.TrellisQuantization);

        // Spelling the defaults out changes nothing.
        using Image<Rgb24> source = TestPhotographs.Create(64, 48, seed: 1);
        Assert.Equal(
            Encode(source, encoder),
            Encode(source, new JpegEncoder { QuantizationPreset = JpegQuantizationPreset.AnnexK, ChromaQuality = 90, TrellisQuantization = false }));
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Presets_WriteTheirBaseTablesAtQuality50(JpegQuantizationPreset preset)
    {
        using Image<Rgb24> source = TestPhotographs.Create(32, 32, seed: 2);
        byte[] data = Encode(source, new JpegEncoder { Quality = 50, QuantizationPreset = preset });
        Dictionary<int, byte[]> tables = QuantTables(data);
        Assert.Equal(new[] { 0, 1 }, tables.Keys.Order().ToArray());
        for (int slot = 0; slot < 2; slot++)
        {
            ReadOnlySpan<ushort> expected = JpegQuantizationTables.GetBaseTable(preset, slot);
            for (int k = 0; k < 64; k++)
            {
                // DQT stores zigzag order; values above 255 are clamped to the 8-bit baseline range.
                Assert.Equal(Math.Min((int)expected[JpegTables.ZigZag[k]], 255), tables[slot][k]);
            }
        }
    }

    [Fact]
    public void Presets_FollowTheIjgQualityCurve()
    {
        // Spot values of the Robidoux table at quality 75 (scale 50) and 20 (scale 250): the base 16 becomes 8
        // and 40, the base 418 becomes 209 and the clamp of 255.
        using Image<Rgb24> source = TestPhotographs.Create(16, 16, seed: 3);
        byte[] q75 = QuantTables(Encode(source, new JpegEncoder { Quality = 75, QuantizationPreset = JpegQuantizationPreset.Robidoux }))[0];
        byte[] q20 = QuantTables(Encode(source, new JpegEncoder { Quality = 20, QuantizationPreset = JpegQuantizationPreset.Robidoux }))[0];
        Assert.Equal(8, q75[0]);
        Assert.Equal(209, q75[63]);
        Assert.Equal(40, q20[0]);
        Assert.Equal(255, q20[63]);
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Presets_DecodeAndOrderedBySizeWithQuality(JpegQuantizationPreset preset)
    {
        using Image<Rgb24> source = TestPhotographs.Create(160, 120, seed: 4);
        byte[] low = Encode(source, new JpegEncoder { Quality = 40, QuantizationPreset = preset });
        byte[] high = Encode(source, new JpegEncoder { Quality = 90, QuantizationPreset = preset });
        using Image<Rgb24> decodedLow = Image.Load<Rgb24>(low);
        using Image<Rgb24> decodedHigh = Image.Load<Rgb24>(high);
        double psnrLow = TestPhotographs.Psnr(source, decodedLow);
        double psnrHigh = TestPhotographs.Psnr(source, decodedHigh);
        this.output.WriteLine($"{preset}: q40 {low.Length} bytes {psnrLow:F2} dB, q90 {high.Length} bytes {psnrHigh:F2} dB");
        Assert.True(high.Length > low.Length);
        Assert.True(psnrHigh > psnrLow);
        Assert.True(psnrLow > 25, $"{preset} at q40 decodes at only {psnrLow:F2} dB.");
    }

    [Fact]
    public void ChromaQuality_ScalesOnlyTheChrominanceTable()
    {
        using Image<Rgb24> source = TestPhotographs.Create(48, 48, seed: 6);
        Dictionary<int, byte[]> same = QuantTables(Encode(source, new JpegEncoder { Quality = 85 }));
        Dictionary<int, byte[]> split = QuantTables(Encode(source, new JpegEncoder { Quality = 85, ChromaQuality = 50 }));
        Assert.Equal(same[0], split[0]);
        Assert.NotEqual(same[1], split[1]);

        // Chroma at quality 50 is exactly the Annex K chrominance table.
        for (int k = 0; k < 64; k++)
        {
            Assert.Equal((int)JpegTables.StdChrominanceQuant[JpegTables.ZigZag[k]], split[1][k]);
        }
    }

    [Fact]
    public void ChromaQuality_IsClampedAndShrinksTheFile()
    {
        Assert.Equal(1, new JpegEncoder { ChromaQuality = -5 }.ChromaQuality);
        Assert.Equal(100, new JpegEncoder { ChromaQuality = 500 }.ChromaQuality);
        Assert.Null(new JpegEncoder { ChromaQuality = null }.ChromaQuality);

        using Image<Rgb24> source = TestPhotographs.Create(200, 150, seed: 7);
        byte[] full = Encode(source, new JpegEncoder { Quality = 90 });
        byte[] reduced = Encode(source, new JpegEncoder { Quality = 90, ChromaQuality = 60 });
        this.output.WriteLine($"chroma q90 {full.Length} bytes, chroma q60 {reduced.Length} bytes");
        Assert.True(reduced.Length < full.Length);
    }

    [Theory]
    [InlineData(JpegEncodingColor.Rgb)]
    [InlineData(JpegEncodingColor.Cmyk)]
    public void ChromaQuality_IsIgnoredByModelsWithoutAChromaTable(JpegEncodingColor color)
    {
        using Image<Rgb24> source = TestPhotographs.Create(40, 24, seed: 8);
        Assert.Equal(
            Encode(source, new JpegEncoder { ColorType = color }),
            Encode(source, new JpegEncoder { ColorType = color, ChromaQuality = 20 }));
    }

    [Fact]
    public void UnknownPresetIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { QuantizationPreset = (JpegQuantizationPreset)99 });

    // =================================================================================================
    // (3) Trellis quantisation
    // =================================================================================================

    [Theory]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 75)]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 90)]
    [InlineData(JpegEncodingColor.Luminance, 85)]
    public void Trellis_ShrinksTheFileAndStaysClose(JpegEncodingColor color, int quality)
    {
        using Image<Rgb24> photograph = TestPhotographs.Create(320, 240, seed: 9);

        // A luminance-only file is compared with the grey version of the photograph it was made from.
        using Image<L8> grey = photograph.CloneAs<L8>();
        using Image<Rgb24> source = color == JpegEncodingColor.Luminance ? grey.CloneAs<Rgb24>() : photograph.Clone();
        byte[] plain = Encode(source, new JpegEncoder { ColorType = color, Quality = quality });
        byte[] trellis = Encode(source, new JpegEncoder { ColorType = color, Quality = quality, TrellisQuantization = true });
        using Image<Rgb24> decodedPlain = Image.Load<Rgb24>(plain);
        using Image<Rgb24> decodedTrellis = Image.Load<Rgb24>(trellis);
        double psnrPlain = TestPhotographs.Psnr(source, decodedPlain);
        double psnrTrellis = TestPhotographs.Psnr(source, decodedTrellis);
        this.output.WriteLine(
            $"{color} q{quality}: plain {plain.Length} bytes {psnrPlain:F2} dB, trellis {trellis.Length} bytes {psnrTrellis:F2} dB");

        // Measured: 3 to 6 % smaller for 0.07 to 0.25 dB with the standard tables.
        Assert.True(trellis.Length < plain.Length * 0.98, $"Trellis saved only {100.0 * (plain.Length - trellis.Length) / plain.Length:F1} %.");
        Assert.True(psnrTrellis > psnrPlain - 0.75, $"Trellis lost {psnrPlain - psnrTrellis:F2} dB.");
    }

    [Theory]
    [InlineData(JpegEncodingColor.YCbCrRatio420)]
    [InlineData(JpegEncodingColor.Luminance)]
    [InlineData(JpegEncodingColor.Ycck)]
    public void Trellis_ProgressiveDecodesToExactlyTheBaselinePixels(JpegEncodingColor color)
    {
        // The trellis decides the coefficients; the scan structure only decides how they are coded. Progressive
        // output optimises its tables by default, so it runs the same two trellis passes as baseline output with
        // optimised tables.
        using Image<Rgb24> source = TestPhotographs.Create(97, 61, seed: 10);
        byte[] baseline = Encode(source, new JpegEncoder { ColorType = color, TrellisQuantization = true, OptimizeHuffmanTables = true });
        byte[] progressive = Encode(source, new JpegEncoder { ColorType = color, TrellisQuantization = true, Progressive = true });
        Assert.Equal(0xC2, FrameMarker(progressive));
        using Image<Rgba32> a = Image.Load<Rgba32>(baseline);
        using Image<Rgba32> b = Image.Load<Rgba32>(progressive);
        AssertIdentical(a, b);
    }

    [Fact]
    public void Trellis_SecondPassWithFittedTablesChangesTheCoefficients()
    {
        // With optimised tables the second pass prices choices with the image's own codes, so it decides
        // differently from the single pass priced with the standard tables. Both decode cleanly.
        using Image<Rgb24> source = TestPhotographs.Create(240, 160, seed: 13);
        byte[] singlePass = Encode(source, new JpegEncoder { TrellisQuantization = true, Quality = 80 });
        byte[] twoPass = Encode(source, new JpegEncoder { TrellisQuantization = true, OptimizeHuffmanTables = true, Quality = 80 });
        byte[] plainOptimised = Encode(source, new JpegEncoder { OptimizeHuffmanTables = true, Quality = 80 });
        using Image<Rgba32> a = Image.Load<Rgba32>(singlePass);
        using Image<Rgba32> b = Image.Load<Rgba32>(twoPass);
        Assert.Throws<Xunit.Sdk.FailException>(() => AssertIdentical(a, b));
        this.output.WriteLine($"plain + optimised {plainOptimised.Length} bytes, trellis + optimised {twoPass.Length} bytes");
        Assert.True(twoPass.Length < plainOptimised.Length);
    }

    [Fact]
    public void Trellis_OutputIsDeterministicAcrossThreadCounts()
    {
        using Image<Rgb24> source = TestPhotographs.Create(256, 200, seed: 11);
        var encoder = new JpegEncoder { TrellisQuantization = true, ColorType = JpegEncodingColor.YCbCrRatio420 };
        int original = Configuration.Default.MaxDegreeOfParallelism;
        byte[] parallel = Encode(source, encoder);
        byte[] serial;
        try
        {
            Configuration.Default.MaxDegreeOfParallelism = 1;
            serial = Encode(source, encoder);
        }
        finally
        {
            Configuration.Default.MaxDegreeOfParallelism = original;
        }

        Assert.Equal(parallel, serial);
    }

    [Fact]
    public void TrellisQuantizer_NeverIncreasesAMagnitudeAndRoundsDcAsUsual()
    {
        ushort[] quant = JpegQuantizationTables.Scale(JpegQuantizationTables.GetBaseTable(JpegQuantizationPreset.AnnexK, 0), 80);
        float[] reciprocals = JpegForwardDct.CreateReciprocalTable(quant);
        var trellis = new JpegTrellisQuantizer(quant, reciprocals, JpegEncoderCore.StandardAcTable(0));
        var random = new Random(12);
        var block = new float[64];
        var plain = new short[64];
        var optimised = new short[64];
        int zeroedTotal = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            FillBlock(block, random, trial);
            JpegForwardDct.Transform(block, JpegForwardDct.Path.Scalar);
            JpegForwardDct.Quantize(block, reciprocals, plain, JpegForwardDct.Path.Scalar);
            trellis.Quantize(block, optimised);
            Assert.Equal(plain[0], optimised[0]);
            for (int k = 1; k < 64; k++)
            {
                Assert.True(Math.Abs(optimised[k]) <= Math.Abs(plain[k]), $"trial {trial} k {k}: {optimised[k]} vs rounded {plain[k]}.");
                Assert.True(optimised[k] == 0 || Math.Sign(optimised[k]) == Math.Sign(plain[k]));
                if (plain[k] != 0 && optimised[k] == 0)
                {
                    zeroedTotal++;
                }
            }
        }

        Assert.True(zeroedTotal > 0, "The trellis never zeroed a coefficient.");
    }

    [Fact]
    public void TrellisQuantizer_KeepsAFlatBlockFlat()
    {
        ushort[] quant = JpegQuantizationTables.Scale(JpegQuantizationTables.GetBaseTable(JpegQuantizationPreset.AnnexK, 0), 90);
        float[] reciprocals = JpegForwardDct.CreateReciprocalTable(quant);
        var trellis = new JpegTrellisQuantizer(quant, reciprocals, JpegEncoderCore.StandardAcTable(0));
        var block = new float[64];
        block.AsSpan().Fill(-37.25f);
        JpegForwardDct.Transform(block);
        var result = new short[64];
        trellis.Quantize(block, result);
        var expected = new short[64];
        JpegForwardDct.Quantize(block, reciprocals, expected);
        Assert.Equal(expected, result);
        Assert.All(result[1..], value => Assert.Equal(0, value));
    }

    // =================================================================================================
    // Helpers
    // =================================================================================================

    /// <summary>Level-shifted 8-bit-range samples: noise, gradients, edges and extremes, chosen by trial number.</summary>
    private static void FillBlock(float[] block, Random random, int trial)
    {
        switch (trial % 5)
        {
            case 0:
                for (int i = 0; i < 64; i++)
                {
                    block[i] = random.Next(-128, 128);
                }

                break;
            case 1:
                for (int i = 0; i < 64; i++)
                {
                    block[i] = (random.Next(-128 * 16, 128 * 16)) / 16f;
                }

                break;
            case 2:
                int a = random.Next(-128, 128);
                int b = random.Next(-128, 128);
                for (int i = 0; i < 64; i++)
                {
                    block[i] = ((i & 7) + (i >> 3)) < 7 ? a : b;
                }

                break;
            case 3:
                for (int i = 0; i < 64; i++)
                {
                    block[i] = (i & 1) == 0 ? -128 : 127;
                }

                break;
            default:
                float baseValue = random.Next(-100, 100);
                for (int i = 0; i < 64; i++)
                {
                    block[i] = baseValue + ((i & 7) * 2.5f) - ((i >> 3) * 1.25f) + (random.Next(-3, 4) / 4f);
                }

                break;
        }
    }

    private static T RunWithScalarFallback<T>(bool forceScalar, Func<T> body)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            return body();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    /// <summary>The DQT tables of a JPEG by slot, 64 entries each in zigzag order.</summary>
    private static Dictionary<int, byte[]> QuantTables(byte[] data)
    {
        var tables = new Dictionary<int, byte[]>();
        foreach ((byte marker, byte[] payload) in JpegSegments(data))
        {
            if (marker != 0xDB)
            {
                continue;
            }

            for (int p = 0; p < payload.Length; p += 65)
            {
                Assert.Equal(0, payload[p] >> 4); // 8-bit precision.
                tables[payload[p] & 15] = payload[(p + 1)..(p + 65)];
            }
        }

        return tables;
    }

    private static byte FrameMarker(byte[] data)
        => JpegSegments(data).First(s => s.Marker is 0xC0 or 0xC1 or 0xC2).Marker;

    /// <summary>The marker segments up to the first SOS.</summary>
    private static IEnumerable<(byte Marker, byte[] Payload)> JpegSegments(byte[] data)
    {
        int position = 2;
        while (position + 4 <= data.Length && data[position] == 0xFF)
        {
            byte marker = data[position + 1];
            int length = (data[position + 2] << 8) | data[position + 3];
            yield return (marker, data[(position + 4)..(position + 2 + length)]);
            if (marker == 0xDA)
            {
                yield break;
            }

            position += 2 + length;
        }
    }

    private static void AssertIdentical(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgba32> a = expected.Frames.RootFrame.GetRowSpan(y);
            Span<Rgba32> b = actual.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < expected.Width; x++)
            {
                if (!a[x].Equals(b[x]))
                {
                    Assert.Fail($"First difference at ({x},{y}): expected {a[x]}, got {b[x]}.");
                }
            }
        }
    }
}

/// <summary>Deterministic photographic stand-ins shared by the JPEG encoder tuning and streaming tests.</summary>
internal static class TestPhotographs
{
    /// <summary>
    /// Smooth shading, mid-frequency texture, a couple of hard edges and seeded sensor-like grain, with colour
    /// varying over tens of pixels: enough structure that quantisation choices visibly matter.
    /// </summary>
    public static Image<Rgb24> Create(int width, int height, int seed)
    {
        var image = new Image<Rgb24>(width, height);
        var random = new Random(seed);
        double phase = random.NextDouble() * Math.PI;
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width;
                double v = (double)y / height;
                double luma = 0.5
                    + (0.2 * Math.Sin((u * 5.0) + (v * 3.0) + phase))
                    + (0.08 * Math.Sin((x * 0.37) - (y * 0.23)))
                    + (0.04 * Math.Sin((x * 1.7) + (y * 1.1)))
                    + (x > width / 3 && y > height / 2 ? 0.12 : 0)
                    + ((random.NextDouble() - 0.5) * 0.06);
                double cb = 40 * Math.Sin((u * 7.0) - (v * 4.0) + phase);
                double cr = 40 * Math.Cos((u * 3.0) + (v * 6.0));
                double l = luma * 255;
                row[x] = new Rgb24(
                    ClampByte(l + (1.402 * cr)),
                    ClampByte(l - (0.344136 * cb) - (0.714136 * cr)),
                    ClampByte(l + (1.772 * cb)));
            }
        }

        return image;
    }

    public static double Psnr(Image<Rgb24> expected, Image<Rgb24> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        double squaredError = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgb24> a = expected.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> b = actual.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < expected.Width; x++)
            {
                int dr = a[x].R - b[x].R;
                int dg = a[x].G - b[x].G;
                int db = a[x].B - b[x].B;
                squaredError += (dr * dr) + (dg * dg) + (db * db);
            }
        }

        double mse = squaredError / ((double)expected.Width * expected.Height * 3);
        return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)(value + 0.5), 0, 255);
}
