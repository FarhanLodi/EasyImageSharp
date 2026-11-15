using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Iptc;
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

/// <summary>The container version <see cref="TiffEncoder"/> writes.</summary>
public enum TiffFormatType
{
    /// <summary>
    /// Classic TIFF, switching to BigTIFF only when the file would not fit classic TIFF's 32-bit offsets (4 GiB).
    /// The <see cref="TiffMetadata.BigTiff"/> flag of a decoded image does not influence the choice.
    /// </summary>
    Auto,

    /// <summary>Classic TIFF (version 42, 32-bit offsets). A file that would exceed 4 GiB is refused.</summary>
    Classic,

    /// <summary>
    /// BigTIFF (version 43): a 16-byte header, 64-bit offsets and entry counts, LONG8 strip offsets and byte counts,
    /// and IFD8 pointers to the Exif/GPS/Interop sub-directories, whatever the file size.
    /// </summary>
    BigTiff,
}

/// <summary>
/// Encodes images as little-endian TIFF or BigTIFF. Every frame of the image becomes a page.
/// <see cref="L8"/> images are written as 8-bit grayscale, RGB formats as 24-bit RGB,
/// and alpha formats as 32-bit RGBA. Each page carries the resolution, EXIF tags (including the
/// Exif/GPS sub-directories), ICC profile, XMP packet, IPTC profile (tag 33723) and Photoshop image resources
/// (tag 34377) of the image (first page) or of the frame.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Compression"/>, <see cref="BitsPerPixel"/>, <see cref="PhotometricInterpretation"/> and
/// <see cref="Predictor"/> override those defaults. The CCITT compressions write a bilevel page thresholded
/// at mid-grey, which is what a scanned document should be stored as: on document-like pages Group 4 is the
/// densest of the three.
/// </para>
/// <para>
/// <see cref="Format"/> picks the container: classic TIFF by default, BigTIFF when the file would not fit 32-bit
/// offsets or when asked for. Pages are buffered and compressed before anything is written, so encoding a large
/// file needs memory for every compressed page at once.
/// </para>
/// </remarks>
public sealed class TiffEncoder : IImageEncoder
{
    /// <summary>
    /// Reported when a classic file's layout runs past what 32-bit offsets can address.
    /// </summary>
    private const string SizeLimitMessage =
        "The TIFF file would exceed classic TIFF's 4 GiB offset limit; encode with TiffFormatType.Auto or TiffFormatType.BigTiff to write BigTIFF.";

    /// <summary>
    /// The uncompressed size of the strips a page is split into when it is too large to buffer as one strip and
    /// <see cref="RowsPerStrip"/> is not set.
    /// </summary>
    private const long SplitStripBytes = 256L * 1024 * 1024;

    private const ushort TagStripOffsets = 273;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagIptc = 33723;
    private const ushort TagPhotoshop = 34377;

    /// <summary>
    /// Tags never copied from a source EXIF profile into a page directory: the sample layout and data pointers
    /// (written from the actual pixel data), sub-directory pointers (rebuilt), and the ICC/XMP/IPTC/Photoshop
    /// payloads (written from their own profiles).
    /// </summary>
    private static readonly HashSet<ushort> ReservedTags = new()
    {
        254, 255, 256, 257, 258, 259, 262, 266, 273, 277, 278, 279, 282, 283, 284, 292, 293, 296, 317, 320, 322, 323,
        324, 325, 330, 338, 339, 347, 512, 513, 514, 515, 517, 518, 519, 520, 521, 700, TagIptc, 34665, 34675, TagPhotoshop,
        34853, 40965,
    };

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

    /// <summary>
    /// The container version to write. Defaults to <see cref="TiffFormatType.Auto"/>: classic TIFF unless the file
    /// would exceed 4 GiB.
    /// </summary>
    public TiffFormatType Format { get; init; } = TiffFormatType.Auto;

    /// <summary>
    /// The number of rows in each strip; the last strip of a page may hold fewer. When left unset a page is one
    /// strip, unless its uncompressed data is too large for a single buffer, in which case it is split into strips
    /// of at most 256 MiB uncompressed. Strips are compressed independently (each CCITT strip starts a fresh coding
    /// line), so smaller strips cost some compression but let readers fetch part of a page.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">At encode time, when the value is zero or negative.</exception>
    public int? RowsPerStrip { get; init; }

    /// <summary>
    /// The largest file classic TIFF may produce: every offset must fit 32 bits. Lowered by tests to exercise the
    /// <see cref="TiffFormatType.Auto"/> switch and the <see cref="TiffFormatType.Classic"/> refusal without
    /// writing gigabytes.
    /// </summary>
    internal long ClassicSizeLimit { get; init; } = uint.MaxValue;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        if (this.RowsPerStrip is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.RowsPerStrip), this.RowsPerStrip, "Rows per strip must be positive.");
        }

        if (this.Format is not (TiffFormatType.Auto or TiffFormatType.Classic or TiffFormatType.BigTiff))
        {
            throw new ArgumentOutOfRangeException(nameof(this.Format), this.Format, "Unknown TIFF format type.");
        }

        PageLayout layout = this.ResolveLayout<TPixel>();

        int frameCount = image.Frames.Count;
        var pages = new Page[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            ImageFrame<TPixel> frame = image.Frames[f];
            int rowsPerStrip = this.ResolveRowsPerStrip(frame, in layout);
            List<byte[]> strips = this.CompressFrame(frame, in layout, rowsPerStrip);
            pages[f] = new Page(strips, BuildDirectory(image, f, in layout, strips, rowsPerStrip));
        }

        // Classic TIFF is kept whenever it can address the whole file, so the default output of every file that
        // fitted before is unchanged; BigTIFF takes over beyond that (Auto) or on request.
        bool bigTiff = this.Format == TiffFormatType.BigTiff;
        if (!bigTiff)
        {
            foreach (Page page in pages)
            {
                page.Classic = page.Directory.ToClassic();
            }

            if (Layout(pages, bigTiff: false) > this.ClassicSizeLimit)
            {
                if (this.Format == TiffFormatType.Classic)
                {
                    throw new NotSupportedException(SizeLimitMessage);
                }

                bigTiff = true;
            }
        }

        if (bigTiff)
        {
            _ = Layout(pages, bigTiff: true);
            WriteBigTiff(stream, pages);
        }
        else
        {
            WriteClassic(stream, pages);
        }
    }

    /// <summary>
    /// Assigns every strip and directory its file offset: <c>[header][strips of page 0][directory 0][strips of page 1]
    /// ...</c>, each word-aligned. Returns the total file length.
    /// </summary>
    private static long Layout(Page[] pages, bool bigTiff)
    {
        long cursor = bigTiff ? 16 : 8;
        foreach (Page page in pages)
        {
            for (int s = 0; s < page.Strips.Count; s++)
            {
                cursor = (cursor + 1) & ~1L; // Word-align.
                page.StripOffsets[s] = cursor;
                cursor += page.Strips[s].Length;
            }

            cursor = (cursor + 1) & ~1L;
            page.DirectoryOffset = cursor;
            cursor += bigTiff ? page.Directory.MeasureBig() : page.Classic!.Measure();
        }

        return cursor;
    }

    private static void WriteClassic(Stream stream, Page[] pages)
    {
        Span<byte> scratch = stackalloc byte[4];
        stream.WriteByte((byte)'I');
        stream.WriteByte((byte)'I');
        stream.WriteByte(42);
        stream.WriteByte(0);
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, pages.Length > 0 ? (uint)pages[0].DirectoryOffset : 0);
        stream.Write(scratch);

        long position = 8;
        for (int f = 0; f < pages.Length; f++)
        {
            Page page = pages[f];
            position = WriteStrips(stream, page, position);
            position = PadTo(stream, position, page.DirectoryOffset);

            // The strip tables now hold their real values; their sizes, and so the measured layout, are unchanged.
            IfdBuilder ifd = page.Classic!;
            AddClassicOffsets(ifd, TagStripOffsets, page.StripOffsets);
            uint next = f + 1 < pages.Length ? (uint)pages[f + 1].DirectoryOffset : 0;
            byte[] block = ifd.Serialize((uint)page.DirectoryOffset, next);
            stream.Write(block);
            position += block.Length;
        }
    }

    private static void WriteBigTiff(Stream stream, Page[] pages)
    {
        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 43); // BigTIFF version
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 8); // Bytes per offset
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0); // Reserved
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], pages.Length > 0 ? (ulong)pages[0].DirectoryOffset : 0);
        stream.Write(header);

        long position = 16;
        for (int f = 0; f < pages.Length; f++)
        {
            Page page = pages[f];
            position = WriteStrips(stream, page, position);
            position = PadTo(stream, position, page.DirectoryOffset);

            page.Directory.AddOffsets(TagStripOffsets, page.StripOffsets);
            long next = f + 1 < pages.Length ? pages[f + 1].DirectoryOffset : 0;
            var block = new byte[checked((int)page.Directory.MeasureBig())];
            page.Directory.SerializeBig(block, page.DirectoryOffset, next);
            stream.Write(block);
            position += block.Length;
        }
    }

    private static long WriteStrips(Stream stream, Page page, long position)
    {
        for (int s = 0; s < page.Strips.Count; s++)
        {
            position = PadTo(stream, position, page.StripOffsets[s]);
            stream.Write(page.Strips[s]);
            position += page.Strips[s].Length;
        }

        return position;
    }

    /// <summary>Stores a strip table in a classic directory: one inline LONG, or an external LONG array.</summary>
    private static void AddClassicOffsets(IfdBuilder ifd, ushort tag, long[] values)
    {
        var data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), checked((uint)values[i]));
        }

        ifd.Add(tag, ExifDataType.Long, (uint)values.Length, data);
    }

    /// <summary>
    /// Builds a page directory: layout entries from the pixel data, resolution from the image metadata, and the
    /// EXIF (IFD0 tags plus Exif/GPS/Interop sub-directories), ICC, XMP, IPTC and Photoshop profiles of the image
    /// (first page) or of the frame (further pages). Strip offsets are placeholders until the file is laid out.
    /// </summary>
    private static TiffDirectory BuildDirectory<TPixel>(
        Image<TPixel> image, int frameIndex, in PageLayout layout, List<byte[]> strips, int rowsPerStrip)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> frame = image.Frames[frameIndex];
        ImageMetadata metadata = image.Metadata;
        ExifProfile? exif = frameIndex == 0 ? metadata.ExifProfile : frame.Metadata.ExifProfile;
        IccProfile? icc = frameIndex == 0 ? metadata.IccProfile : frame.Metadata.IccProfile;
        XmpProfile? xmp = frameIndex == 0 ? metadata.XmpProfile : frame.Metadata.XmpProfile;
        IptcProfile? iptc = frameIndex == 0 ? metadata.IptcProfile : frame.Metadata.IptcProfile;
        PhotoshopResources? photoshop = frameIndex == 0 ? metadata.PhotoshopResources : frame.Metadata.PhotoshopResources;

        var ifd = new TiffDirectory();
        if (exif is not null)
        {
            foreach (IExifValue value in exif.Values)
            {
                if (value.Tag.Ifd == ExifIfd.Ifd0 && !ReservedTags.Contains(value.Tag.Id))
                {
                    ifd.TryAdd(value);
                }
            }

            TiffDirectory exifIfd = TiffDirectory.FromExif(exif, ExifIfd.Exif);
            TiffDirectory interop = TiffDirectory.FromExif(exif, ExifIfd.Interop);
            TiffDirectory gps = TiffDirectory.FromExif(exif, ExifIfd.Gps);
            if (interop.Count > 0)
            {
                exifIfd.AddSubDirectory(0xA005, interop);
            }

            if (exifIfd.Count > 0)
            {
                ifd.AddSubDirectory(0x8769, exifIfd);
            }

            if (gps.Count > 0)
            {
                ifd.AddSubDirectory(0x8825, gps);
            }
        }

        // Layout entries (tags are kept in ascending order when the directory is serialized).
        int spp = layout.SamplesPerPixel;
        ifd.AddLong(256, (uint)frame.Width); // ImageWidth
        ifd.AddLong(257, (uint)frame.Height); // ImageLength
        var bits = new ushort[spp];
        Array.Fill(bits, (ushort)layout.BitsPerSample);
        ifd.AddShorts(258, bits); // BitsPerSample
        ifd.AddShort(259, (ushort)layout.CompressionTag); // Compression
        ifd.AddShort(262, (ushort)layout.Photometric); // PhotometricInterpretation
        ifd.AddOffsets(TagStripOffsets, new long[strips.Count]); // StripOffsets (patched once the layout is known)
        ifd.AddShort(277, (ushort)spp); // SamplesPerPixel
        ifd.AddLong(278, (uint)rowsPerStrip); // RowsPerStrip
        var byteCounts = new long[strips.Count];
        for (int s = 0; s < strips.Count; s++)
        {
            byteCounts[s] = strips[s].Length;
        }

        ifd.AddOffsets(TagStripByteCounts, byteCounts); // StripByteCounts
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

        byte[]? iim = iptc?.ToByteArray();
        if (iim is { Length: > 0 })
        {
            // IPTC-NAA is typed LONG, as Photoshop and libtiff write it: the block's own bytes, zero-padded to a
            // multiple of four. Readers take the bytes as they are stored, not as integers.
            var padded = new byte[(iim.Length + 3) & ~3];
            iim.CopyTo(padded, 0);
            ifd.Add(TagIptc, ExifDataType.Long, padded.Length / 4, padded);
        }

        PhotoshopResources? block = PhotoshopResources.Compose(photoshop, iptc, iim, xmp is not null, embedIptc: false);
        if (block is not null)
        {
            ifd.AddBytes(TagPhotoshop, ExifDataType.Byte, block.ToByteArray());
        }

        return ifd;
    }

    private static long PadTo(Stream stream, long position, long target)
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

    /// <summary>The bytes one row of a page occupies before compression.</summary>
    private static long RowBytes(int width, in PageLayout layout)
        => (((long)width * layout.SamplesPerPixel * layout.BitsPerSample) + 7) / 8;

    /// <summary>
    /// The rows per strip for a page: the requested count (capped at the page height), or the whole page; either is
    /// reduced so that one strip's uncompressed bytes fit a single buffer.
    /// </summary>
    private int ResolveRowsPerStrip<TPixel>(ImageFrame<TPixel> frame, in PageLayout layout)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        // Measured as longs: the row stride and the page total both overflow an int well inside the dimensions an
        // image can legitimately have.
        long rowBytes = RowBytes(frame.Width, in layout);
        if (rowBytes > Array.MaxLength)
        {
            throw new NotSupportedException(
                $"TIFF cannot buffer one {frame.Width}-pixel row at {layout.BitsPerSample * layout.SamplesPerPixel} bits per pixel.");
        }

        int height = frame.Height;
        long rows = this.RowsPerStrip is int requested ? Math.Min(requested, height) : height;
        if (rows * rowBytes > Array.MaxLength)
        {
            rows = Math.Max(1, (this.RowsPerStrip is null ? SplitStripBytes : Array.MaxLength) / rowBytes);
        }

        return (int)Math.Max(1, rows);
    }

    private List<byte[]> CompressFrame<TPixel>(ImageFrame<TPixel> frame, in PageLayout layout, int rowsPerStrip)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int spp = layout.SamplesPerPixel;
        int rowBytes = (int)RowBytes(width, in layout);
        var rgbaRow = new Rgba32[width];
        var strips = new List<byte[]>((height + rowsPerStrip - 1) / rowsPerStrip);

        // A WhiteIsZero page stores the complement of the grey level, which also lets a bilevel scan code its
        // background as the white runs the fax schemes compress best.
        bool invert = layout.Photometric == 0;

        for (int top = 0; top < height; top += rowsPerStrip)
        {
            int rows = Math.Min(rowsPerStrip, height - top);
            var raw = new byte[rowBytes * rows];
            for (int r = 0; r < rows; r++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(top + r), rgbaRow);
                Span<byte> row = raw.AsSpan(r * rowBytes, rowBytes);
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

            strips.Add(this.CompressStrip(raw, width, rows, rowBytes, in layout));
        }

        return strips;
    }

    /// <summary>Codes one strip's uncompressed rows with the page's compression scheme.</summary>
    private byte[] CompressStrip(byte[] raw, int width, int rows, int rowBytes, in PageLayout layout)
    {
        if (layout.Ccitt is TiffCcittScheme scheme)
        {
            return TiffCcitt.Encode(raw, width, rows, scheme);
        }

        if (layout.ApplyPredictor)
        {
            ApplyHorizontalDifferencing(raw, rowBytes, layout.SamplesPerPixel);
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

    /// <summary>One page being written: its compressed strips, its directory, and the offsets the layout assigns.</summary>
    private sealed class Page
    {
        public Page(List<byte[]> strips, TiffDirectory directory)
        {
            this.Strips = strips;
            this.Directory = directory;
            this.StripOffsets = new long[strips.Count];
        }

        public List<byte[]> Strips { get; }

        public TiffDirectory Directory { get; }

        /// <summary>The directory converted for classic serialization, once the classic layout is being measured.</summary>
        public IfdBuilder? Classic { get; set; }

        public long[] StripOffsets { get; }

        public long DirectoryOffset { get; set; }
    }

    /// <summary>
    /// A page directory independent of the container version. Classic TIFF is serialized by converting it to the
    /// shared <see cref="IfdBuilder"/> (so classic output is exactly what it has always been); BigTIFF is serialized
    /// here, with 8-byte entry counts, 20-byte entries, 8-byte value fields, LONG8 strip tables and IFD8 pointers.
    /// </summary>
    private sealed class TiffDirectory
    {
        private readonly Dictionary<ushort, Field> fields = new();
        private readonly Dictionary<ushort, TiffDirectory> subDirectories = new();

        /// <summary>The number of entries, including sub-directory pointers.</summary>
        public int Count => this.fields.Count + this.subDirectories.Count;

        /// <summary>Builds the directory holding a profile's values for one part (Exif, GPS, Interop).</summary>
        public static TiffDirectory FromExif(ExifProfile profile, ExifIfd part)
        {
            var directory = new TiffDirectory();
            foreach (IExifValue value in profile.Values)
            {
                if (value.Tag.Ifd == part && value.Tag.Id is not (0x8769 or 0x8825 or 0xA005))
                {
                    directory.TryAdd(value);
                }
            }

            return directory;
        }

        /// <summary>Adds (or replaces) an entry with pre-encoded little-endian data.</summary>
        public void Add(ushort tag, ExifDataType type, long count, byte[] data)
        {
            this.subDirectories.Remove(tag);
            this.fields[tag] = new Field(type, count, data, null);
        }

        /// <summary>Adds a strip table: LONG values in classic TIFF, LONG8 values in BigTIFF.</summary>
        public void AddOffsets(ushort tag, long[] values)
        {
            this.subDirectories.Remove(tag);
            this.fields[tag] = new Field(ExifDataType.Long, values.Length, Array.Empty<byte>(), values);
        }

        public void AddShort(ushort tag, ushort value)
        {
            var data = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
            this.Add(tag, ExifDataType.Short, 1, data);
        }

        public void AddShorts(ushort tag, ushort[] values)
        {
            var data = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), values[i]);
            }

            this.Add(tag, ExifDataType.Short, values.Length, data);
        }

        public void AddLong(ushort tag, uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            this.Add(tag, ExifDataType.Long, 1, data);
        }

        public void AddRational(ushort tag, Rational value)
        {
            var data = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value.Numerator);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), value.Denominator);
            this.Add(tag, ExifDataType.Rational, 1, data);
        }

        public void AddBytes(ushort tag, ExifDataType type, byte[] data) => this.Add(tag, type, data.Length, data);

        /// <summary>Encodes and adds an <see cref="IExifValue"/> exactly as <see cref="IfdBuilder.TryAdd"/> does; false when it cannot be represented.</summary>
        public bool TryAdd(IExifValue value)
        {
            if (!ExifValueEncoder.TryEncode(value, out ExifDataType type, out uint count, out byte[]? data) || data is null)
            {
                return false;
            }

            this.Add(value.Tag.Id, type, count, data);
            return true;
        }

        /// <summary>Attaches a sub-directory referenced by a pointer entry with the given tag.</summary>
        public void AddSubDirectory(ushort pointerTag, TiffDirectory sub)
        {
            this.fields.Remove(pointerTag);
            this.subDirectories[pointerTag] = sub;
        }

        /// <summary>Converts to the classic serializer. Strip-table values must fit 32 bits.</summary>
        public IfdBuilder ToClassic()
        {
            var ifd = new IfdBuilder();
            foreach ((ushort tag, Field field) in this.fields)
            {
                if (field.Offsets is long[] values)
                {
                    AddClassicOffsets(ifd, tag, values);
                }
                else
                {
                    ifd.Add(tag, field.Type, checked((uint)field.Count), field.Data);
                }
            }

            foreach ((ushort tag, TiffDirectory sub) in this.subDirectories)
            {
                ifd.AddSubIfd(tag, sub.ToClassic());
            }

            return ifd;
        }

        /// <summary>The number of bytes <see cref="SerializeBig"/> produces, including nested sub-directories.</summary>
        public long MeasureBig()
        {
            long size = 8 + (20L * this.Count) + 8;
            foreach (Field field in this.fields.Values)
            {
                long length = field.LengthBig;
                if (length > 8)
                {
                    size += (length + 1) & ~1L;
                }
            }

            foreach (TiffDirectory sub in this.subDirectories.Values)
            {
                size += sub.MeasureBig();
            }

            return size;
        }

        /// <summary>
        /// Serializes the directory as BigTIFF for placement at absolute file offset <paramref name="baseOffset"/>:
        /// the entry table, then the external values, then the sub-directories, all addressed absolutely.
        /// </summary>
        public void SerializeBig(Span<byte> buffer, long baseOffset, long nextDirectory)
        {
            var sorted = new List<(ushort Tag, Field? Field, TiffDirectory? Sub)>(this.Count);
            foreach ((ushort tag, Field field) in this.fields)
            {
                sorted.Add((tag, field, null));
            }

            foreach ((ushort tag, TiffDirectory sub) in this.subDirectories)
            {
                sorted.Add((tag, null, sub));
            }

            sorted.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));

            int count = sorted.Count;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)count);
            int entryPos = 8;
            int dataPos = 8 + (20 * count) + 8;
            long externalTotal = 0;
            foreach ((ushort _, Field? field, TiffDirectory? _) in sorted)
            {
                if (field is not null && field.LengthBig > 8)
                {
                    externalTotal += (field.LengthBig + 1) & ~1L;
                }
            }

            int subPos = checked((int)(dataPos + externalTotal));
            foreach ((ushort tag, Field? field, TiffDirectory? sub) in sorted)
            {
                Span<byte> entry = buffer.Slice(entryPos, 20);
                BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
                if (field is not null)
                {
                    byte[] data = field.Offsets is long[] values ? Long8Bytes(values) : field.Data;
                    ExifDataType type = field.Offsets is null ? field.Type : ExifDataType.Long8;
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)type);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry[4..], (ulong)field.Count);
                    if (data.Length <= 8)
                    {
                        data.CopyTo(entry[12..]);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(entry[12..], (ulong)(baseOffset + dataPos));
                        data.CopyTo(buffer[dataPos..]);
                        dataPos += (data.Length + 1) & ~1;
                    }
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)ExifDataType.Ifd8);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry[4..], 1);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry[12..], (ulong)(baseOffset + subPos));
                    int subSize = checked((int)sub!.MeasureBig());
                    sub.SerializeBig(buffer.Slice(subPos, subSize), baseOffset + subPos, 0);
                    subPos += subSize;
                }

                entryPos += 20;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(buffer[entryPos..], (ulong)nextDirectory);
        }

        private static byte[] Long8Bytes(long[] values)
        {
            var data = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(i * 8), (ulong)values[i]);
            }

            return data;
        }

        /// <summary>One entry: pre-encoded data, or (for the strip tables) values whose width depends on the container.</summary>
        private sealed record Field(ExifDataType Type, long Count, byte[] Data, long[]? Offsets)
        {
            public long LengthBig => this.Offsets is null ? this.Data.Length : 8L * this.Offsets.Length;
        }
    }
}
