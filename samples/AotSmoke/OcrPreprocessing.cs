using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;

namespace AotSmoke;

/// <summary>Exercises tensor and independent-page generic paths after trimming and native compilation.</summary>
internal static class OcrPreprocessing
{
    internal static void RunAll(Report report)
    {
        report.Record("tensor BGR caller buffer with padding", static () =>
        {
            using var image = new Image<Rgb24>(2, 2, new Rgb24(255, 128, 0));
            float[] output = new float[32];
            Array.Fill(output, -99f);
            image.WriteTensor(output, new TensorWriteOptions
            {
                Channels = TensorChannels.Bgr,
                DestinationOffset = 2,
                RowStride = 3,
                PlaneStride = 8,
            });

            for (int index = 0; index < output.Length; index++)
            {
                float expected = -99f;
                for (int channel = 0; channel < 3; channel++)
                {
                    int withinPlane = index - 2 - (channel * 8);
                    if (withinPlane is 0 or 1 or 3 or 4)
                    {
                        expected = channel switch { 0 => 0f, 1 => 128 / 255f, _ => 1f };
                    }
                }

                if (output[index] != expected)
                {
                    return $"element {index} is {output[index]}, expected {expected}";
                }
            }

            return null;
        });

        report.Record("tensor high precision roundtrip", static () =>
        {
            using var image = new Image<Rgb48>(2, 1);
            image[0, 0] = new Rgb48(10000, 20000, 30000);
            image[1, 0] = new Rgb48(10001, 20001, 30001);
            float[] tensor = new float[6];
            image.WriteTensor(tensor);
            if (tensor[0] == tensor[1])
            {
                return "adjacent 16-bit samples collapsed to the same tensor value";
            }

            using Image<Rgb48> restored = TensorImage.FromChwTensor<Rgb48>(tensor, 2, 1);
            return restored[0, 0].Equals(image[0, 0]) && restored[1, 0].Equals(image[1, 0])
                ? null : "16-bit tensor roundtrip changed samples";
        });

        report.Record("processing high precision rotation", static () =>
        {
            using var image = new Image<RgbaVector>(11, 11, new RgbaVector(2.125f, -0.125f, 0.123456f, 1f));
            image.Mutate(context => context.Rotate(31f));
            RgbaVector center = image[image.Width / 2, image.Height / 2];
            if (image.Width != 16 || image.Height != 16)
            {
                return $"rotated canvas is {image.Width}x{image.Height}, expected 16x16";
            }

            return MathF.Abs(center.R - 2.125f) < 0.00001f
                && MathF.Abs(center.G + 0.125f) < 0.00001f
                && MathF.Abs(center.B - 0.123456f) < 0.00001f
                && center.A == 1f
                ? null : "rotation lost floating-point channel precision";
        });

        report.Record("processing independent document page dispatch", static () =>
        {
            using var pages = new Image<L8>(32, 24, new L8(255));
            using var second = new Image<L8>(24, 32, new L8(255));
            pages.Frames.AddFrame(second.Frames.RootFrame);
            pages.Mutate(context => context.AutoRotateDocumentPages().AutoCropPages());
            if (pages.Frames.Count != 2 || pages.Width != 32 || pages.Height != 24
                || pages.Frames[1].Width != 24 || pages.Frames[1].Height != 32)
            {
                return "independent page dispatch changed undetectable blank-page geometry";
            }

            for (int index = 0; index < pages.Frames.Count; index++)
            {
                ImageFrame<L8> frame = pages.Frames[index];
                for (int y = 0; y < frame.Height; y++)
                {
                    foreach (L8 pixel in frame.GetRowSpan(y))
                    {
                        if (pixel.PackedValue != 255)
                        {
                            return $"page {index} pixels changed";
                        }
                    }
                }
            }

            return null;
        });
    }
}
