using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tga;

/// <summary>
/// Decodes Truevision TGA images: colour-mapped (type 1), truecolor (type 2) and grayscale (type 3) images,
/// their run-length encoded variants (types 9/10/11), 8/15/16/24/32-bit pixels, colour maps with 15/16/24/32-bit
/// entries and a non-zero first-entry index, all four origins from the descriptor byte, and files with or
/// without the TGA 2.0 footer (extension and developer areas are skipped).
/// </summary>
/// <remarks>
/// Alpha handling: 32-bit pixels and 32-bit map entries always carry alpha; 16-bit truecolor pixels carry a
/// one-bit alpha (bit 15 set = opaque) only when the descriptor declares one attribute bit; 15-bit pixels,
/// 16-bit map entries and 24-bit data are opaque; 16-bit grayscale pixels are luminance + alpha. Run-length
/// packets that span row boundaries are tolerated. Image type 0 (no image data) is malformed; the Huffman/
/// quadtree types 32 and 33 are not supported.
/// </remarks>
public sealed class TgaDecoder : IImageDecoder
{
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
            throw DecoderGuard.Wrap("TGA", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            TgaHeader header = ParseHeader(data);
            return new ImageInfo(header.Width, header.Height, header.PixelDepth, 1, ImageFormat.Tga);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("TGA", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        TgaHeader header = ParseHeader(data);
        int width = header.Width;
        int height = header.Height;
        options.EnsureFrameWithinLimits(width, height, "TGA");

        long offset = TgaHeader.Size + header.IdLength;
        Rgba32[]? palette = null;
        if (header.ColorMapType == 1)
        {
            long mapBytes = header.ColorMapBytes;
            if (offset + mapBytes > data.Length)
            {
                throw new InvalidImageContentException("TGA colour map is truncated.");
            }

            if (header.BaseType == TgaHeader.TypeColorMapped)
            {
                palette = ReadColorMap(data.Slice((int)offset, (int)mapBytes), header);
            }

            offset += mapBytes;
        }

        if (offset > data.Length)
        {
            throw new InvalidImageContentException("TGA image ID or colour map is truncated.");
        }

        var context = new PixelContext(header, palette);
        var image = new Image<TPixel>(width, height);
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        ReadOnlySpan<byte> pixels = data[(int)offset..];

        if (header.IsRunLengthEncoded)
        {
            // Packets may legally span rows in the wild, so decode the whole stream into a storage-order buffer first.
            var storage = new Rgba32[width * height];
            DecodeRunLength(pixels, storage, context);
            for (int row = 0; row < height; row++)
            {
                StoreRow(frame, storage.AsSpan(row * width, width), row, header);
            }
        }
        else
        {
            int rowBytes = width * header.BytesPerPixel;
            if ((long)rowBytes * height > pixels.Length)
            {
                throw new InvalidImageContentException("TGA pixel data is truncated.");
            }

            var rgbaRow = new Rgba32[width];
            for (int row = 0; row < height; row++)
            {
                context.ConvertPixels(pixels.Slice(row * rowBytes, rowBytes), rgbaRow);
                StoreRow(frame, rgbaRow, row, header);
            }
        }

        return image;
    }

    /// <summary>Writes a storage-order row into the frame, honouring the origin bits of the descriptor.</summary>
    private static void StoreRow<TPixel>(ImageFrame<TPixel> frame, Span<Rgba32> row, int storageRow, in TgaHeader header)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (header.RightToLeft)
        {
            row.Reverse();
        }

        int destY = header.TopToBottom ? storageRow : frame.Height - 1 - storageRow;
        PixelOps.FromRgba32(row, frame.GetRowSpan(destY));
    }

    private static void DecodeRunLength(ReadOnlySpan<byte> src, Span<Rgba32> dest, in PixelContext context)
    {
        int bytesPerPixel = context.BytesPerPixel;
        int pos = 0;
        int written = 0;
        Span<Rgba32> one = stackalloc Rgba32[1];
        while (written < dest.Length)
        {
            if (pos >= src.Length)
            {
                throw new InvalidImageContentException("TGA run-length data ends before the image is complete.");
            }

            int packet = src[pos++];
            int count = (packet & 0x7F) + 1;
            if (count > dest.Length - written)
            {
                // Packets never legitimately extend past the last pixel; a decoder that tolerated it would still
                // have to stop here, so clip rather than fail.
                count = dest.Length - written;
            }

            if ((packet & 0x80) != 0)
            {
                if (pos + bytesPerPixel > src.Length)
                {
                    throw new InvalidImageContentException("TGA run-length packet is truncated.");
                }

                context.ConvertPixels(src.Slice(pos, bytesPerPixel), one);
                dest.Slice(written, count).Fill(one[0]);
                pos += bytesPerPixel;
            }
            else
            {
                int bytes = count * bytesPerPixel;
                if (pos + bytes > src.Length)
                {
                    throw new InvalidImageContentException("TGA raw packet is truncated.");
                }

                context.ConvertPixels(src.Slice(pos, bytes), dest.Slice(written, count));
                pos += bytes;
            }

            written += count;
        }
    }

    private static Rgba32[] ReadColorMap(ReadOnlySpan<byte> map, in TgaHeader header)
    {
        var palette = new Rgba32[header.ColorMapLength];
        int entryBytes = header.ColorMapEntryBytes;
        for (int i = 0; i < palette.Length; i++)
        {
            ReadOnlySpan<byte> e = map.Slice(i * entryBytes, entryBytes);
            palette[i] = header.ColorMapEntrySize switch
            {
                15 or 16 => Expand555(BinaryPrimitives.ReadUInt16LittleEndian(e), byte.MaxValue),
                24 => new Rgba32(e[2], e[1], e[0]),
                _ => new Rgba32(e[2], e[1], e[0], e[3]),
            };
        }

        return palette;
    }

    private static Rgba32 Expand555(ushort value, byte alpha)
    {
        static byte Widen(int v) => (byte)(((v * 255) + 15) / 31);
        return new Rgba32(Widen((value >> 10) & 0x1F), Widen((value >> 5) & 0x1F), Widen(value & 0x1F), alpha);
    }

    /// <summary>Validates every header field that decoding depends on; pixel-data bounds are checked by the caller.</summary>
    private static TgaHeader ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < TgaHeader.Size)
        {
            throw new InvalidImageContentException("TGA header is truncated.");
        }

        TgaHeader h = TgaHeader.Read(data);
        if (h.ImageType == TgaHeader.TypeNoImage)
        {
            throw new InvalidImageContentException("TGA file contains no image data (image type 0).");
        }

        if (h.ImageType is TgaHeader.TypeHuffmanColorMapped or TgaHeader.TypeHuffmanQuadtree)
        {
            throw new NotSupportedException($"TGA image type {h.ImageType} (Huffman/Delta/quadtree compression) is not supported.");
        }

        if (h.BaseType is not (TgaHeader.TypeColorMapped or TgaHeader.TypeTrueColor or TgaHeader.TypeGrayscale))
        {
            throw new InvalidImageContentException($"Invalid TGA image type: {h.ImageType}.");
        }

        if (h.ColorMapType > 1)
        {
            throw new InvalidImageContentException($"Invalid TGA colour map type: {h.ColorMapType}.");
        }

        if (h.Width == 0 || h.Height == 0)
        {
            throw new InvalidImageContentException("Invalid TGA dimensions.");
        }

        if ((h.Descriptor & TgaHeader.DescriptorReservedBits) != 0)
        {
            throw new NotSupportedException("Interleaved TGA images (descriptor bits 6-7) are not supported.");
        }

        bool depthOk = h.BaseType switch
        {
            TgaHeader.TypeColorMapped => h.PixelDepth is 8 or 16,
            TgaHeader.TypeTrueColor => h.PixelDepth is 15 or 16 or 24 or 32,
            _ => h.PixelDepth is 8 or 16,
        };
        if (!depthOk)
        {
            throw new InvalidImageContentException($"Invalid TGA pixel depth {h.PixelDepth} for image type {h.ImageType}.");
        }

        if (h.BaseType == TgaHeader.TypeColorMapped)
        {
            if (h.ColorMapType != 1 || h.ColorMapLength == 0)
            {
                throw new InvalidImageContentException("Colour-mapped TGA image has no colour map.");
            }
        }

        if (h.ColorMapType == 1 && h.ColorMapEntrySize is not (15 or 16 or 24 or 32))
        {
            throw new InvalidImageContentException($"Invalid TGA colour map entry size: {h.ColorMapEntrySize}.");
        }

        return h;
    }

    /// <summary>Converts stored pixels of one specific layout into <see cref="Rgba32"/>.</summary>
    private readonly struct PixelContext
    {
        private readonly int kind;
        private readonly int firstEntry;
        private readonly Rgba32[]? palette;

        private const int KindGray8 = 0;
        private const int KindGray16 = 1;
        private const int KindIndex8 = 2;
        private const int KindIndex16 = 3;
        private const int KindRgb555 = 4;
        private const int KindArgb1555 = 5;
        private const int KindBgr24 = 6;
        private const int KindBgra32 = 7;

        public PixelContext(in TgaHeader header, Rgba32[]? palette)
        {
            this.palette = palette;
            this.firstEntry = header.ColorMapFirstEntry;
            this.BytesPerPixel = header.BytesPerPixel;
            this.kind = header.BaseType switch
            {
                TgaHeader.TypeColorMapped => header.PixelDepth == 8 ? KindIndex8 : KindIndex16,
                TgaHeader.TypeGrayscale => header.PixelDepth == 8 ? KindGray8 : KindGray16,
                _ => header.PixelDepth switch
                {
                    15 => KindRgb555,
                    16 => header.AlphaBits == 1 ? KindArgb1555 : KindRgb555,
                    24 => KindBgr24,
                    _ => KindBgra32,
                },
            };
        }

        public int BytesPerPixel { get; }

        public void ConvertPixels(ReadOnlySpan<byte> src, Span<Rgba32> dest)
        {
            switch (this.kind)
            {
                case KindGray8:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        byte v = src[i];
                        dest[i] = new Rgba32(v, v, v);
                    }

                    break;
                case KindGray16:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        byte v = src[i * 2];
                        dest[i] = new Rgba32(v, v, v, src[(i * 2) + 1]);
                    }

                    break;
                case KindIndex8:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        dest[i] = this.Lookup(src[i]);
                    }

                    break;
                case KindIndex16:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        dest[i] = this.Lookup(BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]));
                    }

                    break;
                case KindRgb555:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        dest[i] = Expand555(BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]), byte.MaxValue);
                    }

                    break;
                case KindArgb1555:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]);
                        dest[i] = Expand555(v, (v & 0x8000) != 0 ? byte.MaxValue : (byte)0);
                    }

                    break;
                case KindBgr24:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        int o = i * 3;
                        dest[i] = new Rgba32(src[o + 2], src[o + 1], src[o]);
                    }

                    break;
                default:
                    for (int i = 0; i < dest.Length; i++)
                    {
                        int o = i * 4;
                        dest[i] = new Rgba32(src[o + 2], src[o + 1], src[o], src[o + 3]);
                    }

                    break;
            }
        }

        private Rgba32 Lookup(int index)
        {
            int i = index - this.firstEntry;
            Rgba32[] map = this.palette!;
            if ((uint)i >= (uint)map.Length)
            {
                throw new InvalidImageContentException($"TGA colour map index {index} is outside the {map.Length}-entry map starting at {this.firstEntry}.");
            }

            return map[i];
        }
    }
}
