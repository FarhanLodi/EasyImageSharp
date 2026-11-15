using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// A fact that runs only when the named registry file is already in the model cache (<c>EASYIMAGESHARP_CACHE</c> or the
/// default cache directory) and is skipped otherwise, so these smoke tests are opt-in and never touch the network: warm
/// the cache once (for example with <c>ImageAiSession.WarmUpAsync</c>) to enable them.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CachedModelFactAttribute : FactAttribute
{
    public CachedModelFactAttribute(params string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            if (!File.Exists(Path.Combine(ModelHub.DefaultCacheDirectory, fileName)))
            {
                this.Skip = $"Real model '{fileName}' is not in the cache ({ModelHub.DefaultCacheDirectory}); warm the cache to run this smoke test.";
                return;
            }
        }
    }
}

/// <summary>
/// Opt-in smoke tests against the real published models (skipped unless cached). They check the contracts the
/// synthetic graphs stand in for: dynamic shapes, output names and batch behaviour of the real exports.
/// </summary>
public class RealModelSmokeTests
{
    private static ImageAiSession OfflineSession(bool quantize = false) => new(new ImageAiOptions
    {
        Offline = true,
        Quantize = quantize,
        ExecutionProvider = ExecutionProvider.Cpu,
        IntraOpNumThreads = 2,
    });

    [CachedModelFact("face_detection_yunet_2026may.onnx")]
    public void YuNet_RunsAtArbitrarySizes_AndFindsNoFaceOnABlankPage()
    {
        using ImageAiSession ai = OfflineSession();
        foreach ((int width, int height) in new[] { (333, 251), (64, 64), (1500, 900) })
        {
            using var page = new Image<Rgb24>(width, height, new Rgb24(250, 250, 250));
            Assert.Empty(page.DetectFaces(ai, new FaceDetectionOptions { TileSize = 640 }));
        }
    }

    [CachedModelFact("face_detection_yunet_2026may.onnx", "face_detection_yunet_2026may.int8.onnx")]
    public void YuNetInt8_IsSelectedByQuantize_AndRunsTheSameContract()
    {
        using ImageAiSession ai = OfflineSession(quantize: true);
        Assert.True(ai.Hub.ResolveAsync(ModelRegistry.FaceDetection).GetAwaiter().GetResult().IsQuantized);
        using Image<Rgb24> noise = TestImages.Noise<Rgb24>(320, 240);
        _ = noise.DetectFaces(ai);
    }

    [CachedModelFact("PP-LCNet_x1_0_doc_ori.onnx")]
    public void Orientation_BatchedPagesMatchSingleRuns()
    {
        using ImageAiSession ai = OfflineSession();
        var pages = new List<Image<Rgb24>>();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                pages.Add(TestImages.CornerPage(400 + (37 * i), 560, corner: i % 4));
            }

            OrientationResult[] batched = ai.DetectOrientationBatch(pages);
            for (int i = 0; i < pages.Count; i++)
            {
                OrientationResult single = pages[i].DetectOrientation(ai);
                Assert.Equal(single.DetectedAngle, batched[i].DetectedAngle);
                for (int c = 0; c < 4; c++)
                {
                    Assert.Equal(single.Probabilities[c], batched[i].Probabilities[c], 4);
                }
            }
        }
        finally
        {
            foreach (Image<Rgb24> page in pages)
            {
                page.Dispose();
            }
        }
    }
}
