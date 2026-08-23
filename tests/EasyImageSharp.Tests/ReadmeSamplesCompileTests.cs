using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Every code sample in the repository README, transcribed verbatim apart from the wrapping method.
/// Nothing here runs: the point is that the file must keep compiling, so a rename or a signature change
/// breaks the build instead of silently leaving the published documentation wrong.
/// </summary>
public class ReadmeSamplesCompileTests
{
    [Fact]
    public void SamplesCompile()
    {
        // The compiler has already proved the point by the time this runs.
        Assert.True(true);
    }

    private static void Header()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");
        image.Mutate(ctx => ctx.AutoOrient().Resize(800, 0));
        image.SaveAsWebp("thumbnail.webp");
    }

    private static void QuickStart()
    {
        using Image<Rgb24> image = Image.Load<Rgb24>("input.png");
        Console.WriteLine($"{image.Width}x{image.Height}");
        image.Mutate(ctx => ctx.Resize(400, 0).Grayscale());
        image.SaveAsJpeg("output.jpg");
    }

    private static void Thumbnail(byte[] uploadedBytes)
    {
        var options = new DecoderOptions { MaxPixels = 50_000_000 };

        using Image<Rgba32> image = Image.Load<Rgba32>(uploadedBytes, options);

        using Image<Rgba32> thumb = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(320, 320),
            Mode = ResizeMode.Crop,
            Sampler = KnownResamplers.Lanczos3,
        }));

        thumb.SaveAsWebp("thumb.webp", new WebpEncoder { Quality = 82 });
    }

    private static async Task<string?> WebUpload(Stream stream)
    {
        ImageInfo info = await Image.IdentifyAsync(stream);
        if ((long)info.Width * info.Height > 40_000_000)
        {
            return $"{info.Width}x{info.Height} is too large.";
        }

        stream.Position = 0;
        try
        {
            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(stream);
            image.Mutate(ctx => ctx.AutoOrient());
            return null;
        }
        catch (ImageFormatException ex)
        {
            return ex.Message;
        }
    }

    private static void PrepareScan()
    {
        using Image<Rgb24> page = Image.Load<Rgb24>("scan.jpg");

        page.Mutate(ctx => ctx
            .BackgroundNormalize(40)
            .Deskew()
            .MedianBlur(1)
            .SauvolaThreshold());

        page.SaveAsPng("clean.png");

        page.Mutate(ctx => ctx.PrepareForOcr());
    }

    private static void Perspective()
    {
        using Image<Rgb24> photo = Image.Load<Rgb24>("desk-photo.jpg");

        PointF[]? quad = photo.DetectPage();
        if (quad is not null)
        {
            photo.Mutate(ctx => ctx.CorrectPerspective(quad));
        }
    }

    private static void BoundingBoxes(Image<Rgba32> image, IEnumerable<(RectangleF Box, string Text)> results)
    {
        image.Mutate(ctx =>
        {
            foreach (var (box, text) in results)
            {
                ctx.DrawRectangle(Color.Lime, 2f, box);
                ctx.DrawLabel(text, Color.Black, Color.Lime, box);
            }
        });
    }

    private static void Frames()
    {
        using Image<Rgb24> document = Image.Load<Rgb24>("fax.tif");

        for (int i = 0; i < document.Frames.Count; i++)
        {
            using Image<Rgb24> page = document.Frames.CloneFrame(i);
            page.SaveAsPng($"page-{i}.png");
        }
    }

    private static void RawPixels(Image<Rgb24> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb24(row[x].B, row[x].G, row[x].R);
                }
            }
        });
    }

    private static void Metadata()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");

        if (image.Metadata.ExifProfile is { } exif &&
            exif.TryGetValue(ExifTag.DateTimeOriginal, out var taken))
        {
            Console.WriteLine(taken.Value);
        }

        Console.WriteLine($"{image.Metadata.HorizontalResolution} DPI");

        image.Mutate(ctx => ctx.AutoOrient());
        image.SaveAsJpeg("out.jpg");
    }

    private static void Limits(byte[] bytes)
    {
        var options = new DecoderOptions
        {
            MaxPixels = 50_000_000,
            MaxFrames = 32,
        };

        using Image<Rgb24> image = Image.Load<Rgb24>(bytes, options);
    }

    private static void SingleThreaded() => Configuration.Default.MaxDegreeOfParallelism = 1;

    private static void TensorBridges(Image<Rgb24> image, float[] output, int width, int height)
    {
        float[] chw = image.ToChwTensor(
            channelMean: [0.485f, 0.456f, 0.406f],
            channelStd: [0.229f, 0.224f, 0.225f]);

        _ = chw;

        using Image<Rgb24> result = TensorImage.FromChwTensor<Rgb24>(output, width, height);
    }
}
