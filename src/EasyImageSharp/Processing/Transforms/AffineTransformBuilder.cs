using System.Numerics;

namespace EasyImageSharp.Processing;

/// <summary>
/// Composes an affine transform (rotation, scale, skew, translation, arbitrary <see cref="Matrix3x2"/>) to be
/// applied with <see cref="IImageProcessingContext.Transform(AffineTransformBuilder, IResampler, Color)"/>.
/// <para>
/// Operations are functions of the source rectangle so rotations and skews can default to its centre; they are
/// evaluated by <see cref="BuildMatrix(Rectangle)"/>. <c>Append</c> applies an operation after everything added so
/// far, <c>Prepend</c> before it. Matrices use the row-vector convention of <see cref="System.Numerics"/>
/// (<c>point * matrix</c>, translation in the last row, <c>A * B</c> applies <c>A</c> first) and coordinates are in
/// pixels of the source frame, with pixel centres at half-integer positions.
/// </para>
/// </summary>
public sealed class AffineTransformBuilder
{
    private readonly List<Func<Rectangle, Matrix3x2>> operations = new();

    /// <summary>The number of operations added so far.</summary>
    public int Count => this.operations.Count;

    // ----- Rotation -----

    /// <summary>Prepends a clockwise rotation about the centre of the source rectangle.</summary>
    public AffineTransformBuilder PrependRotationDegrees(float degrees)
        => this.PrependRotationRadians(TransformUtilities.DegreesToRadians(degrees));

    /// <summary>Prepends a clockwise rotation about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder PrependRotationDegrees(float degrees, PointF origin)
        => this.PrependRotationRadians(TransformUtilities.DegreesToRadians(degrees), origin);

    /// <summary>Prepends a clockwise rotation about the centre of the source rectangle.</summary>
    public AffineTransformBuilder PrependRotationRadians(float radians)
        => this.Prepend(rect => Matrix3x2.CreateRotation(radians, TransformUtilities.Center(rect)));

    /// <summary>Prepends a clockwise rotation about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder PrependRotationRadians(float radians, PointF origin)
        => this.Prepend(_ => Matrix3x2.CreateRotation(radians, TransformUtilities.ToVector2(origin)));

    /// <summary>Appends a clockwise rotation about the centre of the source rectangle.</summary>
    public AffineTransformBuilder AppendRotationDegrees(float degrees)
        => this.AppendRotationRadians(TransformUtilities.DegreesToRadians(degrees));

    /// <summary>Appends a clockwise rotation about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder AppendRotationDegrees(float degrees, PointF origin)
        => this.AppendRotationRadians(TransformUtilities.DegreesToRadians(degrees), origin);

    /// <summary>Appends a clockwise rotation about the centre of the source rectangle.</summary>
    public AffineTransformBuilder AppendRotationRadians(float radians)
        => this.Append(rect => Matrix3x2.CreateRotation(radians, TransformUtilities.Center(rect)));

    /// <summary>Appends a clockwise rotation about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder AppendRotationRadians(float radians, PointF origin)
        => this.Append(_ => Matrix3x2.CreateRotation(radians, TransformUtilities.ToVector2(origin)));

    // ----- Scale -----

    /// <summary>Prepends a uniform scale about the origin.</summary>
    public AffineTransformBuilder PrependScale(float scale) => this.PrependScale(new SizeF(scale, scale));

    /// <summary>Prepends a scale about the origin.</summary>
    public AffineTransformBuilder PrependScale(float scaleX, float scaleY) => this.PrependScale(new SizeF(scaleX, scaleY));

    /// <summary>Prepends a scale about the origin.</summary>
    public AffineTransformBuilder PrependScale(SizeF scales)
        => this.Prepend(_ => Matrix3x2.CreateScale(scales.Width, scales.Height));

    /// <summary>Prepends a scale about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder PrependScale(SizeF scales, PointF origin)
        => this.Prepend(_ => Matrix3x2.CreateScale(scales.Width, scales.Height, TransformUtilities.ToVector2(origin)));

    /// <summary>Appends a uniform scale about the origin.</summary>
    public AffineTransformBuilder AppendScale(float scale) => this.AppendScale(new SizeF(scale, scale));

    /// <summary>Appends a scale about the origin.</summary>
    public AffineTransformBuilder AppendScale(float scaleX, float scaleY) => this.AppendScale(new SizeF(scaleX, scaleY));

    /// <summary>Appends a scale about the origin.</summary>
    public AffineTransformBuilder AppendScale(SizeF scales)
        => this.Append(_ => Matrix3x2.CreateScale(scales.Width, scales.Height));

    /// <summary>Appends a scale about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder AppendScale(SizeF scales, PointF origin)
        => this.Append(_ => Matrix3x2.CreateScale(scales.Width, scales.Height, TransformUtilities.ToVector2(origin)));

    // ----- Skew -----

    /// <summary>Prepends a skew (shear) about the centre of the source rectangle; a positive X angle shifts rows further right the lower they are.</summary>
    public AffineTransformBuilder PrependSkewDegrees(float degreesX, float degreesY)
        => this.PrependSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY));

    /// <summary>Prepends a skew (shear) about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder PrependSkewDegrees(float degreesX, float degreesY, PointF origin)
        => this.PrependSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY), origin);

    /// <summary>Prepends a skew (shear) about the centre of the source rectangle.</summary>
    public AffineTransformBuilder PrependSkewRadians(float radiansX, float radiansY)
        => this.Prepend(rect => Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.Center(rect)));

    /// <summary>Prepends a skew (shear) about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder PrependSkewRadians(float radiansX, float radiansY, PointF origin)
        => this.Prepend(_ => Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.ToVector2(origin)));

    /// <summary>Appends a skew (shear) about the centre of the source rectangle; a positive X angle shifts rows further right the lower they are.</summary>
    public AffineTransformBuilder AppendSkewDegrees(float degreesX, float degreesY)
        => this.AppendSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY));

    /// <summary>Appends a skew (shear) about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder AppendSkewDegrees(float degreesX, float degreesY, PointF origin)
        => this.AppendSkewRadians(TransformUtilities.DegreesToRadians(degreesX), TransformUtilities.DegreesToRadians(degreesY), origin);

    /// <summary>Appends a skew (shear) about the centre of the source rectangle.</summary>
    public AffineTransformBuilder AppendSkewRadians(float radiansX, float radiansY)
        => this.Append(rect => Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.Center(rect)));

    /// <summary>Appends a skew (shear) about <paramref name="origin"/>.</summary>
    public AffineTransformBuilder AppendSkewRadians(float radiansX, float radiansY, PointF origin)
        => this.Append(_ => Matrix3x2.CreateSkew(radiansX, radiansY, TransformUtilities.ToVector2(origin)));

    // ----- Translation / raw matrices -----

    /// <summary>Prepends a translation.</summary>
    public AffineTransformBuilder PrependTranslation(PointF position)
        => this.Prepend(_ => Matrix3x2.CreateTranslation(position.X, position.Y));

    /// <summary>Appends a translation.</summary>
    public AffineTransformBuilder AppendTranslation(PointF position)
        => this.Append(_ => Matrix3x2.CreateTranslation(position.X, position.Y));

    /// <summary>Prepends an arbitrary matrix.</summary>
    public AffineTransformBuilder PrependMatrix(Matrix3x2 matrix)
    {
        EnsureFinite(matrix);
        return this.Prepend(_ => matrix);
    }

    /// <summary>Appends an arbitrary matrix.</summary>
    public AffineTransformBuilder AppendMatrix(Matrix3x2 matrix)
    {
        EnsureFinite(matrix);
        return this.Append(_ => matrix);
    }

    /// <summary>Removes every operation.</summary>
    public void Clear() => this.operations.Clear();

    // ----- Building -----

    /// <summary>
    /// Builds the matrix mapping source coordinates to destination coordinates for the given source rectangle,
    /// including a final translation so the transformed bounding box (<see cref="GetTransformedBoundingBox"/>)
    /// starts at the origin; the matching canvas size is <see cref="GetTransformedSize"/>.
    /// </summary>
    public Matrix3x2 BuildMatrix(Rectangle sourceRectangle)
    {
        Matrix3x2 matrix = this.Compose(sourceRectangle);
        RectangleF bounds = TransformUtilities.GetBoundingBox(sourceRectangle, matrix);
        return matrix * Matrix3x2.CreateTranslation(-bounds.X, -bounds.Y);
    }

    /// <summary>Builds the matrix for a source of the given size located at the origin.</summary>
    public Matrix3x2 BuildMatrix(Size sourceSize) => this.BuildMatrix(new Rectangle(Point.Empty, sourceSize));

    /// <summary>The bounding box of the source rectangle's corners after all operations (before the final translation to the origin).</summary>
    public RectangleF GetTransformedBoundingBox(Rectangle sourceRectangle)
        => TransformUtilities.GetBoundingBox(sourceRectangle, this.Compose(sourceRectangle));

    /// <summary>The canvas size needed to hold the transformed source: the bounding box rounded up to whole pixels.</summary>
    public Size GetTransformedSize(Rectangle sourceRectangle)
        => TransformUtilities.CeilingSize(this.GetTransformedBoundingBox(sourceRectangle).Size);

    private Matrix3x2 Compose(Rectangle sourceRectangle)
    {
        ValidateRectangle(sourceRectangle);
        Matrix3x2 matrix = Matrix3x2.Identity;
        foreach (Func<Rectangle, Matrix3x2> operation in this.operations)
        {
            matrix *= operation(sourceRectangle);
        }

        return matrix;
    }

    private AffineTransformBuilder Prepend(Func<Rectangle, Matrix3x2> operation)
    {
        this.operations.Insert(0, operation);
        return this;
    }

    private AffineTransformBuilder Append(Func<Rectangle, Matrix3x2> operation)
    {
        this.operations.Add(operation);
        return this;
    }

    internal static void ValidateRectangle(Rectangle sourceRectangle)
    {
        if (sourceRectangle.Width <= 0 || sourceRectangle.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRectangle), sourceRectangle, "The source rectangle must have a positive width and height.");
        }
    }

    private static void EnsureFinite(Matrix3x2 matrix)
    {
        if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M21)
            || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32))
        {
            throw new ArgumentException("The matrix contains non-finite values.", nameof(matrix));
        }
    }
}
