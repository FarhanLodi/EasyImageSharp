using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Image embeddings, content moderation and learned quality driven end to end through their public extension methods,
/// with each registry model redirected to a tiny contract graph from <c>Models/gen_models.py</c> (offline, CPU).
/// </summary>
public class VisionModelOperationsTests
{
    private static readonly float[] ClipMean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] ClipStd = [0.26862954f, 0.26130258f, 0.27577711f];

    private static ImageAiSession SessionWith(string name, string modelFile)
    {
        var options = new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu, Offline = true };
        options.ModelPathOverrides[name] = TestModels.Get(modelFile);
        return new ImageAiSession(options);
    }

    /// <summary>The normalised channel-means embedding the contract graph returns for a solid colour.</summary>
    private static float[] ExpectedSolidEmbedding(byte r, byte g, byte b)
        => ImageEmbeddings.Normalize([(r / 255f - ClipMean[0]) / ClipStd[0], (g / 255f - ClipMean[1]) / ClipStd[1], (b / 255f - ClipMean[2]) / ClipStd[2], 1f]);

    /// <summary>A <paramref name="width"/> x <paramref name="height"/> image: <paramref name="outer"/> with a centred band of <paramref name="inner"/>.</summary>
    private static Image<Rgb24> Banded(int width, int height, int bandWidth, Rgb24 outer, Rgb24 inner)
    {
        var image = new Image<Rgb24>(width, height, outer);
        int start = (width - bandWidth) / 2;
        for (int y = 0; y < height; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).Slice(start, bandWidth).Fill(inner);
        }

        return image;
    }

    // ----- Embeddings -----

    [Fact]
    public void Embedding_IsUnitLengthAndSeesOnlyTheCentredSquare()
    {
        using ImageAiSession ai = SessionWith("image-embedding", "embedding_channel_means.onnx");

        // 448x224: the shorter side already is 224, so the transform is a pure centre crop of x = 112..335 (all blue).
        using Image<Rgb24> image = Banded(448, 224, 224, new Rgb24(255, 0, 0), new Rgb24(0, 0, 255));
        float[] embedding = image.GetImageEmbedding(ai);

        Assert.Equal(4, embedding.Length);
        Assert.Equal(1.0, Math.Sqrt(embedding.Sum(v => (double)v * v)), 5);
        float[] expected = ExpectedSolidEmbedding(0, 0, 255);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], embedding[i], 1e-4);
        }
    }

    [Fact]
    public void Embedding_OfAnExtremeAspectRatioStillUsesTheCentredSquare()
    {
        using ImageAiSession ai = SessionWith("image-embedding", "embedding_channel_means.onnx");

        // A 20000x10 strip would need a 448000x224 intermediate; the centred 10x10 square is cropped first instead.
        using Image<Rgb24> image = Banded(20000, 10, 40, new Rgb24(255, 0, 0), new Rgb24(0, 255, 0));
        float[] embedding = image.GetImageEmbedding(ai);

        float[] expected = ExpectedSolidEmbedding(0, 255, 0);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], embedding[i], 1e-4);
        }
    }

    [Fact]
    public async Task Embedding_AsyncMatchesSyncAndSimilarityRanksImages()
    {
        using ImageAiSession ai = SessionWith("image-embedding", "embedding_channel_means.onnx");
        using var red = new Image<Rgba32>(300, 200, new Rgba32(220, 30, 30));
        using var darkRed = new Image<Rgba32>(200, 300, new Rgba32(180, 20, 20));
        using var blue = new Image<Rgba32>(256, 256, new Rgba32(20, 40, 230));

        float[] redSync = red.GetImageEmbedding(ai);
        float[] redAsync = await red.GetImageEmbeddingAsync(ai);
        float[] darkRedEmbedding = darkRed.GetImageEmbedding(ai);
        float[] blueEmbedding = await blue.GetImageEmbeddingAsync(ai);

        // ONNX Runtime may reduce in a different order from run to run, so equal inputs agree to rounding, not bits.
        for (int i = 0; i < redSync.Length; i++)
        {
            Assert.Equal(redSync[i], redAsync[i], 1e-5);
        }

        Assert.Equal(1f, ImageEmbeddings.CosineSimilarity(redSync, redAsync), 5);
        Assert.True(ImageEmbeddings.CosineSimilarity(redSync, darkRedEmbedding) > ImageEmbeddings.CosineSimilarity(redSync, blueEmbedding));
    }

    [Fact]
    public async Task Embedding_HonoursCancellationAndRejectsNulls()
    {
        using ImageAiSession ai = SessionWith("image-embedding", "embedding_channel_means.onnx");
        using var image = new Image<Rgb24>(64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => image.GetImageEmbedding(ai, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image.GetImageEmbeddingAsync(ai, cts.Token));
        Assert.Throws<ArgumentNullException>(() => image.GetImageEmbedding(null!));
        Assert.Throws<ArgumentNullException>(() => ((Image<Rgb24>)null!).GetImageEmbedding(ai));
    }

    [Fact]
    public void Embedding_FromAModelWithTheWrongOutputShape_IsAContractError()
    {
        using ImageAiSession ai = SessionWith("image-embedding", "identity_rgb.onnx");
        using var image = new Image<Rgb24>(32, 32);

        Assert.Throws<ModelContractException>(() => image.GetImageEmbedding(ai));
    }

    [Fact]
    public void CosineSimilarity_CoversTheGeometricCases()
    {
        Assert.Equal(1f, ImageEmbeddings.CosineSimilarity([1f, 2f, 3f], [2f, 4f, 6f]), 6);
        Assert.Equal(-1f, ImageEmbeddings.CosineSimilarity([1f, 0f], [-3f, 0f]), 6);
        Assert.Equal(0f, ImageEmbeddings.CosineSimilarity([1f, 0f], [0f, 5f]), 6);
        Assert.Equal(0f, ImageEmbeddings.CosineSimilarity([0f, 0f], [1f, 1f]));
        Assert.Throws<ArgumentException>(() => ImageEmbeddings.CosineSimilarity([1f], [1f, 2f]));
        Assert.Throws<ArgumentException>(() => ImageEmbeddings.CosineSimilarity([], []));
    }

    [Fact]
    public void Normalize_ReturnsAUnitVectorAndRejectsDegenerateInput()
    {
        float[] unit = ImageEmbeddings.Normalize([3f, 4f]);

        Assert.Equal(0.6f, unit[0], 6);
        Assert.Equal(0.8f, unit[1], 6);
        Assert.Throws<ArgumentException>(() => ImageEmbeddings.Normalize([0f, 0f]));
        Assert.Throws<ArgumentException>(() => ImageEmbeddings.Normalize([]));
        Assert.Throws<ArgumentException>(() => ImageEmbeddings.Normalize([1f, float.NaN]));
    }

    // ----- Content moderation -----

    [Theory]
    [InlineData(255, ContentClassification.UnsafeLabel)]
    [InlineData(0, ContentClassification.SafeLabel)]
    public void Moderation_ReturnsSoftMaxedProbabilitiesInModelOrder(byte gray, string expectedTop)
    {
        using ImageAiSession ai = SessionWith("content-moderation", "moderation_brightness.onnx");
        using var image = new Image<Rgb24>(500, 380, new Rgb24(gray, gray, gray));

        ContentClassification result = image.ClassifyContent(ai);

        // The graph returns (m, -m) with m the mean of the -1..1 input: +-1 for white / black.
        float unsafeExpected = (float)(1 / (1 + Math.Exp(gray == 255 ? -2.0 : 2.0)));
        Assert.Equal(new[] { ContentClassification.UnsafeLabel, ContentClassification.SafeLabel }, result.Labels);
        Assert.Equal(unsafeExpected, result.UnsafeProbability, 4);
        Assert.Equal(1f, result.Probabilities.Sum(), 5);
        Assert.Equal(expectedTop, result.TopLabel);
        Assert.Equal(result.Probabilities.Max(), result.TopProbability);
        Assert.Equal(1f - unsafeExpected, result.GetProbability("sfw"), 4);
        Assert.Equal(gray == 255, result.IsUnsafe());
        Assert.True(result.IsUnsafe(0f));
        Assert.Contains("NSFW=", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moderation_ReadsClassNamesFromTheModelMetadata()
    {
        using ImageAiSession ai = SessionWith("content-moderation", "moderation_three_labels.onnx");
        using var image = new Image<Rgba32>(64, 64);

        ContentClassification result = await image.ClassifyContentAsync(ai);

        double e2 = Math.Exp(2);
        Assert.Equal(new[] { "neutral", "nsfw", "drawing" }, result.Labels);
        Assert.Equal((float)(e2 / (e2 + 2)), result.UnsafeProbability, 5);
        Assert.Equal("nsfw", result.TopLabel);
        Assert.Equal((float)(1 / (e2 + 2)), result.GetProbability("DRAWING"), 5);
    }

    [Fact]
    public void Moderation_RejectsAModelWithoutAnUnsafeClassAndBadArguments()
    {
        using ImageAiSession ai = SessionWith("content-moderation", "moderation_no_unsafe.onnx");
        using var image = new Image<Rgb24>(64, 64);

        Assert.Throws<ModelContractException>(() => image.ClassifyContent(ai));

        using ImageAiSession valid = SessionWith("content-moderation", "moderation_brightness.onnx");
        ContentClassification result = image.ClassifyContent(valid);
        Assert.Throws<ArgumentOutOfRangeException>(() => result.IsUnsafe(1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.IsUnsafe(float.NaN));
        Assert.Throws<ArgumentException>(() => result.GetProbability("violence"));
    }

    [Fact]
    public async Task Moderation_HonoursCancellation()
    {
        using ImageAiSession ai = SessionWith("content-moderation", "moderation_brightness.onnx");
        using var image = new Image<Rgb24>(64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => image.ClassifyContent(ai, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image.ClassifyContentAsync(ai, cts.Token));
    }

    // ----- Learned quality -----

    [Fact]
    public async Task Quality_ReportsTheMeanAndSpreadOfBothDistributions()
    {
        using ImageAiSession ai = SessionWith("quality-assessment", "quality_fixed.onnx");
        using Image<Rgb24> image = TestImages.Gradient<Rgb24>(640, 480);

        QualityAssessment sync = image.AssessQuality(ai);
        QualityAssessment result = await image.AssessQualityAsync(ai);

        Assert.Equal(5.5f, result.TechnicalScore, 4);
        Assert.Equal(0.5f, result.TechnicalStandardDeviation, 4);
        Assert.Equal(5.5f, result.AestheticScore, 4);
        Assert.Equal((float)Math.Sqrt(8.25), result.AestheticStandardDeviation, 4);
        Assert.Equal(10, result.TechnicalDistribution.Count);
        Assert.Equal(1f, result.AestheticDistribution.Sum(), 5);
        Assert.Equal(sync.TechnicalScore, result.TechnicalScore);
        Assert.Contains("Technical=", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_RejectsAModelWithoutBothHeads()
    {
        using ImageAiSession ai = SessionWith("quality-assessment", "quality_one_head.onnx");
        using var image = new Image<Rgb24>(64, 64);

        Assert.Throws<ModelContractException>(() => image.AssessQuality(ai));
    }

    [Fact]
    public void Quality_StretchesWithCentredNearestNeighbourSampling()
    {
        // Column x of the 448-wide source stores x / 2 in red; nearest sampling at floor((t + 0.5) * 2) = 2t + 1 picks t.
        using var image = new Image<Rgb24>(448, 100);
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgb24> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgb24((byte)(x / 2), (byte)y, 0);
            }
        }

        float[] tensor = QualityAssessmentRunner.NearestStretchTensor(image.Frames.RootFrame, 224, TensorNormalization.Unit);

        const int plane = 224 * 224;
        for (int t = 0; t < 224; t++)
        {
            Assert.Equal(t / 255f, tensor[t], 6);
        }

        // Rows: floor((t + 0.5) * 100 / 224).
        for (int t = 0; t < 224; t++)
        {
            int expectedRow = (int)Math.Floor((t + 0.5) * 100 / 224);
            Assert.Equal(expectedRow / 255f, tensor[plane + (t * 224)], 6);
        }
    }

    [Fact]
    public async Task Quality_HonoursCancellation()
    {
        using ImageAiSession ai = SessionWith("quality-assessment", "quality_fixed.onnx");
        using var image = new Image<Rgb24>(64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => image.AssessQuality(ai, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image.AssessQualityAsync(ai, cts.Token));
    }

    // ----- Registry entries -----

    [Fact]
    public void TheVisionModelsArePublishedWithTheirContracts()
    {
        foreach (ModelDescriptor descriptor in new[] { ModelRegistry.ImageEmbedding, ModelRegistry.Inpainting, ModelRegistry.ContentModeration, ModelRegistry.QualityAssessment })
        {
            Assert.True(descriptor.IsPublished, descriptor.Name);
            Assert.Contains(descriptor, ModelRegistry.All);
            Assert.Same(descriptor, ModelRegistry.Find(descriptor.Name));
            Assert.Equal(descriptor.Sha256, ModelRegistry.GetChecksum(descriptor.FileName));
            Assert.Equal(ModelRegistry.DefaultBaseUrl, descriptor.BaseUrl);
        }

        Assert.True(ModelRegistry.ImageEmbedding.HasPublishedInt8);
        Assert.Same(ModelRegistry.ImageEmbedding, ModelRegistry.Find("clip-vit-base-patch32-vision.int8.onnx"));
        Assert.Equal([1, 3, 224, 224], ModelRegistry.ImageEmbedding.InputShape);
        Assert.Equal(ClipMean, ModelRegistry.ImageEmbedding.Normalization.Mean);
        Assert.Equal(ClipStd, ModelRegistry.ImageEmbedding.Normalization.Std);
        Assert.Equal([1, 3, 512, 512], ModelRegistry.Inpainting.InputShape);
        Assert.Same(TensorNormalization.Symmetric, ModelRegistry.ContentModeration.Normalization);
        Assert.Same(TensorNormalization.Symmetric, ModelRegistry.QualityAssessment.Normalization);
        Assert.Equal("MIT", ModelRegistry.ImageEmbedding.License);
        Assert.Equal("Apache-2.0", ModelRegistry.Inpainting.License);
        Assert.Equal("Apache-2.0", ModelRegistry.ContentModeration.License);
        Assert.Equal("Apache-2.0", ModelRegistry.QualityAssessment.License);
    }
}
