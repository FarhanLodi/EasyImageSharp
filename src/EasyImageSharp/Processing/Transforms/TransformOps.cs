using System.Numerics;
using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// The warp engine behind affine and projective transforms: every destination pixel centre is mapped back into
/// the source through the inverse matrix and resampled with a separable 2-D kernel window (radius from the
/// <see cref="IResampler"/>, taps clamped to the source, weights normalised over the clamped window). Destination
/// pixels only partly covered by the source are blended with the fill colour by their geometric coverage
/// (estimated from a 4x4 sub-sample grid), so edges are anti-aliased without fading fully covered border pixels.
/// Source colours are premultiplied by alpha while filtering when the pixel format has an alpha channel. Rows are
/// processed in parallel.
/// </summary>
internal static class TransformOps
{
    /// <summary>Weight sums smaller than this are treated as an empty window and fall back to point sampling.</summary>
    private const float MinimumWeightSum = 1e-6f;

    // ----- Public entry points (source -> destination matrices in source-frame coordinates) -----

    /// <summary>
    /// Transforms <paramref name="sourceRectangle"/> of <paramref name="source"/> by <paramref name="matrix"/> (which
    /// maps source-frame coordinates to destination coordinates) onto a new canvas of <paramref name="targetSize"/>.
    /// </summary>
    public static ImageFrame<TPixel> TransformAffine<TPixel>(
        ImageFrame<TPixel> source, Rectangle sourceRectangle, Matrix3x2 matrix, Size targetSize, IResampler sampler, Color fill)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> region = SelectRegion(source, sourceRectangle, targetSize, sampler);
        Matrix3x2 local = Matrix3x2.CreateTranslation(sourceRectangle.X, sourceRectangle.Y) * matrix;
        if (!Matrix3x2.Invert(local, out Matrix3x2 inverse))
        {
            throw new ArgumentException("The transform matrix is not invertible.", nameof(matrix));
        }

        return WarpAffine(region, inverse, targetSize, sampler, fill);
    }

    /// <summary>
    /// Transforms <paramref name="sourceRectangle"/> of <paramref name="source"/> by the projective
    /// <paramref name="matrix"/> (which maps source-frame coordinates to destination coordinates, see
    /// <see cref="ProjectiveTransformBuilder"/> for the layout) onto a new canvas of <paramref name="targetSize"/>.
    /// </summary>
    public static ImageFrame<TPixel> TransformProjective<TPixel>(
        ImageFrame<TPixel> source, Rectangle sourceRectangle, Matrix4x4 matrix, Size targetSize, IResampler sampler, Color fill)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> region = SelectRegion(source, sourceRectangle, targetSize, sampler);
        Matrix4x4 local = Matrix4x4.CreateTranslation(sourceRectangle.X, sourceRectangle.Y, 0f) * matrix;
        if (!Matrix4x4.Invert(local, out Matrix4x4 inverse))
        {
            throw new ArgumentException("The transform matrix is not invertible.", nameof(matrix));
        }

        return WarpProjective(region, inverse, targetSize, sampler, fill);
    }

    // ----- Low-level warps (destination -> source inverse matrices) -----

    /// <summary>
    /// Warps <paramref name="source"/> onto a <paramref name="destination"/>-sized canvas. <paramref name="inverse"/>
    /// maps destination coordinates back to source coordinates (pixel centres at half-integers).
    /// </summary>
    public static ImageFrame<TPixel> WarpAffine<TPixel>(
        ImageFrame<TPixel> source, Matrix3x2 inverse, Size destination, IResampler sampler, Color fill)
        where TPixel : unmanaged, IPixel<TPixel>
        => Warp(source, new AffineMap(inverse), destination, sampler, fill);

    /// <summary>
    /// Warps <paramref name="source"/> onto a <paramref name="destination"/>-sized canvas. <paramref name="inverse"/>
    /// maps destination coordinates back to source coordinates through a perspective divide (see
    /// <see cref="ProjectiveTransformBuilder"/> for the matrix layout); destination pixels whose homogeneous weight is
    /// not positive receive the fill colour.
    /// </summary>
    public static ImageFrame<TPixel> WarpProjective<TPixel>(
        ImageFrame<TPixel> source, Matrix4x4 inverse, Size destination, IResampler sampler, Color fill)
        where TPixel : unmanaged, IPixel<TPixel>
        => Warp(source, new ProjectiveMap(inverse), destination, sampler, fill);

    // ----- Implementation -----

    private static ImageFrame<TPixel> SelectRegion<TPixel>(ImageFrame<TPixel> source, Rectangle sourceRectangle, Size targetSize, IResampler sampler)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(sampler);
        Guard.MustBePositive(targetSize.Width, nameof(targetSize));
        Guard.MustBePositive(targetSize.Height, nameof(targetSize));
        var full = new Rectangle(0, 0, source.Width, source.Height);
        return sourceRectangle == full ? source : FrameOps.Crop(source, sourceRectangle);
    }


    private static ImageFrame<TPixel> Warp<TPixel, TMap>(ImageFrame<TPixel> source, TMap map, Size destination, IResampler sampler, Color fill)
        where TPixel : unmanaged, IPixel<TPixel>
        where TMap : struct, IInverseMap
    {
        ArgumentNullException.ThrowIfNull(sampler);
        Guard.MustBePositive(destination.Width, nameof(destination));
        Guard.MustBePositive(destination.Height, nameof(destination));

        int srcWidth = source.Width;
        int srcHeight = source.Height;
        int destWidth = destination.Width;
        int destHeight = destination.Height;
        bool premultiply = PixelFormatInfo<TPixel>.HasAlpha;
        Rgba32[] src = ToRgba32Array(source);
        Rgba32 fillPixel = fill.ToRgba32();
        Vector4 fillVector = Load(fillPixel, premultiply);
        bool nearest = sampler is NearestNeighborResampler;
        float radius = sampler.Radius;
        if (!(radius > 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentException("The resampler radius must be a positive finite number.", nameof(sampler));
        }

        int maxTaps = (2 * (int)MathF.Ceiling(radius)) + 2;
        var dest = new ImageFrame<TPixel>(destWidth, destHeight);

        ParallelRowIterator.IterateRows(destWidth, destHeight, (startRow, endRow) =>
        {
            var row = new Rgba32[destWidth];
            var window = new KernelWindow(maxTaps);
            for (int y = startRow; y < endRow; y++)
            {
                float dy = y + 0.5f;
                for (int x = 0; x < destWidth; x++)
                {
                    if (!map.TryMap(x + 0.5f, dy, out float sx, out float sy))
                    {
                        row[x] = fillPixel;
                        continue;
                    }

                    if (nearest)
                    {
                        row[x] = PointSample(src, srcWidth, srcHeight, sx, sy, fillPixel);
                        continue;
                    }

                    if (!window.Prepare(sx, sy, srcWidth, srcHeight, sampler, radius))
                    {
                        row[x] = fillPixel;
                        continue;
                    }

                    // Geometric coverage of the destination pixel by the source rectangle: 1 when the kernel window
                    // is wholly inside, otherwise estimated from a 4x4 grid of sub-samples so edges are anti-aliased
                    // against the fill without fading pixels that are fully covered (e.g. axis-aligned edges).
                    float coverage = window.TouchesBorder ? Coverage(map, x, y, srcWidth, srcHeight) : 1f;
                    if (coverage <= 0f)
                    {
                        row[x] = fillPixel;
                        continue;
                    }

                    Vector4 colour = window.Sample(src, srcWidth, premultiply);
                    if (coverage < 1f)
                    {
                        colour = (colour * coverage) + (fillVector * (1f - coverage));
                    }

                    row[x] = FrameOps.StorePixel(colour, premultiply, compand: false);
                }

                PixelOps.FromRgba32<TPixel>(row, dest.GetRowSpan(y));
            }
        });

        return dest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba32 PointSample(Rgba32[] src, int width, int height, float sx, float sy, Rgba32 fill)
    {
        int ix = (int)MathF.Floor(sx);
        int iy = (int)MathF.Floor(sy);
        return (uint)ix < (uint)width && (uint)iy < (uint)height ? src[(iy * width) + ix] : fill;
    }

    /// <summary>Fraction of a destination pixel's area whose pre-image lies inside the source, from a 4x4 sub-sample grid.</summary>
    private static float Coverage<TMap>(TMap map, int x, int y, int width, int height)
        where TMap : struct, IInverseMap
    {
        const int Grid = 4;
        int inside = 0;
        for (int j = 0; j < Grid; j++)
        {
            float py = y + ((j + 0.5f) / Grid);
            for (int i = 0; i < Grid; i++)
            {
                float px = x + ((i + 0.5f) / Grid);
                if (map.TryMap(px, py, out float sx, out float sy) && sx >= 0f && sy >= 0f && sx < width && sy < height)
                {
                    inside++;
                }
            }
        }

        return inside / (float)(Grid * Grid);
    }

    /// <summary>
    /// A separable kernel window around one source position: taps clamped to the source, weights normalised over
    /// the clamped taps. Reused per row batch to avoid allocations.
    /// </summary>
    private sealed class KernelWindow
    {
        private readonly float[] weightsX;
        private readonly float[] weightsY;
        private int left;
        private int top;
        private int countX;
        private int countY;
        private float sumX;
        private float sumY;
        private float sourceX;
        private float sourceY;

        public KernelWindow(int maxTaps)
        {
            this.weightsX = new float[maxTaps];
            this.weightsY = new float[maxTaps];
        }

        /// <summary>Whether the unclamped window extends past the source (the pixel may be partially covered).</summary>
        public bool TouchesBorder { get; private set; }

        /// <summary>Computes the window for a source position; returns <see langword="false"/> when it lies entirely outside the source.</summary>
        public bool Prepare(float sx, float sy, int width, int height, IResampler sampler, float radius)
        {
            // Kernel positions are measured from pixel centres, so work in index space where pixel i sits at i.
            float cx = sx - 0.5f;
            float cy = sy - 0.5f;
            int l = (int)MathF.Ceiling(cx - radius);
            int r = (int)MathF.Floor(cx + radius);
            int t = (int)MathF.Ceiling(cy - radius);
            int b = (int)MathF.Floor(cy + radius);
            if (r < l)
            {
                r = l;
            }

            if (b < t)
            {
                b = t;
            }

            if (r < 0 || l >= width || b < 0 || t >= height)
            {
                return false;
            }

            this.TouchesBorder = l < 0 || t < 0 || r >= width || b >= height;
            this.sourceX = sx;
            this.sourceY = sy;

            // Clamp to the source and drop taps outside it; the remaining weights are normalised in Sample.
            int cl = Math.Max(l, 0);
            int cr = Math.Min(r, width - 1);
            int ct = Math.Max(t, 0);
            int cb = Math.Min(b, height - 1);
            this.left = cl;
            this.top = ct;
            this.countX = Math.Min(cr - cl + 1, this.weightsX.Length);
            this.countY = Math.Min(cb - ct + 1, this.weightsY.Length);

            float sx0 = 0f;
            for (int i = 0; i < this.countX; i++)
            {
                float w = sampler.GetValue(cl + i - cx);
                this.weightsX[i] = w;
                sx0 += w;
            }

            float sy0 = 0f;
            for (int j = 0; j < this.countY; j++)
            {
                float w = sampler.GetValue(ct + j - cy);
                this.weightsY[j] = w;
                sy0 += w;
            }

            this.sumX = sx0;
            this.sumY = sy0;
            return true;
        }

        /// <summary>The weighted (premultiplied when requested) colour of the prepared window.</summary>
        public Vector4 Sample(Rgba32[] src, int width, bool premultiply)
        {
            float total = this.sumX * this.sumY;
            if (MathF.Abs(total) < MinimumWeightSum)
            {
                // Degenerate window (all weights cancel): fall back to the nearest source pixel.
                int ix = Math.Clamp((int)MathF.Floor(this.sourceX), this.left, this.left + this.countX - 1);
                int iy = Math.Clamp((int)MathF.Floor(this.sourceY), this.top, this.top + this.countY - 1);
                return Load(src[(iy * width) + ix], premultiply);
            }

            Vector4 accumulator = Vector4.Zero;
            for (int j = 0; j < this.countY; j++)
            {
                float wy = this.weightsY[j];
                if (wy == 0f)
                {
                    continue;
                }

                int rowOffset = ((this.top + j) * width) + this.left;
                Vector4 rowSum = Vector4.Zero;
                for (int i = 0; i < this.countX; i++)
                {
                    float wx = this.weightsX[i];
                    if (wx != 0f)
                    {
                        rowSum += Load(src[rowOffset + i], premultiply) * wx;
                    }
                }

                accumulator += rowSum * wy;
            }

            return accumulator * (1f / total);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Load(Rgba32 p, bool premultiply)
    {
        if (premultiply)
        {
            float a = p.A * (1f / 255f);
            return new Vector4(p.R * a, p.G * a, p.B * a, p.A);
        }

        return new Vector4(p.R, p.G, p.B, p.A);
    }

    private static Rgba32[] ToRgba32Array<TPixel>(ImageFrame<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            return (Rgba32[])(object)source.PixelArray;
        }

        var result = new Rgba32[source.Width * source.Height];
        PixelOps.ToRgba32<TPixel>(source.PixelSpan, result);
        return result;
    }

    /// <summary>Maps a destination coordinate back into the source; returns <see langword="false"/> for points with no pre-image.</summary>
    private interface IInverseMap
    {
        bool TryMap(float x, float y, out float sourceX, out float sourceY);
    }

    private readonly struct AffineMap : IInverseMap
    {
        private readonly Matrix3x2 inverse;

        public AffineMap(Matrix3x2 inverse) => this.inverse = inverse;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryMap(float x, float y, out float sourceX, out float sourceY)
        {
            sourceX = (x * this.inverse.M11) + (y * this.inverse.M21) + this.inverse.M31;
            sourceY = (x * this.inverse.M12) + (y * this.inverse.M22) + this.inverse.M32;
            return true;
        }
    }

    private readonly struct ProjectiveMap : IInverseMap
    {
        private readonly Matrix4x4 inverse;

        public ProjectiveMap(Matrix4x4 inverse) => this.inverse = inverse;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryMap(float x, float y, out float sourceX, out float sourceY)
        {
            float w = (x * this.inverse.M14) + (y * this.inverse.M24) + this.inverse.M44;
            if (!(w > 1e-6f))
            {
                sourceX = 0f;
                sourceY = 0f;
                return false;
            }

            float invW = 1f / w;
            sourceX = ((x * this.inverse.M11) + (y * this.inverse.M21) + this.inverse.M41) * invW;
            sourceY = ((x * this.inverse.M12) + (y * this.inverse.M22) + this.inverse.M42) * invW;
            return true;
        }
    }
}
