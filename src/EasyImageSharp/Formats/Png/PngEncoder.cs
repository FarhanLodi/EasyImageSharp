using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Encodes images as PNG. By default the colour type follows the pixel format (<see cref="L8"/> becomes 8-bit
/// grayscale, opaque RGB formats truecolor, everything else truecolor with alpha) at 8 bits per sample; set
/// <see cref="ColorType"/> and <see cref="BitDepth"/> to write palette images (quantized with
/// <see cref="Quantizer"/>), 1/2/4-bit grayscale, 16-bit samples, a fixed scanline filter or Adam7 interlacing.
/// </summary>
public sealed class PngEncoder : IImageEncoder
{
    // Adam7 pass geometry: (x start, y start, x step, y step).
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7Passes =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    /// <summary>The deflate effort used for the IDAT stream. Defaults to <see cref="CompressionLevel.Optimal"/>.</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>The colour type to write; <see langword="null"/> picks one from the pixel format.</summary>
    public PngColorType? ColorType { get; init; }

    /// <summary>
    /// Bits per sample; <see langword="null"/> writes 8 bits (or, for palette images, the smallest depth that
    /// holds the palette). Must be valid for the colour type, see <see cref="PngColorType"/>.
    /// </summary>
    public PngBitDepth? BitDepth { get; init; }

    /// <summary>The scanline filter strategy. Defaults to <see cref="PngFilterMethod.Adaptive"/>.</summary>
    public PngFilterMethod FilterMethod { get; init; } = PngFilterMethod.Adaptive;

    /// <summary>Whether to interlace. Defaults to <see cref="PngInterlaceMethod.None"/>.</summary>
    public PngInterlaceMethod InterlaceMethod { get; init; } = PngInterlaceMethod.None;

    /// <summary>
    /// The quantizer used for <see cref="PngColorType.Palette"/> output and for dithering grayscale images below
    /// 8 bits; <see langword="null"/> uses <see cref="KnownQuantizers.Wu"/> (256 colours, Floyd–Steinberg).
    /// </summary>
    public IQuantizer? Quantizer { get; init; }

    /// <summary>What to do with the colour of fully transparent pixels. Defaults to <see cref="PngTransparentColorMode.Preserve"/>.</summary>
    public PngTransparentColorMode TransparentColorMode { get; init; } = PngTransparentColorMode.Preserve;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        PngColorType colorType = this.ColorType ?? DefaultColorType<TPixel>();
        int bitDepth = this.BitDepth.HasValue ? (int)this.BitDepth.Value : 8;
        if (this.BitDepth.HasValue)
        {
            ValidateBitDepth(colorType, bitDepth);
        }

        // Palette and sub-byte grayscale go through a quantizer to obtain one index per pixel.
        byte[]? indices = null;
        Rgba32[]? palette = null;
        if (colorType == PngColorType.Palette)
        {
            (indices, palette) = this.QuantizeToPalette(frame, this.BitDepth.HasValue ? 1 << bitDepth : 256);
            if (!this.BitDepth.HasValue)
            {
                bitDepth = palette.Length <= 2 ? 1 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 4 : 8;
            }
        }
        else if (colorType == PngColorType.Grayscale && bitDepth < 8)
        {
            indices = this.QuantizeToGrayLevels(frame, bitDepth);
        }

        int channels = colorType switch
        {
            PngColorType.Grayscale or PngColorType.Palette => 1,
            PngColorType.GrayscaleWithAlpha => 2,
            PngColorType.Rgb => 3,
            _ => 4,
        };
        int bitsPerPixel = channels * bitDepth;
        int filterBpp = Math.Max(1, bitsPerPixel / 8);
        bool clearTransparent = this.TransparentColorMode == PngTransparentColorMode.Clear
            && colorType is PngColorType.RgbWithAlpha or PngColorType.GrayscaleWithAlpha;

        // Signature
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; // Compression method: deflate.
        ihdr[11] = 0; // Filter method: adaptive (per-scanline filter type bytes).
        ihdr[12] = (byte)this.InterlaceMethod;
        WriteChunk(stream, "IHDR"u8, ihdr);
        PngMetadataChunks.Write(stream, image.Metadata);

        // Metadata chunks (pHYs, tEXt, ...) belong here, directly after IHDR and before PLTE.

        if (palette is not null)
        {
            WritePaletteChunks(stream, palette);
        }

        // IDAT: filter each scanline (per pass when interlaced), then deflate everything into one chunk.
        var scanlines = new ScanlineSource<TPixel>(frame, colorType, bitDepth, indices, clearTransparent);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, this.CompressionLevel, leaveOpen: true))
        {
            if (this.InterlaceMethod == PngInterlaceMethod.Adam7)
            {
                foreach ((int x0, int y0, int dx, int dy) in Adam7Passes)
                {
                    int passWidth = x0 < width ? (width - x0 + dx - 1) / dx : 0;
                    int passHeight = y0 < height ? (height - y0 + dy - 1) / dy : 0;
                    if (passWidth == 0 || passHeight == 0)
                    {
                        continue;
                    }

                    this.WritePass(zlib, scanlines, x0, y0, dx, dy, passWidth, passHeight, bitsPerPixel, filterBpp);
                }
            }
            else
            {
                this.WritePass(zlib, scanlines, 0, 0, 1, 1, width, height, bitsPerPixel, filterBpp);
            }
        }

        WriteChunk(stream, "IDAT"u8, compressed.ToArray());
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    // ----- Setup -----

    private static PngColorType DefaultColorType<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(L8))
        {
            return PngColorType.Grayscale;
        }

        if (typeof(TPixel) == typeof(Rgb24) || typeof(TPixel) == typeof(Bgr24))
        {
            return PngColorType.Rgb;
        }

        return PngColorType.RgbWithAlpha;
    }

    private static void ValidateBitDepth(PngColorType colorType, int bitDepth)
    {
        bool valid = colorType switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Palette => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!valid)
        {
            throw new NotSupportedException($"PNG colour type {colorType} does not allow a bit depth of {bitDepth}.");
        }
    }

    private (byte[] Indices, Rgba32[] Palette) QuantizeToPalette<TPixel>(ImageFrame<TPixel> frame, int maxColors)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        IQuantizer quantizer = this.Quantizer ?? KnownQuantizers.Wu;
        QuantizerOptions options = quantizer.Options;
        if (options.MaxColors > maxColors)
        {
            options = options.WithMaxColors(maxColors);
        }

        IQuantizer<TPixel> worker = quantizer.CreatePixelSpecificQuantizer<TPixel>(options);
        IndexedImageFrame<TPixel> indexed = worker.QuantizeFrame(frame);
        ReadOnlySpan<TPixel> entries = indexed.Palette.Span;
        var palette = new Rgba32[entries.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = entries[i].ToRgba32();
        }

        return (indexed.IndexArray, palette);
    }

    /// <summary>Reduces the frame's luminance to 2^bitDepth evenly spaced levels (dithered as the quantizer options say) and returns the level of every pixel.</summary>
    private byte[] QuantizeToGrayLevels<TPixel>(ImageFrame<TPixel> frame, int bitDepth)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int levels = 1 << bitDepth;
        var grays = new Color[levels];
        for (int i = 0; i < levels; i++)
        {
            byte v = (byte)(i * 255 / (levels - 1));
            grays[i] = new Color(v, v, v);
        }

        // Match on luminance: quantize a grayscale copy so colour pixels pick the level nearest their brightness.
        var luminance = frame.CloneAs<L8>();
        IQuantizer source = this.Quantizer ?? KnownQuantizers.Wu;
        var quantizer = new PaletteQuantizer(grays, source.Options);
        return quantizer.CreatePixelSpecificQuantizer<L8>().QuantizeFrame(luminance).IndexArray;
    }

    private static void WritePaletteChunks(Stream stream, Rgba32[] palette)
    {
        var plte = new byte[palette.Length * 3];
        int lastAlpha = -1;
        for (int i = 0; i < palette.Length; i++)
        {
            plte[i * 3] = palette[i].R;
            plte[(i * 3) + 1] = palette[i].G;
            plte[(i * 3) + 2] = palette[i].B;
            if (palette[i].A != byte.MaxValue)
            {
                lastAlpha = i;
            }
        }

        WriteChunk(stream, "PLTE"u8, plte);
        if (lastAlpha >= 0)
        {
            var trns = new byte[lastAlpha + 1];
            for (int i = 0; i <= lastAlpha; i++)
            {
                trns[i] = palette[i].A;
            }

            WriteChunk(stream, "tRNS"u8, trns);
        }
    }

    // ----- Scanlines and filtering -----

    private void WritePass<TPixel>(
        Stream zlib, ScanlineSource<TPixel> scanlines, int x0, int y0, int dx, int dy, int passWidth, int passHeight,
        int bitsPerPixel, int filterBpp)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int bytesPerRow = ((passWidth * bitsPerPixel) + 7) / 8;
        var current = new byte[bytesPerRow];
        var previous = new byte[bytesPerRow];
        var filtered = new byte[bytesPerRow + 1];
        var candidate = new byte[bytesPerRow];

        for (int row = 0; row < passHeight; row++)
        {
            scanlines.Fill(y0 + (row * dy), x0, dx, passWidth, current);
            filtered[0] = (byte)this.Filter(current, row == 0 ? default : previous, filterBpp, candidate, filtered.AsSpan(1));
            zlib.Write(filtered);
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Filters one scanline into <paramref name="best"/> and returns the filter type byte.</summary>
    private int Filter(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> scratch, Span<byte> best)
    {
        switch (this.FilterMethod)
        {
            case PngFilterMethod.None:
                current.CopyTo(best);
                return 0;
            case PngFilterMethod.Sub:
                PngFilters.Filter(1, current, previous, bpp, best);
                return 1;
            case PngFilterMethod.Up:
                PngFilters.Filter(2, current, previous, bpp, best);
                return 2;
            case PngFilterMethod.Average:
                PngFilters.Filter(3, current, previous, bpp, best);
                return 3;
            case PngFilterMethod.Paeth:
                PngFilters.Filter(4, current, previous, bpp, best);
                return 4;
            default:
                return ChooseFilter(current, previous, bpp, scratch, best);
        }
    }

    /// <summary>
    /// Applies each PNG filter and keeps the one with the smallest absolute sum. Each filter runs as its
    /// own loop (rather than a switch inside one), so the vectorised Sub, Up and Average kernels apply and
    /// the sum is folded sixteen bytes at a time. The original early-exit only skipped work on candidates
    /// that were already losing, so scoring every candidate in full picks the same filter.
    /// </summary>
    private static int ChooseFilter(
        ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> scratch, Span<byte> best)
    {
        int bestFilter = 0;
        long bestSum = long.MaxValue;
        Span<byte> candidate = scratch[..current.Length];

        for (int filter = 0; filter <= 4; filter++)
        {
            PngFilters.Filter(filter, current, previous, bpp, candidate);
            long sum = PngFilters.AbsoluteSum(candidate);
            if (sum < bestSum)
            {
                bestSum = sum;
                bestFilter = filter;
                candidate.CopyTo(best);
            }
        }

        return bestFilter;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(type);
        stream.Write(data);

        // Chained Append calls compose correctly: the entry XOR undoes the previous exit XOR.
        uint crc = Crc32.Append(Crc32.Append(0, type), data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    /// <summary>Produces packed scanline bytes for any colour type, bit depth and column subset (for Adam7).</summary>
    private sealed class ScanlineSource<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly ImageFrame<TPixel> frame;
        private readonly PngColorType colorType;
        private readonly int bitDepth;
        private readonly byte[]? indices;
        private readonly bool clearTransparent;
        private readonly Rgba32[] row;
        private int cachedRow = -1;

        public ScanlineSource(ImageFrame<TPixel> frame, PngColorType colorType, int bitDepth, byte[]? indices, bool clearTransparent)
        {
            this.frame = frame;
            this.colorType = colorType;
            this.bitDepth = bitDepth;
            this.indices = indices;
            this.clearTransparent = clearTransparent;
            this.row = new Rgba32[frame.Width];
        }

        /// <summary>Writes the samples of pixels (x0, x0 + dx, ...) of image row <paramref name="y"/> into <paramref name="dest"/>.</summary>
        public void Fill(int y, int x0, int dx, int count, Span<byte> dest)
        {
            if (this.indices is not null)
            {
                this.FillIndices(y, x0, dx, count, dest);
                return;
            }

            if (this.cachedRow != y)
            {
                PixelOps.ToRgba32<TPixel>(this.frame.GetRowSpan(y), this.row);
                if (this.clearTransparent)
                {
                    for (int i = 0; i < this.row.Length; i++)
                    {
                        if (this.row[i].A == 0)
                        {
                            this.row[i] = default;
                        }
                    }
                }

                this.cachedRow = y;
            }

            bool wide = this.bitDepth == 16;
            int o = 0;
            for (int i = 0; i < count; i++)
            {
                Rgba32 p = this.row[x0 + (i * dx)];
                switch (this.colorType)
                {
                    case PngColorType.Grayscale:
                        o = WriteSample(dest, o, PixelOps.Luminance8(p), wide);
                        break;
                    case PngColorType.GrayscaleWithAlpha:
                        o = WriteSample(dest, o, PixelOps.Luminance8(p), wide);
                        o = WriteSample(dest, o, p.A, wide);
                        break;
                    case PngColorType.Rgb:
                        o = WriteSample(dest, o, p.R, wide);
                        o = WriteSample(dest, o, p.G, wide);
                        o = WriteSample(dest, o, p.B, wide);
                        break;
                    default:
                        o = WriteSample(dest, o, p.R, wide);
                        o = WriteSample(dest, o, p.G, wide);
                        o = WriteSample(dest, o, p.B, wide);
                        o = WriteSample(dest, o, p.A, wide);
                        break;
                }
            }
        }

        private void FillIndices(int y, int x0, int dx, int count, Span<byte> dest)
        {
            ReadOnlySpan<byte> source = this.indices.AsSpan(y * this.frame.Width, this.frame.Width);
            if (this.bitDepth == 8)
            {
                for (int i = 0; i < count; i++)
                {
                    dest[i] = source[x0 + (i * dx)];
                }

                return;
            }

            // Pack MSB-first; the last byte of a row is zero-padded.
            dest.Clear();
            int perByte = 8 / this.bitDepth;
            for (int i = 0; i < count; i++)
            {
                int shift = 8 - this.bitDepth - ((i % perByte) * this.bitDepth);
                dest[i / perByte] |= (byte)(source[x0 + (i * dx)] << shift);
            }
        }

        private static int WriteSample(Span<byte> dest, int offset, byte value, bool wide)
        {
            dest[offset++] = value;
            if (wide)
            {
                dest[offset++] = value; // v * 257 = (v << 8) | v: exact 8-to-16-bit widening.
            }

            return offset;
        }
    }
}
