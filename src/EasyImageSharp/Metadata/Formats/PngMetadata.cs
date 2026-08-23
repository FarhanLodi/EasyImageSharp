using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata.Exif;

namespace EasyImageSharp.Metadata;

/// <summary>A PNG textual chunk (tEXt, zTXt or iTXt).</summary>
public readonly struct PngTextData : IEquatable<PngTextData>
{
    /// <summary>Creates a Latin-1 text entry (written as tEXt).</summary>
    public PngTextData(string keyword, string value)
        : this(keyword, value, string.Empty, string.Empty)
    {
    }

    /// <summary>Creates an international text entry (written as iTXt when a language tag or translated keyword is set, or the text is not Latin-1).</summary>
    public PngTextData(string keyword, string value, string languageTag, string translatedKeyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.Length is < 1 or > 79)
        {
            throw new ArgumentException("A PNG text keyword must be 1 to 79 characters long.", nameof(keyword));
        }

        this.Keyword = keyword;
        this.Value = value ?? string.Empty;
        this.LanguageTag = languageTag ?? string.Empty;
        this.TranslatedKeyword = translatedKeyword ?? string.Empty;
    }

    /// <summary>The keyword (1-79 Latin-1 characters), e.g. "Title", "Author", "Comment".</summary>
    public string Keyword { get; }

    /// <summary>The text.</summary>
    public string Value { get; }

    /// <summary>The RFC 3066 language tag of an iTXt chunk, or an empty string.</summary>
    public string LanguageTag { get; }

    /// <summary>The keyword translated into <see cref="LanguageTag"/>, or an empty string.</summary>
    public string TranslatedKeyword { get; }

    public bool Equals(PngTextData other)
        => this.Keyword == other.Keyword && this.Value == other.Value
            && this.LanguageTag == other.LanguageTag && this.TranslatedKeyword == other.TranslatedKeyword;

    public override bool Equals(object? obj) => obj is PngTextData other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Keyword, this.Value, this.LanguageTag, this.TranslatedKeyword);

    public static bool operator ==(PngTextData left, PngTextData right) => left.Equals(right);

    public static bool operator !=(PngTextData left, PngTextData right) => !left.Equals(right);

    public override string ToString() => $"{this.Keyword}: {this.Value}";
}

/// <summary>PNG-specific metadata: IHDR facts, gamma and textual chunks.</summary>
public sealed class PngMetadata : IFormatMetadata
{
    /// <summary>Creates PNG metadata with default values.</summary>
    public PngMetadata()
    {
    }

    private PngMetadata(PngMetadata other)
    {
        this.ColorType = other.ColorType;
        this.BitDepth = other.BitDepth;
        this.Gamma = other.Gamma;
        this.Interlaced = other.Interlaced;
        this.TextData = new List<PngTextData>(other.TextData);
    }

    /// <summary>The colour type of the decoded file (informational; the encoder chooses from the pixel format).</summary>
    public PngColorType? ColorType { get; set; }

    /// <summary>The bit depth of the decoded file (informational; the encoder writes 8 bits).</summary>
    public PngBitDepth? BitDepth { get; set; }

    /// <summary>The gAMA value (image gamma, e.g. 0.45455), or <see langword="null"/>. Written back as a gAMA chunk when set.</summary>
    public float? Gamma { get; set; }

    /// <summary>True when the decoded file was Adam7-interlaced (informational).</summary>
    public bool Interlaced { get; set; }

    /// <summary>tEXt/zTXt/iTXt entries in file order (excluding the XMP packet, see <see cref="ImageMetadata.XmpProfile"/>). Written back by the encoder.</summary>
    public IList<PngTextData> TextData { get; } = new List<PngTextData>();

    public PngMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>How an APNG frame is disposed of before the next one is rendered.</summary>
public enum PngDisposalMethod : byte
{
    None = 0,
    RestoreToBackground = 1,
    RestoreToPrevious = 2,
}

/// <summary>How an APNG frame is blended onto the output buffer.</summary>
public enum PngBlendMethod : byte
{
    Source = 0,
    Over = 1,
}

/// <summary>Per-frame PNG metadata (reserved for animated PNG; the current decoder produces a single frame).</summary>
public sealed class PngFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public PngFrameMetadata()
    {
    }

    private PngFrameMetadata(PngFrameMetadata other)
    {
        this.FrameDelay = other.FrameDelay;
        this.DisposalMethod = other.DisposalMethod;
        this.BlendMethod = other.BlendMethod;
    }

    /// <summary>The frame delay in seconds as a fraction; defaults to 0/100.</summary>
    public Rational FrameDelay { get; set; } = new(0, 100);

    public PngDisposalMethod DisposalMethod { get; set; }

    public PngBlendMethod BlendMethod { get; set; }

    public PngFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
