using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tiff;

/// <summary>
/// Decodes TIFF images: multi-page (each page becomes a frame), both byte orders, chunky and planar
/// (PlanarConfiguration 2) sample layouts, strips and tiles, and None/LZW/Deflate/PackBits, CCITT Modified
/// Huffman, Group 3 and Group 4, and JPEG compression.
/// </summary>
/// <remarks>
/// <para>
/// Samples may be 1, 2, 4, 8, 16 or 32 bits wide in the unsigned, signed and floating-point sample formats.
/// Unsigned 16-bit samples are kept at full width when the requested pixel format can hold more than 8 bits
/// per component (Rgb48, Rgba64, L16, La32, RgbaVector); otherwise, and for every wider or non-unsigned
/// sample, the value is reduced to 8 bits (unsigned samples keep their most significant byte, signed samples
/// are shifted into the same range first, and floating-point samples are read as the 0..1 range imaging data
/// uses). Horizontal differencing (predictor 2) is undone for 8- and 16-bit samples.
/// </para>
/// <para>
/// The photometric interpretations read are WhiteIsZero and BlackIsZero (with an optional alpha sample), RGB
/// and RGBA, palette colour, the transparency mask, Separated (CMYK, with or without an alpha sample), YCbCr,
/// CIELab and ICCLab. Uncompressed YCbCr must be unsubsampled; subsampled YCbCr occurs in JPEG-compressed
/// pages, where the JPEG decoder resolves it.
/// </para>
/// <para>
/// Old-style JPEG (compression 6), JBIG, the CCITT uncompressed-mode extension, the floating-point predictor
/// and planar pages with sub-byte samples are reported as <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class TiffDecoder : IImageDecoder
{
    private const int TagImageWidth = 256;
    private const int TagImageLength = 257;
    private const int TagBitsPerSample = 258;
    private const int TagCompression = 259;
    private const int TagPhotometric = 262;
    private const int TagFillOrder = 266;
    private const int TagStripOffsets = 273;
    private const int TagSamplesPerPixel = 277;
    private const int TagRowsPerStrip = 278;
    private const int TagStripByteCounts = 279;
    private const int TagPlanarConfiguration = 284;
    private const int TagPredictor = 317;
    private const int TagColorMap = 320;
    private const int TagTileWidth = 322;
    private const int TagTileLength = 323;
    private const int TagTileOffsets = 324;
    private const int TagTileByteCounts = 325;
    private const int TagExtraSamples = 338;
    private const int TagSampleFormat = 339;
    private const int TagT4Options = 292;
    private const int TagT6Options = 293;
    private const int TagYCbCrSubSampling = 530;
    private const int TagJpegTables = 347;

    private const int CompressionNone = 1;
    private const int CompressionCcittRle = 2;
    private const int CompressionCcittGroup3 = 3;
    private const int CompressionCcittGroup4 = 4;
    private const int CompressionLzw = 5;
    private const int CompressionJpeg = 7;
    private const int CompressionDeflate = 8;
    private const int CompressionDeflateLegacy = 32946;
    private const int CompressionPackBits = 32773;

    private const int TagXmp = 700;
    private const int TagIccProfile = 34675;

    /// <summary>
    /// Tags that describe the sample layout of a page (or point at its data). They are exposed through
    /// <see cref="TiffFrameMetadata"/> rather than the page's <see cref="ExifProfile"/>, because they would be
    /// wrong for the pixels once the image is re-encoded.
    /// </summary>
    private static readonly HashSet<int> LayoutTags = new()
    {
        254, 255, TagImageWidth, TagImageLength, TagBitsPerSample, TagCompression, TagPhotometric, TagFillOrder,
        TagStripOffsets, TagSamplesPerPixel, TagRowsPerStrip, TagStripByteCounts, TagPlanarConfiguration,
        TagPredictor, TagColorMap, TagTileWidth, TagTileLength, TagTileOffsets, TagTileByteCounts,
        TagExtraSamples, TagSampleFormat, TagT4Options, TagT6Options, TagYCbCrSubSampling,
        330, 347, 512, 513, 514, 515, 517, 518, 519, 520, 521, 529, 532, TagXmp, TagIccProfile,
    };

    /// <summary>The only tags the decoder materializes; everything else is skipped without allocation.</summary>
    private static readonly HashSet<int> KnownTags = new()
    {
        TagImageWidth, TagImageLength, TagBitsPerSample, TagCompression, TagPhotometric, TagFillOrder,
        TagStripOffsets, TagSamplesPerPixel, TagRowsPerStrip, TagStripByteCounts, TagPlanarConfiguration,
        TagPredictor, TagColorMap, TagTileWidth, TagTileLength, TagTileOffsets, TagTileByteCounts,
        TagExtraSamples, TagSampleFormat, TagT4Options, TagT6Options, TagYCbCrSubSampling, TagJpegTables,
    };

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
            throw DecoderGuard.Wrap("TIFF", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool bigEndian = ValidateHeader(data);
        var frames = new List<ImageFrame<TPixel>>();
        var visited = new HashSet<long>();

        long ifdOffset = ReadU32(data, 4, bigEndian);
        while (ifdOffset != 0 && visited.Add(ifdOffset) && frames.Count < options.MaxFrames)
        {
            frames.Add(DecodeFrame<TPixel>(data, CheckedIfdOffset(ifdOffset, data.Length), bigEndian, options));
            ifdOffset = NextIfdOffset(data, (int)ifdOffset, bigEndian);
        }

        if (frames.Count == 0)
        {
            throw new InvalidImageContentException("TIFF image contains no pages.");
        }

        return new Image<TPixel>(frames, CreateImageMetadata(frames[0].Metadata, bigEndian));
    }

    /// <summary>Builds the image-level metadata from the first page: profiles are copied, the resolution comes from the page's tags.</summary>
    private static ImageMetadata CreateImageMetadata(ImageFrameMetadata firstPage, bool bigEndian)
    {
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Tiff };
        metadata.GetTiffMetadata().ByteOrder = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
        metadata.ExifProfile = firstPage.ExifProfile?.DeepClone();
        metadata.IccProfile = firstPage.IccProfile?.DeepClone();
        metadata.XmpProfile = firstPage.XmpProfile?.DeepClone();
        if (metadata.ExifProfile is not null)
        {
            metadata.ApplyExifResolution(metadata.ExifProfile);
        }

        return metadata;
    }

    /// <summary>
    /// Reads a page's directory into its frame metadata: the sample layout into <see cref="TiffFrameMetadata"/>,
    /// ICC/XMP into their profiles, everything else (with the Exif/GPS sub-directories) into the page's EXIF profile.
    /// Malformed ancillary entries are skipped; only the size caps abort the decode.
    /// </summary>
    private static void PopulateFrameMetadata(
        ReadOnlySpan<byte> data, int ifdOffset, bool bigEndian, Dictionary<int, long[]> tags, ImageFrameMetadata frameMetadata)
    {
        TiffFrameMetadata tiff = frameMetadata.GetTiffMetadata();
        long[] bps = tags.TryGetValue(TagBitsPerSample, out long[]? b) ? b : new long[] { 1 };
        var bitsPerSample = new ushort[bps.Length];
        for (int i = 0; i < bps.Length; i++)
        {
            bitsPerSample[i] = (ushort)Math.Clamp(bps[i], 0, ushort.MaxValue);
        }

        tiff.BitsPerSample = bitsPerSample;
        tiff.SamplesPerPixel = (ushort)Math.Clamp(GetSingle(tags, TagSamplesPerPixel, bps.Length), 0, ushort.MaxValue);
        tiff.Compression = (TiffCompressionMethod)Math.Clamp(GetSingle(tags, TagCompression, CompressionNone), 0, ushort.MaxValue);
        tiff.PhotometricInterpretation = (TiffPhotometricInterpretation)Math.Clamp(GetSingle(tags, TagPhotometric, 1), 0, ushort.MaxValue);
        tiff.Predictor = (TiffPredictor)Math.Clamp(GetSingle(tags, TagPredictor, 1), 0, ushort.MaxValue);
        tiff.PlanarConfiguration = (TiffPlanarConfiguration)Math.Clamp(GetSingle(tags, TagPlanarConfiguration, 1), 0, ushort.MaxValue);
        tiff.Tiled = tags.ContainsKey(TagTileOffsets);
        tiff.RowsPerStrip = !tiff.Tiled && tags.TryGetValue(TagRowsPerStrip, out long[]? rps) && rps.Length > 0 && rps[0] > 0 && rps[0] <= uint.MaxValue
            ? (uint)rps[0]
            : null;

        List<IExifValue> values;
        try
        {
            values = ExifReader.ReadDirectoryTree(data, ifdOffset, bigEndian);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            return;
        }

        var profile = new ExifProfile();
        foreach (IExifValue value in values)
        {
            int id = value.Tag.Id;
            if (value.Tag.Ifd == ExifIfd.Ifd0)
            {
                if (id == TagIccProfile)
                {
                    if (value.GetValue() is byte[] icc && icc.Length > 0)
                    {
                        frameMetadata.IccProfile = new IccProfile(icc);
                    }

                    continue;
                }

                if (id == TagXmp)
                {
                    if (value.GetValue() is byte[] xmp && xmp.Length > 0)
                    {
                        frameMetadata.XmpProfile = new XmpProfile(xmp);
                    }

                    continue;
                }

                if (LayoutTags.Contains(id))
                {
                    continue;
                }
            }

            profile.AddParsed(value);
        }

        if (profile.Values.Count > 0)
        {
            frameMetadata.ExifProfile = profile;
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
            throw DecoderGuard.Wrap("TIFF", ex);
        }
    }

    private static ImageInfo IdentifyCore(ReadOnlySpan<byte> data)
    {
        bool bigEndian = ValidateHeader(data);
        long firstIfd = ReadU32(data, 4, bigEndian);
        Dictionary<int, long[]> tags = ReadTags(data, CheckedIfdOffset(firstIfd, data.Length), bigEndian);

        long width = GetSingle(tags, TagImageWidth, 0);
        long height = GetSingle(tags, TagImageLength, 0);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidImageContentException("Invalid TIFF page dimensions.");
        }

        long[] bps = tags.TryGetValue(TagBitsPerSample, out long[]? b) ? b : new long[] { 1 };
        long spp = GetSingle(tags, TagSamplesPerPixel, bps.Length);
        long bitsPerPixel = Math.Clamp(bps[0], 0, 64) * Math.Clamp(spp, 0, 64);

        int frameCount = 0;
        var visited = new HashSet<long>();
        long ifdOffset = firstIfd;
        while (ifdOffset != 0 && visited.Add(ifdOffset) && frameCount < int.MaxValue)
        {
            frameCount++;
            ifdOffset = NextIfdOffset(data, CheckedIfdOffset(ifdOffset, data.Length), bigEndian);
        }

        var firstPage = new ImageFrameMetadata();
        PopulateFrameMetadata(data, CheckedIfdOffset(firstIfd, data.Length), bigEndian, tags, firstPage);
        return new ImageInfo((int)width, (int)height, (int)bitsPerPixel, frameCount, ImageFormat.Tiff, CreateImageMetadata(firstPage, bigEndian));
    }

    private static int CheckedIfdOffset(long offset, int dataLength)
        => offset >= 0 && offset + 2 <= dataLength
            ? (int)offset
            : throw new InvalidImageContentException("TIFF directory offset is out of range.");

    private static bool ValidateHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new InvalidImageContentException("TIFF header is truncated.");
        }

        bool bigEndian = data[0] == 0x4D;
        int magic = (int)ReadU16(data, 2, bigEndian);
        if (magic != 42)
        {
            throw new InvalidImageContentException("Invalid TIFF magic number.");
        }

        return bigEndian;
    }

    private static ImageFrame<TPixel> DecodeFrame<TPixel>(ReadOnlySpan<byte> data, int ifdOffset, bool bigEndian, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Dictionary<int, long[]> tags = ReadTags(data, ifdOffset, bigEndian);

        long widthTag = GetSingle(tags, TagImageWidth, 0);
        long heightTag = GetSingle(tags, TagImageLength, 0);
        if (widthTag <= 0 || heightTag <= 0 || widthTag > int.MaxValue || heightTag > int.MaxValue)
        {
            throw new InvalidImageContentException("Invalid TIFF page dimensions.");
        }

        int width = (int)widthTag;
        int height = (int)heightTag;
        options.EnsureFrameWithinLimits(width, height, "TIFF");

        long[] bpsValues = tags.TryGetValue(TagBitsPerSample, out long[]? bps) ? bps : new long[] { 1 };
        long spp = GetSingle(tags, TagSamplesPerPixel, bpsValues.Length);
        int compression = (int)GetSingle(tags, TagCompression, CompressionNone);
        int photometric = (int)GetSingle(tags, TagPhotometric, 1);
        int predictor = (int)GetSingle(tags, TagPredictor, 1);
        int planarConfig = (int)GetSingle(tags, TagPlanarConfiguration, 1);
        int fillOrder = (int)GetSingle(tags, TagFillOrder, 1);
        int sampleFormat = (int)GetSingle(tags, TagSampleFormat, 1);
        int extraSamples = (int)GetSingle(tags, TagExtraSamples, -1);
        int faxOptions = (int)GetSingle(tags, compression == CompressionCcittGroup4 ? TagT6Options : TagT4Options, 0);
        bool subsampled = tags.TryGetValue(TagYCbCrSubSampling, out long[]? sub) && sub.Length >= 2 && (sub[0] != 1 || sub[1] != 1);

        Layout layout = ValidateLayout(
            bpsValues, spp, compression, photometric, predictor, planarConfig, fillOrder, sampleFormat, extraSamples,
            faxOptions, subsampled);
        int bits = layout.BitsPerSample;
        int samples = layout.SamplesPerPixel;
        if (compression == CompressionJpeg)
        {
            // The JPEG decoder resolves the colour transform itself - YCbCr with any subsampling, and Adobe's
            // inverted CMYK - so a JPEG-coded page is read back as plain grey or RGB and its photometric
            // interpretation plays no further part.
            photometric = samples == 1 ? 1 : 2;
            layout = layout with
            {
                Jpeg = new TiffJpegState(
                    tags.TryGetValue(TagJpegTables, out long[]? jpegTables) ? ToBytes(jpegTables) : null, options, samples),
            };
        }

        long rowBytesLong = (((long)width * bits * samples) + 7) / 8;
        if (rowBytesLong > int.MaxValue || rowBytesLong * height > int.MaxValue)
        {
            throw new InvalidImageContentException("TIFF page is too large to decode.");
        }

        int rowBytes = (int)rowBytesLong;
        bool tiled = tags.ContainsKey(TagTileOffsets);
        byte[] raw = layout.Planar
            ? ReadPlanes(data, tags, width, height, rowBytes, layout, bigEndian, options, tiled)
            : tiled
                ? ReadTiles(data, tags, width, height, rowBytes, layout, bigEndian, options)
                : ReadStrips(data, tags, width, height, rowBytes, layout, bigEndian);

        if (fillOrder == 2 && bits < 8 && layout.Ccitt is null)
        {
            ReverseBits(raw);
        }

        if (TiffColor.NeedsReduction(bits, sampleFormat, photometric))
        {
            // Wide, signed and floating-point samples become plain bytes once, so the row conversion below only
            // ever sees the 1- to 16-bit unsigned layouts it has fast paths for.
            raw = TiffColor.ReduceToBytes(raw, width, height, rowBytes, bits, sampleFormat, samples, bigEndian);
            rowBytes = width * samples;
            layout = layout with { BitsPerSample = 8 };
        }

        Rgba32[]? palette = photometric == 3 ? ReadPalette(tags, bits) : null;

        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(width, height);
        var rgbaRow = new Rgba32[width];

        // 16-bit samples only survive intact when the requested pixel format is wide enough to hold
        // them; every other combination keeps the 8-bit path unchanged. Palette images are excluded
        // because their colour map is read at 8 bits per component.
        bool wideSamples = bits == 16 && photometric != 3 && PixelOps.IsHighPrecision<TPixel>();
        Rgba64[]? wideRow = wideSamples ? new Rgba64[width] : null;

        // When the page's byte layout already is one of the pixel formats, the row becomes a bulk copy or
        // shuffle instead of a per-pixel round trip through Rgba32.
        TiffRowLayout fast = wideSamples ? TiffRowLayout.General : ClassifyRowLayout(layout, photometric);
        TPixel[]? paletteLut = fast == TiffRowLayout.Palette8 ? BuildPaletteLut<TPixel>(palette!) : null;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = raw.AsSpan(y * rowBytes, rowBytes);
            Span<TPixel> destination = frame.GetRowSpan(y);
            switch (fast)
            {
                case TiffRowLayout.Rgb24:
                    PixelOps.Convert<Rgb24, TPixel>(MemoryMarshal.Cast<byte, Rgb24>(row[..(width * 3)]), destination);
                    continue;
                case TiffRowLayout.Rgba32:
                    PixelOps.Convert<Rgba32, TPixel>(MemoryMarshal.Cast<byte, Rgba32>(row[..(width * 4)]), destination);
                    continue;
                case TiffRowLayout.L8:
                    PixelOps.Convert<L8, TPixel>(MemoryMarshal.Cast<byte, L8>(row[..width]), destination);
                    continue;
                case TiffRowLayout.Palette8:
                    for (int x = 0; x < width; x++)
                    {
                        int index = row[x];
                        destination[x] = index < paletteLut!.Length
                            ? paletteLut[index]
                            : throw new InvalidImageContentException("TIFF palette index out of range.");
                    }

                    continue;
            }

            if (wideSamples)
            {
                ConvertRow16(row, wideRow!, width, layout, photometric, bigEndian);
                PixelOps.Convert<Rgba64, TPixel>(wideRow, destination);
            }
            else
            {
                ConvertRow(row, rgbaRow, width, layout, photometric, palette, bigEndian);
                PixelOps.FromRgba32<TPixel>(rgbaRow, destination);
            }
        }

        PopulateFrameMetadata(data, ifdOffset, bigEndian, tags, frame.Metadata);
        return frame;
    }

    /// <summary>Checks the sample layout against what the decoder implements, separating malformed from unsupported.</summary>
    private static Layout ValidateLayout(
        long[] bpsValues, long spp, int compression, int photometric, int predictor, int planarConfig, int fillOrder,
        int sampleFormat, int extraSamples, int faxOptions, bool subsampled)
    {
        if (spp <= 0 || spp > 5)
        {
            throw new NotSupportedException($"TIFF images with {spp} samples per pixel are not supported.");
        }

        long bits = bpsValues[0];
        for (int i = 1; i < bpsValues.Length; i++)
        {
            if (bpsValues[i] != bits)
            {
                throw new NotSupportedException("TIFF images whose samples have different bit depths are not supported.");
            }
        }

        if (compression == CompressionJpeg)
        {
            return ValidateJpeg(bits, spp, photometric, planarConfig);
        }

        if (bits is not (1 or 2 or 4 or 8 or 16 or 32))
        {
            throw new NotSupportedException($"TIFF images with {bits}-bit samples are not supported (supported: 1, 2, 4, 8, 16, 32).");
        }

        if (planarConfig is not (1 or 2))
        {
            throw new NotSupportedException($"TIFF planar configuration {planarConfig} is not supported.");
        }

        if (planarConfig == 2 && bits < 8)
        {
            throw new NotSupportedException("TIFF planar configuration 2 is only supported for whole-byte samples.");
        }

        if (sampleFormat is not (1 or 2 or 3 or 4))
        {
            throw new NotSupportedException($"TIFF sample format {sampleFormat} is not supported.");
        }

        if (sampleFormat is 2 or 3 && bits is not (16 or 32))
        {
            throw new NotSupportedException($"TIFF sample format {sampleFormat} is only supported for 16- and 32-bit samples.");
        }

        TiffCcittOptions? ccitt = null;
        switch (compression)
        {
            case CompressionNone or CompressionLzw or CompressionDeflate or CompressionDeflateLegacy or CompressionPackBits:
                break;
            case CompressionCcittRle or CompressionCcittGroup3 or CompressionCcittGroup4:
                ccitt = ValidateCcitt(compression, bits, spp, fillOrder, faxOptions);
                break;
            case 6:
                throw new NotSupportedException("Old-style JPEG-compressed TIFF (compression 6) is not supported.");
            default:
                throw new NotSupportedException($"TIFF compression {compression} is not supported.");
        }

        if (fillOrder == 2 && compression != CompressionNone && ccitt is null)
        {
            throw new NotSupportedException("TIFF FillOrder 2 (LSB-first) is only supported for uncompressed and CCITT-coded data.");
        }

        if (predictor is not (1 or 2))
        {
            throw new NotSupportedException($"TIFF predictor {predictor} is not supported.");
        }

        bool supported = photometric switch
        {
            0 or 1 => spp is 1 or 2, // Bilevel/grayscale, optionally with an alpha sample.
            2 => spp is 3 or 4 && bits >= 8, // RGB / RGBA.
            3 => spp == 1 && bits is 1 or 2 or 4 or 8, // Palette.
            4 => spp == 1 && bits == 1, // Transparency mask: a bilevel page, imaged like WhiteIsZero.
            TiffColor.PhotometricSeparated => spp is 4 or 5 && bits >= 8, // CMYK, optionally with an alpha sample.
            TiffColor.PhotometricYCbCr => spp == 3 && bits >= 8 && !subsampled,
            TiffColor.PhotometricCieLab or TiffColor.PhotometricIccLab => spp == 3 && bits >= 8,
            _ => throw new NotSupportedException($"TIFF photometric interpretation {photometric} is not supported."),
        };
        if (!supported)
        {
            throw new NotSupportedException(
                $"TIFF photometric interpretation {photometric} with {spp} samples of {bits} bits is not supported.");
        }

        // A second gray sample is alpha only when ExtraSamples says so; a fourth RGB sample is treated as alpha
        // even when unspecified, which is what most readers do. A fourth Separated sample is the black ink, so
        // only a fifth one can be alpha there.
        bool hasAlpha = photometric == TiffColor.PhotometricSeparated
            ? spp == 5
            : spp == 4 || (spp == 2 && extraSamples is 1 or 2);
        bool applyPredictor = predictor == 2 && compression is CompressionLzw or CompressionDeflate or CompressionDeflateLegacy;
        if (applyPredictor && bits is not (8 or 16))
        {
            throw new NotSupportedException("TIFF predictor 2 is only supported for 8- and 16-bit samples.");
        }

        return new Layout((int)bits, (int)spp, hasAlpha, compression, applyPredictor, ccitt, planarConfig == 2, null);
    }

    /// <summary>
    /// Checks a JPEG-compressed page (compression 7). Its segments always decode to 8-bit grey or RGB, so the
    /// page's own sample layout only has to be plausible.
    /// </summary>
    private static Layout ValidateJpeg(long bits, long spp, int photometric, int planarConfig)
    {
        if (bits != 8)
        {
            throw new NotSupportedException($"JPEG-compressed TIFF pages must carry 8-bit samples, not {bits}-bit ones.");
        }

        if (planarConfig != 1)
        {
            throw new NotSupportedException("Planar JPEG-compressed TIFF pages are not supported.");
        }

        if (spp is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"JPEG-compressed TIFF pages with {spp} samples per pixel are not supported.");
        }

        if (photometric is not (0 or 1 or 2 or TiffColor.PhotometricSeparated or TiffColor.PhotometricYCbCr))
        {
            throw new NotSupportedException(
                $"JPEG-compressed TIFF pages with photometric interpretation {photometric} are not supported.");
        }

        return new Layout((int)bits, spp == 1 ? 1 : 3, false, CompressionJpeg, false, null, false, null);
    }

    /// <summary>
    /// Maps a CCITT compression tag and its T4Options/T6Options bits onto the coded-segment layout, rejecting
    /// the sample layouts and coding options the bilevel codec does not implement.
    /// </summary>
    private static TiffCcittOptions ValidateCcitt(int compression, long bits, long spp, int fillOrder, int faxOptions)
    {
        if (bits != 1 || spp != 1)
        {
            throw new NotSupportedException(
                $"TIFF CCITT compression is only defined for single-sample bilevel pages, not {spp} samples of {bits} bits.");
        }

        // Bit 1 of both option words enables T.4 uncompressed mode, whose extension codes this decoder rejects.
        if ((faxOptions & 2) != 0)
        {
            throw new NotSupportedException("TIFF CCITT uncompressed mode is not supported.");
        }

        TiffCcittScheme scheme = compression switch
        {
            CompressionCcittRle => TiffCcittScheme.ModifiedHuffman,
            CompressionCcittGroup3 => TiffCcittScheme.Group3,
            _ => TiffCcittScheme.Group4,
        };

        return new TiffCcittOptions(
            scheme,
            TwoDimensional: scheme == TiffCcittScheme.Group3 && (faxOptions & 1) != 0,
            ByteAlign: scheme == TiffCcittScheme.ModifiedHuffman,
            LsbFirst: fillOrder == 2);
    }

    private static byte[] ReadStrips(
        ReadOnlySpan<byte> data, Dictionary<int, long[]> tags, int width, int height, int rowBytes, in Layout layout, bool bigEndian)
    {
        if (!tags.TryGetValue(TagStripOffsets, out long[]? stripOffsets))
        {
            throw new InvalidImageContentException("TIFF page is missing its strip offsets.");
        }

        tags.TryGetValue(TagStripByteCounts, out long[]? stripByteCounts);
        long rowsPerStrip = GetSingle(tags, TagRowsPerStrip, height);
        if (rowsPerStrip <= 0 || rowsPerStrip > height)
        {
            rowsPerStrip = height;
        }

        int stripCount = (int)((height + rowsPerStrip - 1) / rowsPerStrip);
        if (stripOffsets.Length < stripCount)
        {
            throw new InvalidImageContentException("TIFF page declares fewer strips than its dimensions require.");
        }

        if (stripByteCounts is null && layout.Compression != CompressionNone)
        {
            throw new InvalidImageContentException("Compressed TIFF page is missing its strip byte counts.");
        }

        // Validate every strip before allocating the page buffer so a truncated file fails cheaply.
        for (int s = 0; s < stripCount; s++)
        {
            int stripRows = (int)Math.Min(rowsPerStrip, height - (s * rowsPerStrip));
            long expected = (long)rowBytes * stripRows;
            long count = stripByteCounts is not null && s < stripByteCounts.Length ? stripByteCounts[s] : expected;
            long offset = stripOffsets[s];
            if (offset < 0 || count < 0 || offset + count > data.Length || (layout.Compression == CompressionNone && count < expected))
            {
                throw new InvalidImageContentException("TIFF strip data is truncated.");
            }
        }

        var raw = new byte[rowBytes * height];
        for (int s = 0; s < stripCount; s++)
        {
            int stripRows = (int)Math.Min(rowsPerStrip, height - (s * rowsPerStrip));
            int expected = rowBytes * stripRows;
            long count = stripByteCounts is not null && s < stripByteCounts.Length ? stripByteCounts[s] : expected;
            ReadOnlySpan<byte> strip = data.Slice((int)stripOffsets[s], (int)count);
            Span<byte> target = raw.AsSpan((int)(s * rowsPerStrip) * rowBytes, expected);
            Decompress(layout, strip, target, width, stripRows);
            if (layout.ApplyPredictor)
            {
                UndoPredictor(target, width, layout, bigEndian);
            }
        }

        return raw;
    }

    private static byte[] ReadTiles(
        ReadOnlySpan<byte> data, Dictionary<int, long[]> tags, int width, int height, int rowBytes, in Layout layout, bool bigEndian,
        DecoderOptions options)
    {
        long tileWidth = GetSingle(tags, TagTileWidth, 0);
        long tileLength = GetSingle(tags, TagTileLength, 0);
        long[] tileOffsets = tags[TagTileOffsets];
        if (!tags.TryGetValue(TagTileByteCounts, out long[]? tileByteCounts))
        {
            throw new InvalidImageContentException("Tiled TIFF page is missing its tile byte counts.");
        }

        if (tileWidth <= 0 || tileLength <= 0 || tileWidth > int.MaxValue || tileLength > int.MaxValue)
        {
            throw new InvalidImageContentException("Invalid TIFF tile dimensions.");
        }

        int bitsPerPixel = layout.BitsPerSample * layout.SamplesPerPixel;
        if (bitsPerPixel < 8 && (tileWidth * bitsPerPixel) % 8 != 0)
        {
            throw new NotSupportedException("TIFF tiles whose width is not a whole number of bytes are not supported.");
        }

        long tileRowBytesLong = ((tileWidth * bitsPerPixel) + 7) / 8;
        long tilesAcross = (width + tileWidth - 1) / tileWidth;
        long tilesDown = (height + tileLength - 1) / tileLength;
        long tileCount = tilesAcross * tilesDown;
        if (tileRowBytesLong * tileLength > int.MaxValue || tileCount > int.MaxValue)
        {
            throw new InvalidImageContentException("TIFF tile layout is too large to decode.");
        }

        // The tile grid pads the image up to whole tiles; a hostile TileWidth/TileLength could otherwise demand a
        // tile buffer far larger than the frame itself, so the padded area is subject to the same pixel limit.
        options.EnsureFrameWithinLimits(
            (int)Math.Min(tilesAcross * tileWidth, int.MaxValue), (int)Math.Min(tilesDown * tileLength, int.MaxValue), "TIFF (tiled)");

        if (tileOffsets.Length < tileCount || tileByteCounts.Length < tileCount)
        {
            throw new InvalidImageContentException("TIFF page declares fewer tiles than its dimensions require.");
        }

        int tileRowBytes = (int)tileRowBytesLong;
        int tileBytes = tileRowBytes * (int)tileLength;
        for (int t = 0; t < tileCount; t++)
        {
            long offset = tileOffsets[t];
            long count = tileByteCounts[t];
            if (offset < 0 || count < 0 || offset + count > data.Length || (layout.Compression == CompressionNone && count < tileBytes))
            {
                throw new InvalidImageContentException("TIFF tile data is truncated.");
            }
        }

        var raw = new byte[rowBytes * height];
        var tile = new byte[tileBytes];
        int t2 = 0;
        for (long ty = 0; ty < tilesDown; ty++)
        {
            for (long tx = 0; tx < tilesAcross; tx++, t2++)
            {
                ReadOnlySpan<byte> src = data.Slice((int)tileOffsets[t2], (int)tileByteCounts[t2]);
                Decompress(layout, src, tile, (int)tileWidth, (int)tileLength);
                if (layout.ApplyPredictor)
                {
                    UndoPredictor(tile, (int)tileWidth, layout, bigEndian);
                }

                int x0 = (int)(tx * tileWidth);
                int y0 = (int)(ty * tileLength);
                int copyPixels = (int)Math.Min(tileWidth, width - x0);
                int copyBytes = ((copyPixels * bitsPerPixel) + 7) / 8;
                int destByteOffset = (x0 * bitsPerPixel) / 8;
                int rows = (int)Math.Min(tileLength, height - y0);
                for (int r = 0; r < rows; r++)
                {
                    tile.AsSpan(r * tileRowBytes, copyBytes).CopyTo(raw.AsSpan(((y0 + r) * rowBytes) + destByteOffset, copyBytes));
                }
            }
        }

        return raw;
    }

    /// <summary>
    /// Reads a PlanarConfiguration 2 page. Every sample lives in its own run of strips (or tiles), so each
    /// plane is decoded through the ordinary readers as a single-sample page and then interleaved into the
    /// chunky buffer the row conversion expects.
    /// </summary>
    private static byte[] ReadPlanes(
        ReadOnlySpan<byte> data, Dictionary<int, long[]> tags, int width, int height, int rowBytes, in Layout layout,
        bool bigEndian, DecoderOptions options, bool tiled)
    {
        int samples = layout.SamplesPerPixel;
        int bytesPerSample = layout.BitsPerSample / 8;
        int planeRowBytes = width * bytesPerSample;
        int offsetsTag = tiled ? TagTileOffsets : TagStripOffsets;
        int countsTag = tiled ? TagTileByteCounts : TagStripByteCounts;
        if (!tags.TryGetValue(offsetsTag, out long[]? offsets))
        {
            throw new InvalidImageContentException("TIFF page is missing its strip offsets.");
        }

        tags.TryGetValue(countsTag, out long[]? counts);
        int segments = SegmentsPerPlane(tags, width, height, tiled);
        if (offsets.Length < (long)segments * samples || (counts is not null && counts.Length < (long)segments * samples))
        {
            throw new InvalidImageContentException("Planar TIFF page declares fewer segments than its samples require.");
        }

        var raw = new byte[rowBytes * height];
        Layout planeLayout = layout with { SamplesPerPixel = 1, HasAlpha = false, Planar = false };
        for (int p = 0; p < samples; p++)
        {
            var planeTags = new Dictionary<int, long[]>(tags)
            {
                [offsetsTag] = offsets.AsSpan(p * segments, segments).ToArray(),
            };
            if (counts is not null)
            {
                planeTags[countsTag] = counts.AsSpan(p * segments, segments).ToArray();
            }

            byte[] plane = tiled
                ? ReadTiles(data, planeTags, width, height, planeRowBytes, planeLayout, bigEndian, options)
                : ReadStrips(data, planeTags, width, height, planeRowBytes, planeLayout, bigEndian);

            int stride = samples * bytesPerSample;
            for (int y = 0; y < height; y++)
            {
                int source = y * planeRowBytes;
                int target = (y * rowBytes) + (p * bytesPerSample);
                for (int x = 0; x < width; x++)
                {
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        raw[target + (x * stride) + b] = plane[source + (x * bytesPerSample) + b];
                    }
                }
            }
        }

        return raw;
    }

    /// <summary>The number of strips or tiles one plane of a page occupies.</summary>
    private static int SegmentsPerPlane(Dictionary<int, long[]> tags, int width, int height, bool tiled)
    {
        if (!tiled)
        {
            long rowsPerStrip = GetSingle(tags, TagRowsPerStrip, height);
            if (rowsPerStrip <= 0 || rowsPerStrip > height)
            {
                rowsPerStrip = height;
            }

            return (int)((height + rowsPerStrip - 1) / rowsPerStrip);
        }

        long tileWidth = GetSingle(tags, TagTileWidth, 0);
        long tileLength = GetSingle(tags, TagTileLength, 0);
        if (tileWidth <= 0 || tileLength <= 0)
        {
            throw new InvalidImageContentException("Invalid TIFF tile dimensions.");
        }

        long count = ((width + tileWidth - 1) / tileWidth) * ((height + tileLength - 1) / tileLength);
        return count <= int.MaxValue ? (int)count : throw new InvalidImageContentException("TIFF tile layout is too large to decode.");
    }

    /// <summary>
    /// Decompresses one strip or tile into <paramref name="target"/>, which must be filled completely.
    /// <paramref name="pixelWidth"/> and <paramref name="rows"/> give the segment's pixel geometry, which the
    /// bilevel codecs need because their coded data carries no row lengths of its own.
    /// </summary>
    private static void Decompress(in Layout layout, ReadOnlySpan<byte> source, Span<byte> target, int pixelWidth, int rows)
    {
        switch (layout.Compression)
        {
            case CompressionNone:
                source[..target.Length].CopyTo(target);
                break;
            case CompressionLzw:
                TiffLzw.Decode(source, target);
                break;
            case CompressionDeflate or CompressionDeflateLegacy:
                InflateSegment(source, target);
                break;
            case CompressionPackBits:
                UnpackBits(source, target);
                break;
            case CompressionCcittRle or CompressionCcittGroup3 or CompressionCcittGroup4:
                TiffCcitt.Decode(source, target, pixelWidth, rows, layout.Ccitt!.Value);
                break;
            case CompressionJpeg:
                TiffJpeg.DecodeSegment(source, layout.Jpeg!, target, pixelWidth, rows);
                break;
            default:
                throw new NotSupportedException($"TIFF compression {layout.Compression} is not supported.");
        }
    }

    /// <summary>Reverses horizontal differencing in place over rows of <paramref name="width"/> pixels.</summary>
    private static void UndoPredictor(Span<byte> rows, int width, in Layout layout, bool bigEndian)
    {
        int spp = layout.SamplesPerPixel;
        if (layout.BitsPerSample == 8)
        {
            int rowBytes = width * spp;
            for (int start = 0; start + rowBytes <= rows.Length; start += rowBytes)
            {
                Span<byte> row = rows.Slice(start, rowBytes);
                for (int i = spp; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - spp]);
                }
            }
        }
        else
        {
            int rowBytes = width * spp * 2;
            for (int start = 0; start + rowBytes <= rows.Length; start += rowBytes)
            {
                Span<byte> row = rows.Slice(start, rowBytes);
                for (int i = spp * 2; i < row.Length; i += 2)
                {
                    Span<byte> current = row.Slice(i, 2);
                    ReadOnlySpan<byte> previous = row.Slice(i - (spp * 2), 2);
                    ushort sum = (ushort)((bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(current) + BinaryPrimitives.ReadUInt16BigEndian(previous)
                        : BinaryPrimitives.ReadUInt16LittleEndian(current) + BinaryPrimitives.ReadUInt16LittleEndian(previous)) & 0xFFFF);
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(current, sum);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(current, sum);
                    }
                }
            }
        }
    }

    private static void ReverseBits(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            byte b = buffer[i];
            b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
            b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
            buffer[i] = b;
        }
    }

    private static Rgba32[] ReadPalette(Dictionary<int, long[]> tags, int bits)
    {
        if (!tags.TryGetValue(TagColorMap, out long[]? colorMap))
        {
            throw new InvalidImageContentException("Palette TIFF is missing its color map.");
        }

        int entries = 1 << bits;
        if (colorMap.Length < entries * 3)
        {
            throw new InvalidImageContentException("TIFF color map is truncated.");
        }

        var palette = new Rgba32[entries];
        for (int i = 0; i < entries; i++)
        {
            palette[i] = new Rgba32(
                (byte)(colorMap[i] >> 8),
                (byte)(colorMap[entries + i] >> 8),
                (byte)(colorMap[(2 * entries) + i] >> 8));
        }

        return palette;
    }

    /// <summary>Row layouts that map straight onto a pixel format.</summary>
    private enum TiffRowLayout
    {
        /// <summary>No shortcut; the row goes through the general per-pixel decode.</summary>
        General,

        /// <summary>Three 8-bit samples, red first - the layout of <see cref="Rgb24"/>.</summary>
        Rgb24,

        /// <summary>Four 8-bit samples with alpha last - the layout of <see cref="Rgba32"/>.</summary>
        Rgba32,

        /// <summary>One 8-bit sample, min-is-black - the layout of <see cref="L8"/>.</summary>
        L8,

        /// <summary>8-bit palette indices, which become a table lookup per pixel.</summary>
        Palette8,
    }

    private static TiffRowLayout ClassifyRowLayout(in Layout layout, int photometric)
    {
        if (layout.BitsPerSample != 8)
        {
            return TiffRowLayout.General;
        }

        return photometric switch
        {
            1 when layout.SamplesPerPixel == 1 && !layout.HasAlpha => TiffRowLayout.L8,
            2 when layout.SamplesPerPixel == 3 && !layout.HasAlpha => TiffRowLayout.Rgb24,
            2 when layout.SamplesPerPixel == 4 && layout.HasAlpha => TiffRowLayout.Rgba32,
            3 => TiffRowLayout.Palette8,
            _ => TiffRowLayout.General,
        };
    }

    /// <summary>The colour map as the destination pixel format, so a paletted row is one lookup per pixel.</summary>
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

    private static void ConvertRow(
        ReadOnlySpan<byte> row, Span<Rgba32> dest, int width, in Layout layout, int photometric, Rgba32[]? palette, bool bigEndian)
    {
        int bits = layout.BitsPerSample;
        int spp = layout.SamplesPerPixel;
        int highByte = bigEndian ? 0 : 1; // Offset of the most significant byte within a 16-bit sample.

        if (TiffColor.IsColorimetric(photometric))
        {
            TiffColor.ConvertRow(row, dest, width, spp, photometric, layout.HasAlpha);
            return;
        }

        if (photometric == 3)
        {
            for (int x = 0; x < width; x++)
            {
                int index = bits == 8 ? row[x] : ReadSubByteSample(row, x, bits);
                dest[x] = index < palette!.Length
                    ? palette[index]
                    : throw new InvalidImageContentException("TIFF palette index out of range.");
            }

            return;
        }

        if (photometric is 0 or 1 or 4)
        {
            // A transparency mask (4) is a bilevel page imaged the way WhiteIsZero pages are, as libtiff does.
            bool invert = photometric is 0 or 4;
            if (bits < 8)
            {
                // Sub-byte gray (never carries alpha here: spp is 1 for these depths in practice; a second
                // sample would be interleaved at the same depth).
                int scale = 255 / ((1 << bits) - 1);
                for (int x = 0; x < width; x++)
                {
                    int sample = ReadSubByteSample(row, x * spp, bits);
                    byte v = (byte)(sample * scale);
                    if (invert)
                    {
                        v = (byte)(255 - v);
                    }

                    byte a = 255;
                    if (layout.HasAlpha)
                    {
                        a = (byte)(ReadSubByteSample(row, (x * spp) + 1, bits) * scale);
                    }

                    dest[x] = new Rgba32(v, v, v, a);
                }
            }
            else
            {
                int bytesPerSample = bits / 8;
                int stride = spp * bytesPerSample;
                int alphaOffset = bytesPerSample + (bytesPerSample == 2 ? highByte : 0);
                int grayOffset = bytesPerSample == 2 ? highByte : 0;
                for (int x = 0; x < width; x++)
                {
                    int i = x * stride;
                    byte v = row[i + grayOffset];
                    if (invert)
                    {
                        v = (byte)(255 - v);
                    }

                    byte a = layout.HasAlpha ? row[i + alphaOffset] : (byte)255;
                    dest[x] = new Rgba32(v, v, v, a);
                }
            }

            return;
        }

        // photometric 2: RGB / RGBA with 8- or 16-bit samples.
        {
            int bytesPerSample = bits / 8;
            int stride = spp * bytesPerSample;
            int o = bytesPerSample == 2 ? highByte : 0;
            for (int x = 0; x < width; x++)
            {
                int i = x * stride;
                byte a = layout.HasAlpha ? row[i + (3 * bytesPerSample) + o] : (byte)255;
                dest[x] = new Rgba32(row[i + o], row[i + bytesPerSample + o], row[i + (2 * bytesPerSample) + o], a);
            }
        }
    }

    /// <summary>
    /// The 16-bit-per-sample counterpart of <see cref="ConvertRow"/>, used only when the caller asked
    /// for a pixel format that carries more than 8 bits per component. Keeping the samples at their
    /// full width here is what lets a 16-bit TIFF reach an <see cref="Rgb48"/> or <see cref="Rgba64"/>
    /// image instead of being narrowed to its high bytes. Only the photometric interpretations that
    /// can carry 16-bit samples are handled; palette pages never reach this method.
    /// </summary>
    private static void ConvertRow16(
        ReadOnlySpan<byte> row, Span<Rgba64> dest, int width, in Layout layout, int photometric, bool bigEndian)
    {
        int spp = layout.SamplesPerPixel;

        if (photometric is 0 or 1)
        {
            bool invert = photometric == 0; // WhiteIsZero.
            for (int x = 0; x < width; x++)
            {
                int i = x * spp;
                ushort v = Sample16(row, i, bigEndian);
                if (invert)
                {
                    v = (ushort)(ushort.MaxValue - v);
                }

                ushort a = layout.HasAlpha ? Sample16(row, i + 1, bigEndian) : ushort.MaxValue;
                dest[x] = new Rgba64(v, v, v, a);
            }

            return;
        }

        // photometric 2: RGB / RGBA.
        for (int x = 0; x < width; x++)
        {
            int i = x * spp;
            ushort a = layout.HasAlpha ? Sample16(row, i + 3, bigEndian) : ushort.MaxValue;
            dest[x] = new Rgba64(
                Sample16(row, i, bigEndian), Sample16(row, i + 1, bigEndian), Sample16(row, i + 2, bigEndian), a);
        }
    }

    /// <summary>Reads the 16-bit sample at the given sample index of an already unpredicted row.</summary>
    private static ushort Sample16(ReadOnlySpan<byte> row, int sampleIndex, bool bigEndian)
    {
        ReadOnlySpan<byte> sample = row[(sampleIndex * 2)..];
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(sample)
            : BinaryPrimitives.ReadUInt16LittleEndian(sample);
    }

    private static int ReadSubByteSample(ReadOnlySpan<byte> row, int index, int depth)
    {
        int bitIndex = index * depth;
        return (row[bitIndex >> 3] >> (8 - depth - (bitIndex & 7))) & ((1 << depth) - 1);
    }

    private static void InflateSegment(ReadOnlySpan<byte> segment, Span<byte> target)
    {
        byte[] compressed = segment.ToArray();
        int read;
        try
        {
            read = InflateWith(compressed, target, zlibWrapped: true);
        }
        catch (InvalidDataException)
        {
            // Some writers emit raw deflate streams without the zlib wrapper.
            read = InflateWith(compressed, target, zlibWrapped: false);
        }

        if (read < target.Length)
        {
            throw new InvalidImageContentException("TIFF deflate data ended before the strip was complete.");
        }
    }

    private static int InflateWith(byte[] compressed, Span<byte> target, bool zlibWrapped)
    {
        using var input = new MemoryStream(compressed);
        using Stream inflater = zlibWrapped
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        int read = 0;
        while (read < target.Length)
        {
            int n = inflater.Read(target[read..]);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read;
    }

    private static void UnpackBits(ReadOnlySpan<byte> strip, Span<byte> target)
    {
        int expectedSize = target.Length;
        int outPos = 0;
        int inPos = 0;
        while (outPos < expectedSize && inPos < strip.Length)
        {
            sbyte n = (sbyte)strip[inPos++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, expectedSize - outPos);
                if (inPos + count > strip.Length)
                {
                    break;
                }

                strip.Slice(inPos, count).CopyTo(target[outPos..]);
                inPos += count;
                outPos += count;
            }
            else if (n != -128)
            {
                if (inPos >= strip.Length)
                {
                    break;
                }

                byte value = strip[inPos++];
                int count = Math.Min(1 - n, expectedSize - outPos);
                target.Slice(outPos, count).Fill(value);
                outPos += count;
            }
        }

        if (outPos < expectedSize)
        {
            throw new InvalidImageContentException("TIFF PackBits data ended before the strip was complete.");
        }
    }

    // ----- IFD parsing -----

    private static long NextIfdOffset(ReadOnlySpan<byte> data, int ifdOffset, bool bigEndian)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > data.Length)
        {
            return 0;
        }

        int entryCount = (int)ReadU16(data, ifdOffset, bigEndian);
        long nextPointer = ifdOffset + 2L + (entryCount * 12L);
        return nextPointer + 4 <= data.Length ? ReadU32(data, (int)nextPointer, bigEndian) : 0;
    }

    private static Dictionary<int, long[]> ReadTags(ReadOnlySpan<byte> data, int ifdOffset, bool bigEndian)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > data.Length)
        {
            throw new InvalidImageContentException("TIFF directory offset is out of range.");
        }

        int entryCount = (int)ReadU16(data, ifdOffset, bigEndian);
        if (ifdOffset + 2 + (entryCount * 12L) > data.Length)
        {
            throw new InvalidImageContentException("TIFF directory is truncated.");
        }

        var tags = new Dictionary<int, long[]>();
        for (int i = 0; i < entryCount; i++)
        {
            int entry = ifdOffset + 2 + (i * 12);
            int tag = (int)ReadU16(data, entry, bigEndian);
            if (!KnownTags.Contains(tag) || tags.ContainsKey(tag))
            {
                continue; // Unused or duplicated tag: never materialized, so hostile counts cost nothing.
            }

            int type = (int)ReadU16(data, entry + 2, bigEndian);
            long count = ReadU32(data, entry + 4, bigEndian);
            int size = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 => 4,
                5 or 10 or 12 => 8,
                _ => 0,
            };
            if (size == 0 || count <= 0 || count > 1 << 22)
            {
                continue;
            }

            long total = size * count;
            long valueOffset = total <= 4 ? entry + 8 : ReadU32(data, entry + 8, bigEndian);
            if (valueOffset < 0 || valueOffset + total > data.Length)
            {
                continue;
            }

            var values = new long[count];
            for (int v = 0; v < count; v++)
            {
                int o = (int)(valueOffset + (v * size));
                values[v] = size switch
                {
                    1 => data[o],
                    2 => ReadU16(data, o, bigEndian),
                    4 => ReadU32(data, o, bigEndian),
                    _ => ReadU32(data, o, bigEndian), // Rationals: numerator only; unused by this decoder.
                };
            }

            tags[tag] = values;
        }

        return tags;
    }

    /// <summary>Repacks a BYTE/UNDEFINED tag, materialized as one value per byte, into a byte array.</summary>
    private static byte[] ToBytes(long[] values)
    {
        var bytes = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = (byte)values[i];
        }

        return bytes;
    }

    private static long GetSingle(Dictionary<int, long[]> tags, int tag, long defaultValue)
        => tags.TryGetValue(tag, out long[]? values) && values.Length > 0 ? values[0] : defaultValue;

    private static uint ReadU16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    /// <summary>The validated per-page sample layout.</summary>
    private readonly record struct Layout(
        int BitsPerSample, int SamplesPerPixel, bool HasAlpha, int Compression, bool ApplyPredictor, TiffCcittOptions? Ccitt,
        bool Planar, TiffJpegState? Jpeg);
}
