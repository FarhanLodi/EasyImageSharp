using System.Text.Json;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Loading and measurement helpers shared by the document-toolkit test files: access to the
/// <c>Fixtures/document/manifest.json</c> ground truth, byte-plane extraction and the small set of image
/// metrics (mask agreement, PSNR, ink statistics) the assertions are expressed in.
/// </summary>
internal static class DocumentFixtures
{
    private const string Folder = "document/";

    private static readonly Lazy<JsonElement> Root = new(
        () => JsonDocument.Parse(File.ReadAllBytes(FixturePath.Get(Folder + "manifest.json"))).RootElement,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The manifest entry of one fixture, e.g. <c>Entry("text_page")</c>.</summary>
    public static JsonElement Entry(string name) => Root.Value.GetProperty("entries").GetProperty(name);

    /// <summary>The names listed under a top-level manifest array such as <c>skew_entries</c>.</summary>
    public static string[] Names(string arrayProperty)
        => Root.Value.GetProperty(arrayProperty).EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>Loads a document fixture as 8-bit grey.</summary>
    public static Image<L8> LoadGray(string fileName) => Image.Load<L8>(FixturePath.Get(Folder + fileName));

    /// <summary>Loads a document fixture as RGB.</summary>
    public static Image<Rgb24> LoadRgb(string fileName) => Image.Load<Rgb24>(FixturePath.Get(Folder + fileName));

    /// <summary>The raw grey plane of an <see cref="L8"/> image, row-major.</summary>
    public static byte[] Plane(Image<L8> image)
    {
        var plane = new byte[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            Span<L8> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < image.Width; x++)
            {
                plane[(y * image.Width) + x] = row[x].PackedValue;
            }
        }

        return plane;
    }

    /// <summary>The ink mask (1 where the pixel is darker than 128) of an <see cref="L8"/> image.</summary>
    public static byte[] InkMask(Image<L8> image)
    {
        byte[] plane = Plane(image);
        var mask = new byte[plane.Length];
        for (int i = 0; i < plane.Length; i++)
        {
            mask[i] = plane[i] < 128 ? (byte)1 : (byte)0;
        }

        return mask;
    }

    /// <summary>The ink mask of a mask fixture (black pixels are the members).</summary>
    public static byte[] LoadMask(string fileName)
    {
        using Image<L8> image = LoadGray(fileName);
        return InkMask(image);
    }

    /// <summary>Number of pixels whose value differs between two same-sized grey images.</summary>
    public static int CountDifferences(Image<L8> actual, Image<L8> expected)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        int differences = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            Span<L8> a = actual.Frames.RootFrame.GetRowSpan(y);
            Span<L8> b = expected.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < a.Length; x++)
            {
                if (a[x].PackedValue != b[x].PackedValue)
                {
                    differences++;
                }
            }
        }

        return differences;
    }

    /// <summary>The largest absolute per-channel difference between two same-sized RGB images.</summary>
    public static int MaxChannelDifference(Image<Rgb24> actual, Image<Rgb24> expected)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        int worst = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            Span<Rgb24> a = actual.Frames.RootFrame.GetRowSpan(y);
            Span<Rgb24> b = expected.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < a.Length; x++)
            {
                worst = Math.Max(worst, Math.Abs(a[x].R - b[x].R));
                worst = Math.Max(worst, Math.Abs(a[x].G - b[x].G));
                worst = Math.Max(worst, Math.Abs(a[x].B - b[x].B));
            }
        }

        return worst;
    }

    /// <summary>Peak signal-to-noise ratio (dB) of two same-sized grey images; infinity when identical.</summary>
    public static double Psnr(Image<L8> actual, Image<L8> expected)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        double sum = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            Span<L8> a = actual.Frames.RootFrame.GetRowSpan(y);
            Span<L8> b = expected.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < a.Length; x++)
            {
                double d = a[x].PackedValue - b[x].PackedValue;
                sum += d * d;
            }
        }

        double mse = sum / ((double)actual.Width * actual.Height);
        return mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>The fraction of pixels set in <paramref name="reference"/> that are still set in <paramref name="mask"/>.</summary>
    public static double Retained(byte[] mask, byte[] reference)
    {
        int total = 0;
        int kept = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            if (reference[i] == 0)
            {
                continue;
            }

            total++;
            if (mask[i] != 0)
            {
                kept++;
            }
        }

        return total == 0 ? 1.0 : (double)kept / total;
    }
}

/// <summary>Deterministic synthetic document pages built in code (no fixture needed).</summary>
internal static class DocumentPages
{
    /// <summary>
    /// A grey page whose paper brightness falls off multiplicatively towards the right and the bottom, built
    /// from a clean black-on-white page so the ink positions stay exactly known.
    /// </summary>
    public static Image<L8> WithIlluminationGradient(Image<L8> clean, double rightFalloff = 0.45, double bottomFalloff = 0.70)
    {
        var lit = new Image<L8>(clean.Width, clean.Height);
        for (int y = 0; y < clean.Height; y++)
        {
            Span<L8> source = clean.Frames.RootFrame.GetRowSpan(y);
            Span<L8> target = lit.Frames.RootFrame.GetRowSpan(y);
            double gy = 1.0 - ((1.0 - bottomFalloff) * y / Math.Max(1, clean.Height - 1));
            for (int x = 0; x < clean.Width; x++)
            {
                double gx = 1.0 - ((1.0 - rightFalloff) * x / Math.Max(1, clean.Width - 1));
                int value = (int)Math.Round(source[x].PackedValue * gx * gy);
                target[x] = new L8((byte)Math.Clamp(value, 0, 255));
            }
        }

        return lit;
    }

    /// <summary>An all-white grey page.</summary>
    public static Image<L8> Blank(int width, int height)
    {
        var image = new Image<L8>(width, height);
        for (int y = 0; y < height; y++)
        {
            image.Frames.RootFrame.GetRowSpan(y).Fill(new L8(255));
        }

        return image;
    }

    /// <summary>Fills a rectangle with the given grey level.</summary>
    public static void Fill(Image<L8> image, Rectangle rect, byte value)
    {
        for (int y = Math.Max(0, rect.Y); y < Math.Min(image.Height, rect.Bottom); y++)
        {
            Span<L8> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = Math.Max(0, rect.X); x < Math.Min(image.Width, rect.Right); x++)
            {
                row[x] = new L8(value);
            }
        }
    }

    /// <summary>Fills a filled disc with the given grey level.</summary>
    public static void FillDisc(Image<L8> image, int cx, int cy, int radius, byte value)
    {
        for (int y = Math.Max(0, cy - radius); y < Math.Min(image.Height, cy + radius + 1); y++)
        {
            Span<L8> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = Math.Max(0, cx - radius); x < Math.Min(image.Width, cx + radius + 1); x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    row[x] = new L8(value);
                }
            }
        }
    }

    /// <summary>A deterministic pseudo-random binary page (0 or 255) with the given ink probability.</summary>
    public static Image<L8> RandomBinary(int width, int height, int seed, double inkProbability = 0.4)
    {
        var rng = new Random(seed);
        var image = new Image<L8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<L8> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new L8(rng.NextDouble() < inkProbability ? (byte)0 : (byte)255);
            }
        }

        return image;
    }

    /// <summary>A deterministic pseudo-random grey page.</summary>
    public static Image<L8> RandomGray(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var image = new Image<L8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<L8> row = image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new L8((byte)rng.Next(256));
            }
        }

        return image;
    }
}
