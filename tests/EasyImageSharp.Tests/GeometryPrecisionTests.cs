using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class GeometryPrecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resize_FloatConstant_RetainsHdrAndNegativeComponents(bool compand)
    {
        using var source = new Image<RgbaVector>(13, 11, new RgbaVector(2.125f, -0.125f, 0.123456f));
        source.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(7, 5), Mode = ResizeMode.Stretch, Compand = compand }));
        Assert.InRange(source[3, 2].R, 2.12499f, 2.12501f);
        Assert.InRange(source[3, 2].G, -0.12501f, -0.12499f);
        Assert.InRange(source[3, 2].B, 0.123446f, 0.123466f);
    }

    [Fact]
    public void Resize_CompandedBlackWhiteAverage_MatchesLinearLightReference()
    {
        using var image = new Image<RgbaVector>(2, 1);
        image[0, 0] = new RgbaVector(0, 0, 0);
        image[1, 0] = new RgbaVector(1, 1, 1);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(1, 1), Mode = ResizeMode.Stretch, Compand = true, Sampler = KnownResamplers.Box }));
        Assert.InRange(image[0, 0].R, 0.73535f, 0.73537f);
    }

    [Fact]
    public void AffineBilinear_SixteenBitRamp_MatchesAnalyticTranslation()
    {
        using var image = new Image<Rgb48>(12, 10);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image[x, y] = new Rgb48((ushort)(10001 + (8 * x) + (12 * y)), 20003, 30007);
        ImageFrame<Rgb48> result = TransformOps.WarpAffine(image.Frames.RootFrame,
            Matrix3x2.CreateTranslation(0.25f, 0.5f), new Size(12, 10), KnownResamplers.Triangle, Color.Transparent);
        for (int y = 1; y < 8; y++)
        for (int x = 1; x < 10; x++)
            Assert.InRange((int)result[x, y].R, 10008 + (8 * x) + (12 * y), 10010 + (8 * x) + (12 * y));
    }

    [Fact]
    public void ProjectiveIdentity_AndSamplerRotation_PreserveFloatPrecision()
    {
        using var image = new Image<RgbaVector>(11, 11, new RgbaVector(2.125f, -0.125f, 0.123456f));
        ImageFrame<RgbaVector> projective = TransformOps.WarpProjective(image.Frames.RootFrame,
            Matrix4x4.Identity, new Size(11, 11), KnownResamplers.Bicubic, Color.Transparent);
        Assert.Equal(image[5, 5].R, projective[5, 5].R, 5);
        Assert.Equal(image[5, 5].G, projective[5, 5].G, 5);
        image.Mutate(ctx => ctx.Rotate(31f, KnownResamplers.Bicubic));
        Assert.Equal(2.125f, image[image.Width / 2, image.Height / 2].R, 5);
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("affine")]
    [InlineData("perspective")]
    public void HighPrecisionFilters_ExcludeHiddenBlue(string operation)
    {
        using var image = new Image<RgbaVector>(2, 1);
        image[0, 0] = new RgbaVector(1.25f, 0, 0, 1);
        image[1, 0] = new RgbaVector(0, 0, 1, 0);
        ImageFrame<RgbaVector> result;
        if (operation == "resize")
            result = FrameOps.Resize(image.Frames.RootFrame, 1, 1, KnownResamplers.Box);
        else if (operation == "affine")
            result = TransformOps.WarpAffine(image.Frames.RootFrame, Matrix3x2.CreateTranslation(0.5f, 0), new Size(1, 1), KnownResamplers.Triangle, Color.Transparent);
        else
            result = PerspectiveWarp.Warp(image.Frames.RootFrame, [new(0, 0), new(2, 0), new(2, 1), new(0, 1)], 1, 1);
        Assert.Equal(1.25f, result[0, 0].R, 5);
        Assert.Equal(0f, result[0, 0].B);
        Assert.InRange(result[0, 0].A, 0.49f, 0.51f);
    }

    [Fact]
    public void PerspectiveIdentity_PreservesAdjacentSixteenBitSamples()
    {
        using var image = new Image<Rgb48>(2, 1);
        image[0, 0] = new Rgb48(10000, 20000, 30000);
        image[1, 0] = new Rgb48(10001, 20001, 30001);
        ImageFrame<Rgb48> result = PerspectiveWarp.Warp(image.Frames.RootFrame, [new(0, 0), new(2, 0), new(2, 1), new(0, 1)], 2, 1);
        Assert.Equal(image[0, 0], result[0, 0]);
        Assert.Equal(image[1, 0], result[1, 0]);
    }

    [Fact]
    public void Perspective_RejectsNonFiniteCrossedAndCollinearCorners()
    {
        using var image = new Image<Rgba32>(2, 2);
        PointF[][] invalid =
        [
            [new(0, 0), new(float.NaN, 0), new(2, 2), new(0, 2)],
            [new(0, 0), new(2, 2), new(2, 0), new(0, 2)],
            [new(0, 0), new(1, 0), new(2, 0), new(0, 2)],
        ];
        foreach (PointF[] quad in invalid)
        {
            Assert.Throws<ArgumentException>(() => PerspectiveWarp.Warp(image.Frames.RootFrame, quad, 2, 2));
            Assert.Throws<ArgumentException>(() => PerspectiveWarp.NaturalSize(quad));
        }
        Assert.Throws<InvalidImageContentException>(() => PerspectiveWarp.NaturalSize([new(0, 0), new(float.MaxValue, 0), new(float.MaxValue, float.MaxValue), new(0, float.MaxValue)]));
    }
}
