using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>The compression schemes supported by <see cref="TiffEncoder"/>.</summary>
public enum TiffCompression
{
    /// <summary>No compression (TIFF tag 259 = 1).</summary>
    None,

    /// <summary>Adobe-style zlib/Deflate (tag 259 = 8).</summary>
    Deflate,

    /// <summary>TIFF-variant LZW (tag 259 = 5).</summary>
    Lzw,

    /// <summary>PackBits byte-oriented run-length coding (tag 259 = 32773).</summary>
    PackBits,

    /// <summary>CCITT Modified Huffman run-length coding of bilevel pages (tag 259 = 2).</summary>
    CcittRle,

    /// <summary>CCITT Group 3 fax coding of bilevel pages, one-dimensional with EOL codes (tag 259 = 3).</summary>
    Ccitt3,

    /// <summary>CCITT Group 4 (ITU-T T.6) fax coding of bilevel pages (tag 259 = 4). The densest option for scans.</summary>
    Ccitt4,
}

/// <summary>The pixel depths <see cref="TiffEncoder"/> can write.</summary>
public enum TiffBitsPerPixel
{
    /// <summary>One bit per pixel: a bilevel page, thresholded at mid-grey.</summary>
    Bit1 = 1,

    /// <summary>Four bits per pixel: 16-level greyscale.</summary>
    Bit4 = 4,

    /// <summary>Eight bits per pixel: 256-level greyscale.</summary>
    Bit8 = 8,

    /// <summary>Twenty-four bits per pixel: 8-bit RGB.</summary>
    Bit24 = 24,

    /// <summary>Thirty-two bits per pixel: 8-bit RGB with an unassociated alpha sample.</summary>
    Bit32 = 32,
}

/// <summary>
/// Encodes images as little-endian TIFF. Every frame of the image becomes a page.
/// <see cref="L8"/> images are written as 8-bit grayscale, RGB formats as 24-bit RGB,
/// and alpha formats as 32-bit RGBA. Each page carries the resolution, EXIF tags (including the
/// Exif/GPS sub-directories), ICC profile and XMP packet of the image (first page) or of the frame.
/// </summary>
/// <remarks>
/// <see cref="Compression"/>, <see cref="BitsPerPixel"/>, <see cref="PhotometricInterpretation"/> and
/// <see cref="Predictor"/> override those defaults. The CCITT compressions write a bilevel page thresholded
/// at mid-grey, which is what a scanned document should be stored as: on document-like pages Group 4 is the
/// densest of the three.
/// </remarks>
public sealed class TiffEncoder : IImageEncoder
{
    /// <summary>The compression scheme to apply to every page. Defaults to <see cref="TiffCompression.Deflate"/>.</summary>
    public TiffCompression Compression { get; init; } = TiffCompression.Deflate;

    /// <summary>
    /// The pixel depth to write. When left unset the depth follows the pixel format (8-bit grey for
    /// <see cref="L8"/>, 24-bit RGB for RGB formats, 32-bit RGBA otherwise), except that the CCITT
    /// compressions always write a bilevel page.
    /// </summary>
    public TiffBitsPerPixel? BitsPerPixel { get; init; }

    /// <summary>
    /// The photometric interpretation to write. When left unset, grey and bilevel pages are
    /// <see cref="TiffPhotometricInterpretation.BlackIsZero"/> (or
    /// <see cref="TiffPhotometricInterpretation.WhiteIsZero"/> for the CCITT compressions, the usual fax
    /// tagging) and colour pages are <see cref="TiffPhotometricInterpretation.Rgb"/>.
    /// </summary>
    public TiffPhotometricInterpretation? PhotometricInterpretation { get; init; }

    /// <summary>
    /// Horizontal differencing applied before compression. Only <see cref="TiffPredictor.Horizontal"/> is
    /// supported, only for 8-bit samples and only with <see cref="TiffCompression.Lzw"/> or
    /// <see cref="TiffCompression.Deflate"/>, where it usually shrinks photographic pages noticeably.
    /// </summary>
    public TiffPredictor Predictor { get; init; } = TiffPredictor.None;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        PageLayout layout = this.ResolveLayout<TPixel>();

        int frameCount = image.Frames.Count;
        var frameData = new byte[frameCount][];
        var directories = new IfdBuilder[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            frameData[f] = this.CompressFrame(image.Frames[f], in layout);
            directories[f] = BuildDirectory(image, f, in layout, frameData[f].Length);
        }

        // Compute the file layout: [header][data0][ifd0 + its values/sub-IFDs][data1][ifd1]...
        // Every position is accumulated as a long and narrowed only through CheckedOffset, so an oversized
        // encode is reported as the size limit it is instead of escaping as an OverflowException.
        var dataOffsets = new int[frameCount];
        var ifdOffsets = new int[frameCount];
        long cursor = 8;
        for (int f = 0; f < frameCount; f++)
        {
            cursor = (cursor + 1) & ~1L; // Word-align.
            dataOffsets[f] = CheckedOffset(cursor);
            cursor += frameData[f].Length;
            cursor = (cursor + 1) & ~1L;
            ifdOffsets[f] = CheckedOffset(cursor);
            cursor += directories[f].Measure();
        }

        // The last directory's own bytes still have to land inside the addressable range, and the write loop
        // below tracks the stream position in an int.
        _ = CheckedOffset(cursor);

        // Header
        Span<byte> scratch = stackalloc byte[4];
        stream.WriteByte((byte)'I');
        stream.WriteByte((byte)'I');
        stream.WriteByte(42);
        stream.WriteByte(0);
        BinaryPrimitives.WriteInt32LittleEndian(scratch, ifdOffsets.Length > 0 ? ifdOffsets[0] : 0);
        stream.Write(scratch);

        int position = 8;
        for (int f = 0; f < frameCount; f++)
        {
            position = PadTo(stream, position, dataOffsets[f]);
            stream.Write(frameData[f]);
            position += frameData[f].Length;

            position = PadTo(stream, position, ifdOffsets[f]);
            directories[f].AddLong(273, (uint)dataOffsets[f]); // StripOffsets
            byte[] block = directories[f].Serialize((uint)ifdOffsets[f], f + 1 < frameCount ? (uint)ifdOffsets[f + 1] : 0);
            stream.Write(block);
            position += block.Length;
        }
    }

    /// <summary>
    /// Reported when a page's data or the file layout runs past what the encoder's 32-bit offsets can address.
    /// </summary>
    private const string SizeLimitMessage =
        "The TIFF file would exceed the format's 4 GiB offset limit; this encoder writes 32-bit offsets and stops at 2 GiB.";

    /// <summary>
    /// Narrows a layout position to the offset a directory entry stores it in, reporting the encoder's size
    /// limit rather than letting an <see cref="OverflowException"/> escape.
    /// </summary>
    /// <param name="position">The absolute file position to narrow.</param>
    /// <returns>The position as an <see cref="int"/>.</returns>
    private static int CheckedOffset(long position)
        => position <= int.MaxValue ? (int)position : throw new NotSupportedException(SizeLimitMessage);

    /// <summary>
    /// Tags never copied from a source EXIF profile into a page directory: the sample layout and data pointers
    /// (written from the actual pixel data), sub-directory pointers (rebuilt), and the ICC/XMP payloads (written
    /// from their own profiles).
    /// </summary>
    private static readonly HashSet<ushort> ReservedTags = new()
    {
        254, 255, 256, 257, 258, 259, 262, 266, 273, 277, 278, 279, 282, 283, 284, 292, 293, 296, 317, 320, 322, 323,
        324, 325, 330, 338, 339, 347, 512, 513, 514, 515, 517, 518, 519, 520, 521, 700, 34665, 34675, 34853, 40965,
    };

    /// <summary>
    /// Builds a page directory: layout entries from the pixel data, resolution from the image metadata, and the
    /// EXIF (IFD0 tags plus Exif/GPS/Interop sub-directories), ICC and XMP profiles of the image (first page) or
    /// of the frame (further pages).
    /// </summary>
    private static IfdBuilder BuildDirectory<TPixel>(Image<TPixel> image, int frameIndex, in PageLayout layout, int dataLength)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> frame = image.Frames[frameIndex];
        ImageMetadata metadata = image.Metadata;
        ExifProfile? exif = frameIndex == 0 ? metadata.ExifProfile : frame.Metadata.ExifProfile;
        IccProfile? icc = frameIndex == 0 ? metadata.IccProfile : frame.Metadata.IccProfile;
        XmpProfile? xmp = frameIndex == 0 ? metadata.XmpProfile : frame.Metadata.XmpProfile;

        var ifd = new IfdBuilder();
        if (exif is not null)
        {
            foreach (IExifValue value in exif.Values)
            {
                if (value.Tag.Ifd == ExifIfd.Ifd0 && !ReservedTags.Contains(value.Tag.Id))
                {
                    ifd.TryAdd(value);
                }
            }

            IfdBuilder exifIfd = exif.BuildDirectory(ExifIfd.Exif);
            IfdBuilder interop = exif.BuildDirectory(ExifIfd.Interop);
            IfdBuilder gps = exif.BuildDirectory(ExifIfd.Gps);
            if (interop.Count > 0)
            {
                exifIfd.AddSubIfd(0xA005, interop);
            }

            if (exifIfd.Count > 0)
            {
                ifd.AddSubIfd(0x8769, exifIfd);
            }

            if (gps.Count > 0)
            {
                ifd.AddSubIfd(0x8825, gps);
            }
        }

        // Layout entries (tags are kept in ascending order by the builder).
        int spp = layout.SamplesPerPixel;
        ifd.AddLong(256, (uint)frame.Width); // ImageWidth
        ifd.AddLong(257, (uint)frame.Height); // ImageLength
        Span<ushort> bits = stackalloc ushort[spp];
        bits.Fill((ushort)layout.BitsPerSample);
        ifd.AddShorts(258, bits); // BitsPerSample
        ifd.AddShort(259, (ushort)layout.CompressionTag); // Compression
        ifd.AddShort(262, (ushort)layout.Photometric); // PhotometricInterpretation
        ifd.AddLong(273, 0); // StripOffsets (patched once the layout is known)
        ifd.AddShort(277, (ushort)spp); // SamplesPerPixel
        ifd.AddLong(278, (uint)frame.Height); // RowsPerStrip
        ifd.AddLong(279, (uint)dataLength); // StripByteCounts
        ifd.AddShort(284, 1); // PlanarConfiguration
        if (layout.CompressionTag == 3)
        {
            ifd.AddLong(292, 0); // T4Options: one-dimensional coding, no fill bits, no uncompressed mode
        }
        else if (layout.CompressionTag == 4)
        {
            ifd.AddLong(293, 0); // T6Options: no uncompressed mode
        }

        if (layout.ApplyPredictor)
        {
            ifd.AddShort(317, 2); // Predictor: horizontal differencing
        }

        if (spp == 4)
        {
            ifd.AddShort(338, 2); // ExtraSamples: unassociated alpha
        }

        // Resolution: the image metadata for the first page; further pages keep their own tags when they have them.
        bool frameHasResolution = frameIndex > 0 && exif is not null
            && exif.Contains(ExifTag.XResolution) && exif.Contains(ExifTag.YResolution);
        if (frameHasResolution)
        {
            ifd.TryAdd(exif!.GetValue(ExifTag.XResolution)!);
            ifd.TryAdd(exif.GetValue(ExifTag.YResolution)!);
            if (exif.TryGetValue(ExifTag.ResolutionUnit, out IExifValue<ushort>? unit))
            {
                ifd.TryAdd(unit);
            }
        }
        else
        {
            (double x, double y, ushort unit) = metadata.ResolutionUnits switch
            {
                PixelResolutionUnit.AspectRatio => (metadata.HorizontalResolution, metadata.VerticalResolution, (ushort)1),
                PixelResolutionUnit.PixelsPerCentimeter => (metadata.HorizontalResolution, metadata.VerticalResolution, (ushort)3),
                PixelResolutionUnit.PixelsPerMeter => (
                    metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerCentimeter),
                    metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerCentimeter),
                    (ushort)3),
                _ => (metadata.HorizontalResolution, metadata.VerticalResolution, (ushort)2),
            };
            ifd.AddRational(282, new Rational(x)); // XResolution
            ifd.AddRational(283, new Rational(y)); // YResolution
            ifd.AddShort(296, unit); // ResolutionUnit
        }

        if (xmp is not null)
        {
            ifd.AddBytes(700, ExifDataType.Byte, xmp.RawArray);
        }

        if (icc is not null)
        {
            ifd.AddBytes(34675, ExifDataType.Undefined, icc.RawArray);
        }

        return ifd;
    }

    private static int PadTo(Stream stream, int position, int target)
    {
        while (position < target)
        {
            stream.WriteByte(0);
            position++;
        }

        return position;
    }

    /// <summary>Chooses the sample layout for every page from the encoder options and the pixel format.</summary>
    private PageLayout ResolveLayout<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        (int compressionTag, TiffCcittScheme? ccitt) = this.Compression switch
        {
            TiffCompression.None => (1, (TiffCcittScheme?)null),
            TiffCompression.CcittRle => (2, TiffCcittScheme.ModifiedHuffman),
            TiffCompression.Ccitt3 => (3, TiffCcittScheme.Group3),
            TiffCompression.Ccitt4 => (4, TiffCcittScheme.Group4),
            TiffCompression.Lzw => (5, null),
            TiffCompression.PackBits => (32773, null),
            _ => (8, null),
        };

        // The fax schemes only code bilevel data, so they pick a bilevel page unless the caller asked for
        // something else - in which case the request is contradictory and is reported as unsupported.
        TiffBitsPerPixel depth = this.BitsPerPixel
            ?? (ccitt is not null ? TiffBitsPerPixel.Bit1 : DefaultDepth<TPixel>());
        if (ccitt is not null && depth != TiffBitsPerPixel.Bit1)
        {
            throw new NotSupportedException($"TIFF {this.Compression} compression only codes bilevel pages, not {(int)depth}-bit ones.");
        }

        int samplesPerPixel = depth switch
        {
            TiffBitsPerPixel.Bit24 => 3,
            TiffBitsPerPixel.Bit32 => 4,
            _ => 1,
        };
        int bitsPerSample = depth switch
        {
            TiffBitsPerPixel.Bit1 => 1,
            TiffBitsPerPixel.Bit4 => 4,
            _ => 8,
        };

        TiffPhotometricInterpretation photometric = this.PhotometricInterpretation
            ?? (samplesPerPixel > 1 ? TiffPhotometricInterpretation.Rgb
                : ccitt is not null ? TiffPhotometricInterpretation.WhiteIsZero
                : TiffPhotometricInterpretation.BlackIsZero);
        bool colour = photometric == TiffPhotometricInterpretation.Rgb;
        if (colour != samplesPerPixel > 1
            || (!colour && photometric is not (TiffPhotometricInterpretation.WhiteIsZero or TiffPhotometricInterpretation.BlackIsZero)))
        {
            throw new NotSupportedException(
                $"The TIFF encoder cannot write photometric interpretation {photometric} with {(int)depth} bits per pixel.");
        }

        if (this.Predictor is not (TiffPredictor.None or TiffPredictor.Horizontal))
        {
            throw new NotSupportedException($"The TIFF encoder does not implement predictor {this.Predictor}.");
        }

        bool predictor = this.Predictor == TiffPredictor.Horizontal;
        if (predictor && (bitsPerSample != 8 || compressionTag is not (5 or 8)))
        {
            throw new NotSupportedException(
                "TIFF horizontal differencing is only supported for 8-bit samples compressed with LZW or Deflate.");
        }

        return new PageLayout(bitsPerSample, samplesPerPixel, (int)photometric, compressionTag, predictor, ccitt);
    }

    private static TiffBitsPerPixel DefaultDepth<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(L8) ? TiffBitsPerPixel.Bit8
            : typeof(TPixel) == typeof(Rgb24) || typeof(TPixel) == typeof(Bgr24) ? TiffBitsPerPixel.Bit24
            : TiffBitsPerPixel.Bit32;

    private byte[] CompressFrame<TPixel>(ImageFrame<TPixel> frame, in PageLayout layout)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int spp = layout.SamplesPerPixel;

        // A page is buffered as one strip, so its uncompressed bytes have to fit a single array: the row
        // stride and the total are measured as longs first, because both products overflow an int well
        // inside the dimensions an image can legitimately have.
        long rowBytesLong = (((long)width * spp * layout.BitsPerSample) + 7) / 8;
        long rawLength = rowBytesLong * height;
        if (rawLength > int.MaxValue)
        {
            throw new NotSupportedException(
                $"TIFF cannot represent a {width}x{height} image at {layout.BitsPerSample * spp} bits per pixel; " + SizeLimitMessage);
        }

        int rowBytes = (int)rowBytesLong;
        var raw = new byte[(int)rawLength];
        var rgbaRow = new Rgba32[width];

        // A WhiteIsZero page stores the complement of the grey level, which also lets a bilevel scan code its
        // background as the white runs the fax schemes compress best.
        bool invert = layout.Photometric == 0;

        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
            Span<byte> row = raw.AsSpan(y * rowBytes, rowBytes);
            switch (layout.BitsPerSample * spp)
            {
                case 1:
                    for (int x = 0; x < width; x++)
                    {
                        if (PixelOps.Luminance8(rgbaRow[x]) >= 128 != invert)
                        {
                            row[x >> 3] |= (byte)(0x80 >> (x & 7));
                        }
                    }

                    break;
                case 4:
                    for (int x = 0; x < width; x++)
                    {
                        int value = PixelOps.Luminance8(rgbaRow[x]) >> 4;
                        if (invert)
                        {
                            value = 15 - value;
                        }

                        row[x >> 1] |= (byte)(value << ((x & 1) == 0 ? 4 : 0));
                    }

                    break;
                case 8:
                    for (int x = 0; x < width; x++)
                    {
                        byte value = PixelOps.Luminance8(rgbaRow[x]);
                        row[x] = invert ? (byte)(255 - value) : value;
                    }

                    break;
                case 24:
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 p = rgbaRow[x];
                        int i = x * 3;
                        row[i] = p.R;
                        row[i + 1] = p.G;
                        row[i + 2] = p.B;
                    }

                    break;
                default:
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 p = rgbaRow[x];
                        int i = x * 4;
                        row[i] = p.R;
                        row[i + 1] = p.G;
                        row[i + 2] = p.B;
                        row[i + 3] = p.A;
                    }

                    break;
            }
        }

        if (layout.Ccitt is TiffCcittScheme scheme)
        {
            return TiffCcitt.Encode(raw, width, height, scheme);
        }

        if (layout.ApplyPredictor)
        {
            ApplyHorizontalDifferencing(raw, rowBytes, spp);
        }

        switch (this.Compression)
        {
            case TiffCompression.Lzw:
                return TiffLzw.Encode(raw);
            case TiffCompression.PackBits:
                return PackBits(raw);
            case TiffCompression.Deflate:
            {
                using var output = new MemoryStream();
                using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(raw);
                }

                return output.ToArray();
            }

            default:
                return raw;
        }
    }

    /// <summary>Replaces every 8-bit sample with its difference from the sample of the same channel to its left.</summary>
    private static void ApplyHorizontalDifferencing(byte[] raw, int rowBytes, int samplesPerPixel)
    {
        for (int start = 0; start + rowBytes <= raw.Length; start += rowBytes)
        {
            for (int i = rowBytes - 1; i >= samplesPerPixel; i--)
            {
                raw[start + i] = (byte)(raw[start + i] - raw[start + i - samplesPerPixel]);
            }
        }
    }

    /// <summary>PackBits run-length coding as defined in TIFF 6.0 section 9.</summary>
    private static byte[] PackBits(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>((data.Length / 2) + 8);
        int i = 0;
        while (i < data.Length)
        {
            int repeat = 1;
            while (i + repeat < data.Length && data[i + repeat] == data[i] && repeat < 128)
            {
                repeat++;
            }

            if (repeat >= 3)
            {
                output.Add((byte)(sbyte)(1 - repeat));
                output.Add(data[i]);
                i += repeat;
                continue;
            }

            // Literal run: bytes are copied until a run of three or more equal bytes begins.
            int start = i;
            int literal = 0;
            while (i < data.Length && literal < 128)
            {
                int run = 1;
                while (i + run < data.Length && data[i + run] == data[i] && run < 3)
                {
                    run++;
                }

                if (run >= 3)
                {
                    break;
                }

                i++;
                literal++;
            }

            output.Add((byte)(literal - 1));
            for (int k = 0; k < literal; k++)
            {
                output.Add(data[start + k]);
            }
        }

        return output.ToArray();
    }

    /// <summary>The sample layout shared by every page of one encode.</summary>
    private readonly record struct PageLayout(
        int BitsPerSample, int SamplesPerPixel, int Photometric, int CompressionTag, bool ApplyPredictor, TiffCcittScheme? Ccitt);
}
