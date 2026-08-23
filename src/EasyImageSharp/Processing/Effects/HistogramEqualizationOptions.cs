namespace EasyImageSharp.Processing;

/// <summary>Options for <c>HistogramEqualization</c>.</summary>
public sealed class HistogramEqualizationOptions
{
    private int luminanceLevels = 256;
    private int clipLimit = 350;
    private int numberOfTiles = 8;

    /// <summary>The default options: global equalization over 256 levels.</summary>
    public static HistogramEqualizationOptions Default { get; } = new();

    /// <summary>The equalization strategy. Defaults to <see cref="HistogramEqualizationMethod.Global"/>.</summary>
    public HistogramEqualizationMethod Method { get; set; } = HistogramEqualizationMethod.Global;

    /// <summary>The number of luminance levels in the histogram (2-256). Defaults to 256.</summary>
    public int LuminanceLevels
    {
        get => this.luminanceLevels;
        set
        {
            if (value is < 2 or > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Luminance levels must be between 2 and 256.");
            }

            this.luminanceLevels = value;
        }
    }

    /// <summary>
    /// Whether the histogram is clipped at <see cref="ClipLimit"/> and the excess redistributed evenly before
    /// building the mapping (contrast limiting). Applies to every method. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ClipHistogram { get; set; }

    /// <summary>The maximum count a histogram bin may hold when <see cref="ClipHistogram"/> is set. Defaults to 350.</summary>
    public int ClipLimit
    {
        get => this.clipLimit;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Clip limit must be at least 1.");
            }

            this.clipLimit = value;
        }
    }

    /// <summary>
    /// The number of tiles along each axis for the adaptive methods (the sliding window uses the resulting
    /// tile size as its window size). Defaults to 8.
    /// </summary>
    public int NumberOfTiles
    {
        get => this.numberOfTiles;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Number of tiles must be at least 1.");
            }

            this.numberOfTiles = value;
        }
    }
}
