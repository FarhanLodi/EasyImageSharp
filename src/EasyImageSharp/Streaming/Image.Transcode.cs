using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;

namespace EasyImageSharp;

public abstract partial class Image
{
    /// <summary>
    /// Decodes <paramref name="input"/>, optionally resizes it, turns it upright and re-encodes it to
    /// <paramref name="output"/>, row by row wherever the formats allow, so a large image can be converted or
    /// thumbnailed in a few megabytes instead of the size of its frame.
    /// </summary>
    /// <param name="input">The encoded image, read from its current position; left open.</param>
    /// <param name="output">The stream the result is written to; left open and not flushed.</param>
    /// <param name="options">The resize, encoder, limits and metadata policy; <see langword="null"/> re-encodes the image as it is.</param>
    /// <param name="cancellationToken">Cancels the transcode at its next row or chunk.</param>
    /// <returns>The source and output sizes and formats, and which ends were streamed.</returns>
    /// <remarks>
    /// <para>
    /// <b>What streams.</b> Non-interlaced, non-animated PNG and sequential (baseline or extended, single-scan)
    /// JPEG are decoded row by row. PNG output is encoded row by row for every option except palette, sub-8-bit
    /// grayscale and interlaced output, and JPEG output for every option except progressive and trellis
    /// quantisation combined with optimised Huffman tables (optimised tables alone stream, holding the compact
    /// Huffman symbols rather than the pixels until the last row). Every other format, and
    /// those PNG variants, still works: the source is decoded in full first, or the output is collected and given
    /// to the regular encoder. <see cref="TranscodeResult.StreamedDecode"/> and
    /// <see cref="TranscodeResult.StreamedEncode"/> report which happened. Every <see cref="ResizeMode"/> and
    /// resampler streams, with <see cref="ResizeOptions.Compand"/> and <see cref="ResizeOptions.PremultiplyAlpha"/>.
    /// </para>
    /// <para>
    /// <b>What comes out.</b> The pixels are exactly those of
    /// <c>Image.Load(input).Mutate(ctx =&gt; ctx.AutoOrient().Resize(options.Resize))</c> saved with the same encoder,
    /// in <see cref="Rgba32"/>, for the root frame: bit-identical when no orientation applies, and within one level
    /// per channel when one does (the resize then runs before the rotation rather than after it, which only changes
    /// the order of the floating-point additions). The exception is a streamed JPEG that the resize shrinks at
    /// least fourfold in both axes: it is decoded at 1/2, 1/4 or 1/8 size through the reduced inverse DCT (always keeping at
    /// least twice the target size) and then resized, which is faster and uses less memory but is not
    /// bit-identical to a full-size decode followed by the resize. A multi-frame source contributes its root
    /// frame, and the output is always a single still image.
    /// </para>
    /// <para>
    /// <b>Memory.</b> A streamed decode holds a few source rows and the resize window instead of the frame; a
    /// non-seekable input is read once, front to back. Limits are described on <see cref="TranscodeOptions"/>. If the
    /// transcode fails, <paramref name="output"/> may already hold part of the result.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A stream cannot be read or written, or the resize options are invalid.</exception>
    /// <exception cref="UnknownImageFormatException">The input is not in a format the library recognises.</exception>
    /// <exception cref="InvalidImageContentException">The input is malformed or truncated.</exception>
    /// <exception cref="ImageSizeLimitExceededException">The source or output exceeds a configured limit.</exception>
    /// <exception cref="NotSupportedException">The input uses an unsupported feature, or the encoder options are invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static TranscodeResult Transcode(
        Stream input, Stream output, TranscodeOptions? options = null, CancellationToken cancellationToken = default)
        => TranscodePipeline<Rgba32>.Run(input, output, options, cancellationToken);

    /// <summary>
    /// Asynchronously decodes, optionally resizes, orients and re-encodes an image, with the same results and
    /// limits as <see cref="Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/>.
    /// </summary>
    /// <param name="input">The encoded image, read from its current position; left open.</param>
    /// <param name="output">The stream the result is written to; left open and flushed at the end.</param>
    /// <param name="options">The resize, encoder, limits and metadata policy; <see langword="null"/> re-encodes the image as it is.</param>
    /// <param name="cancellationToken">Cancels the transcode at its next read, write, row or chunk.</param>
    /// <returns>The source and output sizes and formats, and which ends were streamed.</returns>
    /// <remarks>
    /// Input is read ahead and output written behind asynchronously, in a window of at least 128 KiB (32 bytes per
    /// source pixel of width, up to 8 MiB), while decoding, resizing and encoding run synchronously between the
    /// reads and writes. Reads a decoder makes beyond that window - the PNG chunk walk of a seekable input, or a
    /// single row whose data does not fit - are synchronous. An input that cannot be streamed is read in full
    /// asynchronously first; an encoder that needs the whole image writes into memory that is then copied out
    /// asynchronously.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A stream cannot be read or written, or the resize options are invalid.</exception>
    /// <exception cref="UnknownImageFormatException">The input is not in a format the library recognises.</exception>
    /// <exception cref="InvalidImageContentException">The input is malformed or truncated.</exception>
    /// <exception cref="ImageSizeLimitExceededException">The source or output exceeds a configured limit.</exception>
    /// <exception cref="NotSupportedException">The input uses an unsupported feature, or the encoder options are invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<TranscodeResult> TranscodeAsync(
        Stream input, Stream output, TranscodeOptions? options = null, CancellationToken cancellationToken = default)
        => TranscodePipeline<Rgba32>.RunAsync(input, output, options, cancellationToken);

    /// <summary>
    /// Writes a thumbnail of <paramref name="input"/> that fits inside <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/>, upright, with the aspect ratio kept, streaming wherever the formats allow.
    /// </summary>
    /// <param name="input">The encoded image, read from its current position; left open.</param>
    /// <param name="output">The stream the thumbnail is written to; left open and not flushed.</param>
    /// <param name="maxWidth">The largest width of the thumbnail.</param>
    /// <param name="maxHeight">The largest height of the thumbnail.</param>
    /// <param name="encoder">The encoder; <see langword="null"/> keeps the source's format when it can be encoded, else PNG.</param>
    /// <param name="cancellationToken">Cancels the operation at its next row or chunk.</param>
    /// <returns>The source and output sizes and formats, and which ends were streamed.</returns>
    /// <remarks>
    /// A shorthand for <see cref="Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/> with
    /// <see cref="ResizeMode.Max"/>, the default bicubic resampler, <see cref="TranscodeOptions.ShrinkOnly"/> (an image
    /// that already fits is not enlarged) and <see cref="TranscodeMetadata.KeepColorProfile"/>, so a thumbnail
    /// carries no camera or location data. Use <see cref="Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/>
    /// for anything else.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWidth"/> or <paramref name="maxHeight"/> is not positive.</exception>
    public static TranscodeResult Thumbnail(
        Stream input, Stream output, int maxWidth, int maxHeight, IImageEncoder? encoder = null, CancellationToken cancellationToken = default)
        => Transcode(input, output, ThumbnailOptions(maxWidth, maxHeight, encoder), cancellationToken);

    /// <summary>
    /// Asynchronously writes a thumbnail of <paramref name="input"/>, as
    /// <see cref="Thumbnail(Stream, Stream, int, int, IImageEncoder?, CancellationToken)"/> does.
    /// </summary>
    /// <param name="input">The encoded image, read from its current position; left open.</param>
    /// <param name="output">The stream the thumbnail is written to; left open and flushed at the end.</param>
    /// <param name="maxWidth">The largest width of the thumbnail.</param>
    /// <param name="maxHeight">The largest height of the thumbnail.</param>
    /// <param name="encoder">The encoder; <see langword="null"/> keeps the source's format when it can be encoded, else PNG.</param>
    /// <param name="cancellationToken">Cancels the operation at its next read, write, row or chunk.</param>
    /// <returns>The source and output sizes and formats, and which ends were streamed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWidth"/> or <paramref name="maxHeight"/> is not positive.</exception>
    public static Task<TranscodeResult> ThumbnailAsync(
        Stream input, Stream output, int maxWidth, int maxHeight, IImageEncoder? encoder = null, CancellationToken cancellationToken = default)
        => TranscodeAsync(input, output, ThumbnailOptions(maxWidth, maxHeight, encoder), cancellationToken);

    private static TranscodeOptions ThumbnailOptions(int maxWidth, int maxHeight, IImageEncoder? encoder)
    {
        Guard.MustBePositive(maxWidth, nameof(maxWidth));
        Guard.MustBePositive(maxHeight, nameof(maxHeight));
        return new TranscodeOptions
        {
            Resize = new ResizeOptions { Size = new Size(maxWidth, maxHeight), Mode = ResizeMode.Max },
            ShrinkOnly = true,
            Encoder = encoder,
            Metadata = TranscodeMetadata.KeepColorProfile,
        };
    }
}
