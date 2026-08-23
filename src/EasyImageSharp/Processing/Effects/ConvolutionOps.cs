using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// The convolution engine. Frames are converted to straight (non-premultiplied) RGBA planes of
/// <see cref="Vector4"/> values in the 0-255 range, convolved in single precision with edge replication
/// (samples outside the region clamp to its nearest edge pixel) and written back with round-half-up.
/// Rows are split across threads with <see cref="ParallelRowIterator"/>.
/// </summary>
internal static class ConvolutionOps
{
    // ----- Frame <-> plane conversion -----

    /// <summary>Reads a region of the frame into 0-255 float RGBA values.</summary>
    public static Vector4[] ReadRegion<TPixel>(ImageFrame<TPixel> frame, Rectangle region)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = region.Width;
        var vectors = new Vector4[width * region.Height];
        ParallelRowIterator.IterateRows(width, region.Height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(region.Y + y).Slice(region.X, width), row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    Rgba32 p = row[x];
                    vectors[offset + x] = new Vector4(p.R, p.G, p.B, p.A);
                }
            }
        });

        return vectors;
    }

    /// <summary>
    /// Writes 0-255 float RGBA values back into a region of the frame. When <paramref name="alphaSource"/> is
    /// given, alpha is taken from it instead of from <paramref name="vectors"/>.
    /// </summary>
    public static void WriteRegion<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Vector4[] vectors, Vector4[]? alphaSource)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = region.Width;
        ParallelRowIterator.IterateRows(width, region.Height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    Vector4 v = vectors[offset + x];
                    float alpha = alphaSource is null ? v.W : alphaSource[offset + x].W;
                    row[x] = new Rgba32(
                        RowProcessor.ClampToByte(v.X),
                        RowProcessor.ClampToByte(v.Y),
                        RowProcessor.ClampToByte(v.Z),
                        RowProcessor.ClampToByte(alpha));
                }

                PixelOps.FromRgba32<TPixel>(row, frame.GetRowSpan(region.Y + y).Slice(region.X, width));
            }
        });
    }

    // ----- Kernels -----

    /// <summary>Builds a normalised 1-D Gaussian kernel with radius <c>ceil(3 sigma)</c> (at least 1).</summary>
    public static float[] BuildGaussianKernel(float sigma)
    {
        int radius = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var kernel = new float[(2 * radius) + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            float value = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            kernel[i + radius] = value;
            sum += value;
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    /// <summary>Builds a uniform 1-D box kernel of length <c>2 radius + 1</c>.</summary>
    public static float[] BuildBoxKernel(int radius)
    {
        var kernel = new float[(2 * radius) + 1];
        Array.Fill(kernel, 1f / kernel.Length);
        return kernel;
    }

    // ----- Convolution passes -----

    /// <summary>
    /// Convolves a plane with a two-dimensional kernel (row-major, <paramref name="kernelWidth"/> x
    /// <paramref name="kernelHeight"/>) anchored at <c>((w-1)/2, (h-1)/2)</c>.
    /// </summary>
    public static Vector4[] Convolve2D(Vector4[] source, int width, int height, float[] kernel, int kernelWidth, int kernelHeight)
    {
        if (kernel.Length != kernelWidth * kernelHeight)
        {
            throw new ArgumentException("Kernel length does not match its dimensions.", nameof(kernel));
        }

        var result = new Vector4[source.Length];
        int anchorX = (kernelWidth - 1) / 2;
        int anchorY = (kernelHeight - 1) / 2;
        int maxX = width - 1;
        int maxY = height - 1;

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    for (int ky = 0; ky < kernelHeight; ky++)
                    {
                        int sy = Math.Clamp(y + ky - anchorY, 0, maxY);
                        int sourceRow = sy * width;
                        int kernelRow = ky * kernelWidth;
                        for (int kx = 0; kx < kernelWidth; kx++)
                        {
                            int sx = Math.Clamp(x + kx - anchorX, 0, maxX);
                            sum += source[sourceRow + sx] * kernel[kernelRow + kx];
                        }
                    }

                    result[row + x] = sum;
                }
            }
        });

        return result;
    }

    /// <summary>Convolves a plane with a horizontal 1-D kernel followed by a vertical 1-D kernel.</summary>
    public static Vector4[] ConvolveSeparable(Vector4[] source, int width, int height, float[] kernelX, float[] kernelY)
    {
        Vector4[] horizontal = ConvolveHorizontal(source, width, height, kernelX);
        return ConvolveVertical(horizontal, width, height, kernelY);
    }

    /// <summary>Convolves every row with a 1-D kernel anchored at <c>(length-1)/2</c>.</summary>
    public static Vector4[] ConvolveHorizontal(Vector4[] source, int width, int height, float[] kernel)
    {
        var result = new Vector4[source.Length];
        int anchor = (kernel.Length - 1) / 2;
        int length = kernel.Length;
        int maxX = width - 1;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    for (int i = 0; i < length; i++)
                    {
                        int sx = Math.Clamp(x + i - anchor, 0, maxX);
                        sum += source[row + sx] * kernel[i];
                    }

                    result[row + x] = sum;
                }
            }
        });

        return result;
    }

    /// <summary>Convolves every column with a 1-D kernel anchored at <c>(length-1)/2</c>.</summary>
    public static Vector4[] ConvolveVertical(Vector4[] source, int width, int height, float[] kernel)
    {
        var result = new Vector4[source.Length];
        int anchor = (kernel.Length - 1) / 2;
        int length = kernel.Length;
        int maxY = height - 1;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    for (int i = 0; i < length; i++)
                    {
                        int sy = Math.Clamp(y + i - anchor, 0, maxY);
                        sum += source[(sy * width) + x] * kernel[i];
                    }

                    result[row + x] = sum;
                }
            }
        });

        return result;
    }

    // ----- Frame-level operations -----

    /// <summary>Convolves a region of the frame with a 2-D kernel in place.</summary>
    public static void Convolve2D<TPixel>(
        ImageFrame<TPixel> frame, Rectangle region, float[] kernel, int kernelWidth, int kernelHeight, bool preserveAlpha)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        Vector4[] source = ReadRegion(frame, region);
        Vector4[] result = Convolve2D(source, region.Width, region.Height, kernel, kernelWidth, kernelHeight);
        WriteRegion(frame, region, result, preserveAlpha ? source : null);
    }

    /// <summary>Convolves a region of the frame with a separable kernel pair in place.</summary>
    public static void ConvolveSeparable<TPixel>(
        ImageFrame<TPixel> frame, Rectangle region, float[] kernelX, float[] kernelY, bool preserveAlpha)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        Vector4[] source = ReadRegion(frame, region);
        Vector4[] result = ConvolveSeparable(source, region.Width, region.Height, kernelX, kernelY);
        WriteRegion(frame, region, result, preserveAlpha ? source : null);
    }

    /// <summary>Applies a single-kernel edge detector: the response is clamped to 0-255, alpha is preserved.</summary>
    public static void DetectEdges<TPixel>(ImageFrame<TPixel> frame, Rectangle region, DenseMatrix<float> kernel)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        Vector4[] source = ReadRegion(frame, region);
        Vector4[] result = Convolve2D(source, region.Width, region.Height, kernel.Span.ToArray(), kernel.Columns, kernel.Rows);
        WriteRegion(frame, region, result, source);
    }

    /// <summary>Applies a gradient-pair edge detector: the response is <c>sqrt(gx² + gy²)</c> per channel, alpha is preserved.</summary>
    public static void DetectEdges<TPixel>(ImageFrame<TPixel> frame, Rectangle region, DenseMatrix<float> kernelX, DenseMatrix<float> kernelY)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        int width = region.Width;
        int height = region.Height;
        Vector4[] source = ReadRegion(frame, region);
        Vector4[] gx = Convolve2D(source, width, height, kernelX.Span.ToArray(), kernelX.Columns, kernelX.Rows);
        Vector4[] gy = Convolve2D(source, width, height, kernelY.Span.ToArray(), kernelY.Columns, kernelY.Rows);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int i = start * width; i < end * width; i++)
            {
                Vector4 a = gx[i];
                Vector4 b = gy[i];
                gx[i] = Vector4.SquareRoot((a * a) + (b * b));
            }
        });

        WriteRegion(frame, region, gx, source);
    }

    /// <summary>Applies a compass edge detector: the response is the maximum over all kernels per channel, alpha is preserved.</summary>
    public static void DetectEdges<TPixel>(ImageFrame<TPixel> frame, Rectangle region, DenseMatrix<float>[] kernels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0 || kernels.Length == 0)
        {
            return;
        }

        int width = region.Width;
        int height = region.Height;
        Vector4[] source = ReadRegion(frame, region);
        Vector4[]? best = null;
        foreach (DenseMatrix<float> kernel in kernels)
        {
            Vector4[] response = Convolve2D(source, width, height, kernel.Span.ToArray(), kernel.Columns, kernel.Rows);
            if (best is null)
            {
                best = response;
                continue;
            }

            Vector4[] current = best;
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int i = start * width; i < end * width; i++)
                {
                    current[i] = Vector4.Max(current[i], response[i]);
                }
            });
        }

        WriteRegion(frame, region, best!, source);
    }
}
