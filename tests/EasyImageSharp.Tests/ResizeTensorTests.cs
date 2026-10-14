using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

public class ResizeTensorTests
{
    [Theory]
    [InlineData(ResizeMode.Stretch)]
    [InlineData(ResizeMode.Max)]
    [InlineData(ResizeMode.Min)]
    [InlineData(ResizeMode.Pad)]
    [InlineData(ResizeMode.BoxPad)]
    [InlineData(ResizeMode.Crop)]
    [InlineData(ResizeMode.Manual)]
    public void FusedMatchesReferenceForModesSamplersAndLayouts(ResizeMode mode)
    {
        using Image<Rgb24> image = TestImages.Gradient(23, 17);
        foreach (IResampler sampler in new[] { KnownResamplers.NearestNeighbor, KnownResamplers.Triangle, KnownResamplers.Bicubic, KnownResamplers.Lanczos3 })
        foreach (TensorLayout layout in Enum.GetValues<TensorLayout>())
        foreach (TensorChannels channels in Enum.GetValues<TensorChannels>())
        {
            Compare(image, new ResizeOptions
            {
                Size = new Size(13, 21), Mode = mode, Sampler = sampler,
                PadColor = new Rgba32(17, 53, 99), Position = AnchorPositionMode.BottomRight,
                TargetRectangle = new Rectangle(-2, 3, 15, 14),
            }, layout, channels);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AlphaAndCompandingMatchReference(bool premultiply, bool compand)
    {
        using var image = new Image<Rgba32>(19, 13);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image[x, y] = new Rgba32((byte)(x * 13), (byte)(y * 19), (byte)(x * y), (byte)((x + y) * 7));
        Compare(image, new ResizeOptions
        {
            Size = new Size(31, 9), Sampler = KnownResamplers.Lanczos3,
            Compand = compand, PremultiplyAlpha = premultiply,
        }, TensorLayout.Chw, TensorChannels.Bgr);
    }

    [Fact]
    public void LuminanceAndWideFormatsMatchReference()
    {
        using var gray = new Image<L8>(17, 9);
        using var wide = new Image<Rgba64>(17, 9);
        using var floating = new Image<RgbaVector>(17, 9);
        for (int y = 0; y < 9; y++)
        for (int x = 0; x < 17; x++)
        {
            gray[x, y] = new L8((byte)(x * 13 + y));
            Vector4 v = new((x + 0.12f) / 17, (y + 0.45f) / 9, 0.34567f, 0.8f);
            wide[x, y] = Rgba64.FromScaledVector4(v);
            floating[x, y] = RgbaVector.FromScaledVector4(v);
        }

        foreach (bool compand in new[] { false, true })
        {
            var options = new ResizeOptions { Size = new Size(11, 21), Compand = compand };
            Compare(gray, options, TensorLayout.Chw, TensorChannels.Grayscale);
            Compare(wide, options, TensorLayout.Hwc, TensorChannels.Rgb);
            Compare(floating, options, TensorLayout.Chw, TensorChannels.Rgb);
        }
    }

    [Fact]
    public void IdentityNearestAndBoxPadPreserveWideSamples()
    {
        using var image = new Image<Rgb48>(2, 1);
        image[0, 0] = new Rgb48(12345, 23456, 34567);
        image[1, 0] = new Rgb48(12346, 23457, 34568);
        Compare(image, new ResizeOptions { Size = new Size(2, 1) }, TensorLayout.Hwc, TensorChannels.Rgb);
        Compare(image, new ResizeOptions { Size = new Size(4, 2), Sampler = KnownResamplers.NearestNeighbor }, TensorLayout.Hwc, TensorChannels.Rgb);
        Compare(image, new ResizeOptions { Size = new Size(6, 6), Mode = ResizeMode.BoxPad }, TensorLayout.Chw, TensorChannels.Rgb);
    }

    [Fact]
    public void GeometryUsesActualRoundedScaleAndCropOffsets()
    {
        using var image = new Image<Rgb24>(23, 17);
        var options = new ResizeOptions { Size = new Size(13, 21), Mode = ResizeMode.Crop, CenterCoordinates = new PointF(0.1f, 0.9f) };
        TensorResizeMapping plan = image.GetTensorResizeMapping(options);
        Assert.Equal(new Size(13, 21), plan.OutputSize);
        Assert.Equal(new Size(23, 17), plan.SourceSize);
        PointF point = new(12.25f, 4.75f);
        PointF restored = plan.ToSource(plan.ToTensor(point));
        Assert.Equal(point.X, restored.X, 5);
        Assert.Equal(point.Y, restored.Y, 5);
        Assert.True(plan.ContentBounds.X <= 0);
        Compare(image, options, TensorLayout.Chw, TensorChannels.Rgb);
    }

    [Fact]
    public void InvalidDestinationIsRejectedBeforeWriting()
    {
        using var image = new Image<Rgb24>(3, 2);
        float[] destination = Enumerable.Repeat(99f, 12).ToArray();
        Assert.Throws<ArgumentException>(() => image.ResizeToTensor(destination, new ResizeOptions { Size = new Size(4, 4) }));
        Assert.All(destination, value => Assert.Equal(99f, value));
    }

    [Fact]
    public void CanceledTensorExecutionLeavesDestinationUntouched()
    {
        using var image = new Image<Rgb24>(2, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        float[] destination = Enumerable.Repeat(99f, 12).ToArray();
        var options = new TensorWriteOptions
        {
            ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancellation.Token },
        };
        Assert.Throws<OperationCanceledException>(() => image.WriteTensor(destination, options));
        Assert.Throws<OperationCanceledException>(() => image.ResizeToTensor(destination,
            new ResizeOptions { Size = new Size(2, 2) }, options));
        Assert.All(destination, value => Assert.Equal(99f, value));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void CropRejectsNonFiniteCoordinatesBeforeWriting(float coordinate)
    {
        using var image = new Image<Rgb24>(3, 2);
        float[] destination = Enumerable.Repeat(99f, 12).ToArray();
        var options = new ResizeOptions { Size = new Size(2, 2), Mode = ResizeMode.Crop, CenterCoordinates = new PointF(coordinate, 0.5f) };
        Assert.Throws<ArgumentException>(() => image.ResizeToTensor(destination, options));
        Assert.Throws<ArgumentException>(() => image.Clone(c => c.Resize(options)));
        Assert.All(destination, value => Assert.Equal(99f, value));
    }

    [Fact]
    public void RoundedResizeDimensionsCannotWrapToOne()
    {
        using var image = new Image<Rgb24>(2, 1);
        Assert.Throws<InvalidImageContentException>(() => image.GetTensorResizeMapping(new ResizeOptions { Size = new Size(0, int.MaxValue) }));
        Assert.Throws<InvalidImageContentException>(() => image.GetTensorResizeMapping(new ResizeOptions { Size = new Size(1, int.MaxValue), Mode = ResizeMode.Crop }));
        Assert.Throws<InvalidImageContentException>(() => image.GetTensorResizeMapping(new ResizeOptions { Size = new Size(65536, 65536) }));
    }

    [Fact]
    public void KernelRejectsOversizedWeightsBeforeAllocatingThem()
    {
        Assert.Throws<InvalidImageContentException>(() => ResizeKernelMap.Build(1_000_000, 3000, new TestSampler(1_000_000, 1)));
        Assert.Throws<InvalidImageContentException>(() => ResizeKernelMap.Build(int.MaxValue, 1, KnownResamplers.Bicubic));
        Assert.Throws<ArgumentException>(() => ResizeKernelMap.Build(3, 2, new TestSampler(2, float.NaN)));
        Assert.Throws<ArgumentException>(() => ResizeKernelMap.Build(3, 2, new TestSampler(2, float.PositiveInfinity)));
    }

    private sealed class TestSampler(float radius, float value) : IResampler
    {
        public float Radius => radius;
        public float GetValue(float x) => value;
    }

    private static void Compare<TPixel>(Image<TPixel> image, ResizeOptions resize, TensorLayout layout, TensorChannels channels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> reference = image.Clone(context => context.Resize(resize));
        int count = channels == TensorChannels.Grayscale ? 1 : 3;
        int rowStride = (reference.Width * (layout == TensorLayout.Hwc ? count : 1)) + 3;
        int planeStride = rowStride * reference.Height + 7;
        int length = planeStride * count + 19;
        float[] expected = Enumerable.Repeat(-99f, length).ToArray();
        float[] actual = expected.ToArray();
        var options = new TensorWriteOptions
        {
            Layout = layout, Channels = channels, DestinationOffset = 5, RowStride = rowStride,
            PlaneStride = layout == TensorLayout.Chw ? planeStride : 0,
        };
        float[] mean = count == 1 ? [0.2f] : [0.2f, 0.3f, 0.4f];
        float[] std = count == 1 ? [0.5f] : [0.5f, 0.7f, 0.9f];
        reference.WriteTensor(expected, options, mean, std);
        TensorResizeMapping mapping = image.ResizeToTensor(actual, resize, options, mean, std);
        Assert.Equal(new Size(reference.Width, reference.Height), mapping.OutputSize);
        Assert.Equal(expected, actual);
    }
}
