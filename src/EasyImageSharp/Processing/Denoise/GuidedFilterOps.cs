using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>The guided filter of He, Sun and Tang, with box means whose windows are clipped at the border.</summary>
internal static class GuidedFilterOps
{
    public const int MaxRadius = 256;

    public static void Validate(int radius, float epsilon)
    {
        if (radius is < 1 or > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be between 1 and 256.");
        }

        if (!(epsilon > 0f) || !float.IsFinite(epsilon))
        {
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "Epsilon must be a positive finite number.");
        }
    }

    /// <summary>The BT.601 luma (0-1) of the guide's root frame, read through its 8-bit values.</summary>
    public static float[] GuideLuma(Image guide)
    {
        int width = guide.Width;
        int height = guide.Height;
        float[] luma = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            OperationExecution.Checkpoint();
            for (int x = 0; x < width; x++)
            {
                Rgba32 p = guide.GetPixelRgba32(x, y);
                luma[(y * width) + x] = (float)(((0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B)) / 255.0);
            }
        }

        return luma;
    }

    public static void Apply<TPixel>(ImageFrame<TPixel> frame, float[]? guide, int radius, float epsilon)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int n = width * height;
        float[] rgb = DenoisePlanes.ReadRgb(frame);
        float[] output = new float[rgb.Length];
        float[] p = new float[n];
        float[] product = new float[n];
        float[]? meanGuide = null, varianceGuide = null;
        if (guide is not null)
        {
            meanGuide = BoxMean(guide, width, height, radius);
            for (int i = 0; i < n; i++)
            {
                product[i] = guide[i] * guide[i];
            }

            varianceGuide = BoxMean(product, width, height, radius);
            for (int i = 0; i < n; i++)
            {
                varianceGuide[i] -= meanGuide[i] * meanGuide[i];
            }
        }

        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < n; i++)
            {
                p[i] = rgb[(i * 3) + channel];
            }

            float[] input = guide ?? p;
            float[] meanP = BoxMean(p, width, height, radius);
            float[] meanI = meanGuide ?? meanP;
            for (int i = 0; i < n; i++)
            {
                product[i] = input[i] * p[i];
            }

            float[] correlation = BoxMean(product, width, height, radius);
            float[] a = new float[n];
            float[] b = new float[n];
            for (int i = 0; i < n; i++)
            {
                float covariance = correlation[i] - (meanI[i] * meanP[i]);
                float variance = varianceGuide is null ? covariance : varianceGuide[i];
                a[i] = covariance / (variance + epsilon);
                b[i] = meanP[i] - (a[i] * meanI[i]);
            }

            float[] meanA = BoxMean(a, width, height, radius);
            float[] meanB = BoxMean(b, width, height, radius);
            for (int i = 0; i < n; i++)
            {
                output[(i * 3) + channel] = (meanA[i] * input[i]) + meanB[i];
            }
        }

        DenoisePlanes.WriteRgb(frame, output);
    }

    /// <summary>
    /// Mean over the <c>(2r+1)</c>-square window clipped to the image (the reference implementation's
    /// <c>boxfilter(I, r) ./ boxfilter(ones, r)</c>): running row sums, then running column sums in double precision,
    /// each in a fixed order, so the result never depends on the thread count.
    /// </summary>
    internal static float[] BoxMean(float[] source, int width, int height, int radius)
    {
        float[] rows = new float[source.Length];
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int o = y * width;
                double sum = 0;
                for (int x = 0; x <= Math.Min(width - 1, radius); x++)
                {
                    sum += source[o + x];
                }

                for (int x = 0; x < width; x++)
                {
                    rows[o + x] = (float)sum;
                    int add = x + radius + 1;
                    int remove = x - radius;
                    if (add < width)
                    {
                        sum += source[o + add];
                    }

                    if (remove >= 0)
                    {
                        sum -= source[o + remove];
                    }
                }
            }
        });

        // Columns are independent, so blocks of them run in parallel; within a column the running sum always
        // visits the rows in the same order, whatever the blocking.
        float[] result = new float[source.Length];
        const int Block = 64;
        int blocks = (width + Block - 1) / Block;
        ParallelRowIterator.IterateRows(height * Block, blocks, (startBlock, endBlock) =>
        {
            for (int block = startBlock; block < endBlock; block++)
            {
                int x0 = block * Block;
                int x1 = Math.Min(width, x0 + Block);
                double[] columns = new double[x1 - x0];
                for (int y = 0; y <= Math.Min(height - 1, radius); y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        columns[x - x0] += rows[(y * width) + x];
                    }
                }

                for (int y = 0; y < height; y++)
                {
                    int rowCount = Math.Min(height - 1, y + radius) - Math.Max(0, y - radius) + 1;
                    for (int x = x0; x < x1; x++)
                    {
                        int columnCount = Math.Min(width - 1, x + radius) - Math.Max(0, x - radius) + 1;
                        result[(y * width) + x] = (float)(columns[x - x0] / (rowCount * columnCount));
                    }

                    int add = y + radius + 1;
                    int remove = y - radius;
                    if (add < height)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            columns[x - x0] += rows[(add * width) + x];
                        }
                    }

                    if (remove >= 0)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            columns[x - x0] -= rows[(remove * width) + x];
                        }
                    }
                }
            }
        });

        return result;
    }
}
