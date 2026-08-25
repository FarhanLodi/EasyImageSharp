using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing.Dithering;

/// <summary>
/// Ordered (threshold-matrix) dithering: every pixel is offset by a value from a tiled matrix, normalised to
/// (-0.5, 0.5) and scaled by the palette's typical colour spacing, before it is matched to the palette. Rows
/// are independent, so ordered dithering runs in parallel; the pattern is fixed relative to the frame origin.
/// </summary>
public sealed class OrderedDither : IDither
{
    private readonly float[] thresholds;
    private readonly int rows;
    private readonly int columns;

    /// <summary>
    /// Creates an ordered dither from a threshold matrix whose entries are the distinct integers
    /// 0 to <c>rows * columns - 1</c> (a Bayer matrix, for example).
    /// </summary>
    public OrderedDither(int[,] thresholdMatrix)
    {
        ArgumentNullException.ThrowIfNull(thresholdMatrix);
        this.rows = thresholdMatrix.GetLength(0);
        this.columns = thresholdMatrix.GetLength(1);
        if (this.rows == 0 || this.columns == 0)
        {
            throw new ArgumentException("The threshold matrix must have at least one row and one column.", nameof(thresholdMatrix));
        }

        int count = this.rows * this.columns;
        this.thresholds = new float[count];
        for (int y = 0; y < this.rows; y++)
        {
            for (int x = 0; x < this.columns; x++)
            {
                int value = thresholdMatrix[y, x];
                if ((uint)value >= (uint)count)
                {
                    throw new ArgumentException($"Threshold values must lie between 0 and {count - 1}.", nameof(thresholdMatrix));
                }

                this.thresholds[(y * this.columns) + x] = ((value + 0.5f) / count) - 0.5f;
            }
        }
    }

    /// <summary>Builds the Bayer matrix of the given power-of-two size (2, 4, 8, 16, ...).</summary>
    public static OrderedDither CreateBayer(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The Bayer matrix size must be a power of two of at least 2.");
        }

        int[,] matrix = { { 0 } };
        for (int n = 1; n < size; n *= 2)
        {
            int[,] next = new int[n * 2, n * 2];
            for (int y = 0; y < n * 2; y++)
            {
                for (int x = 0; x < n * 2; x++)
                {
                    int quadrant = ((y / n) * 2) + (x / n);
                    int offset = quadrant switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
                    next[y, x] = (4 * matrix[y % n, x % n]) + offset;
                }
            }

            matrix = next;
        }

        return new OrderedDither(matrix);
    }

    public void Apply<TPixel>(
        ImageFrame<TPixel> frame, Rectangle bounds, IPaletteMap paletteMap, float scale, Memory<byte> indices, bool replacePixels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(paletteMap);
        DitherHelpers.ValidateRegion(frame, bounds, indices);
        scale = Math.Clamp(scale, 0f, 1f);

        float amplitude = DitherHelpers.EstimatePaletteSpacing(paletteMap.Palette) * scale;
        int width = bounds.Width;
        int height = bounds.Height;
        float[] thresholds = this.thresholds;
        int rows = this.rows;
        int columns = this.columns;

        ParallelRowIterator.IterateRows(width, height, (startRow, endRow) =>
        {
            var source = new Rgba32[width];
            for (int y = startRow; y < endRow; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(bounds.Y + y).Slice(bounds.X, width);
                PixelOps.ToRgba32<TPixel>(pixels, source);
                int thresholdRow = ((bounds.Y + y) % rows) * columns;
                Span<byte> indexRow = indices.IsEmpty ? Span<byte>.Empty : indices.Span.Slice(y * width, width);

                for (int x = 0; x < width; x++)
                {
                    float offset = thresholds[thresholdRow + ((bounds.X + x) % columns)] * amplitude;
                    Rgba32 p = source[x];
                    var candidate = new Rgba32(
                        DitherHelpers.ClampToByte(p.R + offset),
                        DitherHelpers.ClampToByte(p.G + offset),
                        DitherHelpers.ClampToByte(p.B + offset),
                        p.A == byte.MaxValue ? byte.MaxValue : DitherHelpers.ClampToByte(p.A + offset));

                    int index = paletteMap.GetPaletteIndex(candidate, out Rgba32 match);
                    if (!indexRow.IsEmpty)
                    {
                        indexRow[x] = (byte)index;
                    }

                    if (replacePixels)
                    {
                        pixels[x] = TPixel.FromRgba32(match);
                    }
                }
            }
        });
    }
}
