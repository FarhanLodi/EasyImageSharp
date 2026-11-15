using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Exact and grid-approximated bilateral filtering of a frame's colour channels.</summary>
internal static class BilateralOps
{
    /// <summary>Grid cells per channel above which the grid's spatial cells are coarsened (64 MiB of floats per array).</summary>
    internal const long MaxGridCells = 1L << 24;

    /// <summary>Margin cells on each side of every grid axis, so the 5-tap blur never needs a bounds check.</summary>
    private const int Pad = 2;

    /// <summary>
    /// The splat and slice tents add 1/6 cell^2 of variance each and the [1 4 6 4 1] blur adds 1, so cells of
    /// <c>sigma / sqrt(4/3)</c> give an overall Gaussian of the requested sigma.
    /// </summary>
    private static readonly double CellPerSigma = 1 / Math.Sqrt(4.0 / 3.0);

    public static void Exact<TPixel>(ImageFrame<TPixel> frame, int radius, float sigmaSpatial, float sigmaRange)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        float[] rgb = DenoisePlanes.ReadRgb(frame);
        float[] output = new float[rgb.Length];

        // The circular window: offsets and spatial weights.
        var offsetX = new List<int>();
        var offsetY = new List<int>();
        var spatial = new List<double>();
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int d2 = (dx * dx) + (dy * dy);
                if (d2 <= radius * radius)
                {
                    offsetX.Add(dx);
                    offsetY.Add(dy);
                    spatial.Add(Math.Exp(-d2 / (2.0 * sigmaSpatial * sigmaSpatial)));
                }
            }
        }

        int[] ox = offsetX.ToArray();
        int[] oy = offsetY.ToArray();
        double[] ws = spatial.ToArray();
        double inverse = 1 / (2.0 * sigmaRange * sigmaRange);

        // 8-bit inputs have integer squared distances (in units of 1/255^2): tabulate the range weight exactly.
        double[]? rangeTable = null;
        if (!PixelOps.IsHighPrecision<TPixel>())
        {
            rangeTable = new double[(3 * 255 * 255) + 1];
            for (int k = 0; k < rangeTable.Length; k++)
            {
                rangeTable[k] = Math.Exp(-(k / (255.0 * 255.0)) * inverse);
            }
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int c = ((y * width) + x) * 3;
                    float cr = rgb[c], cg = rgb[c + 1], cb = rgb[c + 2];
                    double sumW = 0, sumR = 0, sumG = 0, sumB = 0;
                    for (int k = 0; k < ox.Length; k++)
                    {
                        int nx = x + ox[k];
                        int ny = y + oy[k];
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int q = ((ny * width) + nx) * 3;
                        float qr = rgb[q], qg = rgb[q + 1], qb = rgb[q + 2];
                        float dr = qr - cr, dg = qg - cg, db = qb - cb;
                        float d2 = (dr * dr) + (dg * dg) + (db * db);
                        double range = rangeTable is not null
                            ? rangeTable[(int)((d2 * 65025f) + 0.5f)]
                            : Math.Exp(-d2 * inverse);
                        double weight = ws[k] * range;
                        sumW += weight;
                        sumR += weight * qr;
                        sumG += weight * qg;
                        sumB += weight * qb;
                    }

                    output[c] = (float)(sumR / sumW);
                    output[c + 1] = (float)(sumG / sumW);
                    output[c + 2] = (float)(sumB / sumW);
                }
            }
        });

        DenoisePlanes.WriteRgb(frame, output);
    }

    public static void Grid<TPixel>(ImageFrame<TPixel> frame, float sigmaSpatial, float sigmaRange)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        float[] rgb = DenoisePlanes.ReadRgb(frame);
        float[] output = new float[rgb.Length];
        // The channels have independent grids, so they are filtered concurrently; each one's splat is sequential.
        ParallelRowIterator.IterateRows(width * height, 3, (start, end) =>
        {
            for (int channel = start; channel < end; channel++)
            {
                FilterChannel(rgb, output, channel, width, height, sigmaSpatial * CellPerSigma, sigmaRange * CellPerSigma);
            }
        });

        DenoisePlanes.WriteRgb(frame, output);
    }

    private static void FilterChannel(float[] rgb, float[] output, int channel, int width, int height, double cellSpatial, double cellRange)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = channel; i < rgb.Length; i += 3)
        {
            min = Math.Min(min, rgb[i]);
            max = Math.Max(max, rgb[i]);
        }

        if (!(max > min))
        {
            // A constant channel is its own bilateral average.
            for (int i = channel; i < rgb.Length; i += 3)
            {
                output[i] = rgb[i];
            }

            return;
        }

        // Size the grid, coarsening the spatial cells (then the range cells) until it fits the cell budget.
        int gw, gh, gd;
        while (true)
        {
            gw = (int)((width - 1) / cellSpatial) + 2 + (2 * Pad);
            gh = (int)((height - 1) / cellSpatial) + 2 + (2 * Pad);
            gd = (int)((max - min) / cellRange) + 2 + (2 * Pad);
            if ((long)gw * gh * gd <= MaxGridCells)
            {
                break;
            }

            if (gw > 2 * (Pad + 2) || gh > 2 * (Pad + 2))
            {
                cellSpatial *= 1.25;
            }
            else
            {
                cellRange *= 1.25;
            }
        }

        int cells = gw * gh * gd;
        float[] values = new float[cells];
        float[] weights = new float[cells];

        // Splat: each pixel distributes (value, 1) over the eight cells around its grid position. Sequential, so the
        // float accumulation order (and therefore the result) never depends on the thread count.
        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
            double gy = (y / cellSpatial) + Pad;
            for (int x = 0; x < width; x++)
            {
                float v = rgb[(((y * width) + x) * 3) + channel];
                double gx = (x / cellSpatial) + Pad;
                double gz = ((v - min) / cellRange) + Pad;
                Splat(values, weights, gw, gh, gx, gy, gz, v);
            }
        }

        // Blur along each axis with [1 4 6 4 1] / 16.
        float[] scratchValues = new float[cells];
        float[] scratchWeights = new float[cells];
        BlurAxis(values, weights, scratchValues, scratchWeights, gw, gh, 1, gw, gh * gd);
        BlurAxis(scratchValues, scratchWeights, values, weights, gw, gh, gw, gh, gw * gd);
        BlurAxis(values, weights, scratchValues, scratchWeights, gw, gh, gw * gh, gd, gw * gh);

        // Slice: trilinear lookup at every pixel's own grid position.
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                double gy = (y / cellSpatial) + Pad;
                for (int x = 0; x < width; x++)
                {
                    int i = (((y * width) + x) * 3) + channel;
                    float v = rgb[i];
                    double gx = (x / cellSpatial) + Pad;
                    double gz = ((v - min) / cellRange) + Pad;
                    (double sum, double weight) = Slice(scratchValues, scratchWeights, gw, gh, gx, gy, gz);
                    output[i] = weight > 1e-12 ? (float)(sum / weight) : v;
                }
            }
        });
    }

    private static void Splat(float[] values, float[] weights, int gw, int gh, double gx, double gy, double gz, float v)
    {
        int x0 = (int)gx, y0 = (int)gy, z0 = (int)gz;
        float fx = (float)(gx - x0), fy = (float)(gy - y0), fz = (float)(gz - z0);
        for (int dz = 0; dz < 2; dz++)
        {
            float wz = dz == 0 ? 1 - fz : fz;
            for (int dy = 0; dy < 2; dy++)
            {
                float wy = dy == 0 ? 1 - fy : fy;
                int row = ((((z0 + dz) * gh) + y0 + dy) * gw) + x0;
                float wxy0 = wz * wy * (1 - fx);
                float wxy1 = wz * wy * fx;
                values[row] += wxy0 * v;
                weights[row] += wxy0;
                values[row + 1] += wxy1 * v;
                weights[row + 1] += wxy1;
            }
        }
    }

    private static (double Sum, double Weight) Slice(float[] values, float[] weights, int gw, int gh, double gx, double gy, double gz)
    {
        int x0 = (int)gx, y0 = (int)gy, z0 = (int)gz;
        double fx = gx - x0, fy = gy - y0, fz = gz - z0;
        double sum = 0, weight = 0;
        for (int dz = 0; dz < 2; dz++)
        {
            double wz = dz == 0 ? 1 - fz : fz;
            for (int dy = 0; dy < 2; dy++)
            {
                double wy = dy == 0 ? 1 - fy : fy;
                int row = ((((z0 + dz) * gh) + y0 + dy) * gw) + x0;
                double w0 = wz * wy * (1 - fx);
                double w1 = wz * wy * fx;
                sum += (w0 * values[row]) + (w1 * values[row + 1]);
                weight += (w0 * weights[row]) + (w1 * weights[row + 1]);
            }
        }

        return (sum, weight);
    }

    /// <summary>
    /// One separable blur pass over the grid: <paramref name="lines"/> lines of <paramref name="length"/> cells
    /// spaced <paramref name="stride"/> apart. Margin cells are zero, so the five taps never leave the grid.
    /// </summary>
    private static void BlurAxis(float[] sourceValues, float[] sourceWeights, float[] targetValues, float[] targetWeights,
        int gw, int gh, int stride, int length, int lines)
    {
        ParallelRowIterator.IterateRows(length, lines, (start, end) =>
        {
            for (int line = start; line < end; line++)
            {
                int origin = LineOrigin(line, stride, gw, gh);
                for (int i = 0; i < length; i++)
                {
                    float v = 0, w = 0;
                    for (int k = -2; k <= 2; k++)
                    {
                        int j = i + k;
                        if ((uint)j >= (uint)length)
                        {
                            continue;
                        }

                        float tap = k switch { 0 => 6f / 16, -1 or 1 => 4f / 16, _ => 1f / 16 };
                        v += tap * sourceValues[origin + (j * stride)];
                        w += tap * sourceWeights[origin + (j * stride)];
                    }

                    targetValues[origin + (i * stride)] = v;
                    targetWeights[origin + (i * stride)] = w;
                }
            }
        });
    }

    /// <summary>The index of the first cell of a blur line, enumerating the two axes other than the blurred one.</summary>
    private static int LineOrigin(int line, int stride, int gw, int gh)
    {
        if (stride == 1)
        {
            // Lines along x: one per (y, z).
            return line * gw;
        }

        if (stride == gw)
        {
            // Lines along y: one per (x, z).
            int z = line / gw;
            int x = line % gw;
            return (z * gw * gh) + x;
        }

        // Lines along z: one per (x, y).
        return line;
    }
}
