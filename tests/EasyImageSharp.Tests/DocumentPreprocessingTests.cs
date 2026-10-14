using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public sealed class DocumentPreprocessingTests
{
    [Fact]
    public void PerceptualSimilarity_DistinguishesOppositeGradientsAndMatchesClones()
    {
        using var leftToRight = new Image<L8>(90, 80);
        using var rightToLeft = new Image<L8>(90, 80);
        for (int y = 0; y < 80; y++)
        {
            for (int x = 0; x < 90; x++)
            {
                leftToRight[x, y] = new L8((byte)(x * 255 / 89));
                rightToLeft[x, y] = new L8((byte)((89 - x) * 255 / 89));
            }
        }

        using Image<L8> clone = leftToRight.Clone();
        DocumentAnalysis original = leftToRight.AnalyzeDocument()[0];
        Assert.Equal(1, original.PerceptualSimilarity(clone.AnalyzeDocument()[0]));
        Assert.Equal(0, original.PerceptualSimilarity(rightToLeft.AnalyzeDocument()[0]));
    }

    [Fact]
    public void Analysis_UniformPageHasAnalyticStatistics()
    {
        using var image = new Image<Rgba32>(16, 12);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));
        DocumentAnalysis result = Assert.Single(image.AnalyzeDocument());
        Assert.Equal(1, result.MeanLuminance, 10);
        Assert.Equal(0, result.LuminanceStandardDeviation, 10);
        Assert.Equal(0, result.Contrast);
        Assert.Equal(0, result.LaplacianVariance, 10);
        Assert.Equal(0, result.DarkPixelFraction);
        Assert.Equal(1, result.BrightPixelFraction);
        Assert.True(result.IsLikelyBlank);
    }

    [Fact]
    public void Analysis_CheckerboardHasKnownContrastVarianceAndExposure()
    {
        using var image = new Image<L8>(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                image[x, y] = new L8((byte)(((x + y) % 2) * 255));
            }
        }

        DocumentAnalysis result = Assert.Single(image.AnalyzeDocument());
        Assert.Equal(0.5, result.MeanLuminance, 10);
        Assert.Equal(0.5, result.LuminanceStandardDeviation, 10);
        Assert.Equal(1, result.Contrast);
        Assert.Equal(16, result.LaplacianVariance, 10);
        Assert.Equal(0.5, result.DarkPixelFraction);
        Assert.Equal(0.5, result.BrightPixelFraction);
        Assert.False(result.IsLikelyBlank);
    }

    [Fact]
    public void Analysis_TransparentInkIsCompositedOnWhiteAndSamplingIsBounded()
    {
        using var image = new Image<Rgba32>(1000, 800);
        DocumentAnalysis result = Assert.Single(image.AnalyzeDocument(32));
        Assert.Equal(32, result.SampleSize.Width);
        Assert.InRange(result.SampleSize.Height, 1, 32);
        Assert.Equal(1, result.MeanLuminance, 10);
        Assert.True(result.IsLikelyBlank);
        Assert.Equal(0, image[0, 0].A);
    }

    [Theory]
    [InlineData(DocumentPreprocessingProfile.NeuralOcr)]
    [InlineData(DocumentPreprocessingProfile.Screenshot)]
    public void PreservingProfiles_ClonePixelsAndRecordSkippedStages(DocumentPreprocessingProfile profile)
    {
        using var source = new Image<Rgba32>(7, 5);
        source[3, 2] = new Rgba32(32, 90, 170, 128);
        using DocumentPreprocessingResult<Rgba32> result = source.PreprocessDocument(new() { Profile = profile });
        Assert.Equal(source[3, 2], result.Image[3, 2]);
        DocumentPagePreprocessingResult page = Assert.Single(result.Pages);
        Assert.All(page.Stages.Where(stage => stage.Name != "Analyze"), stage =>
        {
            Assert.False(stage.Applied);
            Assert.Contains("Disabled", stage.Reason);
            Assert.True(stage.Elapsed >= TimeSpan.Zero);
        });
        Assert.True(page.Coordinates.TryMapToProcessed(new PointF(3.5f, 2.5f), out PointF mapped));
        Assert.Equal(3.5f, mapped.X);
        Assert.Equal(2.5f, mapped.Y);
        result.Image[3, 2] = Rgba32.White;
        Assert.Equal(new Rgba32(32, 90, 170, 128), source[3, 2]);
        result.Dispose();
        Assert.Throws<ObjectDisposedException>(() => result.Image[0, 0]);
        Assert.Equal(7, source.Width);
    }

    [Fact]
    public void OrientationPipeline_MapsActualPixelCentersAndLeavesSourceUnchanged()
    {
        using Image<L8> source = DocumentFixtures.LoadGray("text_page_rot90.png");
        byte[] original = DocumentFixtures.Plane(source);
        using DocumentPreprocessingResult<L8> result = source.PreprocessDocument(new() { AutoOrient = true });
        DocumentPagePreprocessingResult page = Assert.Single(result.Pages);
        Assert.True(page.Stages.Single(stage => stage.Name == "Orient").Applied);
        Assert.Equal(500, result.Image.Width);
        Assert.Equal(700, result.Image.Height);
        foreach (PointF input in new[] { new PointF(0.5f, 0.5f), new PointF(130.5f, 75.5f), new PointF(699.5f, 499.5f) })
        {
            Assert.True(page.Coordinates.TryMapToProcessed(input, out PointF output));
            Assert.Equal(source[(int)input.X, (int)input.Y], result.Image[(int)output.X, (int)output.Y]);
            Assert.True(page.Coordinates.TryMapToSource(output, out PointF restored));
            Assert.Equal(input.X, restored.X, 3);
            Assert.Equal(input.Y, restored.Y, 3);
        }

        Assert.Equal(original, DocumentFixtures.Plane(source));
        Assert.False(page.Coordinates.TryMapToProcessed(new PointF(float.NaN, 0), out _));
    }

    [Fact]
    public void PhonePipeline_MapsDetectedQuadToOutputRectangle()
    {
        using Image<L8> source = DocumentFixtures.LoadGray("perspective_page.png");
        PointF[] quad = source.DetectPage()!;
        using DocumentPreprocessingResult<L8> result = source.PreprocessDocument(new()
        {
            Profile = DocumentPreprocessingProfile.PhoneDocument,
            AutoOrient = false,
            NormalizeBackground = false,
        });
        DocumentPagePreprocessingResult page = Assert.Single(result.Pages);
        Assert.True(page.Stages.Single(stage => stage.Name == "Perspective").Applied);
        PointF[] expected = [new(0, 0), new(result.Image.Width, 0), new(result.Image.Width, result.Image.Height), new(0, result.Image.Height)];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(page.Coordinates.TryMapToProcessed(quad[i], out PointF mapped));
            Assert.Equal(expected[i].X, mapped.X, 3);
            Assert.Equal(expected[i].Y, mapped.Y, 3);
            Assert.True(page.Coordinates.TryMapToSource(mapped, out PointF restored));
            Assert.Equal(quad[i].X, restored.X, 3);
            Assert.Equal(quad[i].Y, restored.Y, 3);
        }
    }

    [Fact]
    public void ScannedProfile_BinarizesAndBlankGeometryAbstains()
    {
        using Image<L8> source = DocumentPages.Blank(40, 30);
        using DocumentPreprocessingResult<L8> result = source.PreprocessDocument(new() { Profile = DocumentPreprocessingProfile.ScannedDocument });
        DocumentPagePreprocessingResult page = Assert.Single(result.Pages);
        Assert.True(page.Stages.Single(stage => stage.Name == "Binarize").Applied);
        Assert.False(page.Stages.Single(stage => stage.Name == "Orient").Applied);
        Assert.False(page.Stages.Single(stage => stage.Name == "Deskew").Applied);
        Assert.Contains("uncertain", page.Stages.Single(stage => stage.Name == "Orient").Reason);
        Assert.All(DocumentFixtures.Plane(result.Image), value => Assert.True(value == 0 || value == 255));
    }

    [Fact]
    public void Pipeline_ProcessesMixedFramesIndependently()
    {
        using Image<L8> source = DocumentPages.Blank(30, 20);
        using Image<L8> second = DocumentFixtures.LoadGray("text_page_rot180.png");
        source.Frames.AddFrame(second.Frames.RootFrame);
        using DocumentPreprocessingResult<L8> result = source.PreprocessDocument(new() { AutoOrient = true });
        Assert.Equal(2, result.Pages.Count);
        Assert.False(result.Pages[0].Stages.Single(stage => stage.Name == "Orient").Applied);
        Assert.True(result.Pages[1].Stages.Single(stage => stage.Name == "Orient").Applied);
        Assert.Equal(30, result.Pages[0].OutputSize.Width);
        Assert.Equal(500, result.Pages[1].OutputSize.Width);
        using Image<L8> secondResult = result.Image.Frames.CloneFrame(1);
        Assert.Equal(RotateMode.None, secondResult.DetectOrientation().Rotation);
    }

    [Fact]
    public void Pipeline_RejectsInvalidSettingsAndCancellationWithoutMutatingSource()
    {
        using Image<L8> source = DocumentPages.Blank(30, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.PreprocessDocument(new() { MinimumOrientationConfidence = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.PreprocessDocument(new() { MaximumSkewAngle = float.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.PreprocessDocument(new() { MinimumSkewAngle = 20 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.AnalyzeDocument(7));
        Assert.Throws<OperationCanceledException>(() => source.PreprocessDocument(cancellationToken: new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => source.PreprocessDocument(new() { ExecutionOptions = new() { CancellationToken = new CancellationToken(true) } }));
        Assert.Throws<ImageSizeLimitExceededException>(() => source.PreprocessDocument(new() { ExecutionOptions = new() { MaxPixelBufferBytes = 1 } }));
        Assert.Equal(255, source[0, 0].PackedValue);
    }

    [Fact]
    public void CoordinateMap_ComposesRotationAndProjectiveTransforms()
    {
        var map = new DocumentCoordinateMap();
        map.AddRotation(12, new Size(200, 100), new Size(217, 140));
        PointF[] quad = [new(10, 4), new(190, 7), new(195, 90), new(6, 94)];
        PointF[] rectangle = [new(0, 0), new(180, 0), new(180, 90), new(0, 90)];
        map.Add(PerspectiveWarp.SolveHomography(quad, rectangle), PerspectiveWarp.SolveHomography(rectangle, quad));
        Assert.True(map.TryMapToProcessed(new PointF(90.5f, 42.5f), out PointF mapped));
        Assert.True(map.TryMapToSource(mapped, out PointF restored));
        Assert.Equal(90.5f, restored.X, 3);
        Assert.Equal(42.5f, restored.Y, 3);
    }
}
