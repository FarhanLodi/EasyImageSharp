using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Decodes a non-interlaced, non-animated PNG straight from a <see cref="Stream"/>, one row at a time, with
/// memory bounded by one scanline plus the inflater's window instead of the whole encoded file and frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Semantics.</b> Rows are converted by the same helpers <see cref="PngDecoder"/> uses, so every colour
/// type and bit depth, palette alpha, tRNS colour keys (matched at full sample precision) and the 16-bit
/// path into high-precision pixel formats produce exactly the pixels <see cref="PngDecoder"/> produces. The
/// chunk walk applies the decoder's rules too: the same structural checks and messages, IDAT chunks
/// concatenated wherever they appear, trailing bytes after the zlib stream ignored, surplus decompressed data
/// rejected, and CRCs verified as the buffered decoder verifies them: a bad CRC on a critical chunk rejects the
/// file, while a damaged ancillary chunk is discarded. IDAT CRCs are checked as the compressed data is consumed.
/// </para>
/// <para>
/// <b>Seekable and non-seekable input.</b> On a seekable stream the whole chunk table is walked first (IDAT
/// payloads are seeked over, not read), so metadata chunks that follow the image data are reported and every
/// structural error is raised before the first row, exactly as the buffered decoder does; decoding then seeks
/// back to the first IDAT chunk. A non-seekable stream is read once, front to back: <see cref="IScanlineSource{TPixel}.Metadata"/>
/// holds what preceded the image data when the first row is requested, chunks after it are validated as they
/// are reached (a late metadata chunk is still added to the metadata, but after any sink has written its
/// header), and a tRNS chunk after the image data - which the format forbids, but the buffered decoder
/// applies anyway - raises <see cref="InvalidImageContentException"/> because the rows it would change have
/// already been produced.
/// </para>
/// <para>
/// <b>What does not stream.</b> Adam7 interlacing spreads every row over seven passes and an APNG composites
/// frames on a canvas, so <see cref="TryCreate{TPixel}"/> returns <see langword="false"/> for both once the
/// IHDR or acTL chunk says so; the stream has then been read past the header, and the caller rewinds it and
/// falls back to <see cref="PngDecoder"/>.
/// </para>
/// <para>
/// <b>Limits.</b> <see cref="DecoderOptions.MaxPixels"/> is applied to the declared frame size before any row
/// buffer is allocated, <see cref="DecoderOptions.MaxEncodedBytes"/> to the bytes read from the stream (up
/// front for a seekable stream), and cancellation is checked per chunk and per row. The 2 GiB address limit of
/// the buffered decoders does not apply. Metadata chunk payloads are held in memory while they are parsed, as
/// the buffered decoder holds the whole file.
/// </para>
/// <para>
/// <b>Inflate backend.</b> The library's managed <see cref="Inflater"/> is a <see langword="ref"/> struct that
/// borrows its input span, so it cannot live inside a source object that outlives one call; this source uses
/// <see cref="ZLibStream"/> on every target framework. On .NET 10 that is also what <see cref="PngDecoder"/>
/// uses; on .NET 8 it is classic zlib, which <see cref="PngIdatReader"/> documents as slower than the managed
/// inflater the buffered decoder uses there.
/// </para>
/// </remarks>
internal static class PngScanlineSource
{
    internal const uint IhdrType = 0x49484452u;
    internal const uint PlteType = 0x504C5445u;
    internal const uint TrnsType = 0x74524E53u;
    internal const uint IdatType = 0x49444154u;
    internal const uint IendType = 0x49454E44u;

    /// <summary>
    /// Reads the PNG header and every chunk up to the image data and, when the image can be streamed, returns a
    /// source positioned at its first row. The source owns no part of <paramref name="stream"/>: it never
    /// disposes it, but it does read (and on a seekable stream, seek) it until the source is disposed.
    /// </summary>
    /// <param name="stream">The encoded PNG, positioned at its signature.</param>
    /// <param name="options">The limits, cancellation and execution options.</param>
    /// <param name="source">The source, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <typeparam name="TPixel">The pixel format the rows are produced in.</typeparam>
    /// <returns>
    /// <see langword="false"/>, without throwing, when the data is not a PNG or is an interlaced or animated one.
    /// </returns>
    /// <exception cref="InvalidImageContentException">The header or a chunk before the image data is malformed.</exception>
    /// <exception cref="ImageSizeLimitExceededException">The image exceeds <paramref name="options"/>' limits.</exception>
    public static bool TryCreate<TPixel>(Stream stream, DecoderOptions options, out IScanlineSource<TPixel>? source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        source = null;
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        PngStreamInput? input = null;
        try
        {
            input = new PngStreamInput(stream, options.MaxEncodedBytes);
            PngStreamHeader? header = PngStreamHeader.Read(input, options);
            if (header is null)
            {
                return false;
            }

            source = new PngScanlineSource<TPixel>(input, header, options);
            input = null; // The source owns it now.
            return true;
        }
        catch (SourceStreamException ex)
        {
            ex.Rethrow();
            throw; // Unreachable: Rethrow never returns.
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("PNG", ex);
        }
        finally
        {
            input?.Dispose();
        }
    }

    internal static InvalidImageContentException Truncated() => new("PNG chunk is truncated.");

    /// <summary>Reads the length and type of the next chunk; false when fewer than eight bytes remain.</summary>
    internal static bool TryReadChunkHeader(PngStreamInput input, out int length, out uint type)
    {
        Span<byte> header = stackalloc byte[8];
        if (input.ReadAtMost(header) < header.Length)
        {
            length = 0;
            type = 0;
            return false;
        }

        length = BinaryPrimitives.ReadInt32BigEndian(header);
        type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (length < 0 || (input.CanSeek && input.Position + length + 4L > input.Length))
        {
            throw Truncated();
        }

        return true;
    }

    /// <summary>Reads a chunk payload, allocating only as fast as bytes actually arrive.</summary>
    internal static ArraySegment<byte> ReadPayload(PngStreamInput input, int length)
    {
        const int Piece = 1 << 16;
        if (length <= Piece || input.CanSeek)
        {
            // Small, or a seekable stream whose length was already checked against the chunk's.
            byte[] data = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (input.ReadAtMost(data) < length)
            {
                throw Truncated();
            }

            return data;
        }

        // A non-seekable stream can claim any length; grow with the data instead of trusting the claim.
        var collected = new MemoryStream();
        byte[] piece = ArrayPool<byte>.Shared.Rent(Piece);
        try
        {
            int remaining = length;
            while (remaining > 0)
            {
                int read = input.ReadAtMost(piece.AsSpan(0, Math.Min(Piece, remaining)));
                if (read == 0)
                {
                    throw Truncated();
                }

                collected.Write(piece, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(piece);
        }

        return new ArraySegment<byte>(collected.GetBuffer(), 0, length);
    }

    /// <summary>Reads the four CRC bytes that close a chunk.</summary>
    internal static uint ReadCrc(PngStreamInput input)
    {
        Span<byte> crc = stackalloc byte[4];
        if (input.ReadAtMost(crc) < crc.Length)
        {
            throw Truncated();
        }

        return BinaryPrimitives.ReadUInt32BigEndian(crc);
    }

    /// <summary>
    /// Reads the CRC that closes a chunk whose payload has been read, and checks it as <see cref="PngDecoder"/>
    /// does: false for a damaged ancillary chunk (to be discarded); a damaged critical chunk throws.
    /// </summary>
    internal static bool VerifyCrc(PngStreamInput input, uint type, ReadOnlySpan<byte> payload)
    {
        if (ReadCrc(input) == PngDecoder.ChunkCrc(type, payload))
        {
            return true;
        }

        return PngDecoder.IsCrcCritical(type) ? throw PngDecoder.CrcMismatch(type) : false;
    }

    /// <summary>
    /// Passes over a chunk the walk does not use. A critical chunk is read so that its CRC is checked, as the
    /// buffered decoder checks it; an ancillary one is discarded whatever its CRC, so it is skipped unread.
    /// </summary>
    internal static void SkipUnusedChunk(PngStreamInput input, uint type, int length)
    {
        if (PngDecoder.IsCrcCritical(type))
        {
            VerifyCrc(input, type, ReadPayload(input, length));
        }
        else
        {
            SkipChunk(input, length);
        }
    }

    /// <summary>Skips a chunk's payload and CRC.</summary>
    internal static void SkipChunk(PngStreamInput input, int length)
    {
        if (!input.Skip(length + 4L))
        {
            throw Truncated();
        }
    }

    /// <summary>
    /// True for the ancillary chunks <see cref="PngMetadataChunks.TryReadChunk"/> interprets, whose payloads
    /// must be read; every other unknown chunk is skipped unread. Keep in step with that method.
    /// </summary>
    internal static bool IsMetadataChunk(uint type)
        => type is 0x70485973u // pHYs
            or 0x67414D41u // gAMA
            or 0x69434350u // iCCP
            or 0x65584966u // eXIf
            or 0x74455874u // tEXt
            or 0x7A545874u // zTXt
            or 0x69545874u; // iTXt
}

/// <summary>Rows of one streamed PNG; created by <see cref="PngScanlineSource.TryCreate{TPixel}"/>.</summary>
/// <typeparam name="TPixel">The pixel format the rows are produced in.</typeparam>
internal sealed class PngScanlineSource<TPixel> : IScanlineSource<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly PngStreamInput input;
    private readonly PngStreamHeader state;
    private readonly PngIdatStream idat;
    private readonly CancellationToken cancellation;

    /// <summary>Bytes of one filtered scanline, without its filter byte.</summary>
    private readonly int bytesPerRow;

    /// <summary>The filter's byte distance to the pixel on the left, at least 1.</summary>
    private readonly int filterBpp;

    /// <summary>Whether 16-bit samples reach the pixel format intact (see <see cref="PngDecoder"/>).</summary>
    private readonly bool wideSamples;

    private ZLibStream? zlib;
    private TPixel[]? paletteLut;

    // Scanline buffers hold the filter byte at index 0 and the row from index 1, so one read fetches both.
    private byte[]? current;
    private byte[]? previous;
    private Rgba32[]? rgbaRow;
    private Rgba64[]? wideRow;
    private int row;
    private bool disposed;

    /// <summary>Set once a row has failed: the inflater and filter state are then unusable.</summary>
    private bool faulted;

    internal PngScanlineSource(PngStreamInput input, PngStreamHeader state, DecoderOptions options)
    {
        this.input = input;
        this.state = state;
        int bitsPerPixel = state.Header.BitDepth * state.Header.Channels;
        long rowBytes = (((long)state.Header.Width * bitsPerPixel) + 7) / 8;
        if (rowBytes + 1 > Array.MaxLength)
        {
            throw new ImageSizeLimitExceededException(
                $"PNG scanlines of {rowBytes:N0} bytes exceed the largest buffer this library can address.");
        }

        this.bytesPerRow = (int)rowBytes;
        this.filterBpp = (bitsPerPixel + 7) / 8;
        this.wideSamples = state.Header.BitDepth == 16 && PixelOps.IsHighPrecision<TPixel>();
        this.idat = new PngIdatStream(input, state);
        this.cancellation = options.ExecutionOptions?.CancellationToken ?? default;
    }

    public int Width => this.state.Header.Width;

    public int Height => this.state.Header.Height;

    public ImageMetadata Metadata => this.state.Metadata;

    public int CurrentRow => this.row;

    public bool ReadRow(Span<TPixel> destination)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.faulted)
        {
            throw new InvalidOperationException("An earlier row of this PNG failed to decode, so no further rows can be read.");
        }

        if (this.row >= this.Height)
        {
            return false;
        }

        if (destination.Length < this.Width)
        {
            throw new ArgumentException($"The destination holds {destination.Length} pixels but a row has {this.Width}.", nameof(destination));
        }

        OperationExecution.Checkpoint();
        this.cancellation.ThrowIfCancellationRequested();
        try
        {
            this.DecodeRow(destination[..this.Width]);
        }
        catch (SourceStreamException ex)
        {
            this.faulted = true;
            ex.Rethrow();
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            this.faulted = true;
            throw DecoderGuard.Wrap("PNG", ex);
        }
        catch
        {
            // Cancellation and limit errors leave the inflater part-way through a row as well.
            this.faulted = true;
            throw;
        }

        return true;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.ReleaseDecoder();
        this.input.Dispose();
    }

    private void DecodeRow(Span<TPixel> destination)
    {
        this.EnsureStarted();
        Span<byte> scanline = this.current.AsSpan(0, this.bytesPerRow + 1);
        int read = 0;
        while (read < scanline.Length)
        {
            int n = this.zlib!.Read(scanline[read..]);
            if (n <= 0)
            {
                throw new InvalidImageContentException("PNG pixel data ended unexpectedly.");
            }

            read += n;
        }

        Span<byte> pixels = scanline[1..];
        PngFilters.Unfilter(
            scanline[0], pixels, this.row == 0 ? default : this.previous.AsSpan(1, this.bytesPerRow), this.filterBpp);

        // The same three-way conversion as PngDecoder.ReadImage's non-interlaced branch.
        ref PngDecoder.PngHeader header = ref this.state.Header;
        int width = header.Width;
        if (this.wideSamples)
        {
            Span<Rgba64> wide = this.wideRow.AsSpan(0, width);
            PngDecoder.ConvertScanline16(pixels, wide, width, header);
            PixelOps.Convert<Rgba64, TPixel>(wide, destination);
        }
        else if (!PngDecoder.TryConvertRowDirect(pixels, width, header, this.state.Palette, this.paletteLut, destination))
        {
            Span<Rgba32> rgba = this.rgbaRow.AsSpan(0, width);
            PngDecoder.ConvertScanline(pixels, rgba, width, header, this.state.Palette);
            PixelOps.FromRgba32<TPixel>(rgba, destination);
        }

        (this.previous, this.current) = (this.current, this.previous);
        this.row++;
        if (this.row == this.Height)
        {
            this.Finish();
        }
    }

    /// <summary>Allocates the row buffers and the inflater on the first row, after the caller has seen the size.</summary>
    private void EnsureStarted()
    {
        if (this.zlib is not null)
        {
            return;
        }

        int width = this.Width;
        this.current = ArrayPool<byte>.Shared.Rent(this.bytesPerRow + 1);
        this.previous = ArrayPool<byte>.Shared.Rent(this.bytesPerRow + 1);
        if (this.wideSamples)
        {
            this.wideRow = ArrayPool<Rgba64>.Shared.Rent(width);
        }
        else
        {
            this.rgbaRow = ArrayPool<Rgba32>.Shared.Rent(width);
        }

        if (this.state.Palette is not null)
        {
            this.paletteLut = PngDecoder.BuildPaletteLut<TPixel>(this.state.Palette);
        }

        this.zlib = new ZLibStream(this.idat, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>
    /// Runs the checks the buffered decoder makes once the last row is in: the compressed data may not decode
    /// to more bytes than the rows need, and - on a non-seekable stream, whose chunks were not walked in
    /// advance - the remaining chunks must be well formed.
    /// </summary>
    private void Finish()
    {
        Span<byte> probe = stackalloc byte[1];
        if (this.zlib!.Read(probe) > 0)
        {
            throw new InvalidImageContentException("PNG pixel data is longer than the image dimensions allow.");
        }

        this.idat.Finish();
        PngMetadataChunks.Finish(this.state.Metadata);
        this.ReleaseDecoder();
    }

    private void ReleaseDecoder()
    {
        this.zlib?.Dispose();
        ReturnToPool(ref this.current);
        ReturnToPool(ref this.previous);
        ReturnToPool(ref this.rgbaRow);
        ReturnToPool(ref this.wideRow);
    }

    private static void ReturnToPool<T>(ref T[]? array)
    {
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
            array = null;
        }
    }
}

/// <summary>
/// What the chunks before the image data established: the header, palette and metadata, and where the image
/// data starts. On a seekable stream the walk covers the whole chunk table, exactly like
/// <see cref="PngDecoder"/>'s; on a non-seekable one it stops inside the first non-empty IDAT chunk.
/// </summary>
internal sealed class PngStreamHeader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private PngStreamHeader(ImageMetadata metadata)
    {
        this.Metadata = metadata;
        this.PngMetadata = metadata.GetPngMetadata();
    }

    /// <summary>The IHDR facts and any tRNS colour key; a field so the converters can take it by reference.</summary>
    public PngDecoder.PngHeader Header;

    /// <summary>The PLTE palette with any tRNS alpha, for colour type 3.</summary>
    public Rgba32[]? Palette { get; private set; }

    public ImageMetadata Metadata { get; }

    public PngMetadata PngMetadata { get; }

    /// <summary>Seekable input: the position of the first IDAT chunk's header.</summary>
    public long FirstIdatChunk { get; private set; } = -1;

    /// <summary>Non-seekable input: the payload bytes left in the IDAT chunk the input stopped in.</summary>
    public int FirstIdatRemaining { get; private set; }

    /// <summary>
    /// Walks the chunks as <see cref="PngDecoder"/> does and returns <see langword="null"/> when the data is not
    /// a PNG or is one that cannot be streamed (interlaced or animated).
    /// </summary>
    public static PngStreamHeader? Read(PngStreamInput input, DecoderOptions options)
    {
        Span<byte> signature = stackalloc byte[8];
        if (input.ReadAtMost(signature) < signature.Length || !signature.SequenceEqual(Signature))
        {
            return null;
        }

        var state = new PngStreamHeader(new ImageMetadata { DecodedImageFormat = ImageFormat.Png });
        bool sawHeader = false;
        bool sawIdat = false;
        long idatLength = 0;
        bool walking = true;
        while (walking)
        {
            OperationExecution.Checkpoint();
            long chunkStart = input.Position;
            if (!PngScanlineSource.TryReadChunkHeader(input, out int length, out uint type))
            {
                break;
            }

            if (!sawHeader && type != PngScanlineSource.IhdrType)
            {
                throw new InvalidImageContentException("PNG file does not start with an IHDR chunk.");
            }

            switch (type)
            {
                case PngScanlineSource.IhdrType:
                    if (sawHeader)
                    {
                        throw new InvalidImageContentException("PNG contains more than one IHDR chunk.");
                    }

                    if (length != 13)
                    {
                        // ParseHeader's own message, raised before a bogus length is read.
                        throw new InvalidImageContentException("PNG IHDR chunk has an invalid length.");
                    }

                    ArraySegment<byte> ihdr = PngScanlineSource.ReadPayload(input, length);
                    PngScanlineSource.VerifyCrc(input, type, ihdr);
                    state.Header = PngDecoder.ParseHeader(ihdr, strictLength: true);
                    options.EnsureFrameWithinLimits(state.Header.Width, state.Header.Height, "PNG");
                    sawHeader = true;
                    state.PngMetadata.ColorType = (PngColorType)state.Header.ColorType;
                    state.PngMetadata.BitDepth = (PngBitDepth)state.Header.BitDepth;
                    state.PngMetadata.Interlaced = state.Header.Interlaced;
                    if (state.Header.Interlaced)
                    {
                        return null; // Adam7 rows arrive over seven passes; the caller falls back.
                    }

                    break;

                case PngScanlineSource.PlteType:
                    if (state.Header.ColorType != 3)
                    {
                        PngScanlineSource.SkipUnusedChunk(input, type, length); // A suggested palette is not consumed.
                        break;
                    }

                    if (length == 0 || length % 3 != 0 || length / 3 > PngDecoder.MaxPaletteEntries
                        || state.Palette is not null || idatLength > 0)
                    {
                        throw new InvalidImageContentException("PNG PLTE chunk is invalid or misplaced.");
                    }

                    ReadOnlySpan<byte> entries = PngScanlineSource.ReadPayload(input, length);
                    PngScanlineSource.VerifyCrc(input, type, entries);
                    var palette = new Rgba32[length / 3];
                    for (int i = 0; i < palette.Length; i++)
                    {
                        palette[i] = new Rgba32(entries[i * 3], entries[(i * 3) + 1], entries[(i * 3) + 2]);
                    }

                    state.Palette = palette;
                    break;

                case PngScanlineSource.TrnsType:
                {
                    ArraySegment<byte> transparency = PngScanlineSource.ReadPayload(input, length);
                    if (PngScanlineSource.VerifyCrc(input, type, transparency))
                    {
                        PngDecoder.ParseTransparency(transparency, ref state.Header, state.Palette);
                    }

                    break;
                }

                case PngScanlineSource.IdatType:
                    sawIdat = true;
                    idatLength += length;
                    if (input.CanSeek)
                    {
                        if (state.FirstIdatChunk < 0)
                        {
                            state.FirstIdatChunk = chunkStart;
                        }

                        PngScanlineSource.SkipChunk(input, length);
                    }
                    else if (length > 0)
                    {
                        state.FirstIdatRemaining = length;
                        walking = false; // Positioned at the first compressed byte.
                    }
                    else
                    {
                        // An empty IDAT counts as image data for acTL but not for PLTE, as in the buffered decoder.
                        PngScanlineSource.VerifyCrc(input, type, ReadOnlySpan<byte>.Empty);
                    }

                    break;

                case PngAnimation.ActlType:
                    if (sawIdat)
                    {
                        throw new InvalidImageContentException("PNG acTL chunk must appear once, before the image data.");
                    }

                    return null; // An APNG composites frames on a canvas; the caller falls back.

                case PngScanlineSource.IendType:
                    PngScanlineSource.SkipUnusedChunk(input, type, length);
                    walking = false;
                    break;

                default:
                    if (PngScanlineSource.IsMetadataChunk(type))
                    {
                        ArraySegment<byte> payload = PngScanlineSource.ReadPayload(input, length);
                        if (PngScanlineSource.VerifyCrc(input, type, payload))
                        {
                            PngMetadataChunks.TryReadChunk(type, payload, state.Metadata, state.PngMetadata);
                        }
                    }
                    else
                    {
                        PngScanlineSource.SkipUnusedChunk(input, type, length);
                    }

                    break;
            }
        }

        if (!sawHeader || idatLength == 0)
        {
            throw new InvalidImageContentException("PNG image is missing its IHDR or IDAT chunks.");
        }

        PngMetadataChunks.Finish(state.Metadata);
        if (state.Header.ColorType == 3 && state.Palette is null)
        {
            throw new InvalidImageContentException("Palette-based PNG is missing its PLTE chunk.");
        }

        if (input.CanSeek)
        {
            input.Seek(state.FirstIdatChunk);
        }

        return state;
    }

    /// <summary>
    /// Applies a chunk met after the image data has begun on a non-seekable stream, where it could not be
    /// seen in advance. Each case raises what the buffered decoder raises for the same chunk, except a tRNS
    /// that would recolour rows already produced.
    /// </summary>
    public void ApplyTrailingChunk(PngStreamInput input, uint type, int length)
    {
        switch (type)
        {
            case PngScanlineSource.IhdrType:
                throw new InvalidImageContentException("PNG contains more than one IHDR chunk.");

            case PngScanlineSource.PlteType when this.Header.ColorType == 3:
                throw new InvalidImageContentException("PNG PLTE chunk is invalid or misplaced.");

            case PngScanlineSource.TrnsType when this.Header.ColorType is 0 or 2 or 3:
                throw new InvalidImageContentException(
                    "PNG tRNS chunk follows the image data; a streamed decode of a non-seekable stream cannot apply it.");

            case PngAnimation.ActlType:
                throw new InvalidImageContentException("PNG acTL chunk must appear once, before the image data.");

            default:
                if (PngScanlineSource.IsMetadataChunk(type))
                {
                    ArraySegment<byte> payload = PngScanlineSource.ReadPayload(input, length);
                    if (PngScanlineSource.VerifyCrc(input, type, payload))
                    {
                        PngMetadataChunks.TryReadChunk(type, payload, this.Metadata, this.PngMetadata);
                    }
                }
                else
                {
                    PngScanlineSource.SkipUnusedChunk(input, type, length);
                }

                break;
        }
    }
}

/// <summary>
/// The concatenated IDAT payloads as a read-only stream, fed to <see cref="ZLibStream"/>. It walks the chunk
/// table on demand: other chunks between IDAT chunks are skipped (seekable input, already validated by the
/// header walk) or applied (non-seekable input), and the data ends at IEND or at the end of the stream.
/// </summary>
internal sealed class PngIdatStream : Stream
{
    private readonly PngStreamInput input;
    private readonly PngStreamHeader state;

    /// <summary>Payload bytes left in the current IDAT chunk.</summary>
    private int remaining;

    /// <summary>The CRC of the four type bytes <c>IDAT</c>, where every IDAT chunk's CRC starts.</summary>
    private static readonly uint IdatTypeCrc = Crc32.Compute("IDAT"u8);

    /// <summary>True while the current chunk's CRC is still unread.</summary>
    private bool inChunk;

    /// <summary>The running CRC of the current IDAT chunk's type and the payload bytes consumed so far.</summary>
    private uint crc = IdatTypeCrc;

    private bool ended;

    public PngIdatStream(PngStreamInput input, PngStreamHeader state)
    {
        this.input = input;
        this.state = state;

        // A non-seekable header walk stops inside the first IDAT payload; a seekable one rewinds to its header.
        this.remaining = state.FirstIdatRemaining;
        this.inChunk = !input.CanSeek;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return this.Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (this.remaining == 0)
        {
            if (!this.NextIdatChunk())
            {
                return 0;
            }
        }

        int read = this.input.ReadAtMost(buffer[..Math.Min(buffer.Length, this.remaining)]);
        if (read == 0)
        {
            throw PngScanlineSource.Truncated();
        }

        this.crc = Crc32.Append(this.crc, buffer[..read]);
        this.remaining -= read;
        return read;
    }

    /// <summary>
    /// Walks the chunks after the last row has been decoded: the rest of the image data is read so that every
    /// IDAT CRC is checked, and a non-seekable stream gets the structural checks the header walk gave a seekable one.
    /// </summary>
    public void Finish()
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                while (this.remaining > 0)
                {
                    // Read throws on truncation, so every call consumes at least one byte of the chunk.
                    int consumed = this.Read(scratch.AsSpan(0, Math.Min(scratch.Length, this.remaining)));
                    Debug.Assert(consumed > 0, "PngIdatStream.Read returned no data inside a chunk.");
                }

                if (!this.NextIdatChunk())
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Moves to the next non-empty IDAT payload; false once the chunk stream has ended.</summary>
    private bool NextIdatChunk()
    {
        if (this.ended)
        {
            return false;
        }

        if (this.inChunk)
        {
            if (PngScanlineSource.ReadCrc(this.input) != this.crc)
            {
                throw PngDecoder.CrcMismatch(PngScanlineSource.IdatType);
            }

            this.inChunk = false;
        }

        while (true)
        {
            OperationExecution.Checkpoint();
            if (!PngScanlineSource.TryReadChunkHeader(this.input, out int length, out uint type))
            {
                this.ended = true;
                return false;
            }

            switch (type)
            {
                case PngScanlineSource.IdatType:
                    if (length == 0)
                    {
                        PngScanlineSource.VerifyCrc(this.input, type, ReadOnlySpan<byte>.Empty);
                        continue;
                    }

                    this.remaining = length;
                    this.inChunk = true;
                    this.crc = IdatTypeCrc;
                    return true;

                case PngScanlineSource.IendType:
                    PngScanlineSource.SkipUnusedChunk(this.input, type, length);
                    this.ended = true;
                    return false;

                default:
                    if (this.input.CanSeek)
                    {
                        PngScanlineSource.SkipChunk(this.input, length);
                    }
                    else
                    {
                        this.state.ApplyTrailingChunk(this.input, type, length);
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// A small buffered reader over the caller's stream that enforces <see cref="DecoderOptions.MaxEncodedBytes"/>,
/// checks for cancellation before every read, and carries the stream's own exceptions in
/// <see cref="SourceStreamException"/>. Positions are relative to where the stream stood when it was wrapped.
/// </summary>
internal sealed class PngStreamInput : IDisposable
{
    private const int BufferSize = 16384;

    /// <summary>
    /// The read after a seek that left the buffer: the header walk of a seekable stream only needs the next chunk's
    /// header, so it does not pull a full buffer of image data it is about to skip.
    /// </summary>
    private const int ProbeSize = 4096;

    private readonly Stream stream;
    private readonly long maxBytes;
    private readonly long origin;
    private byte[]? buffer;

    /// <summary>The position of <c>buffer[0]</c>.</summary>
    private long bufferStart;
    private int offset;
    private int count;
    private bool probeNext;

    public PngStreamInput(Stream stream, long maxBytes)
    {
        this.stream = stream;
        this.maxBytes = maxBytes;
        this.CanSeek = stream.CanSeek;
        if (this.CanSeek)
        {
            try
            {
                this.origin = stream.Position;
                this.Length = Math.Max(0, stream.Length - this.origin);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SourceStreamException(ex);
            }

            if (this.Length > maxBytes)
            {
                throw LimitExceeded();
            }
        }
        else
        {
            this.Length = -1;
        }

        this.buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    public bool CanSeek { get; }

    /// <summary>Bytes from the origin to the end of a seekable stream; -1 when the stream cannot seek.</summary>
    public long Length { get; }

    /// <summary>The position of the next byte to be read, relative to the origin.</summary>
    public long Position => this.bufferStart + this.offset;

    /// <summary>Fills <paramref name="destination"/> unless the stream ends first, and returns the bytes read.</summary>
    public int ReadAtMost(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (this.offset == this.count && !this.Fill())
            {
                break;
            }

            int n = Math.Min(destination.Length - total, this.count - this.offset);
            this.buffer.AsSpan(this.offset, n).CopyTo(destination[total..]);
            this.offset += n;
            total += n;
        }

        return total;
    }

    /// <summary>Skips <paramref name="length"/> bytes; false when the stream ends first.</summary>
    public bool Skip(long length)
    {
        long buffered = this.count - this.offset;
        if (length <= buffered)
        {
            this.offset += (int)length;
            return true;
        }

        if (this.CanSeek)
        {
            long target = this.Position + length;
            if (target > this.Length)
            {
                this.Seek(this.Length);
                return false;
            }

            this.Seek(target);
            return true;
        }

        length -= buffered;
        this.offset = this.count;
        while (length > 0)
        {
            if (!this.Fill())
            {
                return false;
            }

            int n = (int)Math.Min(length, this.count);
            this.offset = n;
            length -= n;
        }

        return true;
    }

    /// <summary>Moves a seekable stream to <paramref name="position"/>, relative to the origin.</summary>
    public void Seek(long position)
    {
        if (position >= this.bufferStart && position <= this.bufferStart + this.count)
        {
            this.offset = (int)(position - this.bufferStart);
            return;
        }

        try
        {
            this.stream.Position = this.origin + position;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SourceStreamException(ex);
        }

        this.bufferStart = position;
        this.offset = 0;
        this.count = 0;
        this.probeNext = true;
    }

    public void Dispose()
    {
        if (this.buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = null;
        }
    }

    private static ImageSizeLimitExceededException LimitExceeded()
        => new("Encoded image exceeds DecoderOptions.MaxEncodedBytes.");

    /// <summary>Refills the buffer from the stream; false at the end of the stream.</summary>
    private bool Fill()
    {
        ObjectDisposedException.ThrowIf(this.buffer is null, this);
        OperationExecution.Checkpoint();
        this.bufferStart += this.count;
        this.offset = 0;
        this.count = 0;

        // Ask for one byte past the limit, so data beyond it is noticed rather than silently cut off.
        int size = this.probeNext ? ProbeSize : BufferSize;
        this.probeNext = false;
        long allowed = this.maxBytes - this.bufferStart;
        int wanted = allowed >= size ? size : (int)Math.Max(0, allowed) + 1;
        int read;
        try
        {
            read = this.stream.Read(this.buffer, 0, wanted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SourceStreamException(ex);
        }

        if (read > 0 && this.bufferStart + read > this.maxBytes)
        {
            throw LimitExceeded();
        }

        this.count = Math.Max(0, read);
        return this.count > 0;
    }
}
