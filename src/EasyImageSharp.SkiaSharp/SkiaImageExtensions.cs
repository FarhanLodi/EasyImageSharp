using System.Numerics;
using EasyImageSharp.PixelFormats;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace EasyImageSharp.SkiaSharp;

/// <summary>Copying pixel interop and optional native drawing. Callers own all returned native objects.</summary>
public static class SkiaImageExtensions
{
    /// <summary>
    /// Copies one frame into an owned, premultiplied RGBA float bitmap. No pin or shared buffer survives this call.
    /// Channels are not color-converted; an optional native color space labels their current encoding.
    /// Hidden RGB at zero alpha is discarded by premultiplication. The caller must dispose the bitmap.
    /// </summary>
    public static unsafe SKBitmap ToSkiaBitmap<TPixel>(this Image<TPixel> image, int frameIndex = 0, SKColorSpace? colorSpace = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageFrame<TPixel> frame = image.Frames[frameIndex];
        if (frame.Width > int.MaxValue / 16) throw new NotSupportedException("The frame exceeds the native float row-stride limit.");
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.RgbaF32, SKAlphaType.Premul, colorSpace);
        var bitmap = new SKBitmap();
        try
        {
            if (!bitmap.TryAllocPixels(info)) throw new OutOfMemoryException("Could not allocate the native bitmap.");
            byte* pointer = (byte*)bitmap.GetPixels();
            for (int y = 0; y < frame.Height; y++)
            {
                Span<Vector4> target = new(pointer + ((long)y * bitmap.RowBytes), frame.Width);
                ReadOnlySpan<TPixel> source = frame.GetRowSpan(y);
                for (int x = 0; x < source.Length; x++)
                {
                    Vector4 p = source[x].ToScaledVector4();
                    target[x] = new(p.X * p.W, p.Y * p.W, p.Z * p.W, p.W);
                }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    /// <summary>
    /// Copies a native bitmap to a single-frame image using RGBA float intermediates and straight alpha.
    /// Native row strides and supported native pixel formats are handled by Skia. No metadata is inferred.
    /// The source color encoding is retained; use ConvertColorSpace explicitly when needed.
    /// </summary>
    public static unsafe Image<TPixel> ToEasyImage<TPixel>(this SKBitmap bitmap)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.GetPixels() == IntPtr.Zero)
            throw new ArgumentException("The native bitmap has no pixel storage.", nameof(bitmap));
        if (bitmap.Width > int.MaxValue / 16) throw new NotSupportedException("The bitmap exceeds the native float row-stride limit.");
        using var converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height,
            SKColorType.RgbaF32, SKAlphaType.Unpremul, bitmap.ColorSpace));
        if (converted.GetPixels() == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate the native conversion buffer.");
        using SKPixmap pixels = bitmap.PeekPixels();
        if (!pixels.ReadPixels(converted.Info, converted.GetPixels(), converted.RowBytes))
            throw new NotSupportedException("Skia could not convert the native pixel layout.");
        var result = new Image<TPixel>(bitmap.Width, bitmap.Height);
        try
        {
            byte* pointer = (byte*)converted.GetPixels();
            for (int y = 0; y < result.Height; y++)
            {
                ReadOnlySpan<Vector4> source = new(pointer + ((long)y * converted.RowBytes), result.Width);
                Span<TPixel> target = result.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < target.Length; x++) target[x] = TPixel.FromScaledVector4(source[x]);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>
    /// Draws onto a copy of one frame using Skia paths, paints, gradients, clipping and blend modes.
    /// Other frames and metadata are copied. The callback must not retain or dispose the canvas.
    /// Drawing occurs in the image's current RGB encoding; ICC conversion is explicit.
    /// </summary>
    public static Image<TPixel> DrawWithSkia<TPixel>(this Image<TPixel> image, Action<SKCanvas> draw, int frameIndex = 0)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(draw);
        using SKBitmap bitmap = image.ToSkiaBitmap(frameIndex);
        using (var canvas = new SKCanvas(bitmap))
        {
            draw(canvas);
            canvas.Flush();
        }
        using Image<TPixel> rendered = bitmap.ToEasyImage<TPixel>();
        Image<TPixel> result = image.Clone();
        try
        {
            ImageFrame<TPixel> target = result.Frames[frameIndex];
            for (int y = 0; y < target.Height; y++) rendered.Frames.RootFrame.GetRowSpan(y).CopyTo(target.GetRowSpan(y));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>
    /// Shapes and draws one Unicode text run with HarfBuzz, using the caller's font and paint.
    /// Font fallback, paragraph bidi ordering, line breaking and vertical text are caller responsibilities.
    /// The typeface must contain the requested glyphs. Resources remain owned by the caller.
    /// </summary>
    public static Image<TPixel> DrawShapedText<TPixel>(this Image<TPixel> image, string text, SKPoint baseline,
        SKFont font, SKPaint paint, SKTextAlign alignment = SKTextAlign.Left, int frameIndex = 0)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        if (!float.IsFinite(baseline.X) || !float.IsFinite(baseline.Y)) throw new ArgumentOutOfRangeException(nameof(baseline));
        return image.DrawWithSkia(canvas => canvas.DrawShapedText(text, baseline, alignment, font, paint), frameIndex);
    }

    /// <summary>
    /// Converts every frame between explicitly supplied native RGB color spaces using floating-point intermediates.
    /// Clears stale copied ICC tags. This is not a full ICC engine: CMYK, arbitrary LUT profiles and rendering-intent
    /// selection are not provided. Source and destination color spaces remain owned by the caller.
    /// </summary>
    public static Image<TPixel> ConvertColorSpace<TPixel>(this Image<TPixel> image, SKColorSpace source, SKColorSpace destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        Image<TPixel> result = image.Clone();
        try
        {
            for (int frame = 0; frame < image.Frames.Count; frame++)
            {
                using SKBitmap input = image.ToSkiaBitmap(frame, source);
                using var output = new SKBitmap(new SKImageInfo(input.Width, input.Height, SKColorType.RgbaF32, SKAlphaType.Unpremul, destination));
                if (output.GetPixels() == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate the native conversion buffer.");
                using SKPixmap pixels = input.PeekPixels();
                if (!pixels.ReadPixels(output.Info, output.GetPixels(), output.RowBytes))
                    throw new NotSupportedException("Skia does not support the requested RGB color-space conversion.");
                using Image<TPixel> converted = output.ToEasyImage<TPixel>();
                ImageFrame<TPixel> target = result.Frames[frame];
                for (int y = 0; y < target.Height; y++) converted.Frames.RootFrame.GetRowSpan(y).CopyTo(target.GetRowSpan(y));
                target.Metadata.IccProfile = null;
            }
            result.Metadata.IccProfile = null;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
