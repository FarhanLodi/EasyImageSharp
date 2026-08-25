using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Mirrors the exact imaging call patterns used by EasyOcrSharp so that migrating
/// only requires swapping the using directives.
/// </summary>
public class EasyOcrSharpCompatibilityTests
{
    [Fact]
    public void ImagePreprocessor_Patterns_Compile_And_Run()
    {
        using Image<Rgb24> image = TestImages.TextPage(100, 80);

        // Clone with processing chain (EasyOcrService / ImageProcessing pattern).
        using Image<Rgb24> processed = image.Clone(ctx => ctx
            .Resize(new ResizeOptions
            {
                Size = new Size(64, 64),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic,
            })
            .Grayscale());

        Assert.Equal(64, processed.Width);

        // Mutate with the preprocessing operations used by ImagePreprocessor.
        using Image<Rgb24> mutated = image.Clone();
        mutated.Mutate(ctx => ctx.GaussianBlur(0.8f));
        mutated.Mutate(ctx => ctx.GaussianSharpen(1.2f));
        mutated.Mutate(ctx => ctx.Grayscale());
        mutated.Mutate(ctx => ctx.BinaryThreshold(0.5f));
        mutated.Mutate(ctx => ctx.AdaptiveThreshold());

        // Rotation via Rgba32 intermediate (ImagePreprocessor.RotateArbitrary pattern).
        using Image<Rgba32> rgba = image.CloneAs<Rgba32>();
        using Image<Rgba32> rotated = rgba.Clone(ctx => ctx.Rotate(2.5f));
        using var canvas = new Image<Rgba32>(rotated.Width, rotated.Height, new Rgba32(255, 255, 255, 255));
        canvas.Mutate(ctx => ctx.DrawImage(rotated, 1f));
        Assert.True(canvas.Width > 0);

        // ProcessPixelRows + GetRowSpan (tensor extraction / ZXing luminance pattern).
        float sum = 0;
        processed.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    sum += row[x].R;
                }
            }
        });
        Assert.True(sum >= 0);
    }

    [Fact]
    public void EasyOcrService_Patterns_Load_Identify_Crop()
    {
        using Image<Rgb24> source = TestImages.Gradient(60, 40);
        using var stream = new MemoryStream();
        source.SaveAsPng(stream);
        byte[] bytes = stream.ToArray();

        // Image.Load<Rgb24>(ReadOnlySpan<byte>) pattern.
        using Image<Rgb24> fromBytes = Image.Load<Rgb24>((ReadOnlySpan<byte>)bytes);
        Assert.Equal(60, fromBytes.Width);

        // Image.Identify(ReadOnlySpan<byte>) pattern.
        ImageInfo info = Image.Identify((ReadOnlySpan<byte>)bytes);
        Assert.Equal(40, info.Height);

        // Region crop pattern: image.Clone(x => x.Crop(rect)).
        using Image<Rgb24> region = fromBytes.Clone(x => x.Crop(new Rectangle(10, 10, 20, 20)));
        Assert.Equal(20, region.Width);
    }

    [Fact]
    public void PdfRasterizer_Pattern_LoadPixelData_CloneAs()
    {
        // PDFium emits BGRA scanlines; the rasterizer wraps them and converts to Rgb24.
        int width = 8;
        int height = 4;
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 255; // B
            bgra[i + 1] = 0; // G
            bgra[i + 2] = 0; // R
            bgra[i + 3] = 255; // A
        }

        using Image<Bgra32> bgraImage = Image.LoadPixelData<Bgra32>(bgra, width, height);
        using Image<Rgb24> rgb = bgraImage.CloneAs<Rgb24>();
        Assert.Equal(new Rgb24(0, 0, 255), rgb[3, 2]);
    }

    [Fact]
    public void Visualization_Pattern_DirectPixelWrites()
    {
        using Image<Rgb24> annotated = TestImages.Gradient(50, 50).Clone();

        // Bresenham-style direct pixel writes (OcrVisualizationExtensions pattern).
        for (int x = 5; x < 45; x++)
        {
            annotated[x, 25] = new Rgb24(255, 0, 0);
        }

        Assert.Equal(new Rgb24(255, 0, 0), annotated[20, 25]);
    }

    [Fact]
    public async Task MultiFrame_Tiff_Pattern()
    {
        // Multi-frame TIFF handling (EasyOcrServiceMultiFrameExtensions pattern).
        using Image<Rgb24> tiff = TestImages.Gradient(30, 30);
        tiff.Frames.AddFrame(tiff.Frames.RootFrame);

        string path = Path.Combine(Path.GetTempPath(), $"EasyImageSharp-mf-{Guid.NewGuid():N}.tiff");
        try
        {
            tiff.SaveAsTiff(path);

            ImageInfo info = await Image.IdentifyAsync(path);
            Assert.Equal(2, info.FrameCount);

            using Image<Rgb24> loaded = await Image.LoadAsync<Rgb24>(path);
            for (int i = 0; i < loaded.Frames.Count; i++)
            {
                using Image<Rgb24> page = loaded.Frames.CloneFrame(i);
                Assert.Equal(30, page.Width);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
