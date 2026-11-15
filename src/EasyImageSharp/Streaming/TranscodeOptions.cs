using EasyImageSharp.Formats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Streaming;

/// <summary>Which of the source's metadata a transcode writes to its output.</summary>
public enum TranscodeMetadata
{
    /// <summary>
    /// Everything the source carried that the output encoder can write: EXIF, ICC, XMP, resolution and
    /// format-specific entries such as PNG text chunks. When the pixels are turned upright the EXIF orientation is
    /// reset to 1, as <see cref="IImageProcessingContext.AutoOrient"/> does.
    /// </summary>
    Preserve,

    /// <summary>
    /// Only the ICC colour profile, so colours still display correctly; EXIF (including GPS position and camera
    /// details), XMP, text and resolution are dropped.
    /// </summary>
    KeepColorProfile,

    /// <summary>No metadata at all: the output carries the defaults of a new image.</summary>
    Strip,
}

/// <summary>
/// Settings for <see cref="Image.Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/>: the resize to apply,
/// the output encoder, the decoder limits and what to do with orientation and metadata. Instances are immutable
/// once built and may be shared between concurrent transcodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Limits.</b> A row-streamed source is never held in memory, so it is not subject to
/// <see cref="DecoderOptions.MaxPixels"/>, which bounds the frames a decode materialises. It is bounded instead
/// by <see cref="MaxSourcePixels"/>, which defaults to <see cref="DecoderOptions.MaxTotalPixels"/> - the number of
/// pixels one decode may produce in all - and is in any case at most 2 147 483 647 pixels. Everything the
/// pipeline <em>does</em> materialise stays under <see cref="DecoderOptions.MaxPixels"/>: a source decoded by a
/// fallback decoder (which must then satisfy both limits), and the output frame when an encoder or an
/// orientation needs it whole. The rows the pipeline holds - source scanlines, the resize window and output rows -
/// are estimated from the declared dimensions before any pixel is read, and must fit in four bytes per
/// <see cref="DecoderOptions.MaxPixels"/> (the size of a full RGBA frame at that limit), but never less than
/// 16 MiB; a source too wide for that raises <see cref="ImageSizeLimitExceededException"/>.
/// <see cref="DecoderOptions.MaxEncodedBytes"/>, cancellation and the execution options apply as they do to
/// <see cref="Image.Load(Stream, DecoderOptions)"/>.
/// </para>
/// </remarks>
public sealed class TranscodeOptions
{
    private readonly DecoderOptions decoderOptions = DecoderOptions.Default;
    private readonly long? maxSourcePixels;

    /// <summary>
    /// The resize to apply, with the same meaning as <see cref="IImageProcessingContext.Resize(ResizeOptions)"/>
    /// on the upright image; every <see cref="ResizeMode"/> streams. <see langword="null"/> (the default) keeps
    /// the source's size.
    /// </summary>
    public ResizeOptions? Resize { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a source whose upright size already fits inside <see cref="ResizeOptions.Size"/>
    /// (a zero dimension counting as unbounded) is not resized, padded or cropped at all, only re-encoded. Defaults to
    /// <see langword="false"/>, which applies <see cref="Resize"/> exactly as the in-memory resize does.
    /// </summary>
    public bool ShrinkOnly { get; init; }

    /// <summary>
    /// The encoder for the output; it wins over <see cref="Format"/>. When both are <see langword="null"/> the
    /// output has the source's format if the library can encode it, and is PNG otherwise.
    /// </summary>
    public IImageEncoder? Encoder { get; init; }

    /// <summary>The output format, encoded with its default encoder; ignored when <see cref="Encoder"/> is set.</summary>
    public ImageFormat? Format { get; init; }

    /// <summary>The decoder limits, custom formats and execution options. Defaults to <see cref="DecoderOptions.Default"/>.</summary>
    public DecoderOptions DecoderOptions
    {
        get => this.decoderOptions;
        init => this.decoderOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether the output is turned upright according to the source's EXIF orientation, with the lossless
    /// rotations and flips <see cref="IImageProcessingContext.AutoOrient"/> uses. The resize applies to the upright
    /// image. Defaults to <see langword="true"/>. A mirror streams; the other orientations hold the (resized)
    /// output in memory, under <see cref="DecoderOptions.MaxPixels"/>.
    /// </summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>Which of the source's metadata is written to the output. Defaults to <see cref="TranscodeMetadata.Preserve"/>.</summary>
    public TranscodeMetadata Metadata { get; init; } = TranscodeMetadata.Preserve;

    /// <summary>
    /// The largest source, in pixels, that is decoded row by row. <see langword="null"/> (the default) uses
    /// <see cref="DecoderOptions.MaxTotalPixels"/>. Checked against the declared dimensions before any pixel is
    /// decoded; see the remarks on <see cref="TranscodeOptions"/> for how it relates to the other limits.
    /// </summary>
    public long? MaxSourcePixels
    {
        get => this.maxSourcePixels;
        init
        {
            if (value is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxSourcePixels must be positive.");
            }

            this.maxSourcePixels = value;
        }
    }
}

/// <summary>What a transcode read and wrote.</summary>
public sealed class TranscodeResult
{
    internal TranscodeResult(ImageFormat sourceFormat, Size sourceSize, Size outputSize, bool streamedDecode, bool streamedEncode)
    {
        this.SourceFormat = sourceFormat;
        this.SourceSize = sourceSize;
        this.OutputSize = outputSize;
        this.StreamedDecode = streamedDecode;
        this.StreamedEncode = streamedEncode;
    }

    /// <summary>The format the input was decoded as.</summary>
    public ImageFormat SourceFormat { get; }

    /// <summary>The size of the source as stored, before any orientation.</summary>
    public Size SourceSize { get; }

    /// <summary>The size of the image that was encoded.</summary>
    public Size OutputSize { get; }

    /// <summary>
    /// True when the source was decoded row by row; false when its format or variant (for example an interlaced
    /// or animated PNG) has no row-streaming decoder and it was decoded in full first.
    /// </summary>
    public bool StreamedDecode { get; }

    /// <summary>
    /// True when the output was encoded row by row; false when the encoder or its options (for example palette
    /// PNG, or a format without a row encoder) needed the whole image, which was then collected first.
    /// </summary>
    public bool StreamedEncode { get; }

    public override string ToString()
        => FormattableString.Invariant(
            $"TranscodeResult [ Source={this.SourceFormat.Name} {this.SourceSize.Width}x{this.SourceSize.Height}, Output={this.OutputSize.Width}x{this.OutputSize.Height}, StreamedDecode={this.StreamedDecode}, StreamedEncode={this.StreamedEncode} ]");
}
