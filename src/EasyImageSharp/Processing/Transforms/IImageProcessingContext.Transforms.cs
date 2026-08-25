using System.Numerics;

namespace EasyImageSharp.Processing;

/// <summary>Geometric transform operations: rotation with a chosen kernel, skew, affine/projective warps, entropy crop.</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Rotates clockwise by any angle with the given kernel, expanding the canvas to the rotated bounding box and
    /// filling the uncovered corners with <paramref name="fill"/>. Multiples of 90 degrees use the lossless fast path.
    /// </summary>
    IImageProcessingContext Rotate(float degrees, IResampler sampler, Color fill);

    /// <summary>
    /// Skews (shears) by the given angles about the image centre, expanding the canvas to the sheared bounding box.
    /// A positive <paramref name="degreesX"/> shifts rows further right the lower they are; a positive
    /// <paramref name="degreesY"/> shifts columns further down the further right they are.
    /// </summary>
    IImageProcessingContext Skew(float degreesX, float degreesY, IResampler sampler, Color fill);

    /// <summary>
    /// Applies the affine transform described by <paramref name="builder"/>; the canvas becomes the transformed
    /// bounding box and uncovered pixels are <paramref name="fill"/>.
    /// </summary>
    IImageProcessingContext Transform(AffineTransformBuilder builder, IResampler sampler, Color fill);

    /// <summary>
    /// Applies the projective (perspective) transform described by <paramref name="builder"/>; the canvas becomes the
    /// transformed bounding box and uncovered pixels are <paramref name="fill"/>.
    /// </summary>
    IImageProcessingContext Transform(ProjectiveTransformBuilder builder, IResampler sampler, Color fill);

    /// <summary>
    /// Transforms <paramref name="sourceRectangle"/> by an explicit affine <paramref name="matrix"/> (source pixel
    /// coordinates to destination coordinates, row-vector convention, pixel centres at half-integers) onto a canvas of
    /// <paramref name="targetSize"/>. Source pixels outside the rectangle and destination pixels with no source are
    /// <paramref name="fill"/>.
    /// </summary>
    IImageProcessingContext Transform(Rectangle sourceRectangle, Matrix3x2 matrix, Size targetSize, IResampler sampler, Color fill);

    /// <summary>
    /// Transforms <paramref name="sourceRectangle"/> by an explicit projective <paramref name="matrix"/> (layout as
    /// documented on <see cref="ProjectiveTransformBuilder"/>) onto a canvas of <paramref name="targetSize"/>. Source
    /// pixels outside the rectangle and destination pixels with no source are <paramref name="fill"/>.
    /// </summary>
    IImageProcessingContext Transform(Rectangle sourceRectangle, Matrix4x4 matrix, Size targetSize, IResampler sampler, Color fill);

    /// <summary>
    /// Crops to the bounding box of edge-detected content: the Sobel gradient magnitude of the luminance is
    /// normalised so a full-contrast step edge scores 1.0, and every pixel scoring at least
    /// <paramref name="threshold"/> (0..1) is kept. The box is measured on the root frame and applied to every frame;
    /// images with no qualifying edge are left unchanged.
    /// </summary>
    IImageProcessingContext EntropyCrop(float threshold);
}
