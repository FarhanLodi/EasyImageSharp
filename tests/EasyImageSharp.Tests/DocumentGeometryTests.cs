using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Skew estimation and correction, quarter-turn orientation, page detection and perspective correction,
/// measured against the known angles and quadrilateral recorded in the fixture manifest.
/// </summary>
public class DocumentGeometryTests
{
    /// <summary>The synthetic pages skewed by a known angle between -12 and +12 degrees.</summary>
    public static TheoryData<string> SkewFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in DocumentFixtures.Names("skew_entries"))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The upright page and its three quarter-turn rotations.</summary>
    public static TheoryData<string> OrientationFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in DocumentFixtures.Names("orientation_entries"))
        {
            data.Add(name);
        }

        return data;
    }

    // ----- Skew estimation -----

    [Theory]
    [MemberData(nameof(SkewFixtures))]
    public void DetectSkew_Hough_MatchesTheKnownAngle(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        double expected = entry.GetProperty("skew_clockwise").GetDouble();

        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        float actual = page.DetectSkew(15f);

        Assert.True(Math.Abs(actual - expected) <= 0.3, $"Expected {expected:0.00} degrees, got {actual:0.00}.");
    }

    [Theory]
    [MemberData(nameof(SkewFixtures))]
    public void DetectSkew_Projection_MatchesTheKnownAngle(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        double expected = entry.GetProperty("skew_clockwise").GetDouble();

        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        double actual = DeskewOps.EstimateProjectionSkew(page.Frames.RootFrame, 15f, out bool significant);

        Assert.True(significant, "The projection estimator did not consider the skew significant.");
        Assert.True(Math.Abs(actual - expected) <= 0.3, $"Expected {expected:0.00} degrees, got {actual:0.00}.");
    }

    [Theory]
    [MemberData(nameof(SkewFixtures))]
    public void Deskew_Hough_StraightensThePage(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        using Image<L8> deskewed = page.Clone(ctx => ctx.Deskew(new DeskewOptions { Method = DeskewMethod.Hough }));

        float residual = deskewed.DetectSkew(15f);
        Assert.True(Math.Abs(residual) <= 0.5, $"Residual skew {residual:0.00} degrees after deskewing.");
    }

    [Theory]
    [MemberData(nameof(SkewFixtures))]
    public void Deskew_Projection_StraightensThePage(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        using Image<L8> deskewed = page.Clone(ctx => ctx.Deskew(new DeskewOptions { Method = DeskewMethod.Projection }));

        float residual = deskewed.DetectSkew(15f);
        Assert.True(Math.Abs(residual) <= 0.5, $"Residual skew {residual:0.00} degrees after deskewing.");
    }

    [Fact]
    public void DetectSkew_OnANoisyUnevenlyLitScan_StillFindsTheAngle()
    {
        JsonElement entry = DocumentFixtures.Entry("noisy_page_skewed");
        double expected = entry.GetProperty("skew_clockwise").GetDouble();

        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        float actual = page.DetectSkew(15f);

        Assert.True(Math.Abs(actual - expected) <= 0.3, $"Expected {expected:0.00} degrees, got {actual:0.00}.");
    }

    [Fact]
    public void DetectSkew_OnAStraightPage_ReportsAlmostZero()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        Assert.True(Math.Abs(page.DetectSkew(15f)) < 0.1);
    }

    [Fact]
    public void DetectSkew_OnABlankPage_ReturnsZero()
    {
        using Image<L8> blank = DocumentPages.Blank(120, 90);
        Assert.Equal(0f, blank.DetectSkew(15f));
    }

    [Theory]
    [InlineData(DeskewMethod.Hough)]
    [InlineData(DeskewMethod.Projection)]
    public void Deskew_LeavesAnAlreadyStraightPageUntouched(DeskewMethod method)
    {
        using Image<L8> page = DocumentFixtures.LoadGray("text_page.png");
        byte[] before = DocumentFixtures.Plane(page);

        page.Mutate(ctx => ctx.Deskew(new DeskewOptions { Method = method }));

        Assert.Equal(500, page.Width);
        Assert.Equal(700, page.Height);
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Fact]
    public void Deskew_NullOptions_Throws()
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.Deskew((DeskewOptions)null!)));
    }

    [Fact]
    public void DeskewOptions_HaveTheDocumentedDefaults()
    {
        var options = new DeskewOptions();
        Assert.Equal(DeskewMethod.Hough, options.Method);
        Assert.Equal(15f, options.MaxAngle);
        Assert.Equal(0.1f, options.MinAngle);
    }

    // ----- Orientation -----

    /// <summary>
    /// The heuristic separates all four quarter turns on this Latin-like page (ascender-heavy lines with a
    /// ragged right margin), so every case is asserted exactly rather than only 0 and 180.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrientationFixtures))]
    public void DetectOrientation_FindsTheQuarterTurnThatUprightsThePage(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        var expected = (RotateMode)entry.GetProperty("fix_rotation_cw").GetInt32();

        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        OrientationEstimate estimate = page.DetectOrientation();

        Assert.Equal(expected, estimate.Rotation);
        Assert.True(estimate.Confidence > 0.5f, $"Confidence was only {estimate.Confidence:0.00}.");
    }

    [Fact]
    public void DetectOrientation_OnABlankPage_IsUnsure()
    {
        using Image<L8> blank = DocumentPages.Blank(200, 150);
        OrientationEstimate estimate = blank.DetectOrientation();

        Assert.Equal(RotateMode.None, estimate.Rotation);
        Assert.Equal(0f, estimate.Confidence);
    }

    [Theory]
    [MemberData(nameof(OrientationFixtures))]
    public void AutoRotateDocument_UprightsThePage(string fixture)
    {
        JsonElement entry = DocumentFixtures.Entry(fixture);
        using Image<L8> page = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        page.Mutate(ctx => ctx.AutoRotateDocument());

        // Whatever the input turn was, the result must look upright to the same heuristic.
        Assert.Equal(RotateMode.None, page.DetectOrientation().Rotation);
        Assert.Equal(500, page.Width);
        Assert.Equal(700, page.Height);
    }

    [Fact]
    public void AutoRotateDocument_BelowTheConfidenceFloor_LeavesThePageAlone()
    {
        using Image<L8> blank = DocumentPages.Blank(120, 90);
        blank.Mutate(ctx => ctx.AutoRotateDocument(0.9f));
        Assert.Equal(120, blank.Width);
        Assert.Equal(90, blank.Height);
    }

    [Fact]
    public void OrientationEstimate_ToStringMentionsTheRotation()
    {
        var estimate = new OrientationEstimate(RotateMode.Rotate90, 0.75f);
        Assert.Contains("Rotate90", estimate.ToString(), StringComparison.Ordinal);
        Assert.Equal(0.75f, estimate.Confidence);
    }

    // ----- Page detection and perspective -----

    [Fact]
    public void DetectPage_FindsTheKnownQuadrilateral()
    {
        JsonElement entry = DocumentFixtures.Entry("perspective_page");
        using Image<L8> photo = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);

        PointF[]? quad = photo.DetectPage();

        Assert.NotNull(quad);
        Assert.Equal(4, quad!.Length);
        JsonElement expected = entry.GetProperty("quad");
        for (int i = 0; i < 4; i++)
        {
            double ex = expected[i][0].GetDouble();
            double ey = expected[i][1].GetDouble();
            double distance = Math.Sqrt(((quad[i].X - ex) * (quad[i].X - ex)) + ((quad[i].Y - ey) * (quad[i].Y - ey)));
            Assert.True(distance <= 3.0, $"Corner {i} is {distance:0.00} px from ({ex}, {ey}).");
        }
    }

    [Fact]
    public void CorrectPerspective_RectifiesThePhotographBackToTheFlatPage()
    {
        JsonElement entry = DocumentFixtures.Entry("perspective_page");
        using Image<L8> photo = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        PointF[]? quad = photo.DetectPage();
        Assert.NotNull(quad);

        var size = new Size(entry.GetProperty("page_width").GetInt32(), entry.GetProperty("page_height").GetInt32());
        using Image<L8> rectified = photo.Clone(ctx => ctx.CorrectPerspective(quad!, size));
        using Image<L8> flat = DocumentFixtures.LoadGray(entry.GetProperty("flat").GetString()!);

        Assert.Equal(size.Width, rectified.Width);
        Assert.Equal(size.Height, rectified.Height);
        double psnr = DocumentFixtures.Psnr(rectified, flat);
        Assert.True(psnr > 25.0, $"Rectified page only reached {psnr:0.00} dB.");
    }

    [Fact]
    public void CorrectPerspective_WithTheGroundTruthQuad_AlsoRectifies()
    {
        JsonElement entry = DocumentFixtures.Entry("perspective_page");
        JsonElement corners = entry.GetProperty("quad");
        var quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            quad[i] = new PointF((float)corners[i][0].GetDouble(), (float)corners[i][1].GetDouble());
        }

        using Image<L8> photo = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        var size = new Size(entry.GetProperty("page_width").GetInt32(), entry.GetProperty("page_height").GetInt32());
        using Image<L8> rectified = photo.Clone(ctx => ctx.CorrectPerspective(quad, size));
        using Image<L8> flat = DocumentFixtures.LoadGray(entry.GetProperty("flat").GetString()!);

        double psnr = DocumentFixtures.Psnr(rectified, flat);
        Assert.True(psnr > 25.0, $"Rectified page only reached {psnr:0.00} dB.");
    }

    /// <summary>
    /// Warping the whole-image quad onto the same size is the identity mapping, so the sampler must reproduce
    /// the source almost exactly (a bilinear resample at pixel centres).
    /// </summary>
    [Fact]
    public void CorrectPerspective_WithTheFullImageQuad_IsTheIdentity()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("perspective_page_flat.png");
        PointF[] quad =
        [
            new PointF(0f, 0f),
            new PointF(page.Width, 0f),
            new PointF(page.Width, page.Height),
            new PointF(0f, page.Height),
        ];

        using Image<L8> warped = page.Clone(ctx => ctx.CorrectPerspective(quad, new Size(page.Width, page.Height)));
        Assert.Equal(0, DocumentFixtures.CountDifferences(warped, page));
    }

    [Fact]
    public void CorrectPerspective_NaturalSize_FollowsTheQuadsLongestSides()
    {
        JsonElement entry = DocumentFixtures.Entry("perspective_page");
        JsonElement corners = entry.GetProperty("quad");
        var quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            quad[i] = new PointF((float)corners[i][0].GetDouble(), (float)corners[i][1].GetDouble());
        }

        using Image<L8> photo = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        using Image<L8> rectified = photo.Clone(ctx => ctx.CorrectPerspective(quad));

        Size natural = PerspectiveWarp.NaturalSize(quad);
        Assert.Equal(natural.Width, rectified.Width);
        Assert.Equal(natural.Height, rectified.Height);
        Assert.InRange(rectified.Width, 550, 570);
        Assert.InRange(rectified.Height, 435, 455);
    }

    [Fact]
    public void CorrectPerspective_RejectsAQuadThatIsNotFourCorners()
    {
        using Image<L8> page = DocumentPages.Blank(20, 20);
        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.CorrectPerspective(null!, null)));
        Assert.Throws<ArgumentException>(() => page.Mutate(ctx => ctx.CorrectPerspective([new PointF(0, 0)], null)));
    }

    [Fact]
    public void SolveHomography_RejectsDegeneratePoints()
    {
        PointF[] collinear =
        [
            new PointF(0, 0),
            new PointF(1, 1),
            new PointF(2, 2),
            new PointF(3, 3),
        ];
        PointF[] square =
        [
            new PointF(0, 0),
            new PointF(10, 0),
            new PointF(10, 10),
            new PointF(0, 10),
        ];

        Assert.Throws<ArgumentException>(() => PerspectiveWarp.SolveHomography(collinear, square));
        Assert.Throws<ArgumentException>(() => PerspectiveWarp.SolveHomography(square.AsSpan(0, 3), square));
    }

    [Fact]
    public void SolveHomography_RoundTripsTheCornerCorrespondences()
    {
        PointF[] source =
        [
            new PointF(0, 0),
            new PointF(100, 0),
            new PointF(100, 80),
            new PointF(0, 80),
        ];
        PointF[] target =
        [
            new PointF(12, 7),
            new PointF(118, 21),
            new PointF(104, 96),
            new PointF(3, 88),
        ];

        double[] h = PerspectiveWarp.SolveHomography(source, target);
        for (int i = 0; i < 4; i++)
        {
            PointF mapped = PerspectiveWarp.Apply(h, source[i]);
            Assert.Equal(target[i].X, mapped.X, 3);
            Assert.Equal(target[i].Y, mapped.Y, 3);
        }
    }

    [Fact]
    public void AutoCropPage_CropsThePhotographDownToThePage()
    {
        JsonElement entry = DocumentFixtures.Entry("perspective_page");
        using Image<L8> photo = DocumentFixtures.LoadGray(entry.GetProperty("file").GetString()!);
        using Image<L8> cropped = photo.Clone(ctx => ctx.AutoCropPage());

        Assert.True(cropped.Width < photo.Width, $"Width stayed at {cropped.Width}.");
        Assert.True(cropped.Height < photo.Height, $"Height stayed at {cropped.Height}.");
        Assert.InRange(cropped.Width, 540, 580);
        Assert.InRange(cropped.Height, 420, 470);

        // The dark photographic background must be gone: the result is a bright page.
        byte[] plane = DocumentFixtures.Plane(cropped);
        double mean = plane.Average(v => (double)v);
        Assert.True(mean > 180, $"Cropped page mean brightness was only {mean:0.0}.");
    }

    [Fact]
    public void AutoCropPage_WithNoDetectablePage_LeavesTheImageUnchanged()
    {
        using Image<L8> blank = DocumentPages.Blank(120, 90);
        byte[] before = DocumentFixtures.Plane(blank);

        blank.Mutate(ctx => ctx.AutoCropPage());

        Assert.Equal(120, blank.Width);
        Assert.Equal(90, blank.Height);
        Assert.Equal(before, DocumentFixtures.Plane(blank));
    }

    [Fact]
    public void DetectPage_OnABlankImage_ReturnsNull()
    {
        using Image<L8> blank = DocumentPages.Blank(120, 90);
        Assert.Null(blank.DetectPage());
    }
}
