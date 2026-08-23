using EasyImageSharp.PixelFormats;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

public class TensorTests
{
    [Fact]
    public void ToChwTensor_LaysOutPlanesInRgbOrder()
    {
        using var image = new Image<Rgb24>(2, 2);
        image[0, 0] = new Rgb24(255, 0, 0);
        image[1, 0] = new Rgb24(0, 255, 0);
        image[0, 1] = new Rgb24(0, 0, 255);
        image[1, 1] = new Rgb24(255, 255, 255);

        float[] tensor = image.ToChwTensor();
        Assert.Equal(12, tensor.Length);

        // R plane
        Assert.Equal(1f, tensor[0]);
        Assert.Equal(0f, tensor[1]);
        // G plane
        Assert.Equal(0f, tensor[4]);
        Assert.Equal(1f, tensor[5]);
        // B plane
        Assert.Equal(1f, tensor[10]);
        Assert.Equal(1f, tensor[11]);
    }

    [Fact]
    public void ToChwTensor_AppliesMeanAndStd()
    {
        using var image = new Image<Rgb24>(1, 1, new Rgb24(255, 128, 0));
        float[] tensor = image.ToChwTensor(new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f });

        Assert.Equal(1f, tensor[0], 3); // (1.0 - 0.5) / 0.5
        Assert.Equal(0f, tensor[1], 2); // (~0.5 - 0.5) / 0.5
        Assert.Equal(-1f, tensor[2], 3); // (0.0 - 0.5) / 0.5
    }

    [Fact]
    public void ToHwcTensor_InterleavesChannels()
    {
        using var image = new Image<Rgb24>(2, 1);
        image[0, 0] = new Rgb24(255, 0, 0);
        image[1, 0] = new Rgb24(0, 0, 255);

        float[] tensor = image.ToHwcTensor();
        Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, 1f }, tensor);
    }

    [Fact]
    public void ToGrayscaleTensor_UsesLuminance()
    {
        using var image = new Image<L8>(2, 1);
        image[0, 0] = new L8(0);
        image[1, 0] = new L8(255);

        float[] tensor = image.ToGrayscaleTensor();
        Assert.Equal(0f, tensor[0]);
        Assert.Equal(1f, tensor[1]);
    }

    [Fact]
    public void FromChwTensor_RoundtripsWithToChwTensor()
    {
        using Image<Rgb24> original = TestImages.Gradient(16, 12);
        float[] tensor = original.ToChwTensor();
        using Image<Rgb24> rebuilt = TensorImage.FromChwTensor<Rgb24>(tensor, 16, 12);

        Assert.Equal(0, TestImages.AveragePixelDifference(original, rebuilt));
    }

    [Fact]
    public void FromChwTensor_RoundtripsWithNormalization()
    {
        float[] mean = { 0.485f, 0.456f, 0.406f }; // ImageNet normalization
        float[] std = { 0.229f, 0.224f, 0.225f };

        using Image<Rgb24> original = TestImages.Gradient(10, 10);
        float[] tensor = original.ToChwTensor(mean, std);
        using Image<Rgb24> rebuilt = TensorImage.FromChwTensor<Rgb24>(tensor, 10, 10, mean, std);

        Assert.True(TestImages.AveragePixelDifference(original, rebuilt) < 1.0);
    }

    [Fact]
    public void FromGrayscaleTensor_BuildsMaskImage()
    {
        float[] mask = { 0f, 0.5f, 1f, 0f };
        using Image<L8> image = TensorImage.FromGrayscaleTensor<L8>(mask, 2, 2);

        Assert.Equal(0, image[0, 0].PackedValue);
        Assert.Equal(128, image[1, 0].PackedValue);
        Assert.Equal(255, image[0, 1].PackedValue);
    }

    [Fact]
    public void FromChwTensor_TooSmall_Throws()
        => Assert.Throws<ArgumentException>(() => TensorImage.FromChwTensor<Rgb24>(new float[10], 4, 4));
}
