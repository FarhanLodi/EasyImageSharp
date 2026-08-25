using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Tests;

/// <summary>Synthetic image builders shared by the test suite.</summary>
internal static class TestImages
{
    /// <summary>A deterministic RGB gradient with a diagonal stripe; exercises all channels.</summary>
    public static Image<Rgb24> Gradient(int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 255 / Math.Max(1, width - 1));
                byte g = (byte)(y * 255 / Math.Max(1, height - 1));
                byte b = (byte)((x + y) % 256);
                image[x, y] = new Rgb24(r, g, b);
            }
        }

        return image;
    }

    /// <summary>Alpha gradient over color bars.</summary>
    public static Image<Rgba32> AlphaGradient(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)(x * 7 % 256),
                    (byte)(y * 11 % 256),
                    (byte)((x ^ y) % 256),
                    (byte)(255 - (y * 255 / Math.Max(1, height - 1))));
            }
        }

        return image;
    }

    /// <summary>White page with black horizontal text-like bars, optionally rotated.</summary>
    public static Image<Rgb24> TextPage(int width, int height, float skewDegrees = 0)
    {
        var image = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
        for (int y = 20; y < height - 20; y += 14)
        {
            for (int barY = y; barY < y + 5 && barY < height; barY++)
            {
                for (int x = 15; x < width - 15; x++)
                {
                    image[x, barY] = new Rgb24(0, 0, 0);
                }
            }
        }

        if (skewDegrees != 0)
        {
            Image<Rgb24> rotated = EasyImageSharp.Processing.ProcessingExtensions.Clone(
                image, ctx => ctx.Rotate(skewDegrees));
            image.Dispose();

            // Crop back to remove the transparent-turned-black corners introduced by rotation:
            // take the central region so the test page stays mostly white.
            var cropped = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
            int offsetX = (rotated.Width - width) / 2;
            int offsetY = (rotated.Height - height) / 2;
            int margin = 30;
            for (int y = margin; y < height - margin; y++)
            {
                for (int x = margin; x < width - margin; x++)
                {
                    cropped[x, y] = rotated[x + offsetX, y + offsetY];
                }
            }

            rotated.Dispose();
            return cropped;
        }

        return image;
    }

    public static double AveragePixelDifference(Image<Rgb24> a, Image<Rgb24> b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new InvalidOperationException("Image sizes differ.");
        }

        double total = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgb24 pa = a[x, y];
                Rgb24 pb = b[x, y];
                total += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
            }
        }

        return total / (a.Width * a.Height * 3.0);
    }
}
