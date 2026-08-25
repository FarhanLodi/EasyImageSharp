using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>
/// A per-pixel transform. Implementations are structs and are always reached through a generic type
/// parameter constrained to <c>struct</c>, so the JIT specialises the loop and inlines <see cref="Apply"/>
/// instead of emitting a delegate call per pixel.
/// </summary>
internal interface IPixelOperation
{
    /// <summary>Maps one pixel.</summary>
    Rgba32 Apply(Rgba32 source);
}

/// <summary>
/// Runs <see cref="IPixelOperation"/> implementations over a frame, row-parallel and without per-pixel
/// indirection.
/// <para>
/// Pixel formats that occupy a single byte (<see cref="L8"/>) take a 256-entry lookup table built by
/// running the operation on every value the format can hold, which is exact by construction and reduces the
/// operation to one table lookup per pixel. Operations that act on R, G and B independently additionally
/// get <see cref="ApplyChannelLut{TPixel}"/>, which rewrites the channel bytes in place.
/// </para>
/// </summary>
internal static class PixelRunner
{
    /// <summary>Applies <paramref name="operation"/> to every pixel of <paramref name="frame"/>.</summary>
    public static void ApplyPixels<TPixel, TOperation>(ImageFrame<TPixel> frame, TOperation operation)
        where TPixel : unmanaged, IPixel<TPixel>
        where TOperation : struct, IPixelOperation
    {
        if (Unsafe.SizeOf<TPixel>() == 1)
        {
            ApplyByteLut(frame, BuildPixelLut<TPixel, TOperation>(operation));
            return;
        }

        int width = frame.Width;
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> row = frame.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = TPixel.FromRgba32(operation.Apply(row[x].ToRgba32()));
                }
            }
        });
    }

    /// <summary>
    /// Applies a 256-entry channel table to R, G and B, leaving alpha untouched. Equivalent to
    /// <see cref="ApplyPixels{TPixel, TOperation}"/> with an operation that maps each colour channel
    /// through <paramref name="lut"/>.
    /// </summary>
    public static void ApplyChannelLut<TPixel>(ImageFrame<TPixel> frame, byte[] lut)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (Unsafe.SizeOf<TPixel>() == 1)
        {
            ApplyByteLut(frame, BuildPixelLut<TPixel, ChannelLutOperation>(new ChannelLutOperation(lut)));
            return;
        }

        if (!PixelOps.TryGetChannelStride<TPixel>(out int bytesPerPixel, out bool hasAlpha))
        {
            ApplyPixels(frame, new ChannelLutOperation(lut));
            return;
        }

        int width = frame.Width;
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(frame.GetRowSpan(y));
                if (!hasAlpha)
                {
                    // Every byte of an opaque interleaved format is a colour channel.
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i] = lut[row[i]];
                    }
                }
                else
                {
                    // Alpha always sits in the last byte of the four, whatever the channel order.
                    for (int i = 0; i + bytesPerPixel <= row.Length; i += bytesPerPixel)
                    {
                        row[i] = lut[row[i]];
                        row[i + 1] = lut[row[i + 1]];
                        row[i + 2] = lut[row[i + 2]];
                    }
                }
            }
        });
    }

    /// <summary>Replaces R, G and B with the BT.709 luminance of the pixel, keeping alpha.</summary>
    public static void Grayscale<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (Unsafe.SizeOf<TPixel>() == 1)
        {
            ApplyByteLut(frame, BuildPixelLut<TPixel, GrayscaleOperation>(default));
            return;
        }

        int width = frame.Width;
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
            PixelOps.Grayscale(frame.PixelSpan.Slice(start * width, (end - start) * width)));
    }

    /// <summary>Maps every pixel of a one-byte format through a table indexed by its packed value.</summary>
    private static void ApplyByteLut<TPixel>(ImageFrame<TPixel> frame, TPixel[] lut)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var table = new byte[256];
        MemoryMarshal.AsBytes(lut.AsSpan()).CopyTo(table);
        int width = frame.Width;
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(frame.GetRowSpan(y));
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = table[row[i]];
                }
            }
        });
    }

    /// <summary>Runs the operation over all 256 values a one-byte pixel format can hold.</summary>
    private static TPixel[] BuildPixelLut<TPixel, TOperation>(TOperation operation)
        where TPixel : unmanaged, IPixel<TPixel>
        where TOperation : struct, IPixelOperation
    {
        var inputs = new TPixel[256];
        Span<byte> raw = MemoryMarshal.AsBytes(inputs.AsSpan());
        for (int v = 0; v < 256; v++)
        {
            raw[v] = (byte)v;
        }

        var lut = new TPixel[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = TPixel.FromRgba32(operation.Apply(inputs[v].ToRgba32()));
        }

        return lut;
    }

    /// <summary>Maps R, G and B through a table, passing alpha through.</summary>
    private readonly struct ChannelLutOperation : IPixelOperation
    {
        private readonly byte[] lut;

        public ChannelLutOperation(byte[] lut) => this.lut = lut;

        public Rgba32 Apply(Rgba32 source)
            => new(this.lut[source.R], this.lut[source.G], this.lut[source.B], source.A);
    }

    /// <summary>Replaces the colour channels with the pixel's luminance.</summary>
    private readonly struct GrayscaleOperation : IPixelOperation
    {
        public Rgba32 Apply(Rgba32 source)
        {
            byte l = PixelOps.Luminance8(source);
            return new Rgba32(l, l, l, source.A);
        }
    }
}
