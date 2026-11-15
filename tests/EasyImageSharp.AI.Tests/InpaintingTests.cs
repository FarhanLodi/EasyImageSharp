using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Inpainting through the public API with a LaMa-contract stand-in (<c>inpaint_fill_gray.onnx</c>: holes become
/// mid gray, everything else is passed through in 0-255), plus the region planner's internal invariants.
/// </summary>
public class InpaintingTests
{
    private static readonly Rgb24 Gray = new(128, 128, 128);

    private static ImageAiSession Session(string modelFile = "inpaint_fill_gray.onnx")
    {
        var options = new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu, Offline = true };
        options.ModelPathOverrides["inpainting"] = TestModels.Get(modelFile);
        return new ImageAiSession(options);
    }

    private static Image<L8> MaskWith(int width, int height, params Rectangle[] holes)
    {
        var mask = new Image<L8>(width, height);
        foreach (Rectangle hole in holes)
        {
            for (int y = hole.Top; y < hole.Bottom; y++)
            {
                mask.Frames.RootFrame.GetRowSpan(y).Slice(hole.X, hole.Width).Fill(new L8(255));
            }
        }

        return mask;
    }

    /// <summary>Asserts that pixels inside the holes are mid gray (within tolerance, ignoring a border) and all others are untouched.</summary>
    private static void AssertFilled(Image<Rgb24> original, Image<Rgb24> result, int tolerance, int border, params Rectangle[] holes)
    {
        Assert.Equal(original.Width, result.Width);
        Assert.Equal(original.Height, result.Height);
        for (int y = 0; y < result.Height; y++)
        {
            Span<Rgb24> before = original.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> after = result.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < result.Width; x++)
            {
                int hole = Array.FindIndex(holes, h => h.Contains(x, y));
                if (hole < 0)
                {
                    if (before[x] != after[x])
                    {
                        Assert.Fail($"Pixel ({x},{y}) outside the hole changed.");
                    }
                }
                else if (holes[hole].Inflate(-border, -border).Contains(x, y)
                    && (Math.Abs(after[x].R - Gray.R) > tolerance || Math.Abs(after[x].G - Gray.G) > tolerance || Math.Abs(after[x].B - Gray.B) > tolerance))
                {
                    Assert.Fail($"Pixel ({x},{y}) inside the hole is {after[x]}, not mid gray.");
                }
            }
        }
    }

    [Fact]
    public void Mask_FillsExactlyTheHolePixelsOfASmallImage()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(300, 200);
        using Image<Rgb24> image = original.Clone();
        var hole = new Rectangle(40, 30, 50, 40);
        using Image<L8> mask = MaskWith(300, 200, hole);

        image.Inpaint(ai, mask);

        // Smaller than 512 px: mirror-padded, not rescaled, so the fill is exact.
        AssertFilled(original, image, tolerance: 0, border: 0, hole);
    }

    [Fact]
    public async Task Rectangles_MatchTheEquivalentMask()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(260, 180, seed: 5);
        using Image<Rgb24> viaMask = original.Clone();
        using Image<Rgb24> viaRegions = original.Clone();
        Rectangle[] holes = [new Rectangle(10, 10, 30, 20), new Rectangle(200, 120, 40, 50)];
        using Image<L8> mask = MaskWith(260, 180, holes);

        await viaMask.InpaintAsync(ai, mask);
        await viaRegions.InpaintRegionsAsync(ai, holes);

        Assert.True(TestImages.PixelsEqual(viaMask, viaRegions));
        AssertFilled(original, viaRegions, tolerance: 0, border: 0, holes);
    }

    [Fact]
    public void LargeHoles_AreFilledThroughADownscaledCropAndBlendedBackAtFullResolution()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Gradient<Rgb24>(1400, 1000);
        using Image<Rgb24> image = original.Clone();
        var hole = new Rectangle(350, 260, 700, 480);

        image.InpaintRegions(ai, [hole]);

        // The crop is about 1400x1000 and runs at 512 px, so the fill is resampled; away from the hole edge it is gray.
        AssertFilled(original, image, tolerance: 2, border: 12, hole);
    }

    [Fact]
    public void DistantHoles_AreFilledFromSeparateCrops()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(2200, 700, seed: 9);
        using Image<Rgb24> image = original.Clone();
        Rectangle[] holes = [new Rectangle(20, 20, 16, 16), new Rectangle(2100, 600, 60, 30), new Rectangle(1000, 300, 5, 5)];

        image.InpaintRegions(ai, holes);

        // Each small hole is filled at native resolution (a 512 px crop around it), so the result is exact.
        AssertFilled(original, image, tolerance: 0, border: 0, holes);
    }

    [Fact]
    public void ThinLongMasks_AreFilledAtNativeResolution()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(3000, 1600, seed: 11);
        using Image<Rgb24> image = original.Clone();

        // A 3 px wire from corner to corner: its bounding box is the whole frame, but it is thin, so it is split into
        // chunks whose crops fit the model without downscaling and the fill is exact.
        using var mask = new Image<L8>(3000, 1600);
        for (int x = 0; x < 3000; x++)
        {
            int y = x * 1599 / 2999;
            for (int dy = -1; dy <= 1; dy++)
            {
                if (y + dy is >= 0 and < 1600)
                {
                    mask.Frames.RootFrame.GetRowSpan(y + dy)[x] = new L8(255);
                }
            }
        }

        image.Inpaint(ai, mask);

        for (int y = 0; y < 1600; y++)
        {
            Span<L8> holes = mask.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> before = original.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> after = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < 3000; x++)
            {
                if (holes[x].PackedValue == 255 ? after[x] != Gray : after[x] != before[x])
                {
                    Assert.Fail($"Pixel ({x},{y}) is wrong.");
                }
            }
        }
    }

    [Fact]
    public void FindRegions_SplitsOnlyLongThinGroups()
    {
        const int width = 2000, height = 1000;
        byte[] wire = InpaintingRunner.HolesFromRectangles(new Size(width, height), [new Rectangle(0, 500, 2000, 4)]);
        byte[] solid = InpaintingRunner.HolesFromRectangles(new Size(width, height), [new Rectangle(100, 100, 1500, 700)]);

        List<Rectangle> wireRegions = InpaintingRunner.FindRegions(wire, width, height, splitSize: 512);
        List<Rectangle> solidRegions = InpaintingRunner.FindRegions(solid, width, height, splitSize: 512);

        Assert.Equal(8, wireRegions.Count);
        Assert.All(wireRegions, r => Assert.True(r.Width <= 256 && r.Height <= 256, r.ToString()));
        Assert.Equal(new Rectangle(0, 496, 256, 8), wireRegions[0]);
        Assert.Single(solidRegions);
        Assert.Single(InpaintingRunner.FindRegions(wire, width, height));
    }

    [Fact]
    public void DynamicSizeExports_ArePaddedToAMultipleOfEight()
    {
        using ImageAiSession ai = Session("inpaint_fill_gray_dynamic.onnx");
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(301, 203, seed: 3);
        using Image<Rgb24> image = original.Clone();
        var hole = new Rectangle(100, 50, 33, 21);

        image.InpaintRegions(ai, [hole], new InpaintingOptions { WorkingSize = 256 });

        AssertFilled(original, image, tolerance: 2, border: 3, hole);
    }

    [Fact]
    public void AlphaIsKeptAndEveryFrameIsFilled()
    {
        using ImageAiSession ai = Session();
        using var image = new Image<Rgba32>(120, 90, new Rgba32(10, 200, 30, 77));
        image.Frames.AddFrame(image.Frames.RootFrame);
        var hole = new Rectangle(30, 20, 10, 10);
        using Image<L8> mask = MaskWith(120, 90, hole);

        image.Inpaint(ai, mask);

        Assert.Equal(2, image.Frames.Count);
        foreach (ImageFrame<Rgba32> frame in image.Frames)
        {
            Assert.Equal(new Rgba32(128, 128, 128, 77), frame.GetRowSpan(25)[35]);
            Assert.Equal(new Rgba32(10, 200, 30, 77), frame.GetRowSpan(5)[5]);
        }
    }

    [Fact]
    public void Threshold_And_Dilation_ShapeTheHole()
    {
        using ImageAiSession ai = Session();
        using var mask = new Image<L8>(64, 64);
        mask.Frames.RootFrame.GetRowSpan(32)[32] = new L8(100);

        using (var image = new Image<Rgb24>(64, 64, new Rgb24(250, 0, 0)))
        {
            image.Inpaint(ai, mask);
            Assert.Equal(new Rgb24(250, 0, 0), image.Frames.RootFrame.GetRowSpan(32)[32]);
        }

        using (var image = new Image<Rgb24>(64, 64, new Rgb24(250, 0, 0)))
        {
            image.Inpaint(ai, mask, new InpaintingOptions { MaskThreshold = 100, MaskDilation = 2 });
            for (int y = 28; y < 37; y++)
            {
                for (int x = 28; x < 37; x++)
                {
                    bool inside = Math.Abs(x - 32) <= 2 && Math.Abs(y - 32) <= 2;
                    Assert.Equal(inside ? Gray : new Rgb24(250, 0, 0), image.Frames.RootFrame.GetRowSpan(y)[x]);
                }
            }
        }
    }

    [Fact]
    public void ProcessingPipeline_InpaintsTheImageBehindTheContext()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(100, 80);
        var hole = new Rectangle(10, 10, 20, 20);
        using Image<L8> mask = MaskWith(100, 80, hole);

        using Image<Rgb24> viaMask = original.Clone(ctx => ctx.Inpaint(ai, mask));
        using Image<Rgb24> viaRegions = original.Clone(ctx => ctx.InpaintRegions(ai, [hole]));

        AssertFilled(original, viaMask, tolerance: 0, border: 0, hole);
        Assert.True(TestImages.PixelsEqual(viaMask, viaRegions));
    }

    [Fact]
    public void RegionsOutsideTheImage_AreANoOpThatLoadsNothing()
    {
        using ImageAiSession ai = Session();
        using Image<Rgb24> original = TestImages.Noise<Rgb24>(50, 40);
        using Image<Rgb24> image = original.Clone();

        image.InpaintRegions(ai, [new Rectangle(60, 0, 10, 10), new Rectangle(-20, -20, 20, 20), new Rectangle(5, 5, 0, 9), new Rectangle(int.MaxValue, int.MaxValue, int.MaxValue, 1)]);

        Assert.True(TestImages.PixelsEqual(original, image));
        Assert.Equal(0, ai.LoadedSessionCount);
    }

    [Fact]
    public async Task InvalidArguments_AreRejected()
    {
        using ImageAiSession ai = Session();
        using var image = new Image<Rgb24>(40, 30);
        using var wrongSize = new Image<L8>(41, 30);
        using var mask = new Image<L8>(40, 30);

        Assert.Throws<ArgumentException>(() => image.Inpaint(ai, wrongSize));
        Assert.Throws<ArgumentNullException>(() => image.Inpaint(ai, (Image<L8>)null!));
        Assert.Throws<ArgumentNullException>(() => image.InpaintRegions(ai, null!));
        Assert.Throws<ArgumentNullException>(() => image.Inpaint(null!, mask));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Inpaint(ai, mask, new InpaintingOptions { MaskThreshold = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Inpaint(ai, mask, new InpaintingOptions { MaskDilation = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Inpaint(ai, mask, new InpaintingOptions { ContextMargin = float.NaN }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => image.InpaintAsync(ai, mask, new InpaintingOptions { WorkingSize = 32 }));
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using ImageAiSession ai = Session();
        using var image = new Image<Rgb24>(40, 30);
        using Image<L8> mask = MaskWith(40, 30, new Rectangle(5, 5, 5, 5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => image.Inpaint(ai, mask, cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => image.InpaintRegionsAsync(ai, [new Rectangle(1, 1, 3, 3)], cancellationToken: cts.Token));
    }

    [Fact]
    public void AModelWithoutTheTwoInputContract_IsAContractError()
    {
        using ImageAiSession ai = Session("identity_rgb.onnx");
        using var image = new Image<Rgb24>(40, 30);

        Assert.Throws<ModelContractException>(() => image.InpaintRegions(ai, [new Rectangle(1, 1, 3, 3)]));
    }

    // ----- Planner invariants -----

    [Fact]
    public void FindRegions_MergesHolesWithinACellAndSeparatesDistantOnes()
    {
        const int width = 100, height = 60;
        byte[] state = InpaintingRunner.HolesFromRectangles(new Size(width, height),
            [new Rectangle(2, 2, 3, 3), new Rectangle(9, 3, 2, 2), new Rectangle(70, 40, 10, 10)]);

        List<Rectangle> regions = InpaintingRunner.FindRegions(state, width, height);

        Assert.Equal(2, regions.Count);
        Assert.Equal(new Rectangle(0, 0, 16, 8), regions[0]);
        Assert.Equal(Rectangle.FromLTRB(64, 40, 80, 56), regions[1]);
    }

    [Fact]
    public void PlanCrop_AlwaysContainsTheRegionAndStaysInsideTheImage()
    {
        var rng = new Random(42);
        for (int i = 0; i < 2000; i++)
        {
            int width = rng.Next(1, 3000);
            int height = rng.Next(1, 3000);
            int x = rng.Next(width);
            int y = rng.Next(height);
            var region = new Rectangle(x, y, rng.Next(1, width - x + 1), rng.Next(1, height - y + 1));
            int modelSize = rng.Next(2) == 0 ? 512 : 256;
            float margin = (float)(rng.NextDouble() * 2);

            Rectangle crop = InpaintingRunner.PlanCrop(region, width, height, modelSize, modelSize, margin);

            Assert.True(crop.Contains(region), $"{crop} does not contain {region}");
            Assert.True(crop.X >= 0 && crop.Y >= 0 && crop.Right <= width && crop.Bottom <= height, $"{crop} leaves {width}x{height}");
            Assert.True(crop.Width >= Math.Min(width, modelSize) && crop.Height >= Math.Min(height, modelSize));
        }
    }

    [Fact]
    public void DownscaledHolesNeverShrink()
    {
        var crop = new Rectangle(0, 0, 1024, 1000);
        var state = new byte[1024 * 1000];
        state[(999 * 1024) + 1023] = 1;
        state[(500 * 1024) + 3] = 1;

        byte[] mask = InpaintingRunner.DownscaleHoles(state, 1024, crop, 512, 500);

        Assert.Equal(2, mask.Count(v => v == 1));
        Assert.Equal(1, mask[(499 * 512) + 511]);
        Assert.Equal(1, mask[(250 * 512) + 1]);
    }

    [Fact]
    public void Dilate_GrowsBySquareNeighbourhoods()
    {
        var holes = new byte[9 * 9];
        holes[(4 * 9) + 4] = 1;

        byte[] grown = InpaintingRunner.Dilate(holes, 9, 9, 1);

        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                Assert.Equal(Math.Abs(x - 4) <= 1 && Math.Abs(y - 4) <= 1 ? 1 : 0, grown[(y * 9) + x]);
            }
        }

        Assert.Same(holes, InpaintingRunner.Dilate(holes, 9, 9, 0));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(5, 5, 4)]
    [InlineData(9, 5, 0)]
    [InlineData(10, 5, 0)]
    [InlineData(13, 5, 3)]
    [InlineData(-1, 5, 0)]
    [InlineData(7, 1, 0)]
    public void Mirror_RepeatsTheEdgeSample(int index, int length, int expected)
        => Assert.Equal(expected, InpaintingRunner.Mirror(index, length));
}
