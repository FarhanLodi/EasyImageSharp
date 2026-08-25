using System.Numerics;

namespace EasyImageSharp.Processing;

/// <summary>
/// Composes a projective (perspective) transform to be applied with
/// <see cref="IImageProcessingContext.Transform(ProjectiveTransformBuilder, IResampler, Color)"/>. Supports every
/// affine operation of <see cref="AffineTransformBuilder"/> plus tapers and four-point quad distortions.
/// <para>
/// Matrices are <see cref="Matrix4x4"/> values in the row-vector convention with the Z row and column left as the
/// identity: <c>(x, y, 0, 1) * M = (x', y', 0, w)</c> and the transformed point is <c>(x' / w, y' / w)</c>. Tapers and
/// quad distortions are defined relative to the source rectangle passed to <see cref="BuildMatrix(Rectangle)"/>.
/// </para>
/// </summary>
public sealed class ProjectiveTransformBuilder
{
    private readonly List<Func<Rectangle, Matrix4x4>> operations = new();

    /// <summary>The number of operations added so far.</summary>
    public int Count => this.operations.Count;

    // ----- Rotation -----

    /// <summary>Prepends a clockwise rotation about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder PrependRotationDegrees(float degrees)
        => this.PrependRotationRadians(TransformUtilities.DegreesToRadians(degrees));

    /// <summary>Prepends a clockwise rotation about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder PrependRotationDegrees(float degrees, PointF origin)
        => this.PrependRotationRadians(TransformUtilities.DegreesToRadians(degrees), origin);

    /// <summary>Prepends a clockwise rotation about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder PrependRotationRadians(float radians)
        => this.Prepend(rect => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateRotation(radians, TransformUtilities.Center(rect))));

    /// <summary>Prepends a clockwise rotation about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder PrependRotationRadians(float radians, PointF origin)
        => this.Prepend(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateRotation(radians, TransformUtilities.ToVector2(origin))));

    /// <summary>Appends a clockwise rotation about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder AppendRotationDegrees(float degrees)
        => this.AppendRotationRadians(TransformUtilities.DegreesToRadians(degrees));

    /// <summary>Appends a clockwise rotation about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder AppendRotationDegrees(float degrees, PointF origin)
        => this.AppendRotationRadians(TransformUtilities.DegreesToRadians(degrees), origin);

    /// <summary>Appends a clockwise rotation about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder AppendRotationRadians(float radians)
        => this.Append(rect => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateRotation(radians, TransformUtilities.Center(rect))));

    /// <summary>Appends a clockwise rotation about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder AppendRotationRadians(float radians, PointF origin)
        => this.Append(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateRotation(radians, TransformUtilities.ToVector2(origin))));

    // ----- Scale -----

    /// <summary>Prepends a uniform scale about the origin.</summary>
    public ProjectiveTransformBuilder PrependScale(float scale) => this.PrependScale(new SizeF(scale, scale));

    /// <summary>Prepends a scale about the origin.</summary>
    public ProjectiveTransformBuilder PrependScale(float scaleX, float scaleY) => this.PrependScale(new SizeF(scaleX, scaleY));

    /// <summary>Prepends a scale about the origin.</summary>
    public ProjectiveTransformBuilder PrependScale(SizeF scales)
        => this.Prepend(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateScale(scales.Width, scales.Height)));

    /// <summary>Prepends a scale about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder PrependScale(SizeF scales, PointF origin)
        => this.Prepend(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateScale(scales.Width, scales.Height, TransformUtilities.ToVector2(origin))));

    /// <summary>Appends a uniform scale about the origin.</summary>
    public ProjectiveTransformBuilder AppendScale(float scale) => this.AppendScale(new SizeF(scale, scale));

    /// <summary>Appends a scale about the origin.</summary>
    public ProjectiveTransformBuilder AppendScale(float scaleX, float scaleY) => this.AppendScale(new SizeF(scaleX, scaleY));

    /// <summary>Appends a scale about the origin.</summary>
    public ProjectiveTransformBuilder AppendScale(SizeF scales)
        => this.Append(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateScale(scales.Width, scales.Height)));

    /// <summary>Appends a scale about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder AppendScale(SizeF scales, PointF origin)
        => this.Append(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateScale(scales.Width, scales.Height, TransformUtilities.ToVector2(origin))));

    // ----- Skew -----

    /// <summary>Prepends a skew (shear) about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder PrependSkewDegrees(float degreesX, float degreesY)
        => this.PrependSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY));

    /// <summary>Prepends a skew (shear) about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder PrependSkewDegrees(float degreesX, float degreesY, PointF origin)
        => this.PrependSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY), origin);

    /// <summary>Prepends a skew (shear) about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder PrependSkewRadians(float radiansX, float radiansY)
        => this.Prepend(rect => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.Center(rect))));

    /// <summary>Prepends a skew (shear) about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder PrependSkewRadians(float radiansX, float radiansY, PointF origin)
        => this.Prepend(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.ToVector2(origin))));

    /// <summary>Appends a skew (shear) about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder AppendSkewDegrees(float degreesX, float degreesY)
        => this.AppendSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY));

    /// <summary>Appends a skew (shear) about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder AppendSkewDegrees(float degreesX, float degreesY, PointF origin)
        => this.AppendSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY), origin);

    /// <summary>Appends a skew (shear) about the centre of the source rectangle.</summary>
    public ProjectiveTransformBuilder AppendSkewRadians(float radiansX, float radiansY)
        => this.Append(rect => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.Center(rect))));

    /// <summary>Appends a skew (shear) about <paramref name="origin"/>.</summary>
    public ProjectiveTransformBuilder AppendSkewRadians(float radiansX, float radiansY, PointF origin)
        => this.Append(_ => TransformUtilities.ToMatrix4x4(Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.ToVector2(origin))));

    // ----- Translation / raw matrices -----

    /// <summary>Prepends a translation.</summary>
    public ProjectiveTransformBuilder PrependTranslation(PointF position)
        => this.Prepend(_ => Matrix4x4.CreateTranslation(position.X, position.Y, 0f));

    /// <summary>Appends a translation.</summary>
    public ProjectiveTransformBuilder AppendTranslation(PointF position)
        => this.Append(_ => Matrix4x4.CreateTranslation(position.X, position.Y, 0f));

    /// <summary>Prepends an arbitrary projective matrix (see the class remarks for the expected layout).</summary>
    public ProjectiveTransformBuilder PrependMatrix(Matrix4x4 matrix)
    {
        EnsureFinite(matrix);
        return this.Prepend(_ => matrix);
    }

    /// <summary>Appends an arbitrary projective matrix (see the class remarks for the expected layout).</summary>
    public ProjectiveTransformBuilder AppendMatrix(Matrix4x4 matrix)
    {
        EnsureFinite(matrix);
        return this.Append(_ => matrix);
    }

    /// <summary>Prepends an affine matrix.</summary>
    public ProjectiveTransformBuilder PrependMatrix(Matrix3x2 matrix) => this.PrependMatrix(TransformUtilities.ToMatrix4x4(matrix));

    /// <summary>Appends an affine matrix.</summary>
    public ProjectiveTransformBuilder AppendMatrix(Matrix3x2 matrix) => this.AppendMatrix(TransformUtilities.ToMatrix4x4(matrix));

    // ----- Perspective primitives -----

    /// <summary>
    /// Prepends a taper: one side of the source rectangle shrinks to <paramref name="fraction"/> (0..1] of its length,
    /// as if that side receded from the viewer.
    /// </summary>
    public ProjectiveTransformBuilder PrependTaper(TaperSide side, TaperCorner corner, float fraction)
    {
        ValidateTaper(side, corner, fraction);
        return this.Prepend(rect => TransformUtilities.CreateTaper(rect, side, corner, fraction));
    }

    /// <summary>
    /// Appends a taper: one side of the source rectangle shrinks to <paramref name="fraction"/> (0..1] of its length,
    /// as if that side receded from the viewer.
    /// </summary>
    public ProjectiveTransformBuilder AppendTaper(TaperSide side, TaperCorner corner, float fraction)
    {
        ValidateTaper(side, corner, fraction);
        return this.Append(rect => TransformUtilities.CreateTaper(rect, side, corner, fraction));
    }

    /// <summary>
    /// Prepends the four-point perspective distortion that maps the corners of the source rectangle onto the given
    /// quadrilateral (in source pixel coordinates).
    /// </summary>
    public ProjectiveTransformBuilder PrependQuadDistortion(PointF topLeft, PointF topRight, PointF bottomRight, PointF bottomLeft)
        => this.Prepend(rect => TransformUtilities.CreateQuadDistortion(rect, topLeft, topRight, bottomRight, bottomLeft));

    /// <summary>
    /// Appends the four-point perspective distortion that maps the corners of the source rectangle onto the given
    /// quadrilateral (in source pixel coordinates). This is the primitive behind arbitrary perspective warps.
    /// </summary>
    public ProjectiveTransformBuilder AppendQuadDistortion(PointF topLeft, PointF topRight, PointF bottomRight, PointF bottomLeft)
        => this.Append(rect => TransformUtilities.CreateQuadDistortion(rect, topLeft, topRight, bottomRight, bottomLeft));

    /// <summary>Removes every operation.</summary>
    public void Clear() => this.operations.Clear();

    // ----- Building -----

    /// <summary>
    /// Builds the matrix mapping source coordinates to destination coordinates for the given source rectangle,
    /// including a final translation so the transformed bounding box (<see cref="GetTransformedBoundingBox"/>)
    /// starts at the origin; the matching canvas size is <see cref="GetTransformedSize"/>.
    /// </summary>
    public Matrix4x4 BuildMatrix(Rectangle sourceRectangle)
    {
        Matrix4x4 matrix = this.Compose(sourceRectangle);
        RectangleF bounds = TransformUtilities.GetBoundingBox(sourceRectangle, matrix);
        return matrix * Matrix4x4.CreateTranslation(-bounds.X, -bounds.Y, 0f);
    }

    /// <summary>Builds the matrix for a source of the given size located at the origin.</summary>
    public Matrix4x4 BuildMatrix(Size sourceSize) => this.BuildMatrix(new Rectangle(Point.Empty, sourceSize));

    /// <summary>The bounding box of the source rectangle's corners after all operations (before the final translation to the origin).</summary>
    public RectangleF GetTransformedBoundingBox(Rectangle sourceRectangle)
        => TransformUtilities.GetBoundingBox(sourceRectangle, this.Compose(sourceRectangle));

    /// <summary>The canvas size needed to hold the transformed source: the bounding box rounded up to whole pixels.</summary>
    public Size GetTransformedSize(Rectangle sourceRectangle)
        => TransformUtilities.CeilingSize(this.GetTransformedBoundingBox(sourceRectangle).Size);

    private Matrix4x4 Compose(Rectangle sourceRectangle)
    {
        AffineTransformBuilder.ValidateRectangle(sourceRectangle);
        Matrix4x4 matrix = Matrix4x4.Identity;
        foreach (Func<Rectangle, Matrix4x4> operation in this.operations)
        {
            matrix *= operation(sourceRectangle);
        }

        return matrix;
    }

    private ProjectiveTransformBuilder Prepend(Func<Rectangle, Matrix4x4> operation)
    {
        this.operations.Insert(0, operation);
        return this;
    }

    private ProjectiveTransformBuilder Append(Func<Rectangle, Matrix4x4> operation)
    {
        this.operations.Add(operation);
        return this;
    }

    private static void ValidateTaper(TaperSide side, TaperCorner corner, float fraction)
    {
        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown taper side.");
        }

        if (!Enum.IsDefined(corner))
        {
            throw new ArgumentOutOfRangeException(nameof(corner), corner, "Unknown taper corner.");
        }

        if (!(fraction > 0f) || fraction > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "The taper fraction must be in the range (0, 1].");
        }
    }

    private static void EnsureFinite(Matrix4x4 matrix)
    {
        if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M14)
            || !float.IsFinite(matrix.M21) || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M24)
            || !float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M44))
        {
            throw new ArgumentException("The matrix contains non-finite values.", nameof(matrix));
        }
    }
}
