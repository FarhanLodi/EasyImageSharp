using System.Runtime.CompilerServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Dithering;

/// <summary>Small helpers shared by the dither implementations.</summary>
internal static class DitherHelpers
{
    public static void ValidateRegion<TPixel>(ImageFrame<TPixel> frame, Rectangle bounds, Memory<byte> indices)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || bounds.Right > frame.Width || bounds.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds), bounds, $"The region must lie inside the {frame.Width}x{frame.Height} frame.");
        }

        if (!indices.IsEmpty && indices.Length < bounds.Width * bounds.Height)
        {
            throw new ArgumentException(
                $"The index buffer holds {indices.Length} bytes but the region needs {bounds.Width * bounds.Height}.", nameof(indices));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClampToByte(float value)
        => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(value + 0.5f);

    /// <summary>
    /// The typical spacing between palette colours: the median over entries of the largest per-channel
    /// difference to their nearest other entry. Ordered dithers use it as the threshold amplitude so a two-colour
    /// palette dithers across the full range while a dense palette receives only subtle offsets.
    /// </summary>
    public static float EstimatePaletteSpacing(ReadOnlySpan<Rgba32> palette)
    {
        Span<int> nearest = palette.Length <= 256 ? stackalloc int[256] : new int[palette.Length];
        int count = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            Rgba32 a = palette[i];
            if (a.A == 0)
            {
                continue;
            }

            int best = int.MaxValue;
            for (int j = 0; j < palette.Length; j++)
            {
                if (i == j || palette[j].A == 0)
                {
                    continue;
                }

                Rgba32 b = palette[j];
                int d = Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
                if (d < best)
                {
                    best = d;
                }
            }

            if (best != int.MaxValue)
            {
                nearest[count++] = best;
            }
        }

        if (count == 0)
        {
            return 0f;
        }

        Span<int> distances = nearest[..count];
        distances.Sort();
        return Math.Clamp(distances[count / 2], 0, 255);
    }
}
