using System.Numerics;
using EasyImageSharp.Formats.Exr;

namespace EasyImageSharp.Metadata;

/// <summary>One channel of an OpenEXR file, as declared by its <c>channels</c> attribute.</summary>
public sealed class ExrChannelInfo
{
    /// <summary>Creates a channel description.</summary>
    /// <param name="name">The channel name, for example <c>R</c> or <c>diffuse.R</c>.</param>
    /// <param name="pixelType">The sample type.</param>
    /// <param name="xSampling">Horizontal subsampling: 1 stores every column.</param>
    /// <param name="ySampling">Vertical subsampling: 1 stores every row.</param>
    /// <param name="perceptuallyLinear">The channel's <c>pLinear</c> hint.</param>
    public ExrChannelInfo(string name, ExrPixelType pixelType, int xSampling = 1, int ySampling = 1, bool perceptuallyLinear = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xSampling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ySampling);
        this.Name = name;
        this.PixelType = pixelType;
        this.XSampling = xSampling;
        this.YSampling = ySampling;
        this.PerceptuallyLinear = perceptuallyLinear;
    }

    /// <summary>The channel name.</summary>
    public string Name { get; }

    /// <summary>The sample type.</summary>
    public ExrPixelType PixelType { get; }

    /// <summary>Horizontal subsampling: 1 stores every column.</summary>
    public int XSampling { get; }

    /// <summary>Vertical subsampling: 1 stores every row.</summary>
    public int YSampling { get; }

    /// <summary>The <c>pLinear</c> hint: true when the values are perceptually rather than physically linear.</summary>
    public bool PerceptuallyLinear { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{this.Name} ({this.PixelType}, {this.XSampling}x{this.YSampling})";
}

/// <summary>The CIE xy chromaticities of an OpenEXR file's RGB primaries and white point (the <c>chromaticities</c> attribute).</summary>
public sealed class ExrChromaticities
{
    /// <summary>Creates a set of chromaticities.</summary>
    public ExrChromaticities(Vector2 red, Vector2 green, Vector2 blue, Vector2 white)
    {
        this.Red = red;
        this.Green = green;
        this.Blue = blue;
        this.White = white;
    }

    /// <summary>The Rec. 709 primaries with a D65 white point, which OpenEXR assumes when the attribute is absent.</summary>
    public static ExrChromaticities Rec709 { get; } = new(new Vector2(0.64f, 0.33f), new Vector2(0.30f, 0.60f), new Vector2(0.15f, 0.06f), new Vector2(0.3127f, 0.3290f));

    /// <summary>The red primary.</summary>
    public Vector2 Red { get; }

    /// <summary>The green primary.</summary>
    public Vector2 Green { get; }

    /// <summary>The blue primary.</summary>
    public Vector2 Blue { get; }

    /// <summary>The white point.</summary>
    public Vector2 White { get; }
}

/// <summary>
/// OpenEXR header facts: compression, line order, data and display windows, tiling, the channel list, the
/// optional chromaticities and every <c>string</c>-typed attribute (such as <c>owner</c>, <c>comments</c> or
/// <c>capDate</c>).
/// </summary>
/// <remarks>
/// Chromaticities are reported, not applied: pixels are returned in the file's own RGB space, just as an embedded
/// ICC profile does not convert pixels elsewhere in the library. <see cref="ExrEncoder"/> writes
/// <see cref="Chromaticities"/> and <see cref="StringAttributes"/> back out when the image carries this metadata.
/// </remarks>
public sealed class ExrMetadata : IFormatMetadata
{
    /// <summary>Creates metadata with default values.</summary>
    public ExrMetadata()
    {
    }

    private ExrMetadata(ExrMetadata other)
    {
        this.Compression = other.Compression;
        this.LineOrder = other.LineOrder;
        this.DataWindow = other.DataWindow;
        this.DisplayWindow = other.DisplayWindow;
        this.PixelAspectRatio = other.PixelAspectRatio;
        this.IsTiled = other.IsTiled;
        this.TileSize = other.TileSize;
        this.LevelMode = other.LevelMode;
        this.Chromaticities = other.Chromaticities;
        foreach (ExrChannelInfo channel in other.Channels)
        {
            this.Channels.Add(channel);
        }

        foreach (KeyValuePair<string, string> pair in other.StringAttributes)
        {
            this.StringAttributes[pair.Key] = pair.Value;
        }
    }

    /// <summary>The compression method the file declares.</summary>
    public ExrCompression Compression { get; set; } = ExrCompression.Zip;

    /// <summary>The order in which the file stores its chunks.</summary>
    public ExrLineOrder LineOrder { get; set; } = ExrLineOrder.IncreasingY;

    /// <summary>The data window: the pixels the file actually stores, in the file's own (possibly negative) coordinates.</summary>
    public Rectangle DataWindow { get; set; }

    /// <summary>The display window: the frame the image is meant to be shown in, in the same coordinates.</summary>
    public Rectangle DisplayWindow { get; set; }

    /// <summary>The ratio of a pixel's width to its height.</summary>
    public float PixelAspectRatio { get; set; } = 1f;

    /// <summary>True for a tiled file, false for a scanline file.</summary>
    public bool IsTiled { get; set; }

    /// <summary>The tile size of a tiled file, or an empty size for a scanline file.</summary>
    public Size TileSize { get; set; }

    /// <summary>The resolution levels of a tiled file.</summary>
    public ExrLevelMode LevelMode { get; set; } = ExrLevelMode.OneLevel;

    /// <summary>The channel list in the file's (alphabetical) storage order.</summary>
    public IList<ExrChannelInfo> Channels { get; } = new List<ExrChannelInfo>();

    /// <summary>The <c>chromaticities</c> attribute, or <see langword="null"/> when the file relies on the Rec. 709 default.</summary>
    public ExrChromaticities? Chromaticities { get; set; }

    /// <summary>Every attribute of type <c>string</c>, keyed by attribute name.</summary>
    public IDictionary<string, string> StringAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ExrMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
