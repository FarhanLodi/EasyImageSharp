using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>Row-copy helpers over the public frame API (the add-on has no access to core internals).</summary>
internal static class FrameUtil
{
    /// <summary>Copies a rectangle of the root frame into a new single-frame image.</summary>
    public static Image<TPixel> CropCopy<TPixel>(Image<TPixel> source, Rectangle rect)
        where TPixel : unmanaged, IPixel<TPixel>
        => CropCopy(source.Frames.RootFrame, rect);

    /// <summary>Copies a rectangle of a frame into a new single-frame image.</summary>
    public static Image<TPixel> CropCopy<TPixel>(ImageFrame<TPixel> source, Rectangle rect)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > source.Width || rect.Bottom > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"Rectangle is outside the {source.Width}x{source.Height} frame.");
        }

        var result = new Image<TPixel>(rect.Width, rect.Height);
        ImageFrame<TPixel> target = result.Frames.RootFrame;
        for (int y = 0; y < rect.Height; y++)
        {
            source.GetRowSpan(rect.Y + y).Slice(rect.X, rect.Width).CopyTo(target.GetRowSpan(y));
        }

        return result;
    }

    /// <summary>A single-frame copy of frame <paramref name="index"/> (or the image itself when it has one frame and index is 0).</summary>
    public static Image<TPixel> FrameAsImage<TPixel>(Image<TPixel> image, int index, out bool isCopy)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (index == 0 && image.Frames.Count == 1)
        {
            isCopy = false;
            return image;
        }

        isCopy = true;
        return image.Frames.CloneFrame(index);
    }

    /// <summary>Copies a <paramref name="width"/> x <paramref name="height"/> block between frames.</summary>
    public static void CopyRegion<TPixel>(
        ImageFrame<TPixel> source, int sourceX, int sourceY,
        ImageFrame<TPixel> target, int targetX, int targetY,
        int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int y = 0; y < height; y++)
        {
            source.GetRowSpan(sourceY + y).Slice(sourceX, width).CopyTo(target.GetRowSpan(targetY + y).Slice(targetX, width));
        }
    }

    /// <summary>Copies every pixel of a same-sized single-frame image into <paramref name="target"/>.</summary>
    public static void CopyInto<TPixel>(Image<TPixel> source, ImageFrame<TPixel> target)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (source.Width != target.Width || source.Height != target.Height)
        {
            throw new ArgumentException($"Cannot copy a {source.Width}x{source.Height} image into a {target.Width}x{target.Height} frame.");
        }

        CopyRegion(source.Frames.RootFrame, 0, 0, target, 0, 0, target.Width, target.Height);
    }

    /// <summary>Extends an image to <paramref name="width"/> x <paramref name="height"/> by replicating its last column / row.</summary>
    public static Image<TPixel> PadReplicate<TPixel>(Image<TPixel> source, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (width < source.Width || height < source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Padded size must not be smaller than the source.");
        }

        var result = new Image<TPixel>(width, height);
        ImageFrame<TPixel> src = source.Frames.RootFrame;
        ImageFrame<TPixel> dst = result.Frames.RootFrame;
        for (int y = 0; y < height; y++)
        {
            Span<TPixel> srcRow = src.GetRowSpan(Math.Min(y, source.Height - 1));
            Span<TPixel> dstRow = dst.GetRowSpan(y);
            srcRow.CopyTo(dstRow);
            if (width > source.Width)
            {
                dstRow[source.Width..].Fill(srcRow[source.Width - 1]);
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces every frame of <paramref name="image"/> with the given same-count replacements (which may differ
    /// in size), preserving order. Uses only the public frame collection API: new frames are appended, then the
    /// originals are removed from the front.
    /// </summary>
    public static void ReplaceAllFrames<TPixel>(Image<TPixel> image, IReadOnlyList<Image<TPixel>> replacements)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int original = image.Frames.Count;
        if (replacements.Count != original)
        {
            throw new ArgumentException($"Expected {original} replacement frames, got {replacements.Count}.", nameof(replacements));
        }

        for (int i = 0; i < original; i++)
        {
            Image<TPixel> replacement = replacements[i];
            ImageFrame<TPixel> frame = image.Frames.CreateFrame(replacement.Width, replacement.Height);
            CopyInto(replacement, frame);
        }

        for (int i = 0; i < original; i++)
        {
            image.Frames.RemoveFrame(0);
        }
    }

    /// <summary>Replaces frame <paramref name="index"/> in place when sizes match, otherwise defers to a full swap by the caller.</summary>
    public static bool TryCopyIntoFrame<TPixel>(Image<TPixel> image, int index, Image<TPixel> replacement)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> frame = image.Frames[index];
        if (frame.Width != replacement.Width || frame.Height != replacement.Height)
        {
            return false;
        }

        CopyInto(replacement, frame);
        return true;
    }

    /// <summary>Stretch-resizes a single-frame image to the given size with the given resampler.</summary>
    public static Image<TPixel> ResizeCopy<TPixel>(Image<TPixel> source, int width, int height, IResampler sampler)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> single = FrameAsImage(source, 0, out bool isCopy);
        try
        {
            if (single.Width == width && single.Height == height)
            {
                return isCopy ? single : single.Clone();
            }

            Image<TPixel> resized = single.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Stretch,
                Sampler = sampler,
            }));
            if (isCopy)
            {
                single.Dispose();
            }

            return resized;
        }
        catch
        {
            if (isCopy)
            {
                single.Dispose();
            }

            throw;
        }
    }
}
