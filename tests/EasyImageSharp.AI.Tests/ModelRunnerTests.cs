using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Preprocessing / postprocessing round trips through the core tensor bridges, overlapped tiling, size-multiple
/// padding and scale handling, driven by the tiny deterministic ONNX graphs in <c>Models/</c>. An identity
/// network must give back exactly the pixels it was fed.
/// </summary>
public class ModelRunnerTests
{
    private static ImageModelContract Rgb(int tileSize = 0, int overlap = 16, int sizeMultiple = 1) => new()
    {
        InputChannels = 3,
        InputNormalization = TensorNormalization.Unit,
        OutputNormalization = TensorNormalization.Unit,
        TileSize = tileSize,
        TileOverlap = overlap,
        SizeMultiple = sizeMultiple,
    };

    private static ImageModelContract Gray(int sizeMultiple = 1) => new()
    {
        InputChannels = 1,
        InputNormalization = TensorNormalization.Unit,
        OutputNormalization = TensorNormalization.Unit,
        SizeMultiple = sizeMultiple,
    };

    // ----- Identity round trips -----

    [Fact]
    public void Identity_ReproducesAnRgb24ImageExactly()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(29, 23);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb());

        Assert.Equal(source.Width, result.Width);
        Assert.Equal(source.Height, result.Height);
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public void Identity_ReproducesAnOpaqueRgba32ImageExactly()
    {
        using var ai = new ImageAiSession();
        using Image<Rgba32> source = TestImages.Noise<Rgba32>(24, 18);
        using Image<Rgba32> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb());

        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public void Identity_ReproducesAnL8ImageExactly()
    {
        using var ai = new ImageAiSession();
        using Image<L8> source = TestImages.Noise<L8>(31, 17);
        using Image<L8> result = ai.RunImageToImage(TestModels.IdentityGray, source, Gray());

        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public void Identity_ReproducesASmoothGradientExactly()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Gradient<Rgb24>(40, 30);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb());

        Assert.True(TestImages.PixelsEqual(source, result));
    }

    /// <summary>A 1x1 image still has to make it through the whole tensor path.</summary>
    [Fact]
    public void Identity_HandlesASinglePixelImage()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(1, 1);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb());

        Assert.True(TestImages.PixelsEqual(source, result));
    }

    // ----- Tiling -----

    [Theory]
    [InlineData(64, 64, 32, 8)]    // exact multiples of the tile
    [InlineData(70, 53, 32, 8)]    // neither dimension is a multiple
    [InlineData(33, 33, 16, 4)]    // odd sizes
    [InlineData(100, 20, 32, 16)]  // wide and short: one tile row
    [InlineData(20, 100, 32, 16)]  // tall and narrow: one tile column
    [InlineData(65, 65, 32, 0)]    // no overlap at all
    public void Identity_WithTiling_ReproducesTheImageExactly(int width, int height, int tile, int overlap)
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(width, height, seed: width * 31 + height);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb(tile, overlap));

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    /// <summary>An image no larger than one tile skips the tiling path entirely but must agree with it.</summary>
    [Fact]
    public void Identity_TiledAndUntiled_AgreeOnASmallImage()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(20, 20);
        using Image<Rgb24> tiled = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb(tileSize: 32, overlap: 8));
        using Image<Rgb24> whole = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb());

        Assert.True(TestImages.PixelsEqual(tiled, whole));
        Assert.True(TestImages.PixelsEqual(tiled, source));
    }

    // ----- Size-multiple padding -----

    [Theory]
    [InlineData(13, 11, 8)]
    [InlineData(16, 16, 16)]
    [InlineData(37, 5, 32)]
    public void Identity_WithSizeMultiplePadding_CropsBackToTheSourceSize(int width, int height, int multiple)
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(width, height, seed: multiple + width);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb(sizeMultiple: multiple));

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    // ----- Scale -----

    [Fact]
    public void Upscaler_DoublesTheDimensions()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(15, 11);
        using Image<Rgb24> result = ai.RunImageToImage(
            TestModels.Upscale2xNearest, source, Rgb() with { ScaleFactor = 2 });

        Assert.Equal(30, result.Width);
        Assert.Equal(22, result.Height);
    }

    /// <summary>The graph is a nearest/floor resize, so every source pixel must appear as an exact 2x2 block.</summary>
    [Fact]
    public void Upscaler_ReplicatesEveryPixelIntoATwoByTwoBlock()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(12, 9);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.Upscale2xNearest, source, Rgb());

        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgb24> row = source.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    Span<Rgb24> target = result.Frames.RootFrame.GetRowSpan((2 * y) + dy);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        Assert.Equal(row[x], target[(2 * x) + dx]);
                    }
                }
            }
        }
    }

    [Fact]
    public void Upscaler_WithTiling_StillDoublesEveryTile()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(70, 40);
        using Image<Rgb24> tiled = ai.RunImageToImage(
            TestModels.Upscale2xNearest, source, Rgb(tileSize: 32, overlap: 8));
        using Image<Rgb24> whole = ai.RunImageToImage(TestModels.Upscale2xNearest, source, Rgb());

        Assert.Equal(140, tiled.Width);
        Assert.Equal(80, tiled.Height);
        Assert.True(TestImages.PixelsEqual(tiled, whole));
    }

    [Fact]
    public void DeclaredScaleFactor_ThatTheModelDoesNotHonour_Throws()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(10, 10);

        ModelContractException error = Assert.Throws<ModelContractException>(
            () => ai.RunImageToImage(TestModels.Upscale2xNearest, source, Rgb() with { ScaleFactor = 4 }));
        Assert.Contains("ScaleFactor", error.Message, StringComparison.Ordinal);
    }

    // ----- Residual outputs -----

    [Fact]
    public void ResidualModel_PredictingZeroNoise_ReturnsTheInputLuminance()
    {
        using var ai = new ImageAiSession();
        using Image<L8> source = TestImages.Noise<L8>(21, 17);
        using Image<L8> result = ai.RunImageToImage(
            TestModels.ResidualZeroGray, source, Gray() with { OutputKind = ImageModelOutputKind.Residual });

        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public void ResidualModel_PredictingHalfGray_SubtractsItFromTheInput()
    {
        using var ai = new ImageAiSession();
        using Image<L8> source = TestImages.Noise<L8>(16, 12, seed: 99);
        using Image<L8> result = ai.RunImageToImage(
            TestModels.ConstantHalfGray, source, Gray() with { OutputKind = ImageModelOutputKind.Residual });

        // result = input - 0.5 in tensor space, clamped to 0 when the input is darker than mid grey.
        for (int y = 0; y < source.Height; y++)
        {
            Span<L8> from = source.Frames.RootFrame.GetRowSpan(y);
            Span<L8> to = result.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                int expected = Math.Clamp((int)Math.Round((from[x].PackedValue / 255f - 0.5f) * 255f), 0, 255);
                Assert.InRange(to[x].PackedValue - expected, -1, 1);
            }
        }
    }

    // ----- Fixed input size -----

    [Fact]
    public void FixedInputSize_StretchesToTheNetworkSizeAndBack()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Gradient<Rgb24>(40, 30);
        using Image<Rgb24> result = ai.RunImageToImage(
            TestModels.IdentityRgb, source, Rgb() with { FixedInputSize = new Size(32, 24), ScaleFactor = 1 });

        Assert.Equal(40, result.Width);
        Assert.Equal(30, result.Height);
    }

    // ----- Contract validation -----

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(4)]
    public void Contract_RejectsAnUnsupportedChannelCount(int channels)
        => Assert.Throws<ArgumentException>(() => new ImageModelContract { InputChannels = channels }.Validate());

    [Fact]
    public void Contract_RejectsOtherOutOfRangeFields()
    {
        Assert.Throws<ArgumentException>(() => new ImageModelContract { ScaleFactor = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new ImageModelContract { TileSize = 4 }.Validate());
        Assert.Throws<ArgumentException>(() => new ImageModelContract { TileSize = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new ImageModelContract { TileOverlap = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new ImageModelContract { SizeMultiple = 0 }.Validate());
        Assert.Throws<ArgumentException>(
            () => new ImageModelContract { FixedInputSize = new Size(0, 10) }.Validate());
    }

    [Fact]
    public void Contract_AcceptsTheDefaults()
    {
        var contract = new ImageModelContract();
        contract.Validate();

        Assert.Equal(3, contract.InputChannels);
        Assert.Equal(16, contract.TileOverlap);
        Assert.Equal(1, contract.SizeMultiple);
        Assert.Equal(ImageModelOutputKind.Image, contract.OutputKind);
        Assert.Equal(0, contract.TileSize);
    }

    [Fact]
    public void RunImageToImage_RejectsNullArguments()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> image = TestImages.Noise<Rgb24>(4, 4);

        Assert.Throws<ArgumentNullException>(() => ai.RunImageToImage(TestModels.IdentityRgb, (Image<Rgb24>)null!, Rgb()));
        Assert.Throws<ArgumentNullException>(() => ai.RunImageToImage(TestModels.IdentityRgb, image, null!));
    }

    [Fact]
    public void RunImageToImage_WithAMissingFile_Throws()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> image = TestImages.Noise<Rgb24>(4, 4);

        Assert.Throws<FileNotFoundException>(
            () => ai.RunImageToImage(Path.Combine(AppContext.BaseDirectory, "no_such_model.onnx"), image, Rgb()));
    }

    // ----- IImageModel plumbing -----

    [Fact]
    public void LocalImageModel_RunsThroughTheSession()
    {
        using var ai = new ImageAiSession();
        var model = new LocalImageModel(TestModels.IdentityRgb, Rgb(), "identity");
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(18, 14);
        using Image<Rgb24> result = ai.RunImageToImage(model, source);

        Assert.Equal("identity", model.Name);
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    [Fact]
    public async Task LocalImageModel_RejectsAMissingFile()
    {
        var model = new LocalImageModel(Path.Combine(AppContext.BaseDirectory, "missing.onnx"), Rgb());
        using var hub = new ModelHub();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => model.ResolveModelPathAsync(hub, CancellationToken.None));
    }

    [Fact]
    public void TensorNormalization_HasTheDocumentedPresets()
    {
        Assert.Equal(0f, TensorNormalization.Unit.Mean[0]);
        Assert.Equal(1f, TensorNormalization.Unit.Std[0]);
        Assert.Equal(0.5f, TensorNormalization.Symmetric.Mean[0]);
        Assert.Equal(1f / 255f, TensorNormalization.Byte.Std[0]);
        Assert.Equal(3, TensorNormalization.ImageNet.Mean.Length);
        Assert.Equal(0.485f, TensorNormalization.ImageNet.Mean[0]);
    }
}
