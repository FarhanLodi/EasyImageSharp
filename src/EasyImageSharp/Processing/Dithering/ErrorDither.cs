using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing.Dithering;

/// <summary>
/// Error-diffusion dithering: pixels are processed in scan-line order and the difference between each pixel and
/// its chosen palette colour is pushed onto the not-yet-processed neighbours according to a kernel. The kernel
/// is given as integer weights with the current pixel in row 0 at <c>originColumn</c>; weights to the right of
/// it on row 0 and all weights on the following rows receive the error (Floyd–Steinberg, Jarvis–Judice–Ninke,
/// Stucki and friends all fit this shape).
/// </summary>
public sealed class ErrorDither : IDither
{
    private readonly int[] tapDx;
    private readonly int[] tapDy;
    private readonly float[] tapWeight;
    private readonly int rows;
    private readonly int leftReach;
    private readonly int rightReach;

    /// <summary>Creates an error-diffusion dither from an integer kernel.</summary>
    /// <param name="kernel">Weights, row-major; row 0 holds the current pixel at <paramref name="originColumn"/>.</param>
    /// <param name="originColumn">The column of the current pixel in row 0.</param>
    /// <param name="divisor">The value the weights are divided by; usually their sum (Atkinson intentionally uses a larger one).</param>
    public ErrorDither(int[,] kernel, int originColumn, int divisor)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        this.rows = kernel.GetLength(0);
        int columns = kernel.GetLength(1);
        if (this.rows == 0 || columns == 0)
        {
            throw new ArgumentException("The kernel must have at least one row and one column.", nameof(kernel));
        }

        if ((uint)originColumn >= (uint)columns)
        {
            throw new ArgumentOutOfRangeException(nameof(originColumn), originColumn, "The origin column must lie inside the kernel.");
        }

        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "The divisor must be positive.");
        }

        var dx = new List<int>();
        var dy = new List<int>();
        var weight = new List<float>();
        for (int y = 0; y < this.rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int w = kernel[y, x];
                if (w < 0)
                {
                    throw new ArgumentException("Kernel weights must not be negative.", nameof(kernel));
                }

                if (w == 0)
                {
                    continue;
                }

                if (y == 0 && x <= originColumn)
                {
                    throw new ArgumentException(
                        "Row 0 of the kernel may only carry weights to the right of the origin; earlier pixels are already final.", nameof(kernel));
                }

                dx.Add(x - originColumn);
                dy.Add(y);
                weight.Add((float)w / divisor);
            }
        }

        this.tapDx = dx.ToArray();
        this.tapDy = dy.ToArray();
        this.tapWeight = weight.ToArray();
        this.leftReach = originColumn;
        this.rightReach = columns - 1 - originColumn;
    }

    /// <summary>
    /// When true, alternate rows are processed right to left, which breaks up the diagonal "worm" artefacts of
    /// one-directional diffusion. Defaults to false.
    /// </summary>
    public bool Serpentine { get; init; }

    /// <summary>Returns a copy of this dither with <see cref="Serpentine"/> set as requested.</summary>
    public ErrorDither WithSerpentine(bool serpentine)
        => new(this.tapDx, this.tapDy, this.tapWeight, this.rows, this.leftReach, this.rightReach) { Serpentine = serpentine };

    private ErrorDither(int[] tapDx, int[] tapDy, float[] tapWeight, int rows, int leftReach, int rightReach)
    {
        this.tapDx = tapDx;
        this.tapDy = tapDy;
        this.tapWeight = tapWeight;
        this.rows = rows;
        this.leftReach = leftReach;
        this.rightReach = rightReach;
    }

    public void Apply<TPixel>(
        ImageFrame<TPixel> frame, Rectangle bounds, IPaletteMap paletteMap, float scale, Memory<byte> indices, bool replacePixels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(paletteMap);
        DitherHelpers.ValidateRegion(frame, bounds, indices);
        scale = Math.Clamp(scale, 0f, 1f);

        int width = bounds.Width;
        int height = bounds.Height;
        int pad = Math.Max(this.leftReach, this.rightReach);
        int stride = width + (2 * pad);
        int bufferRows = this.rows;

        // Circular buffer of accumulated RGBA error (one Vector4 per pixel slot) for the current row and the rows
        // the kernel reaches below it, padded on both sides so taps never need bounds checks.
        var error = new Vector4[bufferRows * stride];
        var source = new Rgba32[width];
        int[] tapDx = this.tapDx;
        int[] tapDy = this.tapDy;
        float[] tapWeight = this.tapWeight;
        var tapOffset = new int[tapDx.Length]; // Per row: slot offset of each tap's target row plus its column shift.

        for (int y = 0; y < height; y++)
        {
            Span<TPixel> pixels = frame.GetRowSpan(bounds.Y + y).Slice(bounds.X, width);
            PixelOps.ToRgba32<TPixel>(pixels, source);
            Span<byte> indexRow = indices.IsEmpty ? Span<byte>.Empty : indices.Span.Slice(y * width, width);

            int currentBuffer = y % bufferRows;
            int currentBase = (currentBuffer * stride) + pad;
            bool reverse = this.Serpentine && (y & 1) == 1;
            for (int t = 0; t < tapDx.Length; t++)
            {
                int rowBase = ((currentBuffer + tapDy[t]) % bufferRows) * stride;
                int dx = reverse ? -tapDx[t] : tapDx[t];
                tapOffset[t] = rowBase + pad + dx;
            }

            ref Vector4 errorRef = ref MemoryMarshal.GetArrayDataReference(error);
            for (int step = 0; step < width; step++)
            {
                int x = reverse ? width - 1 - step : step;
                Vector4 accumulated = Unsafe.Add(ref errorRef, currentBase + x);
                Rgba32 p = source[x];
                byte r = DitherHelpers.ClampToByte(p.R + accumulated.X);
                byte g = DitherHelpers.ClampToByte(p.G + accumulated.Y);
                byte b = DitherHelpers.ClampToByte(p.B + accumulated.Z);
                byte a = DitherHelpers.ClampToByte(p.A + accumulated.W);

                int index = paletteMap.GetPaletteIndex(new Rgba32(r, g, b, a), out Rgba32 match);
                if (!indexRow.IsEmpty)
                {
                    indexRow[x] = (byte)index;
                }

                if (replacePixels)
                {
                    pixels[x] = TPixel.FromRgba32(match);
                }

                if (match.A == 0)
                {
                    continue; // A transparent pixel has no colour error to spread.
                }

                var difference = new Vector4(r - match.R, g - match.G, b - match.B, a - match.A) * scale;
                for (int t = 0; t < tapOffset.Length; t++)
                {
                    ref Vector4 slot = ref Unsafe.Add(ref errorRef, tapOffset[t] + x);
                    slot += difference * tapWeight[t];
                }
            }

            // This buffer row is finished; it next represents row y + bufferRows.
            Array.Clear(error, currentBuffer * stride, stride);
        }
    }
}
