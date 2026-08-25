using System.Numerics;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the geometric transform operations.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Rotates clockwise by any angle with the given kernel, expanding the canvas and filling the corners with transparent black.</summary>
    public static IImageProcessingContext Rotate(this IImageProcessingContext context, float degrees, IResampler sampler)
        => context.Rotate(degrees, sampler, Color.Transparent);

    /// <summary>Skews by the given angles with the default (bicubic) kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Skew(this IImageProcessingContext context, float degreesX, float degreesY)
        => context.Skew(degreesX, degreesY, KnownResamplers.Bicubic, Color.Transparent);

    /// <summary>Skews by the given angles with the given kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Skew(this IImageProcessingContext context, float degreesX, float degreesY, IResampler sampler)
        => context.Skew(degreesX, degreesY, sampler, Color.Transparent);

    /// <summary>Applies an affine transform with the default (bicubic) kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(this IImageProcessingContext context, AffineTransformBuilder builder)
        => context.Transform(builder, KnownResamplers.Bicubic, Color.Transparent);

    /// <summary>Applies an affine transform with the given kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(this IImageProcessingContext context, AffineTransformBuilder builder, IResampler sampler)
        => context.Transform(builder, sampler, Color.Transparent);

    /// <summary>Applies a projective transform with the default (bicubic) kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(this IImageProcessingContext context, ProjectiveTransformBuilder builder)
        => context.Transform(builder, KnownResamplers.Bicubic, Color.Transparent);

    /// <summary>Applies a projective transform with the given kernel and a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(this IImageProcessingContext context, ProjectiveTransformBuilder builder, IResampler sampler)
        => context.Transform(builder, sampler, Color.Transparent);

    /// <summary>Transforms a source rectangle by an explicit affine matrix onto a canvas of the given size with a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(
        this IImageProcessingContext context, Rectangle sourceRectangle, Matrix3x2 matrix, Size targetSize, IResampler sampler)
        => context.Transform(sourceRectangle, matrix, targetSize, sampler, Color.Transparent);

    /// <summary>Transforms a source rectangle by an explicit projective matrix onto a canvas of the given size with a transparent-black fill.</summary>
    public static IImageProcessingContext Transform(
        this IImageProcessingContext context, Rectangle sourceRectangle, Matrix4x4 matrix, Size targetSize, IResampler sampler)
        => context.Transform(sourceRectangle, matrix, targetSize, sampler, Color.Transparent);

    /// <summary>Crops to the bounding box of edge-detected content using a threshold of 0.5.</summary>
    public static IImageProcessingContext EntropyCrop(this IImageProcessingContext context) => context.EntropyCrop(0.5f);

    /// <summary>Crops to the top-left <paramref name="width"/> x <paramref name="height"/> region.</summary>
    public static IImageProcessingContext Crop(this IImageProcessingContext context, int width, int height)
        => context.Crop(new Rectangle(0, 0, width, height));

    /// <summary>Resizes to the exact target size with the given kernel, optionally filtering in linear light.</summary>
    public static IImageProcessingContext Resize(this IImageProcessingContext context, int width, int height, IResampler sampler, bool compand)
        => context.Resize(new ResizeOptions { Size = new Size(width, height), Sampler = sampler, Compand = compand });
}
