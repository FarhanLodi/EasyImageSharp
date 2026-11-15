using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Batched inference: stacking along a dynamic batch axis, the sequential fallback for fixed-batch graphs and for
/// graphs that only pretend to batch, tiling and padding inside a batch, buffer reuse, bounds, cancellation and
/// concurrent use of one session. Every batched result must equal the single-image result.
/// </summary>
public class BatchInferenceTests
{
    private static string IdentityRgbBatch => TestModels.Get("identity_rgb_batch.onnx");

    private static string IdentityGrayBatch => TestModels.Get("identity_gray_batch.onnx");

    private static string Upscale2xBatch => TestModels.Get("upscale2x_nearest_batch.onnx");

    private static string ClassifierQuadrantBatch => TestModels.Get("classifier_quadrant_batch.onnx");

    private static string ClassifierFalseBatch => TestModels.Get("classifier_false_batch.onnx");

    private static ImageModelContract Rgb(int tileSize = 0, int overlap = 16, int sizeMultiple = 1) => new()
    {
        InputChannels = 3,
        TileSize = tileSize,
        TileOverlap = overlap,
        SizeMultiple = sizeMultiple,
    };

    private static List<Image<Rgb24>> Images(params (int Width, int Height)[] sizes)
        => sizes.Select((s, i) => TestImages.Noise<Rgb24>(s.Width, s.Height, seed: 100 + i)).ToList();

    private static void DisposeAll<TPixel>(IEnumerable<Image<TPixel>> images)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (Image<TPixel> image in images)
        {
            image.Dispose();
        }
    }

    // ----- Runner -----

    [Fact]
    public void BatchRunner_StacksSamplesOnADynamicBatchAxis()
    {
        using var ai = new ImageAiSession();
        InferenceSession session = ai.GetSession(IdentityRgbBatch);
        var runner = new OrtBatchRunner(session, "input", "output", maxBatchSize: 4);

        Assert.True(runner.SupportsBatching);
        float[][] samples = [.. Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3 * 5 * 4).Select(v => (float)(v + (100 * i))).ToArray())];
        BatchOutput first = runner.Run(samples, [3, 5, 4], CancellationToken.None, out bool rejected);

        Assert.False(rejected);
        Assert.Equal([3L, 3, 5, 4], first.Shape);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(samples[i], first.Sample(i).ToArray());
        }

        // The second batch of the same shape is written into the reused (pinned) output buffer.
        BatchOutput second = runner.Run(samples, [3, 5, 4], CancellationToken.None, out rejected);
        Assert.False(rejected);
        Assert.Equal(samples[2], second.Sample(2).ToArray());
    }

    [Fact]
    public void BatchRunner_RunsOneAtATimeForAFixedBatchDimension()
    {
        using var ai = new ImageAiSession();
        InferenceSession session = ai.GetSession(TestModels.IdentityRgb);
        var runner = new OrtBatchRunner(session, "input", "output", maxBatchSize: 8);

        Assert.False(runner.SupportsBatching);
        Assert.Equal(1, runner.MaxBatch);
        Assert.Equal(1, runner.BatchCapacity(12));
    }

    [Fact]
    public void BatchRunner_CapacityIsBoundedByTheElementBudget()
    {
        using var ai = new ImageAiSession();
        var runner = new OrtBatchRunner(ai.GetSession(IdentityRgbBatch), "input", "output", maxBatchSize: 1000);

        Assert.Equal(1000, runner.BatchCapacity(3));
        Assert.Equal(2, runner.BatchCapacity(OrtBatchRunner.MaxBatchElements / 2));
        Assert.Equal(1, runner.BatchCapacity(OrtBatchRunner.MaxBatchElements * 4));
    }

    [Fact]
    public void BatchRunner_FallsBackWhenTheGraphRejectsAStackedTensor()
    {
        using var ai = new ImageAiSession();
        var runner = new OrtBatchRunner(ai.GetSession(ClassifierFalseBatch), "x", "logits", maxBatchSize: 4);
        Assert.True(runner.SupportsBatching);

        float[][] samples = [new float[3 * 224 * 224], new float[3 * 224 * 224]];
        runner.Run(samples, [3, 224, 224], CancellationToken.None, out bool rejected);

        Assert.True(rejected);
        Assert.False(runner.SupportsBatching);
        Assert.Equal(1, runner.MaxBatch);
    }

    // ----- Image-to-image -----

    [Fact]
    public void RunBatch_IdentityReturnsEveryImageExactly_MixedSizes()
    {
        using var ai = new ImageAiSession();
        List<Image<Rgb24>> images = Images((20, 10), (13, 7), (20, 10), (20, 10), (1, 1), (13, 7));
        try
        {
            Image<Rgb24>[] results = ImageModelRunner.RunBatch(ai.GetSession(IdentityRgbBatch), images, Rgb(), maxBatchSize: 2);
            try
            {
                Assert.Equal(images.Count, results.Length);
                for (int i = 0; i < images.Count; i++)
                {
                    Assert.True(TestImages.PixelsEqual(images[i], results[i]), $"Image {i} differs.");
                }
            }
            finally
            {
                DisposeAll(results);
            }
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void RunBatch_TiledUpscaleEqualsTheSingleImageRun(int maxBatchSize)
    {
        using var ai = new ImageAiSession();
        InferenceSession session = ai.GetSession(Upscale2xBatch);
        ImageModelContract contract = Rgb(tileSize: 16, overlap: 4);
        List<Image<Rgb24>> images = Images((50, 37), (16, 16), (33, 20));
        try
        {
            Image<Rgb24>[] batched = ImageModelRunner.RunBatch(session, images, contract, maxBatchSize);
            try
            {
                for (int i = 0; i < images.Count; i++)
                {
                    using Image<Rgb24> single = ImageModelRunner.Run(session, images[i], contract);
                    Assert.Equal(images[i].Width * 2, batched[i].Width);
                    Assert.Equal(images[i].Height * 2, batched[i].Height);
                    Assert.True(TestImages.PixelsEqual(single, batched[i]), $"Image {i} differs from its single run.");

                    // Nearest x2 replicates every source pixel into a 2x2 block.
                    Assert.Equal(images[i][3 % images[i].Width, 5 % images[i].Height], batched[i][(3 % images[i].Width) * 2 + 1, (5 % images[i].Height) * 2]);
                }
            }
            finally
            {
                DisposeAll(batched);
            }
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Fact]
    public void RunBatch_SizeMultiplePaddingIsRemovedPerImage()
    {
        using var ai = new ImageAiSession();
        var contract = new ImageModelContract { InputChannels = 1, SizeMultiple = 8 };
        List<Image<L8>> images = [TestImages.Noise<L8>(13, 9, 1), TestImages.Noise<L8>(15, 10, 2), TestImages.Noise<L8>(16, 16, 3)];
        try
        {
            Image<L8>[] results = ImageModelRunner.RunBatch(ai.GetSession(IdentityGrayBatch), images, contract);
            try
            {
                for (int i = 0; i < images.Count; i++)
                {
                    Assert.True(TestImages.PixelsEqual(images[i], results[i]), $"Image {i} differs.");
                }
            }
            finally
            {
                DisposeAll(results);
            }
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Fact]
    public void RunBatch_FixedBatchModelFallsBackToSequentialRuns()
    {
        using var ai = new ImageAiSession();
        List<Image<Rgb24>> images = Images((9, 9), (9, 9), (4, 6));
        try
        {
            Image<Rgb24>[] results = ImageModelRunner.RunBatch(ai.GetSession(TestModels.IdentityRgb), images, Rgb());
            try
            {
                for (int i = 0; i < images.Count; i++)
                {
                    Assert.True(TestImages.PixelsEqual(images[i], results[i]));
                }
            }
            finally
            {
                DisposeAll(results);
            }
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Fact]
    public void RunBatch_FixedInputSizeContractsShareOneShape()
    {
        using var ai = new ImageAiSession();
        ImageModelContract contract = Rgb() with { FixedInputSize = new Size(16, 16) };
        InferenceSession session = ai.GetSession(IdentityRgbBatch);
        List<Image<Rgb24>> images = Images((16, 16), (40, 30), (7, 5));
        try
        {
            Image<Rgb24>[] results = ImageModelRunner.RunBatch(session, images, contract);
            try
            {
                for (int i = 0; i < images.Count; i++)
                {
                    using Image<Rgb24> single = ImageModelRunner.Run(session, images[i], contract);
                    Assert.Equal(images[i].Width, results[i].Width);
                    Assert.Equal(images[i].Height, results[i].Height);
                    Assert.True(TestImages.PixelsEqual(single, results[i]));
                }

                Assert.True(TestImages.PixelsEqual(images[0], results[0]));
            }
            finally
            {
                DisposeAll(results);
            }
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Fact]
    public void RunBatch_EmptyListReturnsEmpty_AndArgumentsAreChecked()
    {
        using var ai = new ImageAiSession();
        InferenceSession session = ai.GetSession(IdentityRgbBatch);

        Assert.Empty(ImageModelRunner.RunBatch(session, Array.Empty<Image<Rgb24>>(), Rgb()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageModelRunner.RunBatch(session, Array.Empty<Image<Rgb24>>(), Rgb(), maxBatchSize: 0));
        Assert.Throws<ArgumentException>(() => ImageModelRunner.RunBatch(session, new Image<Rgb24>[] { null! }, Rgb()));
        Assert.Throws<ArgumentNullException>(() => ImageModelRunner.RunBatch<Rgb24>(session, null!, Rgb()));
    }

    [Fact]
    public void RunBatch_HonoursCancellation()
    {
        using var ai = new ImageAiSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        List<Image<Rgb24>> images = Images((8, 8), (8, 8));
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(
                () => ImageModelRunner.RunBatch(ai.GetSession(IdentityRgbBatch), images, Rgb(), cancellationToken: cts.Token));
        }
        finally
        {
            DisposeAll(images);
        }
    }

    [Fact]
    public async Task SessionBatch_UsesTheModelContractAndIsSafeAcrossThreads()
    {
        var options = new ImageAiOptions { MaxBatchSize = 3 };
        using var ai = new ImageAiSession(options);
        var model = new LocalImageModel(Upscale2xBatch, Rgb(tileSize: 8, overlap: 2));
        List<Image<Rgb24>> images = Images((21, 13), (8, 8), (21, 13), (30, 9));
        try
        {
            using Image<Rgb24> expected0 = ai.RunImageToImage(model, images[0]);
            using Image<Rgb24> expected2 = ai.RunImageToImage(model, images[2]);
            Image<Rgb24>[][] all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ai.RunImageToImageBatchAsync(model, images)));
            foreach (Image<Rgb24>[] results in all)
            {
                try
                {
                    Assert.True(TestImages.PixelsEqual(expected0, results[0]));
                    Assert.True(TestImages.PixelsEqual(expected2, results[2]));
                    Assert.Equal(60, results[3].Width);
                }
                finally
                {
                    DisposeAll(results);
                }
            }

            Image<Rgb24>[] sync = ai.RunImageToImageBatch(model, images);
            DisposeAll(sync);
            Assert.Equal(1, ai.LoadedSessionCount);
        }
        finally
        {
            DisposeAll(images);
        }
    }

    // ----- Orientation (classification) -----

    [Theory]
    [InlineData("classifier_quadrant_batch.onnx", 3)]
    [InlineData("classifier_quadrant.onnx", 8)]
    [InlineData("classifier_false_batch.onnx", 4)]
    public async Task DetectOrientationBatch_EqualsThePerImageResult(string model, int maxBatchSize)
    {
        var options = new ImageAiOptions { MaxBatchSize = maxBatchSize, Offline = true };
        options.ModelPathOverrides["doc-orientation"] = TestModels.Get(model);
        using var ai = new ImageAiSession(options);
        var pages = new List<Image<Rgb24>>();
        try
        {
            for (int i = 0; i < 7; i++)
            {
                // The quadrant stand-in reads the bright corner: corner c means "rotated c * 90 degrees clockwise".
                pages.Add(TestImages.CornerPage(300 + (10 * i), 400 - (5 * i), corner: i % 4));
            }

            OrientationResult[] batched = await ai.DetectOrientationBatchAsync(pages);
            OrientationResult[] sync = ai.DetectOrientationBatch(pages);

            Assert.Equal(pages.Count, batched.Length);
            for (int i = 0; i < pages.Count; i++)
            {
                // Same inputs, same graph; only the runtime's summation order may differ (it can depend on buffer
                // alignment), so scores agree to float rounding.
                OrientationResult single = await pages[i].DetectOrientationAsync(ai);
                Assert.Equal(single.DetectedAngle, batched[i].DetectedAngle);
                for (int c = 0; c < 4; c++)
                {
                    Assert.Equal(single.Probabilities[c], batched[i].Probabilities[c], 1e-6f);
                    Assert.Equal(single.Probabilities[c], sync[i].Probabilities[c], 1e-6f);
                }
            }

            if (model != "classifier_false_batch.onnx")
            {
                Assert.Equal([0, 90, 180, 270, 0, 90, 180], batched.Select(r => r.DetectedAngle));
            }
        }
        finally
        {
            DisposeAll(pages);
        }
    }

    [Fact]
    public void DetectOrientationBatch_RejectsNullEntries()
    {
        using var ai = new ImageAiSession();
        Assert.Throws<ArgumentNullException>(() => ai.DetectOrientationBatch<Rgb24>(null!));
        Assert.Throws<ArgumentException>(() => ai.DetectOrientationBatch(new Image<Rgb24>[] { null! }));
    }

    // ----- Options -----

    [Fact]
    public void MaxBatchSize_IsValidatedAndSnapshotted()
    {
        var options = new ImageAiOptions();
        Assert.Equal(ImageModelRunner.DefaultMaxBatchSize, options.MaxBatchSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxBatchSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxBatchSize = 1025);

        options.MaxBatchSize = 5;
        using var ai = new ImageAiSession(options);
        options.MaxBatchSize = 9;
        Assert.Equal(5, ai.Options.MaxBatchSize);
    }
}
