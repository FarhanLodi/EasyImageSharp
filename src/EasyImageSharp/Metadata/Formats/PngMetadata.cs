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

/// <summary>PNG-specific metadata: IHDR facts, gamma, the APNG animation control (acTL) and textual chunks.</summary>
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
        this.IsAnimated = other.IsAnimated;
        this.RepeatCount = other.RepeatCount;
        this.AnimateRootFrame = other.AnimateRootFrame;
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

    /// <summary>
    /// True when the file carries an APNG animation control chunk (acTL). Still images report false;
    /// setting it asks the encoder to write an animation even for a single-frame image.
    /// </summary>
    public bool IsAnimated { get; set; }

    /// <summary>
    /// The number of times the animation plays, from the acTL <c>num_plays</c> field: 0 means loop forever.
    /// Still images report 1 (play once). Unlike <see cref="GifMetadata.RepeatCount"/> and
    /// <see cref="EasyImageSharp.Formats.Webp.WebpMetadata.RepeatCount"/> this is a 32-bit value, because APNG stores
    /// <c>num_plays</c> as a uint32 rather than GIF's and WebP's 16 bits.
    /// </summary>
    public uint RepeatCount { get; set; } = 1;

    /// <summary>
    /// True when the IDAT image is also the animation's first frame, which is the case when an fcTL chunk
    /// precedes IDAT. False when the file places every fcTL after IDAT, making the IDAT image a still
    /// fallback that sits outside the animation and is not shown by an APNG-aware viewer.
    /// </summary>
    public bool AnimateRootFrame { get; set; } = true;

    /// <summary>tEXt/zTXt/iTXt entries in file order (excluding the XMP packet, see <see cref="ImageMetadata.XmpProfile"/>). Written back by the encoder.</summary>
    public IList<PngTextData> TextData { get; } = new List<PngTextData>();

    public PngMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>How an APNG frame is disposed of before the next one is rendered (the fcTL <c>dispose_op</c> field).</summary>
public enum PngDisposalMethod : byte
{
    /// <summary>Leave the frame's rectangle as it is; the next frame is drawn on top of it.</summary>
    None = 0,

    /// <summary>Clear the frame's rectangle to fully transparent black before the next frame is drawn.</summary>
    RestoreToBackground = 1,

    /// <summary>Restore the frame's rectangle to the contents it had before the frame was drawn.</summary>
    RestoreToPrevious = 2,
}

/// <summary>How an APNG frame is blended onto the output buffer (the fcTL <c>blend_op</c> field).</summary>
public enum PngBlendMethod : byte
{
    /// <summary>Overwrite the frame's rectangle with the frame's pixels, alpha included.</summary>
    Source = 0,

    /// <summary>Composite the frame's pixels over the canvas with source-over alpha blending.</summary>
    Over = 1,
}

/// <summary>
/// Per-frame PNG metadata from an APNG frame control chunk (fcTL). Every decoded frame is already
/// composited onto the full canvas, so the frame's sub-rectangle is not preserved; what survives is how
/// long the frame is shown and how it was combined with the canvas underneath.
/// </summary>
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

    /// <summary>What happened to the frame's rectangle after it was shown.</summary>
    public PngDisposalMethod DisposalMethod { get; set; }

    /// <summary>How the frame was combined with the canvas underneath.</summary>
    public PngBlendMethod BlendMethod { get; set; }

    public PngFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
