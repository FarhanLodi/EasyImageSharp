namespace EasyImageSharp.Metadata;

/// <summary>The colour model of a JPEG's components as signalled by the file.</summary>
public enum JpegColorType
{
    /// <summary>Not yet known (image not decoded from JPEG).</summary>
    Unknown = 0,

    /// <summary>Single-component grayscale.</summary>
    Grayscale,

    /// <summary>Three components stored as YCbCr (JFIF).</summary>
    YCbCr,

    /// <summary>Three components stored as RGB (Adobe transform 0 or component ids 'R','G','B').</summary>
    Rgb,

    /// <summary>Four components stored as CMYK (Adobe).</summary>
    Cmyk,

    /// <summary>Four components stored as YCCK (Adobe transform 2).</summary>
    Ycck,
}

/// <summary>JPEG-specific metadata: quality estimate, colour type, progressive flag and comment segments.</summary>
public sealed class JpegMetadata : IFormatMetadata
{
    /// <summary>Creates JPEG metadata with default values.</summary>
    public JpegMetadata()
    {
    }

    private JpegMetadata(JpegMetadata other)
    {
        this.Quality = other.Quality;
        this.ColorType = other.ColorType;
        this.Progressive = other.Progressive;
        this.Comments = new List<string>(other.Comments);
    }

    /// <summary>
    /// The encoding quality (1-100) estimated from the luminance quantization table by inverting the ITU-T T.81
    /// Annex K scaling, or <see langword="null"/> when the file has no quantization table. Files written with
    /// the standard tables (this library, libjpeg and most encoders) yield the exact quality they were saved with.
    /// </summary>
    public int? Quality { get; set; }

    /// <summary>The colour model of the encoded components.</summary>
    public JpegColorType ColorType { get; set; }

    /// <summary>True when the file used progressive (SOF2) encoding.</summary>
    public bool Progressive { get; set; }

    /// <summary>The COM (comment) segments in file order. Written back by the JPEG encoder.</summary>
    public IList<string> Comments { get; } = new List<string>();

    public JpegMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
