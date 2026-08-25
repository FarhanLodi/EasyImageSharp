using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// The <c>IImageProcessingContext</c> overloads, which reach the pipeline image reflectively and dispatch on its
/// runtime pixel type. Every built-in pixel format has to be routed correctly, and the operations have to
/// compose with the classical core operators inside one <c>Mutate</c> call.
/// </summary>
public class AiProcessingBridgeTests
{
    private static ImageAiSession SessionWith(params (string Name, string Path)[] overrides)
    {
        var options = new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu, Offline = true };
        foreach ((string name, string path) in overrides)
        {
            options.ModelPathOverrides[name] = path;
        }

        return new ImageAiSession(options);
    }

    [Fact]
    public void AutoOrient_WorksInsideAPipelineForRgb24()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgb24> page = TestImages.CornerPage(150, 100, corner: 1);

        page.Mutate(ctx => ctx.AutoOrient(ai));

        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
        Assert.Equal(100, page.Width);
        Assert.Equal(150, page.Height);
    }

    [Fact]
    public void AutoOrient_WorksForRgba32()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Rgba32> page = TestImages.CornerPage(150, 100, corner: 2).CloneAs<Rgba32>();

        page.Mutate(ctx => ctx.AutoOrient(ai));

        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
    }

    [Fact]
    public void AutoOrient_WorksForBgr24()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Bgr24> page = TestImages.CornerPage(150, 100, corner: 3).CloneAs<Bgr24>();

        page.Mutate(ctx => ctx.AutoOrient(ai));

        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
    }

    [Fact]
    public void AutoOrient_WorksForBgra32()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<Bgra32> page = TestImages.CornerPage(150, 100, corner: 2).CloneAs<Bgra32>();

        page.Mutate(ctx => ctx.AutoOrient(ai));

        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
    }

    [Fact]
    public void AutoOrient_WorksForL8()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierQuadrant));
        using Image<L8> page = TestImages.CornerPage(150, 100, corner: 1).CloneAs<L8>();

        page.Mutate(ctx => ctx.AutoOrient(ai));

        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
    }

    [Fact]
    public void BinarizeAI_WorksInsideAPipeline()
    {
        using ImageAiSession ai = SessionWith(("binarization", TestModels.ConstantHalfGray));
        using Image<L8> page = TestImages.Noise<L8>(24, 16);

        page.Mutate(ctx => ctx.BinarizeAI(ai));

        for (int y = 0; y < page.Height; y++)
        {
            foreach (L8 pixel in page.Frames.RootFrame.GetRowSpan(y).ToArray())
            {
                Assert.True(pixel.PackedValue is 0 or 255);
            }
        }
    }

    [Fact]
    public void DenoiseAI_WorksInsideAPipeline()
    {
        using ImageAiSession ai = SessionWith(("denoise-gray", TestModels.ResidualZeroGray));
        using Image<L8> source = TestImages.Noise<L8>(20, 14);
        using Image<L8> page = source.Clone();

        page.Mutate(ctx => ctx.DenoiseAI(ai));

        Assert.True(TestImages.PixelsEqual(source, page));
    }

    [Fact]
    public void Upscale_WorksInsideAPipelineAndResizesTheImage()
    {
        using ImageAiSession ai = SessionWith(("super-resolution-x4", TestModels.Upscale2xNearest));
        using Image<Rgb24> page = TestImages.Noise<Rgb24>(16, 12);

        page.Mutate(ctx => ctx.Upscale(ai, factor: 2));

        Assert.Equal(32, page.Width);
        Assert.Equal(24, page.Height);
    }

    [Fact]
    public void RemoveBackground_WorksInsideAPipeline()
    {
        using ImageAiSession ai = SessionWith(("saliency", TestModels.SaliencyBrightness));
        using Image<Rgba32> page = TestImages.CornerPage(120, 90, corner: 0).CloneAs<Rgba32>();

        page.Mutate(ctx => ctx.RemoveBackground(ai));

        Assert.True(page[10, 10].A > page[110, 80].A);
    }

    [Fact]
    public void DewarpDocument_WorksInsideAPipelineAndKeepsTheSize()
    {
        // The dewarp contract stretches to the network size and back, so an identity graph is a no-op resize.
        using ImageAiSession ai = SessionWith(("doc-dewarp", TestModels.IdentityRgb));
        using Image<Rgb24> page = TestImages.Gradient<Rgb24>(60, 44);

        page.Mutate(ctx => ctx.DewarpDocument(ai));

        Assert.Equal(60, page.Width);
        Assert.Equal(44, page.Height);
    }

    /// <summary>The point of the pipeline overloads: mixing learned and classical steps in one pass.</summary>
    [Fact]
    public void AiAndClassicalOperatorsCompose()
    {
        using ImageAiSession ai = SessionWith(
            ("doc-orientation", TestModels.ClassifierQuadrant),
            ("denoise-gray", TestModels.ResidualZeroGray));
        using Image<Rgb24> page = TestImages.CornerPage(160, 100, corner: 2);

        page.Mutate(ctx => ctx.AutoOrient(ai).DenoiseAI(ai).Grayscale());

        Assert.Equal(160, page.Width);
        Assert.Equal(100, page.Height);
        Assert.Equal(0, page.DetectOrientation(ai).DetectedAngle);
    }

    [Fact]
    public void PipelineOverloads_RejectNullArguments()
    {
        using ImageAiSession ai = SessionWith(("doc-orientation", TestModels.ClassifierFixed));
        using Image<Rgb24> page = TestImages.Noise<Rgb24>(8, 8);

        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.AutoOrient(null!)));
        Assert.Throws<ArgumentNullException>(() => page.Mutate(ctx => ctx.RemoveBackground(ai, null!)));
        Assert.Throws<ArgumentNullException>(() => AiProcessingExtensions.AutoOrient(null!, ai));
    }
}
