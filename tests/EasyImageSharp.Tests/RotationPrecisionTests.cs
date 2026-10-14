using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class RotationPrecisionTests
{
    [Theory]
    [InlineData(17, 13)]
    [InlineData(301, 199)] // Expanded destination crosses the default row-parallel threshold.
    public void Rotate_SixteenBitRamp_MatchesAnalyticInterior(int sourceWidth, int sourceHeight)
    {
        using var image = new Image<Rgb48>(sourceWidth, sourceHeight);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image[x, y] = new Rgb48((ushort)(10001 + (13 * x) + (23 * y)), 20003, 40007);
            }
        }

        image.Mutate(ctx => ctx.Rotate(31f));
        double radians = 31 * Math.PI / 180;
        int samples = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                double tx = x + 0.5 - (image.Width / 2.0);
                double ty = y + 0.5 - (image.Height / 2.0);
                double sx = (Math.Cos(radians) * tx) + (Math.Sin(radians) * ty) + (sourceWidth / 2.0) - 0.5;
                double sy = (-Math.Sin(radians) * tx) + (Math.Cos(radians) * ty) + (sourceHeight / 2.0) - 0.5;
                if (sx >= 0 && sx <= sourceWidth - 1 && sy >= 0 && sy <= sourceHeight - 1)
                {
                    // Bilinear interpolation reproduces an affine intensity field exactly.
                    int expected = (int)Math.Round(10001 + (13 * sx) + (23 * sy));
                    Assert.InRange((int)image[x, y].R, expected - 1, expected + 1);
                    samples++;
                }
            }
        }

        Assert.True(samples > 100);
    }

    [Fact]
    public void Rotate_ConstantSixteenBitInterior_PreservesNonByteValues()
    {
        var expected = new Rgba64(10001, 20003, 40007, 50009);
        using var image = new Image<Rgba64>(11, 11, expected);
        image.Mutate(ctx => ctx.Rotate(31f));
        Rgba64 actual = image[image.Width / 2, image.Height / 2];
        Assert.InRange((int)actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange((int)actual.G, expected.G - 1, expected.G + 1);
        Assert.InRange((int)actual.B, expected.B - 1, expected.B + 1);
        Assert.InRange((int)actual.A, expected.A - 1, expected.A + 1);
    }

    [Fact]
    public void Rotate_FloatInterior_PreservesHdrAndNegativeChannels()
    {
        using var image = new Image<RgbaVector>(11, 11, new RgbaVector(2.125f, -0.125f, 0.123456f, 1f));
        image.Mutate(ctx => ctx.Rotate(31f));
        RgbaVector actual = image[image.Width / 2, image.Height / 2];
        Assert.Equal(2.125f, actual.R, 5);
        Assert.Equal(-0.125f, actual.G, 5);
        Assert.Equal(0.123456f, actual.B, 5);
    }

    [Fact]
    public void Rotate_TransparentColoredNeighbors_DoNotBleedIntoVisiblePixels()
    {
        using var image = new Image<RgbaVector>(9, 9, new RgbaVector(0f, 0f, 1f, 0f));
        for (int y = 2; y < 7; y++)
        {
            for (int x = 2; x < 7; x++)
            {
                image[x, y] = new RgbaVector(1.25f, 0f, 0f, 1f);
            }
        }

        image.Mutate(ctx => ctx.Rotate(23f));
        int partialPixels = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                RgbaVector pixel = image[x, y];
                if (pixel.A > 0f)
                {
                    Assert.Equal(1.25f, pixel.R, 5);
                    Assert.Equal(0f, pixel.B);
                    if (pixel.A < 1f)
                    {
                        partialPixels++;
                    }
                }
            }
        }

        Assert.True(partialPixels > 0);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Rotate_NonFiniteAngle_ThrowsWithoutReplacingImage(float degrees)
    {
        using var image = new Image<Rgba64>(3, 4, new Rgba64(123, 456, 789));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.Rotate(degrees)));
        Assert.Equal(3, image.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal((ushort)123, image[0, 0].R);
    }
}
