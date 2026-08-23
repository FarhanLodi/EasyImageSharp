using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Affine/projective transforms, rotate-with-sampler, skew, entropy crop, and the Pillow warp references.</summary>
public class TransformTests
{
    public static IEnumerable<object[]> AffineFixtures => GeometryTestSupport.FixtureNames("affine");

    public static IEnumerable<object[]> PerspectiveFixtures => GeometryTestSupport.FixtureNames("perspective");

    // ----- Guards: the legacy Rotate(float) output must not change -----

    [Theory]
    [InlineData("rgb24_rotate30", 0xC8011C6823C587C2UL)]
    [InlineData("rgb24_rotate_m17_5", 0x06ED83DBBDD7C416UL)]
    [InlineData("rgb24_rotate90", 0x3BC6A53E795B621DUL)]
    [InlineData("rgb24_rotate45", 0xEE6E3A9B223362B0UL)]
    [InlineData("rgb24_rotate45_45", 0x454D0B994BA4CB82UL)]
    [InlineData("rgba32_rotate30", 0x76122E2A5DDE367BUL)]
    [InlineData("rgba32_rotate200", 0xAC3FFDC939C8668DUL)]
    [InlineData("rgb24_deskew_noop", 0x4F01BE8B11FE7831UL)]
    public void LegacyRotate_ChecksumCapturedBeforeGeometryWork_IsUnchanged(string scenario, ulong expected)
    {
        // Captured from the pre-transform build (commit 2f42424) with the same synthetic images.
        ulong actual = scenario switch
        {
            "rgb24_rotate30" => Checksum(TestImages.Gradient(100, 40), ctx => ctx.Rotate(30f)),
            "rgb24_rotate_m17_5" => Checksum(TestImages.Gradient(100, 40), ctx => ctx.Rotate(-17.5f)),
            "rgb24_rotate90" => Checksum(TestImages.Gradient(64, 48), ctx => ctx.Rotate(90f)),
            "rgb24_rotate45" => Checksum(TestImages.Gradient(64, 48), ctx => ctx.Rotate(45f)),
            "rgb24_rotate45_45" => Checksum(TestImages.Gradient(64, 48), ctx => ctx.Rotate(45f).Rotate(45f)),
            "rgba32_rotate30" => Checksum(TestImages.AlphaGradient(50, 30), ctx => ctx.Rotate(30f)),
            "rgba32_rotate200" => Checksum(TestImages.AlphaGradient(50, 30), ctx => ctx.Rotate(200f)),
            "rgb24_deskew_noop" => Checksum(TestImages.Gradient(120, 80), ctx => ctx.Deskew(15f)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Equal(expected, actual);
    }

    // ----- Rotate with a sampler -----

    [Theory]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(270f)]
    [InlineData(-90f)]
    [InlineData(450f)]
    public void RotateWithSampler_RightAngles_UseLosslessPath(float degrees)
    {
        using Image<Rgb24> source = TestImages.Gradient(37, 23);
        using Image<Rgb24> lossless = source.Clone(ctx => ctx.Rotate(degrees));
        using Image<Rgb24> sampled = source.Clone(ctx => ctx.Rotate(degrees, KnownResamplers.Lanczos3));
        Assert.Equal(lossless.Size, sampled.Size);
        Assert.Equal(0, TestImages.AveragePixelDifference(lossless, sampled));
    }

    [Fact]
    public void RotateWithSampler_ZeroDegrees_IsNoOp()
    {
        using Image<Rgb24> source = TestImages.Gradient(20, 10);
        using Image<Rgb24> rotated = source.Clone(ctx => ctx.Rotate(360f, KnownResamplers.Bicubic));
        Assert.Equal(0, TestImages.AveragePixelDifference(source, rotated));
    }

    [Fact]
    public void RotateWithSampler_ExpandsCanvasLikeLegacyRotate()
    {
        using Image<Rgb24> source = TestImages.Gradient(100, 40);
        using Image<Rgb24> legacy = source.Clone(ctx => ctx.Rotate(30f));
        using Image<Rgb24> sampled = source.Clone(ctx => ctx.Rotate(30f, KnownResamplers.Bicubic));
        Assert.Equal(legacy.Size, sampled.Size);

        // Same geometry: the interiors agree closely even though the kernels differ.
        double psnr = GeometryTestSupport.Psnr(legacy.CloneAs<Rgba32>(), sampled.CloneAs<Rgba32>(), (x, y) => IsInterior(legacy, x, y, 4));
        Assert.True(psnr > 30, $"PSNR legacy vs sampled rotate = {psnr:F2} dB");
    }

    [Theory]
    [InlineData("NearestNeighbor")]
    [InlineData("Triangle")]
    [InlineData("Bicubic")]
    [InlineData("Lanczos3")]
    public void RightAngleRotation_ThroughGeneralWarp_IsExact(string sampler)
    {
        // The general engine (no fast path) must reproduce the lossless rotation exactly at 90/180/270 degrees.
        using Image<Rgb24> source = TestImages.Gradient(41, 27);
        foreach (float degrees in new[] { 90f, 180f, 270f })
        {
            using Image<Rgb24> expected = source.Clone(ctx => ctx.Rotate(degrees));
            var builder = new AffineTransformBuilder().AppendRotationDegrees(degrees);
            using Image<Rgb24> actual = source.Clone(ctx => ctx.Transform(builder, GeometryTestSupport.Resampler(sampler)));
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(0, TestImages.AveragePixelDifference(expected, actual));
        }
    }

    [Fact]
    public void FlipThroughAffineScale_IsExact()
    {
        using Image<Rgb24> source = TestImages.Gradient(33, 21);
        using Image<Rgb24> flipped = source.Clone(ctx => ctx.Flip(FlipMode.Horizontal));
        var builder = new AffineTransformBuilder().AppendScale(-1f, 1f);
        using Image<Rgb24> nearest = source.Clone(ctx => ctx.Transform(builder, KnownResamplers.NearestNeighbor));
        using Image<Rgb24> bicubic = source.Clone(ctx => ctx.Transform(builder, KnownResamplers.Bicubic));
        Assert.Equal(source.Size, nearest.Size);
        Assert.Equal(0, TestImages.AveragePixelDifference(flipped, nearest));
        Assert.Equal(0, TestImages.AveragePixelDifference(flipped, bicubic));
    }

    [Fact]
    public void RotateThenRotateBack_RestoresInterior()
    {
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        using Image<Rgba32> rotated = source.Clone(ctx => ctx.Rotate(23f, KnownResamplers.Bicubic));
        using Image<Rgba32> restored = rotated.Clone(ctx => ctx.Rotate(-23f, KnownResamplers.Bicubic));

        // The double-expanded canvas is centred on the original; crop it back out.
        int offsetX = (restored.Width - source.Width) / 2;
        int offsetY = (restored.Height - source.Height) / 2;
        using Image<Rgba32> centre = restored.Clone(ctx => ctx.Crop(new Rectangle(offsetX, offsetY, source.Width, source.Height)));
        double psnr = GeometryTestSupport.Psnr(source, centre, (x, y) => x >= 6 && y >= 6 && x < source.Width - 6 && y < source.Height - 6);
        Assert.True(psnr > 35, $"PSNR after rotate/unrotate = {psnr:F2} dB");
    }

    [Fact]
    public void RotateRgba_EdgesFadeInAlphaWithoutDarkFringe()
    {
        using var source = new Image<Rgba32>(40, 24, new Rgba32(255, 0, 0, 255));
        using Image<Rgba32> rotated = source.Clone(ctx => ctx.Rotate(37f, KnownResamplers.Bicubic));
        int partial = 0;
        for (int y = 0; y < rotated.Height; y++)
        {
            for (int x = 0; x < rotated.Width; x++)
            {
                Rgba32 p = rotated[x, y];
                if (p.A == 0)
                {
                    Assert.Equal(new Rgba32(0, 0, 0, 0), p);
                    continue;
                }

                if (p.A < 255)
                {
                    partial++;
                }

                Assert.True(p.R >= 253 && p.G == 0 && p.B == 0, $"pixel ({x},{y}) = {p} has a dark fringe");
            }
        }

        Assert.True(partial > 20, "edges should be anti-aliased (partial alpha)");
    }

    [Fact]
    public void RotateRgb_UsesFillColorForCorners()
    {
        using Image<Rgb24> source = TestImages.Gradient(50, 30);
        using Image<Rgb24> white = source.Clone(ctx => ctx.Rotate(30f, KnownResamplers.Bicubic, Color.White));
        using Image<Rgb24> transparent = source.Clone(ctx => ctx.Rotate(30f, KnownResamplers.Bicubic));
        Assert.Equal(new Rgb24(255, 255, 255), white[0, 0]);
        Assert.Equal(new Rgb24(0, 0, 0), transparent[0, 0]);
    }

    // ----- Skew -----

    [Fact]
    public void Skew_ShearsAboutTheCentre()
    {
        using var source = new Image<Rgba32>(40, 20, new Rgba32(0, 0, 255, 255));
        using Image<Rgba32> skewed = source.Clone(ctx => ctx.Skew(20f, 0f));
        int expectedWidth = (int)Math.Ceiling(40 + (20 * Math.Tan(20 * Math.PI / 180)) - 1e-3);
        Assert.Equal(expectedWidth, skewed.Width);
        Assert.Equal(20, skewed.Height);

        int FirstCovered(int y)
        {
            for (int x = 0; x < skewed.Width; x++)
            {
                if (skewed[x, y].A >= 128)
                {
                    return x;
                }
            }

            return -1;
        }

        Assert.True(FirstCovered(0) < FirstCovered(skewed.Height - 1), "positive X skew shifts lower rows to the right");
        Assert.InRange(FirstCovered(0), 0, 1);
        Assert.InRange(FirstCovered(skewed.Height - 1), 6, 8);
        Assert.Equal(new Rgba32(0, 0, 255, 255), skewed[skewed.Width / 2, skewed.Height / 2]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), skewed[skewed.Width - 1, 0]);
    }

    [Fact]
    public void Skew_ZeroAngles_IsNoOp()
    {
        using Image<Rgb24> source = TestImages.Gradient(20, 10);
        using Image<Rgb24> skewed = source.Clone(ctx => ctx.Skew(0f, 0f, KnownResamplers.Bicubic));
        Assert.Equal(0, TestImages.AveragePixelDifference(source, skewed));
    }

    // ----- Builders -----

    [Fact]
    public void AffineBuilder_BuildMatrix_MatchesSystemNumericsComposition()
    {
        var rect = new Rectangle(0, 0, 80, 50);
        var builder = new AffineTransformBuilder()
            .AppendRotationDegrees(30f)
            .AppendScale(new SizeF(2f, 0.5f))
            .AppendTranslation(new PointF(7f, -3f))
            .PrependSkewDegrees(10f, 0f, new PointF(0f, 0f));

        Matrix3x2 expected = Matrix3x2.CreateSkew(10f * MathF.PI / 180f, 0f, Vector2.Zero)
            * Matrix3x2.CreateRotation(30f * MathF.PI / 180f, new Vector2(40f, 25f))
            * Matrix3x2.CreateScale(2f, 0.5f)
            * Matrix3x2.CreateTranslation(7f, -3f);
        RectangleF bounds = builder.GetTransformedBoundingBox(rect);
        expected *= Matrix3x2.CreateTranslation(-bounds.X, -bounds.Y);

        Matrix3x2 actual = builder.BuildMatrix(rect);
        AssertMatrixEqual(expected, actual);
        Assert.Equal(4, builder.Count);

        // The bounding box is the hull of the four transformed corners.
        Vector2[] corners = new[] { new Vector2(0, 0), new Vector2(80, 0), new Vector2(80, 50), new Vector2(0, 50) }
            .Select(c => Vector2.Transform(c, expected)).ToArray();
        Assert.Equal(0f, corners.Min(c => c.X), 3);
        Assert.Equal(0f, corners.Min(c => c.Y), 3);
        Assert.Equal(bounds.Width, corners.Max(c => c.X), 3);
        Assert.Equal(bounds.Height, corners.Max(c => c.Y), 3);
    }

    [Fact]
    public void AffineBuilder_PrependAppend_OrderIsRespected()
    {
        var rect = new Rectangle(0, 0, 10, 10);
        Matrix3x2 a = Matrix3x2.CreateScale(2f);
        Matrix3x2 b = Matrix3x2.CreateTranslation(5f, 0f);

        Matrix3x2 appendOrder = new AffineTransformBuilder().AppendMatrix(a).AppendMatrix(b).BuildMatrix(rect);
        Matrix3x2 prependOrder = new AffineTransformBuilder().AppendMatrix(b).PrependMatrix(a).BuildMatrix(rect);
        Matrix3x2 reversed = new AffineTransformBuilder().AppendMatrix(b).AppendMatrix(a).BuildMatrix(rect);
        AssertMatrixEqual(appendOrder, prependOrder);

        // Scale-then-translate versus translate-then-scale differ before the bounding-box normalisation;
        // compare the raw compositions through the bounding boxes instead.
        RectangleF boundsAb = new AffineTransformBuilder().AppendMatrix(a).AppendMatrix(b).GetTransformedBoundingBox(rect);
        RectangleF boundsBa = new AffineTransformBuilder().AppendMatrix(b).AppendMatrix(a).GetTransformedBoundingBox(rect);
        Assert.Equal(5f, boundsAb.X, 4);
        Assert.Equal(10f, boundsBa.X, 4);
        AssertMatrixEqual(appendOrder, reversed); // identical after normalisation: both are 2x scale at the origin
    }

    [Fact]
    public void AffineBuilder_RightAngle_SizeSnapsToWholePixels()
    {
        var rect = new Rectangle(0, 0, 100, 40);
        var builder = new AffineTransformBuilder().AppendRotationDegrees(90f);
        Assert.Equal(new Size(40, 100), builder.GetTransformedSize(rect));
        Assert.Equal(new Size(200, 80), new AffineTransformBuilder().AppendScale(2f).GetTransformedSize(rect));
        Assert.Equal(new Size(1, 1), new AffineTransformBuilder().AppendScale(0.001f).GetTransformedSize(rect));
    }

    [Fact]
    public void AffineBuilder_InvalidInput_Throws()
    {
        var builder = new AffineTransformBuilder();
        Assert.Throws<ArgumentException>(() => builder.AppendMatrix(new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.BuildMatrix(new Rectangle(0, 0, 0, 5)));
        Assert.Throws<ArgumentNullException>(() => TestImages.Gradient(4, 4).Mutate(ctx => ctx.Transform((AffineTransformBuilder)null!)));

        using Image<Rgb24> image = TestImages.Gradient(8, 8);
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.Transform(new AffineTransformBuilder().AppendScale(0f))));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.Transform(new Rectangle(0, 0, 4, 4), Matrix3x2.Identity, new Size(0, 4), KnownResamplers.Bicubic)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.Transform(new Rectangle(4, 4, 8, 8), Matrix3x2.Identity, new Size(4, 4), KnownResamplers.Bicubic)));
    }

    [Fact]
    public void ProjectiveBuilder_QuadDistortion_MapsCornersOntoQuad()
    {
        var rect = new Rectangle(0, 0, 60, 40);
        var tl = new PointF(5f, 3f);
        var tr = new PointF(58f, 8f);
        var br = new PointF(52f, 39f);
        var bl = new PointF(1f, 34f);
        var builder = new ProjectiveTransformBuilder().AppendQuadDistortion(tl, tr, br, bl);
        RectangleF bounds = builder.GetTransformedBoundingBox(rect);
        Assert.Equal(1f, bounds.X, 3);
        Assert.Equal(3f, bounds.Y, 3);
        Assert.Equal(57f, bounds.Width, 3);
        Assert.Equal(36f, bounds.Height, 3);

        Matrix4x4 matrix = builder.BuildMatrix(rect);
        AssertProjects(matrix, new Vector2(0, 0), new Vector2(tl.X - 1f, tl.Y - 3f));
        AssertProjects(matrix, new Vector2(60, 0), new Vector2(tr.X - 1f, tr.Y - 3f));
        AssertProjects(matrix, new Vector2(60, 40), new Vector2(br.X - 1f, br.Y - 3f));
        AssertProjects(matrix, new Vector2(0, 40), new Vector2(bl.X - 1f, bl.Y - 3f));

        // The centre of the rectangle does NOT map to the centre of the quad for a true perspective (w varies).
        Assert.True(Vector4.Transform(new Vector4(60, 40, 0, 1), matrix).W != Vector4.Transform(new Vector4(0, 0, 0, 1), matrix).W);
    }

    [Fact]
    public void ProjectiveBuilder_AffineOperations_MatchAffineBuilder()
    {
        var rect = new Rectangle(0, 0, 70, 30);
        Matrix3x2 affine = new AffineTransformBuilder().AppendRotationDegrees(-15f).AppendScale(1.5f).AppendSkewDegrees(0f, 8f).BuildMatrix(rect);
        Matrix4x4 projective = new ProjectiveTransformBuilder().AppendRotationDegrees(-15f).AppendScale(1.5f).AppendSkewDegrees(0f, 8f).BuildMatrix(rect);
        foreach (Vector2 p in new[] { new Vector2(0, 0), new Vector2(70, 0), new Vector2(35.5f, 12.25f), new Vector2(0, 30) })
        {
            AssertProjects(projective, p, Vector2.Transform(p, affine));
        }

        Assert.Equal(1f, projective.M33);
        Assert.Equal(1f, projective.M44);
        Assert.Equal(0f, projective.M14);
        Assert.Equal(0f, projective.M24);
    }

    [Fact]
    public void ProjectiveBuilder_NoOperations_IsIdentityAndCopiesPixels()
    {
        using Image<Rgb24> source = TestImages.Gradient(31, 17);
        var builder = new ProjectiveTransformBuilder();
        Assert.Equal(Matrix4x4.Identity, builder.BuildMatrix(new Rectangle(0, 0, 31, 17)));
        using Image<Rgb24> copy = source.Clone(ctx => ctx.Transform(builder, KnownResamplers.Bicubic));
        Assert.Equal(source.Size, copy.Size);
        Assert.Equal(0, TestImages.AveragePixelDifference(source, copy));
    }

    [Fact]
    public void ProjectiveBuilder_Taper_ShrinksTheTaperedSide()
    {
        using var source = new Image<Rgba32>(60, 40, new Rgba32(10, 200, 30, 255));
        var builder = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Right, TaperCorner.Both, 0.5f);
        using Image<Rgba32> tapered = source.Clone(ctx => ctx.Transform(builder, KnownResamplers.Bicubic));
        Assert.Equal(new Size(60, 40), tapered.Size);

        static int OpaqueRows(Image<Rgba32> image, int x, byte minAlpha)
        {
            int count = 0;
            for (int y = 0; y < image.Height; y++)
            {
                if (image[x, y].A >= minAlpha)
                {
                    count++;
                }
            }

            return count;
        }

        // The left edge keeps its full height (the two corner pixels are clipped by the sloping top/bottom edges
        // and therefore only partially covered); the right edge shrinks to half.
        Assert.Equal(40, OpaqueRows(tapered, 0, 1));
        Assert.InRange(OpaqueRows(tapered, 0, 255), 38, 40);
        Assert.InRange(OpaqueRows(tapered, tapered.Width - 1, 255), 17, 21);
        Assert.Equal(new Rgba32(0, 0, 0, 0), tapered[tapered.Width - 1, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), tapered[tapered.Width - 1, 39]);

        // Bounding box: only the tapered corners move.
        RectangleF bounds = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.LeftOrTop, 0.25f)
            .GetTransformedBoundingBox(new Rectangle(0, 0, 60, 40));
        Assert.Equal(new RectangleF(0, 0, 60, 40), bounds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectiveTransformBuilder().PrependTaper(TaperSide.Left, TaperCorner.Both, 1.5f));
    }

    [Fact]
    public void ProjectiveBuilder_DegenerateQuad_Throws()
    {
        var builder = new ProjectiveTransformBuilder().AppendQuadDistortion(new PointF(0, 0), new PointF(10, 0), new PointF(20, 0), new PointF(30, 0));
        Assert.Throws<ArgumentException>(() => builder.BuildMatrix(new Rectangle(0, 0, 10, 10)));
    }

    // ----- Invertibility -----

    [Fact]
    public void Affine_TransformThenInverse_RestoresInterior()
    {
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var builder = new AffineTransformBuilder().AppendRotationDegrees(17f).AppendScale(1.3f, 0.9f).AppendSkewDegrees(5f, -3f);
        Matrix3x2 forward = builder.BuildMatrix(rect);
        Size size = builder.GetTransformedSize(rect);
        Assert.True(Matrix3x2.Invert(forward, out Matrix3x2 inverse));

        using Image<Rgba32> warped = source.Clone(ctx => ctx.Transform(rect, forward, size, KnownResamplers.Bicubic));
        using Image<Rgba32> restored = warped.Clone(ctx => ctx.Transform(new Rectangle(0, 0, size.Width, size.Height), inverse, source.Size, KnownResamplers.Bicubic));
        Assert.Equal(source.Size, restored.Size);
        double psnr = GeometryTestSupport.Psnr(source, restored, (x, y) => x >= 5 && y >= 5 && x < source.Width - 5 && y < source.Height - 5);
        Assert.True(psnr > 35, $"PSNR after affine round trip = {psnr:F2} dB");
    }

    [Fact]
    public void Projective_TransformThenInverse_RestoresInterior()
    {
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var builder = new ProjectiveTransformBuilder().AppendQuadDistortion(new PointF(6, 4), new PointF(93, 9), new PointF(88, 61), new PointF(2, 55));
        Matrix4x4 forward = builder.BuildMatrix(rect);
        Size size = builder.GetTransformedSize(rect);
        Assert.True(Matrix4x4.Invert(forward, out Matrix4x4 inverse));

        using Image<Rgba32> warped = source.Clone(ctx => ctx.Transform(rect, forward, size, KnownResamplers.Bicubic));
        using Image<Rgba32> restored = warped.Clone(ctx => ctx.Transform(new Rectangle(0, 0, size.Width, size.Height), inverse, source.Size, KnownResamplers.Bicubic));
        Assert.Equal(source.Size, restored.Size);
        double psnr = GeometryTestSupport.Psnr(source, restored, (x, y) => x >= 5 && y >= 5 && x < source.Width - 5 && y < source.Height - 5);
        Assert.True(psnr > 32, $"PSNR after projective round trip = {psnr:F2} dB");
    }

    [Fact]
    public void Projective_LowLevelWarp_IdentityIsExact()
    {
        // Alpha stays >= 5 so premultiplication is reversible; fully transparent pixels legitimately lose their colour.
        using var rgba = new Image<Rgba32>(29, 19);
        for (int y = 0; y < 19; y++)
        {
            for (int x = 0; x < 29; x++)
            {
                rgba[x, y] = new Rgba32((byte)(x * 8), (byte)(y * 13), (byte)((x * y) % 256), (byte)(255 - (y * 250 / 18)));
            }
        }

        using Image<Rgb24> rgb = TestImages.Gradient(29, 19);
        ImageFrame<Rgba32> warpedRgba = TransformOps.WarpProjective(rgba.Frames.RootFrame, Matrix4x4.Identity, rgba.Size, KnownResamplers.Lanczos3, Color.Transparent);
        ImageFrame<Rgb24> warpedRgb = TransformOps.WarpProjective(rgb.Frames.RootFrame, Matrix4x4.Identity, rgb.Size, KnownResamplers.Bicubic, Color.Transparent);
        for (int y = 0; y < 19; y++)
        {
            for (int x = 0; x < 29; x++)
            {
                Assert.Equal(rgba[x, y], warpedRgba[x, y]);
                Assert.Equal(rgb[x, y], warpedRgb[x, y]);
            }
        }
    }

    [Fact]
    public void Transform_SourceRectangle_LimitsSampledRegion()
    {
        using Image<Rgb24> source = TestImages.Gradient(40, 30);
        var region = new Rectangle(10, 5, 20, 15);
        using Image<Rgb24> cropped = source.Clone(ctx => ctx.Crop(region));

        // Identity matrix with a sub-rectangle: the output canvas shows the region at its original location.
        using Image<Rgb24> viaTransform = source.Clone(ctx => ctx.Transform(region, Matrix3x2.Identity, new Size(40, 30), KnownResamplers.NearestNeighbor, Color.White));
        Assert.Equal(new Rgb24(255, 255, 255), viaTransform[0, 0]);
        Assert.Equal(new Rgb24(255, 255, 255), viaTransform[35, 25]);
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                Assert.Equal(cropped[x, y], viaTransform[region.X + x, region.Y + y]);
            }
        }
    }

    [Fact]
    public void Transform_MultiFrameImage_AppliesToEveryFrame()
    {
        using var image = new Image<Rgba32>(30, 20, new Rgba32(255, 0, 0, 255));
        image.Frames.AddFrame(new Image<Rgba32>(30, 20, new Rgba32(0, 255, 0, 255)).Frames.RootFrame);
        image.Mutate(ctx => ctx.Rotate(30f, KnownResamplers.Bicubic));
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(image.Frames[0].Width, image.Frames[1].Width);
        Assert.Equal(image.Frames[0].Height, image.Frames[1].Height);
        Assert.Equal(new Rgba32(255, 0, 0, 255), image.Frames[0][image.Width / 2, image.Height / 2]);
        Assert.Equal(new Rgba32(0, 255, 0, 255), image.Frames[1][image.Width / 2, image.Height / 2]);
    }

    // ----- Pillow references -----

    [Theory]
    [MemberData(nameof(AffineFixtures))]
    public void Affine_MatchesPillowReference(string name)
    {
        GeometryTestSupport.Entry entry = GeometryTestSupport.GetEntry(name);
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        using Image<Rgba32> expected = GeometryTestSupport.LoadRgba(entry.Name, entry.Width, entry.Height);
        Matrix3x2 inverse = GeometryTestSupport.InverseAffine(entry.Coeffs!);
        Assert.True(Matrix3x2.Invert(inverse, out Matrix3x2 forward));
        IResampler sampler = GeometryTestSupport.Resampler(entry.Filter);
        Color fill = GeometryTestSupport.Fill(entry);

        using Image<Rgba32> actual = source.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, source.Width, source.Height), forward, new Size(entry.Width, entry.Height), sampler, fill));
        Assert.Equal(expected.Size, actual.Size);

        if (sampler is NearestNeighborResampler)
        {
            int mismatches = GeometryTestSupport.CountMismatches(expected, actual);
            Assert.True(mismatches == 0, $"{name}: {mismatches} pixels differ from Pillow's nearest-neighbour result.");
            return;
        }

        double margin = sampler.Radius + 1.5;
        double psnr = GeometryTestSupport.Psnr(expected, actual, (x, y) =>
        {
            (double sx, double sy) = GeometryTestSupport.MapAffine(entry.Coeffs!, x + 0.5, y + 0.5);
            return sx >= margin && sy >= margin && sx <= source.Width - margin && sy <= source.Height - margin;
        });
        Assert.True(psnr > 35, $"{name}: interior PSNR {psnr:F2} dB vs Pillow (expected > 35 dB).");
    }

    [Theory]
    [MemberData(nameof(PerspectiveFixtures))]
    public void Perspective_MatchesPillowReference(string name)
    {
        GeometryTestSupport.Entry entry = GeometryTestSupport.GetEntry(name);
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        using Image<Rgba32> expected = GeometryTestSupport.LoadRgba(entry.Name, entry.Width, entry.Height);
        Matrix4x4 inverse = GeometryTestSupport.InverseProjective(entry.Coeffs!);
        Assert.True(Matrix4x4.Invert(inverse, out Matrix4x4 forward));
        IResampler sampler = GeometryTestSupport.Resampler(entry.Filter);
        Color fill = GeometryTestSupport.Fill(entry);

        using Image<Rgba32> viaPublicApi = source.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, source.Width, source.Height), forward, new Size(entry.Width, entry.Height), sampler, fill));
        ImageFrame<Rgba32> viaLowLevel = TransformOps.WarpProjective(source.Frames.RootFrame, inverse, new Size(entry.Width, entry.Height), sampler, fill);
        Assert.Equal(expected.Size, viaPublicApi.Size);

        if (sampler is NearestNeighborResampler)
        {
            int total = entry.Width * entry.Height;
            int mismatches = GeometryTestSupport.CountMismatches(expected, viaPublicApi);
            Assert.True(mismatches <= total * 0.005, $"{name}: {mismatches}/{total} pixels differ from Pillow's nearest-neighbour result.");
            return;
        }

        double margin = sampler.Radius + 1.5;
        Func<int, int, bool> interior = (x, y) =>
        {
            (double sx, double sy) = GeometryTestSupport.MapProjective(entry.Coeffs!, x + 0.5, y + 0.5);
            return sx >= margin && sy >= margin && sx <= source.Width - margin && sy <= source.Height - margin;
        };
        double psnr = GeometryTestSupport.Psnr(expected, viaPublicApi, interior);
        Assert.True(psnr > 35, $"{name}: interior PSNR {psnr:F2} dB vs Pillow (expected > 35 dB).");

        using var lowLevel = new Image<Rgba32>(entry.Width, entry.Height);
        viaLowLevel.PixelSpan.CopyTo(lowLevel.Frames.RootFrame.PixelSpan);
        double psnrLowLevel = GeometryTestSupport.Psnr(viaPublicApi, lowLevel);
        Assert.True(psnrLowLevel > 50, $"{name}: public and low-level warps disagree ({psnrLowLevel:F2} dB).");
    }

    // ----- Entropy crop / crop overload -----

    [Fact]
    public void EntropyCrop_CropsToEdgeContent()
    {
        using var image = new Image<Rgb24>(100, 80, new Rgb24(255, 255, 255));
        for (int y = 10; y < 50; y++)
        {
            for (int x = 20; x < 60; x++)
            {
                image[x, y] = new Rgb24(0, 0, 0);
            }
        }

        image.Mutate(ctx => ctx.EntropyCrop());

        // Sobel responds one pixel either side of each edge: columns 19..60, rows 9..50.
        Assert.Equal(new Size(42, 42), image.Size);
        Assert.Equal(new Rgb24(255, 255, 255), image[0, 0]);
        Assert.Equal(new Rgb24(0, 0, 0), image[1, 1]);
        Assert.Equal(new Rgb24(0, 0, 0), image[40, 40]);
        Assert.Equal(new Rgb24(255, 255, 255), image[41, 41]);
    }

    [Fact]
    public void EntropyCrop_ThresholdSelectsContrast()
    {
        // A faint square (contrast 60) and a strong square (contrast 255): a high threshold keeps only the strong one.
        using var image = new Image<L8>(120, 60, new L8(255));
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                image[x, y] = new L8(195);
            }

            for (int x = 80; x < 100; x++)
            {
                image[x, y] = new L8(0);
            }
        }

        using Image<L8> strongOnly = image.Clone(ctx => ctx.EntropyCrop(0.9f));
        using Image<L8> both = image.Clone(ctx => ctx.EntropyCrop(0.1f));
        Assert.Equal(new Size(22, 22), strongOnly.Size);
        Assert.Equal(new Size(92, 22), both.Size);
        Assert.Equal(new L8(0), strongOnly[1, 1]);
    }

    [Fact]
    public void EntropyCrop_UniformImage_IsUnchanged()
    {
        using var image = new Image<Rgba32>(30, 20, new Rgba32(50, 60, 70, 255));
        image.Mutate(ctx => ctx.EntropyCrop());
        Assert.Equal(new Size(30, 20), image.Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.EntropyCrop(1.5f)));
    }

    [Fact]
    public void Crop_WidthHeight_TakesTopLeftRegion()
    {
        using Image<Rgb24> source = TestImages.Gradient(40, 30);
        using Image<Rgb24> cropped = source.Clone(ctx => ctx.Crop(12, 7));
        Assert.Equal(new Size(12, 7), cropped.Size);
        Assert.Equal(source[11, 6], cropped[11, 6]);
        Assert.Equal(source[0, 0], cropped[0, 0]);
    }

    // ----- Helpers -----

    private static ulong Checksum<TPixel>(Image<TPixel> image, Action<IImageProcessingContext> operation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using (image)
        {
            using Image<TPixel> result = image.Clone(operation);
            return GeometryTestSupport.Checksum(result);
        }
    }

    private static bool IsInterior(Image<Rgb24> image, int x, int y, int margin)
        => x >= margin && y >= margin && x < image.Width - margin && y < image.Height - margin;

    private static void AssertMatrixEqual(Matrix3x2 expected, Matrix3x2 actual)
    {
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M12, actual.M12, 4);
        Assert.Equal(expected.M21, actual.M21, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
        Assert.Equal(expected.M31, actual.M31, 3);
        Assert.Equal(expected.M32, actual.M32, 3);
    }

    private static void AssertProjects(Matrix4x4 matrix, Vector2 point, Vector2 expected)
    {
        Vector4 h = Vector4.Transform(new Vector4(point.X, point.Y, 0f, 1f), matrix);
        Assert.True(h.W > 0f);
        Assert.Equal(expected.X, h.X / h.W, 3);
        Assert.Equal(expected.Y, h.Y / h.W, 3);
    }
}

/// <summary>Shared helpers for the geometry test files: fixture access, resampler lookup, PSNR, checksums.</summary>
internal static class GeometryTestSupport
{
    public static readonly (string Name, IResampler Sampler)[] AllResamplers =
    {
        ("NearestNeighbor", KnownResamplers.NearestNeighbor),
        ("Box", KnownResamplers.Box),
        ("Triangle", KnownResamplers.Triangle),
        ("Hermite", KnownResamplers.Hermite),
        ("Bicubic", KnownResamplers.Bicubic),
        ("CatmullRom", KnownResamplers.CatmullRom),
        ("MitchellNetravali", KnownResamplers.MitchellNetravali),
        ("Robidoux", KnownResamplers.Robidoux),
        ("RobidouxSharp", KnownResamplers.RobidouxSharp),
        ("Spline", KnownResamplers.Spline),
        ("Lanczos2", KnownResamplers.Lanczos2),
        ("Lanczos3", KnownResamplers.Lanczos3),
        ("Lanczos5", KnownResamplers.Lanczos5),
        ("Lanczos8", KnownResamplers.Lanczos8),
        ("Welch", KnownResamplers.Welch),
    };

    private static Entry[]? manifest;

    public static IResampler Resampler(string name) => name.ToLowerInvariant() switch
    {
        "nearest" => KnownResamplers.NearestNeighbor,
        "bilinear" => KnownResamplers.Bilinear,
        "lanczos" => KnownResamplers.Lanczos3,
        _ => AllResamplers.Single(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).Sampler,
    };

    public static IEnumerable<object[]> FixtureNames(string kind)
    {
        if (!FixturePath.Exists("geometry/manifest.json"))
        {
            yield return new object[] { "(manifest missing: run Fixtures/generate.py)" };
            yield break;
        }

        foreach (Entry entry in Manifest().Where(e => e.Kind == kind))
        {
            yield return new object[] { entry.Name };
        }
    }

    public static Entry GetEntry(string name)
        => Manifest().SingleOrDefault(e => e.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"geometry fixture '{name}' is missing; run Fixtures/generate.py.");

    public static Image<Rgba32> LoadSource() => LoadRgba("source", 96, 64);

    public static Image<Rgba32> LoadRgba(string name, int width, int height)
    {
        byte[] bytes = FixturePath.Read($"geometry/{name}.rgba");
        if (bytes.Length != width * height * 4)
        {
            throw new Xunit.Sdk.XunitException($"geometry/{name}.rgba has {bytes.Length} bytes, expected {width * height * 4}.");
        }

        var image = new Image<Rgba32>(width, height);
        MemoryMarshal.Cast<byte, Rgba32>(bytes).CopyTo(image.Frames.RootFrame.PixelSpan);
        return image;
    }

    public static Color Fill(Entry entry)
        => entry.FillColor is { Length: 4 } f ? new Color((byte)f[0], (byte)f[1], (byte)f[2], (byte)f[3]) : Color.Transparent;

    /// <summary>Pillow's inverse affine coefficients (a..f: xin = a x + b y + c, yin = d x + e y + f) as a row-vector matrix.</summary>
    public static Matrix3x2 InverseAffine(double[] c)
        => new((float)c[0], (float)c[3], (float)c[1], (float)c[4], (float)c[2], (float)c[5]);

    /// <summary>Pillow's inverse perspective coefficients (a..h) as the library's projective matrix layout.</summary>
    public static Matrix4x4 InverseProjective(double[] c) => new(
        (float)c[0], (float)c[3], 0f, (float)c[6],
        (float)c[1], (float)c[4], 0f, (float)c[7],
        0f, 0f, 1f, 0f,
        (float)c[2], (float)c[5], 0f, 1f);

    public static (double X, double Y) MapAffine(double[] c, double x, double y)
        => ((c[0] * x) + (c[1] * y) + c[2], (c[3] * x) + (c[4] * y) + c[5]);

    public static (double X, double Y) MapProjective(double[] c, double x, double y)
    {
        double w = (c[6] * x) + (c[7] * y) + 1;
        return (((c[0] * x) + (c[1] * y) + c[2]) / w, ((c[3] * x) + (c[4] * y) + c[5]) / w);
    }

    /// <summary>Peak signal-to-noise ratio over the RGB channels of the (optionally masked) pixels.</summary>
    public static double Psnr(Image<Rgba32> expected, Image<Rgba32> actual, Func<int, int, bool>? mask = null)
    {
        if (expected.Size != actual.Size)
        {
            throw new Xunit.Sdk.XunitException($"size mismatch: {expected.Size} vs {actual.Size}");
        }

        double sum = 0;
        long count = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (mask is not null && !mask(x, y))
                {
                    continue;
                }

                Rgba32 e = expected[x, y];
                Rgba32 a = actual[x, y];
                sum += ((e.R - a.R) * (e.R - a.R)) + ((e.G - a.G) * (e.G - a.G)) + ((e.B - a.B) * (e.B - a.B));
                count += 3;
            }
        }

        if (count == 0)
        {
            throw new Xunit.Sdk.XunitException("the comparison mask is empty");
        }

        double mse = sum / count;
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    public static int MaxAbsDifference(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        int max = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba32 e = expected[x, y];
                Rgba32 a = actual[x, y];
                max = Math.Max(max, Math.Max(Math.Abs(e.R - a.R), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.B - a.B))));
            }
        }

        return max;
    }

    public static int CountMismatches(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        int count = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected[x, y] != actual[x, y])
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>FNV-1a over the dimensions and every pixel's RGBA bytes.</summary>
    public static ulong Checksum<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ulong hash = 14695981039346656037UL;
        void Mix(byte b)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        Mix((byte)image.Width);
        Mix((byte)(image.Width >> 8));
        Mix((byte)image.Height);
        Mix((byte)(image.Height >> 8));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 p = image[x, y].ToRgba32();
                Mix(p.R);
                Mix(p.G);
                Mix(p.B);
                Mix(p.A);
            }
        }

        return hash;
    }

    private static Entry[] Manifest()
    {
        if (manifest is null)
        {
            string json = File.ReadAllText(FixturePath.Get("geometry/manifest.json"));
            manifest = JsonSerializer.Deserialize<Entry[]>(json) ?? Array.Empty<Entry>();
        }

        return manifest;
    }

    internal sealed class Entry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("filter")]
        public string Filter { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("coeffs")]
        public double[]? Coeffs { get; set; }

        [JsonPropertyName("fill")]
        public int[]? FillColor { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;
    }
}
