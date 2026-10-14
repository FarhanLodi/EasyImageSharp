using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Sampled document quality measurements. These are image statistics, not calibrated OCR accuracy predictions.</summary>
public sealed class DocumentAnalysis
{
    internal DocumentAnalysis(Size size, Size sampleSize, double mean, double deviation, double contrast,
        double dark, double bright, double laplacianVariance, double backgroundSpread, ulong perceptualHash)
    {
        this.Size = size;
        this.SampleSize = sampleSize;
        this.MeanLuminance = mean;
        this.LuminanceStandardDeviation = deviation;
        this.Contrast = contrast;
        this.DarkPixelFraction = dark;
        this.BrightPixelFraction = bright;
        this.LaplacianVariance = laplacianVariance;
        this.BackgroundSpread = backgroundSpread;
        this.PerceptualHash = perceptualHash;
    }

    public Size Size { get; }

    /// <summary>The dimensions of the sampled grid; compare sharpness only at comparable content and sampling scales.</summary>
    public Size SampleSize { get; }

    /// <summary>Mean display-referred BT.709 luminance in [0,1], after compositing transparency on white.</summary>
    public double MeanLuminance { get; }

    public double LuminanceStandardDeviation { get; }

    /// <summary>Difference between the 99th and 1st luminance percentiles in [0,1].</summary>
    public double Contrast { get; }

    /// <summary>Fraction at or below 0.02. Dark text also contributes; this does not establish underexposure.</summary>
    public double DarkPixelFraction { get; }

    /// <summary>Fraction at or above 0.98. White paper also contributes; this does not establish overexposure.</summary>
    public double BrightPixelFraction { get; }

    /// <summary>Variance of the four-neighbor Laplacian on the sampled grid, using normalized luminance. A relative sharpness statistic, not a blur probability.</summary>
    public double LaplacianVariance { get; }

    /// <summary>Spread of the 90th luminance percentiles of an 8x8 block grid; a heuristic for uneven illumination.</summary>
    public double BackgroundSpread { get; }

    /// <summary>64-bit horizontal difference hash over a 9x8 resampling of the diagnostic luminance grid. Useful for duplicate candidates, not proof of identical content.</summary>
    public ulong PerceptualHash { get; }

    /// <summary>One minus normalized Hamming distance of the difference hashes, in [0,1]. Uniform pages and different sparse text can collide; verify candidates using pixels or recognized text.</summary>
    public double PerceptualSimilarity(DocumentAnalysis other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return 1 - (BitOperations.PopCount(this.PerceptualHash ^ other.PerceptualHash) / 64.0);
    }

    /// <summary>True for nearly uniform samples (standard deviation below 0.005 and percentile contrast below 0.015). Sparse content may be missed by sampling.</summary>
    public bool IsLikelyBlank => this.LuminanceStandardDeviation < 0.005 && this.Contrast < 0.015;
}

/// <summary>Non-mutating document diagnostics and opt-in preprocessing.</summary>
public static partial class DocumentProcessingExtensions
{
    /// <summary>Measures each frame independently on a bounded grid. HDR values are clipped to display range for these diagnostics only.</summary>
    public static IReadOnlyList<DocumentAnalysis> AnalyzeDocument<TPixel>(this Image<TPixel> image,
        int maximumSampleDimension = 512, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        ValidateSampleDimension(maximumSampleDimension);
        using var execution = OperationExecution.Enter(cancellationToken: cancellationToken);
        cancellationToken = execution.CancellationToken;
        var results = new List<DocumentAnalysis>(image.Frames.Count);
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Analyze(frame, maximumSampleDimension, cancellationToken));
        }

        return results.AsReadOnly();
    }

    private static void ValidateSampleDimension(int value)
    {
        if (value < 8 || value > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Sample dimension must be between 8 and 4096.");
        }
    }

    private static DocumentAnalysis Analyze<TPixel>(ImageFrame<TPixel> frame, int maximumDimension, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double scale = Math.Min(1, maximumDimension / (double)Math.Max(frame.Width, frame.Height));
        int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var samples = new double[width * height];
        var histogram = new int[256];
        var blocks = new int[64][];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = new int[256];
        }

        double sum = 0, squared = 0;
        int dark = 0, bright = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<TPixel> source = frame.GetRowSpan(Math.Min(frame.Height - 1, (int)((y + 0.5) / scale)));
            for (int x = 0; x < width; x++)
            {
                Vector4 p = source[Math.Min(frame.Width - 1, (int)((x + 0.5) / scale))].ToScaledVector4();
                double alpha = Unit(p.W);
                double luminance = ((0.2126 * Unit(p.X)) + (0.7152 * Unit(p.Y)) + (0.0722 * Unit(p.Z))) * alpha + 1 - alpha;
                samples[(y * width) + x] = luminance;
                sum += luminance;
                squared += luminance * luminance;
                dark += luminance <= 0.02 ? 1 : 0;
                bright += luminance >= 0.98 ? 1 : 0;
                int bin = (int)Math.Round(luminance * 255);
                histogram[bin]++;
                blocks[((y * 8 / height) * 8) + (x * 8 / width)][bin]++;
            }
        }

        double laplacianSum = 0, laplacianSquared = 0;
        int laplacianCount = 0;
        for (int y = 1; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 1; x < width - 1; x++)
            {
                int i = (y * width) + x;
                double value = samples[i - 1] + samples[i + 1] + samples[i - width] + samples[i + width] - (4 * samples[i]);
                laplacianSum += value;
                laplacianSquared += value * value;
                laplacianCount++;
            }
        }

        double[] backgrounds = blocks.Where(b => b.Sum() > 0).Select(b => Percentile(b, 0.9)).ToArray();
        double mean = sum / samples.Length;
        double laplacianMean = laplacianCount == 0 ? 0 : laplacianSum / laplacianCount;
        return new DocumentAnalysis(new Size(frame.Width, frame.Height), new Size(width, height), mean,
            Math.Sqrt(Math.Max(0, (squared / samples.Length) - (mean * mean))),
            Percentile(histogram, 0.99) - Percentile(histogram, 0.01), dark / (double)samples.Length,
            bright / (double)samples.Length,
            laplacianCount == 0 ? 0 : Math.Max(0, (laplacianSquared / laplacianCount) - (laplacianMean * laplacianMean)),
            backgrounds.Max() - backgrounds.Min(), DifferenceHash(samples, width, height));
    }

    private static ulong DifferenceHash(double[] luminance, int width, int height)
    {
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        {
            int row = Math.Min(height - 1, (int)((y + 0.5) * height / 8)) * width;
            for (int x = 0; x < 8; x++)
            {
                int left = Math.Min(width - 1, (int)((x + 0.5) * width / 9));
                int right = Math.Min(width - 1, (int)((x + 1.5) * width / 9));
                if (luminance[row + left] > luminance[row + right])
                {
                    hash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return hash;
    }

    private static double Unit(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static double Percentile(int[] histogram, double fraction)
    {
        int rank = Math.Max(1, (int)Math.Ceiling(histogram.Sum() * fraction));
        int count = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            count += histogram[i];
            if (count >= rank)
            {
                return i / 255.0;
            }
        }

        return 1;
    }
}
