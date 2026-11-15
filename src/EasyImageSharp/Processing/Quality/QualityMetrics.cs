namespace EasyImageSharp.Processing;

/// <summary>Which channels a full-reference metric in <see cref="ImageQuality"/> compares.</summary>
public enum QualityChannels
{
    /// <summary>BT.601 luma <c>0.299 R' + 0.587 G' + 0.114 B'</c> of the encoded values.</summary>
    Luma,

    /// <summary>Red, green and blue separately: SSIM and MS-SSIM average the three, PSNR pools their errors.</summary>
    Rgb,
}

/// <summary>Focus measurements of one image; see <see cref="ImageQuality.MeasureSharpness"/>.</summary>
public sealed class SharpnessMetrics
{
    internal SharpnessMetrics(double laplacianVariance, double tenengrad)
    {
        this.LaplacianVariance = laplacianVariance;
        this.Tenengrad = tenengrad;
    }

    /// <summary>
    /// Variance of the four-neighbour Laplacian <c>[0 1 0; 1 -4 1; 0 1 0]</c> of the luma in 8-bit units
    /// (squared). The common blur heuristic: low values indicate little high-frequency detail.
    /// </summary>
    public double LaplacianVariance { get; }

    /// <summary>Mean of <c>Gx^2 + Gy^2</c> for the 3 x 3 Sobel gradients of the luma in 8-bit units (squared).</summary>
    public double Tenengrad { get; }
}

/// <summary>Tonal distribution of one image; see <see cref="ImageQuality.MeasureExposure"/>.</summary>
public sealed class ExposureMetrics
{
    private readonly long[] histogram;

    internal ExposureMetrics(long pixelCount, double meanLuma, long black, long white, long anyLow, long anyHigh, long[] histogram)
    {
        this.PixelCount = pixelCount;
        this.MeanLuma = meanLuma;
        double total = Math.Max(1, pixelCount);
        this.ShadowClipping = black / total;
        this.HighlightClipping = white / total;
        this.ChannelShadowClipping = anyLow / total;
        this.ChannelHighlightClipping = anyHigh / total;
        this.histogram = histogram;
    }

    /// <summary>The number of pixels measured (fully transparent pixels are excluded).</summary>
    public long PixelCount { get; }

    /// <summary>Mean BT.601 luma in 0-1; 0 when no pixel was measured.</summary>
    public double MeanLuma { get; }

    /// <summary>Fraction of pixels whose three colour channels are all clipped to black.</summary>
    public double ShadowClipping { get; }

    /// <summary>Fraction of pixels whose three colour channels are all clipped to full scale (pure white).</summary>
    public double HighlightClipping { get; }

    /// <summary>Fraction of pixels with at least one colour channel clipped to 0 (also counts saturated colours).</summary>
    public double ChannelShadowClipping { get; }

    /// <summary>Fraction of pixels with at least one colour channel clipped to full scale (also counts saturated colours).</summary>
    public double ChannelHighlightClipping { get; }

    /// <summary>The 1st luma percentile in 0-1.</summary>
    public double Percentile1 => this.GetPercentile(1);

    /// <summary>The 5th luma percentile in 0-1.</summary>
    public double Percentile5 => this.GetPercentile(5);

    /// <summary>The median luma in 0-1.</summary>
    public double Median => this.GetPercentile(50);

    /// <summary>The 95th luma percentile in 0-1.</summary>
    public double Percentile95 => this.GetPercentile(95);

    /// <summary>The 99th luma percentile in 0-1.</summary>
    public double Percentile99 => this.GetPercentile(99);

    /// <summary>
    /// The luma below which <paramref name="percent"/> percent of the measured pixels lie: the upper edge of the
    /// first bin of a 4096-bin histogram whose cumulative count reaches <c>ceil(percent / 100 * PixelCount)</c>,
    /// so the value is accurate to 1/4096. Returns 0 when no pixel was measured.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is not in 0-100.</exception>
    public double GetPercentile(double percent)
    {
        if (!(percent >= 0 && percent <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percentile must be between 0 and 100.");
        }

        if (this.PixelCount == 0)
        {
            return 0;
        }

        long rank = Math.Max(1, (long)Math.Ceiling(percent / 100 * this.PixelCount));
        long cumulative = 0;
        for (int i = 0; i < this.histogram.Length; i++)
        {
            cumulative += this.histogram[i];
            if (cumulative >= rank)
            {
                return (i + 1) / (double)this.histogram.Length;
            }
        }

        return 1;
    }
}
