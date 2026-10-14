using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Checks downstream preprocessing arithmetic without downloading models or running inference.</summary>
public class TensorOcrCompatibilityTests
{
    [Fact]
    public void PaddleRecognition_BgrBatchWithRightPaddingMatchesExistingArithmetic()
    {
        const int height = 48;
        const int tensorWidth = 128;
        const int plane = height * tensorWidth;
        float[] actual = new float[2 * 3 * plane];
        float[] expected = new float[actual.Length];
        using Image<Rgb24> source = TestImages.Gradient(75, 23);
        for (int batch = 0; batch < 2; batch++)
        {
            int width = batch == 0 ? 97 : 61;
            using Image<Rgb24> resized = source.Clone(c => c.Resize(new ResizeOptions
            {
                Size = new Size(width, height), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Triangle,
            }));
            int offset = batch * 3 * plane;
            resized.WriteTensor(actual, new TensorWriteOptions
            {
                Channels = TensorChannels.Bgr, DestinationOffset = offset,
                RowStride = tensorWidth, PlaneStride = plane,
            }, [0.5f], [0.5f]);

            // Independent transcription of PaddleOCRNet SVTR's existing per-channel loop.
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = resized.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int i = offset + y * tensorWidth + x;
                    expected[i] = row[x].B / 127.5f - 1f;
                    expected[i + plane] = row[x].G / 127.5f - 1f;
                    expected[i + 2 * plane] = row[x].R / 127.5f - 1f;
                }
            }
        }

        for (int i = 0; i < actual.Length; i++)
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0f, 1e-6f);
    }

    [Theory]
    [InlineData(TensorChannels.Rgb)]
    [InlineData(TensorChannels.Bgr)]
    public void Detector_NormalizationUsesOutputChannelOrder(TensorChannels channels)
    {
        using Image<Rgb24> image = TestImages.Gradient(31, 27);
        float[] mean = [0.485f, 0.456f, 0.406f];
        float[] std = [0.229f, 0.224f, 0.225f];
        int plane = image.Width * image.Height;
        float[] tensor = new float[3 * plane];
        image.WriteTensor(tensor, new TensorWriteOptions { Channels = channels }, mean, std);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgb24 pixel = image[x, y];
                int i = y * image.Width + x;
                float first = channels == TensorChannels.Rgb ? pixel.R : pixel.B;
                float third = channels == TensorChannels.Rgb ? pixel.B : pixel.R;
                Assert.InRange(Math.Abs(tensor[i] - ((first / 255f - mean[0]) / std[0])), 0, 1e-6f);
                Assert.InRange(Math.Abs(tensor[i + plane] - ((pixel.G / 255f - mean[1]) / std[1])), 0, 1e-6f);
                Assert.InRange(Math.Abs(tensor[i + 2 * plane] - ((third / 255f - mean[2]) / std[2])), 0, 1e-6f);
            }
        }
    }
}
