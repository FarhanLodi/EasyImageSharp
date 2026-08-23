using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Resize modes, anchors, centre coordinates and the byte-identity guards for the RGB resize paths.</summary>
public class ResizeModeTests
{
    // ----- Guards: opaque-format resize output must not change -----

    [Theory]
    [InlineData("rgb24_bicubic_down", 0x18CFD04B89A2979FUL)]
    [InlineData("rgb24_bicubic_up", 0xE8FAD279BCEF7DABUL)]
    [InlineData("rgb24_lanczos3_down", 0xA9B741072DD4F7FDUL)]
    [InlineData("rgb24_triangle_down", 0x76789AE33E84A7FFUL)]
    [InlineData("rgb24_nearest_down", 0x379F7E449303B72DUL)]
    [InlineData("rgb24_bicubic_max", 0x4FEB2F4287C1F64FUL)]
    [InlineData("rgb24_bicubic_pad", 0x09ACAB8E7A6B3646UL)]
    public void OpaqueResize_ChecksumCapturedBeforeGeometryWork_IsUnchanged(string scenario, ulong expected)
    {
        // Captured from the pre-transform build (commit 2f42424) on TestImages.Gradient(97, 61).
        ResizeOptions options = scenario switch
        {
            "rgb24_bicubic_down" => new ResizeOptions { Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bicubic, Size = new Size(64, 40) },
            "rgb24_bicubic_up" => new ResizeOptions { Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bicubic, Size = new Size(150, 90) },
            "rgb24_lanczos3_down" => new ResizeOptions { Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Lanczos3, Size = new Size(64, 40) },
            "rgb24_triangle_down" => new ResizeOptions { Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Triangle, Size = new Size(64, 40) },
            "rgb24_nearest_down" => new ResizeOptions { Mode = ResizeMode.Stretch, Sampler = KnownResamplers.NearestNeighbor, Size = new Size(64, 40) },
            "rgb24_bicubic_max" => new ResizeOptions { Mode = ResizeMode.Max, Sampler = KnownResamplers.Bicubic, Size = new Size(50, 50) },
            "rgb24_bicubic_pad" => new ResizeOptions { Mode = ResizeMode.Pad, Sampler = KnownResamplers.Bicubic, Size = new Size(50, 50), PadColor = Color.Red },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using Image<Rgb24> source = TestImages.Gradient(97, 61);
        using Image<Rgb24> resized = source.Clone(ctx => ctx.Resize(options));
        Assert.Equal(expected, GeometryTestSupport.Checksum(resized));
    }

    [Fact]
    public void Defaults_AreStretchBicubicPremultipliedNotCompanded()
    {
        var options = new ResizeOptions();
        Assert.Equal(ResizeMode.Stretch, options.Mode);
        Assert.Same(KnownResamplers.Bicubic, options.Sampler);
        Assert.True(options.PremultiplyAlpha);
        Assert.False(options.Compand);
        Assert.Equal(AnchorPositionMode.Center, options.Position);
        Assert.Null(options.CenterCoordinates);
        Assert.Equal(Color.Transparent, options.PadColor);
    }

    // ----- Crop -----

    [Theory]
    [InlineData(AnchorPositionMode.Left)]
    [InlineData(AnchorPositionMode.Center)]
    [InlineData(AnchorPositionMode.Right)]
    public void Crop_ScalesToCoverThenCropsAtAnchor(AnchorPositionMode anchor)
    {
        // 100x50 into 40x40: scale to cover -> 80x40, then keep a 40-wide window.
        using Image<Rgb24> source = TestImages.Gradient(100, 50);
        using Image<Rgb24> cropped = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(40, 40),
            Mode = ResizeMode.Crop,
            Position = anchor,
            Sampler = KnownResamplers.Bicubic,
        }));

        Assert.Equal(new Size(40, 40), cropped.Size);
        using Image<Rgb24> covered = source.Clone(ctx => ctx.Resize(80, 40));
        int offset = anchor switch { AnchorPositionMode.Left => 0, AnchorPositionMode.Right => 40, _ => 20 };
        Assert.Equal(covered[offset, 20], cropped[0, 20]);
        Assert.Equal(covered[offset + 39, 5], cropped[39, 5]);
    }

    [Fact]
    public void Crop_CenterCoordinates_SelectsTheWindow()
    {
        using Image<Rgb24> source = TestImages.Gradient(100, 50);
        ResizeOptions Options(PointF? center, AnchorPositionMode anchor = AnchorPositionMode.Center) => new()
        {
            Size = new Size(40, 40),
            Mode = ResizeMode.Crop,
            CenterCoordinates = center,
            Position = anchor,
        };

        using Image<Rgb24> left = source.Clone(ctx => ctx.Resize(Options(new PointF(0f, 0.5f))));
        using Image<Rgb24> right = source.Clone(ctx => ctx.Resize(Options(new PointF(1f, 0.5f))));
        using Image<Rgb24> centre = source.Clone(ctx => ctx.Resize(Options(new PointF(0.5f, 0.5f))));
        using Image<Rgb24> anchoredLeft = source.Clone(ctx => ctx.Resize(Options(null, AnchorPositionMode.Left)));
        using Image<Rgb24> anchoredRight = source.Clone(ctx => ctx.Resize(Options(null, AnchorPositionMode.Right)));
        using Image<Rgb24> anchoredCentre = source.Clone(ctx => ctx.Resize(Options(null)));

        Assert.Equal(0, TestImages.AveragePixelDifference(left, anchoredLeft));
        Assert.Equal(0, TestImages.AveragePixelDifference(right, anchoredRight));
        Assert.Equal(0, TestImages.AveragePixelDifference(centre, anchoredCentre));
        Assert.NotEqual(0, TestImages.AveragePixelDifference(left, right));

        // A point a quarter of the way in lands at the centre of the crop: window starts at 80*0.25 - 20 = 0.
        using Image<Rgb24> quarter = source.Clone(ctx => ctx.Resize(Options(new PointF(0.25f, 0.5f))));
        Assert.Equal(0, TestImages.AveragePixelDifference(quarter, anchoredLeft));
        using Image<Rgb24> threeEighths = source.Clone(ctx => ctx.Resize(Options(new PointF(0.375f, 0.5f))));
        using Image<Rgb24> covered = source.Clone(ctx => ctx.Resize(80, 40));
        Assert.Equal(covered[10, 20], threeEighths[0, 20]);
    }

    [Fact]
    public void Crop_TallTarget_CropsVertically()
    {
        using Image<Rgb24> source = TestImages.Gradient(50, 100);
        using Image<Rgb24> top = source.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(40, 40), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Top }));
        using Image<Rgb24> bottom = source.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(40, 40), Mode = ResizeMode.Crop, Position = AnchorPositionMode.BottomLeft }));
        using Image<Rgb24> covered = source.Clone(ctx => ctx.Resize(40, 80));
        Assert.Equal(new Size(40, 40), top.Size);
        Assert.Equal(covered[10, 0], top[10, 0]);
        Assert.Equal(covered[10, 40], bottom[10, 0]);
    }

    // ----- Pad / BoxPad anchors -----

    [Theory]
    [InlineData(AnchorPositionMode.Center, 10, 5)]
    [InlineData(AnchorPositionMode.Top, 10, 0)]
    [InlineData(AnchorPositionMode.Bottom, 10, 10)]
    [InlineData(AnchorPositionMode.Left, 0, 5)]
    [InlineData(AnchorPositionMode.Right, 20, 5)]
    [InlineData(AnchorPositionMode.TopLeft, 0, 0)]
    [InlineData(AnchorPositionMode.TopRight, 20, 0)]
    [InlineData(AnchorPositionMode.BottomRight, 20, 10)]
    [InlineData(AnchorPositionMode.BottomLeft, 0, 10)]
    public void BoxPad_PlacesUnscaledContentAtAnchor(AnchorPositionMode anchor, int expectedX, int expectedY)
    {
        using Image<Rgb24> source = TestImages.Gradient(30, 20);
        using Image<Rgb24> padded = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(50, 30),
            Mode = ResizeMode.BoxPad,
            Position = anchor,
            PadColor = Color.Blue,
        }));

        Assert.Equal(new Size(50, 30), padded.Size);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                Assert.Equal(source[x, y], padded[expectedX + x, expectedY + y]);
            }
        }

        // Somewhere outside the content is pad colour.
        int outsideX = expectedX == 0 ? 49 : 0;
        Assert.Equal(new Rgb24(0, 0, 255), padded[outsideX, expectedY == 0 ? 29 : 0]);
    }

    [Fact]
    public void BoxPad_LargerSource_BehavesLikePad()
    {
        using Image<Rgb24> source = TestImages.Gradient(100, 50);
        using Image<Rgb24> boxPad = source.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(60, 60), Mode = ResizeMode.BoxPad, PadColor = Color.Red }));
        using Image<Rgb24> pad = source.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(60, 60), Mode = ResizeMode.Pad, PadColor = Color.Red }));
        Assert.Equal(new Size(60, 60), boxPad.Size);
        Assert.Equal(0, TestImages.AveragePixelDifference(pad, boxPad));
    }

    [Theory]
    [InlineData(AnchorPositionMode.Top, 0)]
    [InlineData(AnchorPositionMode.Center, 15)]
    [InlineData(AnchorPositionMode.Bottom, 30)]
    public void Pad_AnchorsScaledContent(AnchorPositionMode anchor, int expectedY)
    {
        // 100x50 into 60x60: content becomes 60x30, leaving 30 rows of padding.
        using Image<Rgba32> source = TestImages.AlphaGradient(100, 50);
        using Image<Rgba32> padded = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(60, 60),
            Mode = ResizeMode.Pad,
            Position = anchor,
            PadColor = new Rgba32(255, 0, 0, 255),
        }));
        using Image<Rgba32> content = source.Clone(ctx => ctx.Resize(60, 30));

        Assert.Equal(new Size(60, 60), padded.Size);
        Assert.Equal(content[30, 0], padded[30, expectedY]);
        Assert.Equal(content[30, 29], padded[30, expectedY + 29]);
        int padRow = expectedY == 0 ? 59 : 0;
        Assert.Equal(new Rgba32(255, 0, 0, 255), padded[30, padRow]);
    }

    // ----- Manual -----

    [Fact]
    public void Manual_ScalesIntoTargetRectangleOnCanvas()
    {
        using Image<Rgb24> source = TestImages.Gradient(80, 60);
        using Image<Rgb24> manual = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(60, 40),
            Mode = ResizeMode.Manual,
            TargetRectangle = new Rectangle(10, 5, 20, 10),
            PadColor = Color.Lime,
        }));
        using Image<Rgb24> scaled = source.Clone(ctx => ctx.Resize(20, 10));

        Assert.Equal(new Size(60, 40), manual.Size);
        Assert.Equal(new Rgb24(0, 255, 0), manual[0, 0]);
        Assert.Equal(new Rgb24(0, 255, 0), manual[59, 39]);
        Assert.Equal(new Rgb24(0, 255, 0), manual[9, 5]);
        Assert.Equal(new Rgb24(0, 255, 0), manual[30, 5]);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Assert.Equal(scaled[x, y], manual[10 + x, 5 + y]);
            }
        }
    }

    [Fact]
    public void Manual_ClipsContentOutsideTheCanvas()
    {
        using Image<Rgb24> source = TestImages.Gradient(40, 40);
        using Image<Rgb24> manual = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(30, 30),
            Mode = ResizeMode.Manual,
            TargetRectangle = new Rectangle(-10, -10, 40, 40),
            Sampler = KnownResamplers.NearestNeighbor,
        }));
        Assert.Equal(new Size(30, 30), manual.Size);
        Assert.Equal(source[10, 10], manual[0, 0]);
        Assert.Equal(source[39, 39], manual[29, 29]);
    }

    [Fact]
    public void Manual_TargetEntirelyOffCanvas_YieldsPadColorOnly()
    {
        using Image<Rgb24> source = TestImages.Gradient(20, 20);
        using Image<Rgb24> manual = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(10, 10),
            Mode = ResizeMode.Manual,
            TargetRectangle = new Rectangle(50, -30, 8, 8),
            PadColor = Color.Blue,
        }));
        Assert.Equal(new Size(10, 10), manual.Size);
        Assert.Equal(new Rgb24(0, 0, 255), manual[0, 0]);
        Assert.Equal(new Rgb24(0, 0, 255), manual[9, 9]);
    }

    [Fact]
    public void Manual_RequiresPositiveTargetRectangle()
    {
        using Image<Rgb24> source = TestImages.Gradient(10, 10);
        Assert.Throws<ArgumentException>(() => source.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(20, 20), Mode = ResizeMode.Manual })));
    }

    // ----- Misc -----

    [Fact]
    public void Min_CoversTheBox()
    {
        using Image<Rgb24> source = TestImages.Gradient(200, 100);
        source.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(50, 50), Mode = ResizeMode.Min }));
        Assert.Equal(new Size(100, 50), source.Size);
    }

    [Fact]
    public void ZeroDimension_IsComputedFromAspectRatio_ForEveryMode()
    {
        foreach (ResizeMode mode in new[] { ResizeMode.Stretch, ResizeMode.Max, ResizeMode.Min, ResizeMode.Pad, ResizeMode.Crop, ResizeMode.BoxPad })
        {
            using Image<Rgb24> source = TestImages.Gradient(200, 100);
            source.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(50, 0), Mode = mode }));
            Assert.Equal(new Size(50, 25), source.Size);
        }
    }

    [Fact]
    public void ResizeConvenience_WithCompand_UsesStretch()
    {
        using Image<Rgb24> source = TestImages.Gradient(64, 32);
        source.Mutate(ctx => ctx.Resize(20, 10, KnownResamplers.Bicubic, compand: true));
        Assert.Equal(new Size(20, 10), source.Size);
    }
}
