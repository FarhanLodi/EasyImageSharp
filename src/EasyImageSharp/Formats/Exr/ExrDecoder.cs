using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Exr;

/// <summary>
/// Decodes single-part OpenEXR files: scanline and tiled images (the full-resolution level of mipmapped and
/// ripmapped files), HALF, FLOAT and UINT channels, and the NONE, RLE, ZIPS, ZIP, PIZ and PXR24 compression
/// methods.
/// </summary>
/// <remarks>
/// <para>
/// The <c>R</c>, <c>G</c>, <c>B</c> and <c>A</c> channels of the default layer become the pixel's components; a
/// missing colour channel reads as 0 and a missing alpha as 1. A file with <c>Y</c> but no colour channels is
/// luminance and fills all three colour components. Other channels (depth, layered names such as
/// <c>diffuse.R</c>, subsampled channels) are skipped. UINT samples become their numeric value.
/// </para>
/// <para>
/// The image covers the data window by default — the pixels the file stores — with
/// <see cref="ExrMetadata.DataWindow"/> recording where that lies; set <see cref="UseDisplayWindow"/> to frame it
/// in the display window instead. Chromaticities are reported in <see cref="ExrMetadata"/> but not applied.
/// </para>
/// <para>
/// <see cref="RgbaVector"/> receives the samples verbatim (out-of-range values, infinities and NaNs included).
/// Every other pixel format is integer-backed and sRGB-encoded by the library's convention: its colour
/// components receive the samples clamped to 0..1 (NaN as 0) and passed through the sRGB transfer function, and
/// its alpha the clamped alpha sample, each rounded to the format's precision.
/// </para>
/// <para>
/// B44, B44A, DWAA, DWAB, HTJ2K and the newer methods, multi-part files, deep data and luminance/chroma
/// (<c>RY</c>/<c>BY</c>) images throw <see cref="NotSupportedException"/> naming the feature; <c>Identify</c>
/// still reports their headers where the file is single-part.
/// </para>
/// </remarks>
public sealed class ExrDecoder : IImageDecoder
{
    internal const string FormatName = "OpenEXR";

    private const int RoleR = 0;
    private const int RoleG = 1;
    private const int RoleB = 2;
    private const int RoleA = 3;
    private const int RoleY = 4;

    /// <summary>
    /// When true the decoded image covers the display window, with the data window placed inside it and any
    /// uncovered pixels transparent black. Defaults to false: the image is exactly the data window.
    /// </summary>
    public bool UseDisplayWindow { get; init; }

    /// <inheritdoc/>
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            return this.DecodeCore<TPixel>(data, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap(FormatName, ex);
        }
    }

    /// <inheritdoc/>
    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            ExrHeader header = ExrHeader.Parse(data);
            int bits = 0;
            foreach (ExrChannel channel in header.Channels)
            {
                bits += channel.BytesPerSample * 8;
            }

            (int width, int height) = this.UseDisplayWindow ? (header.DisplayWidth, header.DisplayHeight) : (header.DataWidth, header.DataHeight);
            return new ImageInfo(width, height, bits, 1, ImageFormat.Exr, CreateMetadata(header));
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap(FormatName, ex);
        }
    }

    private static ImageMetadata CreateMetadata(ExrHeader header)
    {
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Exr };
        metadata.SetFormatMetadata(header.CreateMetadata());
        return metadata;
    }

    private Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ExrHeader header = ExrHeader.Parse(data);
        if (header.Compression > 5)
        {
            throw new NotSupportedException($"OpenEXR {ExrHeader.CompressionName(header.Compression)} compression is not supported.");
        }

        int[] roles = ResolveRoles(header.Channels);
        options.EnsureFrameWithinLimits(header.DataWidth, header.DataHeight, FormatName);

        int originX, originY, width, height;
        if (this.UseDisplayWindow)
        {
            (originX, originY, width, height) = (header.DisplayMinX, header.DisplayMinY, header.DisplayWidth, header.DisplayHeight);
            options.EnsureFrameWithinLimits(width, height, FormatName);
        }
        else
        {
            (originX, originY, width, height) = (header.DataMinX, header.DataMinY, header.DataWidth, header.DataHeight);
        }

        ChunkPlan plan = PlanChunks(data, header);
        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(width, height);
        bool coversOutput = header.DataMinX <= originX && header.DataMinY <= originY
            && header.DataMaxX >= originX + width - 1 && header.DataMaxY >= originY + height - 1;
        if (!coversOutput)
        {
            frame.PixelSpan.Clear();
        }

        // Chunks are independent and cover disjoint pixels, so they decode in parallel when the configuration
        // allows it; every pixel is written exactly once either way, so the result does not depend on the degree
        // of parallelism. The span cannot cross into the worker lambdas, which therefore read a copy of the file.
        long chunkPixels = header.Tiled
            ? (long)Math.Min(header.TileWidth, header.DataWidth) * Math.Min(header.TileHeight, header.DataHeight)
            : (long)header.DataWidth * Math.Min(header.LinesPerChunk, header.DataHeight);
        int batchWidth = (int)Math.Min(chunkPixels, int.MaxValue);
        Configuration configuration = OperationExecution.Current?.Configuration ?? Configuration.Default;
        bool parallel = configuration.MaxDegreeOfParallelism > 1 && plan.Count >= 2
            && (long)batchWidth * plan.Count >= configuration.MinimumPixelsPerTask * 2L;
        if (!parallel)
        {
            DecodeChunks(data, header, roles, plan, 0, plan.Count, frame, originX, originY, width, height);
        }
        else
        {
            byte[] copy = data.ToArray();
            try
            {
                ParallelRowIterator.IterateRows(batchWidth, plan.Count, (start, end)
                    => DecodeChunks(copy, header, roles, plan, start, end, frame, originX, originY, width, height));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                throw;
            }
        }

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, CreateMetadata(header));
    }

    /// <summary>Decodes chunks <paramref name="start"/> to <paramref name="end"/> (exclusive) into the frame.</summary>
    private static void DecodeChunks<TPixel>(
        ReadOnlySpan<byte> data, ExrHeader header, int[] roles, ChunkPlan plan, int start, int end,
        ImageFrame<TPixel> frame, int originX, int originY, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var target = new Target<TPixel>(frame.PixelSpan, originX, originY, width, height);
        ExrPiz? piz = header.Compression == 4 ? new ExrPiz() : null;
        for (int i = start; i < end; i++)
        {
            OperationExecution.Checkpoint();
            ReadOnlySpan<byte> packed = plan.Read(data, header, i, out int x0, out int x1, out int y0, out int y1);
            DecodeChunk(header, roles, packed, x0, x1, y0, y1, piz, ref target);
        }
    }

    /// <summary>Maps R, G, B, A and Y to channel indices (or -1), rejecting the layouts this decoder does not render.</summary>
    private static int[] ResolveRoles(ExrChannel[] channels)
    {
        int[] roles = { -1, -1, -1, -1, -1 };
        bool chroma = false;
        for (int i = 0; i < channels.Length; i++)
        {
            int role = channels[i].Name switch
            {
                "R" => RoleR,
                "G" => RoleG,
                "B" => RoleB,
                "A" => RoleA,
                "Y" => RoleY,
                _ => -1,
            };

            chroma |= channels[i].Name is "RY" or "BY";
            if (role < 0)
            {
                continue;
            }

            if (channels[i].XSampling != 1 || channels[i].YSampling != 1)
            {
                throw new NotSupportedException($"OpenEXR channel '{channels[i].Name}' is subsampled; only full-resolution R, G, B, A and Y channels are supported.");
            }

            roles[role] = i;
        }

        bool rgb = roles[RoleR] >= 0 || roles[RoleG] >= 0 || roles[RoleB] >= 0;
        if (!rgb && chroma)
        {
            throw new NotSupportedException("OpenEXR luminance/chroma (Y, RY, BY) images are not supported.");
        }

        if (!rgb && roles[RoleY] < 0 && roles[RoleA] < 0)
        {
            string names = string.Join(", ", channels.Select(c => c.Name));
            throw new NotSupportedException(
                $"OpenEXR file has no R, G, B, A or Y channel in its default layer (channels: {names}); channel layers are not supported.");
        }

        if (rgb)
        {
            roles[RoleY] = -1;
        }

        return roles;
    }

    /// <summary>The full-resolution chunks of a scanline or tiled file, located through the offset table.</summary>
    private readonly struct ChunkPlan
    {
        private readonly long[] offsets;
        private readonly int tilesX;

        public ChunkPlan(long[] offsets, int tilesX)
        {
            this.offsets = offsets;
            this.tilesX = tilesX;
        }

        public int Count => this.offsets.Length;

        /// <summary>Validates chunk <paramref name="index"/>'s header and returns its packed bytes and pixel range.</summary>
        public ReadOnlySpan<byte> Read(ReadOnlySpan<byte> data, ExrHeader header, int index, out int x0, out int x1, out int y0, out int y1)
        {
            int offset = (int)this.offsets[index];
            if (header.Tiled)
            {
                int tx = index % this.tilesX;
                int ty = index / this.tilesX;
                int fileTx = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                int fileTy = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
                int levelX = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 8)..]);
                int levelY = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12)..]);
                int size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 16)..]);
                if (fileTx != tx || fileTy != ty || levelX != 0 || levelY != 0)
                {
                    throw new InvalidImageContentException($"OpenEXR tile {index} is labelled ({fileTx}, {fileTy}, level {levelX}, {levelY}), expected ({tx}, {ty}, level 0, 0).");
                }

                if (size < 0 || size > data.Length - offset - 20)
                {
                    throw new InvalidImageContentException($"OpenEXR tile {index} is truncated.");
                }

                x0 = header.DataMinX + (tx * header.TileWidth);
                y0 = header.DataMinY + (ty * header.TileHeight);
                x1 = (int)Math.Min((long)x0 + header.TileWidth - 1, header.DataMaxX);
                y1 = (int)Math.Min((long)y0 + header.TileHeight - 1, header.DataMaxY);
                return data.Slice(offset + 20, size);
            }
            else
            {
                int linesPerChunk = header.LinesPerChunk;
                int expectedY = header.DataMinY + (index * linesPerChunk);
                int fileY = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                int size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
                if (fileY != expectedY)
                {
                    throw new InvalidImageContentException($"OpenEXR chunk {index} starts at line {fileY}, expected {expectedY}.");
                }

                if (size < 0 || size > data.Length - offset - 8)
                {
                    throw new InvalidImageContentException($"OpenEXR chunk {index} is truncated.");
                }

                x0 = header.DataMinX;
                x1 = header.DataMaxX;
                y0 = expectedY;
                y1 = (int)Math.Min((long)expectedY + linesPerChunk - 1, header.DataMaxY);
                return data.Slice(offset + 8, size);
            }
        }
    }

    private static ChunkPlan PlanChunks(ReadOnlySpan<byte> data, ExrHeader header)
    {
        long count;
        int tilesX = 0;
        int chunkHeaderSize;
        if (header.Tiled)
        {
            tilesX = (int)(((long)header.DataWidth + header.TileWidth - 1) / header.TileWidth);
            long tilesY = ((long)header.DataHeight + header.TileHeight - 1) / header.TileHeight;
            count = tilesX * tilesY;
            chunkHeaderSize = 20;
        }
        else
        {
            count = ((long)header.DataHeight + header.LinesPerChunk - 1) / header.LinesPerChunk;
            chunkHeaderSize = 8;
        }

        // The table has to be present in the file before anything is sized from it.
        long tableEnd = header.HeaderEnd + (count * 8);
        if (tableEnd > data.Length)
        {
            throw new InvalidImageContentException($"OpenEXR offset table of {count:N0} entries does not fit in the file.");
        }

        var offsets = new long[count];
        for (int i = 0; i < offsets.Length; i++)
        {
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(data[(header.HeaderEnd + (i * 8))..]);
            if (offset < (ulong)tableEnd || offset > (ulong)(data.Length - chunkHeaderSize))
            {
                throw new InvalidImageContentException($"OpenEXR chunk {i} has the out-of-range offset {offset}.");
            }

            offsets[i] = (long)offset;
        }

        return new ChunkPlan(offsets, tilesX);
    }

    /// <summary>Bytes of one chunk's uncompressed layout: per line, per channel, that channel's samples on the line.</summary>
    private static long LayoutSize(ExrChannel[] channels, int x0, int x1, int y0, int y1)
    {
        long size = 0;
        foreach (ExrChannel channel in channels)
        {
            long lines = ExrHeader.SampleCount(channel.YSampling, y0, y1);
            size += lines * ExrHeader.SampleCount(channel.XSampling, x0, x1) * channel.BytesPerSample;
        }

        return size;
    }

    /// <summary>
    /// The largest uncompressed-to-compressed ratio each method can reach. A chunk that claims to expand further
    /// is corrupt, and rejecting it before its buffer is rented keeps a small file from forcing a large allocation.
    /// </summary>
    private static long MaxExpansion(int compression) => compression switch
    {
        1 => 64,    // RLE: a two-byte record yields at most 128 bytes.
        4 => 512,   // PIZ: about ten bits can encode a 256-word run.
        _ => 1032,  // zlib's ceiling for ZIPS, ZIP and PXR24.
    };

    private static void DecodeChunk<TPixel>(
        ExrHeader header, int[] roles, ReadOnlySpan<byte> packed, int x0, int x1, int y0, int y1, ExrPiz? piz, ref Target<TPixel> target)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        long expectedLong = LayoutSize(header.Channels, x0, x1, y0, y1);
        if (packed.Length >= expectedLong)
        {
            // Uncompressed data, or a chunk the writer stored raw because compression would not have helped.
            Extract(header, roles, packed[..(int)expectedLong], x0, x1, y0, y1, ref target);
            return;
        }

        if (header.Compression == 0)
        {
            throw new InvalidImageContentException("OpenEXR uncompressed chunk is truncated.");
        }

        if (expectedLong > (packed.Length * MaxExpansion(header.Compression)) + 4096 || expectedLong > Array.MaxLength)
        {
            throw new InvalidImageContentException("OpenEXR chunk claims an impossible compression ratio.");
        }

        int expected = (int)expectedLong;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(expected);
        byte[] output = ArrayPool<byte>.Shared.Rent(expected);
        try
        {
            Span<byte> raw = output.AsSpan(0, expected);
            switch (header.Compression)
            {
                case 1:
                    ExrCodecs.RleDecode(packed, scratch.AsSpan(0, expected));
                    ExrCodecs.UndoPredictorAndInterleave(scratch.AsSpan(0, expected), raw);
                    break;
                case 2:
                case 3:
                    ExrCodecs.InflateExact(packed, scratch.AsSpan(0, expected), "OpenEXR ZIP chunk");
                    ExrCodecs.UndoPredictorAndInterleave(scratch.AsSpan(0, expected), raw);
                    break;
                case 4:
                    piz!.Decompress(packed, raw, header.Channels, x0, x1, y0, y1);
                    break;
                default:
                    ExrCodecs.Pxr24Decode(packed, raw, header.Channels, x0, x1, y0, y1, scratch);
                    break;
            }

            Extract(header, roles, raw, x0, x1, y0, y1, ref target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>Converts the R, G, B, A and Y samples of one uncompressed chunk into output pixels.</summary>
    private static void Extract<TPixel>(ExrHeader header, int[] roles, ReadOnlySpan<byte> raw, int x0, int x1, int y0, int y1, ref Target<TPixel> target)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ExrChannel[] channels = header.Channels;
        Span<int> offsets = stackalloc int[5];
        Span<int> types = stackalloc int[5];
        for (int r = 0; r < 5; r++)
        {
            types[r] = roles[r] >= 0 ? channels[roles[r]].PixelType : -1;
        }

        int width = x1 - x0 + 1;
        int lineStart = 0;
        for (int y = y0; y <= y1; y++)
        {
            offsets.Fill(-1);
            int offset = lineStart;
            for (int c = 0; c < channels.Length; c++)
            {
                ExrChannel channel = channels[c];
                if (ExrHeader.FloorMod(y, channel.YSampling) != 0)
                {
                    continue;
                }

                for (int r = 0; r < 5; r++)
                {
                    if (roles[r] == c)
                    {
                        offsets[r] = offset;
                    }
                }

                offset += ExrHeader.SampleCount(channel.XSampling, x0, x1) * channel.BytesPerSample;
            }

            int row = y - target.OriginY;
            if (row >= 0 && row < target.Height)
            {
                bool luminance = types[RoleY] >= 0;
                for (int i = 0; i < width; i++)
                {
                    int column = x0 + i - target.OriginX;
                    if (column < 0 || column >= target.Width)
                    {
                        continue;
                    }

                    Vector4 value;
                    float alpha = types[RoleA] >= 0 ? ReadSample(raw, offsets[RoleA], i, types[RoleA]) : 1f;
                    if (luminance)
                    {
                        float l = ReadSample(raw, offsets[RoleY], i, types[RoleY]);
                        value = new Vector4(l, l, l, alpha);
                    }
                    else
                    {
                        value = new Vector4(
                            types[RoleR] >= 0 ? ReadSample(raw, offsets[RoleR], i, types[RoleR]) : 0f,
                            types[RoleG] >= 0 ? ReadSample(raw, offsets[RoleG], i, types[RoleG]) : 0f,
                            types[RoleB] >= 0 ? ReadSample(raw, offsets[RoleB], i, types[RoleB]) : 0f,
                            alpha);
                    }

                    target.Pixels[(row * target.Width) + column] = SceneLinearPixels.FromLinear<TPixel>(value);
                }
            }

            lineStart = offset;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ReadSample(ReadOnlySpan<byte> raw, int lineOffset, int index, int type) => type switch
    {
        ExrChannel.Half => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(raw[(lineOffset + (index * 2))..])),
        ExrChannel.Float => BinaryPrimitives.ReadSingleLittleEndian(raw[(lineOffset + (index * 4))..]),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(raw[(lineOffset + (index * 4))..]),
    };

    /// <summary>The output frame and where it sits in the file's pixel coordinates.</summary>
    private ref struct Target<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        public Target(Span<TPixel> pixels, int originX, int originY, int width, int height)
        {
            this.Pixels = pixels;
            this.OriginX = originX;
            this.OriginY = originY;
            this.Width = width;
            this.Height = height;
        }

        public Span<TPixel> Pixels { get; }

        public int OriginX { get; }

        public int OriginY { get; }

        public int Width { get; }

        public int Height { get; }
    }
}
