using System.Diagnostics;
using System.Globalization;
using EasyImageSharp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;

// JPEG encoder evaluation: rate-distortion comparison of encoder options against the default configuration, and
// encode speed. Run from the repository root:
//
//   dotnet run -c Release --project tools/JpegEncoderEvaluation -- rd [--subsampling 420|444] [--qualities 30,50,...]
//       [--max-images N] [--variants trellis,preset] <image or directory>...
//   dotnet run -c Release --project tools/JpegEncoderEvaluation -- speed <image> [iterations]
//
// "rd" encodes every input with every variant over a sweep of quality settings, decodes the result with this
// library, and measures size, luma SSIM (Wang et al. 2004: 11x11 Gaussian window, sigma 1.5, K1 0.01, K2 0.03),
// luma PSNR and RGB PSNR. For each variant it then reports the mean size change versus "default" at matched
// fidelity: log(size) is interpolated piecewise linearly as a function of the metric for both curves and the
// difference averaged over the metric range both cover (a Bjontegaard-style delta rate with linear instead of cubic
// interpolation). Luma SSIM and luma PSNR ignore chroma, so they flatter variants that spend fewer bits on colour;
// RGB PSNR does not. Use lossless inputs (PNG): re-encoding JPEGs measures generation loss as well.
//
// The benchmark corpus generator (benchmarks/corpus/generate.py) provides suitable deterministic inputs; its
// _photo and _scan_page functions can write lossless PNGs of any size. Timings are wall-clock on whatever else the
// machine is doing; compare variants within one run only.
if (args is ["speed", string input, ..])
{
    int iterations = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 10;
    return Speed(input, iterations);
}

if (args is ["rd", ..])
{
    return RateDistortion(args[1..]);
}

Console.Error.WriteLine("usage: speed <image> [iterations]");
Console.Error.WriteLine("       rd [--subsampling 420|444] [--qualities 30,40,...] [--max-images N] [--variants a,b] <image or directory>...");
return 1;

static int Speed(string input, int iterations)
{
    using Image<Rgb24> source = Image.Load<Rgb24>(input);
    foreach ((string name, JpegEncoder encoder) in new[]
    {
        ("q75 4:2:0", new JpegEncoder { Quality = 75, ColorType = JpegEncodingColor.YCbCrRatio420 }),
        ("q90 4:4:4", new JpegEncoder()),
        ("q75 4:2:0 trellis", new JpegEncoder { Quality = 75, ColorType = JpegEncodingColor.YCbCrRatio420, TrellisQuantization = true }),
    })
    {
        foreach (int threads in new[] { 1, Environment.ProcessorCount })
        {
            Configuration.Default.MaxDegreeOfParallelism = threads;
            using var stream = new MemoryStream();
            for (int i = 0; i < 3; i++)
            {
                stream.SetLength(0);
                source.Save(stream, encoder);
            }

            var times = new List<double>();
            for (int i = 0; i < iterations; i++)
            {
                stream.SetLength(0);
                long start = Stopwatch.GetTimestamp();
                source.Save(stream, encoder);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            times.Sort();
            Console.WriteLine(FormattableString.Invariant(
                $"{name} threads={threads}: median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms, {stream.Length} bytes"));
        }
    }

    return 0;
}

static int RateDistortion(string[] args)
{
    JpegEncodingColor color = JpegEncodingColor.YCbCrRatio420;
    int[] qualities = { 30, 40, 50, 60, 70, 75, 80, 85, 90, 95 };
    int maxImages = int.MaxValue;
    string[]? filter = null;
    var inputs = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--subsampling":
                color = args[++i] == "444" ? JpegEncodingColor.YCbCrRatio444 : JpegEncodingColor.YCbCrRatio420;
                break;
            case "--qualities":
                qualities = args[++i].Split(',').Select(q => int.Parse(q, CultureInfo.InvariantCulture)).ToArray();
                break;
            case "--max-images":
                maxImages = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--variants":
                filter = args[++i].Split(',');
                break;
            default:
                if (Directory.Exists(args[i]))
                {
                    inputs.AddRange(Directory.EnumerateFiles(args[i])
                        .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ppm" or ".tga" or ".tif" or ".tiff")
                        .Order(StringComparer.Ordinal));
                }
                else
                {
                    inputs.Add(args[i]);
                }

                break;
        }
    }

    inputs = inputs.Take(maxImages).ToList();
    if (inputs.Count == 0)
    {
        Console.Error.WriteLine("No input images.");
        return 1;
    }

    // Each variant is a function of quality; the first is the reference every other variant is compared with.
    var variants = new (string Name, Func<int, JpegEncoder> Create)[]
    {
        ("default", q => new JpegEncoder { Quality = q, ColorType = color }),
        ("optimised Huffman", q => new JpegEncoder { Quality = q, ColorType = color, OptimizeHuffmanTables = true }),
        ("trellis", q => new JpegEncoder { Quality = q, ColorType = color, TrellisQuantization = true }),
        ("trellis + optimised", q => new JpegEncoder { Quality = q, ColorType = color, TrellisQuantization = true, OptimizeHuffmanTables = true }),
        ("progressive (optimised)", q => new JpegEncoder { Quality = q, ColorType = color, Progressive = true }),
        ("progressive + trellis", q => new JpegEncoder { Quality = q, ColorType = color, Progressive = true, TrellisQuantization = true }),
        ("chroma quality q-20", q => new JpegEncoder { Quality = q, ChromaQuality = q - 20, ColorType = color }),
        ("preset Flat", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.Flat }),
        ("preset Robidoux", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.Robidoux }),
        ("preset KleinSilversteinCarney", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.KleinSilversteinCarney }),
        ("preset WatsonTaylorBorthwick", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.WatsonTaylorBorthwick }),
        ("preset AhumadaWatsonPeterson", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.AhumadaWatsonPeterson }),
        ("preset PetersonAhumadaWatson", q => new JpegEncoder { Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.PetersonAhumadaWatson }),
        ("Robidoux + trellis + optimised", q => new JpegEncoder
        {
            Quality = q, ColorType = color, QuantizationPreset = JpegQuantizationPreset.Robidoux, TrellisQuantization = true, OptimizeHuffmanTables = true,
        }),
    };

    if (filter is not null)
    {
        variants = variants.Where((v, i) => i == 0 || filter.Any(f => v.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    Console.WriteLine(FormattableString.Invariant(
        $"# {inputs.Count} image(s), {color}, qualities {string.Join(',', qualities)}; runtime {Environment.Version}, {Environment.ProcessorCount} logical CPUs"));
    Console.WriteLine("# size change at matched metric vs 'default' (negative = smaller): one block per image, then the mean over images");

    // [variant][image] -> size change at matched Y-SSIM / Y-PSNR / RGB-PSNR, and encode time.
    var ssimDelta = new double[variants.Length][];
    var yPsnrDelta = new double[variants.Length][];
    var psnrDelta = new double[variants.Length][];
    var encodeMs = new double[variants.Length];
    for (int v = 0; v < variants.Length; v++)
    {
        ssimDelta[v] = new double[inputs.Count];
        yPsnrDelta[v] = new double[inputs.Count];
        psnrDelta[v] = new double[inputs.Count];
    }

    for (int n = 0; n < inputs.Count; n++)
    {
        using Image<Rgb24> source = Image.Load<Rgb24>(inputs[n]);
        float[] sourceLuma = Luma(source);
        var curves = new List<Point>[variants.Length];
        for (int v = 0; v < variants.Length; v++)
        {
            curves[v] = new List<Point>();
            foreach (int quality in qualities)
            {
                JpegEncoder encoder = variants[v].Create(quality);
                using var stream = new MemoryStream();
                long start = Stopwatch.GetTimestamp();
                source.Save(stream, encoder);
                encodeMs[v] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                using Image<Rgb24> decoded = Image.Load<Rgb24>(stream.ToArray());
                float[] decodedLuma = Luma(decoded);
                curves[v].Add(new Point(
                    stream.Length,
                    Ssim(sourceLuma, decodedLuma, source.Width, source.Height),
                    LumaPsnr(sourceLuma, decodedLuma),
                    Psnr(source, decoded)));
            }
        }

        Console.WriteLine(FormattableString.Invariant($"## {Path.GetFileName(inputs[n])} ({source.Width}x{source.Height})"));
        for (int v = 0; v < variants.Length; v++)
        {
            ssimDelta[v][n] = MatchedSizeChange(curves[0], curves[v], p => p.Ssim);
            yPsnrDelta[v][n] = MatchedSizeChange(curves[0], curves[v], p => p.LumaPsnr);
            psnrDelta[v][n] = MatchedSizeChange(curves[0], curves[v], p => p.Psnr);
            string points = string.Join(' ', curves[v].Select(p => FormattableString.Invariant($"{p.Bytes}/{p.Ssim:F4}/{p.LumaPsnr:F2}/{p.Psnr:F2}")));
            Console.WriteLine(FormattableString.Invariant(
                $"  {variants[v].Name,-32} Y-SSIM {ssimDelta[v][n],6:+0.0;-0.0}%  Y-PSNR {yPsnrDelta[v][n],6:+0.0;-0.0}%  RGB-PSNR {psnrDelta[v][n],6:+0.0;-0.0}%   bytes/Y-SSIM/Y-PSNR/RGB-PSNR: {points}"));
        }
    }

    Console.WriteLine();
    Console.WriteLine("| Variant | Size at matched Y-SSIM | Size at matched Y-PSNR | Size at matched RGB-PSNR | Encode time vs default |");
    Console.WriteLine("|---|---:|---:|---:|---:|");
    for (int v = 0; v < variants.Length; v++)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"| {variants[v].Name} | {Mean(ssimDelta[v]):+0.0;-0.0} % | {Mean(yPsnrDelta[v]):+0.0;-0.0} % | {Mean(psnrDelta[v]):+0.0;-0.0} % | {encodeMs[v] / encodeMs[0]:F2}x |"));
    }

    return 0;
}

static double Mean(double[] values)
{
    double[] finite = values.Where(double.IsFinite).ToArray();
    return finite.Length == 0 ? double.NaN : finite.Average();
}

// Mean relative size change of `candidate` versus `reference` at equal metric: log(size) is interpolated piecewise
// linearly as a function of the metric for both curves and the difference is averaged over the metric range both
// curves cover (a Bjontegaard-style delta rate with linear instead of cubic interpolation).
static double MatchedSizeChange(List<Point> reference, List<Point> candidate, Func<Point, double> metric)
{
    (double[] mr, double[] lr) = Curve(reference, metric);
    (double[] mc, double[] lc) = Curve(candidate, metric);
    double low = Math.Max(mr[0], mc[0]);
    double high = Math.Min(mr[^1], mc[^1]);
    if (!(high > low))
    {
        return double.NaN;
    }

    const int Samples = 200;
    double sum = 0;
    for (int i = 0; i <= Samples; i++)
    {
        double m = low + ((high - low) * i / Samples);
        sum += Interpolate(mc, lc, m) - Interpolate(mr, lr, m);
    }

    return (Math.Exp(sum / (Samples + 1)) - 1) * 100;
}

// The curve as (metric, log size) sorted by metric, keeping only points that improve on the previous metric.
static (double[] Metric, double[] LogSize) Curve(List<Point> points, Func<Point, double> metric)
{
    var sorted = points.OrderBy(p => p.Bytes).ToList();
    var m = new List<double>();
    var l = new List<double>();
    foreach (Point p in sorted)
    {
        double value = metric(p);
        if (m.Count == 0 || value > m[^1])
        {
            m.Add(value);
            l.Add(Math.Log(p.Bytes));
        }
    }

    return (m.ToArray(), l.ToArray());
}

static double Interpolate(double[] x, double[] y, double at)
{
    int i = 1;
    while (i < x.Length - 1 && x[i] < at)
    {
        i++;
    }

    double t = (at - x[i - 1]) / (x[i] - x[i - 1]);
    return y[i - 1] + (t * (y[i] - y[i - 1]));
}

// BT.601 luma in 0..255.
static float[] Luma(Image<Rgb24> image)
{
    var luma = new float[image.Width * image.Height];
    for (int y = 0; y < image.Height; y++)
    {
        Span<Rgb24> row = image.Frames.RootFrame.GetRowSpan(y);
        for (int x = 0; x < image.Width; x++)
        {
            luma[(y * image.Width) + x] = (0.299f * row[x].R) + (0.587f * row[x].G) + (0.114f * row[x].B);
        }
    }

    return luma;
}

static double LumaPsnr(float[] expected, float[] actual)
{
    double squaredError = 0;
    for (int i = 0; i < expected.Length; i++)
    {
        double d = expected[i] - actual[i];
        squaredError += d * d;
    }

    double mse = squaredError / expected.Length;
    return mse <= 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
}

static double Psnr(Image<Rgb24> expected, Image<Rgb24> actual)
{
    double squaredError = 0;
    for (int y = 0; y < expected.Height; y++)
    {
        Span<Rgb24> a = expected.Frames.RootFrame.GetRowSpan(y);
        Span<Rgb24> b = actual.Frames.RootFrame.GetRowSpan(y);
        for (int x = 0; x < expected.Width; x++)
        {
            int dr = a[x].R - b[x].R;
            int dg = a[x].G - b[x].G;
            int db = a[x].B - b[x].B;
            squaredError += (dr * dr) + (dg * dg) + (db * db);
        }
    }

    double mse = squaredError / ((double)expected.Width * expected.Height * 3);
    return mse <= 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
}

// Mean SSIM (Wang, Bovik, Sheikh and Simoncelli 2004) of two luma planes: 11x11 Gaussian window with sigma 1.5,
// K1 = 0.01, K2 = 0.03, L = 255, averaged over the positions where the window fits inside the image.
static double Ssim(float[] a, float[] b, int width, int height)
{
    const int Radius = 5;
    if (width <= 2 * Radius || height <= 2 * Radius)
    {
        return double.NaN;
    }

    var kernel = new double[(2 * Radius) + 1];
    double total = 0;
    for (int i = -Radius; i <= Radius; i++)
    {
        kernel[i + Radius] = Math.Exp(-(i * i) / (2 * 1.5 * 1.5));
        total += kernel[i + Radius];
    }

    for (int i = 0; i < kernel.Length; i++)
    {
        kernel[i] /= total;
    }

    int n = width * height;
    var products = new double[5][];
    for (int k = 0; k < 5; k++)
    {
        products[k] = new double[n];
    }

    for (int i = 0; i < n; i++)
    {
        double x = a[i];
        double y = b[i];
        products[0][i] = x;
        products[1][i] = y;
        products[2][i] = x * x;
        products[3][i] = y * y;
        products[4][i] = x * y;
    }

    // Separable filtering, horizontal then vertical, valid region only.
    int outWidth = width - (2 * Radius);
    int outHeight = height - (2 * Radius);
    var filtered = new double[5][];
    var horizontal = new double[outWidth * height];
    for (int k = 0; k < 5; k++)
    {
        double[] src = products[k];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                double s = 0;
                int baseIndex = (y * width) + x;
                for (int t = 0; t < kernel.Length; t++)
                {
                    s += kernel[t] * src[baseIndex + t];
                }

                horizontal[(y * outWidth) + x] = s;
            }
        }

        var dst = new double[outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                double s = 0;
                for (int t = 0; t < kernel.Length; t++)
                {
                    s += kernel[t] * horizontal[((y + t) * outWidth) + x];
                }

                dst[(y * outWidth) + x] = s;
            }
        }

        filtered[k] = dst;
    }

    const double C1 = 0.01 * 255 * 0.01 * 255;
    const double C2 = 0.03 * 255 * 0.03 * 255;
    double sum = 0;
    for (int i = 0; i < outWidth * outHeight; i++)
    {
        double mx = filtered[0][i];
        double my = filtered[1][i];
        double vx = filtered[2][i] - (mx * mx);
        double vy = filtered[3][i] - (my * my);
        double cxy = filtered[4][i] - (mx * my);
        sum += ((2 * mx * my) + C1) * ((2 * cxy) + C2) / (((mx * mx) + (my * my) + C1) * (vx + vy + C2));
    }

    return sum / (outWidth * outHeight);
}

internal readonly record struct Point(long Bytes, double Ssim, double LumaPsnr, double Psnr);
