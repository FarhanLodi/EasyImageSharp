using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Rule / hole-punch / margin cleanup, projection-profile layout segmentation, DPI normalisation and the
/// end-to-end OCR preset, measured against the masks and line boxes in the fixture manifest.
/// </summary>
public class DocumentLayoutTests
{
    // ----- Rules -----

    /// <summary>
    /// 7.7 % of the injected rule mask is literally text ink (two rules are drawn straight through a text line
    /// and a column of words), and the operator deliberately keeps those pixels so the strokes stay intact.
    /// The removal rate is therefore measured on the rule pixels that are not shared with text.
    /// </summary>
    [Fact]
    public void RemoveLines_ErasesTheRulesAndKeepsTheText()
    {
        byte[] rules = DocumentFixtures.LoadMask("rules_page_rules_mask.png");
        byte[] text = DocumentFixtures.LoadMask("rules_page_text_mask.png");
        var rulesOnly = new byte[rules.Length];
        for (int i = 0; i < rules.Length; i++)
        {
            rulesOnly[i] = (byte)(rules[i] != 0 && text[i] == 0 ? 1 : 0);
        }

        using Image<L8> page = DocumentFixtures.LoadGray("rules_page.png");
        page.Mutate(ctx => ctx.RemoveLines(200));
        byte[] after = DocumentFixtures.InkMask(page);

        double ruleRemoval = 1 - DocumentFixtures.Retained(after, rulesOnly);
        double allRuleRemoval = 1 - DocumentFixtures.Retained(after, rules);
        double textRetention = DocumentFixtures.Retained(after, text);

        Assert.True(ruleRemoval >= 0.95, $"Only {ruleRemoval:P2} of the rule-only pixels were removed.");
        Assert.True(allRuleRemoval >= 0.94, $"Only {allRuleRemoval:P2} of the whole rule mask was removed.");
        Assert.True(textRetention >= 0.98, $"Only {textRetention:P2} of the text survived.");
    }

    [Fact]
    public void RemoveLines_Horizontal_LeavesVerticalRulesAlone()
    {
        using Image<L8> page = DocumentPages.Blank(200, 160);
        DocumentPages.Fill(page, new Rectangle(10, 40, 180, 3), 0);
        DocumentPages.Fill(page, new Rectangle(60, 10, 3, 140), 0);

        page.Mutate(ctx => ctx.RemoveLines(100, LineOrientation.Horizontal));
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(255, plane[(41 * 200) + 150]);
        Assert.Equal(0, plane[(120 * 200) + 61]);
    }

    [Fact]
    public void RemoveLines_Vertical_LeavesHorizontalRulesAlone()
    {
        using Image<L8> page = DocumentPages.Blank(200, 160);
        DocumentPages.Fill(page, new Rectangle(10, 40, 180, 3), 0);
        DocumentPages.Fill(page, new Rectangle(60, 10, 3, 140), 0);

        page.Mutate(ctx => ctx.RemoveLines(100, LineOrientation.Vertical));
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(0, plane[(41 * 200) + 150]);
        Assert.Equal(255, plane[(120 * 200) + 61]);
    }

    [Fact]
    public void RemoveLines_KeepsShapesShorterThanTheMinimumLength()
    {
        using Image<L8> page = DocumentPages.Blank(200, 120);
        DocumentPages.Fill(page, new Rectangle(20, 60, 40, 3), 0);

        page.Mutate(ctx => ctx.RemoveLines(150, LineOrientation.Both));

        Assert.Equal(0, DocumentFixtures.Plane(page)[(61 * 200) + 40]);
    }

    [Fact]
    public void RemoveLines_RejectsANonPositiveLength()
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.RemoveLines(0, LineOrientation.Both)));
    }

    // ----- Hole punches -----

    [Fact]
    public void RemoveHolePunches_ErasesTheDiscsAndKeepsTheText()
    {
        byte[] holes = DocumentFixtures.LoadMask("holes_page_holes_mask.png");
        byte[] text = DocumentFixtures.LoadMask("rules_page_text_mask.png");

        using Image<L8> page = DocumentFixtures.LoadGray("holes_page.png");
        page.Mutate(ctx => ctx.RemoveHolePunches());
        byte[] after = DocumentFixtures.InkMask(page);

        double removal = 1 - DocumentFixtures.Retained(after, holes);
        double textRetention = DocumentFixtures.Retained(after, text);

        Assert.True(removal >= 0.95, $"Only {removal:P2} of the hole-punch pixels were removed.");
        Assert.True(textRetention >= 0.98, $"Only {textRetention:P2} of the text survived.");
    }

    [Fact]
    public void RemoveHolePunches_KeepsADiscInTheMiddleOfThePage()
    {
        using Image<L8> page = DocumentPages.Blank(400, 300);
        DocumentPages.FillDisc(page, 200, 150, 10, 0);

        page.Mutate(ctx => ctx.RemoveHolePunches());

        Assert.Equal(0, DocumentFixtures.Plane(page)[(150 * 400) + 200]);
    }

    [Fact]
    public void RemoveHolePunches_KeepsASquareBlockNearTheEdge()
    {
        using Image<L8> page = DocumentPages.Blank(400, 300);
        DocumentPages.Fill(page, new Rectangle(10, 140, 20, 20), 0);

        page.Mutate(ctx => ctx.RemoveHolePunches());

        Assert.Equal(0, DocumentFixtures.Plane(page)[(150 * 400) + 20]);
    }

    // ----- Margin noise and borders -----

    [Fact]
    public void RemoveMarginNoise_ClearsTheMarginBandAndBorderBlobs()
    {
        using Image<L8> page = DocumentPages.Blank(400, 300);
        DocumentPages.Fill(page, new Rectangle(0, 100, 20, 40), 0);
        DocumentPages.Fill(page, new Rectangle(3, 50, 2, 2), 0);
        DocumentPages.Fill(page, new Rectangle(100, 140, 60, 12), 0);

        page.Mutate(ctx => ctx.RemoveMarginNoise());
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(255, plane[(120 * 400) + 10]);
        Assert.Equal(255, plane[(50 * 400) + 3]);
        Assert.Equal(0, plane[(145 * 400) + 130]);
    }

    [Fact]
    public void RemoveBorders_ErasesADarkScannerFrameAndKeepsTheText()
    {
        using Image<L8> page = DocumentPages.Blank(300, 220);
        DocumentPages.Fill(page, new Rectangle(0, 0, 300, 6), 0);
        DocumentPages.Fill(page, new Rectangle(0, 214, 300, 6), 0);
        DocumentPages.Fill(page, new Rectangle(0, 0, 6, 220), 0);
        DocumentPages.Fill(page, new Rectangle(294, 0, 6, 220), 0);
        DocumentPages.Fill(page, new Rectangle(120, 100, 50, 10), 0);

        page.Mutate(ctx => ctx.RemoveBorders());
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(255, plane[(2 * 300) + 150]);
        Assert.Equal(255, plane[(110 * 300) + 2]);
        Assert.Equal(0, plane[(105 * 300) + 140]);
    }

    // ----- Layout segmentation -----

    /// <summary>The segmenter must reproduce the line boxes the generator laid out, box for box.</summary>
    [Fact]
    public void SegmentTextLines_MatchesTheManifestLineBoxes()
    {
        JsonElement lines = DocumentFixtures.Entry("text_page").GetProperty("lines");
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");

        IReadOnlyList<Rectangle> actual = SegmentLines(page);

        Assert.Equal(lines.GetArrayLength(), actual.Count);
        for (int i = 0; i < actual.Count; i++)
        {
            JsonElement bounds = lines[i].GetProperty("bounds");
            var expected = new Rectangle(
                bounds[0].GetInt32(), bounds[1].GetInt32(), bounds[2].GetInt32(), bounds[3].GetInt32());
            Assert.Equal(expected, actual[i]);
        }
    }

    [Fact]
    public void SegmentWords_MatchesTheManifestWordBoxes()
    {
        JsonElement lines = DocumentFixtures.Entry("text_page").GetProperty("lines");
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        TextRegions regions = page.SegmentText();

        Assert.Equal(lines.GetArrayLength(), regions.Lines.Count);
        int expectedTotal = 0;
        for (int i = 0; i < regions.Lines.Count; i++)
        {
            JsonElement words = lines[i].GetProperty("words");
            expectedTotal += words.GetArrayLength();
            Assert.Equal(words.GetArrayLength(), regions.Lines[i].Words.Count);
            for (int w = 0; w < words.GetArrayLength(); w++)
            {
                JsonElement box = words[w];
                var expected = new Rectangle(
                    box[0].GetInt32(), box[1].GetInt32(), box[2].GetInt32(), box[3].GetInt32());
                Assert.Equal(expected, regions.Lines[i].Words[w]);
            }
        }

        Assert.Equal(expectedTotal, regions.Words.Count);
    }

    [Fact]
    public void SegmentTextLines_BoxesCoverEveryInkPixel()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        IReadOnlyList<Rectangle> lines = SegmentLines(page);
        byte[] ink = DocumentFixtures.InkMask(page);

        int covered = 0;
        int total = 0;
        for (int y = 0; y < page.Height; y++)
        {
            for (int x = 0; x < page.Width; x++)
            {
                if (ink[(y * page.Width) + x] == 0)
                {
                    continue;
                }

                total++;
                foreach (Rectangle line in lines)
                {
                    if (line.Contains(x, y))
                    {
                        covered++;
                        break;
                    }
                }
            }
        }

        Assert.True(total > 0);
        Assert.Equal(total, covered);
    }

    [Fact]
    public void SegmentText_IgnoresLongRulesSoLinesDoNotFuse()
    {
        int expected = DocumentFixtures.Entry("text_page").GetProperty("lines").GetArrayLength();
        using Image<L8> page = DocumentFixtures.LoadGray("rules_page.png");

        // The vertical rules span most of the page; without the rule filter they would join every line.
        Assert.Equal(expected, page.SegmentText().Lines.Count);
    }

    [Fact]
    public void SegmentText_OnABlankPage_FindsNothing()
    {
        using Image<L8> blank = DocumentPages.Blank(120, 90);
        TextRegions regions = blank.SegmentText();
        Assert.Empty(regions.Lines);
        Assert.Empty(regions.Words);
    }

    [Fact]
    public void SegmentWords_AreOrderedLeftToRightWithinEachLine()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        foreach (TextLine line in page.SegmentText().Lines)
        {
            for (int i = 1; i < line.Words.Count; i++)
            {
                Assert.True(line.Words[i].X > line.Words[i - 1].X);
            }
        }
    }

    // ----- DPI -----

    [Fact]
    public void NormalizeDpi_RescalesByTheDpiRatio()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        page.Mutate(ctx => ctx.NormalizeDpi(300f, 150));

        Assert.Equal(250, page.Width);
        Assert.Equal(350, page.Height);
    }

    [Fact]
    public void NormalizeDpi_UpscalesWhenTheTargetIsHigher()
    {
        using Image<L8> page = DocumentPages.Blank(100, 80);
        page.Mutate(ctx => ctx.NormalizeDpi(150f, 300));

        Assert.Equal(200, page.Width);
        Assert.Equal(160, page.Height);
    }

    [Fact]
    public void NormalizeDpi_WithMatchingDpi_IsANoOp()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        byte[] before = DocumentFixtures.Plane(page);

        page.Mutate(ctx => ctx.NormalizeDpi(300f, 300));

        Assert.Equal(500, page.Width);
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-10f)]
    [InlineData(float.PositiveInfinity)]
    public void NormalizeDpi_RejectsANonPositiveSourceDpi(float source)
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.NormalizeDpi(source, 300)));
    }

    [Fact]
    public void NormalizeDpi_RejectsANonPositiveTargetDpi()
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.NormalizeDpi(300f, 0)));
    }

    // ----- The OCR preset -----

    [Fact]
    public void PrepareForOcr_BinarisesAndStraightensANoisyScan()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("noisy_page_skewed.png");
        Assert.True(Math.Abs(page.DetectSkew(15f)) > 1.5, "The fixture should start out visibly skewed.");

        page.Mutate(ctx => ctx.PrepareForOcr());

        byte[] plane = DocumentFixtures.Plane(page);
        Assert.All(plane, value => Assert.True(value is 0 or 255, $"Level {value} is not binary."));

        float residual = page.DetectSkew(15f);
        Assert.True(Math.Abs(residual) <= 0.3, $"Residual skew {residual:0.00} degrees.");

        double inkFraction = plane.Count(v => v == 0) / (double)plane.Length;
        Assert.InRange(inkFraction, 0.02, 0.35);
    }

    [Fact]
    public void PrepareForOcr_CanSkipEveryStage()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("noisy_page.png");
        byte[] before = DocumentFixtures.Plane(page);

        page.Mutate(ctx => ctx.PrepareForOcr(new OcrPreprocessOptions
        {
            NormalizeBackground = false,
            Deskew = false,
            Binarize = false,
        }));

        // Only the grayscale conversion remains, and the fixture is already grey.
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Fact]
    public void PrepareForOcr_NullOptions_Throws()
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.PrepareForOcr(null!)));
    }

    [Fact]
    public void OcrPreprocessOptions_HaveTheDocumentedDefaults()
    {
        var options = new OcrPreprocessOptions();
        Assert.True(options.NormalizeBackground);
        Assert.False(options.MedianDenoise);
        Assert.True(options.Deskew);
        Assert.True(options.Binarize);
        Assert.Equal(15f, options.DeskewMaxAngle);
        Assert.Equal(3, options.DespeckleMaxArea);
        Assert.Equal(0, options.BackgroundRadius);
        Assert.Null(options.BinarizeOptions);
    }

    private static IReadOnlyList<Rectangle> SegmentLines(Image<L8> page)
    {
        IReadOnlyList<Rectangle>? lines = null;
        page.Mutate(ctx => lines = ctx.SegmentTextLines());
        return lines!;
    }
}
