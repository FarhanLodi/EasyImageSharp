using System.Diagnostics;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Encoder coverage for <see cref="JpegEncoder"/>: quantisation quality, chroma subsampling, progressive
/// scans, restart intervals, optimised Huffman tables and the Adobe colour models. Output is checked by
/// decoding it again with this library's decoder (which the fixture tests separately pin against libjpeg) and
/// by parsing the marker structure directly.
/// </summary>
public class JpegEncoderTests
{
    /// <summary>
    /// PSNR (dB) of the default encode path measured on every JPEG fixture before the encoder was rewritten,
    /// with the previous implementation: 4:4:4 YCbCr, quality 90, standard Huffman tables. The rewrite must not
    /// decode worse than this. The tolerance absorbs the handful of coefficients that land on the other side of
    /// a rounding boundary because the fast AA&amp;N transform and the old direct-matrix one differ in the last
    /// bits; it is far below any visible difference, and the mean across the corpus must still not regress.
    /// </summary>
    private static readonly Dictionary<string, double> ReferencePsnr = new()
    {
        ["baseline_420"] = 39.0153,
        ["baseline_420_odd"] = 38.1058,
        ["baseline_422"] = 38.5274,
        ["baseline_444"] = 38.2405,
        ["baseline_gray"] = 45.0640,
        ["cmyk_adobe"] = 35.9045,
        ["progressive_420"] = 39.0153,
        ["progressive_420_odd"] = 38.1058,
        ["progressive_422"] = 38.5274,
        ["progressive_444"] = 38.2405,
        ["progressive_gray"] = 45.0640,
        ["restart_baseline_420"] = 39.0153,
        ["restart_progressive_420"] = 39.0153,
        ["ycck_adobe"] = 38.1907,
    };

    /// <summary>Mean of <see cref="ReferencePsnr"/>; the rewrite must be at least this good on average.</summary>
    private const double ReferenceMeanPsnr = 39.2880;

    private const double PsnrTolerance = 0.05;

    private readonly ITestOutputHelper output;

    public JpegEncoderTests(ITestOutputHelper output) => this.output = output;

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in ReferencePsnr.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            data.Add(name);
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
    // (1) The default path must not decode worse than the encoder this one replaced
    // =================================================================================================

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void DefaultEncode_IsNoWorseThanTheReferenceMeasurement(string name)
    {
        using Image<Rgb24> source = Image.Load<Rgb24>(FixturePath.Get($"jpeg/{name}.jpg"));
        using Image<Rgb24> decoded = RoundTrip(source, new JpegEncoder());

        double psnr = Psnr(source, decoded);
        double reference = ReferencePsnr[name];
        this.output.WriteLine($"{name}: {psnr:F4} dB (reference {reference:F4} dB, delta {psnr - reference:+0.0000;-0.0000})");
        Assert.True(
            psnr >= reference - PsnrTolerance,
            $"{name}: default encode gives {psnr:F4} dB, below the {reference:F4} dB the previous encoder achieved.");
    }

    [Fact]
    public void DefaultEncode_MeanQualityAcrossTheCorpusDoesNotRegress()
    {
        double total = 0;
        foreach (string name in ReferencePsnr.Keys)
        {
            using Image<Rgb24> source = Image.Load<Rgb24>(FixturePath.Get($"jpeg/{name}.jpg"));
            using Image<Rgb24> decoded = RoundTrip(source, new JpegEncoder());
            total += Psnr(source, decoded);
        }

        double mean = total / ReferencePsnr.Count;
        this.output.WriteLine($"mean PSNR {mean:F4} dB (reference {ReferenceMeanPsnr:F4} dB)");
        Assert.True(mean >= ReferenceMeanPsnr, $"Mean PSNR {mean:F4} dB is below the reference {ReferenceMeanPsnr:F4} dB.");
    }

    [Fact]
    public void Quality_TradesSizeForFidelityMonotonically()
    {
        using Image<Rgb24> source = Photograph(256, 192);
        int previousSize = 0;
        double previousPsnr = 0;
        foreach (int quality in new[] { 20, 40, 60, 80, 95 })
        {
            byte[] data = Encode(source, new JpegEncoder { Quality = quality });
            using Image<Rgb24> decoded = Decode(data);
            double psnr = Psnr(source, decoded);
            this.output.WriteLine($"q{quality}: {data.Length} bytes, {psnr:F2} dB");
            Assert.True(data.Length > previousSize, $"q{quality} is not larger than the previous quality step.");
            Assert.True(psnr > previousPsnr, $"q{quality} is not sharper than the previous quality step.");
            previousSize = data.Length;
            previousPsnr = psnr;
        }
    }

    // =================================================================================================
    // (2) Chroma subsampling
    // =================================================================================================

    [Fact]
    public void Ratio420_IsMuchSmallerThan444_AtNearlyTheSameQuality()
    {
        using Image<Rgb24> source = Photograph(512, 384);
        byte[] full = Encode(source, new JpegEncoder { Quality = 50, ColorType = JpegEncodingColor.YCbCrRatio444 });
        byte[] half = Encode(source, new JpegEncoder { Quality = 50, ColorType = JpegEncodingColor.YCbCrRatio420 });

        using Image<Rgb24> decodedFull = Decode(full);
        using Image<Rgb24> decodedHalf = Decode(half);
        double psnrFull = Psnr(source, decodedFull);
        double psnrHalf = Psnr(source, decodedHalf);
        double saving = 100.0 * (full.Length - half.Length) / full.Length;
        double drop = psnrFull - psnrHalf;

        this.output.WriteLine($"4:4:4 {full.Length} bytes / {psnrFull:F2} dB, 4:2:0 {half.Length} bytes / {psnrHalf:F2} dB");
        this.output.WriteLine($"saving {saving:F1} %, PSNR drop {drop:F2} dB");
        Assert.InRange(saving, 30.0, 45.0);
        Assert.True(drop < 1.5, $"4:2:0 costs {drop:F2} dB, more than the 1.5 dB budget.");
    }

    [Theory]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 0x11)]
    [InlineData(JpegEncodingColor.YCbCrRatio422, 0x21)]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 0x22)]
    [InlineData(JpegEncodingColor.YCbCrRatio411, 0x41)]
    [InlineData(JpegEncodingColor.YCbCrRatio410, 0x42)]
    public void ChromaLayout_IsWrittenAsTheExpectedSamplingFactors(JpegEncodingColor color, int lumaFactors)
    {
        using Image<Rgb24> source = Photograph(64, 48);
        byte[] data = Encode(source, new JpegEncoder { ColorType = color });
        byte[] sof = SegmentPayload(data, 0xC0);

        Assert.Equal(3, sof[5]);
        Assert.Equal(lumaFactors, sof[7]);  // Component 1 (luma) sampling factors.
        Assert.Equal(0x11, sof[10]);        // Cb is always 1x1.
        Assert.Equal(0x11, sof[13]);        // Cr is always 1x1.
    }

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void EverySubsamplingLayout_SurvivesOddDimensions(JpegEncodingColor color)
    {
        foreach ((int width, int height) in new[] { (1, 1), (3, 5), (7, 9), (17, 1), (1, 17), (31, 33), (65, 47) })
        {
            using Image<Rgb24> source = Photograph(width, height);
            using Image<Rgb24> decoded = RoundTrip(source, new JpegEncoder { ColorType = color, Quality = 95 });
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
        }
    }

    // =================================================================================================
    // (3) Progressive output must reconstruct the very same coefficients as the baseline
    // =================================================================================================

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void Progressive_DecodesToExactlyTheBaselinePixels(JpegEncodingColor color)
    {
        using Image<Rgb24> source = Photograph(96, 72);
        byte[] baseline = Encode(source, new JpegEncoder { Quality = 80, ColorType = color });
        byte[] progressive = Encode(source, new JpegEncoder { Quality = 80, ColorType = color, Progressive = true });

        Assert.Equal(0xC0, FrameMarker(baseline));
        Assert.Equal(0xC2, FrameMarker(progressive));

        using Image<Rgba32> a = Image.Load<Rgba32>(baseline);
        using Image<Rgba32> b = Image.Load<Rgba32>(progressive);
        AssertIdentical(a, b, $"{color}: progressive output decodes differently from baseline output.");
        this.output.WriteLine($"{color}: baseline {baseline.Length} bytes, progressive {progressive.Length} bytes");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(2, 4)]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(24, 24)]
    [InlineData(64, 64)]
    public void ProgressiveScans_ProducesTheRequestedNumberOfScans(int requested, int expected)
    {
        using Image<Rgb24> source = Photograph(96, 72);
        byte[] data = Encode(source, new JpegEncoder { Progressive = true, ProgressiveScans = requested });

        Assert.Equal(expected, CountMarkers(data, 0xDA));

        // However the script is split, it must still reconstruct the baseline coefficients exactly.
        using Image<Rgba32> reference = Image.Load<Rgba32>(Encode(source, new JpegEncoder()));
        using Image<Rgba32> actual = Image.Load<Rgba32>(data);
        AssertIdentical(reference, actual, $"{requested} progressive scans changed the decoded pixels.");
    }

    [Fact]
    public void Progressive_DefaultsToOptimisedTablesAndBeatsTheBaselineSize()
    {
        using Image<Rgb24> source = Photograph(256, 192);
        byte[] baseline = Encode(source, new JpegEncoder());
        byte[] progressive = Encode(source, new JpegEncoder { Progressive = true });

        Assert.True(new JpegEncoder { Progressive = true }.OptimizeHuffmanTables);
        Assert.False(new JpegEncoder().OptimizeHuffmanTables);
        this.output.WriteLine($"baseline {baseline.Length} bytes, progressive {progressive.Length} bytes");
        Assert.True(progressive.Length < baseline.Length, "Progressive output should be smaller than baseline output.");
    }

    // =================================================================================================
    // (4) Restart intervals
    // =================================================================================================

    [Theory]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 1, false)]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 5, false)]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 3, false)]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 8, false)]
    [InlineData(JpegEncodingColor.Luminance, 4, false)]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 3, true)]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 7, true)]
    public void RestartMarkers_LandOnEveryIntervalOfMcus(JpegEncodingColor color, int interval, bool progressive)
    {
        using Image<Rgb24> source = Photograph(96, 72);
        var encoder = new JpegEncoder { ColorType = color, RestartInterval = interval, Progressive = progressive };
        byte[] data = Encode(source, encoder);

        // DRI must announce the interval the encoder was asked for.
        byte[] dri = SegmentPayload(data, 0xDD);
        Assert.Equal(interval, (dri[0] << 8) | dri[1]);

        // A sequential frame codes the whole image as one interleaved scan, so the restart count follows
        // directly from the MCU grid. A progressive frame has per-component scans as well, so only the
        // ordering rule and the decoded result are checked there.
        byte[] sof = SegmentPayload(data, progressive ? (byte)0xC2 : (byte)0xC0);
        (int mcusX, int mcusY) = McuGrid(sof);
        if (!progressive)
        {
            int expected = ((mcusX * mcusY) - 1) / interval;
            Assert.Equal(expected, CountRestartMarkers(data));
            this.output.WriteLine($"{color} interval {interval}: {mcusX}x{mcusY} MCUs, {expected} restart markers");
        }

        AssertRestartMarkersCycleInOrder(data);

        // Restarts only reset the DC predictors on both sides, so the picture must come out unchanged.
        using Image<Rgba32> plain = Image.Load<Rgba32>(
            Encode(source, new JpegEncoder { ColorType = color, Progressive = progressive }));
        using Image<Rgba32> restarted = Image.Load<Rgba32>(data);
        AssertIdentical(plain, restarted, $"{color} interval {interval}: restart markers changed the decoded pixels.");
    }

    [Fact]
    public void RestartInterval_IsOmittedByDefault()
    {
        using Image<Rgb24> source = Photograph(96, 72);
        byte[] data = Encode(source, new JpegEncoder());
        Assert.Equal(0, CountMarkers(data, 0xDD));
        Assert.Equal(0, CountRestartMarkers(data));
    }

    // =================================================================================================
    // (5) Optimised Huffman tables
    // =================================================================================================

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void OptimizedTables_NeverExceedSixteenBits(JpegEncodingColor color)
    {
        using Image<Rgb24> source = Photograph(128, 96);
        foreach (bool progressive in new[] { false, true })
        {
            byte[] data = Encode(source, new JpegEncoder { ColorType = color, OptimizeHuffmanTables = true, Progressive = progressive });
            int tables = 0;
            foreach (byte[] payload in AllSegmentPayloads(data, 0xC4))
            {
                // A DHT payload may define several tables back to back: class/id byte, 16 counts, then symbols.
                int offset = 0;
                while (offset < payload.Length)
                {
                    int symbols = 0;
                    int code = 0;
                    for (int length = 1; length <= 16; length++)
                    {
                        int count = payload[offset + length];
                        symbols += count;

                        // Canonical assignment: after taking `count` codes of this length, the next code must
                        // still fit in `length` bits, which is exactly the "no code longer than 16 bits" rule.
                        code += count;
                        Assert.True(code <= 1 << length, $"{color}: table needs a code longer than {length} bits.");
                        code <<= 1;
                    }

                    offset += 17 + symbols;
                    tables++;
                }

                Assert.Equal(payload.Length, offset);
            }

            Assert.True(tables > 0, $"{color}: no Huffman tables were written.");
        }
    }

    // A YCbCr frame gains the most: it has two very differently distributed component groups, and the Annex K
    // chrominance table is only a rough fit for real chroma.
    [Theory]
    [InlineData(JpegEncodingColor.YCbCrRatio444, 5.0)]
    [InlineData(JpegEncodingColor.YCbCrRatio420, 5.0)]

    // The remaining models put every component on the luminance table pair, as libjpeg does, so one merged set
    // of statistics has to serve them all and there is less to win. The Annex K tables were derived from
    // luminance statistics in the first place, which is exactly the case they already fit.
    [InlineData(JpegEncodingColor.Rgb, 3.5)]
    [InlineData(JpegEncodingColor.Cmyk, 3.0)]
    [InlineData(JpegEncodingColor.Luminance, 3.0)]
    public void OptimizedTables_ShrinkTheFileWithoutChangingThePixels(JpegEncodingColor color, double minimumSaving)
    {
        using Image<Rgb24> source = Photograph(512, 384);
        byte[] standard = Encode(source, new JpegEncoder { ColorType = color, OptimizeHuffmanTables = false });
        byte[] optimized = Encode(source, new JpegEncoder { ColorType = color, OptimizeHuffmanTables = true });

        double saving = 100.0 * (standard.Length - optimized.Length) / standard.Length;
        this.output.WriteLine($"{color}: {standard.Length} -> {optimized.Length} bytes ({saving:F1} % smaller)");
        Assert.True(saving >= minimumSaving, $"{color}: optimised tables saved only {saving:F1} %.");

        using Image<Rgba32> a = Image.Load<Rgba32>(standard);
        using Image<Rgba32> b = Image.Load<Rgba32>(optimized);
        AssertIdentical(a, b, $"{color}: optimised tables changed the decoded pixels.");
    }

    // =================================================================================================
    // (6) Colour models
    // =================================================================================================

    [Theory]
    [InlineData(JpegEncodingColor.Cmyk, 2)]
    [InlineData(JpegEncodingColor.Ycck, 3)]
    public void AdobeColorModels_RoundTripThroughTheDecoder(JpegEncodingColor color, int maxAbsError)
    {
        // At quality 100 every quantisation divisor is 1, so what is left is the rounding of the colour
        // separation and of the transform itself. YCCK adds one more transform stage than CMYK, and its ink
        // channels are reconstructed through a YCbCr inverse, which is why it is allowed one more count.
        using Image<Rgb24> source = Photograph(256, 192);
        using Image<Rgb24> decoded = RoundTrip(source, new JpegEncoder { ColorType = color, Quality = 100 });

        int max = 0;
        long beyondTwo = 0;
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgb24> a = source.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> b = decoded.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                int difference = Math.Max(
                    Math.Abs(a[x].R - b[x].R),
                    Math.Max(Math.Abs(a[x].G - b[x].G), Math.Abs(a[x].B - b[x].B)));
                max = Math.Max(max, difference);
                if (difference > 2)
                {
                    beyondTwo++;
                }
            }
        }

        long pixels = (long)source.Width * source.Height;
        this.output.WriteLine($"{color}: max abs error {max}, {beyondTwo} of {pixels} pixels beyond +/-2, PSNR {Psnr(source, decoded):F2} dB");
        Assert.True(max <= maxAbsError, $"{color}: max abs channel error {max} exceeds {maxAbsError}.");
        Assert.True(beyondTwo * 1000 <= pixels, $"{color}: {beyondTwo} of {pixels} pixels are off by more than 2.");
    }

    [Theory]
    [InlineData(JpegEncodingColor.Rgb, 3, 0)]
    [InlineData(JpegEncodingColor.Cmyk, 4, 0)]
    [InlineData(JpegEncodingColor.Ycck, 4, 2)]
    public void AdobeColorModels_AnnounceThemselvesWithApp14(JpegEncodingColor color, int components, int transform)
    {
        using Image<Rgb24> source = Photograph(64, 48);
        byte[] data = Encode(source, new JpegEncoder { ColorType = color });

        byte[] app14 = SegmentPayload(data, 0xEE);
        Assert.Equal("Adobe"u8.ToArray(), app14[..5]);
        Assert.Equal(transform, app14[11]);
        Assert.Equal(components, SegmentPayload(data, 0xC0)[5]);

        // JFIF describes a JFIF colour model, so an Adobe frame must not claim to be one.
        Assert.Equal(0, CountMarkers(data, 0xE0));
    }

    [Fact]
    public void Rgb_IsStoredWithoutAColourTransform()
    {
        using Image<Rgb24> source = Photograph(64, 48);
        byte[] sof = SegmentPayload(Encode(source, new JpegEncoder { ColorType = JpegEncodingColor.Rgb }), 0xC0);

        // 'R', 'G' and 'B' component identifiers are how a decoder recognises untransformed RGB.
        Assert.Equal((byte)'R', sof[6]);
        Assert.Equal((byte)'G', sof[9]);
        Assert.Equal((byte)'B', sof[12]);
    }

    [Fact]
    public void Rgb_ReproducesTheSourceMoreFaithfullyThanYCbCr()
    {
        using Image<Rgb24> source = Photograph(128, 96);
        using Image<Rgb24> rgb = RoundTrip(source, new JpegEncoder { Quality = 95, ColorType = JpegEncodingColor.Rgb });
        using Image<Rgb24> ycbcr = RoundTrip(source, new JpegEncoder { Quality = 95, ColorType = JpegEncodingColor.YCbCrRatio444 });

        double psnrRgb = Psnr(source, rgb);
        double psnrYcbcr = Psnr(source, ycbcr);
        this.output.WriteLine($"RGB {psnrRgb:F2} dB, YCbCr {psnrYcbcr:F2} dB");
        Assert.True(psnrRgb > psnrYcbcr, "Skipping the colour transform should not lose fidelity.");
    }

    [Fact]
    public void L8Images_DefaultToASingleLuminanceComponent()
    {
        using var source = new Image<L8>(64, 48);
        for (int y = 0; y < source.Height; y++)
        {
            Span<L8> row = source.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                row[x] = new L8((byte)((x * 4) ^ (y * 3)));
            }
        }

        byte[] data = Encode(source, new JpegEncoder { Quality = 95 });
        Assert.Equal(1, SegmentPayload(data, 0xC0)[5]);
        Assert.Equal(1, CountMarkers(data, 0xE0)); // Grayscale is a JFIF colour model.

        using Image<L8> decoded = Image.Load<L8>(data);
        double sum = 0;
        for (int y = 0; y < source.Height; y++)
        {
            Span<L8> a = source.Frames.RootFrame.GetRowSpan(y);
            Span<L8> b = decoded.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                sum += Math.Abs(a[x].PackedValue - b[x].PackedValue);
            }
        }

        Assert.True(sum / (source.Width * source.Height) < 4.0);
    }

    [Fact]
    public void ColorTypeOverridesThePixelTypeDefault()
    {
        using var source = new Image<L8>(32, 32);
        byte[] gray = Encode(source, new JpegEncoder());
        byte[] color = Encode(source, new JpegEncoder { ColorType = JpegEncodingColor.YCbCrRatio444 });
        Assert.Equal(1, SegmentPayload(gray, 0xC0)[5]);
        Assert.Equal(3, SegmentPayload(color, 0xC0)[5]);
    }

    // =================================================================================================
    // Scan layout, metadata and option validation
    // =================================================================================================

    [Fact]
    public void NonInterleaved_WritesOneScanPerComponent()
    {
        using Image<Rgb24> source = Photograph(96, 72);
        byte[] interleaved = Encode(source, new JpegEncoder { ColorType = JpegEncodingColor.YCbCrRatio420 });
        byte[] separate = Encode(source, new JpegEncoder { ColorType = JpegEncodingColor.YCbCrRatio420, Interleaved = false });

        Assert.Equal(1, CountMarkers(interleaved, 0xDA));
        Assert.Equal(3, CountMarkers(separate, 0xDA));

        using Image<Rgba32> a = Image.Load<Rgba32>(interleaved);
        using Image<Rgba32> b = Image.Load<Rgba32>(separate);
        AssertIdentical(a, b, "Splitting the scan changed the decoded pixels.");
    }

    [Fact]
    public void QuantizationTables_FollowTheIjgQualityScaling()
    {
        using Image<Rgb24> source = Photograph(32, 32);

        // Quality 100 drives every divisor to the 1 the clamp imposes; quality 50 reproduces Annex K exactly.
        byte[] top = SegmentPayload(Encode(source, new JpegEncoder { Quality = 100 }), 0xDB);
        Assert.All(top[1..65].ToArray(), value => Assert.Equal(1, value));

        byte[] half = SegmentPayload(Encode(source, new JpegEncoder { Quality = 50 }), 0xDB);
        Assert.Equal(16, half[1]);  // The DC divisor of the Annex K luminance table.
        Assert.Equal(11, half[2]);
    }

    [Theory]
    [MemberData(nameof(AllColorTypes))]
    public void MetadataSegmentsSurviveEveryColourModel(JpegEncodingColor color)
    {
        using Image<Rgb24> source = Photograph(48, 32);
        source.Metadata.HorizontalResolution = 300;
        source.Metadata.VerticalResolution = 300;
        source.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
        source.Metadata.GetFormatMetadata<JpegMetadata>().Comments.Add("EasyImageSharp encoder test");

        byte[] data = Encode(source, new JpegEncoder { ColorType = color });
        using Image<Rgb24> decoded = Decode(data);

        Assert.Equal(
            new[] { "EasyImageSharp encoder test" },
            decoded.Metadata.GetFormatMetadata<JpegMetadata>().Comments.ToArray());

        // Pixel density travels in the JFIF APP0 segment, which only describes the JFIF colour models. Adobe
        // frames carry an APP14 segment in its place, so they keep the metadata but not the density.
        bool jfif = color is not (JpegEncodingColor.Rgb or JpegEncodingColor.Cmyk or JpegEncodingColor.Ycck);
        Assert.Equal(jfif ? 1 : 0, CountMarkers(data, 0xE0));
        if (jfif)
        {
            Assert.Equal(300, decoded.Metadata.HorizontalResolution, 3);
            Assert.Equal(300, decoded.Metadata.VerticalResolution, 3);
        }
    }

    [Fact]
    public void Quality_IsClampedToTheValidRange()
    {
        Assert.Equal(1, new JpegEncoder { Quality = -5 }.Quality);
        Assert.Equal(100, new JpegEncoder { Quality = 1000 }.Quality);
        Assert.Equal(90, new JpegEncoder().Quality);
    }

    [Fact]
    public void UnsupportedOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { ColorType = (JpegEncodingColor)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { RestartInterval = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { RestartInterval = 70000 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { ProgressiveScans = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { ProgressiveScans = 65 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoder { ProgressiveScans = -1 });
    }

    [Fact]
    public void DimensionsBeyondTheFormatAreRejected()
    {
        using var wide = new Image<Rgb24>(70000, 1);
        Assert.Throws<NotSupportedException>(() => Encode(wide, new JpegEncoder()));
    }

    [Fact]
    public void EncoderOverloadsSaveTheSameBytes()
    {
        using Image<Rgb24> source = Photograph(48, 32);
        var encoder = new JpegEncoder { Quality = 70, ColorType = JpegEncodingColor.YCbCrRatio420 };

        using var direct = new MemoryStream();
        source.Save(direct, encoder);

        using var viaExtension = new MemoryStream();
        source.SaveAsJpeg(viaExtension, encoder);

        Assert.Equal(direct.ToArray(), viaExtension.ToArray());
        Assert.Throws<ArgumentNullException>(() => source.SaveAsJpeg(new MemoryStream(), (JpegEncoder)null!));
    }

    // =================================================================================================
    // (7) Throughput
    // =================================================================================================

    [Fact]
    public void Throughput_LargePhotographEncodesQuickly()
    {
        using Image<Rgb24> source = Photograph(3032, 2008);
        var encoder = new JpegEncoder { Quality = 75, ColorType = JpegEncodingColor.YCbCrRatio420 };

        byte[] data = Encode(source, encoder);
        Encode(source, encoder);

        var stopwatch = Stopwatch.StartNew();
        const int Iterations = 5;
        for (int i = 0; i < Iterations; i++)
        {
            Encode(source, encoder);
        }

        stopwatch.Stop();
        double milliseconds = stopwatch.Elapsed.TotalMilliseconds / Iterations;
        double megapixels = source.Width * (double)source.Height / 1_000_000;
        this.output.WriteLine(
            $"3032x2008 q75 4:2:0: {milliseconds:F1} ms/encode ({megapixels / (milliseconds / 1000):F0} MP/s), {data.Length} bytes");

        // Generous headroom over the ~40 ms this takes on a developer machine so shared CI never flakes.
        Assert.True(milliseconds < 2000, $"Encoding took {milliseconds:F0} ms.");
    }

    [Fact]
    public void SingleThreadedConfigurationProducesIdenticalOutput()
    {
        using Image<Rgb24> source = Photograph(320, 240);
        var encoder = new JpegEncoder { ColorType = JpegEncodingColor.YCbCrRatio420 };

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

    // =================================================================================================
    // Helpers
    // =================================================================================================

    /// <summary>
    /// A photographic stand-in: fine luminance texture over smooth shading, with colour that varies on a scale
    /// of tens of pixels the way a real scene's does. Deterministic, so size and PSNR comparisons are stable.
    /// </summary>
    private static Image<Rgb24> Photograph(int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width;
                double v = (double)y / height;
                double luma = 0.50
                    + (0.22 * Math.Sin((u * 6.0) + (v * 3.0)))
                    + (0.10 * Math.Sin((u * 23.0) - (v * 17.0)))
                    + (0.06 * Math.Sin((x * 0.55) + (y * 0.31)))
                    + (0.03 * Math.Sin((x * 1.9) - (y * 1.3)));

                const double Scale = 0.19;
                double cb = (0.5 * Math.Sin((x * Scale) + (y * Scale * 0.6)))
                    + (0.5 * Math.Sin((u * 9.0) - (v * 5.0)))
                    + (0.25 * Math.Cos((x * Scale * 1.7) - (y * Scale * 0.9)));
                double cr = (0.5 * Math.Cos((x * Scale * 0.8) - (y * Scale * 1.1)))
                    + (0.5 * Math.Cos((u * 7.0) + (v * 8.0)))
                    + (0.25 * Math.Sin((x * Scale * 1.3) + (y * Scale * 1.5)));

                double lumaByte = luma * 255;
                double cbValue = cb * 60;
                double crValue = cr * 60;
                row[x] = new Rgb24(
                    ClampByte(lumaByte + (1.402 * crValue)),
                    ClampByte(lumaByte - (0.344136 * cbValue) - (0.714136 * crValue)),
                    ClampByte(lumaByte + (1.772 * cbValue)));
            }
        }

        return image;
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)(value + 0.5), 0, 255);

    private static byte[] Encode<TPixel>(Image<TPixel> image, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static Image<Rgb24> Decode(byte[] data) => Image.Load<Rgb24>(data);

    private static Image<Rgb24> RoundTrip<TPixel>(Image<TPixel> image, JpegEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
        => Decode(Encode(image, encoder));

    private static double Psnr(Image<Rgb24> expected, Image<Rgb24> actual)
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

    private static void AssertIdentical(Image<Rgba32> expected, Image<Rgba32> actual, string message)
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
                    Assert.Fail($"{message} First difference at ({x},{y}): expected {a[x]}, got {b[x]}.");
                }
            }
        }
    }

    // ----- Marker parsing -----

    /// <summary>Walks the marker segments of a JPEG, yielding (marker, payload) and skipping entropy-coded data.</summary>
    private static IEnumerable<(byte Marker, byte[] Payload)> Segments(byte[] data)
    {
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xD8, data[1]);
        int position = 2;
        while (position + 1 < data.Length)
        {
            Assert.Equal(0xFF, data[position]);
            byte marker = data[position + 1];
            position += 2;
            if (marker == 0xD9)
            {
                yield break;
            }

            int length = (data[position] << 8) | data[position + 1];
            yield return (marker, data[(position + 2)..(position + length)]);
            position += length;
            if (marker != 0xDA)
            {
                continue;
            }

            // Skip the entropy-coded segment: stuffed 0xFF00 and RSTn markers belong to it, anything else ends it.
            while (position + 1 < data.Length)
            {
                if (data[position] == 0xFF && data[position + 1] != 0x00 && !(data[position + 1] is >= 0xD0 and <= 0xD7))
                {
                    break;
                }

                position++;
            }
        }
    }

    private static IEnumerable<byte[]> AllSegmentPayloads(byte[] data, byte marker)
        => Segments(data).Where(s => s.Marker == marker).Select(s => s.Payload);

    private static byte[] SegmentPayload(byte[] data, byte marker)
    {
        byte[]? payload = AllSegmentPayloads(data, marker).FirstOrDefault();
        Assert.True(payload is not null, $"No 0x{marker:X2} segment in the output.");
        return payload!;
    }

    private static byte FrameMarker(byte[] data)
        => Segments(data).First(s => s.Marker is 0xC0 or 0xC1 or 0xC2).Marker;

    private static int CountMarkers(byte[] data, byte marker)
        => Segments(data).Count(s => s.Marker == marker);

    /// <summary>The MCU grid implied by a SOF payload: the sampling factors give the MCU size in pixels.</summary>
    private static (int McusX, int McusY) McuGrid(byte[] sof)
    {
        int height = (sof[1] << 8) | sof[2];
        int width = (sof[3] << 8) | sof[4];
        int maxH = 1;
        int maxV = 1;
        for (int i = 0; i < sof[5]; i++)
        {
            maxH = Math.Max(maxH, sof[7 + (i * 3)] >> 4);
            maxV = Math.Max(maxV, sof[7 + (i * 3)] & 15);
        }

        return ((width + (8 * maxH) - 1) / (8 * maxH), (height + (8 * maxV) - 1) / (8 * maxV));
    }

    /// <summary>Counts RSTn markers inside entropy-coded data, skipping stuffed bytes and segment headers.</summary>
    private static int CountRestartMarkers(byte[] data) => RestartMarkers(data).Count;

    private static void AssertRestartMarkersCycleInOrder(byte[] data)
    {
        List<byte> markers = RestartMarkers(data);
        int expected = 0;
        foreach (byte marker in markers)
        {
            // Within one scan RSTn runs 0..7 and wraps; a new scan starts the cycle again at RST0.
            if (marker == 0xD0 && expected != 0)
            {
                expected = 0;
            }

            Assert.Equal(0xD0 + (expected & 7), marker);
            expected++;
        }
    }

    private static List<byte> RestartMarkers(byte[] data)
    {
        var markers = new List<byte>();
        int position = 2;
        while (position + 1 < data.Length)
        {
            byte marker = data[position + 1];
            position += 2;
            if (marker == 0xD9)
            {
                break;
            }

            int length = (data[position] << 8) | data[position + 1];
            position += length;
            if (marker != 0xDA)
            {
                continue;
            }

            while (position + 1 < data.Length)
            {
                if (data[position] == 0xFF)
                {
                    byte next = data[position + 1];
                    if (next is >= 0xD0 and <= 0xD7)
                    {
                        markers.Add(next);
                        position += 2;
                        continue;
                    }

                    if (next != 0x00)
                    {
                        break;
                    }
                }

                position++;
            }
        }

        return markers;
    }
}
