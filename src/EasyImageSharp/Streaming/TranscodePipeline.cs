using System.Buffers;
using System.Runtime.CompilerServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Streaming;

/// <summary>
/// Runs one transcode: opens the input with a row-streaming decoder when one handles it (else decodes it in
/// full), builds the chain of stages the options ask for - resize, orientation, canvas placement - in front of a
/// row-streaming encoder (else one that collects the image for an ordinary encoder), and pumps the rows through.
/// </summary>
/// <remarks>
/// The chain is built from the end: <c>source -&gt; resize -&gt; orientation -&gt; canvas -&gt; sink</c>. The resize
/// runs in the stored orientation, before the image is turned upright, so that an orientation which needs the
/// whole frame only ever holds the small resized one; its target is the stored-orientation size of what the
/// in-memory path computes for the upright image, and the canvas placement happens upright, so the geometry is
/// exactly that of <c>AutoOrient().Resize(options)</c>. A source that had to be decoded in full is resized in
/// memory instead of row by row, which gives the same pixels in parallel.
/// </remarks>
/// <typeparam name="TPixel">The pixel format rows travel in.</typeparam>
internal sealed class TranscodePipeline<TPixel> : IDisposable
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>The input prefix the format detection and custom format matchers are given.</summary>
    internal const int DetectionPrefixBytes = 4096;

    /// <summary>The smallest working-set budget, whatever <see cref="DecoderOptions.MaxPixels"/> says.</summary>
    internal const long MinimumWorkingSetBudget = 16L << 20;

    /// <summary>Estimated decoder scratch per source pixel: two scanlines of up to 8 bytes and two converted rows.</summary>
    private const int DecoderBytesPerPixel = 48;

    /// <summary>Estimated encoder scratch per output pixel: packed, previous, candidate and filtered scanlines plus RGBA.</summary>
    private const int EncoderBytesPerPixel = 40;

    /// <summary>The write-behind drain threshold of the asynchronous pipeline.</summary>
    private const int DrainThreshold = 1 << 16;

    private readonly TranscodeOptions options;
    private readonly StreamingInput input;
    private readonly Stream output;
    private readonly long maxSourcePixels;

    private IScanlineSource<TPixel>? source;
    private ImageFormat? sourceFormat;
    private bool streamedDecode;
    private bool streamedEncode;
    private IScanlineSink<TPixel>? chain;
    private TPixel[]? row;
    private Size sourceSize;
    private Size outputSize;

    /// <summary>Creates a pipeline over an input and an output; <see cref="Run"/> and <see cref="RunAsync"/> drive it.</summary>
    internal TranscodePipeline(TranscodeOptions options, StreamingInput input, Stream output)
    {
        this.options = options;
        this.input = input;
        this.output = output;
        this.maxSourcePixels = options.MaxSourcePixels ?? options.DecoderOptions.MaxTotalPixels;
    }

    /// <summary>The decoder options a row-streaming decoder is given: <see cref="TranscodeOptions.MaxSourcePixels"/> replaces MaxPixels.</summary>
    internal DecoderOptions StreamingDecoderOptions => Derive(this.options.DecoderOptions, this.maxSourcePixels, maxFrames: 1);

    /// <summary>The decoder options a fallback decode is given: both pixel limits apply, and only the root frame is decoded.</summary>
    internal DecoderOptions FallbackDecoderOptions
        => Derive(this.options.DecoderOptions, Math.Min(this.options.DecoderOptions.MaxPixels, this.maxSourcePixels), maxFrames: 1);

    /// <summary>The bytes the asynchronous pipeline keeps read ahead of a row-streaming decoder.</summary>
    private int ReadAheadBytes
        => this.streamedDecode ? (int)Math.Clamp((long)this.source!.Width * 32, 128 * 1024, 8 * 1024 * 1024) : 0;

    /// <summary>Runs a transcode synchronously.</summary>
    public static TranscodeResult Run(Stream input, Stream output, TranscodeOptions? options, CancellationToken cancellationToken)
    {
        options = Validate(input, output, options);
        using var execution = OperationExecution.Enter(options.DecoderOptions.ExecutionOptions, cancellationToken);
        using var streamingInput = new StreamingInput(input);
        using var pipeline = new TranscodePipeline<TPixel>(options, streamingInput, output);
        if (!pipeline.TryOpenStreamingSource())
        {
            pipeline.OpenFallbackSource(BoundedImageInput.Read(streamingInput, pipeline.FallbackDecoderOptions));
        }

        pipeline.BuildChain();
        while (pipeline.PumpRow())
        {
        }

        return pipeline.Complete();
    }

    /// <summary>
    /// Runs a transcode with asynchronous input and output: the input is read ahead of the decoder and the encoded
    /// bytes are drained behind the encoder, both asynchronously, while decoding, resizing and encoding run
    /// synchronously in between. Bytes a decoder needs beyond the read-ahead window (a header walk on a seekable
    /// input, or one row's data larger than the window) are read synchronously.
    /// </summary>
    public static async Task<TranscodeResult> RunAsync(
        Stream input, Stream output, TranscodeOptions? options, CancellationToken cancellationToken)
    {
        options = Validate(input, output, options);
        using var execution = OperationExecution.Enter(options.DecoderOptions.ExecutionOptions, cancellationToken);
        CancellationToken token = execution.CancellationToken;
        using var streamingInput = new StreamingInput(input);
        using var writeBehind = new WriteBehindStream(output);
        using var pipeline = new TranscodePipeline<TPixel>(options, streamingInput, writeBehind);
        await streamingInput.FillAsync(128 * 1024, token).ConfigureAwait(false);
        if (!pipeline.TryOpenStreamingSource())
        {
            ReadOnlyMemory<byte> data = await BoundedImageInput.ReadAsync(streamingInput, pipeline.FallbackDecoderOptions, token)
                .ConfigureAwait(false);
            pipeline.OpenFallbackSource(data);
        }

        pipeline.BuildChain();
        int readAhead = pipeline.ReadAheadBytes;
        while (true)
        {
            if (readAhead > 0 && streamingInput.Buffered < readAhead / 2 && !streamingInput.Exhausted)
            {
                await streamingInput.FillAsync(readAhead, token).ConfigureAwait(false);
            }

            if (!pipeline.PumpRow())
            {
                break;
            }

            await writeBehind.DrainAsync(DrainThreshold, token).ConfigureAwait(false);
        }

        TranscodeResult result = pipeline.Complete();
        await writeBehind.DrainAsync(0, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Opens a row-streaming decoder when the input's format and variant have one; on <see langword="false"/> the
    /// input is back at its start for the fallback. Malformed headers and exceeded limits throw.
    /// </summary>
    internal bool TryOpenStreamingSource()
    {
        byte[] prefixBuffer = ArrayPool<byte>.Shared.Rent(DetectionPrefixBytes);
        ImageFormat? format;
        try
        {
            ReadOnlySpan<byte> prefix = prefixBuffer.AsSpan(0, this.input.Peek(prefixBuffer.AsSpan(0, DetectionPrefixBytes)));

            // Custom formats are considered first, exactly as Image.Load considers them: one that claims the data
            // decodes it, and only the built-in decoders have row-streaming forms.
            format = ImageFormatDetector.Detect(prefix);
            foreach (ImageFormat custom in this.options.DecoderOptions.AdditionalFormats)
            {
                if (custom.Matches(prefix))
                {
                    format = null;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefixBuffer);
        }

        IScanlineSource<TPixel>? opened = null;
        if (format == ImageFormat.Png)
        {
            PngScanlineSource.TryCreate(this.input, this.StreamingDecoderOptions, out opened);
        }
        else if (format == ImageFormat.Jpeg)
        {
            opened = this.OpenJpeg();
        }

        if (opened is null)
        {
            // The fallback reads everything from the start; what was recorded is replayed once and then released,
            // so the header bytes are not kept twice while the whole input is buffered.
            this.input.Rewind();
            this.input.StopRecording();
            return false;
        }

        this.input.StopRecording();
        this.source = opened;
        this.sourceFormat = format!;
        this.streamedDecode = true;
        return true;
    }

    /// <summary>
    /// Opens a JPEG row source. The header is read once at full size to learn the dimensions and EXIF orientation
    /// (which costs only a few MCU rows of scratch), then the input is rewound and reopened at the reduced-IDCT
    /// scale the resize allows. Null when the frame type cannot be streamed.
    /// </summary>
    private IScanlineSource<TPixel>? OpenJpeg()
    {
        DecoderOptions decoderOptions = this.StreamingDecoderOptions;
        if (!JpegScanlineSource.TryCreate(this.input, decoderOptions, 1, out IScanlineSource<TPixel>? probe))
        {
            return null;
        }

        int denominator;
        using (probe)
        {
            (int contentWidth, int contentHeight) = this.StoredContentSize(probe!.Width, probe.Height, probe.Metadata);
            denominator = ReducedDecode.ChooseDenominator(probe.Width, probe.Height, contentWidth, contentHeight);
        }

        this.input.Rewind();
        return JpegScanlineSource.TryCreate(this.input, decoderOptions, denominator, out IScanlineSource<TPixel>? source)
            ? source
            : null;
    }

    /// <summary>Decodes the whole input with the regular decoder, keeping its root frame as the source.</summary>
    internal void OpenFallbackSource(ReadOnlyMemory<byte> data)
    {
        DecoderOptions decoderOptions = this.FallbackDecoderOptions;
        ImageFormat format = ImageFormatDetector.DetectOrThrow(data.Span, decoderOptions);
        Image<TPixel> image = format.CreateDecoder().Decode<TPixel>(data.Span, decoderOptions);
        this.source = new ImageScanlineSource<TPixel>(image, ownsImage: true);
        this.sourceFormat = format;
        this.streamedDecode = false;
    }

    /// <summary>
    /// The resize target in the source's stored orientation for a source of the given stored size and metadata,
    /// or the stored size itself when no resize applies.
    /// </summary>
    internal (int Width, int Height) StoredContentSize(int width, int height, ImageMetadata metadata)
    {
        int orientation = this.options.AutoOrient ? OrientationStage<TPixel>.ReadOrientation(metadata) : 1;
        ImageProcessingContext<TPixel>.ResizePlan? plan = this.PlanResize(width, height, orientation);
        if (plan is not { } p)
        {
            return (width, height);
        }

        return OrientationStage<TPixel>.SwapsAxes(orientation) ? (p.ContentHeight, p.ContentWidth) : (p.ContentWidth, p.ContentHeight);
    }

    /// <summary>Builds the stage chain for the open source and starts it; nothing has been decoded yet.</summary>
    internal void BuildChain()
    {
        IScanlineSource<TPixel> source = this.source!;
        DecoderOptions decoderOptions = this.options.DecoderOptions;
        int orientation = this.options.AutoOrient ? OrientationStage<TPixel>.ReadOrientation(source.Metadata) : 1;
        bool swap = OrientationStage<TPixel>.SwapsAxes(orientation);
        ImageProcessingContext<TPixel>.ResizePlan? plan = this.PlanResize(source.Width, source.Height, orientation);

        int contentWidth = swap ? source.Height : source.Width;
        int contentHeight = swap ? source.Width : source.Height;
        int canvasWidth = contentWidth;
        int canvasHeight = contentHeight;
        if (plan is { } p)
        {
            (contentWidth, contentHeight, canvasWidth, canvasHeight) = (p.ContentWidth, p.ContentHeight, p.CanvasWidth, p.CanvasHeight);
        }

        (int storedWidth, int storedHeight) = swap ? (contentHeight, contentWidth) : (contentWidth, contentHeight);
        bool resize = storedWidth != source.Width || storedHeight != source.Height;
        bool place = plan is { } q && (q.CanvasWidth != q.ContentWidth || q.CanvasHeight != q.ContentHeight || q.OffsetX != 0 || q.OffsetY != 0);

        IImageEncoder encoder = this.ResolveEncoder();
        if (encoder is PngEncoder png && PngScanlineSink.TryCreate(this.output, png, out IScanlineSink<TPixel>? streamingSink))
        {
            this.chain = streamingSink;
            this.streamedEncode = true;
        }
        else if (encoder is JpegEncoder jpeg && JpegScanlineSink.TryCreate(this.output, jpeg, out streamingSink))
        {
            // Progressive output and trellis with optimised tables need every block first; TryCreate refuses them
            // and the output is collected for the regular encoder instead.
            this.chain = streamingSink;
            this.streamedEncode = true;
        }

        // Everything the pipeline will materialise or hold is checked now, before a single pixel is decoded.
        long canvasPixels = (long)canvasWidth * canvasHeight;
        bool holdsOutput = !this.streamedEncode || orientation is not (1 or 2);
        if (holdsOutput && canvasPixels > decoderOptions.MaxPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"The {canvasWidth}x{canvasHeight} output is held in memory before it is encoded, and {canvasPixels:N0} pixels "
                + $"exceeds the configured limit of {decoderOptions.MaxPixels:N0}. Raise DecoderOptions.MaxPixels, or use an "
                + "encoder that streams and leave the orientation alone.");
        }

        long budget = WorkingSetBudget(decoderOptions);
        long sourceBytes = (long)source.Width * (Unsafe.SizeOf<TPixel>() + (this.streamedDecode ? DecoderBytesPerPixel : 0));
        long outputBytes = (long)Math.Max(canvasWidth, contentWidth) * (Unsafe.SizeOf<TPixel>() + EncoderBytesPerPixel);
        long remaining = budget - sourceBytes - outputBytes;
        if (remaining < 0)
        {
            throw new ImageSizeLimitExceededException(
                $"Streaming a {source.Width}x{source.Height} source to a {canvasWidth}x{canvasHeight} output holds about "
                + $"{sourceBytes + outputBytes:N0} bytes of rows, which exceeds the working-set budget of {budget:N0} bytes "
                + "(four bytes per DecoderOptions.MaxPixels, at least 16 MiB).");
        }

        this.sourceSize = new Size(source.Width, source.Height);
        if (resize && source is ImageScanlineSource<TPixel> decoded)
        {
            // A source that is in memory anyway is resized there: the same FrameOps arithmetic the row-streaming
            // resize reproduces bit for bit, but spread over the configured degree of parallelism.
            ResizeOptions resizeOptions = this.options.Resize!;
            decoded.Resize(storedWidth, storedHeight, resizeOptions.Sampler, resizeOptions.PremultiplyAlpha, resizeOptions.Compand);
            resize = false;
        }

        // Each stage owns the one after it, so this.chain always disposes everything built so far.
        this.chain ??= new EncoderScanlineSink<TPixel>(this.output, encoder, decoderOptions.MaxPixels);
        if (place)
        {
            this.chain = new CanvasPlacementStage<TPixel>(
                this.chain, canvasWidth, canvasHeight, plan!.Value.OffsetX, plan.Value.OffsetY, this.options.Resize!.PadColor);
        }

        if (orientation != 1)
        {
            this.chain = new OrientationStage<TPixel>(this.chain, orientation, decoderOptions.MaxPixels);
        }

        if (resize)
        {
            ResizeOptions resizeOptions = this.options.Resize!;
            this.chain = new ScanlineResizer<TPixel>(
                this.chain, storedWidth, storedHeight, resizeOptions.Sampler, resizeOptions.PremultiplyAlpha, resizeOptions.Compand, remaining);
        }

        this.outputSize = new Size(canvasWidth, canvasHeight);
        this.chain.Begin(source.Width, source.Height, this.BuildOutputMetadata(source.Metadata, orientation));
        this.row = ArrayPool<TPixel>.Shared.Rent(source.Width);
    }

    /// <summary>Moves one source row through the chain; false once the source is exhausted.</summary>
    internal bool PumpRow()
    {
        Span<TPixel> buffer = this.row.AsSpan(0, this.source!.Width);
        if (!this.source.ReadRow(buffer))
        {
            return false;
        }

        this.chain!.WriteRow(buffer);
        return true;
    }

    /// <summary>Completes the chain, which writes the encoder's trailer, and reports what was done.</summary>
    internal TranscodeResult Complete()
    {
        IScanlineSource<TPixel> source = this.source!;
        if (source.CurrentRow != source.Height)
        {
            throw new InvalidOperationException($"The source produced {source.CurrentRow} of its {source.Height} rows.");
        }

        this.chain!.Complete();
        return new TranscodeResult(
            this.sourceFormat!, this.sourceSize, this.outputSize, this.streamedDecode, this.streamedEncode);
    }

    public void Dispose()
    {
        this.chain?.Dispose();
        this.chain = null;
        this.source?.Dispose();
        this.source = null;
        if (this.row is not null)
        {
            ArrayPool<TPixel>.Shared.Return(this.row);
            this.row = null;
        }
    }

    private static TranscodeOptions Validate(Stream input, Stream output, TranscodeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(output));
        }

        options ??= new TranscodeOptions();
        if (options.Resize is { } resize)
        {
            ArgumentNullException.ThrowIfNull(resize.Sampler, "options.Resize.Sampler");
        }

        return options;
    }

    /// <summary>Four bytes per <see cref="DecoderOptions.MaxPixels"/>, the size of a full RGBA frame at that limit, but at least 16 MiB.</summary>
    internal static long WorkingSetBudget(DecoderOptions options)
        => Math.Max(MinimumWorkingSetBudget, options.MaxPixels > long.MaxValue / 4 ? long.MaxValue : options.MaxPixels * 4);

    private static DecoderOptions Derive(DecoderOptions options, long maxPixels, int maxFrames) => new()
    {
        MaxPixels = maxPixels,
        MaxTotalPixels = options.MaxTotalPixels,
        MaxFrames = maxFrames,
        MaxEncodedBytes = options.MaxEncodedBytes,
        ExecutionOptions = options.ExecutionOptions,
        AdditionalFormats = options.AdditionalFormats,
    };

    /// <summary>
    /// The resize plan for the upright image, as <see cref="IImageProcessingContext.Resize(ResizeOptions)"/>
    /// computes it, or <see langword="null"/> when no resize applies.
    /// </summary>
    private ImageProcessingContext<TPixel>.ResizePlan? PlanResize(int storedWidth, int storedHeight, int orientation)
    {
        if (this.options.Resize is not { } resize)
        {
            return null;
        }

        bool swap = OrientationStage<TPixel>.SwapsAxes(orientation);
        int width = swap ? storedHeight : storedWidth;
        int height = swap ? storedWidth : storedHeight;
        ImageProcessingContext<TPixel>.ResizePlan plan = ImageProcessingContext<TPixel>.ComputeResizeTargets(resize, width, height);
        if (this.options.ShrinkOnly
            && (resize.Size.Width <= 0 || width <= resize.Size.Width)
            && (resize.Size.Height <= 0 || height <= resize.Size.Height))
        {
            return null;
        }

        return plan;
    }

    private IImageEncoder ResolveEncoder()
    {
        if (this.options.Encoder is { } encoder)
        {
            return encoder;
        }

        ImageFormat format = this.options.Format
            ?? (this.sourceFormat is { CanEncode: true } own ? own : ImageFormat.Png);
        return format.CreateEncoder();
    }

    /// <summary>The output metadata the <see cref="TranscodeOptions.Metadata"/> policy asks for.</summary>
    private ImageMetadata BuildOutputMetadata(ImageMetadata source, int orientation)
    {
        switch (this.options.Metadata)
        {
            case TranscodeMetadata.Strip:
                return new ImageMetadata();

            case TranscodeMetadata.KeepColorProfile:
                return new ImageMetadata { IccProfile = source.IccProfile?.DeepClone() };

            default:
                ImageMetadata metadata = source.DeepClone();

                // The output is always one still frame, so a source animation's flag must not turn it into an APNG.
                if (metadata.TryGetFormatMetadata(out PngMetadata? png))
                {
                    png.IsAnimated = false;
                }

                if (orientation != 1)
                {
                    OrientationStage<TPixel>.MarkUpright(metadata, orientation);
                }

                return metadata;
        }
    }
}
