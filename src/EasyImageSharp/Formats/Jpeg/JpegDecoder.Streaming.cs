using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// The row-at-a-time mode behind <see cref="JpegScanlineSource"/>: the input is read through a sliding window
/// instead of being held whole, and each component keeps only three MCU rows ("strips") of samples. Decoding runs
/// one MCU row ahead of the rows being produced, because the triangle filter of a 4:2:0 frame needs the chroma row
/// below the last row of a strip, and the strip before stays resident for the row above its first row.
/// </summary>
/// <remarks>
/// Only frames whose whole image arrives in a single sequential scan can be produced this way: baseline and
/// extended sequential DCT frames, 8-bit or 12-bit, with one interleaved scan (or a single-component frame).
/// Progressive and lossless frames, and sequential files that spread their components over several scans, need
/// the whole frame and are left to the full decoder.
/// </remarks>
internal sealed partial class JpegDecoderCore
{
    /// <summary>Initial size of the window a streaming decode reads its input through.</summary>
    private const int StreamWindowSize = 64 * 1024;

    /// <summary>The input of a streaming decode; null for an in-memory decode.</summary>
    private readonly Stream? input;

    /// <summary>Total bytes read from <see cref="input"/>, held to <see cref="DecoderOptions.MaxEncodedBytes"/>.</summary>
    private long inputBytesRead;

    /// <summary>True once <see cref="input"/> has reported its end.</summary>
    private bool inputEnded;

    // Streaming scan state.
    private JpegComponent[] streamScan = Array.Empty<JpegComponent>();
    private int streamStripsDecoded;
    private int streamUnitsDecoded;

    private JpegDecoderCore(Stream input, DecoderOptions options, int scaleDenominator)
        : this(new byte[StreamWindowSize], 0, options, scaleDenominator)
    {
        this.input = input;
    }

    /// <summary>Output rows each MCU row (strip) contributes.</summary>
    private int StripOutputRows => this.maxV * this.outputBlockSize;

    /// <summary>Creates a decoder that reads <paramref name="input"/> incrementally.</summary>
    internal static JpegDecoderCore ForStream(Stream input, DecoderOptions options, int scaleDenominator)
        => new(input, options, scaleDenominator);

    /// <summary>
    /// Reads the markers up to and including the first scan header and prepares the strips. Returns false, having
    /// allocated no sample memory, when the frame cannot be produced row by row.
    /// </summary>
    internal bool TryBeginStreaming()
    {
        this.ReadStartOfImage();
        while (true)
        {
            OperationExecution.Checkpoint();
            MarkerResult result = this.ProcessNextMarker();
            if (this.frameParsed && (this.progressive || this.lossless))
            {
                return false;
            }

            if (result == MarkerResult.EndOfImage)
            {
                // ProcessNextMarker only reports an EOI after a scan, which this loop never lets happen.
                throw new InvalidImageContentException("JPEG ended before any image data.");
            }

            if (result == MarkerResult.StartOfScan)
            {
                break;
            }
        }

        if (!this.frameParsed)
        {
            throw new InvalidImageContentException("JPEG scan appeared before the frame header.");
        }

        if (this.components.Length is not (1 or 3 or 4))
        {
            return false;
        }

        JpegComponent[] scan = this.ReadScanHeader();
        if (scan.Length != this.components.Length)
        {
            // The components are spread over several scans: the first one alone does not make a single row.
            return false;
        }

        foreach (JpegComponent component in this.components)
        {
            component.StripRows = component.V * component.BlockSize;
            long stripSamples = 3L * component.StripRows * component.PlaneWidth;
            if (stripSamples > int.MaxValue)
            {
                throw new InvalidImageContentException("JPEG component row is too large to decode.");
            }

            if (this.WideSamples)
            {
                component.WidePlane = new ushort[stripSamples];
            }
            else
            {
                component.Plane = new byte[stripSamples];
            }
        }

        this.streamScan = scan;
        return true;
    }

    /// <summary>
    /// Writes output row <paramref name="y"/> (rows are requested in order) into <paramref name="destination"/>,
    /// decoding MCU rows as they become needed.
    /// </summary>
    internal void ReadStreamingRow<TPixel>(int y, Span<TPixel> destination, RowScratch scratch)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int strip = y / this.StripOutputRows;
        int needed = Math.Min(strip + 2, this.mcusY);
        while (this.streamStripsDecoded < needed)
        {
            this.DecodeStrip(this.streamStripsDecoded++);
        }

        this.ConvertRow(y, destination, scratch);
    }

    /// <summary>Creates the per-row scratch the conversion needs; the caller disposes it.</summary>
    internal RowScratch CreateRowScratch() => new(this);

    /// <summary>Entropy-decodes MCU row <paramref name="my"/> into strip slot my % 3, after clearing it.</summary>
    private void DecodeStrip(int my)
    {
        OperationExecution.Checkpoint();

        // A segment that ends early leaves blocks undecoded; clearing the slot first makes them read as zero
        // samples, exactly as in the freshly allocated plane of a full decode.
        foreach (JpegComponent component in this.components)
        {
            int offset = component.RowOffset(my * component.StripRows);
            int length = component.StripRows * component.PlaneWidth;
            if (this.WideSamples)
            {
                component.WidePlane.AsSpan(offset, length).Clear();
            }
            else
            {
                component.Plane.AsSpan(offset, length).Clear();
            }
        }

        if (this.streamScan.Length == 1)
        {
            JpegComponent component = this.streamScan[0];
            int totalBlocks = component.BlocksPerLine * component.BlocksPerColumn;
            for (int v = 0; v < component.V; v++)
            {
                int by = (my * component.V) + v;
                if (by < component.BlocksPerColumn)
                {
                    this.DecodeBlockRow(component, by, ref this.streamUnitsDecoded, totalBlocks, this.streamScan);
                }
            }
        }
        else
        {
            this.DecodeMcuRow(this.streamScan, my, ref this.streamUnitsDecoded, this.mcusX * this.mcusY);
        }

        if (my == this.mcusY - 1)
        {
            this.scansDecoded = 1;
            this.ExpectEndOfImage();
        }
    }

    /// <summary>
    /// After the last MCU row: the remaining markers must lead to EOI, as a full decode requires. Further scans
    /// cannot occur in a sequential file whose first scan covered every component.
    /// </summary>
    private void ExpectEndOfImage()
    {
        while (true)
        {
            OperationExecution.Checkpoint();
            MarkerResult result = this.ProcessNextMarker();
            if (result == MarkerResult.EndOfImage)
            {
                return;
            }

            if (result == MarkerResult.StartOfScan)
            {
                throw new InvalidImageContentException("JPEG sequential frame has a scan after its components were complete.");
            }
        }
    }

    /// <summary>
    /// Streaming input only: moves the unread bytes to the front of the window, grows it when
    /// <paramref name="count"/> does not fit, and reads until <paramref name="count"/> bytes are available or the
    /// input ends. Returns whether they are available. For an in-memory decode the data is all there is.
    /// </summary>
    private bool Refill(int count)
    {
        if (this.input is null || this.inputEnded)
        {
            return this.dataLength - this.pos >= count;
        }

        int remaining = this.dataLength - this.pos;
        if (this.pos > 0)
        {
            Buffer.BlockCopy(this.data, this.pos, this.data, 0, remaining);
            this.pos = 0;
            this.dataLength = remaining;
        }

        if (count > this.data.Length)
        {
            Array.Resize(ref this.data, Math.Max(count, this.data.Length * 2));
        }

        while (this.dataLength < count)
        {
            int read = this.input.Read(this.data, this.dataLength, this.data.Length - this.dataLength);
            if (read <= 0)
            {
                this.inputEnded = true;
                break;
            }

            this.dataLength += read;
            this.inputBytesRead += read;
            this.options.EnsureEncodedWithinLimits(this.inputBytesRead);
        }

        return this.dataLength - this.pos >= count;
    }
}
