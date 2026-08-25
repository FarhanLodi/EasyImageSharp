using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Illumination and tone corrections for scanned or photographed pages.</summary>
internal static class IlluminationOps
{
    /// <summary>
    /// Estimates the paper background of a luminance plane: a grey-level closing with a square of the given
    /// radius removes ink strokes thinner than the element (background = local maximum then minimum), and a
    /// box blur of the same radius smooths the block artefacts.
    /// </summary>
    public static byte[] EstimateBackground(byte[] luminance, int width, int height, int radius)
    {
        StructuringElement element = StructuringElement.Square(radius);
        byte[] closed = MorphologyOps.Close(luminance, width, height, element);
        return IntegralImage.BoxBlur(closed, width, height, radius);
    }

    /// <summary>A background radius of about 1/25 of the smaller dimension, at least 8 px.</summary>
    public static int AutoBackgroundRadius(int width, int height) => Math.Max(8, Math.Min(width, height) / 25);

    /// <summary>
    /// Divides every channel by the estimated background (from the luminance) and rescales so the background
    /// becomes white: <c>out = min(255, in * 255 / max(background, 1))</c>.
    /// </summary>
    public static void BackgroundNormalize<TPixel>(ImageFrame<TPixel> frame, int backgroundRadius)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        if (backgroundRadius <= 0)
        {
            backgroundRadius = AutoBackgroundRadius(width, height);
        }

        byte[] luminance = DocumentPlanes.Luminance(frame);
        byte[] background = EstimateBackground(luminance, width, height, backgroundRadius);
        DivideByBackground(frame, background);
    }

    /// <summary>
    /// Shadow removal: the background is estimated with a large radius (1/8 of the smaller dimension) so that
    /// even wide soft shadows are captured, then divided out as in <see cref="BackgroundNormalize{TPixel}"/>.
    /// </summary>
    public static void RemoveShadows<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int radius = Math.Max(12, Math.Min(width, height) / 8);
        byte[] luminance = DocumentPlanes.Luminance(frame);
        byte[] background = EstimateBackground(luminance, width, height, radius);
        DivideByBackground(frame, background);
    }

    private static void DivideByBackground<TPixel>(ImageFrame<TPixel> frame, byte[] background)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(y);
                PixelOps.ToRgba32<TPixel>(pixels, row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    float gain = 255f / Math.Max(1, (int)background[offset + x]);
                    Rgba32 p = row[x];
                    row[x] = new Rgba32(Scale(p.R, gain), Scale(p.G, gain), Scale(p.B, gain), p.A);
                }

                PixelOps.FromRgba32<TPixel>(row, pixels);
            }
        });
    }

    private static byte Scale(byte value, float gain) => (byte)Math.Min(255, (int)((value * gain) + 0.5f));

    /// <summary>
    /// Linear stretch of all channels mapping the luminance at <paramref name="lowPercentile"/> to 0 and at
    /// <paramref name="highPercentile"/> to 255 (values outside are clipped).
    /// </summary>
    public static void ContrastStretch<TPixel>(ImageFrame<TPixel> frame, float lowPercentile, float highPercentile)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (lowPercentile < 0 || highPercentile > 100 || lowPercentile >= highPercentile)
        {
            throw new ArgumentOutOfRangeException(nameof(lowPercentile), "Percentiles must satisfy 0 <= low < high <= 100.");
        }

        byte[] luminance = DocumentPlanes.Luminance(frame);
        int[] histogram = DocumentPlanes.Histogram(luminance);
        int low = DocumentPlanes.Percentile(histogram, luminance.Length, lowPercentile);
        int high = DocumentPlanes.Percentile(histogram, luminance.Length, highPercentile);
        DocumentPlanes.ApplyLut(frame, StretchLut(low, high));
    }

    /// <summary>
    /// Per-channel stretch: each of R, G and B is stretched independently between its 0.5th and 99.5th percentiles,
    /// which also neutralises a colour cast. Grey images get the same mapping on every channel.
    /// </summary>
    public static void AutoLevels<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        var hr = new int[256];
        var hg = new int[256];
        var hb = new int[256];
        var row = new Rgba32[width];
        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
            for (int x = 0; x < width; x++)
            {
                hr[row[x].R]++;
                hg[row[x].G]++;
                hb[row[x].B]++;
            }
        }

        long total = (long)width * height;
        DocumentPlanes.ApplyLut(
            frame,
            StretchLut(DocumentPlanes.Percentile(hr, total, 0.5), DocumentPlanes.Percentile(hr, total, 99.5)),
            StretchLut(DocumentPlanes.Percentile(hg, total, 0.5), DocumentPlanes.Percentile(hg, total, 99.5)),
            StretchLut(DocumentPlanes.Percentile(hb, total, 0.5), DocumentPlanes.Percentile(hb, total, 99.5)));
    }

    /// <summary>Stretches the luminance range [min, max] to [0, 255] without clipping.</summary>
    public static void Normalize<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] luminance = DocumentPlanes.Luminance(frame);
        int low = 255;
        int high = 0;
        foreach (byte v in luminance)
        {
            if (v < low)
            {
                low = v;
            }

            if (v > high)
            {
                high = v;
            }
        }

        DocumentPlanes.ApplyLut(frame, StretchLut(low, high));
    }

    /// <summary>Gamma correction <c>out = 255 (in / 255)^(1 / gamma)</c>: gamma above 1 brightens midtones, below 1 darkens them.</summary>
    public static void Gamma<TPixel>(ImageFrame<TPixel> frame, float gamma)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (!(gamma > 0) || float.IsInfinity(gamma))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "Gamma must be positive and finite.");
        }

        var lut = new byte[256];
        double exponent = 1.0 / gamma;
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp((int)Math.Round(255.0 * Math.Pow(i / 255.0, exponent)), 0, 255);
        }

        DocumentPlanes.ApplyLut(frame, lut);
    }

    private static byte[] StretchLut(int low, int high)
    {
        var lut = new byte[256];
        if (high <= low)
        {
            for (int i = 0; i < 256; i++)
            {
                lut[i] = (byte)i;
            }

            return lut;
        }

        double scale = 255.0 / (high - low);
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp((int)Math.Round((i - low) * scale), 0, 255);
        }

        return lut;
    }
}
