using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Component = EasyImageSharp.Formats.Jpeg.JpegEncoderCore.Component;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Creates row-at-a-time JPEG encoders: the frame is pushed top to bottom and encoded one MCU row at a time, so the
/// pixels of the image never have to exist in memory at once.
/// </summary>
/// <remarks>
/// <para>
/// Every configuration the sink accepts produces exactly the bytes
/// <see cref="JpegEncoder.Encode{TPixel}(Image{TPixel}, Stream)"/> produces for the same pixels and options. Two
/// need the whole image's coefficients and are refused (<see cref="TryCreate{TPixel}"/> returns false, so the caller
/// can fall back to the image-based encoder): progressive output, whose scans revisit every coefficient several
/// times, and <see cref="JpegEncoder.TrellisQuantization"/> combined with
/// <see cref="JpegEncoder.OptimizeHuffmanTables"/>, where the trellis runs a second time over every block with
/// Huffman tables fitted to the first pass. Trellis quantisation with the standard tables streams.
/// </para>
/// <para>
/// Pixel memory is one MCU row (8 or 16 pixel rows, depending on the chroma subsampling) of converted samples plus
/// that row's coefficients. Interleaved output with the standard Huffman tables is written as soon as each MCU row
/// is complete. With <see cref="JpegEncoder.OptimizeHuffmanTables"/>, or with one scan per component
/// (<see cref="JpegEncoder.Interleaved"/> false), the scan cannot be written until every row has arrived; the sink
/// then records each block's Huffman symbols in a <see cref="JpegSymbolBuffer"/> (a few times the size of the
/// finished entropy-coded data, so 7 to 30 times smaller than the pixels or the two bytes per coefficient a
/// coefficient buffer would take) and writes the scans from it in <see cref="IScanlineSink{TPixel}.Complete"/>.
/// </para>
/// </remarks>
internal static class JpegScanlineSink
{
    /// <summary>Creates a sink writing to <paramref name="output"/> with the options of <paramref name="encoder"/>.</summary>
    /// <param name="output">The stream the JPEG is written to; headers are written by <c>Begin</c>.</param>
    /// <param name="encoder">The encoder options. The colour model is resolved for <typeparamref name="TPixel"/> as the encoder does.</param>
    /// <param name="sink">The sink, or null when the options cannot be encoded incrementally.</param>
    /// <returns>
    /// False, without throwing, for progressive output and for trellis quantisation with optimised Huffman tables;
    /// true otherwise.
    /// </returns>
    public static bool TryCreate<TPixel>(Stream output, JpegEncoder encoder, out IScanlineSink<TPixel>? sink)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoder);

        if (encoder.Progressive || (encoder.TrellisQuantization && encoder.OptimizeHuffmanTables))
        {
            sink = null;
            return false;
        }

        sink = new Sink<TPixel>(output, encoder.CreateCore<TPixel>());
        return true;
    }

    private sealed class Sink<TPixel> : IScanlineSink<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly Stream stream;
        private readonly JpegEncoderCore core;

        private JpegEncoderCore.BandBuffers? bands;
        private JpegBitWriter? writer;
        private Component[] components = Array.Empty<Component>();

        // Direct mode: one interleaved scan with the standard tables, entropy coded as MCU rows complete.
        private JpegEncoderCore.ScanState? directState;

        // Recorded mode: one symbol buffer per scan, written in Complete.
        private Component[][] scans = Array.Empty<Component[]>();
        private JpegSymbolBuffer[] recorded = Array.Empty<JpegSymbolBuffer>();
        private JpegEncoderCore.ScanState[] recordStates = Array.Empty<JpegEncoderCore.ScanState>();
        private long[]?[][] dcFrequencies = Array.Empty<long[]?[]>();
        private long[]?[][] acFrequencies = Array.Empty<long[]?[]>();

        private int width;
        private int height;
        private int mcuHeight;
        private int row;
        private bool begun;
        private bool completed;
        private bool disposed;

        public Sink(Stream stream, JpegEncoderCore core)
        {
            this.stream = stream;
            this.core = core;
        }

        private bool Direct => this.directState is not null;

        public void Begin(int width, int height, ImageMetadata metadata)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            ArgumentNullException.ThrowIfNull(metadata);
            if (this.begun)
            {
                throw new InvalidOperationException("Begin has already been called.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

            this.core.Initialize(width, height);
            this.begun = true;
            this.width = width;
            this.height = height;
            this.mcuHeight = this.core.McuHeight;
            this.components = this.core.Components;

            // One MCU row of coefficients per component; the block row stored first is updated per MCU row.
            foreach (Component c in this.components)
            {
                c.Coefficients = new short[c.V * c.BlocksPerLineTotal * 64];
                c.BlockRowBase = 0;
                c.Predictor = 0;
            }

            this.bands = this.core.RentBands();
            this.core.WriteFrameHeaders(this.stream, metadata);

            int restartInterval = this.core.RestartInterval;
            if (this.core.IsSingleScan && !this.core.OptimizesHuffmanTables)
            {
                this.core.UseStandardHuffmanTables();
                this.core.WriteHuffmanTables(this.stream, this.components);
                this.core.WriteScanHeader(this.stream, this.components, 0, 63, 0, 0);
                this.directState = new JpegEncoderCore.ScanState(this.core.McusPerRow * this.core.McuRows, restartInterval);
                this.writer = new JpegBitWriter(this.stream);
                return;
            }

            this.scans = this.core.IsSingleScan
                ? new[] { this.components }
                : Array.ConvertAll(this.components, c => new[] { c });
            int scanCount = this.scans.Length;
            this.recorded = new JpegSymbolBuffer[scanCount];
            this.recordStates = new JpegEncoderCore.ScanState[scanCount];
            this.dcFrequencies = new long[]?[scanCount][];
            this.acFrequencies = new long[]?[scanCount][];
            for (int s = 0; s < scanCount; s++)
            {
                this.recorded[s] = new JpegSymbolBuffer();
                this.recordStates[s] = new JpegEncoderCore.ScanState(this.McuCount(this.scans[s]), restartInterval);
                this.dcFrequencies[s] = new long[]?[2];
                this.acFrequencies[s] = new long[]?[2];
                if (this.core.OptimizesHuffmanTables)
                {
                    foreach (Component c in this.scans[s])
                    {
                        this.dcFrequencies[s][c.TableIndex] ??= new long[257];
                        this.acFrequencies[s][c.TableIndex] ??= new long[257];
                    }
                }
            }
        }

        public void WriteRow(ReadOnlySpan<TPixel> row)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.begun)
            {
                throw new InvalidOperationException("Begin must be called before the first row.");
            }

            if (this.row >= this.height)
            {
                throw new InvalidOperationException($"All {this.height} rows have already been written.");
            }

            if (row.Length < this.width)
            {
                throw new ArgumentException($"A row must hold {this.width} pixels; got {row.Length}.", nameof(row));
            }

            JpegEncoderCore.BandBuffers bands = this.bands!;
            int bandRow = this.row % this.mcuHeight;
            this.core.ConvertRow(row[..this.width], bands, bandRow);
            this.row++;

            bool lastRow = this.row == this.height;
            if (bandRow == this.mcuHeight - 1 || lastRow)
            {
                // Bottom edge: the last MCU row is completed by repeating the image's last row.
                for (int r = bandRow + 1; r < this.mcuHeight; r++)
                {
                    this.core.ReplicateRow(bands, r);
                }

                this.FlushMcuRow((this.row - 1) / this.mcuHeight);
            }
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.begun)
            {
                throw new InvalidOperationException("Begin must be called before Complete.");
            }

            if (this.completed)
            {
                throw new InvalidOperationException("Complete has already been called.");
            }

            if (this.row != this.height)
            {
                throw new InvalidOperationException($"Only {this.row} of {this.height} rows were written.");
            }

            this.completed = true;
            if (this.Direct)
            {
                this.writer!.Flush();
                JpegEncoderCore.WriteEndOfImage(this.stream);
                return;
            }

            // The same order of segments the whole-image encoder writes: standard tables once up front, or
            // tables derived per scan from that scan's statistics just before it.
            if (!this.core.OptimizesHuffmanTables)
            {
                this.core.UseStandardHuffmanTables();
                this.core.WriteHuffmanTables(this.stream, this.components);
            }

            this.writer = new JpegBitWriter(this.stream);
            for (int s = 0; s < this.scans.Length; s++)
            {
                Component[] scanComponents = this.scans[s];
                if (this.core.OptimizesHuffmanTables)
                {
                    this.core.UseOptimizedTables(this.dcFrequencies[s], this.acFrequencies[s]);
                    this.core.WriteHuffmanTables(this.stream, scanComponents);
                }

                this.core.WriteScanHeader(this.stream, scanComponents, 0, 63, 0, 0);
                this.ReplayScan(s);
                this.writer.Flush();
            }

            JpegEncoderCore.WriteEndOfImage(this.stream);
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.bands?.Dispose();
            this.writer?.Dispose();
            foreach (JpegSymbolBuffer buffer in this.recorded)
            {
                buffer.Dispose();
            }
        }

        /// <summary>Blocks in one scan: the padded MCU grid when interleaved, the component's own grid otherwise.</summary>
        private int McuCount(Component[] scanComponents)
            => scanComponents.Length == 1 && this.components.Length > 1
                ? scanComponents[0].BlocksPerLine * scanComponents[0].BlocksPerColumn
                : this.core.McusPerRow * this.core.McuRows;

        /// <summary>Transforms the buffered MCU row and either codes it or records its symbols.</summary>
        private void FlushMcuRow(int mcuRow)
        {
            foreach (Component c in this.components)
            {
                c.BlockRowBase = mcuRow * c.V;
            }

            this.core.TransformMcuRow(this.bands!, mcuRow);
            if (this.directState is JpegEncoderCore.ScanState state)
            {
                this.core.EncodeSequentialMcuRows(this.writer!, this.components, mcuRow, mcuRow + 1, state);
                return;
            }

            for (int s = 0; s < this.scans.Length; s++)
            {
                Component[] scanComponents = this.scans[s];
                JpegSymbolBuffer buffer = this.recorded[s];
                JpegEncoderCore.ScanState scanState = this.recordStates[s];
                long[]?[] dc = this.dcFrequencies[s];
                long[]?[] ac = this.acFrequencies[s];
                if (scanComponents.Length == 1 && this.components.Length > 1)
                {
                    // Non-interleaved scan: the component's own block grid in raster order, without MCU padding.
                    Component c = scanComponents[0];
                    for (int v = 0; v < c.V; v++)
                    {
                        int by = (mcuRow * c.V) + v;
                        if (by >= c.BlocksPerColumn)
                        {
                            break;
                        }

                        for (int bx = 0; bx < c.BlocksPerLine; bx++)
                        {
                            buffer.AppendBlock(c.Coefficients.AsSpan(c.BlockOffset(bx, by), 64), ref c.Predictor, dc[c.TableIndex], ac[c.TableIndex]);
                            scanState.McuDone(scanComponents);
                        }
                    }

                    continue;
                }

                for (int mx = 0; mx < this.core.McusPerRow; mx++)
                {
                    foreach (Component c in scanComponents)
                    {
                        for (int v = 0; v < c.V; v++)
                        {
                            int offset = c.BlockOffset(mx * c.H, (mcuRow * c.V) + v);
                            for (int h = 0; h < c.H; h++, offset += 64)
                            {
                                buffer.AppendBlock(c.Coefficients.AsSpan(offset, 64), ref c.Predictor, dc[c.TableIndex], ac[c.TableIndex]);
                            }
                        }
                    }

                    scanState.McuDone(scanComponents);
                }
            }
        }

        /// <summary>Writes one recorded scan, inserting restart markers where the whole-image encoder does.</summary>
        private void ReplayScan(int s)
        {
            Component[] scanComponents = this.scans[s];
            JpegSymbolBuffer buffer = this.recorded[s];
            JpegBitWriter writer = this.writer!;
            buffer.Rewind();
            var state = new JpegEncoderCore.ScanState(this.McuCount(scanComponents), this.core.RestartInterval);
            if (scanComponents.Length == 1 && this.components.Length > 1)
            {
                Component c = scanComponents[0];
                int blocks = c.BlocksPerLine * c.BlocksPerColumn;
                for (int i = 0; i < blocks; i++)
                {
                    buffer.ReplayBlock(writer, c.DcLookup, c.AcLookup);
                    if (state.McuDone(scanComponents))
                    {
                        writer.WriteMarker(state.NextRestartMarker());
                    }
                }

                return;
            }

            int mcus = this.core.McusPerRow * this.core.McuRows;
            for (int m = 0; m < mcus; m++)
            {
                foreach (Component c in scanComponents)
                {
                    for (int b = c.H * c.V; b > 0; b--)
                    {
                        buffer.ReplayBlock(writer, c.DcLookup, c.AcLookup);
                    }
                }

                if (state.McuDone(scanComponents))
                {
                    writer.WriteMarker(state.NextRestartMarker());
                }
            }
        }
    }
}
