using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Draws one image onto a frame of another using <see cref="PixelBlender"/>.</summary>
internal static class CompositingOps
{
    /// <summary>
    /// Composites <paramref name="sourceRectangle"/> of <paramref name="source"/> (its root frame) onto
    /// <paramref name="destination"/> with its top-left corner at <paramref name="location"/>. Parts falling
    /// outside either image are clipped.
    /// </summary>
    public static void DrawImage<TPixel>(
        ImageFrame<TPixel> destination,
        Image source,
        Point location,
        Rectangle sourceRectangle,
        PixelColorBlendingMode colorBlending,
        PixelAlphaCompositionMode alphaComposition,
        float opacity)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Rectangle sourceBounds = Rectangle.Intersect(sourceRectangle, new Rectangle(0, 0, source.Width, source.Height));
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
        {
            return;
        }

        // Destination area covered by the source rectangle, clipped to the destination frame.
        Rectangle target = Rectangle.Intersect(
            new Rectangle(location.X, location.Y, sourceBounds.Width, sourceBounds.Height),
            new Rectangle(0, 0, destination.Width, destination.Height));
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        int sourceOffsetX = sourceBounds.X + (target.X - location.X);
        int sourceOffsetY = sourceBounds.Y + (target.Y - location.Y);
        opacity = Math.Clamp(opacity, 0f, 1f);
        SourceReader reader = SourceReader.Create(source);

        ParallelRowIterator.IterateRows(target.Width, target.Height, (start, end) =>
        {
            var sourceRow = new Rgba32[target.Width];
            var destRow = new Rgba32[target.Width];
            for (int y = start; y < end; y++)
            {
                reader.ReadRow(sourceOffsetY + y, sourceOffsetX, sourceRow);
                Span<TPixel> destSpan = destination.GetRowSpan(target.Y + y).Slice(target.X, target.Width);
                PixelOps.ToRgba32<TPixel>(destSpan, destRow);
                PixelBlender.Blend(destRow, destRow, sourceRow, opacity, colorBlending, alphaComposition);
                PixelOps.FromRgba32<TPixel>(destRow, destSpan);
            }
        });
    }

    /// <summary>Reads rows of an <see cref="Image"/> of unknown pixel type as <see cref="Rgba32"/>, with fast paths for the built-in formats.</summary>
    private readonly struct SourceReader
    {
        private readonly Image image;
        private readonly Action<int, int, Rgba32[]>? fastRead;

        private SourceReader(Image image, Action<int, int, Rgba32[]>? fastRead)
        {
            this.image = image;
            this.fastRead = fastRead;
        }

        public static SourceReader Create(Image image) => image switch
        {
            Image<Rgba32> typed => new SourceReader(image, (y, x, dest) => Read(typed, y, x, dest)),
            Image<Rgb24> typed => new SourceReader(image, (y, x, dest) => Read(typed, y, x, dest)),
            Image<Bgra32> typed => new SourceReader(image, (y, x, dest) => Read(typed, y, x, dest)),
            Image<Bgr24> typed => new SourceReader(image, (y, x, dest) => Read(typed, y, x, dest)),
            Image<L8> typed => new SourceReader(image, (y, x, dest) => Read(typed, y, x, dest)),
            _ => new SourceReader(image, null),
        };

        public void ReadRow(int y, int x, Rgba32[] destination)
        {
            if (this.fastRead is not null)
            {
                this.fastRead(y, x, destination);
                return;
            }

            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = this.image.GetPixelRgba32(x + i, y);
            }
        }

        private static void Read<TPixel>(Image<TPixel> image, int y, int x, Rgba32[] destination)
            where TPixel : unmanaged, IPixel<TPixel>
            => PixelOps.ToRgba32<TPixel>(image.Frames.RootFrame.GetRowSpan(y).Slice(x, destination.Length), destination);
    }
}
