using System.Numerics;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// The pixel work behind <see cref="IImageProcessingContext.ToneMap(ToneMappingOptions)"/>. Curves are evaluated
/// in double precision per pixel, so the result is independent of row partitioning.
/// </summary>
internal static class ToneMappingOps
{
    /// <summary>Hable's default linear white.</summary>
    internal const double HableDefaultWhite = 11.2;

    /// <summary>
    /// Exposed values are capped here. Every curve has long since reached white, and the cap keeps the squares
    /// inside the rational curves finite, so an extreme exposure cannot turn a highlight into NaN.
    /// </summary>
    private const double MaxExposed = 1e100;

    public static void Validate(ToneMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!float.IsFinite(options.Exposure))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Exposure, "Exposure must be finite.");
        }

        if (options.WhitePoint is { } white && !(white > 0f && float.IsFinite(white)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), white, "WhitePoint must be positive and finite.");
        }

        if (options.ToneMapper is < ToneMapper.Clip or > ToneMapper.Hable)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ToneMapper, "Unknown tone mapper.");
        }
    }

    public static void Apply<TPixel>(ImageFrame<TPixel> frame, ToneMappingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double scale = Math.Min(Math.Pow(2.0, options.Exposure), MaxExposed);
        ToneMapper mapper = options.ToneMapper;
        double white = mapper switch
        {
            ToneMapper.ReinhardExtended => options.WhitePoint ?? Math.Min(MaxLuminance(frame) * scale, MaxExposed),
            ToneMapper.Hable => options.WhitePoint ?? HableDefaultWhite,
            _ => 1.0,
        };

        if (!(white > 0) || double.IsInfinity(white))
        {
            white = 1.0;
        }

        double hableWhite = mapper == ToneMapper.Hable ? Hable(white) : 1.0;
        ParallelRowIterator.IterateRows(frame.Width, frame.Height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> row = frame.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Vector4 linear = SceneLinearPixels.ToLinear(row[x]);
                    double r = Math.Min(Sanitize(linear.X) * scale, MaxExposed);
                    double g = Math.Min(Sanitize(linear.Y) * scale, MaxExposed);
                    double b = Math.Min(Sanitize(linear.Z) * scale, MaxExposed);
                    MapColor(mapper, ref r, ref g, ref b, white, hableWhite);
                    row[x] = TPixel.FromScaledVector4(new Vector4(
                        SceneLinearPixels.EncodeSrgb((float)r),
                        SceneLinearPixels.EncodeSrgb((float)g),
                        SceneLinearPixels.EncodeSrgb((float)b),
                        linear.W));
                }
            }
        });
    }

    /// <summary>Applies one curve to an exposed linear colour; the caller clamps and encodes the result.</summary>
    internal static void MapColor(ToneMapper mapper, ref double r, ref double g, ref double b, double white, double hableWhite)
    {
        switch (mapper)
        {
            case ToneMapper.Reinhard:
            case ToneMapper.ReinhardExtended:
            {
                double l = Luminance(r, g, b);
                if (l <= 0)
                {
                    r = g = b = 0;
                    return;
                }

                double mapped = mapper == ToneMapper.Reinhard
                    ? l / (1 + l)
                    : l * (1 + (l / (white * white))) / (1 + l);
                double factor = mapped / l;
                r *= factor;
                g *= factor;
                b *= factor;
                return;
            }

            case ToneMapper.AcesFilmic:
                r = Aces(r);
                g = Aces(g);
                b = Aces(b);
                return;
            case ToneMapper.Hable:
                r = Hable(r) / hableWhite;
                g = Hable(g) / hableWhite;
                b = Hable(b) / hableWhite;
                return;
            default:
                return;
        }
    }

    /// <summary>Rec. 709 luminance of linear RGB.</summary>
    internal static double Luminance(double r, double g, double b) => (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    internal static double Aces(double x) => (x * ((2.51 * x) + 0.03)) / ((x * ((2.43 * x) + 0.59)) + 0.14);

    internal static double Hable(double x)
    {
        const double A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
        return (((x * ((A * x) + (C * B))) + (D * E)) / ((x * ((A * x) + B)) + (D * F))) - (E / F);
    }

    /// <summary>Negative values and NaN become 0; positive infinity becomes the largest finite float.</summary>
    private static double Sanitize(float value)
        => value > 0f ? (float.IsPositiveInfinity(value) ? float.MaxValue : value) : 0.0;

    private static double MaxLuminance<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double max = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            OperationExecution.Checkpoint();
            foreach (TPixel pixel in frame.GetRowSpan(y))
            {
                Vector4 linear = SceneLinearPixels.ToLinear(pixel);
                max = Math.Max(max, Luminance(Sanitize(linear.X), Sanitize(linear.Y), Sanitize(linear.Z)));
            }
        }

        return max;
    }
}
