namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Chroma upsampling for the JPEG decoder. The two common layouts, 2:1 horizontal (4:2:2) and 2:1 in both
/// directions (4:2:0), use the triangle filter popularised by libjpeg's "fancy upsampling" (jdsample.c):
/// each output sample is 3/4 of the nearest input sample plus 1/4 of the next nearest, with the image edges
/// replicated. Every other ratio falls back to sample replication.
/// </summary>
internal static class JpegUpsampler
{
    /// <summary>
    /// Produces output row <paramref name="y"/> (image coordinates) of a component at full resolution.
    /// <paramref name="output"/> must hold at least <c>max(outputWidth, 2 * compWidth)</c> samples.
    /// </summary>
    /// <param name="plane">The component's MCU-padded sample plane.</param>
    /// <param name="planeWidth">Row stride of <paramref name="plane"/> in samples.</param>
    /// <param name="planeHeight">Number of rows in <paramref name="plane"/>.</param>
    /// <param name="compWidth">Number of valid samples per plane row (ceil(imageWidth * h / maxH)).</param>
    /// <param name="compHeight">Number of valid plane rows (ceil(imageHeight * v / maxV)).</param>
    /// <param name="h">Horizontal sampling factor of the component.</param>
    /// <param name="v">Vertical sampling factor of the component.</param>
    /// <param name="maxH">Largest horizontal sampling factor in the frame.</param>
    /// <param name="maxV">Largest vertical sampling factor in the frame.</param>
    /// <param name="y">Output (image) row to produce.</param>
    /// <param name="outputWidth">Image width; the number of samples the replication fallback writes.</param>
    /// <param name="output">Receives the upsampled row.</param>
    public static void UpsampleRow(
        byte[] plane, int planeWidth, int planeHeight, int compWidth, int compHeight,
        int h, int v, int maxH, int maxV, int y, int outputWidth, Span<byte> output)
    {
        // libjpeg only applies the triangle filter when the component is wider than two samples.
        if (maxH == 2 * h && compWidth > 2)
        {
            if (maxV == v)
            {
                UpsampleH2V1(plane.AsSpan(y * planeWidth, compWidth), output);
                return;
            }

            if (maxV == 2 * v)
            {
                int row = y >> 1;
                int neighbour = (y & 1) == 0 ? Math.Max(row - 1, 0) : Math.Min(row + 1, compHeight - 1);
                UpsampleH2V2(
                    plane.AsSpan(row * planeWidth, compWidth),
                    plane.AsSpan(neighbour * planeWidth, compWidth),
                    output);
                return;
            }
        }

        int rowOffset = Math.Min(y * v / maxV, planeHeight - 1) * planeWidth;
        for (int x = 0; x < outputWidth; x++)
        {
            output[x] = plane[rowOffset + Math.Min(x * h / maxH, planeWidth - 1)];
        }
    }

    /// <summary>
    /// 2:1 horizontal triangle upsampling of one row: out[2i] = (3 in[i] + in[i-1] + 1) / 4 and
    /// out[2i+1] = (3 in[i] + in[i+1] + 2) / 4, with the first and last output samples copied from the edges.
    /// </summary>
    internal static void UpsampleH2V1(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int n = input.Length;
        int last = n - 1;
        output[0] = input[0];
        output[1] = (byte)(((3 * input[0]) + input[1] + 2) >> 2);
        int o = 2;
        for (int i = 1; i < last; i++)
        {
            int nearer = 3 * input[i];
            output[o++] = (byte)((nearer + input[i - 1] + 1) >> 2);
            output[o++] = (byte)((nearer + input[i + 1] + 2) >> 2);
        }

        output[o++] = (byte)(((3 * input[last]) + input[last - 1] + 1) >> 2);
        output[o] = input[last];
    }

    /// <summary>
    /// 2:1 triangle upsampling in both directions for one output row. <paramref name="nearRow"/> is the input
    /// row the output row belongs to and <paramref name="farRow"/> its vertical neighbour on the output row's
    /// side (the row above for even output rows, below for odd). Vertically each column sum is
    /// 3 near + far; horizontally the sums are blended 3:1 again, giving weights 9/16, 3/16, 3/16, 1/16.
    /// </summary>
    internal static void UpsampleH2V2(ReadOnlySpan<byte> nearRow, ReadOnlySpan<byte> farRow, Span<byte> output)
    {
        int n = nearRow.Length;
        int current = (3 * nearRow[0]) + farRow[0];
        int next = (3 * nearRow[1]) + farRow[1];
        output[0] = (byte)(((current * 4) + 8) >> 4);
        output[1] = (byte)(((current * 3) + next + 7) >> 4);
        int previous = current;
        current = next;
        int o = 2;
        for (int i = 1; i < n - 1; i++)
        {
            next = (3 * nearRow[i + 1]) + farRow[i + 1];
            output[o++] = (byte)(((current * 3) + previous + 8) >> 4);
            output[o++] = (byte)(((current * 3) + next + 7) >> 4);
            previous = current;
            current = next;
        }

        output[o++] = (byte)(((current * 3) + previous + 8) >> 4);
        output[o] = (byte)(((current * 4) + 7) >> 4);
    }
}
