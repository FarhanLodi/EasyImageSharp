using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

public class CoreImageTests
{
    [Fact]
    public void Constructor_WithBackground_FillsAllPixels()
    {
        using var image = new Image<Rgb24>(10, 8, new Rgb24(1, 2, 3));
        Assert.Equal(new Rgb24(1, 2, 3), image[9, 7]);
    }

    [Fact]
    public void Indexer_OutOfBounds_Throws()
    {
        using var image = new Image<Rgb24>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => image[4, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => image[0, -1]);
    }

    [Fact]
    public void LoadPixelData_Bgra32_MatchesSourceBytes()
    {
        // Two BGRA pixels: blue and semi-transparent red.
        byte[] data = { 255, 0, 0, 255, 0, 0, 255, 128 };
        using Image<Bgra32> image = Image.LoadPixelData<Bgra32>(data, 2, 1);

        Assert.Equal(new Rgba32(0, 0, 255, 255), image[0, 0].ToRgba32());
        Assert.Equal(new Rgba32(255, 0, 0, 128), image[1, 0].ToRgba32());
    }

    [Fact]
    public void CloneAs_ConvertsBetweenPixelFormats()
    {
        using Image<Rgb24> rgb = TestImages.Gradient(12, 12);
        using Image<Bgra32> bgra = rgb.CloneAs<Bgra32>();
        using Image<L8> gray = rgb.CloneAs<L8>();

        Assert.Equal(rgb[5, 5].R, bgra[5, 5].R);
        Assert.Equal(rgb[5, 5].G, bgra[5, 5].G);
        Assert.Equal(rgb[5, 5].B, bgra[5, 5].B);
        Assert.Equal(255, bgra[5, 5].A);

        Rgb24 p = rgb[3, 3];
        byte expected = (byte)Math.Clamp((int)((p.R * 0.2126f) + (p.G * 0.7152f) + (p.B * 0.0722f) + 0.5f), 0, 255);
        Assert.Equal(expected, gray[3, 3].PackedValue);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        using Image<Rgb24> original = TestImages.Gradient(8, 8);
        using Image<Rgb24> clone = original.Clone();
        clone[0, 0] = new Rgb24(9, 9, 9);
        Assert.NotEqual(original[0, 0], clone[0, 0]);
    }

    [Fact]
    public void ProcessPixelRows_ProvidesWritableSpans()
    {
        using var image = new Image<Rgb24>(6, 3);
        image.ProcessPixelRows(accessor =>
        {
            Assert.Equal(6, accessor.Width);
            Assert.Equal(3, accessor.Height);
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                row.Fill(new Rgb24((byte)y, 0, 0));
            }
        });

        Assert.Equal(new Rgb24(2, 0, 0), image[5, 2]);
    }

    [Fact]
    public void Dispose_MakesOperationsThrow()
    {
        var image = new Image<Rgb24>(4, 4);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Clone());
    }

    [Fact]
    public void Frames_ExportFrame_RemovesAndReturnsStandaloneImage()
    {
        using Image<Rgb24> image = TestImages.Gradient(10, 10);
        image.Frames.AddFrame(image.Frames.RootFrame);
        Assert.Equal(2, image.Frames.Count);

        using Image<Rgb24> exported = image.Frames.ExportFrame(1);
        Assert.Single(image.Frames);
        Assert.Equal(10, exported.Width);
    }

    [Fact]
    public void Frames_CannotRemoveLastFrame()
    {
        using Image<Rgb24> image = TestImages.Gradient(5, 5);
        Assert.Throws<InvalidOperationException>(() => image.Frames.RemoveFrame(0));
    }

    [Fact]
    public async Task LoadAsync_And_IdentifyAsync_WorkWithStreams()
    {
        using Image<Rgb24> original = TestImages.Gradient(30, 30);
        using var stream = new MemoryStream();
        original.SaveAsPng(stream);

        stream.Position = 0;
        ImageInfo info = await Image.IdentifyAsync(stream);
        Assert.Equal(30, info.Width);

        stream.Position = 0;
        using Image<Rgb24> loaded = await Image.LoadAsync<Rgb24>(stream);
        Assert.Equal(0, TestImages.AveragePixelDifference(original, loaded));
    }
}
