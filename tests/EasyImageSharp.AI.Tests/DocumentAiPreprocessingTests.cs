using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.AI.Tests;

public sealed class DocumentAiPreprocessingTests
{
    [Fact]
    public async Task Orientation_PreservesSourceAndMapsPixelCenters()
    {
        using ImageAiSession ai = CreateSession("doc-orientation", TestModels.ClassifierFixed);
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(75, 51);
        using Image<Rgb24> original = source.Clone();
        using DocumentPreprocessingResult<Rgb24> result = await source.PreprocessDocumentAIAsync(ai);
        Assert.Equal(51, result.Image.Width);
        Assert.Equal(75, result.Image.Height);
        Assert.True(TestImages.PixelsEqual(source, original));
        IDocumentCoordinateMap map = result.Pages[0].Coordinates;
        PointF input = new(13.5f, 17.5f);
        Assert.True(map.TryMapToProcessed(input, out PointF mapped));
        Assert.Equal(source[13, 17], result.Image[(int)mapped.X, (int)mapped.Y]);
        Assert.True(map.TryMapToSource(mapped, out PointF restored));
        Assert.Equal(input.X, restored.X, 3);
        Assert.Equal(input.Y, restored.Y, 3);
    }

    [Fact]
    public async Task LowModelConfidence_AbstainsWithRecordedReason()
    {
        using ImageAiSession ai = CreateSession("doc-orientation", TestModels.ClassifierFixed);
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(75, 51);
        using DocumentPreprocessingResult<Rgb24> result = await source.PreprocessDocumentAIAsync(ai, new() { MinimumOrientationConfidence = 1 });
        Assert.True(TestImages.PixelsEqual(source, result.Image));
        DocumentPreprocessingStage stage = result.Pages[0].Stages.Single(s => s.Name == "AiOrient");
        Assert.False(stage.Applied);
        Assert.Contains("below threshold", stage.Reason);
    }

    [Fact]
    public async Task ImageDewarp_UsesFullResolutionBgrAndReportsUnavailableGeometry()
    {
        using ImageAiSession ai = CreateSession("doc-dewarp", TestModels.Get("first_channel_image.onnx"));
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(97, 73);
        using DocumentPreprocessingResult<Rgb24> result = await source.PreprocessDocumentAIAsync(ai, new() { AutoOrient = false, Dewarp = true });
        Assert.Equal(source.Width, result.Image.Width);
        Assert.Equal(source.Height, result.Image.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(new Rgb24(0, 0, source[x, y].B), result.Image[x, y]);
            }
        }

        Assert.False(result.Pages[0].Coordinates.TryMapToSource(new PointF(20, 20), out _));
        Assert.False(result.Pages[0].Coordinates.TryMapToProcessed(new PointF(20, 20), out _));
        Assert.Contains("unavailable", result.Pages[0].Stages.Single(stage => stage.Name == "AiDewarp").Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamplingGrid_WarpsOriginalDetailAndExposesOnlyBackwardMap(bool flip)
    {
        using ImageAiSession ai = CreateSession("doc-dewarp", TestModels.Get(flip ? "document_flip_grid.onnx" : "document_identity_grid.onnx"));
        using Image<Rgba64> source = TestImages.Noise<Rgba64>(101, 69);
        // A non-byte-aligned component verifies that the warp does not round-trip through Rgba32.
        source[33, 21] = new Rgba64(12345, 23456, 34567, 65535);
        using DocumentPreprocessingResult<Rgba64> result = await source.PreprocessDocumentAIAsync(ai, new()
        {
            AutoOrient = false,
            Dewarp = true,
            DewarpContract = new()
            {
                OutputKind = DocumentDewarpOutputKind.NormalizedSamplingGrid,
                FixedInputSize = new Size(8, 8),
                SizeMultiple = 1,
            },
        });
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source[flip ? source.Width - x - 1 : x, y], result.Image[x, y]);
            }
        }

        Assert.True(result.Pages[0].Coordinates.TryMapToSource(new PointF(10.5f, 20.5f), out PointF mapped));
        Assert.Equal(flip ? 90.5f : 10.5f, mapped.X, 3);
        Assert.Equal(20.5f, mapped.Y, 3);
        Assert.False(result.Pages[0].Coordinates.TryMapToProcessed(mapped, out _));
    }

    [Fact]
    public async Task ExplicitRgbContract_IsHonored()
    {
        using ImageAiSession ai = CreateSession("doc-dewarp", TestModels.Get("first_channel_image.onnx"));
        using var image = new Image<Rgb24>(17, 13, new Rgb24(50, 100, 200));
        await image.DewarpDocumentWithContractAsync(ai, new() { Channels = TensorChannels.Rgb });
        Assert.Equal(new Rgb24(50, 0, 0), image[0, 0]);
    }

    [Fact]
    public void ExistingDewarp_AlsoUsesCorrectDynamicBgrContract()
    {
        using ImageAiSession ai = CreateSession("doc-dewarp", TestModels.Get("first_channel_image.onnx"));
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(71, 47);
        using Image<Rgb24> result = source.Clone();
        result.DewarpDocument(ai);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(new Rgb24(0, 0, source[x, y].B), result[x, y]);
            }
        }
    }

    [Fact]
    public async Task ImageModelRunner_BgrContractNormalizesInModelOrder()
    {
        using var ai = new ImageAiSession(new() { Offline = true, ExecutionProvider = ExecutionProvider.Cpu });
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(19, 13);
        var normalization = new TensorNormalization { Mean = [0.1f, 0.2f, 0.3f], Std = [0.3f, 0.4f, 0.5f] };
        using Image<Rgb24> result = await ai.RunImageToImageAsync(TestModels.IdentityRgb, source, new()
        {
            UseBgrChannelOrder = true,
            InputNormalization = normalization,
            OutputNormalization = normalization,
        });
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public async Task RequestedMissingModel_DoesNotSilentlySkip()
    {
        using var cache = new TempCache();
        using var ai = new ImageAiSession(new() { Offline = true, CachePath = cache.Path });
        using var source = new Image<Rgb24>(20, 10);
        await Assert.ThrowsAsync<OfflineModelMissingException>(() => source.PreprocessDocumentAIAsync(ai, new() { AutoOrient = false, Dewarp = true }));
        Assert.Equal(20, source.Width);
    }

    [Fact]
    public async Task ExecutionSettings_CancelBeforeModelResolutionAndBoundClones()
    {
        using var cache = new TempCache();
        using var ai = new ImageAiSession(new() { Offline = true, CachePath = cache.Path });
        using var source = new Image<Rgb24>(20, 10);
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.PreprocessDocumentAIAsync(ai, new()
        {
            Preprocessing = new() { ExecutionOptions = new() { CancellationToken = new CancellationToken(true) } },
        }));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => source.PreprocessDocumentAIAsync(ai, new()
        {
            AutoOrient = false,
            Preprocessing = new() { ExecutionOptions = new() { MaxPixelBufferBytes = 1 } },
        }));
        Assert.Equal(0, ai.LoadedSessionCount);
    }

    [Fact]
    public async Task WrongExportContractAndBudget_AreRejected()
    {
        using ImageAiSession ai = CreateSession("doc-dewarp", TestModels.IdentityRgb);
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(19, 13);
        await Assert.ThrowsAsync<ModelContractException>(() => source.PreprocessDocumentAIAsync(ai, new()
        {
            AutoOrient = false, Dewarp = true,
            DewarpContract = new() { OutputKind = DocumentDewarpOutputKind.NormalizedSamplingGrid, SizeMultiple = 1 },
        }));
        await Assert.ThrowsAsync<ModelContractException>(() => source.PreprocessDocumentAIAsync(ai, new()
        {
            AutoOrient = false, Dewarp = true, DewarpContract = new() { MaximumInputPixels = 1 },
        }));
        await Assert.ThrowsAsync<ModelContractException>(() => source.PreprocessDocumentAIAsync(ai, new()
        {
            AutoOrient = false, Dewarp = true, DewarpContract = new() { OutputName = "missing-output" },
        }));
    }

    private static ImageAiSession CreateSession(string name, string path)
    {
        var options = new ImageAiOptions { Offline = true, ExecutionProvider = ExecutionProvider.Cpu };
        options.ModelPathOverrides[name] = path;
        return new ImageAiSession(options);
    }
}
