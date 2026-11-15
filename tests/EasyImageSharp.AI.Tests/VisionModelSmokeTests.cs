using System.Diagnostics;
using System.Text.Json;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Opt-in smoke tests against the real published models. Each test runs only when its model file is available
/// locally (never downloaded): in the directory named by <c>EASYIMAGESHARP_REAL_MODELS</c> (for example the staging folder
/// of files about to be published) or already in the default model cache. The expected values come from the official
/// weights run by the reference scripts in <c>tools/ModelExport/VisionModels</c> on the same deterministic scene.
/// </summary>
public class VisionModelSmokeTests(ITestOutputHelper output)
{
    private static readonly Lazy<JsonElement> References = new(() =>
        JsonDocument.Parse(File.ReadAllText(TestModels.Get("vision_model_references.json"))).RootElement);

    private static float[] Reference(string name) => References.Value.GetProperty(name).EnumerateArray().Select(e => e.GetSingle()).ToArray();

    private static ImageAiSession SessionFor(ModelDescriptor descriptor)
    {
        var options = new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu, Offline = true };
        options.ModelPathOverrides[descriptor.Name] = RealModels.Find(descriptor)!;
        return new ImageAiSession(options);
    }

    [RealModelFact("image-embedding")]
    public void ClipEmbedding_MatchesTheOfficialWeights()
    {
        using ImageAiSession ai = SessionFor(ModelRegistry.ImageEmbedding);
        using Image<Rgb24> scene = RealModels.Scene();

        var timer = Stopwatch.StartNew();
        float[] embedding = scene.GetImageEmbedding(ai);
        output.WriteLine($"first embedding (incl. session load): {timer.ElapsedMilliseconds} ms");
        timer.Restart();
        scene.GetImageEmbedding(ai);
        output.WriteLine($"warm embedding: {timer.ElapsedMilliseconds} ms");

        float similarity = ImageEmbeddings.CosineSimilarity(embedding, Reference("clip_vit_b32_embedding"));
        output.WriteLine($"cosine similarity with the reference pipeline: {similarity:0.00000}");
        Assert.Equal(512, embedding.Length);
        Assert.True(similarity > 0.99f, $"cosine similarity {similarity}");
    }

    [RealModelFact("content-moderation")]
    public void Moderation_MatchesTheOfficialWeights()
    {
        using ImageAiSession ai = SessionFor(ModelRegistry.ContentModeration);
        using Image<Rgb24> scene = RealModels.Scene();

        ContentClassification result = scene.ClassifyContent(ai);
        float[] expected = Reference("nsfw_probabilities");
        output.WriteLine($"{result}; reference NSFW={expected[0]:0.0000}");

        Assert.Equal(new[] { "NSFW", "SFW" }, result.Labels);
        Assert.InRange(result.UnsafeProbability, expected[0] - 0.05f, expected[0] + 0.05f);
    }

    [RealModelFact("quality-assessment")]
    public void Quality_MatchesTheOfficialWeights()
    {
        using ImageAiSession ai = SessionFor(ModelRegistry.QualityAssessment);
        using Image<Rgb24> scene = RealModels.Scene();

        QualityAssessment result = scene.AssessQuality(ai);
        float technical = Reference("nima_technical").Select((p, i) => p * (i + 1)).Sum();
        float aesthetic = Reference("nima_aesthetic").Select((p, i) => p * (i + 1)).Sum();
        output.WriteLine($"{result}; reference technical={technical:0.0000}, aesthetic={aesthetic:0.0000}");

        Assert.InRange(result.TechnicalScore, technical - 0.02f, technical + 0.02f);
        Assert.InRange(result.AestheticScore, aesthetic - 0.02f, aesthetic + 0.02f);
    }

    [RealModelFact("inpainting")]
    public void Inpainting_ReplacesOnlyTheMaskedObject()
    {
        using ImageAiSession ai = SessionFor(ModelRegistry.Inpainting);
        using Image<Rgb24> original = RealModels.Scene(640, 480);
        using Image<Rgb24> background = RealModels.Scene(640, 480, withDisc: false);
        using Image<Rgb24> image = original.Clone();

        // The yellow disc sits at (426, 240) with radius 96; mask its bounding square plus a small margin.
        var hole = new Rectangle(426 - 100, 240 - 100, 200, 200);
        var timer = Stopwatch.StartNew();
        image.InpaintRegions(ai, [hole]);
        output.WriteLine($"inpaint 640x480, one 200x200 hole (incl. session load): {timer.ElapsedMilliseconds} ms");
        using (Image<Rgb24> again = original.Clone())
        {
            timer.Restart();
            again.InpaintRegions(ai, [hole]);
            output.WriteLine($"inpaint 640x480, one 200x200 hole (warm): {timer.ElapsedMilliseconds} ms");
        }

        // Inside the hole, compare with the disc-free scene: the fill should be far closer to it than the disc was.
        double discError = 0, fillError = 0;
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgb24> before = original.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> after = image.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> truth = background.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < image.Width; x++)
            {
                if (!hole.Contains(x, y))
                {
                    if (before[x] != after[x])
                    {
                        Assert.Fail($"Pixel ({x},{y}) outside the hole changed.");
                    }

                    continue;
                }

                discError += Math.Abs(before[x].R - truth[x].R) + Math.Abs(before[x].G - truth[x].G) + Math.Abs(before[x].B - truth[x].B);
                fillError += Math.Abs(after[x].R - truth[x].R) + Math.Abs(after[x].G - truth[x].G) + Math.Abs(after[x].B - truth[x].B);
            }
        }

        int count = hole.Width * hole.Height * 3;
        output.WriteLine($"mean absolute error against the disc-free scene inside the hole: before {discError / count:0.0}, after {fillError / count:0.0}");
        Assert.True(fillError < discError * 0.5, $"fill error {fillError / count:0.0} vs disc error {discError / count:0.0}");
    }

    [Fact]
    public async Task StagedFiles_MatchThePinnedChecksums()
    {
        string? directory = Environment.GetEnvironmentVariable(RealModels.DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        foreach (ModelDescriptor descriptor in new[] { ModelRegistry.ImageEmbedding, ModelRegistry.Inpainting, ModelRegistry.ContentModeration, ModelRegistry.QualityAssessment })
        {
            foreach ((string? file, string? sha) in new[] { (descriptor.FileName, descriptor.Sha256), (descriptor.Int8FileName, descriptor.Int8Sha256) })
            {
                string path = Path.Combine(directory, file ?? string.Empty);
                if (file is not null && File.Exists(path))
                {
                    Assert.Equal(sha, await ModelHub.ComputeSha256Async(path));
                }
            }
        }
    }
}

/// <summary>Locates real model files for <see cref="VisionModelSmokeTests"/> without touching the network.</summary>
internal static class RealModels
{
    /// <summary>Directory holding real model files under their registry file names.</summary>
    public const string DirectoryVariable = "EASYIMAGESHARP_REAL_MODELS";

    public static string? Find(ModelDescriptor descriptor)
    {
        string? directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            string staged = Path.Combine(directory, descriptor.FileName);
            if (File.Exists(staged))
            {
                return staged;
            }
        }

        string cached = Path.Combine(ModelHub.DefaultCacheDirectory, descriptor.FileName);
        return File.Exists(cached) ? cached : null;
    }

    /// <summary>
    /// The C# twin of <c>reference_images.scene()</c>: a horizontal red ramp, a vertical blue ramp, a 16 px green
    /// checkerboard and one yellow disc, all in integer arithmetic so both produce identical pixels.
    /// </summary>
    public static Image<Rgb24> Scene(int width = 320, int height = 240, bool withDisc = true)
    {
        var image = new Image<Rgb24>(width, height);
        int cx = width * 2 / 3, cy = height / 2, radius = Math.Min(width, height) / 5;
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                bool disc = withDisc && ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < radius * radius;
                row[x] = disc
                    ? new Rgb24(240, 200, 40)
                    : new Rgb24((byte)(x * 255 / (width - 1)), (byte)(((((x / 16) + (y / 16)) % 2) * 200) + 30), (byte)(y * 255 / (height - 1)));
            }
        }

        return image;
    }
}

/// <summary>A fact that is skipped unless the named registry model is available locally (see <see cref="RealModels"/>).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute(string modelName)
    {
        ModelDescriptor? descriptor = ModelRegistry.Find(modelName);
        if (descriptor is null || RealModels.Find(descriptor) is null)
        {
            this.Skip = $"Real model '{modelName}' is not available locally; set {RealModels.DirectoryVariable} or pre-seed the model cache to run it.";
        }
    }
}
