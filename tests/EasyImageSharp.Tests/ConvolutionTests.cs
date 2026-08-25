using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Convolution engine, box blur, edge detectors and the region overloads of the Gaussian filters.</summary>
public class ConvolutionTests
{
    // ----- Independent references (Pillow / numpy fixtures) -----

    [Fact]
    public void BoxBlur_MatchesPillowWithinOne()
    {
        using Image<Rgba32> src = EffectsTests.LoadFixture("src_rgb.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture("boxblur2_expected.png");
        using Image<Rgba32> actual = src.Clone(c => c.BoxBlur(2));
        Assert.True(EffectsTests.MaxDifference(expected, actual) <= 1, $"max diff {EffectsTests.MaxDifference(expected, actual)}");
    }

    [Fact]
    public void Convolve3x3_MatchesPillowKernelInInterior()
    {
        using Image<Rgba32> src = EffectsTests.LoadFixture("src_rgb.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture("kernel3_expected.png");
        // Pillow: sum(k * p) / scale + offset with kernel [0,-1,0,-1,6,-1,0,-1,0], scale 2.
        float[] kernel = [0f, -0.5f, 0f, -0.5f, 3f, -0.5f, 0f, -0.5f, 0f];
        using Image<Rgba32> actual = src.Clone(c => c.Convolve(kernel, 3, 3));
        // Pillow leaves the outer 1-pixel border untouched, so compare the interior only.
        int diff = EffectsTests.MaxDifference(expected, actual, includeAlpha: true, border: 1);
        Assert.True(diff <= 1, $"max diff {diff}");
    }

    [Fact]
    public void Convolve5x5_MatchesNumpyReferenceWithEdgeReplication()
    {
        using Image<Rgba32> src = EffectsTests.LoadFixture("src_rgb.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture("conv5_expected.png");
        float[] kernel = new float[25];
        float[,] baseKernel =
        {
            { 0.00f, 0.01f, 0.02f, 0.01f, 0.00f },
            { 0.01f, 0.05f, 0.10f, 0.05f, 0.01f },
            { 0.02f, 0.10f, 0.28f, 0.10f, 0.02f },
            { 0.01f, 0.05f, 0.10f, 0.05f, 0.01f },
            { 0.00f, 0.01f, 0.02f, 0.01f, 0.00f },
        };
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                kernel[(y * 5) + x] = baseKernel[y, x] * 0.9f;
            }
        }

        kernel[24] += 0.1f; // Asymmetric tap at the bottom-right corner exercises the anchor/orientation.
        using Image<Rgba32> actual = src.Clone(c => c.Convolve(kernel, 5, 5, preserveAlpha: true));
        int diff = EffectsTests.MaxDifference(expected, actual);
        Assert.True(diff <= 1, $"max diff {diff}");
    }

    [Fact]
    public void Sobel_MatchesNumpyMagnitudeWithinOne()
    {
        using Image<Rgba32> src = EffectsTests.LoadFixture("src_rgb.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture("sobel_expected.png");
        using Image<Rgba32> actual = src.Clone(c => c.DetectEdges());
        int diff = EffectsTests.MaxDifference(expected, actual);
        Assert.True(diff <= 1, $"max diff {diff}");
        // Grayscale first: all channels equal.
        Assert.Equal(actual[10, 10].R, actual[10, 10].G);
        Assert.Equal(actual[10, 10].G, actual[10, 10].B);
    }

    // ----- Engine properties -----

    [Fact]
    public void SeparableConvolution_EqualsEquivalent2DKernel()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        float[] kx = [0.25f, 0.5f, 0.25f];
        float[] ky = [0.1f, 0.8f, 0.1f];
        var k2d = new float[9];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                k2d[(y * 3) + x] = kx[x] * ky[y];
            }
        }

        using Image<Rgba32> separable = src.Clone(c => c.Convolve(kx, ky));
        using Image<Rgba32> full = src.Clone(c => c.Convolve(k2d, 3, 3));
        Assert.True(EffectsTests.MaxDifference(separable, full) <= 1);
    }

    [Fact]
    public void IdentityKernel_LeavesImageUnchanged()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        float[] identity = [0, 0, 0, 0, 1, 0, 0, 0, 0];
        using Image<Rgba32> result = src.Clone(c => c.Convolve(identity, 3, 3));
        Assert.Equal(EffectsTests.Checksum(src), EffectsTests.Checksum(result));
        using Image<Rgba32> shifted = src.Clone(c => c.Convolve(new float[] { 0, 0, 1 }, new float[] { 1 }));
        // A kernel [0,0,1] anchored at index 1 samples x+1: the image shifts left by one column (edge replicated).
        Assert.Equal(src[5, 7], shifted[4, 7]);
        Assert.Equal(src[63, 7], shifted[63, 7]);
    }

    [Fact]
    public void PreserveAlpha_KeepsSourceAlpha_OtherwiseAlphaIsConvolved()
    {
        using var image = new Image<Rgba32>(5, 1);
        for (int x = 0; x < 5; x++)
        {
            image[x, 0] = new Rgba32(100, 100, 100, (byte)(x == 2 ? 255 : 0));
        }

        float[] box = [1f / 3f, 1f / 3f, 1f / 3f];
        using Image<Rgba32> preserved = image.Clone(c => c.Convolve(box, 3, 1, preserveAlpha: true));
        Assert.Equal(255, preserved[2, 0].A);
        Assert.Equal(0, preserved[1, 0].A);
        Assert.Equal(100, preserved[2, 0].R);
        using Image<Rgba32> convolved = image.Clone(c => c.Convolve(box, 3, 1, preserveAlpha: false));
        Assert.Equal(85, convolved[2, 0].A);
        Assert.Equal(85, convolved[1, 0].A);
        Assert.Equal(0, convolved[0, 0].A);
    }

    [Fact]
    public void Convolve_ValidatesArguments()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        Assert.Throws<ArgumentException>(() => src.Clone(c => c.Convolve(new float[8], 3, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Clone(c => c.Convolve(new float[3], 0, 3)));
        Assert.Throws<ArgumentException>(() => src.Clone(c => c.Convolve(ReadOnlyMemory<float>.Empty, new float[] { 1f })));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Clone(c => c.BoxBlur(-1)));
        using Image<Rgba32> unchanged = src.Clone(c => c.BoxBlur(0));
        Assert.Equal(EffectsTests.Checksum(src), EffectsTests.Checksum(unchanged));
    }

    [Fact]
    public void DenseMatrix_ConvolveOverload_AndTranspose()
    {
        DenseMatrix<float> m = new float[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        Assert.Equal(3, m.Columns);
        Assert.Equal(2, m.Rows);
        Assert.Equal(6f, m[1, 2]);
        DenseMatrix<float> t = m.Transpose();
        Assert.Equal(2, t.Columns);
        Assert.Equal(3, t.Rows);
        Assert.Equal(6f, t[2, 1]);
        Assert.Equal(2f, t[1, 0]);
        Assert.Equal(m, m.Transpose().Transpose());
        Assert.NotEqual(m, t);
        Assert.Throws<ArgumentOutOfRangeException>(() => m[2, 0]);
        Assert.Throws<ArgumentException>(() => new DenseMatrix<float>(2, 2, new float[3]));

        using Image<Rgba32> src = EffectsTests.Synthetic();
        DenseMatrix<float> box = new float[,] { { 1f / 9, 1f / 9, 1f / 9 }, { 1f / 9, 1f / 9, 1f / 9 }, { 1f / 9, 1f / 9, 1f / 9 } };
        using Image<Rgba32> a = src.Clone(c => c.Convolve(box));
        using Image<Rgba32> b = src.Clone(c => c.BoxBlur(1));
        Assert.True(EffectsTests.MaxDifference(a, b) <= 1);
    }

    // ----- Edge detectors -----

    [Fact]
    public void AllKnownEdgeDetectors_FindVerticalEdge_AndFlatAreasStayBlack()
    {
        // Left half dark, right half bright: a single vertical edge at x = 16.
        using var image = new Image<Rgb24>(32, 16, new Rgb24(20, 20, 20));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 16; x < 32; x++)
            {
                image[x, y] = new Rgb24(220, 220, 220);
            }
        }

        void Check(string name, Image<Rgb24> result)
        {
            Assert.True(result[16, 8].R > 100 || result[15, 8].R > 100, $"{name}: edge not detected ({result[15, 8]}, {result[16, 8]})");
            Assert.Equal(new Rgb24(0, 0, 0), result[4, 8]);
            Assert.Equal(new Rgb24(0, 0, 0), result[28, 8]);
        }

        Check("Sobel", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Sobel)));
        Check("Prewitt", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Prewitt)));
        Check("Scharr", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Scharr)));
        Check("RobertsCross", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.RobertsCross)));
        Check("Kirsch", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Kirsch)));
        Check("Robinson", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Robinson)));
        Check("Laplacian3x3", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Laplacian3x3)));
        Check("Laplacian5x5", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Laplacian5x5)));
        Check("LaplacianOfGaussian", image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.LaplacianOfGaussian)));
    }

    [Fact]
    public void Kayyali_RespondsToDiagonalEdges_NotToAxisAlignedOnes()
    {
        // Kayyali's kernels only have corner taps: a vertical step gives no response, a diagonal step does.
        using var vertical = new Image<L8>(16, 16, new L8(20));
        using var diagonal = new Image<L8>(16, 16, new L8(20));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (x >= 8)
                {
                    vertical[x, y] = new L8(220);
                }

                if (x + y >= 16)
                {
                    diagonal[x, y] = new L8(220);
                }
            }
        }

        vertical.Mutate(c => c.DetectEdges(KnownEdgeDetectorKernels.Kayyali, grayscale: false));
        diagonal.Mutate(c => c.DetectEdges(KnownEdgeDetectorKernels.Kayyali, grayscale: false));
        Assert.Equal(0, vertical[7, 8].PackedValue);
        Assert.Equal(0, vertical[8, 8].PackedValue);
        Assert.Equal(255, diagonal[8, 8].PackedValue);
        Assert.Equal(0, diagonal[2, 2].PackedValue);
        Assert.Equal(0, diagonal[13, 13].PackedValue);
    }

    [Fact]
    public void Sobel_OnVerticalStep_HasExpectedMagnitude()
    {
        using var image = new Image<L8>(8, 5, new L8(0));
        for (int y = 0; y < 5; y++)
        {
            for (int x = 4; x < 8; x++)
            {
                image[x, y] = new L8(100);
            }
        }

        image.Mutate(c => c.DetectEdges(KnownEdgeDetectorKernels.Sobel, grayscale: false));
        // Column 3: gx = (0*-1 + 100*1) * (1+2+1) = 400 -> clamps to 255; gy = 0.
        Assert.Equal(255, image[3, 2].PackedValue);
        Assert.Equal(255, image[4, 2].PackedValue);
        Assert.Equal(0, image[1, 2].PackedValue);
        Assert.Equal(0, image[6, 2].PackedValue);
    }

    [Fact]
    public void RobertsCross_TwoByTwoKernelIsAnchoredAtTopLeft()
    {
        using var image = new Image<L8>(6, 6, new L8(0));
        image[3, 3] = new L8(200);
        image.Mutate(c => c.DetectEdges(KnownEdgeDetectorKernels.RobertsCross, grayscale: false));
        // Gx = [[1,0],[0,-1]] samples (x,y) and (x+1,y+1); the bright pixel contributes at (3,3), (2,2), (2,3), (3,2).
        Assert.Equal(200, image[3, 3].PackedValue);
        Assert.Equal(200, image[2, 2].PackedValue);
        Assert.Equal(200, image[2, 3].PackedValue);
        Assert.Equal(200, image[3, 2].PackedValue);
        Assert.Equal(0, image[4, 4].PackedValue);
        Assert.Equal(0, image[0, 0].PackedValue);
    }

    [Fact]
    public void DetectEdges_GrayscaleFalse_KeepsPerChannelResponse_AndAlphaIsPreserved()
    {
        using var image = new Image<Rgba32>(10, 4, new Rgba32(0, 0, 0, 77));
        for (int y = 0; y < 4; y++)
        {
            for (int x = 5; x < 10; x++)
            {
                image[x, y] = new Rgba32(200, 0, 0, 77);
            }
        }

        using Image<Rgba32> colour = image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Sobel, grayscale: false));
        Assert.True(colour[4, 2].R > 0);
        Assert.Equal(0, colour[4, 2].G);
        Assert.Equal(0, colour[4, 2].B);
        Assert.Equal(77, colour[4, 2].A);
        Assert.Equal(77, colour[0, 0].A);

        using Image<Rgba32> grey = image.Clone(c => c.DetectEdges(KnownEdgeDetectorKernels.Sobel, grayscale: true));
        Assert.Equal(grey[4, 2].R, grey[4, 2].G);
        Assert.Equal(77, grey[4, 2].A);
    }

    [Fact]
    public void EdgeDetectorKernels_EqualityAndValidation()
    {
        Assert.Equal(KnownEdgeDetectorKernels.Sobel, new EdgeDetector2DKernel(KnownEdgeDetectorKernels.Sobel.KernelX, KnownEdgeDetectorKernels.Sobel.KernelY));
        Assert.NotEqual(KnownEdgeDetectorKernels.Sobel, KnownEdgeDetectorKernels.Prewitt);
        Assert.Equal(KnownEdgeDetectorKernels.Kirsch, KnownEdgeDetectorKernels.Kirsch);
        Assert.NotEqual(KnownEdgeDetectorKernels.Kirsch, KnownEdgeDetectorKernels.Robinson);
        Assert.Equal(8, KnownEdgeDetectorKernels.Robinson.Flatten().Length);
        Assert.Equal(KnownEdgeDetectorKernels.Laplacian3x3.GetHashCode(), new EdgeDetectorKernel(KnownEdgeDetectorKernels.Laplacian3x3.Kernel).GetHashCode());
        Assert.Throws<ArgumentException>(() => new EdgeDetectorKernel(default));
        Assert.Throws<ArgumentException>(() => new EdgeDetector2DKernel(default, KnownEdgeDetectorKernels.Sobel.KernelY));

        using Image<Rgba32> src = EffectsTests.Synthetic();
        Assert.Throws<ArgumentException>(() => src.Clone(c => c.DetectEdges(default(EdgeDetectorKernel), true)));
    }

    // ----- Region overloads of the Gaussian filters and box blur -----

    [Fact]
    public void GaussianBlur_Rectangle_EqualsCropBlurPaste()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        var rect = new Rectangle(8, 6, 30, 20);
        using Image<Rgba32> region = src.Clone(c => c.GaussianBlur(2f, rect));
        using Image<Rgba32> cropped = src.Clone(c => c.Crop(rect).GaussianBlur(2f));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Rgba32 expected = rect.Contains(x, y) ? cropped[x - rect.X, y - rect.Y] : src[x, y];
                Assert.Equal(expected, region[x, y]);
            }
        }
    }

    [Fact]
    public void GaussianSharpen_And_BoxBlur_Rectangle_OnlyChangeRegion()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        var rect = new Rectangle(0, 0, 32, 48);
        using Image<Rgba32> sharpened = src.Clone(c => c.GaussianSharpen(1.5f, rect));
        using Image<Rgba32> sharpenedCrop = src.Clone(c => c.Crop(rect).GaussianSharpen(1.5f));
        using Image<Rgba32> boxed = src.Clone(c => c.BoxBlur(3, rect));
        using Image<Rgba32> boxedCrop = src.Clone(c => c.Crop(rect).BoxBlur(3));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                if (rect.Contains(x, y))
                {
                    Assert.Equal(sharpenedCrop[x, y], sharpened[x, y]);
                    Assert.Equal(boxedCrop[x, y], boxed[x, y]);
                }
                else
                {
                    Assert.Equal(src[x, y], sharpened[x, y]);
                    Assert.Equal(src[x, y], boxed[x, y]);
                }
            }
        }
    }

    [Fact]
    public void GaussianBlur_FullRectangle_MatchesClassicOverloadExactly()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        using Image<Rgba32> classic = src.Clone(c => c.GaussianBlur(1.5f));
        using Image<Rgba32> region = src.Clone(c => c.GaussianBlur(1.5f, new Rectangle(0, 0, 64, 48)));
        Assert.Equal(EffectsTests.Checksum(classic), EffectsTests.Checksum(region));
        Assert.Equal(16702821983734622917UL, EffectsTests.Checksum(region));
    }

    [Fact]
    public void BoxBlur_UniformImageStaysUniform_AndAppliesToEveryFrameAndFormat()
    {
        using var uniform = new Image<Bgra32>(30, 20, Bgra32.FromRgba32(new Rgba32(10, 20, 30, 200)));
        uniform.Mutate(c => c.BoxBlur(4));
        Assert.Equal(new Rgba32(10, 20, 30, 200), uniform[0, 0].ToRgba32());
        Assert.Equal(new Rgba32(10, 20, 30, 200), uniform[29, 19].ToRgba32());

        using Image<Rgba32> frames = EffectsTests.TwoFrames();
        using Image<Rgba32> blurred = frames.Clone(c => c.BoxBlur(2));
        for (int f = 0; f < 2; f++)
        {
            using var single = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { frames.Frames[f].Clone() });
            single.Mutate(c => c.BoxBlur(2));
            using var frame = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { blurred.Frames[f].Clone() });
            Assert.Equal(EffectsTests.Checksum(single), EffectsTests.Checksum(frame));
        }
    }
}
