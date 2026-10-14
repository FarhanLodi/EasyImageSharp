using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>Random-access TIFF/BigTIFF reader that reads only selected strips/tiles, without buffering the complete file or page.</summary>
/// <remarks>
/// Supports unsigned 8/16-bit chunky gray/RGB, optional unassociated alpha, and None/LZW/Deflate/PackBits compression.
/// Other layouts are rejected. Regions use stored pixel coordinates; orientation and metadata profiles are not applied.
/// The seekable stream must remain unchanged and exclusively available until disposal. Operations are not thread-safe.
/// </remarks>
public sealed class TiffRegionReader : IDisposable
{
    private readonly Stream stream;
    private readonly long origin, length;
    private readonly bool leaveOpen, bigEndian;
    private readonly TiffRegionOptions options;
    private readonly Dictionary<int, Field> fields;
    private readonly int bits, samples, photometric, compression, predictor;
    private readonly long segmentWidth, segmentHeight, segmentsAcross;
    private readonly Field offsets, counts;
    private bool disposed;

    internal TiffRegionReader(Stream stream, int pageIndex, TiffRegionOptions options, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("TIFF region access requires a readable, seekable stream.", nameof(stream));
        }

        options.Validate();
        if (pageIndex < 0 || pageIndex >= options.MaxPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        this.stream = stream;
        this.options = options;
        this.leaveOpen = leaveOpen;
        this.origin = stream.Position;
        this.length = stream.Length - this.origin;
        this.PageIndex = pageIndex;
        using var execution = OperationExecution.Enter(options.ExecutionOptions, cancellationToken);
        var read = new ReadBudget(options.MaxReadBytes);
        Span<byte> header = stackalloc byte[16];
        this.ReadAt(0, header[..8], read);
        this.bigEndian = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (!this.bigEndian && !(header[0] == (byte)'I' && header[1] == (byte)'I'))
        {
            throw new InvalidImageContentException("Invalid TIFF byte order.");
        }

        int magic = this.U16(header[2..]);
        this.IsBigTiff = magic == 43;
        long ifd;
        if (this.IsBigTiff)
        {
            if (this.U16(header[4..]) != 8 || this.U16(header[6..]) != 0)
            {
                throw new InvalidImageContentException("Unsupported BigTIFF offset width or reserved field.");
            }

            this.ReadAt(8, header[8..], read);
            ifd = Signed(this.U64(header[8..]));
        }
        else if (magic == 42)
        {
            ifd = this.U32(header[4..]);
        }
        else
        {
            throw new InvalidImageContentException("Invalid TIFF magic.");
        }

        var visited = new HashSet<long>();
        Dictionary<int, Field>? selected = null;
        for (int index = 0; index <= pageIndex; index++)
        {
            if (ifd == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "The TIFF has fewer pages than requested.");
            }

            if (!visited.Add(ifd))
            {
                throw new InvalidImageContentException("TIFF directory cycle detected.");
            }

            (Dictionary<int, Field> tags, long next) = this.ReadDirectory(ifd, read);
            selected = tags;
            ifd = next;
        }

        this.fields = selected!;
        this.Width = this.Single(256, 0, read);
        this.Height = this.Single(257, 0, read);
        if (this.Width <= 0 || this.Height <= 0)
        {
            throw new InvalidImageContentException("Invalid TIFF page dimensions.");
        }

        long spp = this.Single(277, 1, read);
        long photo = this.Single(262, 1, read);
        long compression = this.Single(259, 1, read);
        long predictor = this.Single(317, 1, read);
        if (photo is not (0 or 1 or 2) || (photo == 2 ? spp is not (3 or 4) : spp is not (1 or 2))
            || this.Single(284, 1, read) != 1 || this.Single(266, 1, read) != 1
            || compression is not (1 or 5 or 8 or 32946 or 32773) || predictor is not (1 or 2))
        {
            throw new NotSupportedException("Region access supports chunky gray/RGB, FillOrder 1, predictor 1/2 and None/LZW/Deflate/PackBits only.");
        }

        this.samples = (int)spp;
        this.photometric = (int)photo;
        this.compression = (int)compression;
        this.predictor = (int)predictor;
        if (predictor == 2 && compression is not (5 or 8 or 32946))
        {
            throw new NotSupportedException("Region predictor 2 is supported with LZW/Deflate only.");
        }
        long[] depths = this.SmallValues(258, [1], this.samples, read);
        if (depths[0] is not (8 or 16) || depths.Any(value => value != depths[0]))
        {
            throw new NotSupportedException("Region access supports uniform 8-bit or 16-bit samples only.");
        }

        this.bits = (int)depths[0];
        if (this.SmallValues(339, [1], this.samples, read).Any(value => value != 1))
        {
            throw new NotSupportedException("Region access supports unsigned integer samples only.");
        }

        bool hasAlpha = this.samples == (this.photometric == 2 ? 4 : 2);
        if (hasAlpha && this.Single(338, -1, read) != 2)
        {
            throw new NotSupportedException("Region access requires ExtraSamples=2 for unassociated alpha; associated/unspecified extra samples are unsupported.");
        }

        this.IsTiled = this.fields.ContainsKey(324);
        if (this.IsTiled)
        {
            this.segmentWidth = this.Single(322, 0, read);
            this.segmentHeight = this.Single(323, 0, read);
        }
        else
        {
            this.segmentWidth = this.Width;
            this.segmentHeight = Math.Min(this.Height, this.Single(278, this.Height, read));
        }

        if (this.segmentWidth < 1 || this.segmentHeight < 1)
        {
            throw new InvalidImageContentException("Invalid TIFF segment dimensions.");
        }

        this.segmentsAcross = ((this.Width - 1) / this.segmentWidth) + 1;
        long segmentRows = ((this.Height - 1) / this.segmentHeight) + 1;
        if (this.segmentsAcross > long.MaxValue / segmentRows)
        {
            throw new NotSupportedException("The TIFF segment table exceeds signed 64-bit addressing.");
        }

        long segmentCount = this.segmentsAcross * segmentRows;
        if (!this.fields.TryGetValue(this.IsTiled ? 324 : 273, out this.offsets)
            || !this.fields.TryGetValue(this.IsTiled ? 325 : 279, out this.counts)
            || this.offsets.Count < segmentCount || this.counts.Count < segmentCount)
        {
            throw new InvalidImageContentException("TIFF segment offset/count tables are missing or truncated.");
        }
    }

    public long Width { get; }
    public long Height { get; }
    public int PageIndex { get; }
    public bool IsBigTiff { get; }
    public bool IsTiled { get; }

    /// <summary>Decodes an owned small region. Only intersecting compressed segments are decompressed; raw predictor-1 segments are read row slices directly.</summary>
    public Image<TPixel> ReadRegion<TPixel>(TiffRegion region, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.ValidateRegion(region);
        using var execution = OperationExecution.Enter(this.options.ExecutionOptions, cancellationToken);
        var budget = new ReadBudget(this.options.MaxReadBytes);
        var output = new Image<TPixel>(region.Width, region.Height);
        try
        {
            long left = region.X / this.segmentWidth, right = (region.X + region.Width - 1) / this.segmentWidth;
            long top = region.Y / this.segmentHeight, bottom = (region.Y + region.Height - 1) / this.segmentHeight;
            for (long sy = top; sy <= bottom; sy++)
            {
                for (long sx = left; sx <= right; sx++)
                {
                    OperationExecution.Checkpoint();
                    this.ReadIntersection(output, region, sx, sy, budget);
                }
            }

            output.Metadata.DecodedImageFormat = ImageFormat.Tiff;
            return output;
        }
        catch (Exception error)
        {
            output.Dispose();
            if (DecoderGuard.IsMalformedInputSymptom(error))
            {
                throw DecoderGuard.Wrap("TIFF region", error);
            }

            throw;
        }
    }

    /// <summary>Lazily yields a processing grid over the page (or a supplied region), independent of its storage tiles. Caller owns each yielded tile.</summary>
    /// <remarks>No operation scope survives a yield. Stop early to avoid further reads. Compressed segments may be decoded again when output tiles overlap one storage segment.</remarks>
    public IEnumerable<TiffRegionTile<TPixel>> ReadTiles<TPixel>(int tileWidth = 512, int tileHeight = 512,
        TiffRegion? region = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (tileWidth < 1 || tileHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tileWidth), "Output tile dimensions must be positive.");
        }

        if (region is { } requested)
        {
            this.ValidateRegion(requested, applyPixelLimit: false);
        }

        long startX = region?.X ?? 0, startY = region?.Y ?? 0;
        long endX = region is { } r ? r.X + r.Width : this.Width;
        long endY = region is { } q ? q.Y + q.Height : this.Height;
        long yielded = 0;
        for (long y = startY; y < endY;)
        {
            int height = (int)Math.Min(tileHeight, endY - y);
            for (long x = startX; x < endX;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (yielded++ >= this.options.MaxTiles)
                {
                    throw new ImageSizeLimitExceededException("TIFF output tile enumeration exceeds MaxTiles.");
                }

                int width = (int)Math.Min(tileWidth, endX - x);
                var tile = new TiffRegion(x, y, width, height);
                yield return new TiffRegionTile<TPixel>(tile, this.ReadRegion<TPixel>(tile, cancellationToken));
                x += width;
            }

            y += height;
        }
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            if (!this.leaveOpen)
            {
                this.stream.Dispose();
            }
        }
    }

    private void ValidateRegion(TiffRegion region, bool applyPixelLimit = true)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < 1 || region.Height < 1
            || region.X > this.Width - region.Width || region.Y > this.Height - region.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region must lie within stored page dimensions.");
        }

        if (applyPixelLimit && (long)region.Width * region.Height > this.options.MaxRegionPixels)
        {
            throw new ImageSizeLimitExceededException("TIFF output region exceeds MaxRegionPixels.");
        }
    }

    private void ReadIntersection<TPixel>(Image<TPixel> output, TiffRegion region, long tileX, long tileY, ReadBudget budget)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        long index = checked((tileY * this.segmentsAcross) + tileX);
        long offset = this.Value(this.offsets, index, budget), count = this.Value(this.counts, index, budget);
        this.Bounds(offset, count);
        long startX = tileX * this.segmentWidth, startY = tileY * this.segmentHeight;
        long rows = this.IsTiled ? this.segmentHeight : Math.Min(this.segmentHeight, this.Height - startY);
        int bytesPerPixel = this.samples * (this.bits / 8);
        long rowBytes = checked(this.segmentWidth * bytesPerPixel);
        long expected = checked(rowBytes * rows);
        long left = Math.Max(startX, region.X), top = Math.Max(startY, region.Y);
        long right = Math.Min(Math.Min(this.Width, startX > long.MaxValue - this.segmentWidth ? long.MaxValue : startX + this.segmentWidth), region.X + region.Width);
        long bottom = Math.Min(Math.Min(this.Height, startY > long.MaxValue - rows ? long.MaxValue : startY + rows), region.Y + region.Height);
        int copyWidth = checked((int)(right - left));
        int copyBytes = checked(copyWidth * bytesPerPixel);
        bool direct = this.compression == 1 && this.predictor == 1;
        if (direct && count < expected)
        {
            throw new InvalidImageContentException("Uncompressed TIFF segment byte count is too small.");
        }

        byte[] decoded;
        if (direct)
        {
            if (copyBytes > this.options.MaxDecodedSegmentBytes)
            {
                throw new ImageSizeLimitExceededException("TIFF row slice exceeds MaxDecodedSegmentBytes.");
            }

            decoded = new byte[copyBytes];
        }
        else
        {
            if (count > this.options.MaxEncodedSegmentBytes || expected > this.options.MaxDecodedSegmentBytes
                || this.segmentWidth > int.MaxValue || rows > int.MaxValue)
            {
                throw new ImageSizeLimitExceededException("Intersecting TIFF segment exceeds the encoded/decoded segment budget.");
            }

            byte[] encoded = new byte[checked((int)count)];
            this.ReadAt(offset, encoded, budget);
            decoded = new byte[checked((int)expected)];
            TiffDecoder.DecompressRegionSegment(encoded, decoded, (int)this.segmentWidth, (int)rows,
                this.bits, this.samples, this.compression, this.predictor, this.bigEndian);
        }

        bool wide = this.bits == 16 && PixelOps.IsHighPrecision<TPixel>();
        Rgba64[] wideRow = wide ? new Rgba64[copyWidth] : [];
        Rgba32[] byteRow = wide ? [] : new Rgba32[copyWidth];
        for (long y = top; y < bottom; y++)
        {
            OperationExecution.Checkpoint();
            long rowOffset = checked(((y - startY) * rowBytes) + ((left - startX) * bytesPerPixel));
            ReadOnlySpan<byte> row;
            if (direct)
            {
                this.ReadAt(checked(offset + rowOffset), decoded, budget);
                row = decoded;
            }
            else
            {
                row = decoded.AsSpan(checked((int)rowOffset), copyBytes);
            }

            Span<TPixel> target = output.Frames.RootFrame.GetRowSpan((int)(y - region.Y)).Slice((int)(left - region.X), copyWidth);
            TiffDecoder.ConvertRegionRow(row, target, this.bits, this.samples, this.photometric, this.bigEndian, byteRow, wideRow);
        }
    }

    private (Dictionary<int, Field> Fields, long Next) ReadDirectory(long offset, ReadBudget budget)
    {
        int countBytes = this.IsBigTiff ? 8 : 2, entryBytes = this.IsBigTiff ? 20 : 12, offsetBytes = this.IsBigTiff ? 8 : 4;
        Span<byte> scratch = stackalloc byte[20];
        this.ReadAt(offset, scratch[..countBytes], budget);
        long entries = this.IsBigTiff ? Signed(this.U64(scratch)) : this.U16(scratch);
        if (entries > this.options.MaxDirectoryEntries)
        {
            throw new ImageSizeLimitExceededException("TIFF directory exceeds MaxDirectoryEntries.");
        }

        long start = checked(offset + countBytes);
        this.Bounds(start, checked((entries * entryBytes) + offsetBytes));
        var fields = new Dictionary<int, Field>();
        for (long i = 0; i < entries; i++)
        {
            long entryOffset = start + (i * entryBytes);
            this.ReadAt(entryOffset, scratch[..entryBytes], budget);
            int tag = this.U16(scratch), type = this.U16(scratch[2..]);
            if (tag is not (256 or 257 or 258 or 259 or 262 or 266 or 273 or 277 or 278 or 279 or 284 or 317 or 322 or 323 or 324 or 325 or 338 or 339))
            {
                continue;
            }

            int elementSize = type switch { 1 => 1, 3 => 2, 4 or 13 => 4, 16 or 18 => 8, _ => 0 };
            if (elementSize == 0)
            {
                throw new NotSupportedException($"TIFF region tag {tag} uses unsupported numeric field type {type}.");
            }

            long count = this.IsBigTiff ? Signed(this.U64(scratch[4..])) : this.U32(scratch[4..]);
            if (count < 1 || count > long.MaxValue / elementSize)
            {
                throw new InvalidImageContentException("Invalid TIFF field count.");
            }

            long size = count * elementSize;
            int valuePosition = this.IsBigTiff ? 12 : 8;
            long values = size <= offsetBytes ? entryOffset + valuePosition
                : this.IsBigTiff ? Signed(this.U64(scratch[valuePosition..])) : this.U32(scratch[valuePosition..]);
            this.Bounds(values, size);
            if (!fields.TryAdd(tag, new Field(values, count, elementSize)))
            {
                throw new InvalidImageContentException($"Duplicate TIFF region tag {tag}.");
            }
        }

        this.ReadAt(start + (entries * entryBytes), scratch[..offsetBytes], budget);
        return (fields, this.IsBigTiff ? Signed(this.U64(scratch)) : this.U32(scratch));
    }

    private long Single(int tag, long fallback, ReadBudget budget)
    {
        if (!this.fields.TryGetValue(tag, out Field field))
        {
            return fallback;
        }

        if (field.Count != 1)
        {
            throw new InvalidImageContentException($"TIFF scalar tag {tag} has multiple values.");
        }

        return this.Value(field, 0, budget);
    }

    private long[] SmallValues(int tag, long[] fallback, int maximum, ReadBudget budget)
    {
        if (!this.fields.TryGetValue(tag, out Field field))
        {
            return fallback;
        }

        if (field.Count != 1 && field.Count != maximum)
        {
            throw new InvalidImageContentException($"TIFF sample tag {tag} has an incompatible value count.");
        }

        var values = new long[(int)field.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = this.Value(field, i, budget);
        }

        return values;
    }

    private long Value(Field field, long index, ReadBudget budget)
    {
        if (index < 0 || index >= field.Count)
        {
            throw new InvalidImageContentException("TIFF field index outside its declared count.");
        }

        Span<byte> bytes = stackalloc byte[8];
        this.ReadAt(checked(field.Offset + (index * field.ElementSize)), bytes[..field.ElementSize], budget);
        return field.ElementSize switch { 1 => bytes[0], 2 => this.U16(bytes), 4 => this.U32(bytes), _ => Signed(this.U64(bytes)) };
    }

    private void ReadAt(long offset, Span<byte> destination, ReadBudget budget)
    {
        OperationExecution.Checkpoint();
        this.Bounds(offset, destination.Length);
        if (destination.Length > budget.Remaining)
        {
            throw new ImageSizeLimitExceededException("TIFF region operation exceeds MaxReadBytes.");
        }

        budget.Remaining -= destination.Length;
        this.stream.Seek(checked(this.origin + offset), SeekOrigin.Begin);
        while (!destination.IsEmpty)
        {
            OperationExecution.Checkpoint();
            int count = this.stream.Read(destination[..Math.Min(destination.Length, 65536)]);
            if (count == 0)
            {
                throw new InvalidImageContentException("Truncated TIFF region data.");
            }

            destination = destination[count..];
        }
    }

    private void Bounds(long offset, long count)
    {
        if (offset < 0 || count < 0 || offset > this.length || count > this.length - offset)
        {
            throw new InvalidImageContentException("TIFF offset/count lies outside the seekable source.");
        }
    }

    private ushort U16(ReadOnlySpan<byte> data) => this.bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);
    private uint U32(ReadOnlySpan<byte> data) => this.bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
    private ulong U64(ReadOnlySpan<byte> data) => this.bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data);
    private static long Signed(ulong value) => value <= long.MaxValue ? (long)value : throw new NotSupportedException("TIFF offsets/counts beyond signed 64-bit stream addressing are unsupported.");
    private readonly record struct Field(long Offset, long Count, int ElementSize);
    private sealed class ReadBudget(long remaining) { internal long Remaining = remaining; }
}
