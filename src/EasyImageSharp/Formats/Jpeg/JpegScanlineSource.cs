using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Produces a sequential JPEG top to bottom, one row at a time, with sample memory bounded by a few MCU rows
/// rather than by the image: the input is read through a sliding window and each component keeps three MCU rows.
/// </summary>
/// <remarks>
/// <para>
/// Covered are baseline and extended sequential Huffman frames with 8-bit or 12-bit samples whose components all
/// arrive in one interleaved scan (or a single-component frame), at full size or reduced 1/2, 1/4 or 1/8 through
/// the reduced inverse DCT. The rows are identical to those a full decode of the same file produces, because both
/// run the same inverse transform, upsampling and colour conversion.
/// </para>
/// <para>
/// Progressive, lossless and multi-scan sequential files need the whole frame before the first row exists:
/// <see cref="TryCreate"/> returns false for them without allocating sample memory, after rewinding a seekable
/// stream to where it started so the caller can hand it to the full decoder. A non-seekable stream cannot be
/// rewound; a caller that may need to fall back should buffer such input first.
/// </para>
/// </remarks>
internal static class JpegScanlineSource
{
    /// <summary>
    /// Reads the header of the JPEG at the current position of <paramref name="stream"/> and, when the frame can be
    /// produced row by row, returns a source for it. The source owns no reference to the caller's stream beyond
    /// reading it and does not dispose it.
    /// </summary>
    /// <param name="stream">The encoded image, positioned at its SOI marker.</param>
    /// <param name="options">Limits applied before anything is allocated, and the execution policy.</param>
    /// <param name="scaleDenominator">1, 2, 4 or 8: the factor the reduced inverse DCT shrinks the image by.</param>
    /// <param name="source">The row source, or null when this returns false.</param>
    /// <returns>False when the frame type cannot be streamed; the caller then decodes the image whole.</returns>
    /// <exception cref="InvalidImageContentException">The header is malformed or truncated.</exception>
    /// <exception cref="ImageSizeLimitExceededException">The frame exceeds the configured limits.</exception>
    /// <exception cref="NotSupportedException">The file uses a JPEG feature this library does not decode.</exception>
    public static bool TryCreate<TPixel>(Stream stream, DecoderOptions options, int scaleDenominator, out IScanlineSource<TPixel>? source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (scaleDenominator is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleDenominator), "JPEG scale denominator must be 1, 2, 4, or 8.");
        }

        source = null;
        long start = stream.CanSeek ? stream.Position : -1;
        using OperationExecution? execution = Enter(options);
        try
        {
            JpegDecoderCore core = JpegDecoderCore.ForStream(stream, options, scaleDenominator);
            if (!core.TryBeginStreaming())
            {
                if (start >= 0)
                {
                    stream.Position = start;
                }

                return false;
            }

            ImageMetadata metadata = core.BuildMetadata();
            source = new Source<TPixel>(core, options, metadata);
            return true;
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            throw DecoderGuard.Wrap("JPEG", ex);
        }
    }

    /// <summary>Enters the options' execution policy, or keeps the caller's ambient one when the options set none.</summary>
    private static OperationExecution? Enter(DecoderOptions options)
        => options.ExecutionOptions is null ? null : OperationExecution.Enter(options.ExecutionOptions);

    /// <summary>
    /// Malformed-input symptoms raised while parsing. Unlike the in-memory decoders this one reads a caller's
    /// stream, whose own I/O errors are passed through rather than reported as bad image data.
    /// </summary>
    private static bool IsMalformed(Exception ex) => DecoderGuard.IsMalformedInputSymptom(ex) && ex is not IOException;

    /// <summary>The row source <see cref="TryCreate"/> returns.</summary>
    internal sealed class Source<TPixel> : IScanlineSource<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly JpegDecoderCore core;
        private readonly DecoderOptions options;
        private JpegDecoderCore.RowScratch? scratch;
        private int currentRow;

        public Source(JpegDecoderCore core, DecoderOptions options, ImageMetadata metadata)
        {
            this.core = core;
            this.options = options;
            this.Metadata = metadata;
            this.scratch = core.CreateRowScratch();
        }

        public int Width => this.core.OutputWidth;

        public int Height => this.core.OutputHeight;

        public ImageMetadata Metadata { get; }

        public int CurrentRow => this.currentRow;

        /// <summary>Bytes of decoded samples held at once: three MCU rows of every component.</summary>
        internal long ResidentSampleBytes => this.core.DecodedPlaneBytes;

        public bool ReadRow(Span<TPixel> destination)
        {
            JpegDecoderCore.RowScratch scratch = this.scratch ?? throw new ObjectDisposedException(nameof(JpegScanlineSource));
            if (this.currentRow >= this.Height)
            {
                return false;
            }

            if (destination.Length < this.Width)
            {
                throw new ArgumentException($"The destination holds {destination.Length} pixels; a row has {this.Width}.", nameof(destination));
            }

            using OperationExecution? execution = Enter(this.options);
            try
            {
                this.core.ReadStreamingRow(this.currentRow, destination, scratch);
            }
            catch (Exception ex) when (IsMalformed(ex))
            {
                throw DecoderGuard.Wrap("JPEG", ex);
            }

            this.currentRow++;
            return true;
        }

        public void Dispose()
        {
            this.scratch?.Dispose();
            this.scratch = null;
        }
    }
}
