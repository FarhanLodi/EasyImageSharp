using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Watermark placement, scaling, tiling, rotation and blending, checked against hand-computed pixels.</summary>
public class WatermarkTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    private static Image<Rgba32> Canvas(int width, int height) => new(width, height, White);

    private static Image<Rgba32> Stamp(int width, int height, Rgba32 color) => new(width, height, color);

    [Theory]
    [InlineData(AnchorPositionMode.BottomRight, 85, 65)]
    [InlineData(AnchorPositionMode.TopLeft, 5, 5)]
    [InlineData(AnchorPositionMode.TopRight, 85, 5)]
    [InlineData(AnchorPositionMode.BottomLeft, 5, 65)]
    [InlineData(AnchorPositionMode.Center, 45, 35)]
    [InlineData(AnchorPositionMode.Top, 45, 5)]
    [InlineData(AnchorPositionMode.Right, 85, 35)]
    public void PlacesAnOpaqueStampAtTheAnchorWithTheMargin(AnchorPositionMode position, int expectedX, int expectedY)
    {
        using Image<Rgba32> image = Canvas(100, 80);
        using Image<Rgba32> stamp = Stamp(10, 10, Red);
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions { Position = position, Margin = 5, Opacity = 1f }));
        AssertOnlyRect(image, new Rectangle(expectedX, expectedY, 10, 10), Red);
    }

    [Fact]
    public void OpacityBlendsSourceOver()
    {
        using Image<Rgba32> image = Canvas(40, 40);
        using Image<Rgba32> stamp = Stamp(10, 10, Red);
        image.Mutate(c => c.Watermark(stamp, AnchorPositionMode.TopLeft, 0.5f));
        Rgba32 p = image[16 + 2, 16 + 2];
        Assert.Equal(new Rgba32(255, 128, 128, 255), p); // 0.5 * red + 0.5 * white, rounded half up
        Assert.Equal(White, image[0, 0]); // default margin of 16 px
    }

    [Fact]
    public void ScaleIsRelativeToTheFrame()
    {
        using Image<Rgba32> image = Canvas(200, 100);
        using Image<Rgba32> stamp = Stamp(50, 50, Red);

        // The stamp must fit in 0.2 * 200 by 0.2 * 100 = 40 x 20, so it becomes 20 x 20.
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions { Scale = 0.2f, Opacity = 1f, Margin = 0, Position = AnchorPositionMode.TopLeft }));
        AssertOnlyRect(image, new Rectangle(0, 0, 20, 20), Red);
    }

    [Fact]
    public void TilesCoverTheFrameOnAGridThroughTheAnchor()
    {
        using Image<Rgba32> image = Canvas(64, 48);
        using Image<Rgba32> stamp = Stamp(8, 8, Red);
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions
        {
            Position = AnchorPositionMode.TopLeft, Margin = 4, Opacity = 1f, Tile = true, TileSpacing = new Size(8, 4),
        }));

        // Tiles start at x = 4 + 16k and y = 4 + 12k (including the partially visible ones at negative offsets).
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                bool inside = Mod(x - 4, 16) < 8 && Mod(y - 4, 12) < 8;
                Assert.Equal(inside ? Red : White, image[x, y]);
            }
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;
    }

    [Fact]
    public void RotationUsesTheRotatedBounds()
    {
        using Image<Rgba32> image = Canvas(60, 60);
        using Image<Rgba32> stamp = Stamp(20, 10, Red);
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions { Rotation = 90, Opacity = 1f, Margin = 0, Position = AnchorPositionMode.BottomRight }));
        AssertOnlyRect(image, new Rectangle(50, 40, 10, 20), Red);

        // A 45-degree rotation keeps the stamp's area roughly and stays inside its expanded bounds.
        using Image<Rgba32> rotated = Canvas(60, 60);
        rotated.Mutate(c => c.Watermark(stamp, new WatermarkOptions { Rotation = -45, Opacity = 1f, Margin = 0, Position = AnchorPositionMode.Center }));
        int red = 0;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                red += rotated[x, y].G < 128 ? 1 : 0;
            }
        }

        Assert.InRange(red, 170, 230);
    }

    [Fact]
    public void BlendModesUseTheW3CFormulas()
    {
        using var image = new Image<Rgba32>(20, 20, new Rgba32(200, 100, 50, 255));
        using Image<Rgba32> stamp = Stamp(4, 4, new Rgba32(128, 255, 0, 255));
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions { BlendMode = PixelColorBlendingMode.Multiply, Opacity = 1f, Margin = 0, Position = AnchorPositionMode.TopLeft }));
        Rgba32 p = image[1, 1];
        Assert.InRange(p.R, 99, 101); // 200 * 128 / 255
        Assert.InRange(p.G, 99, 101); // 100 * 255 / 255
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void TextWatermarkRendersTheBitmapFontInTheAnchoredCorner()
    {
        using Image<Rgba32> image = Canvas(200, 100);
        var textOptions = new TextOptions { Scale = 2 };
        Size block = textOptions.Measure("(c) 2026");
        image.Mutate(c => c.Watermark("(c) 2026", Color.Black, textOptions, new WatermarkOptions { Opacity = 1f, Margin = 10 }));

        // The glyphs must match a direct DrawText at the expected block position, and nothing else may change.
        using Image<Rgba32> expected = Canvas(200, 100);
        expected.Mutate(c => c.DrawText("(c) 2026", Color.Black, new PointF(200 - 10 - block.Width, 100 - 10 - block.Height), textOptions, DrawingOptions.Default));
        Assert.Equal(0, EffectsTests.MaxDifference(expected, image));

        using Image<Rgba32> untouched = Canvas(20, 20);
        untouched.Mutate(c => c.Watermark(string.Empty));
        Assert.Equal(EffectsTests.Checksum(Canvas(20, 20)), EffectsTests.Checksum(untouched));
    }

    [Fact]
    public void AppliesToEveryFrameAndKeepsWidePrecisionOutsideTheStamp()
    {
        using Image<Rgba32> image = Canvas(30, 30);
        image.Frames.AddFrame(image.Frames.RootFrame);
        using Image<Rgba32> stamp = Stamp(5, 5, Red);
        image.Mutate(c => c.Watermark(stamp, new WatermarkOptions { Opacity = 1f, Margin = 0, Position = AnchorPositionMode.TopLeft }));
        Assert.Equal(Red, image.Frames[1][2, 2]);

        using var wide = new Image<Rgba64>(30, 30, new Rgba64(1001, 2002, 3003, 65535));
        using Image<Rgba32> clear = Stamp(30, 30, new Rgba32(0, 0, 0, 0));
        clear[0, 0] = Red;
        wide.Mutate(c => c.Watermark(clear, new WatermarkOptions { Opacity = 1f, Margin = 0 }));
        Assert.Equal(new Rgba64(1001, 2002, 3003, 65535), wide[5, 5]); // transparent stamp pixels leave 16-bit values intact
        Assert.Equal(65535, wide[0, 0].R);
    }

    [Fact]
    public void ValidatesArguments()
    {
        using Image<Rgba32> image = Canvas(10, 10);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(c => c.Watermark((Image)null!)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(c => c.Watermark(image, null!)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(c => c.Watermark((string)null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { Opacity = 2f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { Scale = -0.1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { Margin = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { Rotation = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { TileSpacing = new Size(-1, 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatermarkOptions { BlendMode = (PixelColorBlendingMode)99 });
        Assert.Throws<ArgumentNullException>(() => new WatermarkOptions { Sampler = null! });
    }

    private static void AssertOnlyRect(Image<Rgba32> image, Rectangle rect, Rgba32 inside)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 expected = x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom ? inside : White;
                Assert.True(expected == image[x, y], $"pixel ({x}, {y}) is {image[x, y]}, expected {expected}");
            }
        }
    }
}
