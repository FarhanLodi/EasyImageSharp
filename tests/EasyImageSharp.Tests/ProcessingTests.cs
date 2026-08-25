using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class ProcessingTests
{
    [Fact]
    public void Resize_Stretch_ProducesExactDimensions()
    {
        using Image<Rgb24> image = TestImages.Gradient(100, 50);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(64, 64),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
    }

    [Fact]
    public void Resize_Max_PreservesAspectRatio()
    {
        using Image<Rgb24> image = TestImages.Gradient(200, 100);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(50, 50), Mode = ResizeMode.Max }));

        Assert.Equal(50, image.Width);
        Assert.Equal(25, image.Height);
    }

    [Fact]
    public void Resize_Pad_FillsCanvasWithPadColor()
    {
        using Image<Rgba32> image = TestImages.AlphaGradient(100, 50);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(60, 60),
            Mode = ResizeMode.Pad,
            PadColor = new Rgba32(255, 0, 0),
        }));

        Assert.Equal(60, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(new Rgba32(255, 0, 0), image[30, 2]); // Letterbox strip at the top.
    }

    [Fact]
    public void Resize_UniformImage_StaysUniform()
    {
        using var image = new Image<Rgb24>(77, 31, new Rgb24(100, 150, 200));
        image.Mutate(ctx => ctx.Resize(25, 40, KnownResamplers.Lanczos3));

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(new Rgb24(100, 150, 200), image[x, y]);
            }
        }
    }

    [Fact]
    public void Rotate90_MapsPixelsCorrectly()
    {
        using var image = new Image<Rgb24>(3, 2);
        image[0, 0] = new Rgb24(255, 0, 0); // Top-left marker.
        image.Mutate(ctx => ctx.Rotate(90));

        Assert.Equal(2, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(new Rgb24(255, 0, 0), image[1, 0]); // Top-left goes to top-right (clockwise).
    }

    [Fact]
    public void Rotate180_TwiceReturnsOriginal()
    {
        using Image<Rgb24> original = TestImages.Gradient(20, 10);
        using Image<Rgb24> rotated = original.Clone(ctx => ctx.Rotate(RotateMode.Rotate180));
        rotated.Mutate(ctx => ctx.Rotate(180));
        Assert.Equal(0, TestImages.AveragePixelDifference(original, rotated));
    }

    [Fact]
    public void RotateArbitrary_ExpandsCanvas()
    {
        using Image<Rgb24> image = TestImages.Gradient(100, 40);
        image.Mutate(ctx => ctx.Rotate(30f));
        Assert.True(image.Width > 100);
        Assert.True(image.Height > 40);
    }

    [Fact]
    public void Flip_Horizontal_MirrorsPixels()
    {
        using Image<Rgb24> original = TestImages.Gradient(10, 5);
        using Image<Rgb24> flipped = original.Clone(ctx => ctx.Flip(FlipMode.Horizontal));
        Assert.Equal(original[0, 2], flipped[9, 2]);
    }

    [Fact]
    public void Crop_ExtractsRegion()
    {
        using Image<Rgb24> original = TestImages.Gradient(40, 40);
        Rgb24 expected = original[12, 8];
        using Image<Rgb24> cropped = original.Clone(ctx => ctx.Crop(new Rectangle(10, 5, 20, 15)));

        Assert.Equal(20, cropped.Width);
        Assert.Equal(15, cropped.Height);
        Assert.Equal(expected, cropped[2, 3]);
    }

    [Fact]
    public void Crop_OutOfBounds_Throws()
    {
        using Image<Rgb24> image = TestImages.Gradient(10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => image.Mutate(ctx => ctx.Crop(new Rectangle(5, 5, 10, 10))));
    }

    [Fact]
    public void Grayscale_EqualizesChannels()
    {
        using Image<Rgb24> image = TestImages.Gradient(10, 10);
        image.Mutate(ctx => ctx.Grayscale());
        Rgb24 p = image[7, 3];
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.G, p.B);
    }

    [Fact]
    public void Invert_IsSelfInverse()
    {
        using Image<Rgb24> original = TestImages.Gradient(15, 15);
        using Image<Rgb24> inverted = original.Clone(ctx => ctx.Invert().Invert());
        Assert.Equal(0, TestImages.AveragePixelDifference(original, inverted));
    }

    [Fact]
    public void BinaryThreshold_ProducesOnlyBlackAndWhite()
    {
        using Image<Rgb24> image = TestImages.Gradient(30, 30);
        image.Mutate(ctx => ctx.BinaryThreshold(0.5f));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgb24 p = image[x, y];
                Assert.True(p is { R: 0, G: 0, B: 0 } or { R: 255, G: 255, B: 255 });
            }
        }
    }

    [Fact]
    public void OtsuThreshold_SeparatesBimodalImage()
    {
        // Left half dark (40), right half bright (200).
        using var image = new Image<Rgb24>(40, 20);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                byte v = x < 20 ? (byte)40 : (byte)200;
                image[x, y] = new Rgb24(v, v, v);
            }
        }

        image.Mutate(ctx => ctx.OtsuThreshold());
        Assert.Equal(new Rgb24(0, 0, 0), image[5, 10]);
        Assert.Equal(new Rgb24(255, 255, 255), image[35, 10]);
    }

    [Fact]
    public void AdaptiveThreshold_HandlesLightingGradient()
    {
        // Background brightness ramps 100 -> 250 left to right; dark dots on top.
        using var image = new Image<Rgb24>(100, 40);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                byte v = (byte)(100 + (x * 150 / 99));
                image[x, y] = new Rgb24(v, v, v);
            }
        }

        for (int x = 10; x < 100; x += 20)
        {
            image[x, 20] = new Rgb24(0, 0, 0);
        }

        image.Mutate(ctx => ctx.AdaptiveThreshold());

        // Dots stay black, background goes white on both the dim and bright side.
        Assert.Equal(new Rgb24(0, 0, 0), image[10, 20]);
        Assert.Equal(new Rgb24(255, 255, 255), image[5, 5]);
        Assert.Equal(new Rgb24(255, 255, 255), image[95, 35]);
    }

    [Fact]
    public void SauvolaThreshold_BinarizesTextPage()
    {
        using Image<Rgb24> image = TestImages.TextPage(120, 90);
        image.Mutate(ctx => ctx.SauvolaThreshold());
        Assert.Equal(new Rgb24(0, 0, 0), image[60, 22]); // Inside a text bar.
        Assert.Equal(new Rgb24(255, 255, 255), image[60, 10]); // Between bars.
    }

    [Fact]
    public void GaussianBlur_SmoothsEdges()
    {
        using var image = new Image<Rgb24>(21, 21, new Rgb24(0, 0, 0));
        image[10, 10] = new Rgb24(255, 255, 255);
        image.Mutate(ctx => ctx.GaussianBlur(2f));

        Assert.True(image[10, 10].R < 255); // Peak spread out.
        Assert.True(image[8, 10].R > 0); // Energy diffused to neighbors.
    }

    [Fact]
    public void GaussianSharpen_IncreasesEdgeContrast()
    {
        using var image = new Image<Rgb24>(30, 10);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                byte v = x < 15 ? (byte)100 : (byte)160;
                image[x, y] = new Rgb24(v, v, v);
            }
        }

        image.Mutate(ctx => ctx.GaussianSharpen(1.5f));
        Assert.True(image[13, 5].R < 100); // Undershoot on the dark side of the edge.
        Assert.True(image[16, 5].R > 160); // Overshoot on the bright side.
    }

    [Fact]
    public void MedianBlur_RemovesSaltAndPepperNoise()
    {
        using var image = new Image<Rgb24>(30, 30, new Rgb24(128, 128, 128));
        image[10, 10] = new Rgb24(255, 255, 255);
        image[20, 20] = new Rgb24(0, 0, 0);

        image.Mutate(ctx => ctx.MedianBlur(1));
        Assert.Equal(new Rgb24(128, 128, 128), image[10, 10]);
        Assert.Equal(new Rgb24(128, 128, 128), image[20, 20]);
    }

    [Fact]
    public void Brightness_And_Contrast_AdjustValues()
    {
        using var image = new Image<Rgb24>(5, 5, new Rgb24(100, 100, 100));
        image.Mutate(ctx => ctx.Brightness(1.5f));
        Assert.Equal(150, image[2, 2].R);

        using var contrastImage = new Image<Rgb24>(5, 5, new Rgb24(100, 100, 100));
        contrastImage.Mutate(ctx => ctx.Contrast(2f));
        Assert.True(contrastImage[2, 2].R < 100); // Below mid-gray pushed darker.
    }

    [Fact]
    public void Pad_CentersImageOnCanvas()
    {
        using var image = new Image<Rgba32>(10, 10, new Rgba32(1, 2, 3));
        image.Mutate(ctx => ctx.Pad(20, 20, new Rgba32(255, 255, 255)));

        Assert.Equal(20, image.Width);
        Assert.Equal(new Rgba32(255, 255, 255), image[0, 0]);
        Assert.Equal(new Rgba32(1, 2, 3), image[10, 10]);
    }

    [Fact]
    public void DrawImage_BlendsWithOpacity()
    {
        using var background = new Image<Rgb24>(10, 10, new Rgb24(0, 0, 0));
        using var overlay = new Image<Rgba32>(10, 10, new Rgba32(255, 255, 255, 255));
        background.Mutate(ctx => ctx.DrawImage(overlay, 0.5f));

        Assert.InRange<int>(background[5, 5].R, 126, 129);
    }

    [Fact]
    public void Deskew_CorrectsKnownRotation()
    {
        using Image<Rgb24> skewed = TestImages.TextPage(300, 200, skewDegrees: 3f);
        int originalWidth = skewed.Width;
        skewed.Mutate(ctx => ctx.Deskew());

        // The page should have been rotated back (canvas expands during rotation).
        Assert.True(skewed.Width != originalWidth, "Deskew did not detect the skew angle.");
    }

    [Fact]
    public void Deskew_LeavesStraightPageUntouched()
    {
        using Image<Rgb24> straight = TestImages.TextPage(300, 200);
        int width = straight.Width;
        int height = straight.Height;
        straight.Mutate(ctx => ctx.Deskew());

        Assert.Equal(width, straight.Width);
        Assert.Equal(height, straight.Height);
    }

    [Fact]
    public void Mutate_AppliesToAllFrames()
    {
        using Image<Rgb24> image = TestImages.Gradient(20, 20);
        image.Frames.AddFrame(image.Frames.RootFrame);
        image.Mutate(ctx => ctx.Resize(10, 10));

        Assert.Equal(10, image.Frames[0].Width);
        Assert.Equal(10, image.Frames[1].Width);
    }

    [Fact]
    public void Clone_WithOperation_LeavesOriginalUntouched()
    {
        using Image<Rgb24> original = TestImages.Gradient(20, 20);
        using Image<Rgb24> resized = original.Clone(ctx => ctx.Resize(5, 5).Grayscale());

        Assert.Equal(20, original.Width);
        Assert.Equal(5, resized.Width);
    }

    [Fact]
    public void GetCurrentSize_ReflectsPriorOperations()
    {
        using Image<Rgb24> image = TestImages.Gradient(40, 40);
        Size observed = default;
        image.Mutate(ctx =>
        {
            ctx.Resize(20, 10);
            observed = ctx.GetCurrentSize();
        });

        Assert.Equal(new Size(20, 10), observed);
    }
}
