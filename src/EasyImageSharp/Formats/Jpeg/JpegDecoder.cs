using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Decodes Huffman-coded JPEG images with 8-bit samples: baseline and extended sequential frames (SOF0/SOF1)
/// as well as progressive frames (SOF2, including successive approximation); grayscale, YCbCr and RGB color;
/// Adobe CMYK and YCCK; every chroma subsampling layout (4:2:2 and 4:2:0 are reconstructed with libjpeg-style
/// triangle "fancy" upsampling, other ratios by sample replication); restart markers; and multi-scan
/// non-interleaved files. Arithmetic-coded, lossless, hierarchical and 12-bit frames are not supported.
/// </summary>
/// <remarks>
/// Progressive images keep the quantized coefficients of every block until the last scan has been read
/// (two bytes per coefficient per component), so they need roughly three times the memory of a baseline
/// image of the same size while decoding. A stream that ends before its EOI marker is treated as truncated
/// and rejected with <see cref="InvalidImageContentException"/> for both frame types.
/// </remarks>
public sealed class JpegDecoder : IImageDecoder
{
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            // The parser needs an array (it is captured by the parallel colour-conversion pass, which a
            // ref struct could not be), but it does not need a fresh one: a pooled buffer avoids an
            // allocation the size of the whole file on every decode.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(buffer);
                var core = new JpegDecoderCore(buffer, data.Length, options);
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
        bool sawFrame = false;
        bool progressive = false;
        int adobeTransform = -1;
        bool rgbIds = false;

        int pos = 2;
        while (pos + 4 <= data.Length)
        {
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
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 5)..]);
                components = data[pos + 7];
                progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
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
                    case 0xE0 or 0xE1 or 0xE2 or 0xFE:
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
        return new ImageInfo(width, height, components * 8, 1, ImageFormat.Jpeg, metadataReader.Finish());
    }

    /// <summary>Reads table 0 of a DQT payload in natural order, or null when the payload does not hold it intact.</summary>
    private static ushort[]? ParseFirstQuantTable(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
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
/// JPEG decode state machine: marker parsing, entropy decoding of sequential and progressive scans and the
/// final color conversion. Progressive-specific block decoding lives in <c>JpegDecoder.Progressive.cs</c>.
/// </summary>
internal sealed partial class JpegDecoderCore
{
    private readonly byte[] data;

    /// <summary>Bytes of <see cref="data"/> that hold the image; the buffer itself may be longer (pooled).</summary>
    private readonly int dataLength;
    private readonly DecoderOptions options;
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

    public JpegDecoderCore(byte[] data, int dataLength, DecoderOptions options)
    {
        this.data = data;
        this.dataLength = dataLength;
        this.options = options;
    }

    private enum ScanKind
    {
        Sequential,
        DcFirst,
        DcRefine,
        AcFirst,
        AcRefine,
    }

    public int Width => this.width;

    public int Height => this.height;

    public void ParseAndDecode()
    {
        if (this.dataLength < 4 || this.data[0] != 0xFF || this.data[1] != 0xD8)
        {
            throw new InvalidImageContentException("Missing JPEG SOI marker.");
        }

        this.pos = 2;
        bool reachedEoi = false;
        while (!reachedEoi)
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

                    reachedEoi = true;
                    break;

                case 0xC0: // SOF0 baseline
                case 0xC1: // SOF1 extended sequential (identical handling)
                case 0xC2: // SOF2 progressive
                    this.ReadFrame(isProgressive: marker == 0xC2);
                    break;

                case 0xC3:
                case >= 0xC5 and <= 0xC7:
                case >= 0xC9 and <= 0xCB:
                case >= 0xCD and <= 0xCF:
                    throw new NotSupportedException(
                        $"JPEG frame type 0x{marker:X2} (lossless, hierarchical or arithmetic-coded) is not supported.");

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
                    this.DecodeScan();
                    break;

                case 0xEE: // APP14 (Adobe)
                    this.ReadAdobeMarker();
                    break;

                case 0xE0: // APP0 (JFIF)
                case 0xE1: // APP1 (EXIF / XMP)
                case 0xE2: // APP2 (ICC)
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
        }

        if (this.progressive)
        {
            this.ReconstructProgressiveFrame();
        }
    }

    public Image<TPixel> ToImage<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (!this.frameParsed)
        {
            throw new InvalidImageContentException("JPEG image contains no frame.");
        }

        int componentCount = this.components.Length;
        if (componentCount is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"JPEG images with {componentCount} components are not supported.");
        }

        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(this.width, this.height);

        bool rgbIds = componentCount == 3
            && this.components[0].Id == 'R' && this.components[1].Id == 'G' && this.components[2].Id == 'B';
        bool isRgb = componentCount == 3 && (this.adobeTransform == 0 || rgbIds);

        // Four components: Adobe transform 0 is CMYK, 2 is YCCK (any other nonzero value is treated as YCCK,
        // as libjpeg does); without an APP14 marker the data is plain CMYK.
        bool isYcck = componentCount == 4 && this.adobeTransform > 0;

        this.metadataReader.SetFrame(this.progressive, JpegDecoder.ClassifyColorType(componentCount, this.adobeTransform, rgbIds));
        this.metadataReader.SetLuminanceQuantTable(this.quantTables[this.components[0].QuantId]);
        ImageMetadata metadata = this.metadataReader.Finish();

        // Adobe writers store CMYK "inverted" (255 - ink); the APP14 marker signals that convention.
        bool adobeInverted = this.adobeTransform >= 0;

        // Subsampled components are upsampled one full-resolution row at a time into a scratch row;
        // full-resolution components are read straight from their plane. Rows are independent, so each
        // batch gets its own scratch and they run in parallel.
        int fullWidth = this.mcusX * this.maxH * 8;
        int imageWidth = this.width;
        ParallelRowIterator.IterateRows(imageWidth, this.height, (startRow, endRow) =>
        {
            byte[]?[] scratch = new byte[componentCount][];
            for (int i = 0; i < componentCount; i++)
            {
                JpegComponent c = this.components[i];
                scratch[i] = c.H == this.maxH && c.V == this.maxV ? null : ArrayPool<byte>.Shared.Rent(fullWidth);
            }

            Rgba32[] rgbaRow = ArrayPool<Rgba32>.Shared.Rent(imageWidth);
            try
            {
                Span<Rgba32> row = rgbaRow.AsSpan(0, imageWidth);
                for (int y = startRow; y < endRow; y++)
                {
                    switch (componentCount)
                    {
                        case 1:
                        {
                            // A single component is luminance: reinterpreting it as L8 makes the write a
                            // bulk broadcast straight into the destination row.
                            ReadOnlySpan<byte> r0 = this.GetComponentRow(this.components[0], y, scratch[0]);
                            PixelOps.Convert<L8, TPixel>(
                                MemoryMarshal.Cast<byte, L8>(r0[..imageWidth]), frame.GetRowSpan(y));
                            continue;
                        }

                        case 3:
                        {
                            ReadOnlySpan<byte> r0 = this.GetComponentRow(this.components[0], y, scratch[0]);
                            ReadOnlySpan<byte> r1 = this.GetComponentRow(this.components[1], y, scratch[1]);
                            ReadOnlySpan<byte> r2 = this.GetComponentRow(this.components[2], y, scratch[2]);
                            if (isRgb)
                            {
                                for (int x = 0; x < imageWidth; x++)
                                {
                                    row[x] = new Rgba32(r0[x], r1[x], r2[x]);
                                }
                            }
                            else
                            {
                                YCbCrRowToRgba(r0, r1, r2, row);
                            }

                            break;
                        }

                        default:
                        {
                            ReadOnlySpan<byte> r0 = this.GetComponentRow(this.components[0], y, scratch[0]);
                            ReadOnlySpan<byte> r1 = this.GetComponentRow(this.components[1], y, scratch[1]);
                            ReadOnlySpan<byte> r2 = this.GetComponentRow(this.components[2], y, scratch[2]);
                            ReadOnlySpan<byte> r3 = this.GetComponentRow(this.components[3], y, scratch[3]);
                            for (int x = 0; x < imageWidth; x++)
                            {
                                int s0 = r0[x];
                                int s1 = r1[x];
                                int s2 = r2[x];
                                int s3 = r3[x];
                                if (isYcck)
                                {
                                    // YCC -> RGB yields the inverted CMY channels of an Adobe CMYK image; K passes through.
                                    Rgba32 cmy = YCbCrToRgb(s0, s1, s2);
                                    row[x] = InvertedCmykToRgb(255 - cmy.R, 255 - cmy.G, 255 - cmy.B, s3);
                                }
                                else if (adobeInverted)
                                {
                                    row[x] = InvertedCmykToRgb(s0, s1, s2, s3);
                                }
                                else
                                {
                                    row[x] = InvertedCmykToRgb(255 - s0, 255 - s1, 255 - s2, 255 - s3);
                                }
                            }

                            break;
                        }
                    }

                    PixelOps.FromRgba32<TPixel>(row, frame.GetRowSpan(y));
                }
            }
            finally
            {
                ArrayPool<Rgba32>.Shared.Return(rgbaRow);
                for (int i = 0; i < componentCount; i++)
                {
                    if (scratch[i] is byte[] buffer)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        });

        return new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, metadata);
    }

    /// <summary>Returns row <paramref name="y"/> of the component at full image resolution.</summary>
    private ReadOnlySpan<byte> GetComponentRow(JpegComponent c, int y, byte[]? scratch)
    {
        if (scratch is null)
        {
            return c.Plane.AsSpan(y * c.PlaneWidth, this.width);
        }

        JpegUpsampler.UpsampleRow(
            c.Plane, c.PlaneWidth, c.PlaneHeight, c.CompWidth, c.CompHeight,
            c.H, c.V, this.maxH, this.maxV, y, this.width, scratch);
        return scratch;
    }

    /// <summary>
    /// Converts a row of Y, Cb and Cr samples to RGB. The vector path evaluates exactly the expression
    /// <see cref="YCbCrToRgb"/> uses, in the same order and with the same truncating conversion, so it
    /// produces the same bytes.
    /// </summary>
    private static void YCbCrRowToRgba(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<Rgba32> destination)
    {
        int width = destination.Length;
        int x = 0;

        if (SimdConfig.Vector256Enabled && width >= Vector256<float>.Count)
        {
            ref byte yRef = ref MemoryMarshal.GetReference(y);
            ref byte cbRef = ref MemoryMarshal.GetReference(cb);
            ref byte crRef = ref MemoryMarshal.GetReference(cr);
            ref uint destRef = ref Unsafe.As<Rgba32, uint>(ref MemoryMarshal.GetReference(destination));

            Vector256<float> half = Vector256.Create(0.5f);
            Vector256<float> offset = Vector256.Create(128f);
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> max = Vector256.Create(255);
            Vector256<uint> opaque = Vector256.Create(0xFF000000u);

            for (; x <= width - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                Vector256<float> luma = WidenEight(ref yRef, x);
                Vector256<float> blue = WidenEight(ref cbRef, x) - offset;
                Vector256<float> red = WidenEight(ref crRef, x) - offset;

                Vector256<float> r = luma + (red * Vector256.Create(1.402f)) + half;
                Vector256<float> g = luma - (blue * Vector256.Create(0.344136f)) - (red * Vector256.Create(0.714136f)) + half;
                Vector256<float> b = luma + (blue * Vector256.Create(1.772f)) + half;

                Vector256<uint> packed = Clamp(r, zero, max)
                    | Vector256.ShiftLeft(Clamp(g, zero, max), 8)
                    | Vector256.ShiftLeft(Clamp(b, zero, max), 16)
                    | opaque;
                packed.StoreUnsafe(ref destRef, (nuint)x);
            }
        }

        for (; x < width; x++)
        {
            destination[x] = YCbCrToRgb(y[x], cb[x], cr[x]);
        }

        static Vector256<uint> Clamp(Vector256<float> value, Vector256<int> zero, Vector256<int> max)
            => Vector256.Max(Vector256.Min(Vector256.ConvertToInt32(value), max), zero).AsUInt32();
    }

    /// <summary>Widens eight consecutive bytes starting at <paramref name="index"/> to single precision.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> WidenEight(ref byte source, int index)
    {
        ulong packed = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, (uint)index));
        (Vector128<ushort> low, _) = Vector128.Widen(Vector128.CreateScalar(packed).AsByte());
        (Vector128<uint> first, Vector128<uint> second) = Vector128.Widen(low);
        return Vector256.ConvertToSingle(Vector256.Create(first.AsInt32(), second.AsInt32()));
    }
    private static Rgba32 YCbCrToRgb(int y, int cb, int cr)
    {
        float yf = y;
        float cbf = cb - 128f;
        float crf = cr - 128f;
        int r = (int)(yf + (1.402f * crf) + 0.5f);
        int g = (int)(yf - (0.344136f * cbf) - (0.714136f * crf) + 0.5f);
        int b = (int)(yf + (1.772f * cbf) + 0.5f);
        return new Rgba32(
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255));
    }

    /// <summary>
    /// Converts inverted CMYK samples (each stored as 255 - ink coverage, the Adobe convention) to RGB with
    /// the usual naive model R = (255 - C)(255 - K) / 255, rounded to nearest.
    /// </summary>
    private static Rgba32 InvertedCmykToRgb(int c, int m, int y, int k)
        => new Rgba32(MulDiv255(c, k), MulDiv255(m, k), MulDiv255(y, k));

    /// <summary>Computes round(a * b / 255) for a, b in 0..255.</summary>
    private static byte MulDiv255(int a, int b)
    {
        int t = (a * b) + 128;
        return (byte)((t + (t >> 8)) >> 8);
    }

    // ----- Marker/segment parsing -----

    private bool TryNextMarker(out byte marker)
    {
        while (this.pos + 1 < this.dataLength)
        {
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
        if (this.pos + 2 > this.dataLength)
        {
            throw new InvalidImageContentException("Unexpected end of JPEG data.");
        }

        int value = (this.data[this.pos] << 8) | this.data[this.pos + 1];
        this.pos += 2;
        return value;
    }

    private byte ReadByte()
    {
        if (this.pos >= this.dataLength)
        {
            throw new InvalidImageContentException("Unexpected end of JPEG data.");
        }

        return this.data[this.pos++];
    }

    private void SkipSegment()
    {
        int length = this.ReadUInt16();
        if (length < 2 || this.pos + length - 2 > this.dataLength)
        {
            throw new InvalidImageContentException("JPEG segment is truncated.");
        }

        this.pos += length - 2;
    }

    private void ReadAdobeMarker()
    {
        int length = this.ReadUInt16();
        if (length < 2 || this.pos + length - 2 > this.dataLength)
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
        if (length < 2 || this.pos + length - 2 > this.dataLength)
        {
            throw new InvalidImageContentException("JPEG metadata segment is truncated.");
        }

        this.metadataReader.ProcessSegment(marker, this.data.AsSpan(this.pos, length - 2));
        this.pos += length - 2;
    }

    private int ReadTableSegmentEnd(string segmentName)
    {
        int length = this.ReadUInt16();
        if (length < 2 || this.pos + length - 2 > this.dataLength)
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
            byte pqTq = this.ReadByte();
            int precision = pqTq >> 4;
            int id = pqTq & 0x0F;
            if (id > 3 || precision > 1)
            {
                throw new InvalidImageContentException($"Invalid quantization table specification: 0x{pqTq:X2}.");
            }

            // Every table must fit inside the segment length the file itself declared.
            if (this.pos + (precision == 0 ? 64 : 128) > end)
            {
                throw new InvalidImageContentException("JPEG DQT segment length does not cover its table.");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[JpegTables.ZigZag[i]] = precision == 0 ? this.ReadByte() : (ushort)this.ReadUInt16();
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

    private void ReadFrame(bool isProgressive)
    {
        this.ReadUInt16(); // Length
        int precision = this.ReadByte();
        if (precision != 8)
        {
            throw new NotSupportedException($"JPEG sample precision {precision} is not supported (only 8-bit).");
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

        // Reject oversized images before any plane memory is allocated.
        this.options.EnsureFrameWithinLimits(this.width, this.height, "JPEG");

        this.progressive = isProgressive;
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

            if (tq > 3)
            {
                throw new InvalidImageContentException($"Invalid JPEG quantization table selector: {tq}.");
            }

            this.maxH = Math.Max(this.maxH, component.H);
            this.maxV = Math.Max(this.maxV, component.V);
            this.components[i] = component;
        }

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
            component.PlaneWidth = component.BlocksPerLineTotal * 8;
            component.PlaneHeight = component.BlocksPerColumnTotal * 8;
            long planeSize = (long)component.PlaneWidth * component.PlaneHeight;
            if (planeSize > int.MaxValue)
            {
                throw new InvalidImageContentException("JPEG component plane is too large to decode.");
            }

            component.Plane = new byte[planeSize];

            if (isProgressive)
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

        this.frameParsed = true;
    }

    // ----- Scan decoding -----

    private void DecodeScan()
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
                throw new InvalidImageContentException("Invalid JPEG Huffman table selector in scan header.");
            }

            match.Pred = 0;

            // The quantization table is latched by the first scan that includes the component (as libjpeg does),
            // so a DQT segment appearing between scans cannot retroactively change already-decoded data.
            match.QuantTable ??= this.quantTables[match.QuantId]
                ?? throw new InvalidImageContentException($"JPEG quantization table {match.QuantId} is undefined.");
        }

        int ss = this.ReadByte();
        int se = this.ReadByte();
        int ahAl = this.ReadByte();
        int ah = ahAl >> 4;
        int al = ahAl & 0x0F;

        if (this.progressive)
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

        if (scanComponentCount == 1)
        {
            // Non-interleaved: the scan covers the component's own block grid (ceil(compWidth / 8) blocks per
            // line), which is narrower than the MCU-padded grid when H < maxH.
            JpegComponent component = scanComponents[0];
            int totalBlocks = component.BlocksPerLine * component.BlocksPerColumn;
            int decoded = 0;
            for (int by = 0; by < component.BlocksPerColumn; by++)
            {
                for (int bx = 0; bx < component.BlocksPerLine; bx++)
                {
                    this.DecodeBlock(component, bx, by);
                    decoded++;
                    this.HandleRestart(decoded, totalBlocks, scanComponents);
                }
            }
        }
        else
        {
            int totalMcus = this.mcusX * this.mcusY;
            int decoded = 0;
            for (int my = 0; my < this.mcusY; my++)
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
        }

        this.scansDecoded++;
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
        if (this.pos + 1 < this.dataLength && this.data[this.pos] == 0xFF)
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
        ushort[] quant = component.QuantTable!;
        HuffmanTable dc = component.DcTable!;
        HuffmanTable ac = component.AcTable!;
        Span<float> block = this.blockScratch;
        block.Clear();

        // DC coefficient.
        int t = this.DecodeHuffman(dc);
        int diff = t == 0 ? 0 : Extend(this.Receive(t), t);
        component.Pred += diff;
        block[0] = component.Pred * quant[0];

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
            block[natural] = Extend(this.Receive(size), size) * quant[natural];
            anyAc = true;
            k++;
        }

        if (!anyAc)
        {
            // A flat block is by far the most common case in smooth areas and in chroma planes.
            WriteFlatBlock(component, bx, by, JpegTables.DcOnlyValue(block[0]));
            return;
        }

        JpegTables.InverseDct(block, this.tempScratch);
        WriteBlock(component, bx, by, block);
    }

    /// <summary>Level-shifts, rounds and clamps an IDCT output block into the component's sample plane.</summary>
    private static void WriteBlock(JpegComponent component, int bx, int by, ReadOnlySpan<float> block)
    {
        int baseX = bx * 8;
        int baseY = by * 8;
        byte[] plane = component.Plane;
        int planeWidth = component.PlaneWidth;

        if (SimdConfig.Vector256Enabled)
        {
            ref float source = ref MemoryMarshal.GetReference(block);
            ref byte destination = ref MemoryMarshal.GetArrayDataReference(plane);
            Vector256<float> bias = Vector256.Create(128.5f);
            Vector256<int> max = Vector256.Create(255);
            for (int y = 0; y < 8; y++)
            {
                Vector256<int> samples = Vector256.ConvertToInt32(Vector256.LoadUnsafe(ref source, (nuint)(y * 8)) + bias);
                samples = Vector256.Max(Vector256.Min(samples, max), Vector256<int>.Zero);
                Vector128<short> narrowed = Vector128.Narrow(samples.GetLower(), samples.GetUpper());
                ulong packed = Vector128.Narrow(narrowed.AsUInt16(), narrowed.AsUInt16()).AsUInt64().GetElement(0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (uint)(((baseY + y) * planeWidth) + baseX)), packed);
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            int rowOffset = ((baseY + y) * planeWidth) + baseX;
            int blockOffset = y * 8;
            for (int x = 0; x < 8; x++)
            {
                plane[rowOffset + x] = (byte)Math.Clamp((int)(block[blockOffset + x] + 128.5f), 0, 255);
            }
        }
    }

    /// <summary>Writes the constant a DC-only block reconstructs to, without running the transform.</summary>
    private static void WriteFlatBlock(JpegComponent component, int bx, int by, float value)
    {
        byte sample = (byte)Math.Clamp((int)(value + 128.5f), 0, 255);
        byte[] plane = component.Plane;
        int planeWidth = component.PlaneWidth;
        int baseX = bx * 8;
        int baseY = by * 8;
        for (int y = 0; y < 8; y++)
        {
            plane.AsSpan((((baseY + y) * planeWidth) + baseX), 8).Fill(sample);
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
            if (this.markerPending || this.pos >= this.dataLength)
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
        public int Id;
        public int H;
        public int V;
        public int QuantId;
        public int DcTableId;
        public int AcTableId;
        public int Pred;

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
        public int PlaneHeight;
        public byte[] Plane = Array.Empty<byte>();

        /// <summary>Progressive only: quantized coefficients of every block, 64 per block in zigzag order.</summary>
        public short[] Coefficients = Array.Empty<short>();

        /// <summary>Quantization table latched by the first scan that includes this component.</summary>
        public ushort[]? QuantTable;

        public HuffmanTable? DcTable;
        public HuffmanTable? AcTable;

        public int CoefficientOffset(int bx, int by) => ((by * this.BlocksPerLineTotal) + bx) * 64;
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
