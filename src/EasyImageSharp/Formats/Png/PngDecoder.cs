using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
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
/// </summary>
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

        // ----- Chunk parsing -----
        int pos = 8; // Skip signature (already validated by the format detector).
        bool sawHeader = false;
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

        // ----- Inflate and convert, one scanline at a time -----
        if (idatLength > int.MaxValue)
        {
            throw new InvalidImageContentException("PNG compressed data is too large.");
        }

        byte[] compressed = ArrayPool<byte>.Shared.Rent((int)idatLength);
        int copied = 0;
        foreach ((int start, int length) in idat)
        {
            data.Slice(start, length).CopyTo(compressed.AsSpan(copied));
            copied += length;
        }

        // Every pixel is written exactly once (Adam7 passes partition the image), so the buffer does not
        // need clearing first.
        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(header.Width, header.Height);
        TPixel[]? paletteLut = palette is null ? null : BuildPaletteLut<TPixel>(palette);
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
            using var input = new MemoryStream(compressed, 0, copied, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);

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
                    byte filterType = ReadFilterType(zlib);
                    ReadExactly(zlib, row);
                    PngFilters.Unfilter(filterType, row, r == 0 ? default : previous.AsSpan(0, bytesPerRow), filterBpp);

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
            Span<byte> probe = stackalloc byte[1];
            if (expectedSize >= 0 && zlib.Read(probe) > 0)
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
            ArrayPool<byte>.Shared.Return(compressed);
        }

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, metadata);
    }

    /// <summary>Longest filtered scanline any pass of this image can produce.</summary>
    private static int MaxBytesPerRow(in PngHeader header)
    {
        int bitsPerPixel = header.BitDepth * header.Channels;
        return (int)((((long)header.Width * bitsPerPixel) + 7) / 8);
    }

    private static byte ReadFilterType(Stream stream)
    {
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw new InvalidImageContentException("PNG pixel data ended unexpectedly.");
        }

        return (byte)value;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int n = stream.Read(destination[read..]);
            if (n <= 0)
            {
                throw new InvalidImageContentException("PNG pixel data ended unexpectedly.");
            }

            read += n;
        }
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

            if (type != 0x49444154u)
            {
                PngMetadataChunks.TryReadChunk(type, data.Slice((int)pos + 8, length), metadata, pngMetadata);
            }

            pos += 12L + length;
        }

        if (!sawHeader)
        {
            throw new InvalidImageContentException("PNG image is missing its IHDR chunk.");
        }

        PngMetadataChunks.Finish(metadata);
        return new ImageInfo(header.Width, header.Height, header.BitDepth * header.Channels, 1, ImageFormat.Png, metadata);
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
