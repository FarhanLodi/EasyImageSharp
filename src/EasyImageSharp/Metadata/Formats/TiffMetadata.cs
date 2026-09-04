namespace EasyImageSharp.Metadata;

/// <summary>TIFF compression schemes (tag 259). Values not listed here are exposed as their raw number.</summary>
public enum TiffCompressionMethod : ushort
{
    None = 1,
    CcittModifiedHuffman = 2,
    CcittGroup3Fax = 3,
    CcittGroup4Fax = 4,
    Lzw = 5,
    OldJpeg = 6,
    Jpeg = 7,
    Deflate = 8,
    JBIG = 9,
    PackBits = 32773,
    DeflateLegacy = 32946,
}

/// <summary>TIFF photometric interpretations (tag 262).</summary>
public enum TiffPhotometricInterpretation : ushort
{
    WhiteIsZero = 0,
    BlackIsZero = 1,
    Rgb = 2,
    PaletteColor = 3,
    TransparencyMask = 4,
    Separated = 5,
    YCbCr = 6,
    CieLab = 8,
    IccLab = 9,
    ItuLab = 10,
    ColorFilterArray = 32803,
    LinearRaw = 34892,
}

/// <summary>TIFF predictors applied before compression (tag 317).</summary>
public enum TiffPredictor : ushort
{
    None = 1,
    Horizontal = 2,
    FloatingPoint = 3,
}

/// <summary>TIFF planar configurations (tag 284).</summary>
public enum TiffPlanarConfiguration : ushort
{
    /// <summary>Samples of a pixel are stored contiguously (chunky).</summary>
    Chunky = 1,

    /// <summary>Each sample is stored in its own plane.</summary>
    Planar = 2,
}

/// <summary>TIFF-specific image metadata.</summary>
public sealed class TiffMetadata : IFormatMetadata
{
    /// <summary>Creates TIFF metadata with default values.</summary>
    public TiffMetadata()
    {
    }

    private TiffMetadata(TiffMetadata other)
    {
        this.ByteOrder = other.ByteOrder;
        this.BigTiff = other.BigTiff;
    }

    /// <summary>The byte order of the decoded file. The encoder always writes little-endian.</summary>
    public ByteOrder ByteOrder { get; set; } = ByteOrder.LittleEndian;

    /// <summary>
    /// True when the decoded file was BigTIFF (version 43: an 8-byte offset container). The encoder always writes
    /// classic TIFF (version 42) regardless of this flag.
    /// </summary>
    public bool BigTiff { get; set; }

    public TiffMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>Per-page TIFF metadata describing how the page's samples were stored in the file.</summary>
public sealed class TiffFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public TiffFrameMetadata()
    {
    }

    private TiffFrameMetadata(TiffFrameMetadata other)
    {
        this.BitsPerSample = other.BitsPerSample is null ? null : (ushort[])other.BitsPerSample.Clone();
        this.SamplesPerPixel = other.SamplesPerPixel;
        this.Compression = other.Compression;
        this.PhotometricInterpretation = other.PhotometricInterpretation;
        this.Predictor = other.Predictor;
        this.PlanarConfiguration = other.PlanarConfiguration;
        this.RowsPerStrip = other.RowsPerStrip;
        this.Tiled = other.Tiled;
    }

    /// <summary>Bits per sample for each sample (tag 258), or <see langword="null"/> when not decoded from TIFF.</summary>
    public ushort[]? BitsPerSample { get; set; }

    /// <summary>Samples per pixel (tag 277).</summary>
    public ushort SamplesPerPixel { get; set; }

    /// <summary>The compression scheme (tag 259).</summary>
    public TiffCompressionMethod Compression { get; set; } = TiffCompressionMethod.None;

    /// <summary>The photometric interpretation (tag 262).</summary>
    public TiffPhotometricInterpretation PhotometricInterpretation { get; set; } = TiffPhotometricInterpretation.BlackIsZero;

    /// <summary>The predictor (tag 317).</summary>
    public TiffPredictor Predictor { get; set; } = TiffPredictor.None;

    /// <summary>The planar configuration (tag 284).</summary>
    public TiffPlanarConfiguration PlanarConfiguration { get; set; } = TiffPlanarConfiguration.Chunky;

    /// <summary>Rows per strip (tag 278), or <see langword="null"/> when absent or tiled.</summary>
    public uint? RowsPerStrip { get; set; }

    /// <summary>True when the page was stored as tiles rather than strips.</summary>
    public bool Tiled { get; set; }

    public TiffFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>BMP bit depths.</summary>
public enum BmpBitsPerPixel : ushort
{
    Pixel1 = 1,
    Pixel4 = 4,
    Pixel8 = 8,
    Pixel16 = 16,
    Pixel24 = 24,
    Pixel32 = 32,
}

/// <summary>BMP-specific metadata.</summary>
public sealed class BmpMetadata : IFormatMetadata
{
    /// <summary>Creates BMP metadata with default values.</summary>
    public BmpMetadata()
    {
    }

    private BmpMetadata(BmpMetadata other) => this.BitsPerPixel = other.BitsPerPixel;

    /// <summary>The bit depth of the decoded file (informational; the encoder writes 24-bit).</summary>
    public BmpBitsPerPixel BitsPerPixel { get; set; } = BmpBitsPerPixel.Pixel24;

    public BmpMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
