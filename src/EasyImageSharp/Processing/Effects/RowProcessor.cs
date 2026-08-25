using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Callback that transforms one row of straight RGBA pixels in place.</summary>
/// <param name="row">The pixels of the row inside the processed region.</param>
/// <param name="y">The frame row index.</param>
internal delegate void RgbaRowAction(Span<Rgba32> row, int y);

/// <summary>
/// Shared plumbing for per-row effects: converts frame rows to <see cref="Rgba32"/> (in place for
/// <see cref="Rgba32"/> frames, through a thread-local scratch buffer otherwise), runs a callback and writes
/// the result back, splitting the rows across threads via <see cref="ParallelRowIterator"/>.
/// </summary>
internal static class RowProcessor
{
    /// <summary>Clamps <paramref name="rectangle"/> to the frame; returns an empty rectangle when they do not overlap.</summary>
    public static Rectangle ClampToFrame<TPixel>(ImageFrame<TPixel> frame, Rectangle rectangle)
        where TPixel : unmanaged, IPixel<TPixel>
        => Rectangle.Intersect(rectangle, new Rectangle(0, 0, frame.Width, frame.Height));

    /// <summary>The whole frame as a rectangle.</summary>
    public static Rectangle Bounds<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
        => new(0, 0, frame.Width, frame.Height);

    /// <summary>Applies <paramref name="body"/> to every row of <paramref name="region"/> (already clamped to the frame).</summary>
    public static void ProcessRows<TPixel>(ImageFrame<TPixel> frame, Rectangle region, RgbaRowAction body)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        if (typeof(TPixel) == typeof(Rgba32))
        {
            ParallelRowIterator.IterateRows(region.Width, region.Height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<Rgba32> row = MemoryMarshal.Cast<TPixel, Rgba32>(frame.GetRowSpan(region.Y + y).Slice(region.X, region.Width));
                    body(row, region.Y + y);
                }
            });
            return;
        }

        ParallelRowIterator.IterateRows(region.Width, region.Height, (start, end) =>
        {
            var scratch = new Rgba32[region.Width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> source = frame.GetRowSpan(region.Y + y).Slice(region.X, region.Width);
                PixelOps.ToRgba32<TPixel>(source, scratch);
                body(scratch, region.Y + y);
                PixelOps.FromRgba32<TPixel>(scratch, source);
            }
        });
    }

    /// <summary>Applies a per-pixel transform to every pixel of <paramref name="region"/>.</summary>
    public static void ProcessPixels<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Func<Rgba32, Rgba32> transform)
        where TPixel : unmanaged, IPixel<TPixel>
        => ProcessRows(frame, region, (row, _) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = transform(row[x]);
            }
        });

    /// <summary>Copies a region of the frame into a straight-RGBA <see cref="Rgba32"/> buffer (row-major, region-sized).</summary>
    public static Rgba32[] ReadRegion<TPixel>(ImageFrame<TPixel> frame, Rectangle region)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var buffer = new Rgba32[region.Width * region.Height];
        for (int y = 0; y < region.Height; y++)
        {
            PixelOps.ToRgba32<TPixel>(
                frame.GetRowSpan(region.Y + y).Slice(region.X, region.Width),
                buffer.AsSpan(y * region.Width, region.Width));
        }

        return buffer;
    }

    /// <summary>Writes a region-sized <see cref="Rgba32"/> buffer back into the frame.</summary>
    public static void WriteRegion<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Rgba32[] buffer)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int y = 0; y < region.Height; y++)
        {
            PixelOps.FromRgba32<TPixel>(
                buffer.AsSpan(y * region.Width, region.Width),
                frame.GetRowSpan(region.Y + y).Slice(region.X, region.Width));
        }
    }

    /// <summary>Converts a byte channel value to the 0-1 float range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ToUnitVector(Rgba32 p) => new Vector4(p.R, p.G, p.B, p.A) * (1f / 255f);

    /// <summary>Converts a 0-1 float colour back to bytes with clamping and round-half-up.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 FromUnitVector(Vector4 v)
    {
        v = Vector4.Clamp(v, Vector4.Zero, Vector4.One) * 255f;
        return new Rgba32((byte)(v.X + 0.5f), (byte)(v.Y + 0.5f), (byte)(v.Z + 0.5f), (byte)(v.W + 0.5f));
    }

    /// <summary>Rounds a 0-255 float channel value to a byte with clamping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClampToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
