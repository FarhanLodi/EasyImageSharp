using System.Buffers.Binary;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// The APNG wire format and its compositor: the acTL, fcTL and fdAT chunk types, the frame control record
/// they carry, and the draw/dispose/snapshot steps that turn a sequence of sub-rectangles into the
/// full-canvas frames the decoder returns. Reading and writing sit side by side deliberately so the field
/// order of a chunk is only spelled out once.
/// </summary>
/// <remarks>
/// The canvas is always <see cref="Rgba32"/>. APNG frames are independent deflate streams composited with
/// straight (non-premultiplied) alpha, and blending goes through <see cref="FrameOps.SourceOver"/> rather
/// than the reference WebP blend: the latter's 24-bit reciprocal truncates, which would break the identity
/// that compositing OVER a fully transparent canvas equals a plain copy - the property that lets the first
/// frame of an animation need no special case.
/// </remarks>
internal static class PngAnimation
{
    /// <summary>The acTL (animation control) chunk type, big-endian.</summary>
    public const uint ActlType = 0x6163544Cu;

    /// <summary>The fcTL (frame control) chunk type, big-endian.</summary>
    public const uint FctlType = 0x6663544Cu;

    /// <summary>The fdAT (frame data) chunk type, big-endian.</summary>
    public const uint FdatType = 0x66644154u;

    /// <summary>The exact payload length of an acTL chunk: num_frames and num_plays.</summary>
    public const int ActlLength = 8;

    /// <summary>The exact payload length of an fcTL chunk: a sequence number, a rectangle, a delay and two operations.</summary>
    public const int FctlLength = 26;

    /// <summary>The length of the sequence number that prefixes an fdAT chunk's compressed data.</summary>
    public const int FdatHeaderLength = 4;

    // ----- Reading -----

    /// <summary>
    /// Reads an acTL payload and returns the declared frame count and play count. The frame count is
    /// validated but never trusted for sizing: it is attacker-controlled up to 2^32-1 and must be checked
    /// against the frames actually found.
    /// </summary>
    public static (int Frames, uint Plays) ParseAnimationControl(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length != ActlLength)
        {
            throw new InvalidImageContentException("PNG acTL chunk has an invalid length.");
        }

        uint frames = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        uint plays = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
        if (frames is 0 or > int.MaxValue)
        {
            throw new InvalidImageContentException("PNG acTL chunk declares an invalid frame count.");
        }

        return ((int)frames, plays);
    }

    /// <summary>
    /// Reads an fcTL payload and validates that the frame's rectangle lies inside the canvas and that its
    /// disposal and blend operations are ones the format defines.
    /// </summary>
    public static ApngFrameControl ParseFrameControl(ReadOnlySpan<byte> chunk, int canvasWidth, int canvasHeight)
    {
        if (chunk.Length != FctlLength)
        {
            throw new InvalidImageContentException("PNG fcTL chunk has an invalid length.");
        }

        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]);
        uint xOffset = BinaryPrimitives.ReadUInt32BigEndian(chunk[12..]);
        uint yOffset = BinaryPrimitives.ReadUInt32BigEndian(chunk[16..]);
        ushort delayNumerator = BinaryPrimitives.ReadUInt16BigEndian(chunk[20..]);
        ushort delayDenominator = BinaryPrimitives.ReadUInt16BigEndian(chunk[22..]);
        byte disposeOp = chunk[24];
        byte blendOp = chunk[25];

        // All four rectangle fields are uint32 on the wire; reject anything that cannot survive the cast to
        // int before it is used in arithmetic, then bound the rectangle against the canvas in long space.
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue
            || xOffset > int.MaxValue || yOffset > int.MaxValue
            || (long)xOffset + width > canvasWidth || (long)yOffset + height > canvasHeight)
        {
            throw new InvalidImageContentException("PNG animation frame does not fit inside the canvas.");
        }

        if (disposeOp > (byte)PngDisposalMethod.RestoreToPrevious || blendOp > (byte)PngBlendMethod.Over)
        {
            throw new InvalidImageContentException("PNG fcTL chunk has an invalid dispose or blend operation.");
        }

        return new ApngFrameControl
        {
            Sequence = sequence,
            Width = (int)width,
            Height = (int)height,
            XOffset = (int)xOffset,
            YOffset = (int)yOffset,
            DelayNumerator = delayNumerator,
            DelayDenominator = delayDenominator,
            Disposal = (PngDisposalMethod)disposeOp,
            Blend = (PngBlendMethod)blendOp,
        };
    }

    /// <summary>
    /// Reads the sequence number that prefixes an fdAT chunk. The frame's compressed data is the rest of the
    /// chunk, <see cref="FdatHeaderLength"/> bytes in.
    /// </summary>
    public static uint ParseFrameDataSequence(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < FdatHeaderLength)
        {
            throw new InvalidImageContentException("PNG fdAT chunk is too short to hold a sequence number.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(chunk);
    }

    // ----- Writing -----

    /// <summary>Writes an acTL chunk. <paramref name="numPlays"/> is 0 to loop forever.</summary>
    /// <param name="stream">The stream the chunk is written to.</param>
    /// <param name="numFrames">The number of animation frames the file contains.</param>
    /// <param name="numPlays">The number of times the animation plays; 0 loops forever.</param>
    public static void WriteAnimationControl(Stream stream, int numFrames, uint numPlays)
    {
        Span<byte> payload = stackalloc byte[ActlLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)numFrames);
        BinaryPrimitives.WriteUInt32BigEndian(payload[4..], numPlays);
        PngMetadataChunks.WriteChunk(stream, "acTL"u8, payload);
    }

    /// <summary>
    /// Writes an fcTL chunk for the frame occupying <paramref name="rectangle"/>. The delay is a fraction of
    /// a second; a zero denominator is read back as 1/100 s, so a caller that means hundredths may write 0.
    /// </summary>
    /// <param name="stream">The stream the chunk is written to.</param>
    /// <param name="sequence">The next number in the series shared by fcTL and fdAT chunks.</param>
    /// <param name="rectangle">The frame's rectangle on the canvas.</param>
    /// <param name="delayNumerator">The numerator of the frame delay, in seconds.</param>
    /// <param name="delayDenominator">The denominator of the frame delay; 0 means 100.</param>
    /// <param name="dispose">What happens to the rectangle once the frame has been shown.</param>
    /// <param name="blend">How the frame's pixels are combined with the canvas underneath.</param>
    public static void WriteFrameControl(
        Stream stream,
        uint sequence,
        in Rectangle rectangle,
        ushort delayNumerator,
        ushort delayDenominator,
        PngDisposalMethod dispose,
        PngBlendMethod blend)
    {
        Span<byte> payload = stackalloc byte[FctlLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(payload[4..], (uint)rectangle.Width);
        BinaryPrimitives.WriteUInt32BigEndian(payload[8..], (uint)rectangle.Height);
        BinaryPrimitives.WriteUInt32BigEndian(payload[12..], (uint)rectangle.X);
        BinaryPrimitives.WriteUInt32BigEndian(payload[16..], (uint)rectangle.Y);
        BinaryPrimitives.WriteUInt16BigEndian(payload[20..], delayNumerator);
        BinaryPrimitives.WriteUInt16BigEndian(payload[22..], delayDenominator);
        payload[24] = (byte)dispose;
        payload[25] = (byte)blend;
        PngMetadataChunks.WriteChunk(stream, "fcTL"u8, payload);
    }

    /// <summary>
    /// Writes an fdAT chunk: the frame's sequence number followed by a complete zlib stream of filtered
    /// scanlines. The CRC is accumulated across the three pieces so the payload is never copied.
    /// </summary>
    public static void WriteFrameData(Stream stream, uint sequence, ReadOnlySpan<byte> deflated)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(scratch, deflated.Length + FdatHeaderLength);
        stream.Write(scratch);
        stream.Write("fdAT"u8);

        Span<byte> sequenceBytes = stackalloc byte[FdatHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(sequenceBytes, sequence);
        stream.Write(sequenceBytes);
        stream.Write(deflated);

        // Chained Append calls compose correctly: the entry XOR undoes the previous exit XOR.
        uint crc = Crc32.Append(Crc32.Append(Crc32.Append(0, "fdAT"u8), sequenceBytes), deflated);
        BinaryPrimitives.WriteUInt32BigEndian(scratch, crc);
        stream.Write(scratch);
    }

    // ----- Compositing -----

    /// <summary>
    /// Draws a frame's sub-rectangle onto the canvas, either overwriting it (APNG_BLEND_OP_SOURCE) or
    /// compositing it with source-over alpha (APNG_BLEND_OP_OVER). The rectangle is assumed to have been
    /// validated by <see cref="ParseFrameControl"/>; <paramref name="source"/> holds
    /// <c>frame.Width * frame.Height</c> pixels in row-major order.
    /// </summary>
    /// <param name="canvas">The full-canvas buffer being composited into.</param>
    /// <param name="canvasWidth">The stride of <paramref name="canvas"/> in pixels.</param>
    /// <param name="source">The frame's own pixels, one rectangle's worth.</param>
    /// <param name="frame">The frame control describing where the rectangle sits.</param>
    /// <param name="blend">True to composite with source-over alpha, false to overwrite.</param>
    public static void Draw(Rgba32[] canvas, int canvasWidth, ReadOnlySpan<Rgba32> source, in ApngFrameControl frame, bool blend)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            int sourceStart = y * frame.Width;
            int canvasStart = ((frame.YOffset + y) * canvasWidth) + frame.XOffset;
            if (!blend)
            {
                source.Slice(sourceStart, frame.Width).CopyTo(canvas.AsSpan(canvasStart, frame.Width));
                continue;
            }

            for (int x = 0; x < frame.Width; x++)
            {
                canvas[canvasStart + x] = FrameOps.SourceOver(source[sourceStart + x], canvas[canvasStart + x]);
            }
        }
    }

    /// <summary>
    /// Applies a frame's disposal to its own rectangle once the frame has been shown.
    /// <see cref="PngDisposalMethod.RestoreToBackground"/> clears the rectangle to fully transparent black,
    /// which is what the format itself defines - unlike GIF and WebP, where clearing to transparent is a
    /// deliberate departure from the declared background colour.
    /// </summary>
    /// <param name="canvas">The full-canvas buffer being composited into.</param>
    /// <param name="canvasWidth">The stride of <paramref name="canvas"/> in pixels.</param>
    /// <param name="disposal">The disposal declared by the frame that has just been shown.</param>
    /// <param name="frame">The frame control describing the rectangle to dispose of.</param>
    /// <param name="saved">
    /// The rectangle-sized snapshot taken before the frame was drawn, read only for
    /// <see cref="PngDisposalMethod.RestoreToPrevious"/>.
    /// </param>
    public static void ApplyDisposal(
        Rgba32[] canvas, int canvasWidth, PngDisposalMethod disposal, in ApngFrameControl frame, Rgba32[]? saved)
    {
        switch (disposal)
        {
            case PngDisposalMethod.RestoreToBackground:
                for (int y = 0; y < frame.Height; y++)
                {
                    canvas.AsSpan(((frame.YOffset + y) * canvasWidth) + frame.XOffset, frame.Width).Clear();
                }

                break;

            case PngDisposalMethod.RestoreToPrevious when saved is not null:
                for (int y = 0; y < frame.Height; y++)
                {
                    saved.AsSpan(y * frame.Width, frame.Width)
                        .CopyTo(canvas.AsSpan(((frame.YOffset + y) * canvasWidth) + frame.XOffset, frame.Width));
                }

                break;

            case PngDisposalMethod.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Snapshots the canvas under a frame's rectangle so <see cref="PngDisposalMethod.RestoreToPrevious"/>
    /// can put it back. Only the rectangle is copied, never the whole canvas.
    /// </summary>
    public static Rgba32[] CopyRegion(Rgba32[] canvas, int canvasWidth, in ApngFrameControl frame)
    {
        var region = new Rgba32[frame.Width * frame.Height];
        for (int y = 0; y < frame.Height; y++)
        {
            canvas.AsSpan(((frame.YOffset + y) * canvasWidth) + frame.XOffset, frame.Width)
                .CopyTo(region.AsSpan(y * frame.Width, frame.Width));
        }

        return region;
    }
}

/// <summary>
/// One APNG frame control chunk (fcTL): where the frame goes, how long it is shown, and how it is combined
/// with and then removed from the canvas.
/// </summary>
internal readonly struct ApngFrameControl
{
    /// <summary>The frame's place in the single sequence number series shared by fcTL and fdAT chunks.</summary>
    public uint Sequence { get; init; }

    /// <summary>The width of the frame's rectangle, at most the canvas width.</summary>
    public int Width { get; init; }

    /// <summary>The height of the frame's rectangle, at most the canvas height.</summary>
    public int Height { get; init; }

    /// <summary>The left edge of the frame's rectangle on the canvas.</summary>
    public int XOffset { get; init; }

    /// <summary>The top edge of the frame's rectangle on the canvas.</summary>
    public int YOffset { get; init; }

    /// <summary>The numerator of the frame delay, in seconds.</summary>
    public ushort DelayNumerator { get; init; }

    /// <summary>The denominator of the frame delay; 0 is defined by the format to mean 100.</summary>
    public ushort DelayDenominator { get; init; }

    /// <summary>What happens to the frame's rectangle once the frame has been shown.</summary>
    public PngDisposalMethod Disposal { get; init; }

    /// <summary>How the frame's pixels are combined with the canvas underneath.</summary>
    public PngBlendMethod Blend { get; init; }

    /// <summary>
    /// The frame delay in seconds as a fraction, ready for <see cref="PngFrameMetadata.FrameDelay"/>. A
    /// denominator of 0 becomes 100 as the format requires, because a <see cref="Rational"/> with a zero
    /// denominator converts to NaN or infinity rather than to a duration.
    /// </summary>
    public Rational Delay => new(this.DelayNumerator, this.DelayDenominator == 0 ? 100u : this.DelayDenominator);
}
