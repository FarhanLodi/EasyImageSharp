using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// The JPEG encoding pipeline behind <see cref="JpegEncoder"/>: component layout, colour conversion and chroma
/// subsampling, forward DCT + quantisation into coefficient blocks (parallel over MCU rows), header/segment
/// writing, and Huffman coding of sequential scans. Progressive scan coding lives in
/// <see cref="JpegProgressiveEncoder"/>.
/// </summary>
/// <remarks>
/// Two buffering strategies are used. Interleaved baseline output with standard Huffman tables is streamed: the
/// image is processed in horizontal strips of MCU rows whose coefficients are computed in parallel and then
/// entropy coded, so memory stays bounded (a few MB) regardless of image size. Every other configuration
/// (progressive, optimised tables, non-interleaved scans) needs all coefficients before the first scan can be
/// written and therefore keeps one <c>short</c> per coefficient per component for the whole image, like libjpeg's
/// full-image coefficient buffer.
/// </remarks>
internal sealed class JpegEncoderCore
{
    /// <summary>Approximate size of the coefficient buffer per strip in streaming mode.</summary>
    private const int TargetStripBytes = 4 << 20;

    /// <summary>Undoes the fixed-point scale <see cref="JpegColorConverter"/> applies to its samples.</summary>
    private const float Unscale = 1f / JpegColorConverter.SampleScale;

    private readonly int quality;
    private readonly JpegEncodingColor colorType;
    private readonly bool interleaved;
    private readonly int restartInterval;
    private readonly bool progressive;
    private readonly int progressiveScans;
    private readonly bool optimizeHuffman;

    private int width;
    private int height;
    private int maxH;
    private int maxV;
    private int mcusX;
    private int mcusY;
    private Component[] components = Array.Empty<Component>();

    // Index 0: luminance table, index 1: chrominance table (both in natural order).
    private readonly ushort[][] quantTables = new ushort[2][];
    private readonly float[][] reciprocalTables = new float[2][];
    private readonly JpegHuffmanTable?[] dcTables = new JpegHuffmanTable?[2];
    private readonly JpegHuffmanTable?[] acTables = new JpegHuffmanTable?[2];
    private readonly bool[] dcTableWritten = new bool[2];
    private readonly bool[] acTableWritten = new bool[2];

    public JpegEncoderCore(
        int quality,
        JpegEncodingColor colorType,
        bool interleaved,
        int restartInterval,
        bool progressive,
        int progressiveScans,
        bool optimizeHuffman)
    {
        this.quality = quality;
        this.colorType = colorType;
        this.interleaved = interleaved;
        this.restartInterval = restartInterval;
        this.progressive = progressive;
        this.progressiveScans = progressiveScans;
        this.optimizeHuffman = optimizeHuffman;
    }

    /// <summary>One frame component: sampling factors, tables and (a window of) its quantised coefficient blocks.</summary>
    internal sealed class Component
    {
        public byte Id;
        public int H;
        public int V;
        public int QuantIndex;
        public int TableIndex;

        /// <summary>Component width in samples: ceil(imageWidth * H / maxH).</summary>
        public int CompWidth;

        /// <summary>Component height in samples: ceil(imageHeight * V / maxV).</summary>
        public int CompHeight;

        /// <summary>Blocks per line covered by a non-interleaved scan: ceil(CompWidth / 8).</summary>
        public int BlocksPerLine;

        /// <summary>Block rows covered by a non-interleaved scan: ceil(CompHeight / 8).</summary>
        public int BlocksPerColumn;

        /// <summary>Blocks per line in the MCU-padded grid (mcusX * H).</summary>
        public int BlocksPerLineTotal;

        /// <summary>Block rows in the MCU-padded grid (mcusY * V).</summary>
        public int BlocksPerColumnTotal;

        /// <summary>Quantised coefficients, 64 per block in zigzag order, block rows starting at <see cref="BlockRowBase"/>.</summary>
        public short[] Coefficients = Array.Empty<short>();

        /// <summary>The block row stored at the start of <see cref="Coefficients"/> (0 in full-image mode).</summary>
        public int BlockRowBase;

        public float[] Reciprocals = Array.Empty<float>();
        public int[] DcLookup = Array.Empty<int>();
        public int[] AcLookup = Array.Empty<int>();
        public int Predictor;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int BlockOffset(int bx, int by) => (((by - this.BlockRowBase) * this.BlocksPerLineTotal) + bx) * 64;
    }

    // =====================================================================================================
    // Entry point
    // =====================================================================================================

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        this.width = image.Width;
        this.height = image.Height;
        if (this.width > ushort.MaxValue || this.height > ushort.MaxValue)
        {
            throw new NotSupportedException(
                $"JPEG cannot represent a {this.width}x{this.height} image; the format limits each dimension to {ushort.MaxValue} pixels.");
        }

        this.SetupComponents();
        this.SetupQuantization();
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        // ----- Headers up to (and excluding) the Huffman tables -----
        WriteMarker(stream, 0xD8); // SOI

        // APP0 (JFIF, density taken from the image resolution) followed by the EXIF/XMP/ICC/COM metadata
        // segments. JFIF only describes the JFIF colour models, so Adobe frames get the metadata without it.
        JpegMetadataWriter.Write(stream, image.Metadata, this.colorType is JpegEncodingColor.Luminance || this.IsYCbCr);

        if (this.colorType is JpegEncodingColor.Rgb or JpegEncodingColor.Cmyk or JpegEncodingColor.Ycck)
        {
            // Adobe APP14: "Adobe", version 100, flags 0/0, colour transform (0 = none, 2 = YCCK).
            byte transform = this.colorType == JpegEncodingColor.Ycck ? (byte)2 : (byte)0;
            WriteSegment(stream, 0xEE, new byte[] { (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, transform });
        }

        this.WriteQuantTable(stream, 0);
        if (Array.Exists(this.components, c => c.QuantIndex == 1))
        {
            this.WriteQuantTable(stream, 1);
        }

        this.WriteFrameHeader(stream);
        if (this.restartInterval > 0)
        {
            WriteSegment(stream, 0xDD, new[] { (byte)(this.restartInterval >> 8), (byte)this.restartInterval });
        }

        bool streaming = !this.progressive && !this.optimizeHuffman && (this.interleaved || this.components.Length == 1);
        if (streaming)
        {
            this.EncodeStreaming(frame, stream);
        }
        else
        {
            this.EncodeBuffered(frame, stream);
        }

        WriteMarker(stream, 0xD9); // EOI
    }

    private bool IsYCbCr => this.colorType is JpegEncodingColor.YCbCrRatio444 or JpegEncodingColor.YCbCrRatio422
        or JpegEncodingColor.YCbCrRatio420 or JpegEncodingColor.YCbCrRatio411 or JpegEncodingColor.YCbCrRatio410;

    // =====================================================================================================
    // Layout and tables
    // =====================================================================================================

    private void SetupComponents()
    {
        (int lumaH, int lumaV) = this.colorType switch
        {
            JpegEncodingColor.YCbCrRatio422 => (2, 1),
            JpegEncodingColor.YCbCrRatio420 => (2, 2),
            JpegEncodingColor.YCbCrRatio411 => (4, 1),
            JpegEncodingColor.YCbCrRatio410 => (4, 2),
            _ => (1, 1),
        };

        this.components = this.colorType switch
        {
            JpegEncodingColor.Luminance => new[] { new Component { Id = 1, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 } },
            JpegEncodingColor.Rgb => new[]
            {
                new Component { Id = (byte)'R', H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = (byte)'G', H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = (byte)'B', H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
            },
            JpegEncodingColor.Cmyk => new[]
            {
                new Component { Id = 1, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = 2, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = 3, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = 4, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
            },
            JpegEncodingColor.Ycck => new[]
            {
                new Component { Id = 1, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = 2, H = 1, V = 1, QuantIndex = 1, TableIndex = 1 },
                new Component { Id = 3, H = 1, V = 1, QuantIndex = 1, TableIndex = 1 },
                new Component { Id = 4, H = 1, V = 1, QuantIndex = 0, TableIndex = 0 },
            },
            _ => new[]
            {
                new Component { Id = 1, H = lumaH, V = lumaV, QuantIndex = 0, TableIndex = 0 },
                new Component { Id = 2, H = 1, V = 1, QuantIndex = 1, TableIndex = 1 },
                new Component { Id = 3, H = 1, V = 1, QuantIndex = 1, TableIndex = 1 },
            },
        };

        this.maxH = 1;
        this.maxV = 1;
        foreach (Component c in this.components)
        {
            this.maxH = Math.Max(this.maxH, c.H);
            this.maxV = Math.Max(this.maxV, c.V);
        }

        this.mcusX = (this.width + (8 * this.maxH) - 1) / (8 * this.maxH);
        this.mcusY = (this.height + (8 * this.maxV) - 1) / (8 * this.maxV);
        foreach (Component c in this.components)
        {
            c.CompWidth = ((this.width * c.H) + this.maxH - 1) / this.maxH;
            c.CompHeight = ((this.height * c.V) + this.maxV - 1) / this.maxV;
            c.BlocksPerLine = (c.CompWidth + 7) / 8;
            c.BlocksPerColumn = (c.CompHeight + 7) / 8;
            c.BlocksPerLineTotal = this.mcusX * c.H;
            c.BlocksPerColumnTotal = this.mcusY * c.V;
        }
    }

    private void SetupQuantization()
    {
        this.quantTables[0] = ScaleQuantTable(JpegTables.StdLuminanceQuant, this.quality);
        this.quantTables[1] = ScaleQuantTable(JpegTables.StdChrominanceQuant, this.quality);
        this.reciprocalTables[0] = JpegForwardDct.CreateReciprocalTable(this.quantTables[0]);
        this.reciprocalTables[1] = JpegForwardDct.CreateReciprocalTable(this.quantTables[1]);
        foreach (Component c in this.components)
        {
            c.Reciprocals = this.reciprocalTables[c.QuantIndex];
        }
    }

    /// <summary>Standard IJG quality scaling of the Annex K tables, clamped to the 8-bit range baseline JPEG requires.</summary>
    internal static ushort[] ScaleQuantTable(byte[] baseTable, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var result = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = (ushort)Math.Clamp(((baseTable[i] * scale) + 50) / 100, 1, 255);
        }

        return result;
    }

    private void UseStandardHuffmanTables()
    {
        this.dcTables[0] = new JpegHuffmanTable(JpegTables.StdDcBits, JpegTables.StdDcValues);
        this.acTables[0] = new JpegHuffmanTable(JpegTables.StdAcBits, JpegTables.StdAcValues);
        this.dcTables[1] = new JpegHuffmanTable(JpegTables.StdDcChrominanceBits, JpegTables.StdDcChrominanceValues);
        this.acTables[1] = new JpegHuffmanTable(JpegTables.StdAcChrominanceBits, JpegTables.StdAcChrominanceValues);
        this.BindTables();
    }

    private void BindTables()
    {
        foreach (Component c in this.components)
        {
            c.DcLookup = this.dcTables[c.TableIndex]?.Lookup ?? Array.Empty<int>();
            c.AcLookup = this.acTables[c.TableIndex]?.Lookup ?? Array.Empty<int>();
        }
    }

    // =====================================================================================================
    // Streaming (strip) mode: interleaved baseline with standard tables
    // =====================================================================================================

    private void EncodeStreaming<TPixel>(ImageFrame<TPixel> frame, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        this.UseStandardHuffmanTables();
        this.WriteHuffmanTables(stream, this.components);
        this.WriteScanHeader(stream, this.components, 0, 63, 0, 0);

        long bytesPerMcuRow = 0;
        foreach (Component c in this.components)
        {
            bytesPerMcuRow += (long)c.V * c.BlocksPerLineTotal * 64 * sizeof(short);
        }

        int stripRows = (int)Math.Clamp(TargetStripBytes / Math.Max(1, bytesPerMcuRow), 1, this.mcusY);
        foreach (Component c in this.components)
        {
            c.Coefficients = new short[stripRows * c.V * c.BlocksPerLineTotal * 64];
        }

        using var writer = new JpegBitWriter(stream);
        var state = new ScanState(this.mcusX * this.mcusY, this.restartInterval);
        int mcuHeight = 8 * this.maxV;
        for (int stripStart = 0; stripStart < this.mcusY; stripStart += stripRows)
        {
            int stripEnd = Math.Min(this.mcusY, stripStart + stripRows);
            foreach (Component c in this.components)
            {
                c.BlockRowBase = stripStart * c.V;
            }

            ParallelRowIterator.IterateRows(
                this.width * mcuHeight,
                stripEnd - stripStart,
                (start, end) => this.FillBlocks(frame, stripStart + start, stripStart + end));

            this.EncodeSequentialMcuRows(writer, this.components, stripStart, stripEnd, state);
        }

        writer.Flush();
    }

    // =====================================================================================================
    // Buffered (full-image) mode: progressive, optimised tables, non-interleaved
    // =====================================================================================================

    private void EncodeBuffered<TPixel>(ImageFrame<TPixel> frame, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (Component c in this.components)
        {
            long count = (long)c.BlocksPerColumnTotal * c.BlocksPerLineTotal * 64;
            if (count > Array.MaxLength)
            {
                throw new NotSupportedException("The image is too large to buffer its JPEG coefficients.");
            }

            c.Coefficients = new short[count];
            c.BlockRowBase = 0;
        }

        ParallelRowIterator.IterateRows(
            this.width * 8 * this.maxV,
            this.mcusY,
            (start, end) => this.FillBlocks(frame, start, end));

        if (!this.optimizeHuffman)
        {
            this.UseStandardHuffmanTables();
            this.WriteHuffmanTables(stream, this.components);
        }

        if (this.progressive)
        {
            this.EncodeProgressive(stream);
            return;
        }

        // Sequential: one interleaved scan, or one scan per component.
        Component[][] scans = this.interleaved || this.components.Length == 1
            ? new[] { this.components }
            : Array.ConvertAll(this.components, c => new[] { c });

        using var writer = new JpegBitWriter(stream);
        foreach (Component[] scanComponents in scans)
        {
            if (this.optimizeHuffman)
            {
                this.OptimizeSequentialTables(scanComponents);
                this.WriteHuffmanTables(stream, scanComponents);
            }

            this.WriteScanHeader(stream, scanComponents, 0, 63, 0, 0);
            var state = new ScanState(this.McuCount(scanComponents), this.restartInterval);
            if (scanComponents.Length == 1 && this.components.Length > 1)
            {
                EncodeSequentialNonInterleaved(writer, scanComponents[0], state);
            }
            else
            {
                this.EncodeSequentialMcuRows(writer, scanComponents, 0, this.mcusY, state);
            }

            writer.Flush();
        }
    }

    private void EncodeProgressive(Stream stream)
    {
        JpegScanDescriptor[] script = JpegScanScript.Create(this.components, this.IsYCbCr, this.interleaved, this.progressiveScans);
        var encoder = new JpegProgressiveEncoder(this.mcusX, this.mcusY, this.restartInterval, this.optimizeHuffman ? 0x7FFF : 1);
        using var writer = new JpegBitWriter(stream);
        foreach (JpegScanDescriptor scan in script)
        {
            if (this.optimizeHuffman)
            {
                this.OptimizeProgressiveTables(encoder, scan);
                this.WriteHuffmanTables(stream, scan.Components, scan.Ss == 0 && scan.Ah == 0, scan.Ss > 0);
            }

            this.WriteScanHeader(stream, scan.Components, scan.Ss, scan.Se, scan.Ah, scan.Al);
            encoder.EncodeScan(writer, scan, null, null);
            writer.Flush();
        }
    }

    private int McuCount(Component[] scanComponents)
        => scanComponents.Length == 1 && this.components.Length > 1
            ? scanComponents[0].BlocksPerLine * scanComponents[0].BlocksPerColumn
            : this.mcusX * this.mcusY;

    // ----- Huffman table optimisation -----

    private void OptimizeSequentialTables(Component[] scanComponents)
    {
        var dcFreq = new long[2][];
        var acFreq = new long[2][];
        foreach (Component c in scanComponents)
        {
            dcFreq[c.TableIndex] ??= new long[257];
            acFreq[c.TableIndex] ??= new long[257];
        }

        foreach (Component c in scanComponents)
        {
            c.Predictor = 0;
        }

        var state = new ScanState(this.McuCount(scanComponents), this.restartInterval);
        if (scanComponents.Length == 1 && this.components.Length > 1)
        {
            Component c = scanComponents[0];
            for (int by = 0; by < c.BlocksPerColumn; by++)
            {
                for (int bx = 0; bx < c.BlocksPerLine; bx++)
                {
                    GatherBlockSequential(c.Coefficients.AsSpan(c.BlockOffset(bx, by), 64), ref c.Predictor, dcFreq[c.TableIndex], acFreq[c.TableIndex]);
                    state.McuDone(scanComponents);
                }
            }
        }
        else
        {
            for (int my = 0; my < this.mcusY; my++)
            {
                for (int mx = 0; mx < this.mcusX; mx++)
                {
                    foreach (Component c in scanComponents)
                    {
                        for (int v = 0; v < c.V; v++)
                        {
                            for (int h = 0; h < c.H; h++)
                            {
                                GatherBlockSequential(
                                    c.Coefficients.AsSpan(c.BlockOffset((mx * c.H) + h, (my * c.V) + v), 64),
                                    ref c.Predictor,
                                    dcFreq[c.TableIndex],
                                    acFreq[c.TableIndex]);
                            }
                        }
                    }

                    state.McuDone(scanComponents);
                }
            }
        }

        foreach (Component c in scanComponents)
        {
            c.Predictor = 0;
        }

        for (int t = 0; t < 2; t++)
        {
            if (dcFreq[t] is not null)
            {
                this.dcTables[t] = JpegHuffmanTable.FromFrequencies(dcFreq[t]);
                this.dcTableWritten[t] = false;
            }

            if (acFreq[t] is not null)
            {
                this.acTables[t] = JpegHuffmanTable.FromFrequencies(acFreq[t]);
                this.acTableWritten[t] = false;
            }
        }

        this.BindTables();
    }

    private void OptimizeProgressiveTables(JpegProgressiveEncoder encoder, JpegScanDescriptor scan)
    {
        var dcFreq = new long[2][];
        var acFreq = new long[2][];
        bool needsDc = scan.Ss == 0 && scan.Ah == 0; // DC refinement scans emit raw bits only.
        bool needsAc = scan.Ss > 0;
        foreach (Component c in scan.Components)
        {
            if (needsDc)
            {
                dcFreq[c.TableIndex] ??= new long[257];
            }

            if (needsAc)
            {
                acFreq[c.TableIndex] ??= new long[257];
            }
        }

        if (!needsDc && !needsAc)
        {
            return;
        }

        encoder.EncodeScan(null, scan, dcFreq, acFreq);
        for (int t = 0; t < 2; t++)
        {
            if (dcFreq[t] is not null)
            {
                this.dcTables[t] = JpegHuffmanTable.FromFrequencies(dcFreq[t]);
                this.dcTableWritten[t] = false;
            }

            if (acFreq[t] is not null)
            {
                this.acTables[t] = JpegHuffmanTable.FromFrequencies(acFreq[t]);
                this.acTableWritten[t] = false;
            }
        }

        this.BindTables();
    }

    // =====================================================================================================
    // Front end: colour conversion, subsampling, DCT, quantisation
    // =====================================================================================================

    /// <summary>
    /// Computes the quantised coefficient blocks of MCU rows [<paramref name="mcuRowStart"/>, <paramref name="mcuRowEnd"/>)
    /// for every component. Safe to call concurrently for disjoint row ranges.
    /// </summary>
    private void FillBlocks<TPixel>(ImageFrame<TPixel> frame, int mcuRowStart, int mcuRowEnd)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int mcuHeight = 8 * this.maxV;
        int paddedWidth = this.mcusX * 8 * this.maxH;
        int compCount = this.components.Length;

        // Full-resolution band (one per component) plus a subsampled band for components below full resolution.
        var fullBands = new short[compCount][];
        var subBands = new short[compCount][];
        for (int i = 0; i < compCount; i++)
        {
            fullBands[i] = ArrayPool<short>.Shared.Rent(mcuHeight * paddedWidth);
            Component c = this.components[i];
            if (c.H != this.maxH || c.V != this.maxV)
            {
                subBands[i] = ArrayPool<short>.Shared.Rent(8 * c.V * this.mcusX * 8 * c.H);
            }
        }

        Rgba32[] scratch = ArrayPool<Rgba32>.Shared.Rent(this.width);
        float[] block = new float[64];
        try
        {
            for (int my = mcuRowStart; my < mcuRowEnd; my++)
            {
                this.ConvertBand(frame, my, fullBands, paddedWidth, mcuHeight, scratch);
                for (int i = 0; i < compCount; i++)
                {
                    Component c = this.components[i];
                    short[] band;
                    int bandWidth;
                    if (subBands[i] is null)
                    {
                        band = fullBands[i];
                        bandWidth = paddedWidth;
                    }
                    else
                    {
                        band = subBands[i];
                        bandWidth = this.mcusX * 8 * c.H;
                        Downsample(fullBands[i], paddedWidth, band, bandWidth, 8 * c.V, this.maxH / c.H, this.maxV / c.V);
                    }

                    this.TransformBand(c, band, bandWidth, my, block);
                }
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(scratch);
            for (int i = 0; i < compCount; i++)
            {
                ArrayPool<short>.Shared.Return(fullBands[i]);
                if (subBands[i] is not null)
                {
                    ArrayPool<short>.Shared.Return(subBands[i]);
                }
            }
        }
    }

    /// <summary>Converts the pixel rows of one MCU row into full-resolution component samples with edge replication.</summary>
    private void ConvertBand<TPixel>(ImageFrame<TPixel> frame, int mcuRow, short[][] bands, int paddedWidth, int mcuHeight, Rgba32[] scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int compCount = this.components.Length;
        for (int r = 0; r < mcuHeight; r++)
        {
            int y = (mcuRow * mcuHeight) + r;
            int rowOffset = r * paddedWidth;
            if (y >= this.height)
            {
                // Bottom edge: replicate the previous (already converted) row.
                for (int i = 0; i < compCount; i++)
                {
                    Array.Copy(bands[i], rowOffset - paddedWidth, bands[i], rowOffset, paddedWidth);
                }

                continue;
            }

            ReadOnlySpan<TPixel> src = frame.GetRowSpan(y);
            switch (this.colorType)
            {
                case JpegEncodingColor.Luminance:
                    JpegColorConverter.ToLuminance(src, bands[0].AsSpan(rowOffset, this.width), scratch);
                    break;
                case JpegEncodingColor.Rgb:
                    JpegColorConverter.ToRgb(src, bands[0].AsSpan(rowOffset, this.width), bands[1].AsSpan(rowOffset, this.width), bands[2].AsSpan(rowOffset, this.width), scratch);
                    break;
                case JpegEncodingColor.Cmyk:
                    JpegColorConverter.ToInvertedCmyk(
                        src, bands[0].AsSpan(rowOffset, this.width), bands[1].AsSpan(rowOffset, this.width),
                        bands[2].AsSpan(rowOffset, this.width), bands[3].AsSpan(rowOffset, this.width), scratch);
                    break;
                case JpegEncodingColor.Ycck:
                    JpegColorConverter.ToYcck(
                        src, bands[0].AsSpan(rowOffset, this.width), bands[1].AsSpan(rowOffset, this.width),
                        bands[2].AsSpan(rowOffset, this.width), bands[3].AsSpan(rowOffset, this.width), scratch);
                    break;
                default:
                    JpegColorConverter.ToYCbCr(src, bands[0].AsSpan(rowOffset, this.width), bands[1].AsSpan(rowOffset, this.width), bands[2].AsSpan(rowOffset, this.width), scratch);
                    break;
            }

            // Right edge: replicate the last sample up to the MCU boundary.
            if (paddedWidth > this.width)
            {
                for (int i = 0; i < compCount; i++)
                {
                    short[] band = bands[i];
                    band.AsSpan(rowOffset + this.width, paddedWidth - this.width).Fill(band[rowOffset + this.width - 1]);
                }
            }
        }
    }

    /// <summary>
    /// Box-averages <paramref name="hf"/> x <paramref name="vf"/> full-resolution samples into one output sample.
    /// The 2x1 and 2x2 cases alternate the rounding bias between neighbouring outputs (0/1 and 1/2) as libjpeg's
    /// h2v1/h2v2 downsamplers do, which keeps the filter from drifting the whole plane in one direction; other
    /// factors round half up. Samples are signed and carry fractional bits, so the shifts are arithmetic.
    /// </summary>
    private static void Downsample(short[] source, int sourceWidth, short[] dest, int destWidth, int destRows, int hf, int vf)
    {
        int shift = BitOperations.Log2((uint)(hf * vf));
        int half = (hf * vf) >> 1;
        for (int oy = 0; oy < destRows; oy++)
        {
            int outRow = oy * destWidth;
            int inRow = oy * vf * sourceWidth;
            if (hf == 2 && vf == 1)
            {
                int bias = 0;
                for (int ox = 0; ox < destWidth; ox++)
                {
                    int i = inRow + (ox << 1);
                    dest[outRow + ox] = (short)((source[i] + source[i + 1] + bias) >> 1);
                    bias ^= 1;
                }
            }
            else if (hf == 2 && vf == 2)
            {
                int bias = 1;
                for (int ox = 0; ox < destWidth; ox++)
                {
                    int i = inRow + (ox << 1);
                    int j = i + sourceWidth;
                    dest[outRow + ox] = (short)((source[i] + source[i + 1] + source[j] + source[j + 1] + bias) >> 2);
                    bias ^= 3;
                }
            }
            else
            {
                for (int ox = 0; ox < destWidth; ox++)
                {
                    int sum = 0;
                    int i = inRow + (ox * hf);
                    for (int v = 0; v < vf; v++)
                    {
                        for (int h = 0; h < hf; h++)
                        {
                            sum += source[i + h];
                        }

                        i += sourceWidth;
                    }

                    dest[outRow + ox] = (short)((sum + half) >> shift);
                }
            }
        }
    }

    /// <summary>Forward-transforms and quantises every block of a component inside one MCU row.</summary>
    private void TransformBand(Component c, short[] band, int bandWidth, int mcuRow, float[] block)
    {
        short[] coefficients = c.Coefficients;
        float[] reciprocals = c.Reciprocals;
        int blocksPerLine = c.BlocksPerLineTotal;
        Span<float> work = block;
        for (int v = 0; v < c.V; v++)
        {
            int by = (mcuRow * c.V) + v;
            int bandRow = v * 8;
            for (int bx = 0; bx < blocksPerLine; bx++)
            {
                // Widen the 8x8 block into the float work area. Samples arrive level-shifted already; all that
                // is left is to undo the fixed-point scale the colour converter applied.
                ref short src = ref band[(bandRow * bandWidth) + (bx * 8)];
                ref float dst = ref MemoryMarshal.GetArrayDataReference(block);
                for (int y = 0; y < 8; y++)
                {
                    ref short s = ref Unsafe.Add(ref src, y * bandWidth);
                    ref float d = ref Unsafe.Add(ref dst, y * 8);
                    d = s * Unscale;
                    Unsafe.Add(ref d, 1) = Unsafe.Add(ref s, 1) * Unscale;
                    Unsafe.Add(ref d, 2) = Unsafe.Add(ref s, 2) * Unscale;
                    Unsafe.Add(ref d, 3) = Unsafe.Add(ref s, 3) * Unscale;
                    Unsafe.Add(ref d, 4) = Unsafe.Add(ref s, 4) * Unscale;
                    Unsafe.Add(ref d, 5) = Unsafe.Add(ref s, 5) * Unscale;
                    Unsafe.Add(ref d, 6) = Unsafe.Add(ref s, 6) * Unscale;
                    Unsafe.Add(ref d, 7) = Unsafe.Add(ref s, 7) * Unscale;
                }

                JpegForwardDct.TransformAndQuantize(work, reciprocals, coefficients.AsSpan(c.BlockOffset(bx, by), 64));
            }
        }
    }

    // =====================================================================================================
    // Sequential (baseline) entropy coding
    // =====================================================================================================

    /// <summary>Restart bookkeeping shared by the sequential encoders.</summary>
    internal sealed class ScanState
    {
        private readonly int totalMcus;
        private readonly int restartInterval;
        private int mcusDone;
        private int nextRestartMarker;

        public ScanState(int totalMcus, int restartInterval)
        {
            this.totalMcus = totalMcus;
            this.restartInterval = restartInterval;
        }

        /// <summary>Counts one finished MCU; returns true when a restart marker must follow it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool McuDone(Component[] scanComponents)
        {
            this.mcusDone++;
            if (this.restartInterval <= 0 || this.mcusDone >= this.totalMcus || this.mcusDone % this.restartInterval != 0)
            {
                return false;
            }

            foreach (Component c in scanComponents)
            {
                c.Predictor = 0;
            }

            return true;
        }

        public byte NextRestartMarker()
        {
            byte marker = (byte)(0xD0 + (this.nextRestartMarker & 7));
            this.nextRestartMarker++;
            return marker;
        }
    }

    private void EncodeSequentialMcuRows(JpegBitWriter writer, Component[] scanComponents, int mcuRowStart, int mcuRowEnd, ScanState state)
    {
        for (int my = mcuRowStart; my < mcuRowEnd; my++)
        {
            for (int mx = 0; mx < this.mcusX; mx++)
            {
                foreach (Component c in scanComponents)
                {
                    short[] coefficients = c.Coefficients;
                    int[] dc = c.DcLookup;
                    int[] ac = c.AcLookup;
                    for (int v = 0; v < c.V; v++)
                    {
                        int by = (my * c.V) + v;
                        int bx = mx * c.H;
                        int offset = c.BlockOffset(bx, by);
                        for (int h = 0; h < c.H; h++, offset += 64)
                        {
                            EncodeBlockSequential(writer, coefficients.AsSpan(offset, 64), ref c.Predictor, dc, ac);
                        }
                    }
                }

                if (state.McuDone(scanComponents))
                {
                    writer.WriteMarker(state.NextRestartMarker());
                }
            }
        }
    }

    private static void EncodeSequentialNonInterleaved(JpegBitWriter writer, Component c, ScanState state)
    {
        var scanComponents = new[] { c };
        for (int by = 0; by < c.BlocksPerColumn; by++)
        {
            for (int bx = 0; bx < c.BlocksPerLine; bx++)
            {
                EncodeBlockSequential(writer, c.Coefficients.AsSpan(c.BlockOffset(bx, by), 64), ref c.Predictor, c.DcLookup, c.AcLookup);
                if (state.McuDone(scanComponents))
                {
                    writer.WriteMarker(state.NextRestartMarker());
                }
            }
        }
    }

    /// <summary>Huffman-codes one block (zigzag order) per T.81 F.1.2: DC difference category + bits, then AC run/size pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeBlockSequential(JpegBitWriter writer, ReadOnlySpan<short> block, ref int predictor, int[] dcLookup, int[] acLookup)
    {
        ref short b = ref MemoryMarshal.GetReference(block);

        // DC: category (bit length of |diff|) followed by the diff bits (one's complement for negatives).
        int dc = b;
        int diff = dc - predictor;
        predictor = dc;
        int magnitude = diff;
        int bits = diff;
        if (diff < 0)
        {
            magnitude = -diff;
            bits = diff - 1;
        }

        int nbits = 32 - BitOperations.LeadingZeroCount((uint)magnitude);
        int entry = dcLookup[nbits];
        writer.WriteCodeAndBits((uint)entry >> 8, entry & 0xFF, (uint)bits, nbits);

        // AC: find the last nonzero coefficient so the run loop stops early and one EOB covers the tail.
        int last = 63;
        while (last > 0 && Unsafe.Add(ref b, last) == 0)
        {
            last--;
        }

        int run = 0;
        for (int k = 1; k <= last; k++)
        {
            int value = Unsafe.Add(ref b, k);
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                int zrl = acLookup[0xF0];
                writer.WriteBits((uint)zrl >> 8, zrl & 0xFF);
                run -= 16;
            }

            magnitude = value;
            bits = value;
            if (value < 0)
            {
                magnitude = -value;
                bits = value - 1;
            }

            nbits = 32 - BitOperations.LeadingZeroCount((uint)magnitude);
            entry = acLookup[(run << 4) | nbits];
            writer.WriteCodeAndBits((uint)entry >> 8, entry & 0xFF, (uint)bits, nbits);
            run = 0;
        }

        if (last < 63)
        {
            int eob = acLookup[0x00];
            writer.WriteBits((uint)eob >> 8, eob & 0xFF);
        }
    }

    /// <summary>Counts the symbols <see cref="EncodeBlockSequential"/> would emit for the block.</summary>
    private static void GatherBlockSequential(ReadOnlySpan<short> block, ref int predictor, long[] dcFreq, long[] acFreq)
    {
        int dc = block[0];
        int diff = dc - predictor;
        predictor = dc;
        dcFreq[32 - BitOperations.LeadingZeroCount((uint)Math.Abs(diff))]++;

        int last = 63;
        while (last > 0 && block[last] == 0)
        {
            last--;
        }

        int run = 0;
        for (int k = 1; k <= last; k++)
        {
            int value = block[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                acFreq[0xF0]++;
                run -= 16;
            }

            int nbits = 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
            acFreq[(run << 4) | nbits]++;
            run = 0;
        }

        if (last < 63)
        {
            acFreq[0x00]++;
        }
    }

    // =====================================================================================================
    // Segment writing
    // =====================================================================================================

    private void WriteQuantTable(Stream stream, int id)
    {
        ushort[] table = this.quantTables[id];
        var payload = new byte[65];
        payload[0] = (byte)id; // 8-bit precision, table id.
        for (int i = 0; i < 64; i++)
        {
            payload[1 + i] = (byte)table[JpegTables.ZigZag[i]];
        }

        WriteSegment(stream, 0xDB, payload);
    }

    private void WriteFrameHeader(Stream stream)
    {
        int count = this.components.Length;
        var sof = new byte[6 + (count * 3)];
        sof[0] = 8; // Sample precision.
        sof[1] = (byte)(this.height >> 8);
        sof[2] = (byte)this.height;
        sof[3] = (byte)(this.width >> 8);
        sof[4] = (byte)this.width;
        sof[5] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            Component c = this.components[i];
            sof[6 + (i * 3)] = c.Id;
            sof[7 + (i * 3)] = (byte)((c.H << 4) | c.V);
            sof[8 + (i * 3)] = (byte)c.QuantIndex;
        }

        WriteSegment(stream, this.progressive ? (byte)0xC2 : (byte)0xC0, sof);
    }

    /// <summary>Writes DHT segments for the tables the given components use that have not been written since they were (re)defined.</summary>
    private void WriteHuffmanTables(Stream stream, Component[] scanComponents, bool dc = true, bool ac = true)
    {
        foreach (Component c in scanComponents)
        {
            int t = c.TableIndex;
            if (dc && !this.dcTableWritten[t] && this.dcTables[t] is JpegHuffmanTable dcTable)
            {
                WriteSegment(stream, 0xC4, dcTable.ToSegmentPayload((byte)t));
                this.dcTableWritten[t] = true;
            }

            if (ac && !this.acTableWritten[t] && this.acTables[t] is JpegHuffmanTable acTable)
            {
                WriteSegment(stream, 0xC4, acTable.ToSegmentPayload((byte)(0x10 | t)));
                this.acTableWritten[t] = true;
            }
        }
    }

    private void WriteScanHeader(Stream stream, Component[] scanComponents, int ss, int se, int ah, int al)
    {
        // A progressive scan codes either DC or AC coefficients, never both, and a DC refinement scan emits raw
        // bits with no table at all. Naming a table that the scan does not use would oblige us to define it up
        // front, so the unused selector is zeroed instead, as libjpeg does.
        bool selectsDc = !this.progressive || (ss == 0 && ah == 0);
        bool selectsAc = !this.progressive || ss > 0;

        int count = scanComponents.Length;
        var sos = new byte[4 + (count * 2)];
        sos[0] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            Component c = scanComponents[i];
            sos[1 + (i * 2)] = c.Id;
            sos[2 + (i * 2)] = (byte)(((selectsDc ? c.TableIndex : 0) << 4) | (selectsAc ? c.TableIndex : 0));
        }

        sos[1 + (count * 2)] = (byte)ss;
        sos[2 + (count * 2)] = (byte)se;
        sos[3 + (count * 2)] = (byte)((ah << 4) | al);
        WriteSegment(stream, 0xDA, sos);
    }

    private static void WriteMarker(Stream stream, byte marker)
    {
        Span<byte> bytes = stackalloc byte[] { 0xFF, marker };
        stream.Write(bytes);
    }

    private static void WriteSegment(Stream stream, byte marker, ReadOnlySpan<byte> payload)
    {
        int length = payload.Length + 2;
        Span<byte> header = stackalloc byte[] { 0xFF, marker, (byte)(length >> 8), (byte)length };
        stream.Write(header);
        stream.Write(payload);
    }
}
