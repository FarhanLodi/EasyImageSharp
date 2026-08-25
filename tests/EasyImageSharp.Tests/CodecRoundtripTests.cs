using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class CodecRoundtripTests
{
    // ----- PNG -----

    [Fact]
    public void Png_Rgb_RoundtripsLosslessly()
    {
        using Image<Rgb24> original = TestImages.Gradient(97, 53);
        using var stream = new MemoryStream();
        original.SaveAsPng(stream);

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        Assert.Equal(0, TestImages.AveragePixelDifference(original, decoded));
    }

    [Fact]
    public void Png_Rgba_PreservesAlpha()
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(41, 67);
        using var stream = new MemoryStream();
        original.SaveAsPng(stream);

        stream.Position = 0;
        using Image<Rgba32> decoded = Image.Load<Rgba32>(stream);

        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Assert.Equal(original[x, y], decoded[x, y]);
            }
        }
    }

    [Fact]
    public void Png_Grayscale_RoundtripsLosslessly()
    {
        using var original = new Image<L8>(64, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                original[x, y] = new L8((byte)((x * 4) % 256));
            }
        }

        using var stream = new MemoryStream();
        original.SaveAsPng(stream);

        stream.Position = 0;
        using Image<L8> decoded = Image.Load<L8>(stream);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.Equal(original[x, y].PackedValue, decoded[x, y].PackedValue);
            }
        }
    }

    [Fact]
    public void Png_Identify_ReportsHeaderInfo()
    {
        using Image<Rgb24> original = TestImages.Gradient(120, 80);
        using var stream = new MemoryStream();
        original.SaveAsPng(stream);

        ImageInfo info = Image.Identify(stream.ToArray());
        Assert.Equal(120, info.Width);
        Assert.Equal(80, info.Height);
        Assert.Equal("PNG", info.Format.Name);
        Assert.Equal(24, info.PixelType.BitsPerPixel);
    }

    // ----- BMP -----

    [Fact]
    public void Bmp_RoundtripsLosslessly()
    {
        using Image<Rgb24> original = TestImages.Gradient(31, 17); // Odd width exercises row padding.
        using var stream = new MemoryStream();
        original.SaveAsBmp(stream);

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);
        Assert.Equal(0, TestImages.AveragePixelDifference(original, decoded));
    }

    // ----- JPEG -----

    [Fact]
    public void Jpeg_Color_RoundtripsWithinTolerance()
    {
        using Image<Rgb24> original = TestImages.Gradient(160, 120);
        using var stream = new MemoryStream();
        original.SaveAsJpeg(stream, 95);

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);

        // Lossy: gradients should survive high-quality encoding closely.
        double difference = TestImages.AveragePixelDifference(original, decoded);
        Assert.True(difference < 6.0, $"Average channel difference too high: {difference:F2}");
    }

    [Fact]
    public void Jpeg_Grayscale_Roundtrips()
    {
        using var original = new Image<L8>(96, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 96; x++)
            {
                original[x, y] = new L8((byte)(x * 255 / 95));
            }
        }

        using var stream = new MemoryStream();
        original.SaveAsJpeg(stream, 95);

        stream.Position = 0;
        using Image<L8> decoded = Image.Load<L8>(stream);

        double total = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 96; x++)
            {
                total += Math.Abs(original[x, y].PackedValue - decoded[x, y].PackedValue);
            }
        }

        Assert.True(total / (96 * 64) < 4.0);
    }

    [Fact]
    public void Jpeg_OddDimensions_Roundtrip()
    {
        using Image<Rgb24> original = TestImages.Gradient(33, 21);
        using var stream = new MemoryStream();
        original.SaveAsJpeg(stream, 90);

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);
        Assert.Equal(33, decoded.Width);
        Assert.Equal(21, decoded.Height);
    }

    [Fact]
    public void Jpeg_Identify_ReportsDimensions()
    {
        using Image<Rgb24> original = TestImages.Gradient(200, 100);
        using var stream = new MemoryStream();
        original.SaveAsJpeg(stream);

        ImageInfo info = Image.Identify(stream.ToArray());
        Assert.Equal(200, info.Width);
        Assert.Equal(100, info.Height);
        Assert.Equal("JPEG", info.Format.Name);
    }

    // ----- TIFF -----

    [Theory]
    [InlineData(TiffCompression.None)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.Lzw)]
    public void Tiff_Rgb_RoundtripsLosslessly(TiffCompression compression)
    {
        using Image<Rgb24> original = TestImages.Gradient(59, 43);
        using var stream = new MemoryStream();
        original.Save(stream, new TiffEncoder { Compression = compression });

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);
        Assert.Equal(0, TestImages.AveragePixelDifference(original, decoded));
    }

    [Fact]
    public void Tiff_MultiFrame_RoundtripsAllPages()
    {
        using Image<Rgb24> page1 = TestImages.Gradient(40, 30);
        using Image<Rgb24> combined = page1.Clone();
        using Image<Rgb24> page2 = TestImages.Gradient(40, 30);
        page2.Mutate(ctx => ctx.Invert());
        combined.Frames.AddFrame(page2.Frames.RootFrame);

        using var stream = new MemoryStream();
        combined.SaveAsTiff(stream);

        stream.Position = 0;
        using Image<Rgb24> decoded = Image.Load<Rgb24>(stream);
        Assert.Equal(2, decoded.Frames.Count);

        using Image<Rgb24> frame0 = decoded.Frames.CloneFrame(0);
        using Image<Rgb24> frame1 = decoded.Frames.CloneFrame(1);
        Assert.Equal(0, TestImages.AveragePixelDifference(page1, frame0));
        Assert.Equal(0, TestImages.AveragePixelDifference(page2, frame1));
    }

    [Fact]
    public void Tiff_Grayscale_Roundtrips()
    {
        using var original = new Image<L8>(50, 20);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                original[x, y] = new L8((byte)(x * 5));
            }
        }

        using var stream = new MemoryStream();
        original.SaveAsTiff(stream);

        stream.Position = 0;
        using Image<L8> decoded = Image.Load<L8>(stream);
        for (int x = 0; x < 50; x++)
        {
            Assert.Equal(original[x, 3].PackedValue, decoded[x, 3].PackedValue);
        }
    }

    [Fact]
    public void Tiff_Identify_ReportsFrameCount()
    {
        using Image<Rgb24> image = TestImages.Gradient(25, 25);
        image.Frames.AddFrame(image.Frames.RootFrame);
        image.Frames.AddFrame(image.Frames.RootFrame);

        using var stream = new MemoryStream();
        image.SaveAsTiff(stream);

        ImageInfo info = Image.Identify(stream.ToArray());
        Assert.Equal(3, info.FrameCount);
        Assert.Equal("TIFF", info.Format.Name);
    }

    // ----- Format detection & error handling -----

    [Fact]
    public void DetectFormat_RecognizesAllEncoders()
    {
        using Image<Rgb24> image = TestImages.Gradient(16, 16);

        foreach ((IImageEncoder encoder, string expected) in new (IImageEncoder, string)[]
        {
            (new PngEncoder(), "PNG"),
            (new JpegEncoder(), "JPEG"),
            (new EasyImageSharp.Formats.Bmp.BmpEncoder(), "BMP"),
            (new TiffEncoder(), "TIFF"),
        })
        {
            using var stream = new MemoryStream();
            image.Save(stream, encoder);
            Assert.Equal(expected, Image.DetectFormat(stream.ToArray()).Name);
        }
    }

    [Fact]
    public void Load_UnknownData_Throws()
        => Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgb24>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));

    [Fact]
    public void Load_TruncatedPng_Throws()
    {
        using Image<Rgb24> image = TestImages.Gradient(32, 32);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        byte[] truncated = stream.ToArray()[..40];

        Assert.ThrowsAny<ImageFormatException>(() => Image.Load<Rgb24>(truncated));
    }

    [Fact]
    public void SaveByPath_InfersEncoderFromExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"EasyImageSharp-test-{Guid.NewGuid():N}.png");
        try
        {
            using Image<Rgb24> image = TestImages.Gradient(20, 20);
            image.Save(path);
            using Image<Rgb24> loaded = Image.Load<Rgb24>(path);
            Assert.Equal(0, TestImages.AveragePixelDifference(image, loaded));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
