using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Decodes PNG images: all color types (grayscale, truecolor, palette, with/without alpha),
/// bit depths 1/2/4/8/16, Adam7 interlacing, palette alpha and colour-key (tRNS) transparency.
/// 16-bit samples are kept at full width when the requested pixel format can hold more than 8 bits
/// per component (Rgb48, Rgba64, L16, La32, RgbaVector) and are otherwise reduced to 8 bits by
/// keeping the high byte; colour keys are matched on the full-precision sample values either way.
/// Animated PNG (APNG) files are returned fully composited, so <c>image.Frames[i]</c> is exactly what a
/// viewer would display for frame <c>i</c>; the loop count and whether the IDAT image takes part in the
/// animation are exposed through <see cref="PngMetadata"/>, and the per-frame delay, disposal and blend
/// operations through <see cref="PngFrameMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// An animation is composited in an 8-bit <see cref="Rgba32"/> canvas, so a 16-bit APNG is narrowed to its
/// high byte even when the requested pixel format could hold more - unlike a 16-bit <em>still</em> PNG,
/// which reaches <see cref="Rgba64"/> and friends intact. This matches the GIF and WebP decoders.
/// </para>
/// <para>
/// fcTL and fdAT chunks in a file that carries no acTL chunk are ignored and the file decodes as a still
/// PNG, which is what browsers do and what keeps a stray ancillary chunk from breaking a valid image.
/// A file that does declare an animation is decoded strictly: a damaged or inconsistent animation raises
/// <see cref="InvalidImageContentException"/> rather than returning the frames decoded so far, because
/// this decoder already rejects any truncated chunk and splitting that behaviour inside one codec would be
/// worse than the difference from <see cref="EasyImageSharp.Formats.Gif.GifDecoder"/>.
/// </para>
/// </remarks>
public sealed class PngDecoder : IImageDecoder
{
    // Adam7 pass layout.
    private static readonly int[] PassXStart = { 0, 4, 0, 2, 0, 1, 0 };
    private static readonly int[] PassYStart = { 0, 0, 4, 0, 2, 0, 1 };
    private static readonly int[] PassXStep = { 8, 8, 4, 4, 2, 2, 1 };
    private static readonly int[] PassYStep = { 8, 8, 8, 4, 4, 2, 2 };

    private const int MaxPaletteEntries = 256;

    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return DecodeCore<TPixel>(data, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("PNG", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        PngHeader header = default;
        Rgba32[]? palette = null;
        var idat = new List<(int Start, int Length)>();
        long idatLength = 0;
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Png };
        PngMetadata pngMetadata = metadata.GetPngMetadata();

        // ----- Animation state, only populated once an acTL chunk has declared the file animated -----
        bool isAnimated = false;
        int declaredFrames = 0;
        uint sequence = 0;
        ApngFrameControl? rootFrameControl = null;
        var frameControls = new List<ApngFrameControl>();
        var frameData = new List<List<(int Start, int Length)>>();

        // ----- Chunk parsing -----
        int pos = 8; // Skip signature (already validated by the format detector).
        bool sawHeader = false;
        bool sawIdat = false;
        while (pos + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
            pos += 8;
            if (length < 0 || (long)pos + length + 4 > data.Length)
            {
                throw new InvalidImageContentException("PNG chunk is truncated.");
            }

            ReadOnlySpan<byte> chunk = data.Slice(pos, length);
            if (!sawHeader && type != 0x49484452u)
            {
                throw new InvalidImageContentException("PNG file does not start with an IHDR chunk.");
            }

            switch (type)
            {
                case 0x49484452u: // IHDR
                    if (sawHeader)
                    {
                        throw new InvalidImageContentException("PNG contains more than one IHDR chunk.");
                    }

                    header = ParseHeader(chunk, strictLength: true);
                    options.EnsureFrameWithinLimits(header.Width, header.Height, "PNG");
                    sawHeader = true;
                    pngMetadata.ColorType = (PngColorType)header.ColorType;
                    pngMetadata.BitDepth = (PngBitDepth)header.BitDepth;
                    pngMetadata.Interlaced = header.Interlaced;
                    break;
                case 0x504C5445u: // PLTE
                    if (header.ColorType != 3)
                    {
                        break; // A suggested palette for truecolor images (or a stray one) is not consumed and therefore not validated.
                    }

                    if (length == 0 || length % 3 != 0 || length / 3 > MaxPaletteEntries || palette is not null || idatLength > 0)
                    {
                        throw new InvalidImageContentException("PNG PLTE chunk is invalid or misplaced.");
                    }

                    palette = new Rgba32[length / 3];
                    for (int i = 0; i < palette.Length; i++)
                    {
                        palette[i] = new Rgba32(chunk[i * 3], chunk[(i * 3) + 1], chunk[(i * 3) + 2]);
                    }

                    break;
                case 0x74524E53u: // tRNS
                    ParseTransparency(chunk, ref header, palette);
                    break;
                case 0x49444154u: // IDAT
                    idat.Add((pos, length));
                    idatLength += length;
                    sawIdat = true;
                    break;
                case PngAnimation.ActlType: // acTL
                {
                    if (isAnimated || sawIdat)
                    {
                        throw new InvalidImageContentException("PNG acTL chunk must appear once, before the image data.");
                    }

                    (declaredFrames, uint plays) = PngAnimation.ParseAnimationControl(chunk);
                    isAnimated = true;
                    pngMetadata.IsAnimated = true;
                    pngMetadata.RepeatCount = plays;
                    break;
                }

                case PngAnimation.FctlType when isAnimated: // fcTL
                {
                    ExpectSequence(ref sequence, chunk);
                    ApngFrameControl control = PngAnimation.ParseFrameControl(chunk, header.Width, header.Height);
                    if (!sawIdat)
                    {
                        // The fcTL before IDAT makes the IDAT image the animation's first frame, so it must
                        // describe the whole canvas: the frame it introduces is the one IHDR already sized.
                        if (rootFrameControl is not null)
                        {
                            throw new InvalidImageContentException(
                                "PNG contains more than one frame control chunk before the image data.");
                        }

                        if (control.Width != header.Width || control.Height != header.Height
                            || control.XOffset != 0 || control.YOffset != 0)
                        {
                            throw new InvalidImageContentException("PNG first frame control must cover the whole canvas.");
                        }

                        rootFrameControl = control;
                        break;
                    }

                    if (frameData.Count > 0 && frameData[^1].Count == 0)
                    {
                        throw new InvalidImageContentException("PNG animation frame has no image data.");
                    }

                    if (frameControls.Count + (rootFrameControl is null ? 0 : 1) >= declaredFrames)
                    {
                        throw new InvalidImageContentException(
                            $"PNG acTL declares {declaredFrames:N0} frames but the file contains more.");
                    }

                    frameControls.Add(control);
                    frameData.Add(new List<(int Start, int Length)>());
                    break;
                }

                case PngAnimation.FdatType when isAnimated: // fdAT
                    if (length < PngAnimation.FdatHeaderLength)
                    {
                        throw new InvalidImageContentException("PNG fdAT chunk is too short.");
                    }

                    ExpectSequence(ref sequence, chunk);
                    if (frameData.Count == 0)
                    {
                        throw new InvalidImageContentException("PNG fdAT chunk is not preceded by a frame control chunk.");
                    }

                    frameData[^1].Add((pos + PngAnimation.FdatHeaderLength, length - PngAnimation.FdatHeaderLength));
                    break;
                case 0x49454E44u: // IEND
                    pos = data.Length;
                    continue;
                default:
                    PngMetadataChunks.TryReadChunk(type, chunk, metadata, pngMetadata);
                    break;
            }

            pos += length + 4; // Skip data + CRC.
        }

        if (!sawHeader || idatLength == 0)
        {
            throw new InvalidImageContentException("PNG image is missing its IHDR or IDAT chunks.");
        }

        PngMetadataChunks.Finish(metadata);

        if (header.ColorType == 3 && palette is null)
        {
            throw new InvalidImageContentException("Palette-based PNG is missing its PLTE chunk.");
        }

        if (isAnimated)
        {
            return DecodeAnimation<TPixel>(
                data, in header, palette, options, metadata, pngMetadata,
                rootFrameControl, frameControls, frameData, idat, declaredFrames);
        }

        // ----- Inflate and convert, one scanline at a time -----
        if (idatLength > int.MaxValue)
        {
            throw new InvalidImageContentException("PNG compressed data is too large.");
        }

        // Every pixel is written exactly once (Adam7 passes partition the image), so the buffer does not
        // need clearing first.
        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(header.Width, header.Height);
        TPixel[]? paletteLut = palette is null ? null : BuildPaletteLut<TPixel>(palette);

        // Trailing bytes after the end of the zlib stream are surplus input, not an error, so the still image
        // does not ask where its compressed data ended.
        var reader = new PngIdatReader(data, idat, 1 + MaxBytesPerRow(in header), verifyStreamEnd: false);
        try
        {
            ReadImage(ref reader, in header, palette, paletteLut, frame);
        }
        finally
        {
            reader.Dispose();
        }

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, metadata);
    }

    /// <summary>
    /// Inflates one image's filtered scanlines out of <paramref name="reader"/> and converts them into
    /// <paramref name="frame"/>, honouring the header's colour type, bit depth and Adam7 interlacing.
    /// The frame must have the header's dimensions; every pixel of it is written, so it may be
    /// uninitialized on entry.
    /// </summary>
    /// <param name="reader">The image's compressed data, already positioned at its first scanline.</param>
    /// <param name="header">The header describing the scanlines' layout and the frame's dimensions.</param>
    /// <param name="palette">The PLTE palette, already carrying any tRNS alpha, or <see langword="null"/>.</param>
    /// <param name="paletteLut">The same palette as the destination pixel format, or <see langword="null"/>.</param>
    /// <param name="frame">The frame the converted pixels are written into.</param>
    /// <typeparam name="TPixel">The pixel format of <paramref name="frame"/>.</typeparam>
    private static void ReadImage<TPixel>(
        ref PngIdatReader reader, in PngHeader header, Rgba32[]? palette, TPixel[]? paletteLut,
        ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        long expectedSize = ComputeInflatedSize(header);

        // 16-bit samples only survive intact when the requested pixel format is wide enough to hold
        // them; every other combination keeps the 8-bit path below unchanged.
        bool wideSamples = header.BitDepth == 16 && PixelOps.IsHighPrecision<TPixel>();
        Rgba64[]? wideRow = wideSamples ? ArrayPool<Rgba64>.Shared.Rent(header.Width) : null;

        Rgba32[] rgbaBuffer = ArrayPool<Rgba32>.Shared.Rent(header.Width);
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(MaxBytesPerRow(header));
        byte[] previousBuffer = ArrayPool<byte>.Shared.Rent(rowBuffer.Length);
        try
        {
            int passCount = header.Interlaced ? 7 : 1;
            for (int pass = 0; pass < passCount; pass++)
            {
                (int xStart, int yStart, int xStep, int yStep) = header.Interlaced
                    ? (PassXStart[pass], PassYStart[pass], PassXStep[pass], PassYStep[pass])
                    : (0, 0, 1, 1);

                int passWidth = (header.Width - xStart + xStep - 1) / xStep;
                int passHeight = (header.Height - yStart + yStep - 1) / yStep;
                if (passWidth <= 0 || passHeight <= 0)
                {
                    continue;
                }

                int bitsPerPixel = header.BitDepth * header.Channels;
                int bytesPerRow = ((passWidth * bitsPerPixel) + 7) / 8;
                int filterBpp = (bitsPerPixel + 7) / 8;

                byte[] current = rowBuffer;
                byte[] previous = previousBuffer;
                for (int r = 0; r < passHeight; r++)
                {
                    Span<byte> row = current.AsSpan(0, bytesPerRow);
                    byte filterType = reader.ReadFilterType();

                    // The reader either lends the scanline out of its own window or fills the row buffer with
                    // it; unfiltering handles both, because it reads a source byte before writing the
                    // destination byte at the same index and so tolerates the two spans being one.
                    ReadOnlySpan<byte> filtered = reader.ReadRow(row);
                    PngFilters.Unfilter(
                        filterType, filtered, row, r == 0 ? default : previous.AsSpan(0, bytesPerRow), filterBpp);

                    int y = yStart + (r * yStep);
                    if (wideSamples)
                    {
                        Span<Rgba64> wide = wideRow!.AsSpan(0, passWidth);
                        ConvertScanline16(row, wide, passWidth, header);
                        if (xStep == 1)
                        {
                            PixelOps.Convert<Rgba64, TPixel>(wide, frame.GetRowSpan(y).Slice(xStart, passWidth));
                        }
                        else
                        {
                            for (int i = 0; i < passWidth; i++)
                            {
                                frame[xStart + (i * xStep), y] = TPixel.FromScaledVector4(wide[i].ToScaledVector4());
                            }
                        }
                    }
                    else if (xStep == 1)
                    {
                        Span<TPixel> destination = frame.GetRowSpan(y).Slice(xStart, passWidth);
                        if (!TryConvertRowDirect(row, passWidth, header, palette, paletteLut, destination))
                        {
                            Span<Rgba32> rgbaRow = rgbaBuffer.AsSpan(0, passWidth);
                            ConvertScanline(row, rgbaRow, passWidth, header, palette);
                            PixelOps.FromRgba32<TPixel>(rgbaRow, destination);
                        }
                    }
                    else
                    {
                        Span<Rgba32> rgbaRow = rgbaBuffer.AsSpan(0, passWidth);
                        ConvertScanline(row, rgbaRow, passWidth, header, palette);
                        for (int i = 0; i < passWidth; i++)
                        {
                            frame[xStart + (i * xStep), y] = TPixel.FromRgba32(rgbaRow[i]);
                        }
                    }

                    (previous, current) = (current, previous);
                }
            }

            // The zlib stream must contain exactly the filtered scanlines; trailing decompressed data means
            // the IHDR and IDAT chunks disagree about the image layout.
            if (expectedSize >= 0 && reader.ProbeSurplus())
            {
                throw new InvalidImageContentException("PNG pixel data is longer than the image dimensions allow.");
            }
        }
        finally
        {
            if (wideRow is not null)
            {
                ArrayPool<Rgba64>.Shared.Return(wideRow);
            }

            ArrayPool<byte>.Shared.Return(previousBuffer);
            ArrayPool<byte>.Shared.Return(rowBuffer);
            ArrayPool<Rgba32>.Shared.Return(rgbaBuffer);
        }
    }

    /// <summary>
    /// Reads the leading sequence number of an fcTL or fdAT chunk and advances the expected value. The two
    /// chunk kinds share one series that must run 0, 1, 2, ... with no gap, repeat or reorder; the numbers
    /// legally reach <see cref="uint.MaxValue"/>, so they are compared for equality and never subtracted.
    /// </summary>
    private static void ExpectSequence(ref uint next, ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < PngAnimation.FdatHeaderLength)
        {
            throw new InvalidImageContentException("PNG animation chunk is too short to hold a sequence number.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(chunk) != next)
        {
            throw new InvalidImageContentException("PNG animation chunk has an out-of-order sequence number.");
        }

        next++;
    }

    /// <summary>
    /// Decodes an APNG: every frame is inflated into a pooled sub-rectangle, composited onto a persistent
    /// canvas honouring the previous frame's disposal and this frame's blend operation, and the whole canvas
    /// is then snapshotted as one output frame - so the caller sees exactly what a viewer would display.
    /// </summary>
    /// <param name="data">The whole file, which the frames' chunk slices index into.</param>
    /// <param name="header">The IHDR header; the canvas size and every frame's pixel layout come from it.</param>
    /// <param name="palette">The PLTE palette, already carrying any tRNS alpha, or <see langword="null"/>.</param>
    /// <param name="options">The decoder limits; frames beyond <see cref="DecoderOptions.MaxFrames"/> are skipped.</param>
    /// <param name="metadata">The image metadata the decoded image is built with.</param>
    /// <param name="pngMetadata">The PNG container inside <paramref name="metadata"/>.</param>
    /// <param name="rootFrameControl">The fcTL seen before IDAT, or <see langword="null"/> when the IDAT image sits outside the animation.</param>
    /// <param name="frameControls">The fcTL chunks that follow IDAT, in file order.</param>
    /// <param name="frameData">Each of those frames' fdAT payload slices, without their sequence numbers.</param>
    /// <param name="idat">The IDAT slices, which are the root frame's data when it is part of the animation.</param>
    /// <param name="declaredFrames">The frame count the acTL chunk declares, which the file must match exactly.</param>
    /// <typeparam name="TPixel">The pixel format of the returned image.</typeparam>
    private static Image<TPixel> DecodeAnimation<TPixel>(
        ReadOnlySpan<byte> data,
        in PngHeader header,
        Rgba32[]? palette,
        DecoderOptions options,
        ImageMetadata metadata,
        PngMetadata pngMetadata,
        ApngFrameControl? rootFrameControl,
        List<ApngFrameControl> frameControls,
        List<List<(int Start, int Length)>> frameData,
        List<(int Start, int Length)> idat,
        int declaredFrames)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        // The IDAT image is the first animation frame only when an fcTL introduced it; otherwise it is a
        // still fallback that an APNG-aware viewer never shows.
        bool rootIsFirstFrame = rootFrameControl is not null;
        pngMetadata.AnimateRootFrame = rootIsFirstFrame;

        int total = frameControls.Count + (rootIsFirstFrame ? 1 : 0);
        if (total != declaredFrames)
        {
            throw new InvalidImageContentException(
                $"PNG acTL declares {declaredFrames:N0} frames but the file contains {total:N0}.");
        }

        if (frameData.Count > 0 && frameData[^1].Count == 0)
        {
            throw new InvalidImageContentException("PNG animation frame has no image data.");
        }

        // The acTL frame count is attacker-controlled, so the two lists are built from what was actually
        // found rather than sized from it.
        var controls = new List<ApngFrameControl>(frameControls.Count + 1);
        var sources = new List<List<(int Start, int Length)>>(frameData.Count + 1);
        if (rootIsFirstFrame)
        {
            controls.Add(rootFrameControl!.Value);
            sources.Add(idat);
        }

        controls.AddRange(frameControls);
        sources.AddRange(frameData);

        int canvasWidth = header.Width;
        var canvas = new Rgba32[canvasWidth * header.Height];
        Rgba32[]? paletteLut = palette is null ? null : BuildPaletteLut<Rgba32>(palette);

        // One scratch rectangle serves every frame: each one is at most the canvas, which IHDR already
        // checked against the size limit.
        int scratchPixels = 0;
        foreach (ApngFrameControl control in controls)
        {
            scratchPixels = Math.Max(scratchPixels, control.Width * control.Height);
        }

        int frameCount = Math.Min(total, options.MaxFrames);
        var frames = new List<ImageFrame<TPixel>>(frameCount);
        Rgba32[] scratch = ArrayPool<Rgba32>.Shared.Rent(scratchPixels);
        try
        {
            ApngFrameControl previous = default;
            bool hasPrevious = false;
            Rgba32[]? saved = null;
            for (int i = 0; i < frameCount; i++)
            {
                ApngFrameControl control = controls[i];
                options.EnsureFrameWithinLimits(control.Width, control.Height, "PNG");
                ReadFrame(
                    data, sources[i], in header, in control, palette, paletteLut, scratch,
                    verifyStreamEnd: !(rootIsFirstFrame && i == 0));

                // 1. Dispose of the previous frame, 2. remember what this one covers if it must be undone,
                // 3. draw it, 4. snapshot the whole canvas - the order GIF and WebP already use.
                if (hasPrevious)
                {
                    PngAnimation.ApplyDisposal(canvas, canvasWidth, previous.Disposal, in previous, saved);
                }

                saved = control.Disposal == PngDisposalMethod.RestoreToPrevious
                    ? PngAnimation.CopyRegion(canvas, canvasWidth, in control)
                    : null;

                // No first-frame special case: source-over onto a fully transparent canvas returns the
                // source unchanged, so APNG_BLEND_OP_OVER and APNG_BLEND_OP_SOURCE agree there.
                PngAnimation.Draw(
                    canvas, canvasWidth, scratch.AsSpan(0, control.Width * control.Height), in control,
                    control.Blend == PngBlendMethod.Over);

                var output = new ImageFrame<TPixel>(canvasWidth, header.Height);
                PixelOps.FromRgba32<TPixel>(canvas, output.PixelSpan);
                PngFrameMetadata frameMetadata = output.Metadata.GetPngMetadata();
                frameMetadata.FrameDelay = control.Delay;
                frameMetadata.DisposalMethod = control.Disposal;
                frameMetadata.BlendMethod = control.Blend;
                frames.Add(output);

                previous = control;
                hasPrevious = true;
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(scratch);
        }

        return new Image<TPixel>(frames, metadata);
    }

    /// <summary>
    /// Inflates one animation frame's rectangle into <paramref name="destination"/>. The frame's compressed
    /// data is the concatenation of its chunk slices - the IDAT chunks for the root frame, the fdAT payloads
    /// (past their sequence numbers) for every other - and forms one complete zlib stream of filtered
    /// scanlines for a sub-image the size of the frame's rectangle.
    /// </summary>
    /// <param name="data">The whole file, which <paramref name="slices"/> indexes into.</param>
    /// <param name="slices">The frame's compressed data, in file order.</param>
    /// <param name="header">The IHDR header, whose colour type and bit depth every frame shares.</param>
    /// <param name="control">The frame control supplying the rectangle's size.</param>
    /// <param name="palette">The PLTE palette, or <see langword="null"/>.</param>
    /// <param name="paletteLut">The same palette as the canvas pixel format, or <see langword="null"/>.</param>
    /// <param name="destination">The pooled scratch buffer the rectangle is written into.</param>
    /// <param name="verifyStreamEnd">
    /// True to require the compressed data to end exactly where its zlib stream does, which is what rejects
    /// an fdAT chunk carrying a second stream after a frame is already complete. IDAT keeps the still
    /// image's leniency about trailing bytes.
    /// </param>
    private static void ReadFrame(
        ReadOnlySpan<byte> data,
        List<(int Start, int Length)> slices,
        in PngHeader header,
        in ApngFrameControl control,
        Rgba32[]? palette,
        Rgba32[]? paletteLut,
        Rgba32[] destination,
        bool verifyStreamEnd)
    {
        long compressedLength = 0;
        foreach ((int _, int length) in slices)
        {
            compressedLength += length;
        }

        if (compressedLength == 0)
        {
            throw new InvalidImageContentException("PNG animation frame has no image data.");
        }

        if (compressedLength > int.MaxValue)
        {
            throw new InvalidImageContentException("PNG compressed data is too large.");
        }

        PngHeader frameHeader = header;
        frameHeader.Width = control.Width;
        frameHeader.Height = control.Height;

        var reader = new PngIdatReader(data, slices, 1 + MaxBytesPerRow(in frameHeader), verifyStreamEnd);
        try
        {
            var frame = new ImageFrame<Rgba32>(
                control.Width, control.Height, destination.AsMemory(0, control.Width * control.Height), null);
            ReadImage(ref reader, in frameHeader, palette, paletteLut, frame);
            if (verifyStreamEnd && !reader.EndedAtStreamEnd)
            {
                throw new InvalidImageContentException(
                    "PNG animation frame carries data past the end of its compressed stream.");
            }
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Longest filtered scanline any pass of this image can produce.</summary>
    private static int MaxBytesPerRow(in PngHeader header)
    {
        int bitsPerPixel = header.BitDepth * header.Channels;
        return (int)((((long)header.Width * bitsPerPixel) + 7) / 8);
    }

    /// <summary>The palette as the destination pixel format, so a palette row is one table lookup per pixel.</summary>
    private static TPixel[] BuildPaletteLut<TPixel>(Rgba32[] palette)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var lut = new TPixel[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            lut[i] = TPixel.FromRgba32(palette[i]);
        }

        return lut;
    }

    /// <summary>
    /// Writes a scanline straight into the destination row when the file's byte layout is one of the
    /// built-in pixel formats, which turns the conversion into a bulk copy, shuffle or table lookup instead
    /// of a per-pixel round trip through <see cref="Rgba32"/>. Returns false when the layout needs the
    /// general path (sub-byte depths, 16-bit samples, colour keys).
    /// </summary>
    private static bool TryConvertRowDirect<TPixel>(
        ReadOnlySpan<byte> row, int pixelCount, in PngHeader header, Rgba32[]? palette, TPixel[]? paletteLut, Span<TPixel> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (header.BitDepth != 8)
        {
            return false;
        }

        switch (header.ColorType)
        {
            case 0 when !header.HasColorKey:
                PixelOps.Convert<L8, TPixel>(MemoryMarshal.Cast<byte, L8>(row[..pixelCount]), destination);
                return true;

            case 2 when !header.HasColorKey:
                PixelOps.Convert<Rgb24, TPixel>(MemoryMarshal.Cast<byte, Rgb24>(row[..(pixelCount * 3)]), destination);
                return true;

            case 6:
                PixelOps.Convert<Rgba32, TPixel>(MemoryMarshal.Cast<byte, Rgba32>(row[..(pixelCount * 4)]), destination);
                return true;

            case 3 when paletteLut is not null:
            {
                int entries = palette!.Length;
                for (int x = 0; x < pixelCount; x++)
                {
                    int index = row[x];
                    if (index >= entries)
                    {
                        throw new InvalidImageContentException("PNG palette index out of range.");
                    }

                    destination[x] = paletteLut[index];
                }

                return true;
            }

            default:
                return false;
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return IdentifyCore(data);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("PNG", ex);
        }
    }

    private static ImageInfo IdentifyCore(ReadOnlySpan<byte> data)
    {
        // Walks the chunk table (without inflating IDAT) to read the header and the metadata chunks.
        PngHeader header = default;
        bool sawHeader = false;
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Png };
        PngMetadata pngMetadata = metadata.GetPngMetadata();

        // Animation facts are read the same way the strict walker reads them, but leniently: an acTL this
        // walker cannot make sense of leaves the file reported as the still image it otherwise is.
        bool isAnimated = false;
        bool sawIdat = false;
        bool sawRootFrameControl = false;
        int frameCount = 1;

        long pos = 8;
        while (pos + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[(int)pos..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(int)(pos + 4)..]);
            if (length < 0)
            {
                throw new InvalidImageContentException("PNG chunk has an invalid length.");
            }

            if (!sawHeader)
            {
                if (type == 0x49484452u)
                {
                    int available = (int)Math.Min(length, data.Length - pos - 8);
                    header = ParseHeader(data.Slice((int)pos + 8, available), strictLength: false);
                    sawHeader = true;
                    pngMetadata.ColorType = (PngColorType)header.ColorType;
                    pngMetadata.BitDepth = (PngBitDepth)header.BitDepth;
                    pngMetadata.Interlaced = header.Interlaced;
                }

                // Only IHDR may precede the header, so any other chunk here means the file is malformed;
                // still advance so a stray chunk cannot stall the scan.
                pos += 12L + length;
                continue;
            }

            if (type == 0x49454E44u || pos + 8 + length > data.Length)
            {
                break; // IEND, or a truncated chunk: header facts are complete.
            }

            switch (type)
            {
                case 0x49444154u: // IDAT
                    sawIdat = true;
                    break;
                case PngAnimation.ActlType: // acTL
                {
                    ReadOnlySpan<byte> chunk = data.Slice((int)pos + 8, length);
                    if (isAnimated || sawIdat || chunk.Length != PngAnimation.ActlLength)
                    {
                        break;
                    }

                    uint frames = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    isAnimated = true;
                    pngMetadata.IsAnimated = true;
                    pngMetadata.RepeatCount = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                    if (frames is not 0 and <= int.MaxValue)
                    {
                        // Deliberately not clamped by MaxFrames: Identify reports what the file declares.
                        frameCount = (int)frames;
                    }

                    break;
                }

                case PngAnimation.FctlType: // fcTL
                    sawRootFrameControl |= !sawIdat;
                    break;
                default:
                    PngMetadataChunks.TryReadChunk(type, data.Slice((int)pos + 8, length), metadata, pngMetadata);
                    break;
            }

            pos += 12L + length;
        }

        if (!sawHeader)
        {
            throw new InvalidImageContentException("PNG image is missing its IHDR chunk.");
        }

        if (isAnimated)
        {
            pngMetadata.AnimateRootFrame = sawRootFrameControl;
        }

        PngMetadataChunks.Finish(metadata);
        return new ImageInfo(
            header.Width, header.Height, header.BitDepth * header.Channels, frameCount, ImageFormat.Png, metadata);
    }

    private static PngHeader ParseHeader(ReadOnlySpan<byte> chunk, bool strictLength)
    {
        if (chunk.Length < 13 || (strictLength && chunk.Length != 13))
        {
            throw new InvalidImageContentException("PNG IHDR chunk has an invalid length.");
        }

        var header = new PngHeader
        {
            Width = BinaryPrimitives.ReadInt32BigEndian(chunk),
            Height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]),
            BitDepth = chunk[8],
            ColorType = chunk[9],
            Interlaced = chunk[12] == 1,
        };

        if (header.Width <= 0 || header.Height <= 0)
        {
            throw new InvalidImageContentException("Invalid PNG dimensions.");
        }

        if (chunk[10] != 0 || chunk[11] != 0 || chunk[12] > 1)
        {
            throw new InvalidImageContentException("Unsupported PNG compression, filter or interlace method.");
        }

        header.Channels = header.ColorType switch
        {
            0 => 1, // Grayscale
            2 => 3, // Truecolor
            3 => 1, // Palette
            4 => 2, // Grayscale + alpha
            6 => 4, // Truecolor + alpha
            _ => throw new InvalidImageContentException($"Invalid PNG color type: {header.ColorType}."),
        };

        bool validDepth = header.ColorType switch
        {
            0 => header.BitDepth is 1 or 2 or 4 or 8 or 16,
            3 => header.BitDepth is 1 or 2 or 4 or 8,
            _ => header.BitDepth is 8 or 16,
        };
        if (!validDepth)
        {
            throw new InvalidImageContentException($"Invalid PNG bit depth {header.BitDepth} for color type {header.ColorType}.");
        }

        return header;
    }

    private static int ComputeInflatedSize(in PngHeader header)
    {
        int bitsPerPixel = header.BitDepth * header.Channels;
        long total = 0;
        if (!header.Interlaced)
        {
            total = (1 + (((long)header.Width * bitsPerPixel + 7) / 8)) * header.Height;
        }
        else
        {
            for (int pass = 0; pass < 7; pass++)
            {
                long passWidth = (header.Width - PassXStart[pass] + PassXStep[pass] - 1) / PassXStep[pass];
                long passHeight = (header.Height - PassYStart[pass] + PassYStep[pass] - 1) / PassYStep[pass];
                if (passWidth > 0 && passHeight > 0)
                {
                    total += (1 + ((passWidth * bitsPerPixel + 7) / 8)) * passHeight;
                }
            }
        }

        return total <= int.MaxValue
            ? (int)total
            : throw new InvalidImageContentException("PNG image is too large to decode.");
    }

    /// <summary>Applies a tRNS chunk: palette alpha for colour type 3, a colour key for types 0 and 2.</summary>
    private static void ParseTransparency(ReadOnlySpan<byte> chunk, ref PngHeader header, Rgba32[]? palette)
    {
        switch (header.ColorType)
        {
            case 0:
                if (chunk.Length != 2)
                {
                    throw new InvalidImageContentException("PNG tRNS chunk has an invalid length for a grayscale image.");
                }

                header.HasColorKey = true;
                header.KeyR = BinaryPrimitives.ReadUInt16BigEndian(chunk);
                break;
            case 2:
                if (chunk.Length != 6)
                {
                    throw new InvalidImageContentException("PNG tRNS chunk has an invalid length for a truecolor image.");
                }

                header.HasColorKey = true;
                header.KeyR = BinaryPrimitives.ReadUInt16BigEndian(chunk);
                header.KeyG = BinaryPrimitives.ReadUInt16BigEndian(chunk[2..]);
                header.KeyB = BinaryPrimitives.ReadUInt16BigEndian(chunk[4..]);
                break;
            case 3:
                if (palette is null || chunk.Length > palette.Length)
                {
                    throw new InvalidImageContentException("PNG tRNS chunk must follow PLTE and may not exceed its entry count.");
                }

                for (int i = 0; i < chunk.Length; i++)
                {
                    palette[i].A = chunk[i];
                }

                break;
            default:
                // tRNS is meaningless for colour types that carry their own alpha channel; ignore it like libpng does.
                break;
        }
    }

    internal static int PaethPredictor(int a, int b, int c) => PngFilters.Paeth(a, b, c);

    private static void ConvertScanline(
        ReadOnlySpan<byte> row, Span<Rgba32> dest, int pixelCount, in PngHeader header, Rgba32[]? palette)
    {
        int depth = header.BitDepth;
        bool hasKey = header.HasColorKey;
        int keyR = header.KeyR;
        switch (header.ColorType)
        {
            case 0: // Grayscale
                if (depth == 8)
                {
                    for (int x = 0; x < pixelCount; x++)
                    {
                        byte v = row[x];
                        dest[x] = new Rgba32(v, v, v, hasKey && v == keyR ? (byte)0 : (byte)255);
                    }
                }
                else if (depth == 16)
                {
                    for (int x = 0; x < pixelCount; x++)
                    {
                        int sample = (row[x * 2] << 8) | row[(x * 2) + 1];
                        byte v = row[x * 2];
                        dest[x] = new Rgba32(v, v, v, hasKey && sample == keyR ? (byte)0 : (byte)255);
                    }
                }
                else
                {
                    int scale = 255 / ((1 << depth) - 1);
                    for (int x = 0; x < pixelCount; x++)
                    {
                        int sample = ReadSubByteSample(row, x, depth);
                        byte v = (byte)(sample * scale);
                        dest[x] = new Rgba32(v, v, v, hasKey && sample == keyR ? (byte)0 : (byte)255);
                    }
                }

                break;

            case 2: // Truecolor
            {
                int keyG = header.KeyG;
                int keyB = header.KeyB;
                if (depth == 16)
                {
                    for (int x = 0; x < pixelCount; x++)
                    {
                        int i = x * 6;
                        bool transparent = hasKey
                            && ((row[i] << 8) | row[i + 1]) == keyR
                            && ((row[i + 2] << 8) | row[i + 3]) == keyG
                            && ((row[i + 4] << 8) | row[i + 5]) == keyB;
                        dest[x] = new Rgba32(row[i], row[i + 2], row[i + 4], transparent ? (byte)0 : (byte)255);
                    }
                }
                else
                {
                    for (int x = 0; x < pixelCount; x++)
                    {
                        int i = x * 3;
                        bool transparent = hasKey && row[i] == keyR && row[i + 1] == keyG && row[i + 2] == keyB;
                        dest[x] = new Rgba32(row[i], row[i + 1], row[i + 2], transparent ? (byte)0 : (byte)255);
                    }
                }

                break;
            }

            case 3: // Palette
                for (int x = 0; x < pixelCount; x++)
                {
                    int index = depth == 8 ? row[x] : ReadSubByteSample(row, x, depth);
                    if (index >= palette!.Length)
                    {
                        throw new InvalidImageContentException("PNG palette index out of range.");
                    }

                    dest[x] = palette[index];
                }

                break;

            case 4: // Grayscale + alpha
            {
                int step = depth == 16 ? 4 : 2;
                int sampleStep = depth == 16 ? 2 : 1;
                for (int x = 0; x < pixelCount; x++)
                {
                    int i = x * step;
                    byte v = row[i];
                    dest[x] = new Rgba32(v, v, v, row[i + sampleStep]);
                }

                break;
            }

            case 6: // Truecolor + alpha
            {
                int step = depth == 16 ? 8 : 4;
                int sampleStep = depth == 16 ? 2 : 1;
                for (int x = 0; x < pixelCount; x++)
                {
                    int i = x * step;
                    dest[x] = new Rgba32(row[i], row[i + sampleStep], row[i + (2 * sampleStep)], row[i + (3 * sampleStep)]);
                }

                break;
            }
        }
    }

    /// <summary>
    /// The 16-bit-per-sample counterpart of <see cref="ConvertScanline"/>, used only when the caller
    /// asked for a pixel format that carries more than 8 bits per component. Keeping the samples at
    /// their full width here is what lets a 16-bit PNG reach an <see cref="Rgb48"/> or
    /// <see cref="Rgba64"/> image without being narrowed to 8 bits first. Palette images cannot use
    /// 16-bit samples, so only the colour types that can are handled.
    /// </summary>
    private static void ConvertScanline16(
        ReadOnlySpan<byte> row, Span<Rgba64> dest, int pixelCount, in PngHeader header)
    {
        bool hasKey = header.HasColorKey;
        switch (header.ColorType)
        {
            case 0: // Grayscale
                for (int x = 0; x < pixelCount; x++)
                {
                    ushort v = Sample16(row, x);
                    dest[x] = new Rgba64(v, v, v, hasKey && v == header.KeyR ? (ushort)0 : ushort.MaxValue);
                }

                break;

            case 2: // Truecolor
                for (int x = 0; x < pixelCount; x++)
                {
                    int i = x * 3;
                    ushort r = Sample16(row, i);
                    ushort g = Sample16(row, i + 1);
                    ushort b = Sample16(row, i + 2);
                    bool transparent = hasKey && r == header.KeyR && g == header.KeyG && b == header.KeyB;
                    dest[x] = new Rgba64(r, g, b, transparent ? (ushort)0 : ushort.MaxValue);
                }

                break;

            case 4: // Grayscale + alpha
                for (int x = 0; x < pixelCount; x++)
                {
                    int i = x * 2;
                    ushort v = Sample16(row, i);
                    dest[x] = new Rgba64(v, v, v, Sample16(row, i + 1));
                }

                break;

            case 6: // Truecolor + alpha
                for (int x = 0; x < pixelCount; x++)
                {
                    int i = x * 4;
                    dest[x] = new Rgba64(
                        Sample16(row, i), Sample16(row, i + 1), Sample16(row, i + 2), Sample16(row, i + 3));
                }

                break;

            default:
                throw new InvalidImageContentException(
                    $"PNG color type {header.ColorType} cannot carry 16-bit samples.");
        }
    }

    /// <summary>Reads the big-endian 16-bit sample at the given sample index of an unfiltered row.</summary>
    private static ushort Sample16(ReadOnlySpan<byte> row, int sampleIndex)
        => (ushort)((row[sampleIndex * 2] << 8) | row[(sampleIndex * 2) + 1]);

    private static int ReadSubByteSample(ReadOnlySpan<byte> row, int index, int depth)
    {
        int bitIndex = index * depth;
        return (row[bitIndex >> 3] >> (8 - depth - (bitIndex & 7))) & ((1 << depth) - 1);
    }

    private struct PngHeader
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public int ColorType;
        public int Channels;
        public bool Interlaced;

        /// <summary>True when a tRNS chunk supplied a colour key for colour type 0 or 2.</summary>
        public bool HasColorKey;

        /// <summary>Colour-key samples at the file's bit depth (only <see cref="KeyR"/> is used for grayscale).</summary>
        public int KeyR;
        public int KeyG;
        public int KeyB;
    }
}
