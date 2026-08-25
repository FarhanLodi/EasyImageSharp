using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Four-point perspective correction: a 3x3 homography is solved from four point correspondences (8x8 linear
/// system by Gaussian elimination with partial pivoting) and the destination is filled by inverse mapping
/// every output pixel into the source with bilinear sampling, row-parallel.
/// </summary>
internal static class PerspectiveWarp
{
    /// <summary>
    /// Solves the homography H (row-major, h33 = 1) with H * (x, y, 1) ~ (u, v, 1) for the four pairs
    /// (source[i] -> target[i]).
    /// </summary>
    public static double[] SolveHomography(ReadOnlySpan<PointF> source, ReadOnlySpan<PointF> target)
    {
        if (source.Length != 4 || target.Length != 4)
        {
            throw new ArgumentException("Exactly four point pairs are required.");
        }

        // Unknowns h11..h32 (h33 = 1). Each pair gives two equations:
        //   x h11 + y h12 + h13 - u x h31 - u y h32 = u
        //   x h21 + y h22 + h23 - v x h31 - v y h32 = v
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = source[i].X;
            double y = source[i].Y;
            double u = target[i].X;
            double v = target[i].Y;
            int r0 = 2 * i;
            int r1 = r0 + 1;
            a[r0, 0] = x; a[r0, 1] = y; a[r0, 2] = 1; a[r0, 6] = -u * x; a[r0, 7] = -u * y; a[r0, 8] = u;
            a[r1, 3] = x; a[r1, 4] = y; a[r1, 5] = 1; a[r1, 6] = -v * x; a[r1, 7] = -v * y; a[r1, 8] = v;
        }

        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                throw new ArgumentException("The four points are degenerate (collinear or repeated); no homography exists.");
            }

            if (pivot != col)
            {
                for (int c = 0; c < 9; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }
            }

            for (int r = 0; r < 8; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double factor = a[r, col] / a[col, col];
                if (factor == 0)
                {
                    continue;
                }

                for (int c = col; c < 9; c++)
                {
                    a[r, c] -= factor * a[col, c];
                }
            }
        }

        var h = new double[9];
        for (int i = 0; i < 8; i++)
        {
            h[i] = a[i, 8] / a[i, i];
        }

        h[8] = 1;
        return h;
    }

    /// <summary>Applies a homography to a point.</summary>
    public static PointF Apply(double[] h, PointF p)
    {
        double w = (h[6] * p.X) + (h[7] * p.Y) + h[8];
        if (Math.Abs(w) < 1e-12)
        {
            w = 1e-12;
        }

        return new PointF(
            (float)(((h[0] * p.X) + (h[1] * p.Y) + h[2]) / w),
            (float)(((h[3] * p.X) + (h[4] * p.Y) + h[5]) / w));
    }

    /// <summary>
    /// Warps the quadrilateral <paramref name="quad"/> (TL, TR, BR, BL) of the frame onto a new
    /// <paramref name="targetWidth"/> x <paramref name="targetHeight"/> frame. Coordinates are continuous pixel
    /// units with the origin at the image's top-left corner (pixel (x, y) spans x..x+1), so the whole image is
    /// the quad (0,0), (W,0), (W,H), (0,H). Sample positions outside the source are clamped to the edge.
    /// </summary>
    public static ImageFrame<TPixel> Warp<TPixel>(ImageFrame<TPixel> source, ReadOnlySpan<PointF> quad, int targetWidth, int targetHeight)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(targetWidth, nameof(targetWidth));
        Guard.MustBePositive(targetHeight, nameof(targetHeight));

        // Homography from the continuous target rectangle to the continuous source quad; each destination
        // pixel centre (dx + 0.5, dy + 0.5) is mapped and sampled bilinearly between source pixel centres.
        ReadOnlySpan<PointF> targetCorners = stackalloc PointF[]
        {
            new PointF(0f, 0f),
            new PointF(targetWidth, 0f),
            new PointF(targetWidth, targetHeight),
            new PointF(0f, targetHeight),
        };
        double[] h = SolveHomography(targetCorners, quad);

        int sw = source.Width;
        int sh = source.Height;
        var src = new Rgba32[sw * sh];
        for (int y = 0; y < sh; y++)
        {
            PixelOps.ToRgba32<TPixel>(source.GetRowSpan(y), src.AsSpan(y * sw, sw));
        }

        var dest = new ImageFrame<TPixel>(targetWidth, targetHeight);
        ParallelRowIterator.IterateRows(targetWidth, targetHeight, (start, end) =>
        {
            var row = new Rgba32[targetWidth];
            for (int dy = start; dy < end; dy++)
            {
                double cy = dy + 0.5;
                for (int dx = 0; dx < targetWidth; dx++)
                {
                    double cx = dx + 0.5;
                    double w = (h[6] * cx) + (h[7] * cy) + h[8];
                    if (Math.Abs(w) < 1e-12)
                    {
                        w = 1e-12;
                    }

                    double sx = (((h[0] * cx) + (h[1] * cy) + h[2]) / w) - 0.5;
                    double sy = (((h[3] * cx) + (h[4] * cy) + h[5]) / w) - 0.5;
                    row[dx] = SampleClamped(src, sw, sh, sx, sy);
                }

                PixelOps.FromRgba32<TPixel>(row, dest.GetRowSpan(dy));
            }
        });

        return dest;
    }

    /// <summary>The natural output size of a quad: the longer of the two horizontal sides by the longer of the two vertical sides.</summary>
    public static Size NaturalSize(ReadOnlySpan<PointF> quad)
    {
        double top = Distance(quad[0], quad[1]);
        double bottom = Distance(quad[3], quad[2]);
        double left = Distance(quad[0], quad[3]);
        double right = Distance(quad[1], quad[2]);
        int width = Math.Max(1, (int)Math.Round(Math.Max(top, bottom)));
        int height = Math.Max(1, (int)Math.Round(Math.Max(left, right)));
        return new Size(width, height);
    }

    private static double Distance(PointF a, PointF b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Rgba32 SampleClamped(Rgba32[] src, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float fx = (float)(x - x0);
        float fy = (float)(y - y0);

        Vector4 p00 = ToVector(src[(y0 * width) + x0]);
        Vector4 p10 = ToVector(src[(y0 * width) + x1]);
        Vector4 p01 = ToVector(src[(y1 * width) + x0]);
        Vector4 p11 = ToVector(src[(y1 * width) + x1]);
        Vector4 v = Vector4.Lerp(Vector4.Lerp(p00, p10, fx), Vector4.Lerp(p01, p11, fx), fy);
        return new Rgba32(
            (byte)Math.Clamp((int)(v.X + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(v.Y + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(v.Z + 0.5f), 0, 255),
            (byte)Math.Clamp((int)(v.W + 0.5f), 0, 255));
    }

    private static Vector4 ToVector(Rgba32 p) => new(p.R, p.G, p.B, p.A);
}
