using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Every code sample in this package's README, transcribed verbatim apart from the wrapping method.
/// Nothing here runs — no model is downloaded and no session is created. The point is that the file must
/// keep compiling, so a rename or a signature change breaks the build instead of silently leaving the
/// published documentation wrong.
/// </summary>
public class ReadmeSamplesCompileTests
{
    [Fact]
    public void SamplesCompile()
    {
        // The compiler has already proved the point by the time this runs.
        Assert.True(true);
    }

    private static void QuickStart()
    {
        using var ai = new ImageAiSession();

        using Image<Rgb24> page = Image.Load<Rgb24>("phone-photo.jpg");

        page.AutoOrient(ai);
        page.DewarpDocument(ai);
        page.DenoiseAI(ai);
        page.Mutate(ctx => ctx.Deskew().SauvolaThreshold());

        page.SaveAsPng("clean.png");
    }

    private static void ModelPathOverride()
    {
        var options = new ImageAiOptions();
        options.ModelPathOverrides["super-resolution-x4"] = @"C:\models\realesrgan_general_x4v3.onnx";

        using var ai = new ImageAiSession(options);
    }

    private static void Configuration()
    {
        var options = new ImageAiOptions
        {
            ExecutionProvider = ExecutionProvider.Auto,
            Quantize = true,
            Offline = false,
            CachePath = null,
            AllowUnverifiedModels = false,
            IntraOpNumThreads = null,
            Log = Console.WriteLine,
        };

        using var ai = new ImageAiSession(options);
    }

    private static void Offline()
    {
        var options = new ImageAiOptions
        {
            CachePath = "/opt/myapp/models",
            Offline = true,
        };

        using var ai = new ImageAiSession(options);
    }

    private static void Operations(ImageAiSession ai, Image<Rgb24> image)
    {
        OrientationResult orientation = image.DetectOrientation(ai);
        _ = orientation;

        image.AutoOrient(ai);
        image.DewarpDocument(ai);
        image.Upscale(ai, factor: 4);
        image.DenoiseAI(ai);
        using Image<L8> mask = image.GetSaliencyMask(ai);
        image.BinarizeAI(ai);
    }
}
