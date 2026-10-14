using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

public class TensorWriteTests
{
    [Theory]
    [InlineData(TensorLayout.Chw, TensorChannels.Rgb)]
    [InlineData(TensorLayout.Chw, TensorChannels.Bgr)]
    [InlineData(TensorLayout.Hwc, TensorChannels.Rgb)]
    [InlineData(TensorLayout.Hwc, TensorChannels.Bgr)]
    public void ByteNormalizationMatchesScalarExactlyForEveryChannelValue(TensorLayout layout, TensorChannels channels)
    {
        using var image = new Image<Rgb24>(256, 1);
        float[] means = [0.485f, -0.2f, 0.406f];
        float[] stds = [0.229f, 0.7f, 0.225f];
        float[] actual = new float[256 * 3];
        for (int x = 0; x < 256; x++)
        {
            image[x, 0] = new Rgb24((byte)x, (byte)(255 - x), (byte)((x + 100) % 256));
        }

        image.WriteTensor(actual, new TensorWriteOptions { Layout = layout, Channels = channels }, means, stds);
        for (int x = 0; x < 256; x++)
        {
            Rgb24 pixel = image[x, 0];
            int first = channels == TensorChannels.Rgb ? pixel.R : pixel.B;
            int third = channels == TensorChannels.Rgb ? pixel.B : pixel.R;
            int start = layout == TensorLayout.Chw ? x : x * 3;
            int step = layout == TensorLayout.Chw ? 256 : 1;
            Assert.Equal(((first / 255f) - means[0]) / stds[0], actual[start]);
            Assert.Equal(((pixel.G / 255f) - means[1]) / stds[1], actual[start + step]);
            Assert.Equal(((third / 255f) - means[2]) / stds[2], actual[start + (2 * step)]);
        }
    }

    [Theory]
    [InlineData(TensorLayout.Chw)]
    [InlineData(TensorLayout.Hwc)]
    public void WriterMatchesExistingRgbNormalization(TensorLayout layout)
    {
        using Image<Rgb24> image = TestImages.Gradient(17, 13);
        float[] mean = [0.485f, 0.456f, 0.406f];
        float[] std = [0.229f, 0.224f, 0.225f];
        float[] expected = layout == TensorLayout.Chw ? image.ToChwTensor(mean, std) : image.ToHwcTensor(mean, std);
        float[] actual = new float[expected.Length];
        image.WriteTensor(actual, new TensorWriteOptions { Layout = layout }, mean, std);
        for (int i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i], 5);
    }

    [Theory]
    [InlineData(TensorLayout.Chw)]
    [InlineData(TensorLayout.Hwc)]
    public void BgrStridedWriterPreservesPaddingAndOtherBatchEntries(TensorLayout layout)
    {
        using var image = new Image<Rgb24>(2, 2);
        image[0, 0] = new Rgb24(255, 128, 0);
        image[1, 0] = new Rgb24(50, 100, 200);
        image[0, 1] = new Rgb24(30, 20, 10);
        image[1, 1] = new Rgb24(90, 80, 70);
        float[] actual = Enumerable.Repeat(-99f, 50).ToArray();
        float[] expected = actual.ToArray();
        int rowStride = layout == TensorLayout.Chw ? 4 : 8;
        int planeStride = layout == TensorLayout.Chw ? 11 : 0;
        float[] means = [0.1f, 0.2f, 0.3f];
        float[] stds = [0.5f, 0.75f, 0.25f];
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            Rgb24 pixel = image[x, y];
            float[] channels = [pixel.B / 255f, pixel.G / 255f, pixel.R / 255f];
            for (int c = 0; c < 3; c++)
            {
                int index = 5 + (y * rowStride) + (layout == TensorLayout.Chw ? x + (c * planeStride) : (x * 3) + c);
                expected[index] = (channels[c] - means[c]) / stds[c];
            }
        }

        image.WriteTensor(actual, new TensorWriteOptions
        {
            Layout = layout, Channels = TensorChannels.Bgr, DestinationOffset = 5,
            RowStride = rowStride, PlaneStride = planeStride,
        }, means, stds);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GrayscaleWriterPreservesExistingEightBitRounding()
    {
        using Image<Rgb24> image = TestImages.Gradient(19, 7);
        float[] actual = new float[19 * 7];
        image.WriteTensor(actual, new TensorWriteOptions { Channels = TensorChannels.Grayscale }, [0.2f], [0.7f]);
        float[] expected = image.ToGrayscaleTensor(0.2f, 0.7f);
        for (int i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i], 6);
    }

    [Fact]
    public void GrayscaleWriterMatchesAllBuiltInEightBitFormats()
    {
        Check<Rgb24>();
        Check<Bgr24>();
        Check<Rgba32>();
        Check<Bgra32>();
        Check<Argb32>();
        Check<Abgr32>();
        Check<L8>();
        Check<La16>();
        Check<A8>();

        static void Check<TPixel>() where TPixel : unmanaged, IPixel<TPixel>
        {
            using var image = new Image<TPixel>(256, 1);
            for (int x = 0; x < 256; x++)
            {
                image[x, 0] = TPixel.FromRgba32(new Rgba32((byte)x, (byte)(255 - x), (byte)((x * 37) % 256), (byte)x));
            }

            byte[] expected = new byte[256];
            PixelOps.ToLuminance<TPixel>(image.Frames.RootFrame.GetRowSpan(0), expected);
            float[] actual = new float[256];
            image.WriteTensor(actual, new TensorWriteOptions { Channels = TensorChannels.Grayscale });
            for (int x = 0; x < 256; x++)
            {
                Assert.Equal(expected[x] / 255f, actual[x]);
            }
        }
    }

    [Fact]
    public void SixteenBitTensorsRetainAdjacentSamplesAndRoundtrip()
    {
        using var image = new Image<Rgb48>(2, 1);
        image[0, 0] = new Rgb48(10000, 20000, 30000);
        image[1, 0] = new Rgb48(10001, 20001, 30001);
        float[] chw = image.ToChwTensor();
        Assert.Equal(10000 / 65535f, chw[0]);
        Assert.Equal(10001 / 65535f, chw[1]);
        Assert.NotEqual(chw[0], chw[1]);
        float[] hwc = image.ToHwcTensor();
        Assert.Equal(chw[1], hwc[3]);
        using var restored = TensorImage.FromChwTensor<Rgb48>(chw, 2, 1);
        Assert.Equal(image[0, 0], restored[0, 0]);
        Assert.Equal(image[1, 0], restored[1, 0]);
    }

    [Fact]
    public void FloatTensorsPreserveValuesOutsideUnitRange()
    {
        Vector4 value = new(-0.2f, 0.123456f, 2.5f, 0.5f);
        using var image = new Image<RgbaVector>(1, 1, RgbaVector.FromScaledVector4(value));
        float[] tensor = image.ToChwTensor();
        Assert.Equal(new[] { value.X, value.Y, value.Z }, tensor);
        using var restored = TensorImage.FromChwTensor<RgbaVector>(tensor, 1, 1);
        Assert.Equal(new Vector4(value.X, value.Y, value.Z, 1), restored[0, 0].ToScaledVector4());
    }

    [Fact]
    public void GrayscaleSixteenBitTensorRoundtrips()
    {
        using var image = new Image<L16>(2, 1);
        image[0, 0] = new L16(12345);
        image[1, 0] = new L16(12346);
        float[] tensor = image.ToGrayscaleTensor();
        Assert.Equal(12345 / 65535f, tensor[0], 6);
        Assert.NotEqual(tensor[0], tensor[1]);
        using var restored = TensorImage.FromGrayscaleTensor<L16>(tensor, 2, 1);
        Assert.Equal(image[0, 0], restored[0, 0]);
        Assert.Equal(image[1, 0], restored[1, 0]);
    }

    [Fact]
    public void InvalidArgumentsDoNotPartiallyWriteDestination()
    {
        using var image = new Image<Rgb24>(2, 2);
        float[] destination = Enumerable.Repeat(42f, 12).ToArray();
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, new TensorWriteOptions { DestinationOffset = 1 }));
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, new TensorWriteOptions { RowStride = 1 }));
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, new TensorWriteOptions { PlaneStride = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.WriteTensor(destination, new TensorWriteOptions { DestinationOffset = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.WriteTensor(destination, new TensorWriteOptions { Layout = TensorLayout.Hwc, PlaneStride = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.WriteTensor(destination, channelStd: new float[] { 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.WriteTensor(destination, channelMean: new float[] { float.NaN }));
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, channelMean: new float[] { 0, 1 }));
        Assert.All(destination, value => Assert.Equal(42f, value));
    }

    [Fact]
    public void TensorDimensionsAndStridesCannotOverflowBoundsChecks()
    {
        Assert.Throws<ArgumentException>(() => TensorImage.FromChwTensor<Rgb24>([], 65536, 65536));
        Assert.Throws<ArgumentException>(() => TensorImage.FromChwTensor<Rgb24>([], int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentException>(() => TensorImage.FromGrayscaleTensor<L8>([], 65536, 65536));
        using var image = new Image<Rgb24>(2, 2);
        float[] destination = Enumerable.Repeat(42f, 12).ToArray();
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, new TensorWriteOptions { RowStride = int.MaxValue }));
        Assert.Throws<ArgumentException>(() => image.WriteTensor(destination, new TensorWriteOptions { PlaneStride = int.MaxValue }));
        Assert.All(destination, value => Assert.Equal(42f, value));
    }
}
