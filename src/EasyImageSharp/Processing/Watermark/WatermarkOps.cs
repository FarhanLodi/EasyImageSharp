using System.Numerics;
using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Layout;
using EasyImageSharp.Fonts.Rendering;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Prepares a watermark stamp and composites it, once or tiled, onto a frame.</summary>
internal static class WatermarkOps
{
    /// <summary>Copies the root frame of an image of any pixel type into a straight-alpha <see cref="Rgba32"/> frame.</summary>
    public static ImageFrame<Rgba32> ReadStamp(Image image)
    {
        return image switch
        {
            Image<Rgba32> typed => Copy(typed),
            Image<Rgb24> typed => Copy(typed),
            Image<Bgra32> typed => Copy(typed),
            Image<Bgr24> typed => Copy(typed),
            Image<Argb32> typed => Copy(typed),
            Image<Abgr32> typed => Copy(typed),
            Image<L8> typed => Copy(typed),
            Image<La16> typed => Copy(typed),
            Image<A8> typed => Copy(typed),
            Image<Rgb48> typed => Copy(typed),
            Image<Rgba64> typed => Copy(typed),
            Image<L16> typed => Copy(typed),
            Image<La32> typed => Copy(typed),
            Image<RgbaVector> typed => Copy(typed),
            _ => CopyAny(image),
        };

        static ImageFrame<Rgba32> Copy<TPixel>(Image<TPixel> source)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            source.EnsureNotDisposed();
            var frame = new ImageFrame<Rgba32>(source.Width, source.Height);
            for (int y = 0; y < source.Height; y++)
            {
                PixelOps.ToRgba32<TPixel>(source.Frames.RootFrame.GetRowSpan(y), frame.GetRowSpan(y));
            }

            return frame;
        }

        static ImageFrame<Rgba32> CopyAny(Image source)
        {
            var frame = new ImageFrame<Rgba32>(source.Width, source.Height);
            for (int y = 0; y < source.Height; y++)
            {
                Span<Rgba32> row = frame.GetRowSpan(y);
                for (int x = 0; x < source.Width; x++)
                {
                    row[x] = source.GetPixelRgba32(x, y);
                }
            }

            return frame;
        }
    }

    /// <summary>Renders text into a tightly sized transparent stamp, or returns <see langword="null"/> for empty text.</summary>
    public static ImageFrame<Rgba32>? RenderText(string text, Color color, TextOptions textOptions)
    {
        Size size = textOptions.Measure(text);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var stamp = new ImageFrame<Rgba32>(size.Width, size.Height);
        float anchor = textOptions.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => size.Width / 2,
            HorizontalAlignment.Right => size.Width,
            _ => 0,
        };
        DrawOps.DrawText(stamp, text, color, new PointF(anchor, 0), textOptions, DrawingOptions.Default);
        return stamp;
    }

    /// <summary>
    /// Renders <paramref name="text"/> with a TrueType/OpenType font (default layout: left aligned, kerning on, hard
    /// line breaks honoured) into a transparent stamp trimmed to the text's ink plus one pixel of anti-aliasing
    /// margin. Returns null when the text draws nothing.
    /// </summary>
    public static ImageFrame<Rgba32>? RenderFontText(string text, Font font, Color color)
    {
        const int Margin = 1;
        if (text.Length == 0)
        {
            return null;
        }

        RectangleF ink = TextLayout.Layout(text, font, TextLayoutOptions.Default).ComputeInkBounds();
        if (ink.Width <= 0 || ink.Height <= 0
            || !float.IsFinite(ink.Left) || !float.IsFinite(ink.Top) || !float.IsFinite(ink.Right) || !float.IsFinite(ink.Bottom))
        {
            return null;
        }

        // Shifting the layout by whole pixels leaves every glyph bitmap unchanged (pen positions keep their
        // sub-pixel phase and baselines stay on the same rounding), so the stamp is exactly the text as it would be
        // drawn anywhere else, only moved.
        double left = Math.Floor(ink.Left);
        double top = Math.Floor(ink.Top);
        double width = Math.Ceiling(ink.Right) - left + (2 * Margin);
        double height = Math.Ceiling(ink.Bottom) - top + (2 * Margin);
        if (width * height > int.MaxValue)
        {
            throw new ImageSizeLimitExceededException(
                $"The watermark text renders to {width:F0}x{height:F0} pixels, which is too large for a single stamp; use a smaller font size.");
        }

        var stamp = new ImageFrame<Rgba32>((int)width, (int)height);
        var placed = new TextLayoutOptions { Origin = new PointF((float)(Margin - left), (float)(Margin - top)) };
        TextRenderer.Draw(stamp, TextLayout.Layout(text, font, placed), color, null, DrawingOptions.Default);
        return stamp;
    }

    /// <summary>Scales the stamp relative to the frame size and rotates it, as the options ask.</summary>
    public static ImageFrame<Rgba32> Prepare(ImageFrame<Rgba32> source, int frameWidth, int frameHeight, WatermarkOptions options)
    {
        ImageFrame<Rgba32> stamp = source;
        if (options.Scale > 0f)
        {
            double factor = Math.Min(options.Scale * frameWidth / source.Width, options.Scale * frameHeight / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * factor));
            int height = Math.Max(1, (int)Math.Round(source.Height * factor));
            stamp = FrameOps.Resize(source, width, height, options.Sampler);
        }

        float degrees = options.Rotation % 360f;
        if (degrees < 0)
        {
            degrees += 360f;
        }

        return degrees switch
        {
            0f => stamp,
            90f => FrameOps.Rotate90(stamp),
            180f => FrameOps.Rotate180(stamp),
            270f => FrameOps.Rotate270(stamp),
            _ => FrameOps.RotateArbitrary(stamp, degrees, Rgba32.Transparent),
        };
    }

    /// <summary>Composites the prepared stamp at its anchored position, or on a grid through it when tiling.</summary>
    public static void Apply<TPixel>(ImageFrame<TPixel> frame, ImageFrame<Rgba32> stamp, WatermarkOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int sw = stamp.Width;
        int sh = stamp.Height;
        int margin = options.Margin;
        int anchorX = FrameOps.AnchorOffset(options.Position, horizontal: true, width - (2 * margin), sw) + margin;
        int anchorY = FrameOps.AnchorOffset(options.Position, horizontal: false, height - (2 * margin), sh) + margin;
        long[] columns = Positions(anchorX, sw, width, options.Tile ? (long)sw + options.TileSpacing.Width : 0);
        long[] rows = Positions(anchorY, sh, height, options.Tile ? (long)sh + options.TileSpacing.Height : 0);
        if (columns.Length == 0 || rows.Length == 0)
        {
            return;
        }

        float opacity = options.Opacity;
        PixelColorBlendingMode mode = options.BlendMode;
        bool wide = PixelOps.IsHighPrecision<TPixel>();
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var destination = new Rgba32[sw];
            for (int y = start; y < end; y++)
            {
                // Tiles never overlap vertically (the pitch is at least the stamp height), so one tile row at most.
                int stampRow = -1;
                foreach (long top in rows)
                {
                    if (y >= top && y < top + sh)
                    {
                        stampRow = (int)(y - top);
                        break;
                    }
                }

                if (stampRow < 0)
                {
                    continue;
                }

                Span<TPixel> target = frame.GetRowSpan(y);
                Span<Rgba32> source = stamp.GetRowSpan(stampRow);
                foreach (long left in columns)
                {
                    int x0 = (int)Math.Max(0, left);
                    int x1 = (int)Math.Min(width, left + sw);
                    if (x0 >= x1)
                    {
                        continue;
                    }

                    Span<Rgba32> src = source.Slice((int)(x0 - left), x1 - x0);
                    Span<TPixel> dst = target[x0..x1];
                    if (wide)
                    {
                        BlendWide(dst, src, opacity, mode);
                        continue;
                    }

                    Span<Rgba32> backdrop = destination.AsSpan(0, x1 - x0);
                    PixelOps.ToRgba32<TPixel>(dst, backdrop);
                    PixelBlender.Blend(backdrop, backdrop, src, opacity, mode, PixelAlphaCompositionMode.SrcOver);
                    PixelOps.FromRgba32<TPixel>(backdrop, dst);
                }
            }
        });
    }

    /// <summary>
    /// Start coordinates of the stamp along one axis: just the anchor when <paramref name="pitch"/> is 0, otherwise
    /// every <c>anchor + k * pitch</c> whose extent overlaps <c>[0, length)</c>.
    /// </summary>
    private static long[] Positions(int anchor, int extent, int length, long pitch)
    {
        if (pitch <= 0)
        {
            return anchor < length && anchor + extent > 0 ? new long[] { anchor } : Array.Empty<long>();
        }

        // The first position whose extent reaches into the frame: the smallest k with anchor + k pitch + extent > 0.
        long numerator = -(long)extent - anchor;
        long k = (numerator >= 0 ? numerator / pitch : -((-numerator + pitch - 1) / pitch)) + 1;
        long first = anchor + (k * pitch);

        var positions = new List<long>();
        for (long p = first; p < length; p += pitch)
        {
            positions.Add(p);
        }

        return positions.ToArray();
    }

    /// <summary>Wide formats blend in floating point so pixels the stamp leaves untouched keep their full precision.</summary>
    private static void BlendWide<TPixel>(Span<TPixel> destination, ReadOnlySpan<Rgba32> source, float opacity, PixelColorBlendingMode mode)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int i = 0; i < source.Length; i++)
        {
            Rgba32 s = source[i];
            if (s.A == 0 || opacity == 0f)
            {
                continue;
            }

            Vector4 src = RowProcessor.ToUnitVector(s);
            src.W *= opacity;
            Vector4 backdrop = destination[i].ToScaledVector4();
            destination[i] = TPixel.FromScaledVector4(PixelBlender.BlendUnit(backdrop, src, mode, PixelAlphaCompositionMode.SrcOver));
        }
    }
}
