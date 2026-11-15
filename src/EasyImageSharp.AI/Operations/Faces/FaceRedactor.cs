using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>Obscures face regions with the core blur / pixelate / fill operations.</summary>
internal static class FaceRedactor
{
    /// <summary>
    /// Detects and redacts faces on every frame in place (each frame is detected on its own) and returns the faces of
    /// the root frame.
    /// </summary>
    public static IReadOnlyList<DetectedFace> DetectAndRedact<TPixel>(
        InferenceSession session, Image<TPixel> image, FaceRedactionOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (image.Frames.Count == 1)
        {
            IReadOnlyList<DetectedFace> faces = FaceDetector.Detect(session, image, options.Detection, cancellationToken);
            Redact(image, faces, options, cancellationToken);
            return faces;
        }

        IReadOnlyList<DetectedFace> rootFaces = [];
        int index = 0;
        AiOperations.ForEachFrame(image, frame =>
        {
            IReadOnlyList<DetectedFace> faces = FaceDetector.Detect(session, frame, options.Detection, cancellationToken);
            if (index++ == 0)
            {
                rootFaces = faces;
            }

            Image<TPixel> copy = frame.Clone();
            try
            {
                Redact(copy, faces, options, cancellationToken);
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        });
        return rootFaces;
    }

    /// <summary>Redacts the given regions on every frame of <paramref name="image"/> in place.</summary>
    public static void Redact<TPixel>(Image<TPixel> image, IReadOnlyList<DetectedFace> faces, FaceRedactionOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var regions = new List<Rectangle>(faces.Count);
        foreach (DetectedFace face in faces)
        {
            Rectangle region = Region(face.Bounds, options.Padding, image.Width, image.Height);
            if (region.Width > 0 && region.Height > 0)
            {
                regions.Add(region);
            }
        }

        if (regions.Count == 0)
        {
            return;
        }

        image.Mutate(new ImageExecutionOptions { CancellationToken = cancellationToken }, ctx =>
        {
            foreach (Rectangle region in regions)
            {
                switch (options.Style)
                {
                    case FaceRedactionStyle.Blur:
                        ctx.GaussianBlur(options.BlurSigma ?? MathF.Max(2f, Math.Min(region.Width, region.Height) / 4f), region);
                        break;
                    case FaceRedactionStyle.Pixelate:
                        ctx.Pixelate(options.PixelSize ?? Math.Max(4, (Math.Max(region.Width, region.Height) + 5) / 6), region);
                        break;
                    default:
                        ctx.Fill(options.FillColor, region);
                        break;
                }
            }
        });
    }

    /// <summary>The padded face box rounded outwards to whole pixels and clipped to the image.</summary>
    internal static Rectangle Region(RectangleF bounds, float padding, int width, int height)
    {
        float padX = bounds.Width * padding;
        float padY = bounds.Height * padding;
        double left = Math.Floor(bounds.X - padX);
        double top = Math.Floor(bounds.Y - padY);
        double right = Math.Ceiling(bounds.Right + padX);
        double bottom = Math.Ceiling(bounds.Bottom + padY);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom))
        {
            return Rectangle.Empty;
        }

        int x0 = (int)Math.Clamp(left, 0, width);
        int y0 = (int)Math.Clamp(top, 0, height);
        int x1 = (int)Math.Clamp(right, 0, width);
        int y1 = (int)Math.Clamp(bottom, 0, height);
        return x1 > x0 && y1 > y0 ? new Rectangle(x0, y0, x1 - x0, y1 - y0) : Rectangle.Empty;
    }
}
