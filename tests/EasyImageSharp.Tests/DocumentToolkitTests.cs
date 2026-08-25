using System.Runtime.InteropServices;
using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Local thresholds, automatic binarisation and the illumination / tone operators of the document toolkit.
/// The expected outputs come from the independent numpy reference implementations in
/// <c>Fixtures/gen_document.py</c>; morphology, geometry, cleanup and layout live in the sibling
/// <c>Document*Tests</c> files.
/// </summary>
public class DocumentToolkitTests
{
    // ----- Legacy Deskew(float) output lock (captured before the toolkit was added) -----

    [Fact]
    public void LegacyDeskew_TextPage_OutputUnchanged()
    {
        using Image<Rgb24> skewed = TestImages.TextPage(300, 200, skewDegrees: 3f);
        skewed.Mutate(ctx => ctx.Deskew(15f));
        Assert.Equal("530865ff-311x216", Fingerprint(skewed));
    }

    [Fact]
    public void LegacyDeskew_Fixture_OutputUnchanged()
    {
        using Image<L8> gray = Image.Load<L8>(FixturePath.Get("document/text_page_skew_p3_0.png"));
        gray.Mutate(ctx => ctx.Deskew());
        Assert.Equal("4b7f183e-575x754", Fingerprint(gray));

        using Image<Rgb24> rgb = Image.Load<Rgb24>(FixturePath.Get("document/text_page_skew_m8_0.png"));
        rgb.Mutate(ctx => ctx.Deskew(10f));
        Assert.Equal("5d23e0ed-695x840", Fingerprint(rgb));
    }

    // ----- Local thresholds against the numpy reference -----

    [Theory]
    [InlineData("niblack")]
    [InlineData("wolf")]
    [InlineData("phansalkar")]
    [InlineData("nick")]
    public void LocalThreshold_MatchesNumpyReference(string method)
    {
        JsonElement spec = DocumentFixtures.Entry("threshold_page").GetProperty("expected").GetProperty(method);
        int window = spec.GetProperty("window").GetInt32();
        float k = (float)spec.GetProperty("k").GetDouble();

        using Image<L8> page = DocumentFixtures.LoadGray("threshold_page.png");
        page.Mutate(ctx => ApplyLocalThreshold(ctx, method, window, k));

        using Image<L8> expected = DocumentFixtures.LoadGray(spec.GetProperty("file").GetString()!);
        Assert.Equal(0, DocumentFixtures.CountDifferences(page, expected));
    }

    /// <summary>The four formulas must not collapse onto one another: the fixture page separates them.</summary>
    [Fact]
    public void LocalThreshold_FormulasDisagreeOnTheReferencePage()
    {
        JsonElement expected = DocumentFixtures.Entry("threshold_page").GetProperty("expected");
        string[] methods = ["niblack", "wolf", "phansalkar", "nick"];
        var masks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string method in methods)
        {
            masks[method] = DocumentFixtures.LoadMask(expected.GetProperty(method).GetProperty("file").GetString()!);
        }

        for (int i = 0; i < methods.Length; i++)
        {
            for (int j = i + 1; j < methods.Length; j++)
            {
                byte[] a = masks[methods[i]];
                byte[] b = masks[methods[j]];
                int differences = 0;
                for (int p = 0; p < a.Length; p++)
                {
                    if (a[p] != b[p])
                    {
                        differences++;
                    }
                }

                Assert.True(differences > 100, $"{methods[i]} and {methods[j]} differ in only {differences} pixels.");
            }
        }
    }

    /// <summary>Documented invariant: a perfectly uniform region has zero local variance and stays white.</summary>
    [Theory]
    [InlineData("niblack")]
    [InlineData("wolf")]
    [InlineData("phansalkar")]
    [InlineData("nick")]
    public void LocalThreshold_UniformPageStaysWhite(string method)
    {
        using Image<L8> flat = DocumentPages.Blank(48, 32);
        DocumentPages.Fill(flat, new Rectangle(0, 0, 48, 32), 200);
        flat.Mutate(ctx => ApplyLocalThreshold(ctx, method, 25, method switch
        {
            "niblack" => -0.2f,
            "wolf" => 0.5f,
            "phansalkar" => 0.25f,
            _ => -0.1f,
        }));

        foreach (byte value in DocumentFixtures.Plane(flat))
        {
            Assert.Equal(255, value);
        }
    }

    [Fact]
    public void LocalThreshold_ProducesOnlyBlackAndWhite()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("threshold_page.png");
        page.Mutate(ctx => ctx.NickThreshold(25, -0.1f));
        foreach (byte value in DocumentFixtures.Plane(page))
        {
            Assert.True(value is 0 or 255, $"Unexpected level {value} in a binarised page.");
        }
    }

    // ----- Binarize(Auto) branch selection -----

    [Fact]
    public void BinarizeAuto_EvenlyLitPage_ChoosesOtsu()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        byte[] luminance = DocumentPlanes.Luminance(page.Frames.RootFrame);
        var options = new BinarizeOptions();

        BinarizeMethod method = LocalThresholdOps.ChooseMethod(
            luminance, page.Width, page.Height, options, out LocalThresholdOps.PageStatistics statistics);

        Assert.Equal(BinarizeMethod.Otsu, method);
        Assert.True(statistics.Separability >= options.MinSeparability, $"Separability {statistics.Separability:0.000}");
        Assert.True(statistics.BackgroundSpread <= options.IlluminationTolerance, $"Spread {statistics.BackgroundSpread}");
    }

    [Fact]
    public void BinarizeAuto_UnevenlyLitPage_ChoosesSauvola()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("noisy_page.png");
        byte[] luminance = DocumentPlanes.Luminance(page.Frames.RootFrame);
        var options = new BinarizeOptions();

        BinarizeMethod method = LocalThresholdOps.ChooseMethod(
            luminance, page.Width, page.Height, options, out LocalThresholdOps.PageStatistics statistics);

        Assert.Equal(BinarizeMethod.Sauvola, method);
        Assert.True(statistics.BackgroundSpread > options.IlluminationTolerance, $"Spread {statistics.BackgroundSpread}");
    }

    [Fact]
    public void BinarizeAuto_EvenlyLitPage_MatchesOtsuOutput()
    {
        using Image<L8> automatic = DocumentFixtures.LoadGray("text_page.png");
        automatic.Mutate(ctx => ctx.Binarize());
        using Image<L8> otsu = DocumentFixtures.LoadGray("text_page.png");
        otsu.Mutate(ctx => ctx.OtsuThreshold());

        Assert.Equal(0, DocumentFixtures.CountDifferences(automatic, otsu));
    }

    [Fact]
    public void BinarizeAuto_UnevenlyLitPage_MatchesSauvolaOutput()
    {
        var options = new BinarizeOptions();
        using Image<L8> automatic = DocumentFixtures.LoadGray("noisy_page.png");
        automatic.Mutate(ctx => ctx.Binarize());
        using Image<L8> sauvola = DocumentFixtures.LoadGray("noisy_page.png");
        sauvola.Mutate(ctx => ctx.SauvolaThreshold(options.WindowSize, options.K));

        Assert.Equal(0, DocumentFixtures.CountDifferences(automatic, sauvola));
    }

    [Theory]
    [InlineData(BinarizeMethod.Otsu)]
    [InlineData(BinarizeMethod.Sauvola)]
    [InlineData(BinarizeMethod.Niblack)]
    [InlineData(BinarizeMethod.WolfJolion)]
    [InlineData(BinarizeMethod.Phansalkar)]
    [InlineData(BinarizeMethod.Nick)]
    public void Binarize_ExplicitMethod_ProducesBinaryOutput(BinarizeMethod method)
    {
        using Image<L8> page = DocumentFixtures.LoadGray("threshold_page.png");
        page.Mutate(ctx => ctx.Binarize(new BinarizeOptions { Method = method }));

        byte[] plane = DocumentFixtures.Plane(page);
        Assert.All(plane, value => Assert.True(value is 0 or 255));

        // Every method must find some ink and keep some paper on this page.
        int ink = plane.Count(v => v == 0);
        Assert.InRange(ink, 1, plane.Length - 1);
    }

    /// <summary>
    /// The local formulas need k values of different signs, so an unset <see cref="BinarizeOptions.K"/> must fall
    /// back to each method's own default. Sharing Sauvola's +0.2 turned a NICK page completely black.
    /// </summary>
    [Theory]
    [InlineData(BinarizeMethod.Niblack)]
    [InlineData(BinarizeMethod.WolfJolion)]
    [InlineData(BinarizeMethod.Phansalkar)]
    [InlineData(BinarizeMethod.Nick)]
    public void Binarize_UnsetK_MatchesTheMethodsConvenienceOverload(BinarizeMethod method)
    {
        using Image<L8> viaOptions = DocumentFixtures.LoadGray("threshold_page.png");
        viaOptions.Mutate(ctx => ctx.Binarize(new BinarizeOptions { Method = method }));

        using Image<L8> viaOverload = DocumentFixtures.LoadGray("threshold_page.png");
        viaOverload.Mutate(ctx =>
        {
            switch (method)
            {
                case BinarizeMethod.Niblack:
                    ctx.NiblackThreshold();
                    break;
                case BinarizeMethod.WolfJolion:
                    ctx.WolfJolionThreshold();
                    break;
                case BinarizeMethod.Phansalkar:
                    ctx.PhansalkarThreshold();
                    break;
                default:
                    ctx.NickThreshold();
                    break;
            }
        });

        Assert.Equal(0, DocumentFixtures.CountDifferences(viaOptions, viaOverload));
    }

    [Fact]
    public void Binarize_AssignedK_IsHonoured()
    {
        using Image<L8> viaOptions = DocumentFixtures.LoadGray("threshold_page.png");
        viaOptions.Mutate(ctx => ctx.Binarize(new BinarizeOptions { Method = BinarizeMethod.Niblack, K = -0.5f }));

        using Image<L8> direct = DocumentFixtures.LoadGray("threshold_page.png");
        direct.Mutate(ctx => ctx.NiblackThreshold(25, -0.5f));

        Assert.Equal(0, DocumentFixtures.CountDifferences(viaOptions, direct));
    }

    [Fact]
    public void BinarizeOptions_KReportsSauvolasDefaultUntilAssigned()
    {
        var options = new BinarizeOptions();
        Assert.Equal(0.2f, options.K);

        options.K = 0.35f;
        Assert.Equal(0.35f, options.K);
        Assert.Equal(0.35f, options.KFor(BinarizeMethod.Nick));
    }

    [Fact]
    public void Binarize_NullOptions_Throws()
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.Binarize(null!)));
    }

    // ----- Illumination and tone against the numpy reference -----

    [Fact]
    public void ContrastStretch_MatchesNumpyReference()
    {
        JsonElement spec = DocumentFixtures.Entry("tone_page").GetProperty("contrast_stretch");
        using Image<Rgb24> page = DocumentFixtures.LoadRgb("tone_page.png");
        page.Mutate(ctx => ctx.ContrastStretch(
            (float)spec.GetProperty("low_percentile").GetDouble(),
            (float)spec.GetProperty("high_percentile").GetDouble()));

        using Image<Rgb24> expected = DocumentFixtures.LoadRgb(spec.GetProperty("file").GetString()!);
        Assert.Equal(0, DocumentFixtures.MaxChannelDifference(page, expected));
    }

    [Fact]
    public void AutoLevels_MatchesNumpyReference()
    {
        JsonElement spec = DocumentFixtures.Entry("tone_page").GetProperty("auto_levels");
        using Image<Rgb24> page = DocumentFixtures.LoadRgb("tone_page.png");
        page.Mutate(ctx => ctx.AutoLevels());

        using Image<Rgb24> expected = DocumentFixtures.LoadRgb(spec.GetProperty("file").GetString()!);
        Assert.Equal(0, DocumentFixtures.MaxChannelDifference(page, expected));
    }

    [Fact]
    public void Normalize_MatchesNumpyReference()
    {
        JsonElement spec = DocumentFixtures.Entry("tone_page").GetProperty("normalize");
        using Image<Rgb24> page = DocumentFixtures.LoadRgb("tone_page.png");
        page.Mutate(ctx => ctx.Normalize());

        using Image<Rgb24> expected = DocumentFixtures.LoadRgb(spec.GetProperty("file").GetString()!);
        Assert.Equal(0, DocumentFixtures.MaxChannelDifference(page, expected));
    }

    [Fact]
    public void Gamma_MatchesNumpyReference()
    {
        JsonElement spec = DocumentFixtures.Entry("tone_page").GetProperty("gamma");
        using Image<Rgb24> page = DocumentFixtures.LoadRgb("tone_page.png");
        page.Mutate(ctx => ctx.Gamma((float)spec.GetProperty("gamma").GetDouble()));

        using Image<Rgb24> expected = DocumentFixtures.LoadRgb(spec.GetProperty("file").GetString()!);

        // Math.Pow and numpy's power are both correctly rounded to within an ulp, which can move a value that
        // lands exactly on a .5 rounding boundary by one level; anything larger is a real difference.
        Assert.True(DocumentFixtures.MaxChannelDifference(page, expected) <= 1);
    }

    [Fact]
    public void Gamma_IsInvertibleAroundOne()
    {
        using Image<Rgb24> page = DocumentFixtures.LoadRgb("tone_page.png");
        using Image<Rgb24> original = page.Clone();
        page.Mutate(ctx => ctx.Gamma(1f));
        Assert.Equal(0, DocumentFixtures.MaxChannelDifference(page, original));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.PositiveInfinity)]
    public void Gamma_RejectsNonPositiveOrInfinite(float gamma)
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.Gamma(gamma)));
    }

    [Theory]
    [InlineData(-1f, 99f)]
    [InlineData(10f, 5f)]
    [InlineData(0f, 101f)]
    public void ContrastStretch_RejectsInvalidPercentiles(float low, float high)
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.ContrastStretch(low, high)));
    }

    // ----- Background normalisation -----

    [Fact]
    public void BackgroundNormalize_FlattensAStrongIlluminationGradient()
    {
        using Image<L8> clean = DocumentFixtures.LoadGray("text_page.png");
        byte[] ink = DocumentFixtures.InkMask(clean);
        using Image<L8> lit = DocumentPages.WithIlluminationGradient(clean);

        int before = BackgroundSpread(DocumentFixtures.Plane(lit), ink);
        lit.Mutate(ctx => ctx.BackgroundNormalize());
        int after = BackgroundSpread(DocumentFixtures.Plane(lit), ink);

        Assert.True(before > 100, $"The synthetic gradient should span the page; spread was {before}.");
        Assert.True(after <= before * 0.2, $"Background spread only fell from {before} to {after}.");
    }

    [Fact]
    public void BackgroundNormalize_KeepsInkDarkerThanPaper()
    {
        using Image<L8> clean = DocumentFixtures.LoadGray("text_page.png");
        byte[] ink = DocumentFixtures.InkMask(clean);
        using Image<L8> lit = DocumentPages.WithIlluminationGradient(clean);
        lit.Mutate(ctx => ctx.BackgroundNormalize());

        byte[] plane = DocumentFixtures.Plane(lit);
        double inkMean = Mean(plane, ink, wantInk: true);
        double paperMean = Mean(plane, ink, wantInk: false);
        Assert.True(paperMean - inkMean > 100, $"Ink {inkMean:0.0} vs paper {paperMean:0.0} after normalisation.");
        Assert.True(paperMean > 235, $"Paper should be pushed to white, got {paperMean:0.0}.");
    }

    [Fact]
    public void RemoveShadows_FlattensAWideGradient()
    {
        using Image<L8> clean = DocumentFixtures.LoadGray("text_page.png");
        byte[] ink = DocumentFixtures.InkMask(clean);
        using Image<L8> lit = DocumentPages.WithIlluminationGradient(clean, rightFalloff: 0.5, bottomFalloff: 0.75);

        int before = BackgroundSpread(DocumentFixtures.Plane(lit), ink);
        lit.Mutate(ctx => ctx.RemoveShadows());
        int after = BackgroundSpread(DocumentFixtures.Plane(lit), ink);

        Assert.True(after < before / 2, $"Background spread only fell from {before} to {after}.");
    }

    [Fact]
    public void BackgroundNormalize_RejectsNegativeRadius()
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.BackgroundNormalize(-1)));
    }

    // ----- Helpers -----

    private static void ApplyLocalThreshold(IImageProcessingContext context, string method, int window, float k)
    {
        switch (method)
        {
            case "niblack":
                context.NiblackThreshold(window, k);
                break;
            case "wolf":
                context.WolfJolionThreshold(window, k);
                break;
            case "phansalkar":
                context.PhansalkarThreshold(window, k, 2f, 10f);
                break;
            case "nick":
                context.NickThreshold(window, k);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown local threshold.");
        }
    }

    /// <summary>Max minus min over the pixels that are paper in the clean reference.</summary>
    private static int BackgroundSpread(byte[] plane, byte[] inkMask)
    {
        int low = 255;
        int high = 0;
        for (int i = 0; i < plane.Length; i++)
        {
            if (inkMask[i] != 0)
            {
                continue;
            }

            low = Math.Min(low, plane[i]);
            high = Math.Max(high, plane[i]);
        }

        return high - low;
    }

    private static double Mean(byte[] plane, byte[] inkMask, bool wantInk)
    {
        long sum = 0;
        long count = 0;
        for (int i = 0; i < plane.Length; i++)
        {
            if ((inkMask[i] != 0) != wantInk)
            {
                continue;
            }

            sum += plane[i];
            count++;
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    internal static string Fingerprint<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        // FNV-1a over the raw pixel bytes of the root frame.
        ulong hash = 14695981039346656037UL;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(image.Frames.RootFrame.GetRowSpan(y));
            foreach (byte b in row)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }
        }

        return $"{(uint)hash:x8}-{image.Width}x{image.Height}";
    }
}
