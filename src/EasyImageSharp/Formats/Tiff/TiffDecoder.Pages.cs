using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

public sealed partial class TiffDecoder
{
    /// <summary>Decodes one TIFF page at a time from buffered encoded bytes. Caller owns every yielded image.</summary>
    /// <remarks>
    /// Pixel buffers for later pages are not allocated early. Encoded bytes must remain valid and unchanged
    /// until enumeration ends. Dispose each page before requesting the next to bound retained pixel memory.
    /// This API does not make files above the span decoder's 2 GiB limit addressable.
    /// </remarks>
    public IEnumerable<Image<TPixel>> DecodePages<TPixel>(ReadOnlyMemory<byte> data, DecoderOptions? options = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options ??= DecoderOptions.Default;
        TiffHeader header;
        using (OperationExecution.Enter(options.ExecutionOptions))
        {
            options.EnsureEncodedWithinLimits(data.Length);
            header = ValidateHeader(data.Span);
        }

        IfdShape shape = IfdShape.For(header.BigTiff);
        long offset = header.FirstIfd;
        var visited = new HashSet<long>();
        long totalPixels = 0;
        long totalPixelBytes = 0;
        int count = 0;
        while (offset != 0 && count < options.MaxFrames)
        {
            Image<TPixel> page;
            long limit = options.ExecutionOptions?.MaxPixelBufferBytes ?? long.MaxValue;
            if (totalPixelBytes >= limit)
                throw new ImageSizeLimitExceededException("TIFF pages exceed the cumulative pixel-buffer budget.");
            using (var execution = OperationExecution.Enter(new ImageExecutionOptions
            {
                Configuration = options.ExecutionOptions?.Configuration,
                CancellationToken = options.ExecutionOptions?.CancellationToken ?? default,
                MaxPixelBufferBytes = limit - totalPixelBytes,
            }))
            {
                if (!visited.Add(offset)) throw new InvalidImageContentException("TIFF page directory cycle detected.");
                try
                {
                    int ifd = CheckedIfdOffset(offset, data.Length, shape.CountBytes);
                    // Read declared dimensions before decoding so the aggregate budget is enforced early.
                    Dictionary<int, long[]> tags = ReadTags(data.Span, ifd, header.BigEndian, shape);
                    long width = GetSingle(tags, TagImageWidth, 0);
                    long height = GetSingle(tags, TagImageLength, 0);
                    if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
                        throw new InvalidImageContentException("Invalid TIFF page dimensions.");
                    long pixels = checked(width * height);
                    if (pixels > options.MaxTotalPixels - totalPixels)
                        throw new ImageSizeLimitExceededException("TIFF pages exceed MaxTotalPixels.");
                    long bytes = checked(pixels * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
                    if (bytes > limit - totalPixelBytes)
                        throw new ImageSizeLimitExceededException("TIFF pages exceed the cumulative pixel-buffer budget.");
                    totalPixels += pixels;
                    ImageFrame<TPixel> frame = DecodeFrame<TPixel>(data.Span, ifd, header.BigEndian, shape, options, tags);
                    offset = NextIfdOffset(data.Span, ifd, header.BigEndian, shape);
                    page = new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, CreateImageMetadata(frame.Metadata, header));
                    totalPixelBytes += execution.AllocatedPixelBytes;
                }
                catch (Exception error) when (DecoderGuard.IsMalformedInputSymptom(error))
                {
                    throw DecoderGuard.Wrap("TIFF", error);
                }
            }

            count++;
            yield return page;
        }

        if (count == 0) throw new InvalidImageContentException("TIFF image contains no pages.");
    }
}
