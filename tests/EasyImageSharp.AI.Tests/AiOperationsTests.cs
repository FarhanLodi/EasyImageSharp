using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// The built-in operations driven end to end through their public extension methods. Each registry model is
/// redirected to a tiny stand-in graph with <see cref="ImageAiOptions.ModelPathOverrides"/>, so the whole file
/// runs offline with no download and no GPU.
/// </summary>
public class AiOperationsTests
{
    /// <summary>A session whose registry models are replaced by the local test graphs.</summary>
    private static ImageAiSession SessionWith(params (string Name, string Path)[] overrides)
    {
        var options = new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu, Offline = true };
        foreach ((string name, string path) in overrides)
        {
            options.ModelPathOverrides[name] = path;
        }

        return new ImageAiSession(options);
    }

    // ----- Orientation logits to RotateMode -----

    [Theory]
    [InlineData(0, 0, RotateMode.None)]
    [InlineData(1, 90, RotateMode.Rotate270)]
    [InlineData(2, 180, RotateMode.Rotate180)]
    [InlineData(3, 270, RotateMode.Rotate90)]
    public void Scores_MapToTheRotationThatUprightsThePage(int winner, int expectedAngle, RotateMode expectedCorrection)
    {
        var scores = new float[4];
        scores[winner] = 5f;

        OrientationResult result = OrientationResult.FromScores(scores);

        Assert.Equal(expectedAngle, result.DetectedAngle);
        Assert.Equal(expectedCorrection, result.Correction);
        Assert.Equal(expectedCorrection, OrientationResult.CorrectionFor(winner));
        Assert.Equal(winner == 0, result.IsUpright);
    }

    [Fact]
    public void RawLogits_AreSoftMaxedIntoProbabilities()
    {
        OrientationResult result = OrientationResult.FromScores([0.1f, 2.0f, 0.5f, -1.0f]);

        Assert.Equal(90, result.DetectedAngle);
        Assert.Equal(RotateMode.Rotate270, result.Correction);
        Assert.Equal(1f, result.Probabilities.Sum(), 4);
        Assert.All(result.Probabilities, p => Assert.InRange(p, 0f, 1f));
        Assert.Equal(result.Probabilities.Max(), result.Confidence);
        Assert.True(result.Confidence > 0.5f);
    }

    [Fact]
    public void ScoresThatAreAlreadyProbabilities_AreUsedAsIs()
    {
        OrientationResult result = OrientationResult.FromScores([0.1f, 0.2f, 0.6f, 0.1f]);

        Assert.Equal(180, result.DetectedAngle);
        Assert.Equal(0.6f, result.Confidence, 5);
    }

    [Fact]
    public void TooFewScores_AreRejected()
    {
        Assert.Throws<ModelContractException>(() => OrientationResult.FromScores([1f, 2f, 3f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrientationResult.CorrectionFor(4));
    }

    [Fact]
    public void ClassAngles_AreTheDocumentedQuarterTurns()
        => Assert.Equal([0, 90, 180, 270], OrientationResult.ClassAngles);

    // ----- Orientation through a model -----

    [Fact]
    public void DetectOrientation_ReadsTheClassifierLogits()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierFixed));
        using Image<Rgb24> page = TestImages.CornerPage(120, 90, corner: 0);

        // The stand-in classifier always answers [0.1, 2.0, 0.5, -1.0]: class 1, i.e. 90 degrees clockwise.
        OrientationResult result = page.DetectOrientation(ai);

        Assert.Equal(90, result.DetectedAngle);
        Assert.Equal(RotateMode.Rotate270, result.Correction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DetectOrientation_ClassifiesTheBrightCorner(int corner)
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(160, 120, corner);

        OrientationResult result = page.DetectOrientation(ai);

        Assert.Equal(corner * 90, result.DetectedAngle);
        Assert.Equal(OrientationResult.CorrectionFor(corner), result.Correction);
    }

    /// <summary>
    /// The stand-in classifier says the page is rotated by however far its bright corner sits from top-left, so
    /// after AutoOrient that corner has to be back at the top left and the page must look upright.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AutoOrient_AppliesTheLosslessRotation(int corner)
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(180, 120, corner);

        RotateMode applied = page.AutoOrient(ai);

        Assert.Equal(OrientationResult.CorrectionFor(corner), applied);
        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
        Assert.Equal(RotateMode.None, page.DetectOrientation(ai).Correction);

        // A quarter turn swaps the dimensions; 0 and 180 keep them.
        bool quarterTurn = applied is RotateMode.Rotate90 or RotateMode.Rotate270;
        Assert.Equal(quarterTurn ? 120 : 180, page.Width);
        Assert.Equal(quarterTurn ? 180 : 120, page.Height);
    }

    [Fact]
    public void AutoOrient_OnAnAlreadyUprightPage_ChangesNothing()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(150, 100, corner: 0);
        using Image<Rgb24> original = page.Clone();

        RotateMode applied = page.AutoOrient(ai);

        Assert.Equal(RotateMode.None, applied);
        Assert.True(TestImages.PixelsEqual(original, page));
    }

    /// <summary>The rotation must be the lossless quarter turn, not a resample: every pixel value is preserved.</summary>
    [Fact]
    public void AutoOrient_IsLossless()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(160, 100, corner: 2);
        using Image<Rgb24> original = page.Clone();

        page.AutoOrient(ai);
        using Image<Rgb24> undone = page.Clone(ctx => ctx.Rotate(RotateMode.Rotate180));

        Assert.True(TestImages.PixelsEqual(original, undone));
    }

    [Fact]
    public async Task DetectOrientationAsync_MatchesTheSynchronousResult()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(140, 110, corner: 3);

        OrientationResult sync = page.DetectOrientation(ai);
        OrientationResult async = await page.DetectOrientationAsync(ai);

        Assert.Equal(sync.DetectedAngle, async.DetectedAngle);
        Assert.Equal(sync.Correction, async.Correction);
    }

    [Fact]
    public void OrientationOperations_RejectNullArguments()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierFixed));
        using Image<Rgb24> page = TestImages.CornerPage(40, 40, corner: 0);

        Assert.Throws<ArgumentNullException>(() => ((Image<Rgb24>)null!).DetectOrientation(ai));
        Assert.Throws<ArgumentNullException>(() => page.DetectOrientation(null!));
    }

    // ----- Super-resolution -----

    [Fact]
    public void Upscale_DoublesTheImageWithTheTwoTimesGraph()
    {
        using ImageAiSession ai = SessionWith(("super-resolution-x4", TestModels.Upscale2xNearest));
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(20, 15);
        using Image<Rgb24> page = source.Clone();

        page.Upscale(ai, factor: 2);

        Assert.Equal(40, page.Width);
        Assert.Equal(30, page.Height);
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgb24> from = source.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> to = page.Frames.RootFrame.GetRowSpan(2 * y);
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(from[x], to[2 * x]);
            }
        }
    }

    /// <summary>
    /// When the requested factor differs from the network scale the result is resampled to exactly the requested
    /// size, so a x2 graph still satisfies a x4 request.
    /// </summary>
    [Fact]
    public void Upscale_ResamplesWhenTheFactorDiffersFromTheNetworkScale()
    {
        using ImageAiSession ai = SessionWith(("super-resolution-x4", TestModels.Upscale2xNearest));
        using Image<Rgb24> page = TestImages.Gradient<Rgb24>(16, 12);

        page.Upscale(ai, factor: 4);

        Assert.Equal(64, page.Width);
        Assert.Equal(48, page.Height);
    }

    [Fact]
    public void Upscale_RejectsAFactorBelowOne()
    {
        using ImageAiSession ai = SessionWith(("super-resolution-x4", TestModels.Upscale2xNearest));
        using Image<Rgb24> page = TestImages.Noise<Rgb24>(8, 8);

        Assert.Throws<ArgumentOutOfRangeException>(() => page.Upscale(ai, factor: 0));
    }

    [Fact]
    public void Upscale_RewritesEveryFrame()
    {
        using ImageAiSession ai = SessionWith(("super-resolution-x4", TestModels.Upscale2xNearest));
        using var page = new Image<Rgb24>(10, 8);
        page.Frames.CreateFrame(10, 8);
        Assert.Equal(2, page.Frames.Count);

        page.Upscale(ai, factor: 2);

        Assert.Equal(2, page.Frames.Count);
        Assert.Equal(20, page.Width);
        Assert.Equal(16, page.Height);
        Assert.Equal(20, page.Frames[1].Width);
        Assert.Equal(16, page.Frames[1].Height);
    }

    // ----- Denoise -----

    [Fact]
    public void DenoiseAI_WithAZeroResidual_KeepsTheLuminance()
    {
        using ImageAiSession ai = SessionWith(("denoise-gray", TestModels.ResidualZeroGray));
        using Image<L8> source = TestImages.Noise<L8>(24, 18);
        using Image<L8> page = source.Clone();

        page.DenoiseAI(ai);

        Assert.True(TestImages.PixelsEqual(source, page));
    }

    [Fact]
    public void DenoiseAI_ProducesAGrayscaleResultFromColour()
    {
        using ImageAiSession ai = SessionWith(("denoise-gray", TestModels.ResidualZeroGray));
        using Image<Rgb24> page = TestImages.Noise<Rgb24>(20, 16);

        page.DenoiseAI(ai);

        for (int y = 0; y < page.Height; y++)
        {
            foreach (Rgb24 pixel in page.Frames.RootFrame.GetRowSpan(y).ToArray())
            {
                Assert.Equal(pixel.R, pixel.G);
                Assert.Equal(pixel.G, pixel.B);
            }
        }
    }

    // ----- Learned binarisation -----

    [Fact]
    public void BinarizeAI_ThresholdsAgainstTheModelsThresholdMap()
    {
        using ImageAiSession ai = SessionWith(("binarization", TestModels.ConstantHalfGray));
        using Image<L8> source = TestImages.Noise<L8>(28, 20);
        using Image<L8> page = source.Clone();

        page.BinarizeAI(ai);

        for (int y = 0; y < source.Height; y++)
        {
            Span<L8> from = source.Frames.RootFrame.GetRowSpan(y);
            Span<L8> to = page.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                // The stand-in model returns a flat 0.5 threshold, so white means luminance >= 127.5.
                byte expected = from[x].PackedValue / 255f >= 0.5f ? (byte)255 : (byte)0;
                Assert.Equal(expected, to[x].PackedValue);
            }
        }
    }

    // ----- Saliency and background removal -----

    [Fact]
    public void GetSaliencyMask_ReturnsAMaskAtTheImageSize()
    {
        using ImageAiSession ai = SessionWith(("saliency", TestModels.SaliencyBrightness));
        using Image<Rgb24> page = TestImages.CornerPage(120, 90, corner: 0);

        using Image<L8> mask = page.GetSaliencyMask(ai);

        Assert.Equal(page.Width, mask.Width);
        Assert.Equal(page.Height, mask.Height);

        // The stand-in model reports brightness as saliency, so the bright corner outscores the dark side.
        Assert.True(mask[10, 10].PackedValue > mask[110, 80].PackedValue);
    }

    [Fact]
    public void RemoveBackground_TurnsTheDarkBackgroundTransparentOnAnAlphaFormat()
    {
        using ImageAiSession ai = SessionWith(("saliency", TestModels.SaliencyBrightness));
        using Image<Rgba32> page = TestImages.CornerPage(120, 90, corner: 0).CloneAs<Rgba32>();

        page.RemoveBackground(ai);

        Assert.True(page[10, 10].A > page[110, 80].A);
    }

    [Fact]
    public void RemoveBackground_BlendsTowardsTheRequestedColourOnAnOpaqueFormat()
    {
        using ImageAiSession ai = SessionWith(("saliency", TestModels.SaliencyBrightness));
        using Image<Rgb24> page = TestImages.CornerPage(120, 90, corner: 0);

        page.RemoveBackground(ai, new BackgroundRemovalOptions { BackgroundColor = Color.Red });

        Rgb24 background = page[110, 80];
        Assert.True(background.R > background.G, "The background should have been pulled towards red.");
    }

    [Fact]
    public void BackgroundRemovalOptions_HaveTheDocumentedDefaults()
    {
        var options = new BackgroundRemovalOptions();
        Assert.Null(options.BackgroundColor);
        Assert.False(options.CropToForeground);
        Assert.Equal(0.5f, options.ForegroundThreshold);
        Assert.Equal(0, options.CropPadding);
        Assert.NotNull(BackgroundRemovalOptions.Default);
    }

    // ----- Offline behaviour without an override -----

    [Fact]
    public async Task AnOperationWithoutAModel_FailsOfflineRatherThanDownloading()
    {
        using var cache = new TempCache();
        using var ai = new ImageAiSession(new ImageAiOptions { Offline = true, CachePath = cache.Path });
        using Image<Rgb24> page = TestImages.Noise<Rgb24>(8, 8);

        await Assert.ThrowsAsync<OfflineModelMissingException>(() => page.DetectOrientationAsync(ai));
    }
}
