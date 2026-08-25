using System.IO.Compression;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
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

    // README: header sample
    private static void Header()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");
        image.Mutate(ctx => ctx.AutoOrient().Resize(800, 0));
        image.SaveAsWebp("thumbnail.webp");
    }

    // README: "Why EasyImageSharp" — document imaging one-liner
    private static void DocumentOneLiner(Image<Rgb24> page)
    {
        page.Mutate(ctx => ctx.BackgroundNormalize(40).Deskew().SauvolaThreshold());
    }

    // README: "Getting started"
    private static async Task GettingStarted()
    {
        // The format is detected from the bytes, never from the file extension.
        using Image<Rgb24> image = Image.Load<Rgb24>("input.png");
        Console.WriteLine($"{image.Width}x{image.Height} {image.Metadata.DecodedImageFormat?.Name}");

        // Mutate edits in place; Clone returns a new image and leaves the source untouched.
        image.Mutate(ctx => ctx.Resize(400, 0).Grayscale());
        using Image<Rgb24> small = image.Clone(ctx => ctx.Resize(100, 0));

        image.SaveAsJpeg("output.jpg");
        await small.SaveAsync("small.png");   // format chosen from the extension
    }

    // README: "Thumbnails with resource limits"
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

    // README: "Validating an untrusted upload"
    private static async Task UntrustedUpload(Stream stream)
    {
        // Identify parses only the header and is never size-limited, so check the declared
        // dimensions before committing to a decode.
        ImageInfo info = await Image.IdentifyAsync(stream);
        if ((long)info.Width * info.Height > 40_000_000)
        {
            throw new InvalidDataException($"{info.Width}x{info.Height} exceeds the supported size.");
        }

        stream.Position = 0;
        try
        {
            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(stream);
            image.Mutate(ctx => ctx.AutoOrient());
            image.SaveAsJpeg("normalised.jpg");
        }
        catch (ImageFormatException ex)   // unknown format, malformed data, or a size limit exceeded
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    // README: "Re-encoding with options"
    private static void Reencode()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>("input.tif");

        image.SaveAsJpeg("out.jpg", new JpegEncoder { Quality = 90, Progressive = true });
        image.SaveAsPng("out.png", new PngEncoder { CompressionLevel = CompressionLevel.SmallestSize });
    }

    // README: "Preparing a scan for OCR"
    private static void PrepareScan()
    {
        using Image<Rgb24> page = Image.Load<Rgb24>("scan.jpg");

        page.Mutate(ctx => ctx
            .BackgroundNormalize(40)   // flatten uneven illumination
            .Deskew()                  // projection-profile straightening
            .MedianBlur(1)             // remove speckle
            .SauvolaThreshold());      // document-grade binarisation

        page.SaveAsPng("clean.png");

        // The same steps as a single preset:
        page.Mutate(ctx => ctx.PrepareForOcr());
    }

    // README: "Rectifying a photographed document"
    private static void Perspective()
    {
        using Image<Rgb24> photo = Image.Load<Rgb24>("desk-photo.jpg");

        if (photo.DetectPage() is { } quad)
        {
            photo.Mutate(ctx => ctx.CorrectPerspective(quad));
        }
    }

    // README: "Annotating detection results"
    private static void Annotate(Image<Rgba32> image, IEnumerable<(RectangleF Box, string Label)> detections)
    {
        image.Mutate(ctx =>
        {
            foreach (var (box, label) in detections)
            {
                ctx.DrawRectangle(Color.Lime, 2f, box);
                ctx.DrawLabel(label, Color.Black, Color.Lime, box);
            }
        });
    }

    // README: "Pages and frames"
    private static void Frames()
    {
        using Image<Rgb24> document = Image.Load<Rgb24>("fax.tif");

        for (int i = 0; i < document.Frames.Count; i++)
        {
            using Image<Rgb24> page = document.Frames.CloneFrame(i);
            page.SaveAsPng($"page-{i:D3}.png");
        }
    }

    // README: "Fast pixel access"
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

    // README: "Processing" — parallelism
    private static void SingleThreaded() => Configuration.Default.MaxDegreeOfParallelism = 1;

    // README: "Metadata"
    private static void Metadata()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>("photo.jpg");

        if (image.Metadata.ExifProfile is { } exif &&
            exif.TryGetValue(ExifTag.DateTimeOriginal, out var taken))
        {
            Console.WriteLine(taken.Value);
        }

        Console.WriteLine($"{image.Metadata.HorizontalResolution} DPI");

        image.Mutate(ctx => ctx.AutoOrient());   // apply EXIF orientation and reset the tag
        image.SaveAsJpeg("out.jpg");             // EXIF, ICC and XMP are preserved
    }

    // README: "Working with untrusted input"
    private static void Limits(byte[] bytes)
    {
        var options = new DecoderOptions
        {
            MaxPixels = 50_000_000,   // per frame; default 256 MP
            MaxFrames = 32,           // e.g. TIFF pages; default unlimited
        };

        using Image<Rgb24> image = Image.Load<Rgb24>(bytes, options);
    }

    // README: "Tensor bridges — in the core package"
    private static void TensorBridges(Image<Rgb24> image, float[] output, int width, int height)
    {
        // Planar [3, H, W] float tensor with ImageNet normalisation, ready for your inference session.
        float[] chw = image.ToChwTensor(
            channelMean: [0.485f, 0.456f, 0.406f],
            channelStd: [0.229f, 0.224f, 0.225f]);

        _ = chw;

        // ...and back again from a model's [3, H, W] output.
        using Image<Rgb24> result = TensorImage.FromChwTensor<Rgb24>(output, width, height);
    }
}
