using System.Buffers.Binary;

namespace EasyImageSharp.Formats.Tga;

/// <summary>The 18-byte Truevision TGA file header plus the sanity checks used for format detection.</summary>
internal readonly struct TgaHeader
{
    public const int Size = 18;
    public const int FooterSize = 26;

    public const int TypeNoImage = 0;
    public const int TypeColorMapped = 1;
    public const int TypeTrueColor = 2;
    public const int TypeGrayscale = 3;
    public const int RleFlag = 8;
    public const int TypeHuffmanColorMapped = 32;
    public const int TypeHuffmanQuadtree = 33;

    public const int DescriptorRightToLeft = 0x10;
    public const int DescriptorTopToBottom = 0x20;
    public const int DescriptorReservedBits = 0xC0;

    public readonly int IdLength;
    public readonly int ColorMapType;
    public readonly int ImageType;
    public readonly int ColorMapFirstEntry;
    public readonly int ColorMapLength;
    public readonly int ColorMapEntrySize;
    public readonly int XOrigin;
    public readonly int YOrigin;
    public readonly int Width;
    public readonly int Height;
    public readonly int PixelDepth;
    public readonly int Descriptor;

    private TgaHeader(ReadOnlySpan<byte> data)
    {
        this.IdLength = data[0];
        this.ColorMapType = data[1];
        this.ImageType = data[2];
        this.ColorMapFirstEntry = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
        this.ColorMapLength = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        this.ColorMapEntrySize = data[7];
        this.XOrigin = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        this.YOrigin = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        this.Width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        this.Height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        this.PixelDepth = data[16];
        this.Descriptor = data[17];
    }

    /// <summary>The TGA 2.0 footer signature ("TRUEVISION-XFILE" + '.' + NUL) that ends the 26-byte footer.</summary>
    public static ReadOnlySpan<byte> FooterSignature => "TRUEVISION-XFILE.\0"u8;

    /// <summary>Image type with the run-length flag removed (1 = colour-mapped, 2 = truecolor, 3 = grayscale).</summary>
    public int BaseType => this.ImageType & ~RleFlag;

    public bool IsRunLengthEncoded => (this.ImageType & RleFlag) != 0 && this.ImageType < TypeHuffmanColorMapped;

    public bool TopToBottom => (this.Descriptor & DescriptorTopToBottom) != 0;

    public bool RightToLeft => (this.Descriptor & DescriptorRightToLeft) != 0;

    /// <summary>The number of attribute (alpha) bits per pixel declared in the descriptor.</summary>
    public int AlphaBits => this.Descriptor & 0x0F;

    /// <summary>Bytes per stored pixel (15-bit pixels occupy two bytes).</summary>
    public int BytesPerPixel => (this.PixelDepth + 7) / 8;

    /// <summary>Bytes per colour-map entry (15-bit entries occupy two bytes).</summary>
    public int ColorMapEntryBytes => (this.ColorMapEntrySize + 7) / 8;

    /// <summary>Total size of the colour map in bytes (zero when there is no map).</summary>
    public long ColorMapBytes => this.ColorMapType == 1 ? (long)this.ColorMapLength * this.ColorMapEntryBytes : 0;

    /// <summary>Reads the header; the caller guarantees at least <see cref="Size"/> bytes.</summary>
    public static TgaHeader Read(ReadOnlySpan<byte> data) => new(data);

    /// <summary>True when the data ends with a TGA 2.0 footer.</summary>
    public static bool HasFooter(ReadOnlySpan<byte> data)
        => data.Length >= Size + FooterSize && data[^18..].SequenceEqual(FooterSignature);

    /// <summary>
    /// The format-detection test. TGA has no leading magic number, so a file is recognised either by a strict
    /// consistency check of the header (including, for uncompressed images, that the file is large enough to
    /// hold the declared pixels) or by the TGA 2.0 footer at the end of the file.
    /// </summary>
    public static bool IsPlausible(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            return false;
        }

        TgaHeader h = Read(data);
        if (h.ColorMapType > 1 || h.Width == 0 || h.Height == 0 || (h.Descriptor & DescriptorReservedBits) != 0)
        {
            return false;
        }

        bool footer = HasFooter(data);
        int baseType = h.BaseType;
        bool knownType = h.ImageType is TypeColorMapped or TypeTrueColor or TypeGrayscale
            or (TypeColorMapped | RleFlag) or (TypeTrueColor | RleFlag) or (TypeGrayscale | RleFlag);
        if (!knownType)
        {
            // Huffman/quadtree variants are only recognised (and then rejected as unsupported) when the footer vouches for the file.
            return footer && h.ImageType is TypeHuffmanColorMapped or TypeHuffmanQuadtree;
        }

        bool depthOk = baseType switch
        {
            TypeColorMapped => h.PixelDepth is 8 or 16,
            TypeTrueColor => h.PixelDepth is 15 or 16 or 24 or 32,
            _ => h.PixelDepth is 8 or 16,
        };
        if (!depthOk)
        {
            return false;
        }

        if (footer)
        {
            return true;
        }

        // Without a footer the remaining header fields must be fully consistent.
        if (baseType == TypeColorMapped && h.ColorMapType != 1)
        {
            return false;
        }

        if (h.ColorMapType == 1)
        {
            if (h.ColorMapLength == 0 || h.ColorMapEntrySize is not (15 or 16 or 24 or 32))
            {
                return false;
            }
        }
        else if (h.ColorMapLength != 0)
        {
            return false;
        }

        bool alphaOk = h.PixelDepth switch
        {
            32 => h.AlphaBits is 0 or 8,
            16 => h.AlphaBits is 0 or 1 or 8,
            _ => h.AlphaBits == 0,
        };
        if (!alphaOk)
        {
            return false;
        }

        long minimumLength = Size + h.IdLength + h.ColorMapBytes;
        if (!h.IsRunLengthEncoded)
        {
            minimumLength += (long)h.Width * h.Height * h.BytesPerPixel;
        }

        return data.Length >= minimumLength;
    }
}
