using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Geometric transform operations.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    public IImageProcessingContext Rotate(float degrees, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        float normalized = NormalizeDegrees(degrees);
        if (normalized == 0f)
        {
            return this;
        }

        if (normalized is 90f or 180f or 270f)
        {
            // Lossless fast paths; the fill colour never shows for right angles.
            return this.Rotate(normalized);
        }

        return this.Transform(new AffineTransformBuilder().AppendRotationDegrees(normalized), sampler, fill);
    }

    public IImageProcessingContext Skew(float degreesX, float degreesY, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        if (!float.IsFinite(degreesX) || !float.IsFinite(degreesY))
        {
            throw new ArgumentOutOfRangeException(nameof(degreesX), "Skew angles must be finite.");
        }

        if (degreesX == 0f && degreesY == 0f)
        {
            return this;
        }

        return this.Transform(new AffineTransformBuilder().AppendSkewDegrees(degreesX, degreesY), sampler, fill);
    }

    public IImageProcessingContext Transform(AffineTransformBuilder builder, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sampler);
        return this.PerFrame(frame =>
        {
            var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
            Matrix3x2 matrix = builder.BuildMatrix(bounds);
            Size size = builder.GetTransformedSize(bounds);
            return TransformOps.TransformAffine(frame, bounds, matrix, size, sampler, fill);
        });
    }

    public IImageProcessingContext Transform(ProjectiveTransformBuilder builder, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sampler);
        return this.PerFrame(frame =>
        {
            var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
            Matrix4x4 matrix = builder.BuildMatrix(bounds);
            Size size = builder.GetTransformedSize(bounds);
            return TransformOps.TransformProjective(frame, bounds, matrix, size, sampler, fill);
        });
    }

    public IImageProcessingContext Transform(Rectangle sourceRectangle, Matrix3x2 matrix, Size targetSize, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        return this.PerFrame(frame => TransformOps.TransformAffine(frame, sourceRectangle, matrix, targetSize, sampler, fill));
    }

    public IImageProcessingContext Transform(Rectangle sourceRectangle, Matrix4x4 matrix, Size targetSize, IResampler sampler, Color fill)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        return this.PerFrame(frame => TransformOps.TransformProjective(frame, sourceRectangle, matrix, targetSize, sampler, fill));
    }

    public IImageProcessingContext EntropyCrop(float threshold)
    {
        if (!(threshold >= 0f) || threshold > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The entropy threshold must be in the range [0, 1].");
        }

        ImageFrame<TPixel> root = this.image.Frames.RootFrame;
        Rectangle bounds = FrameOps.EntropyCropBounds(root, threshold);
        if (bounds == new Rectangle(0, 0, root.Width, root.Height))
        {
            return this;
        }

        return this.PerFrame(frame => FrameOps.Crop(frame, bounds));
    }

    private static float NormalizeDegrees(float degrees)
    {
        if (!float.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The rotation angle must be finite.");
        }

        float normalized = degrees % 360f;
        if (normalized < 0)
        {
            normalized += 360f;
        }

        return normalized;
    }
}
