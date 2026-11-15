namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Lossless (SOF3) scan decoding per ITU-T T.81 Annex H: every sample is predicted from its already decoded
/// neighbours with one of seven predictors and the Huffman-coded difference is added modulo 2^16. Samples carry
/// P - Pt bits in the entropy-coded data and are scaled back by the point transform Pt.
/// </summary>
/// <remarks>
/// As in libjpeg, an MCU row's differences are decoded first and then undifferenced one sample line at a time, and
/// a restart interval must span whole MCU rows, because the "first line" prediction rule restarts with it.
/// </remarks>
internal sealed partial class JpegDecoderCore
{
    /// <summary>The predictor selection value (Ss) of the current lossless scan, 1-7.</summary>
    private int losslessPredictor;

    /// <summary>The point transform (Al) of the current lossless scan.</summary>
    private int pointTransform;

    /// <summary>Validates a lossless scan header and resolves the DC Huffman tables it codes differences with.</summary>
    private void PrepareLosslessScan(JpegComponent[] scanComponents, int predictor, int al)
    {
        // Ss = 0 selects "no prediction", which T.81 reserves for the differential frames of the hierarchical mode.
        if (predictor is < 1 or > 7)
        {
            throw new InvalidImageContentException($"Invalid lossless JPEG predictor selection value {predictor}.");
        }

        if (al >= this.precision)
        {
            throw new InvalidImageContentException(
                $"Lossless JPEG point transform {al} leaves no bits of the {this.precision}-bit samples.");
        }

        foreach (JpegComponent component in scanComponents)
        {
            component.DcTable = this.dcTables[component.DcTableId]
                ?? throw new InvalidImageContentException($"JPEG DC Huffman table {component.DcTableId} is undefined.");
        }

        this.losslessPredictor = predictor;
        this.pointTransform = al;
    }

    private void DecodeLosslessScan(JpegComponent[] scanComponents)
    {
        int count = scanComponents.Length;
        bool interleaved = count > 1;

        // A non-interleaved scan codes its component's own samples line by line, one sample per MCU; an
        // interleaved one codes whole MCUs of H x V samples per component over the padded MCU grid.
        int mcusPerRow = interleaved ? this.mcusX : scanComponents[0].CompWidth;
        int mcuRows = interleaved ? this.mcusY : scanComponents[0].CompHeight;
        if (this.restartInterval > 0 && this.restartInterval % mcusPerRow != 0)
        {
            throw new NotSupportedException(
                "Lossless JPEG restart intervals that do not span whole MCU rows are not supported.");
        }

        int rowsPerInterval = this.restartInterval / mcusPerRow;
        var lineWidth = new int[count];
        var linesPerRow = new int[count];
        var columnsPerMcu = new int[count];
        var differences = new int[count][];
        var previousLine = new int[count][];
        var currentLine = new int[count][];
        for (int c = 0; c < count; c++)
        {
            JpegComponent component = scanComponents[c];
            columnsPerMcu[c] = interleaved ? component.H : 1;
            linesPerRow[c] = interleaved ? component.V : 1;
            lineWidth[c] = mcusPerRow * columnsPerMcu[c];
            differences[c] = new int[lineWidth[c] * linesPerRow[c]];
            previousLine[c] = new int[lineWidth[c]];
            currentLine[c] = new int[lineWidth[c]];
        }

        bool firstLine = true;
        for (int my = 0; my < mcuRows; my++)
        {
            OperationExecution.Checkpoint();

            // Entropy decoding: every difference of the MCU row, in MCU order.
            for (int mx = 0; mx < mcusPerRow; mx++)
            {
                for (int c = 0; c < count; c++)
                {
                    HuffmanTable table = scanComponents[c].DcTable!;
                    int[] target = differences[c];
                    int columns = columnsPerMcu[c];
                    for (int v = 0; v < linesPerRow[c]; v++)
                    {
                        int rowStart = (v * lineWidth[c]) + (mx * columns);
                        for (int h = 0; h < columns; h++)
                        {
                            target[rowStart + h] = this.DecodeLosslessDifference(table);
                        }
                    }
                }
            }

            // Prediction: undifference the sample lines this MCU row contributes to each component.
            for (int c = 0; c < count; c++)
            {
                JpegComponent component = scanComponents[c];
                for (int v = 0; v < linesPerRow[c]; v++)
                {
                    int line = (my * linesPerRow[c]) + v;
                    ReadOnlySpan<int> lineDifferences = differences[c].AsSpan(v * lineWidth[c], lineWidth[c]);
                    this.Undifference(lineDifferences, previousLine[c], currentLine[c], firstLine && v == 0);
                    this.StoreLosslessLine(component, line, currentLine[c]);
                    (previousLine[c], currentLine[c]) = (currentLine[c], previousLine[c]);
                }
            }

            firstLine = false;
            if (rowsPerInterval > 0 && (my + 1) % rowsPerInterval == 0 && my + 1 < mcuRows)
            {
                this.HandleRestart((my + 1) * mcusPerRow, mcuRows * mcusPerRow, scanComponents);
                firstLine = true;
            }
        }
    }

    /// <summary>Decodes one difference: a Huffman-coded magnitude category, 16 meaning exactly 32768 (T.81 H.1.2.2).</summary>
    private int DecodeLosslessDifference(HuffmanTable table)
    {
        int category = this.DecodeHuffman(table);
        if (category == 0)
        {
            return 0;
        }

        if (category == 16)
        {
            return 32768;
        }

        if (category > 16)
        {
            throw new InvalidImageContentException($"Invalid lossless JPEG difference category {category}.");
        }

        return Extend(this.Receive(category), category);
    }

    /// <summary>
    /// Reconstructs one sample line from its differences (T.81 H.1.2.1). The first line of a scan or restart
    /// interval predicts from the left neighbour only and starts from 2^(P - Pt - 1); every other line starts with
    /// the sample above and then uses the scan's predictor. Sums are taken modulo 2^16, as the standard requires.
    /// </summary>
    private void Undifference(ReadOnlySpan<int> differences, ReadOnlySpan<int> above, Span<int> line, bool firstLine)
    {
        int width = differences.Length;
        if (firstLine)
        {
            int ra = (differences[0] + (1 << (this.precision - this.pointTransform - 1))) & 0xFFFF;
            line[0] = ra;
            for (int x = 1; x < width; x++)
            {
                ra = (differences[x] + ra) & 0xFFFF;
                line[x] = ra;
            }

            return;
        }

        int rb = above[0];
        int left = (differences[0] + rb) & 0xFFFF;
        line[0] = left;
        int predictor = this.losslessPredictor;
        for (int x = 1; x < width; x++)
        {
            int rc = rb;
            rb = above[x];
            int prediction = predictor switch
            {
                1 => left,
                2 => rb,
                3 => rc,
                4 => left + rb - rc,
                5 => left + ((rb - rc) >> 1),
                6 => rb + ((left - rc) >> 1),
                _ => (left + rb) >> 1,
            };

            left = (differences[x] + prediction) & 0xFFFF;
            line[x] = left;
        }
    }

    /// <summary>Writes a reconstructed line into the component plane, undoing the point transform.</summary>
    private void StoreLosslessLine(JpegComponent component, int line, ReadOnlySpan<int> samples)
    {
        // Lines beyond the padded plane cannot occur for a consistent header, but a non-interleaved scan of a
        // component is never taller than the plane, so only the width needs care.
        Span<ushort> target = component.WidePlane.AsSpan(component.RowOffset(line), component.PlaneWidth);
        int shift = this.pointTransform;
        int max = this.maxSample;
        int width = Math.Min(samples.Length, target.Length);
        for (int x = 0; x < width; x++)
        {
            // In a valid stream every sample fits P - Pt bits; corrupt data may not, and is clamped.
            target[x] = (ushort)Math.Min(samples[x] << shift, max);
        }
    }
}
