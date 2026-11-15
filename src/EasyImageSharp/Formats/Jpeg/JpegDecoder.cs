using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Decodes Huffman-coded JPEG images: baseline and extended sequential frames (SOF0/SOF1) and progressive frames
/// (SOF2, including successive approximation) with 8-bit or 12-bit samples, and lossless frames (SOF3, ITU-T T.81
/// Annex H: predictors 1-7, point transform, 2 to 16 bits per sample); grayscale, YCbCr and RGB color; Adobe CMYK
/// and YCCK; every chroma subsampling layout (4:2:2 and 4:2:0 DCT frames are reconstructed with libjpeg-style
/// triangle "fancy" upsampling, other ratios and lossless frames by sample replication, as libjpeg does); restart
/// markers; and multi-scan non-interleaved files. Arithmetic-coded and hierarchical frames are not supported and
/// throw <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Samples wider than 8 bits (12-bit DCT frames, lossless frames above 8 bits) keep their full precision when the
/// image is decoded into a pixel format with 16-bit or floating-point components (<see cref="L16"/>,
/// <see cref="La32"/>, <see cref="Rgb48"/>, <see cref="Rgba64"/>, <see cref="RgbaVector"/>): the largest code value
/// of the file's precision maps to full scale. Every other pixel format receives the same values rounded to 8 bits.
/// <see cref="JpegMetadata.BitsPerSample"/> reports the precision of the file.
/// </para>
/// <para>
/// Progressive images keep the quantized coefficients of every block until the last scan has been read
/// (two bytes per coefficient per component), so they need roughly three times the memory of a baseline
/// image of the same size while decoding. A stream that ends before its EOI marker is treated as truncated
/// and rejected with <see cref="InvalidImageContentException"/> for every frame type.
/// </para>
/// </remarks>
public sealed class JpegDecoder : IImageDecoder
{
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        => this.DecodeCore<TPixel>(data, options, 1);

    /// <summary>
    /// Decodes directly at 1/2, 1/4, or 1/8 resolution using reduced inverse DCT blocks; 1 preserves full resolution.
    /// Dimensions are rounded up. Baseline and progressive images (8-bit and 12-bit), supported color spaces, and
    /// metadata are retained. Source dimension limits still apply. Progressive coefficient storage remains at source
    /// resolution, while decoded sample planes and the output image are reduced; this is not decode followed by
    /// resize. Lossless images have no DCT blocks to reduce and throw <see cref="NotSupportedException"/> for any
    /// denominator other than 1.
    /// </summary>
    public Image<TPixel> DecodeReduced<TPixel>(ReadOnlySpan<byte> data, int scaleDenominator, DecoderOptions? options = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (scaleDenominator is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleDenominator), "JPEG scale denominator must be 1, 2, 4, or 8.");
        }

        return this.DecodeCore<TPixel>(data, options ?? DecoderOptions.Default, scaleDenominator);
    }

    private Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options, int scaleDenominator)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            // The parser needs an array (it is captured by the parallel colour-conversion pass, which a
            // ref struct could not be), but it does not need a fresh one: a pooled buffer avoids an
            // allocation the size of the whole file on every decode.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(buffer);
                var core = new JpegDecoderCore(buffer, data.Length, options, scaleDenominator);
                core.ParseAndDecode();
                return core.ToImage<TPixel>();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("JPEG", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var execution = OperationExecution.Enter(options.ExecutionOptions);
        options.EnsureEncodedWithinLimits(data.Length);
        try
        {
            return IdentifyCore(data);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("JPEG", ex);
        }
    }

    private static ImageInfo IdentifyCore(ReadOnlySpan<byte> data)
    {
        // Walks the marker segments up to the first scan, collecting the header facts and metadata segments.
        var metadataReader = new JpegMetadataReader();
        int width = 0;
        int height = 0;
        int components = 0;
        int precision = 8;
        bool sawFrame = false;
        bool progressive = false;
        bool lossless = false;
        int adobeTransform = -1;
        bool rgbIds = false;

        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            OperationExecution.Checkpoint();
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            byte marker = data[pos + 1];
            if (marker is 0xFF or 0x00)
            {
                pos++;
                continue;
            }

            pos += 2;
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7 or 0x01)
            {
                if (marker == 0xD9)
                {
                    break;
                }

                continue; // No payload.
            }

            if (pos + 2 > data.Length)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            bool isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof && pos + 8 <= data.Length)
            {
                precision = data[pos + 2];
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 5)..]);
                components = data[pos + 7];
                progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                lossless = marker is 0xC3 or 0xC7 or 0xCB or 0xCF;
                sawFrame = true;
                if (components == 3 && pos + 8 + 9 <= data.Length)
                {
                    rgbIds = data[pos + 8] == 'R' && data[pos + 11] == 'G' && data[pos + 14] == 'B';
                }
            }

            if (marker == 0xDA)
            {
                break; // Start of scan: no further metadata segments follow.
            }

            int available = Math.Min(Math.Max(length - 2, 0), data.Length - pos - 2);
            if (available > 0 && length >= 2)
            {
                ReadOnlySpan<byte> payload = data.Slice(pos + 2, available);
                switch (marker)
                {
                    case 0xE0 or 0xE1 or 0xE2 or 0xED or 0xFE:
                        metadataReader.ProcessSegment(marker, payload);
                        break;
                    case 0xEE when payload.Length >= 12 && payload[0] == 'A' && payload[1] == 'd' && payload[2] == 'o' && payload[3] == 'b' && payload[4] == 'e':
                        adobeTransform = payload[11];
                        break;
                    case 0xDB:
                        metadataReader.SetLuminanceQuantTable(ParseFirstQuantTable(payload));
                        break;
                }
            }

            pos += length;
        }

        if (!sawFrame)
        {
            throw new InvalidImageContentException("JPEG image is missing a start-of-frame marker.");
        }

        metadataReader.SetFrame(progressive, ClassifyColorType(components, adobeTransform, rgbIds));
        ImageMetadata metadata = metadataReader.Finish();
        RecordPrecision(metadata, precision, lossless);
        return new ImageInfo(width, height, components * precision, 1, ImageFormat.Jpeg, metadata);
    }

    /// <summary>Stores the frame's sample precision and coding process in the JPEG metadata.</summary>
    internal static void RecordPrecision(ImageMetadata metadata, int precision, bool lossless)
    {
        JpegMetadata jpeg = metadata.GetJpegMetadata();
        jpeg.BitsPerSample = precision;
        jpeg.Lossless = lossless;
    }

    /// <summary>Reads table 0 of a DQT payload in natural order, or null when the payload does not hold it intact.</summary>
    private static ushort[]? ParseFirstQuantTable(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            OperationExecution.Checkpoint();
            byte pqTq = payload[pos++];
            int precision = pqTq >> 4;
            int id = pqTq & 0x0F;
            int size = precision == 0 ? 64 : 128;
            if (precision > 1 || pos + size > payload.Length)
            {
                return null;
            }

            if (id == 0)
            {
                var table = new ushort[64];
                for (int i = 0; i < 64; i++)
                {
                    table[JpegTables.ZigZag[i]] = precision == 0
                        ? payload[pos + i]
                        : BinaryPrimitives.ReadUInt16BigEndian(payload[(pos + (i * 2))..]);
                }

                return table;
            }

            pos += size;
        }

        return null;
    }

    internal static JpegColorType ClassifyColorType(int components, int adobeTransform, bool rgbComponentIds) => components switch
    {
        1 => JpegColorType.Grayscale,
        3 => adobeTransform == 0 || rgbComponentIds ? JpegColorType.Rgb : JpegColorType.YCbCr,
        4 => adobeTransform > 0 ? JpegColorType.Ycck : JpegColorType.Cmyk,
        _ => JpegColorType.Unknown,
    };
}

/// <summary>
/// JPEG decode state machine: marker parsing, entropy decoding of sequential, progressive and lossless scans and
/// block reconstruction. Progressive-specific block decoding lives in <c>JpegDecoder.Progressive.cs</c>, lossless
/// scans in <c>JpegDecoder.Lossless.cs</c>, the conversion of the decoded planes to pixels in
/// <c>JpegDecoder.Output.cs</c> and the row-at-a-time mode behind <see cref="JpegScanlineSource"/> in
/// <c>JpegDecoder.Streaming.cs</c>.
/// </summary>
internal sealed partial class JpegDecoderCore
{
    /// <summary>
    /// The bytes being parsed. For an in-memory decode this is the whole file; for a streaming decode it is a window
    /// over the input that <see cref="Refill"/> slides forward and grows on demand.
    /// </summary>
    private byte[] data;

    /// <summary>Bytes of <see cref="data"/> that hold input; the buffer itself may be longer (pooled or a window).</summary>
    private int dataLength;
    private readonly DecoderOptions options;
    private readonly int scaleDenominator;
    private readonly int outputBlockSize;
    private int pos;

    private readonly ushort[]?[] quantTables = new ushort[4][];
    private readonly HuffmanTable?[] dcTables = new HuffmanTable?[4];
    private readonly HuffmanTable?[] acTables = new HuffmanTable?[4];

    private JpegComponent[] components = Array.Empty<JpegComponent>();
    private int width;
    private int height;
    private int maxH = 1;
    private int maxV = 1;
    private int mcusX;
    private int mcusY;
    private int restartInterval;
    private int adobeTransform = -1;
    private bool frameParsed;
    private bool progressive;
    private bool lossless;

    /// <summary>Bits per sample of the frame (P in the frame header): 8 or 12 for DCT frames, 2-16 for lossless ones.</summary>
    private int precision = 8;

    /// <summary>Largest sample value of the frame's precision.</summary>
    private int maxSample = 255;
    private int scansDecoded;
    private readonly JpegMetadataReader metadataReader = new();

    // Parameters of the scan currently being decoded.
    private ScanKind scanKind;
    private int spectralStart;
    private int spectralEnd;
    private int approxLow;
    private int eobRun;
    private readonly float[] blockScratch = new float[64];
    private readonly float[] tempScratch = new float[64];

    // Entropy-coded bit reader state.
    private ulong bitBuffer;
    private int bitCount;
    private bool markerPending;
    private bool insufficientData;

    public JpegDecoderCore(byte[] data, int dataLength, DecoderOptions options, int scaleDenominator = 1)
    {
        this.data = data;
        this.dataLength = dataLength;
        this.options = options;
        this.scaleDenominator = scaleDenominator;
        this.outputBlockSize = 8 / scaleDenominator;
    }

    private enum ScanKind
    {
        Sequential,
        DcFirst,
        DcRefine,
        AcFirst,
        AcRefine,
    }

    /// <summary>What <see cref="ProcessNextMarker"/> found.</summary>
    private enum MarkerResult
    {
        /// <summary>A segment that was consumed; parsing continues.</summary>
        Continue,

        /// <summary>An SOS marker; the scan header has not been read yet.</summary>
        StartOfScan,

        /// <summary>The EOI marker of a file that has a frame and at least one scan.</summary>
        EndOfImage,
    }

    public int Width => this.width;

    public int Height => this.height;

    internal long DecodedPlaneBytes => this.components.Sum(component => component.Plane.LongLength + (component.WidePlane.LongLength * sizeof(ushort)));

    internal long CoefficientBytes => this.components.Sum(component => component.Coefficients.LongLength * sizeof(short));

    /// <summary>Width of the decoded image after any reduction.</summary>
    internal int OutputWidth => (this.width + this.scaleDenominator - 1) / this.scaleDenominator;

    /// <summary>Height of the decoded image after any reduction.</summary>
    internal int OutputHeight => (this.height + this.scaleDenominator - 1) / this.scaleDenominator;

    /// <summary>True when the samples do not fit a byte plane: 12-bit DCT frames and every lossless frame.</summary>
    private bool WideSamples => this.precision != 8 || this.lossless;

    /// <summary>
    /// Whether 2:1 subsampled components use the triangle filter. libjpeg applies it only when the IDCT produces
    /// more than one sample per block (so not at 1/8 scale) and never to lossless frames, which have no blocks.
    /// </summary>
    private bool FancyUpsampling => !this.lossless && this.outputBlockSize > 1;

    public void ParseAndDecode()
    {
        this.ReadStartOfImage();
        while (true)
        {
            OperationExecution.Checkpoint();
            switch (this.ProcessNextMarker())
            {
                case MarkerResult.StartOfScan:
                    this.DecodeScan();
                    break;

                case MarkerResult.EndOfImage:
                    if (this.progressive)
                    {
                        this.ReconstructProgressiveFrame();
                    }

                    return;
            }
        }
    }

    private void ReadStartOfImage()
    {
        if (!this.EnsureBuffered(4) || this.data[this.pos] != 0xFF || this.data[this.pos + 1] != 0xD8)
        {
            throw new InvalidImageContentException("Missing JPEG SOI marker.");
        }

        this.pos += 2;
    }

    /// <summary>Finds the next marker and consumes its segment, except for SOS, whose header the caller reads.</summary>
    private MarkerResult ProcessNextMarker()
    {
        if (!this.TryNextMarker(out byte marker))
        {
            throw new InvalidImageContentException("Unexpected end of JPEG data while searching for a marker.");
        }

        switch (marker)
        {
            case 0xD9: // EOI
                if (!this.frameParsed)
                {
                    throw new InvalidImageContentException("JPEG ended before any image data.");
                }

                if (this.scansDecoded == 0)
                {
                    throw new InvalidImageContentException("JPEG frame header is not followed by any scan.");
                }

                return MarkerResult.EndOfImage;

            case 0xC0: // SOF0 baseline
            case 0xC1: // SOF1 extended sequential (identical handling)
                this.ReadFrame(isProgressive: false, isLossless: false);
                break;

            case 0xC2: // SOF2 progressive
                this.ReadFrame(isProgressive: true, isLossless: false);
                break;

            case 0xC3: // SOF3 lossless
                this.ReadFrame(isProgressive: false, isLossless: true);
                break;

            case >= 0xC5 and <= 0xC7:
                throw new NotSupportedException(
                    $"JPEG frame type 0x{marker:X2} (hierarchical, differential Huffman coding) is not supported.");

            case 0xC9: // SOF9 extended sequential, arithmetic coding
                this.arithmetic = true;
                this.ReadFrame(isProgressive: false, isLossless: false);
                break;

            case 0xCA:
                throw new NotSupportedException(
                    "JPEG frame type 0xCA (progressive DCT, arithmetic coding) is not supported; sequential arithmetic-coded JPEG (SOF9) and Huffman-coded JPEG can be decoded.");

            case 0xCB:
                throw new NotSupportedException(
                    "JPEG frame type 0xCB (lossless, arithmetic coding) is not supported; lossless JPEG must be Huffman-coded (SOF3).");

            case >= 0xCD and <= 0xCF:
                throw new NotSupportedException(
                    $"JPEG frame type 0x{marker:X2} (hierarchical, differential arithmetic coding) is not supported.");

            case 0xCC: // DAC
                this.ReadArithmeticConditioning();
                break;

            case 0xC4:
                this.ReadHuffmanTables();
                break;

            case 0xDB:
                this.ReadQuantizationTables();
                break;

            case 0xDD:
                this.ReadUInt16(); // Segment length (4).
                this.restartInterval = this.ReadUInt16();
                break;

            case 0xDA:
                return MarkerResult.StartOfScan;

            case 0xEE: // APP14 (Adobe)
                this.ReadAdobeMarker();
                break;

            case 0xE0: // APP0 (JFIF)
            case 0xE1: // APP1 (EXIF / XMP)
            case 0xE2: // APP2 (ICC)
            case 0xED: // APP13 (Photoshop image resources / IPTC)
            case 0xFE: // COM
                this.ReadMetadataSegment(marker);
                break;

            case >= 0xD0 and <= 0xD7: // Stray restart marker
            case 0x01:
            case 0xD8:
                break;

            default:
                this.SkipSegment();
                break;
        }

        return MarkerResult.Continue;
    }

    // ----- Input window -----

    /// <summary>
    /// True when at least <paramref name="count"/> unread bytes are buffered, refilling the window from the input
    /// stream first when decoding a stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureBuffered(int count) => this.dataLength - this.pos >= count || this.Refill(count);

    // ----- Marker/segment parsing -----

    private bool TryNextMarker(out byte marker)
    {
        while (this.EnsureBuffered(2))
        {
            OperationExecution.Checkpoint();
            if (this.data[this.pos] == 0xFF)
            {
                byte candidate = this.data[this.pos + 1];
                if (candidate is not 0x00 and not 0xFF)
                {
                    this.pos += 2;
                    marker = candidate;
                    return true;
                }
            }

            this.pos++;
        }

        marker = 0;
        return false;
    }

    private int ReadUInt16()
    {
        if (!this.EnsureBuffered(2))
        {
            throw new InvalidImageContentException("Unexpected end of JPEG data.");
        }

        int value = (this.data[this.pos] << 8) | this.data[this.pos + 1];
        this.pos += 2;
        return value;
    }

    private byte ReadByte()
    {
        if (!this.EnsureBuffered(1))
        {
            throw new InvalidImageContentException("Unexpected end of JPEG data.");
        }

        return this.data[this.pos++];
    }

    private void SkipSegment()
    {
        int length = this.ReadUInt16();
        if (length < 2 || !this.EnsureBuffered(length - 2))
        {
            throw new InvalidImageContentException("JPEG segment is truncated.");
        }

        this.pos += length - 2;
    }

    private void ReadAdobeMarker()
    {
        int length = this.ReadUInt16();
        if (length < 2 || !this.EnsureBuffered(length - 2))
        {
            throw new InvalidImageContentException("JPEG APP14 segment is truncated.");
        }

        int end = this.pos + length - 2;
        if (length >= 14
            && this.data[this.pos] == 'A' && this.data[this.pos + 1] == 'd' && this.data[this.pos + 2] == 'o'
            && this.data[this.pos + 3] == 'b' && this.data[this.pos + 4] == 'e')
        {
            this.adobeTransform = this.data[this.pos + 11];
        }

        this.pos = end;
    }

    private void ReadMetadataSegment(byte marker)
    {
        int length = this.ReadUInt16();
        if (length < 2 || !this.EnsureBuffered(length - 2))
        {
            throw new InvalidImageContentException("JPEG metadata segment is truncated.");
        }

        this.metadataReader.ProcessSegment(marker, this.data.AsSpan(this.pos, length - 2));
        this.pos += length - 2;
    }

    /// <summary>
    /// Reads a table segment's length and buffers the whole segment, so the absolute end position returned stays
    /// valid while the segment is parsed.
    /// </summary>
    private int ReadTableSegmentEnd(string segmentName)
    {
        int length = this.ReadUInt16();
        if (length < 2 || !this.EnsureBuffered(length - 2))
        {
            throw new InvalidImageContentException($"JPEG {segmentName} segment is truncated.");
        }

        return this.pos + length - 2;
    }

    private void ReadQuantizationTables()
    {
        int end = this.ReadTableSegmentEnd("DQT");
        while (this.pos < end)
        {
            OperationExecution.Checkpoint();
            byte pqTq = this.ReadByte();
            int tablePrecision = pqTq >> 4;
            int id = pqTq & 0x0F;
            if (id > 3 || tablePrecision > 1)
            {
                throw new InvalidImageContentException($"Invalid quantization table specification: 0x{pqTq:X2}.");
            }

            // Every table must fit inside the segment length the file itself declared.
            if (this.pos + (tablePrecision == 0 ? 64 : 128) > end)
            {
                throw new InvalidImageContentException("JPEG DQT segment length does not cover its table.");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[JpegTables.ZigZag[i]] = tablePrecision == 0 ? this.ReadByte() : (ushort)this.ReadUInt16();
            }

            this.quantTables[id] = table;
        }
    }

    private void ReadHuffmanTables()
    {
        int end = this.ReadTableSegmentEnd("DHT");
        Span<byte> counts = stackalloc byte[16];
        while (this.pos < end)
        {
            OperationExecution.Checkpoint();
            byte tcTh = this.ReadByte();
            int tableClass = tcTh >> 4;
            int id = tcTh & 0x0F;
            if (tableClass > 1 || id > 3)
            {
                throw new InvalidImageContentException("Invalid Huffman table specification.");
            }

            if (this.pos + 16 > end)
            {
                throw new InvalidImageContentException("JPEG DHT segment is truncated.");
            }

            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                counts[i] = this.ReadByte();
                total += counts[i];
            }

            // A canonical Huffman table has at most 256 symbols (T.81 B.2.4.2), and they must lie inside the segment.
            if (total > 256 || this.pos + total > end)
            {
                throw new InvalidImageContentException("JPEG Huffman table has an invalid number of symbols.");
            }

            var values = new byte[total];
            for (int i = 0; i < total; i++)
            {
                values[i] = this.ReadByte();
            }

            var table = new HuffmanTable(counts, values);
            if (tableClass == 0)
            {
                this.dcTables[id] = table;
            }
            else
            {
                this.acTables[id] = table;
            }
        }
    }

    private void ReadFrame(bool isProgressive, bool isLossless)
    {
        this.ReadUInt16(); // Length
        int framePrecision = this.ReadByte();
        if (isLossless)
        {
            // T.81 H.4.1: lossless frames carry 2 to 16 bits per sample.
            if (framePrecision is < 2 or > 16)
            {
                throw new InvalidImageContentException($"Invalid lossless JPEG sample precision {framePrecision}.");
            }
        }
        else if (framePrecision is not (8 or 12))
        {
            throw new NotSupportedException($"JPEG sample precision {framePrecision} is not supported (only 8-bit and 12-bit).");
        }

        this.height = this.ReadUInt16();
        this.width = this.ReadUInt16();
        int componentCount = this.ReadByte();
        if (this.width <= 0 || this.height <= 0)
        {
            throw new InvalidImageContentException("Invalid JPEG dimensions.");
        }

        if (this.frameParsed)
        {
            throw new InvalidImageContentException("JPEG contains more than one frame header.");
        }

        if (componentCount is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"JPEG images with {componentCount} components are not supported.");
        }

        // A streaming decode declines every lossless frame once the header is read, so only a full decode throws.
        if (isLossless && this.scaleDenominator != 1 && this.input is null)
        {
            throw new NotSupportedException("Reduced decoding is not supported for lossless JPEG, which has no DCT blocks to reduce.");
        }

        // Reject oversized images before any plane memory is allocated.
        this.options.EnsureFrameWithinLimits(this.width, this.height, "JPEG");

        this.progressive = isProgressive;
        this.lossless = isLossless;
        this.precision = framePrecision;
        this.maxSample = (1 << framePrecision) - 1;
        this.components = new JpegComponent[componentCount];
        for (int i = 0; i < componentCount; i++)
        {
            byte id = this.ReadByte();
            byte hv = this.ReadByte();
            byte tq = this.ReadByte();
            var component = new JpegComponent
            {
                Id = id,
                H = hv >> 4,
                V = hv & 0x0F,
                QuantId = tq,
            };
            if (component.H is < 1 or > 4 || component.V is < 1 or > 4)
            {
                throw new InvalidImageContentException("Invalid JPEG sampling factors.");
            }

            // Lossless frames have no quantization; their Tq field is meaningless and ignored, as libjpeg does.
            if (tq > 3 && !isLossless)
            {
                throw new InvalidImageContentException($"Invalid JPEG quantization table selector: {tq}.");
            }

            this.maxH = Math.Max(this.maxH, component.H);
            this.maxV = Math.Max(this.maxV, component.V);
            this.components[i] = component;
        }

        if (isLossless)
        {
            this.LayOutLosslessFrame();
        }
        else
        {
            this.LayOutDctFrame();
        }

        // A streaming decode allocates a few MCU rows once it has seen the scan; everything else holds the frame.
        if (this.input is null)
        {
            this.AllocateFramePlanes();
        }

        this.frameParsed = true;
    }

    /// <summary>Computes the block grid and the (possibly reduced) sample plane geometry of every component.</summary>
    private void LayOutDctFrame()
    {
        this.mcusX = (this.width + (8 * this.maxH) - 1) / (8 * this.maxH);
        this.mcusY = (this.height + (8 * this.maxV) - 1) / (8 * this.maxV);

        foreach (JpegComponent component in this.components)
        {
            component.CompWidth = ((this.width * component.H) + this.maxH - 1) / this.maxH;
            component.CompHeight = ((this.height * component.V) + this.maxV - 1) / this.maxV;
            component.BlocksPerLine = (component.CompWidth + 7) / 8;
            component.BlocksPerColumn = (component.CompHeight + 7) / 8;
            component.BlocksPerLineTotal = this.mcusX * component.H;
            component.BlocksPerColumnTotal = this.mcusY * component.V;

            // Use a larger chroma IDCT when it eliminates power-of-two upsampling at the requested scale.
            int blockSize = this.outputBlockSize;
            while (blockSize < 8 && (this.maxH * this.outputBlockSize) % (component.H * blockSize * 2) == 0
                && (this.maxV * this.outputBlockSize) % (component.V * blockSize * 2) == 0)
            {
                blockSize *= 2;
            }

            component.BlockSize = blockSize;
            component.SampleH = component.H * blockSize / this.outputBlockSize;
            component.SampleV = component.V * blockSize / this.outputBlockSize;
            component.CompWidth = ((this.width * component.H * blockSize) + (this.maxH * 8) - 1) / (this.maxH * 8);
            component.CompHeight = ((this.height * component.V * blockSize) + (this.maxV * 8) - 1) / (this.maxV * 8);
            component.PlaneWidth = component.BlocksPerLineTotal * blockSize;
            component.PlaneHeight = component.BlocksPerColumnTotal * blockSize;
        }
    }

    /// <summary>
    /// Lossless frames have no blocks: an interleaved MCU holds H x V samples of each component (T.81 H.1.1), so the
    /// planes are padded to whole MCUs and nothing is ever reduced.
    /// </summary>
    private void LayOutLosslessFrame()
    {
        this.mcusX = (this.width + this.maxH - 1) / this.maxH;
        this.mcusY = (this.height + this.maxV - 1) / this.maxV;

        foreach (JpegComponent component in this.components)
        {
            component.CompWidth = ((this.width * component.H) + this.maxH - 1) / this.maxH;
            component.CompHeight = ((this.height * component.V) + this.maxV - 1) / this.maxV;
            component.BlockSize = 1;
            component.SampleH = component.H;
            component.SampleV = component.V;
            component.PlaneWidth = this.mcusX * component.H;
            component.PlaneHeight = this.mcusY * component.V;
        }
    }

    /// <summary>Allocates every component's whole sample plane (and progressive coefficient store).</summary>
    private void AllocateFramePlanes()
    {
        foreach (JpegComponent component in this.components)
        {
            long planeSize = (long)component.PlaneWidth * component.PlaneHeight;
            if (planeSize > int.MaxValue)
            {
                throw new InvalidImageContentException("JPEG component plane is too large to decode.");
            }

            if (this.WideSamples)
            {
                component.WidePlane = new ushort[planeSize];
            }
            else
            {
                component.Plane = new byte[planeSize];
            }

            if (this.progressive)
            {
                // Progressive scans refine coefficients across the whole image; every block's coefficients
                // (in zigzag order) are kept until the frame is complete.
                long coefficientCount = (long)component.BlocksPerLineTotal * component.BlocksPerColumnTotal * 64;
                if (coefficientCount > Array.MaxLength)
                {
                    throw new InvalidImageContentException("Progressive JPEG coefficient buffer is too large to decode.");
                }

                component.Coefficients = new short[coefficientCount];
            }
        }
    }

    // ----- Scan decoding -----

    private void DecodeScan()
    {
        JpegComponent[] scanComponents = this.ReadScanHeader();
        if (this.lossless)
        {
            this.DecodeLosslessScan(scanComponents);
        }
        else
        {
            this.DecodeDctScan(scanComponents);
        }

        this.scansDecoded++;
    }

    /// <summary>
    /// Reads an SOS header (the marker has been consumed), resolves the tables of the components it names and resets
    /// the entropy decoder for the scan's data.
    /// </summary>
    private JpegComponent[] ReadScanHeader()
    {
        if (!this.frameParsed)
        {
            throw new InvalidImageContentException("JPEG scan appeared before the frame header.");
        }

        this.ReadUInt16(); // Length
        int scanComponentCount = this.ReadByte();
        if (scanComponentCount is < 1 or > 4)
        {
            throw new InvalidImageContentException($"Invalid number of components in JPEG scan header: {scanComponentCount}.");
        }

        var scanComponents = new JpegComponent[scanComponentCount];
        for (int i = 0; i < scanComponentCount; i++)
        {
            byte cs = this.ReadByte();
            byte tdTa = this.ReadByte();
            JpegComponent? match = Array.Find(this.components, c => c.Id == cs);
            scanComponents[i] = match ?? throw new InvalidImageContentException($"Scan references unknown component {cs}.");
            match.DcTableId = tdTa >> 4;
            match.AcTableId = tdTa & 0x0F;
            if (match.DcTableId > 3 || match.AcTableId > 3)
            {
                throw new InvalidImageContentException(
                    this.arithmetic ? "Invalid JPEG arithmetic conditioning table selector in scan header." : "Invalid JPEG Huffman table selector in scan header.");
            }

            match.Pred = 0;

            // The quantization table is latched by the first scan that includes the component (as libjpeg does),
            // so a DQT segment appearing between scans cannot retroactively change already-decoded data. Lossless
            // frames are not quantized.
            if (!this.lossless && match.QuantTable is null)
            {
                match.QuantTable = this.quantTables[match.QuantId]
                    ?? throw new InvalidImageContentException($"JPEG quantization table {match.QuantId} is undefined.");
                match.Dequant = BuildDequant(match.QuantTable, match.BlockSize);
            }
        }

        int ss = this.ReadByte();
        int se = this.ReadByte();
        int ahAl = this.ReadByte();
        int ah = ahAl >> 4;
        int al = ahAl & 0x0F;

        if (this.lossless)
        {
            this.PrepareLosslessScan(scanComponents, ss, al);
        }
        else if (this.progressive)
        {
            this.PrepareProgressiveScan(scanComponents, ss, se, ah, al);
        }
        else
        {
            // Sequential scans always cover the full spectrum without approximation; like libjpeg we tolerate
            // encoders that fill in other values.
            this.scanKind = ScanKind.Sequential;
            foreach (JpegComponent component in scanComponents)
            {
                if (this.arithmetic)
                {
                    continue; // Arithmetic scans use adaptive statistics, not Huffman tables.
                }

                component.DcTable = this.dcTables[component.DcTableId]
                    ?? throw new InvalidImageContentException($"JPEG DC Huffman table {component.DcTableId} is undefined.");
                component.AcTable = this.acTables[component.AcTableId]
                    ?? throw new InvalidImageContentException($"JPEG AC Huffman table {component.AcTableId} is undefined.");
            }
        }

        this.bitCount = 0;
        this.markerPending = false;
        this.insufficientData = false;
        this.eobRun = 0;
        if (this.arithmetic)
        {
            this.ResetArithmeticDecoder(scanComponents);
        }

        return scanComponents;
    }

    /// <summary>
    /// The multipliers that dequantize a block for its inverse transform: the AAN-scaled table of
    /// <see cref="JpegIdct"/> for full 8x8 blocks, the plain quantization steps for the reduced transforms.
    /// </summary>
    private static float[] BuildDequant(ushort[] quant, int blockSize)
    {
        if (blockSize == 8)
        {
            return JpegIdct.BuildDequantTable(quant);
        }

        var table = new float[64];
        for (int i = 0; i < 64; i++)
        {
            table[i] = quant[i];
        }

        return table;
    }

    private void DecodeDctScan(JpegComponent[] scanComponents)
    {
        if (scanComponents.Length == 1)
        {
            // Non-interleaved: the scan covers the component's own block grid (ceil(compWidth / 8) blocks per
            // line), which is narrower than the MCU-padded grid when H < maxH.
            JpegComponent component = scanComponents[0];
            int totalBlocks = component.BlocksPerLine * component.BlocksPerColumn;
            int decoded = 0;
            for (int by = 0; by < component.BlocksPerColumn; by++)
            {
                OperationExecution.Checkpoint();
                this.DecodeBlockRow(component, by, ref decoded, totalBlocks, scanComponents);
            }
        }
        else
        {
            int totalMcus = this.mcusX * this.mcusY;
            int decoded = 0;
            for (int my = 0; my < this.mcusY; my++)
            {
                OperationExecution.Checkpoint();
                this.DecodeMcuRow(scanComponents, my, ref decoded, totalMcus);
            }
        }
    }

    /// <summary>Decodes one row of blocks of a non-interleaved scan; <paramref name="decoded"/> counts blocks for restarts.</summary>
    private void DecodeBlockRow(JpegComponent component, int by, ref int decoded, int totalBlocks, JpegComponent[] scanComponents)
    {
        for (int bx = 0; bx < component.BlocksPerLine; bx++)
        {
            this.DecodeBlock(component, bx, by);
            decoded++;
            this.HandleRestart(decoded, totalBlocks, scanComponents);
        }
    }

    /// <summary>Decodes one row of MCUs of an interleaved scan; <paramref name="decoded"/> counts MCUs for restarts.</summary>
    private void DecodeMcuRow(JpegComponent[] scanComponents, int my, ref int decoded, int totalMcus)
    {
        for (int mx = 0; mx < this.mcusX; mx++)
        {
            foreach (JpegComponent component in scanComponents)
            {
                for (int v = 0; v < component.V; v++)
                {
                    for (int h = 0; h < component.H; h++)
                    {
                        this.DecodeBlock(component, (mx * component.H) + h, (my * component.V) + v);
                    }
                }
            }

            decoded++;
            this.HandleRestart(decoded, totalMcus, scanComponents);
        }
    }

    private void HandleRestart(int decodedUnits, int totalUnits, JpegComponent[] scanComponents)
    {
        if (this.restartInterval <= 0 || decodedUnits >= totalUnits || decodedUnits % this.restartInterval != 0)
        {
            return;
        }

        // Byte-align and consume the expected RSTn marker.
        this.bitCount = 0;
        this.markerPending = false;
        if (this.arithmetic)
        {
            this.SkipToMarkerAfterArithmeticSegment();
        }

        if (this.EnsureBuffered(2) && this.data[this.pos] == 0xFF)
        {
            byte marker = this.data[this.pos + 1];
            if (marker is >= 0xD0 and <= 0xD7)
            {
                this.pos += 2;
                this.insufficientData = false;
                this.eobRun = 0;
                foreach (JpegComponent component in scanComponents)
                {
                    component.Pred = 0;
                }

                if (this.arithmetic)
                {
                    this.ResetArithmeticDecoder(scanComponents);
                }

                return;
            }
        }

        throw new InvalidImageContentException("Expected a JPEG restart marker that was not found.");
    }

    /// <summary>Decodes one 8x8 block of the current scan; dispatches on the scan kind.</summary>
    private void DecodeBlock(JpegComponent component, int bx, int by)
    {
        if (this.insufficientData)
        {
            // The entropy-coded segment ended early (marker or end of data): like libjpeg, leave the remaining
            // blocks of this interval untouched instead of decoding noise.
            return;
        }

        switch (this.scanKind)
        {
            case ScanKind.Sequential when this.arithmetic:
                this.DecodeSequentialBlockArithmetic(component, bx, by);
                break;
            case ScanKind.Sequential:
                this.DecodeSequentialBlock(component, bx, by);
                break;
            case ScanKind.DcFirst:
                this.DecodeDcFirst(component, bx, by);
                break;
            case ScanKind.DcRefine:
                this.DecodeDcRefine(component, bx, by);
                break;
            case ScanKind.AcFirst:
                this.DecodeAcFirst(component, bx, by);
                break;
            default:
                this.DecodeAcRefine(component, bx, by);
                break;
        }
    }

    private void DecodeSequentialBlock(JpegComponent component, int bx, int by)
    {
        float[] dequant = component.Dequant!;
        HuffmanTable dc = component.DcTable!;
        HuffmanTable ac = component.AcTable!;
        Span<float> block = this.blockScratch;
        block.Clear();

        // DC coefficient.
        int t = this.DecodeHuffman(dc);
        int diff = t == 0 ? 0 : Extend(this.Receive(t), t);
        component.Pred += diff;
        block[0] = component.Pred * dequant[0];

        // AC coefficients.
        bool anyAc = false;
        int k = 1;
        while (k < 64)
        {
            int rs = this.DecodeHuffman(ac);
            int run = rs >> 4;
            int size = rs & 0x0F;
            if (size == 0)
            {
                if (run != 15)
                {
                    break; // EOB
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > 63)
            {
                throw new InvalidImageContentException("JPEG AC coefficient index out of range.");
            }

            int natural = JpegTables.ZigZag[k];
            block[natural] = Extend(this.Receive(size), size) * dequant[natural];
            anyAc = true;
            k++;
        }

        if (!anyAc)
        {
            // A flat block is by far the most common case in smooth areas and in chroma planes.
            this.WriteFlatBlock(component, bx, by, FlatValue(component, block[0]));
            return;
        }

        this.WriteBlock(component, bx, by, block);
    }

    /// <summary>
    /// The value every sample of a block takes when only its DC coefficient is non-zero, given the dequantized DC
    /// coefficient. The AAN table already folds in the 1/8 of the two passes, and evaluating the factorised
    /// transform on such a block reproduces its input exactly, so for full-size blocks this is the DC term itself;
    /// the reduced transforms take plain quantization steps, so their flat value is DC / 8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FlatValue(JpegComponent component, float dc) => component.BlockSize == 8 ? dc : dc * 0.125f;

    /// <summary>
    /// Inverse-transforms a dequantized block (clobbering it) and writes the level-shifted, rounded and clamped
    /// samples into the component's sample plane.
    /// </summary>
    private void WriteBlock(JpegComponent component, int bx, int by, Span<float> block)
    {
        int size = component.BlockSize;
        int offset = component.RowOffset(by * size) + (bx * size);
        int stride = component.PlaneWidth;

        if (this.WideSamples)
        {
            if (size == 8)
            {
                JpegIdct.TransformToWide(block, component.WidePlane.AsSpan(offset), stride, this.maxSample);
                return;
            }

            JpegReducedIdct.Transform(block, this.tempScratch, size);
            float center = ((this.maxSample + 1) >> 1) + 0.5f;
            ushort[] widePlane = component.WidePlane;
            for (int y = 0; y < size; y++)
            {
                Span<ushort> target = widePlane.AsSpan(offset + (y * stride), size);
                for (int x = 0; x < size; x++)
                {
                    target[x] = (ushort)(int)Math.Clamp(block[(y * size) + x] + center, 0f, this.maxSample);
                }
            }

            return;
        }

        if (size == 8)
        {
            JpegIdct.TransformToBytes(block, component.Plane.AsSpan(offset), stride);
            return;
        }

        JpegReducedIdct.Transform(block, this.tempScratch, size);
        byte[] plane = component.Plane;
        for (int y = 0; y < size; y++)
        {
            Span<byte> target = plane.AsSpan(offset + (y * stride), size);
            for (int x = 0; x < size; x++)
            {
                target[x] = (byte)(int)Math.Clamp(block[(y * size) + x] + 128.5f, 0f, 255f);
            }
        }
    }

    /// <summary>Writes the constant a DC-only block reconstructs to, without running the transform.</summary>
    private void WriteFlatBlock(JpegComponent component, int bx, int by, float value)
    {
        int size = component.BlockSize;
        int offset = component.RowOffset(by * size) + (bx * size);
        int stride = component.PlaneWidth;
        if (this.WideSamples)
        {
            float center = ((this.maxSample + 1) >> 1) + 0.5f;
            ushort wideSample = (ushort)(int)Math.Clamp(value + center, 0f, this.maxSample);
            for (int y = 0; y < size; y++)
            {
                component.WidePlane.AsSpan(offset + (y * stride), size).Fill(wideSample);
            }

            return;
        }

        byte sample = (byte)(int)Math.Clamp(value + 128.5f, 0f, 255f);
        byte[] plane = component.Plane;
        for (int y = 0; y < size; y++)
        {
            plane.AsSpan(offset + (y * stride), size).Fill(sample);
        }
    }

    // ----- Huffman decoding and bit reader -----

    /// <summary>
    /// Decodes one Huffman symbol. Once the entropy-coded data is exhausted this returns 0 (a zero-size DC
    /// difference or an EOB), which ends the current block cleanly; subsequent blocks are then skipped.
    /// </summary>
    /// <remarks>
    /// Most symbols are eight bits or shorter, so they resolve in one table read. Longer codes, and codes
    /// that reach past the end of the buffered bits, fall through to the canonical length-by-length walk of
    /// ITU-T T.81 F.2.2.3, which is also what an over-subscribed table lands on.
    /// </remarks>
    private int DecodeHuffman(HuffmanTable table)
    {
        if (this.insufficientData)
        {
            return 0;
        }

        if (this.bitCount < 16)
        {
            this.FillBits();
        }

        if (this.bitCount >= HuffmanTable.LookaheadBits)
        {
            int peek = (int)((this.bitBuffer >> (this.bitCount - HuffmanTable.LookaheadBits)) & HuffmanTable.LookaheadMask);
            int size = table.LookaheadLength[peek];
            if (size != 0)
            {
                this.bitCount -= size;
                return table.LookaheadValue[peek];
            }
        }

        int code = this.ReadBit();
        int length = 1;
        while (code > table.MaxCode[length])
        {
            if (length == 16)
            {
                if (this.insufficientData)
                {
                    return 0;
                }

                throw new InvalidImageContentException("Corrupt JPEG Huffman data.");
            }

            code = (code << 1) | this.ReadBit();
            length++;
        }

        if (this.insufficientData)
        {
            return 0;
        }

        int index = table.ValPtr[length] + code - table.MinCode[length];
        if ((uint)index >= (uint)table.Values.Length)
        {
            throw new InvalidImageContentException("Corrupt JPEG Huffman data.");
        }

        return table.Values[index];
    }

    private int Receive(int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (this.bitCount < length)
        {
            this.FillBits();
        }

        if (this.bitCount >= length)
        {
            this.bitCount -= length;
            return (int)((this.bitBuffer >> this.bitCount) & ((1UL << length) - 1));
        }

        int value = 0;
        for (int i = 0; i < length; i++)
        {
            value = (value << 1) | this.ReadBit();
        }

        return value;
    }

    private static int Extend(int value, int length)
        => value < 1 << (length - 1) ? value - (1 << length) + 1 : value;

    /// <summary>
    /// Tops the accumulator up with whole bytes of entropy-coded data, unstuffing <c>FF 00</c> pairs on the
    /// way. Any other <c>FF xx</c> ends the segment: the marker is left unconsumed for the marker loop, so
    /// <see cref="pos"/> points at it exactly as it did when bits were pulled one byte at a time.
    /// </summary>
    private void FillBits()
    {
        while (this.bitCount <= 56)
        {
            if (this.markerPending)
            {
                return;
            }

            // Fewer than two bytes buffered: a streaming decode refills its window; at the true end of the data a
            // lone final byte is still consumed below, with a missing successor read as the end of the image.
            if (this.dataLength - this.pos < 2 && !this.Refill(2) && this.pos >= this.dataLength)
            {
                return;
            }

            byte b = this.data[this.pos];
            if (b == 0xFF)
            {
                byte next = this.pos + 1 < this.dataLength ? this.data[this.pos + 1] : (byte)0xD9;
                if (next != 0x00)
                {
                    this.markerPending = true;
                    return;
                }

                this.pos += 2; // Skip the stuffed zero byte.
            }
            else
            {
                this.pos++;
            }

            this.bitBuffer = (this.bitBuffer << 8) | b;
            this.bitCount += 8;
        }
    }

    private int ReadBit()
    {
        if (this.bitCount == 0)
        {
            this.FillBits();
            if (this.bitCount == 0)
            {
                this.insufficientData = true;
                return 0;
            }
        }

        this.bitCount--;
        return (int)(this.bitBuffer >> this.bitCount) & 1;
    }

    private sealed class JpegComponent
    {
        public int BlockSize = 8;
        public int SampleH;
        public int SampleV;
        public int Id;
        public int H;
        public int V;
        public int QuantId;
        public int DcTableId;
        public int AcTableId;
        public int Pred;

        /// <summary>The DC conditioning context of an arithmetic scan (T.81 F.1.4.4.1.2): 0, 4, 8, 12 or 16.</summary>
        public int DcContext;

        /// <summary>Width of the component in samples: ceil(imageWidth * H / maxH).</summary>
        public int CompWidth;

        /// <summary>Height of the component in samples: ceil(imageHeight * V / maxV).</summary>
        public int CompHeight;

        /// <summary>Blocks per line covered by a non-interleaved scan: ceil(CompWidth / 8).</summary>
        public int BlocksPerLine;

        /// <summary>Block rows covered by a non-interleaved scan: ceil(CompHeight / 8).</summary>
        public int BlocksPerColumn;

        /// <summary>Blocks per line in the MCU-padded plane (mcusX * H); interleaved scans cover all of them.</summary>
        public int BlocksPerLineTotal;

        /// <summary>Block rows in the MCU-padded plane (mcusY * V).</summary>
        public int BlocksPerColumnTotal;

        public int PlaneWidth;

        /// <summary>Rows of the MCU-padded plane, whether or not they are all held in memory at once.</summary>
        public int PlaneHeight;

        /// <summary>
        /// Zero when the whole plane is resident. In streaming mode, the rows one MCU row contributes
        /// (V * BlockSize): the plane then holds just three such strips, MCU row m in slot m % 3.
        /// </summary>
        public int StripRows;

        /// <summary>Decoded 8-bit samples (8-bit DCT frames).</summary>
        public byte[] Plane = Array.Empty<byte>();

        /// <summary>Decoded samples of frames wider than 8 bits (12-bit DCT and every lossless frame).</summary>
        public ushort[] WidePlane = Array.Empty<ushort>();

        /// <summary>Progressive only: quantized coefficients of every block, 64 per block in zigzag order.</summary>
        public short[] Coefficients = Array.Empty<short>();

        /// <summary>Quantization table latched by the first scan that includes this component.</summary>
        public ushort[]? QuantTable;

        /// <summary>Dequantization multipliers for the inverse transform of <see cref="BlockSize"/>, natural order.</summary>
        public float[]? Dequant;

        public HuffmanTable? DcTable;
        public HuffmanTable? AcTable;

        public int CoefficientOffset(int bx, int by) => ((by * this.BlocksPerLineTotal) + bx) * 64;

        /// <summary>Offset of plane row <paramref name="row"/> (in full-plane coordinates) within the resident buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RowOffset(int row)
        {
            if (this.StripRows == 0)
            {
                return row * this.PlaneWidth;
            }

            int strip = row / this.StripRows;
            return (((strip % 3) * this.StripRows) + (row - (strip * this.StripRows))) * this.PlaneWidth;
        }
    }

    /// <summary>Canonical Huffman decoding table (ITU-T T.81 F.2.2.3) plus a short-code lookahead.</summary>
    private sealed class HuffmanTable
    {
        /// <summary>Number of bits the lookahead table is indexed by.</summary>
        public const int LookaheadBits = 8;

        /// <summary>Mask selecting <see cref="LookaheadBits"/> bits.</summary>
        public const int LookaheadMask = (1 << LookaheadBits) - 1;

        public readonly int[] MinCode = new int[17];
        public readonly int[] MaxCode = new int[17];
        public readonly int[] ValPtr = new int[17];
        public readonly byte[] Values;

        /// <summary>Code length for each 8-bit peek, or 0 when no code of at most 8 bits starts with it.</summary>
        public readonly byte[] LookaheadLength = new byte[1 << LookaheadBits];

        /// <summary>Decoded symbol for each 8-bit peek that <see cref="LookaheadLength"/> resolves.</summary>
        public readonly byte[] LookaheadValue = new byte[1 << LookaheadBits];

        public HuffmanTable(ReadOnlySpan<byte> counts, byte[] values)
        {
            this.Values = values;
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = counts[length - 1];
                this.ValPtr[length] = k;
                this.MinCode[length] = code;
                this.MaxCode[length] = count > 0 ? code + count - 1 : -1;

                // Every peek whose leading bits are one of this length's codes resolves to that code's
                // symbol. Entries are only filled when the code and its symbol are both in range, so an
                // over-subscribed table leaves them at zero and falls through to the canonical walk, which
                // reports the corruption exactly as before.
                if (length <= LookaheadBits)
                {
                    int span = 1 << (LookaheadBits - length);
                    for (int i = 0; i < count; i++)
                    {
                        int prefix = (code + i) << (LookaheadBits - length);
                        if (k + i >= values.Length || prefix < 0 || prefix + span > (1 << LookaheadBits))
                        {
                            continue;
                        }

                        this.LookaheadLength.AsSpan(prefix, span).Fill((byte)length);
                        this.LookaheadValue.AsSpan(prefix, span).Fill(values[k + i]);
                    }
                }

                code = (code + count) << 1;
                k += count;
            }
        }
    }
}
