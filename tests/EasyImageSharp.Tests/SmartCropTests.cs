using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Content-aware cropping on scenes where the interesting region is known by construction.</summary>
public class SmartCropTests
{
    /// <summary>A flat grey canvas with a detailed, saturated subject (a red/yellow checkerboard) at the given box.</summary>
    private static Image<Rgba32> Scene(int width, int height, Rectangle subject)
    {
        var image = new Image<Rgba32>(width, height, new Rgba32(128, 128, 128, 255));
        for (int y = subject.Top; y < subject.Bottom; y++)
        {
            for (int x = subject.Left; x < subject.Right; x++)
            {
                image[x, y] = ((x / 4) + (y / 4)) % 2 == 0 ? new Rgba32(220, 30, 30, 255) : new Rgba32(250, 220, 40, 255);
            }
        }

        return image;
    }

    [Theory]
    [InlineData(400, 200, 300, 60, 60, 80, 1, 1)]   // subject on the right of a landscape image, square crop
    [InlineData(400, 200, 20, 40, 60, 80, 1, 1)]    // subject on the left
    [InlineData(200, 400, 60, 290, 80, 60, 1, 1)]   // subject at the bottom of a portrait image
    [InlineData(600, 300, 450, 100, 60, 60, 9, 16)] // a tall crop of a wide image
    public void CropContainsTheSubject(int width, int height, int sx, int sy, int sw, int sh, int aspectW, int aspectH)
    {
        var subject = new Rectangle(sx, sy, sw, sh);
        using Image<Rgba32> image = Scene(width, height, subject);
        Rectangle crop = image.FindSmartCrop(aspectW, aspectH);

        Assert.True(crop.X >= 0 && crop.Y >= 0 && crop.Right <= width && crop.Bottom <= height, $"crop {crop} leaves the image");
        Assert.True(Math.Abs((crop.Width * aspectH) - (crop.Height * aspectW)) <= Math.Max(aspectW, aspectH), $"crop {crop} has the wrong aspect");
        Assert.True(Math.Max(crop.Width / (double)width, crop.Height / (double)height) > 0.99, "with MinScale = 1 the crop is the largest that fits");
        Assert.True(crop.Left <= subject.Left && crop.Right >= subject.Right && crop.Top <= subject.Top && crop.Bottom >= subject.Bottom,
            $"crop {crop} cuts the subject {subject}");
    }

    [Fact]
    public void FlatImagesFallBackToTheCentre()
    {
        using var image = new Image<Rgba32>(300, 100, new Rgba32(90, 90, 90, 255));
        Assert.Equal(new Rectangle(100, 0, 100, 100), image.FindSmartCrop(1, 1));
    }

    [Fact]
    public void BoostsSteerTheCrop()
    {
        using var image = new Image<Rgba32>(300, 100, new Rgba32(90, 90, 90, 255));
        var options = new SmartCropOptions { Boosts = new[] { new SmartCropBoost(new Rectangle(10, 20, 40, 40), 2f) } };
        Rectangle crop = image.FindSmartCrop(1, 1, options);
        Assert.True(crop.Left <= 10 && crop.Right >= 50, $"crop {crop} misses the boosted region");

        // A negative boost on the right half pushes the crop away from it.
        using Image<Rgba32> scene = Scene(300, 100, new Rectangle(200, 20, 60, 60));
        Rectangle avoided = scene.FindSmartCrop(1, 1, new SmartCropOptions { Boosts = new[] { new SmartCropBoost(new Rectangle(150, 0, 150, 100), -5f) } });
        Assert.True(avoided.Right <= 200, $"crop {avoided} did not avoid the negatively boosted region");
    }

    [Fact]
    public void SkinColouredRegionsAttractTheCrop()
    {
        using var image = new Image<Rgba32>(400, 200, new Rgba32(60, 90, 140, 255));
        for (int y = 60; y < 140; y++)
        {
            for (int x = 40; x < 100; x++)
            {
                image[x, y] = new Rgba32(205, 150, 115, 255); // a smooth, skin-toned patch: no edges inside
            }
        }

        Rectangle crop = image.FindSmartCrop(1, 1, new SmartCropOptions { DetailWeight = 0, SaturationWeight = 0 });
        Assert.True(crop.Left <= 40 && crop.Right >= 100, $"crop {crop} misses the skin-toned patch");
    }

    [Fact]
    public void MinScaleAllowsZoomingIn()
    {
        using Image<Rgba32> image = Scene(400, 400, new Rectangle(250, 250, 60, 60));
        Rectangle full = image.FindSmartCrop(1, 1);
        Rectangle zoomed = image.FindSmartCrop(1, 1, new SmartCropOptions { MinScale = 0.3f });
        Assert.Equal(new Rectangle(0, 0, 400, 400), full);
        Assert.True(zoomed.Width < 400, $"crop {zoomed} did not zoom in");
        Assert.True(zoomed.Left <= 250 && zoomed.Right >= 310 && zoomed.Top <= 250 && zoomed.Bottom >= 310, $"crop {zoomed} cuts the subject");
    }

    [Fact]
    public void SmartCropProducesTheRequestedSizeOnEveryFrame()
    {
        using Image<Rgba32> image = Scene(400, 200, new Rectangle(300, 60, 60, 80));
        image.Frames.AddFrame(image.Frames.RootFrame);
        Rectangle crop = image.FindSmartCrop(64, 64);
        using Image<Rgba32> expected = image.Clone(c => c.Crop(crop).Resize(64, 64));
        image.Mutate(c => c.SmartCrop(64, 64));
        Assert.Equal(new Size(64, 64), image.Size);
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(EffectsTests.Checksum(expected), EffectsTests.Checksum(image));
    }

    [Fact]
    public void IsDeterministic()
    {
        using Image<Rgba32> image = EffectsTests.Synthetic(320, 240);
        Rectangle first = image.FindSmartCrop(16, 9, new SmartCropOptions { MinScale = 0.5f });
        using var serial = OperationExecution.Enter(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } });
        Assert.Equal(first, image.FindSmartCrop(16, 9, new SmartCropOptions { MinScale = 0.5f }));
        Rectangle viaContext = default;
        image.Mutate(c => viaContext = c.FindSmartCrop(16, 9, new SmartCropOptions { MinScale = 0.5f }));
        Assert.Equal(first, viaContext);
    }

    [Fact]
    public void ValidatesArguments()
    {
        using var image = new Image<Rgba32>(20, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.FindSmartCrop(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(c => c.SmartCrop(10, -1)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(c => c.SmartCrop(10, 10, null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmartCropOptions { MinScale = 0f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmartCropOptions { AnalysisSize = 8 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmartCropOptions { SkinWeight = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmartCropBoost(Rectangle.Empty, float.NaN));
        Assert.Throws<ArgumentNullException>(() => new SmartCropOptions { Boosts = null! });
    }
}
