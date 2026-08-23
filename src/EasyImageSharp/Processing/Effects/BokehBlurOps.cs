using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Bokeh (lens) blur using the complex-kernel decomposition of a disc: the circular kernel is approximated by
/// a sum of one to six separable complex Gaussian components (each <c>exp(-a x²) (cos(b x²) + i sin(b x²))</c>
/// scaled by real weights A and B), which turns an O(r²) disc convolution into a handful of O(r) separable
/// passes. Colour channels are raised to <c>gamma</c> before blurring and back afterwards so highlights bloom
/// into visible discs. Alpha is blurred linearly.
/// </summary>
internal static class BokehBlurOps
{
    /// <summary>Component parameters (a, b, A, B) for 1..6 components, from the published least-squares fits.</summary>
    private static readonly float[][][] Components =
    [
        [[0.862325f, 1.624835f, 0.767583f, 1.862321f]],
        [
            [0.886528f, 5.268909f, 0.411259f, -0.548794f],
            [1.960518f, 1.558213f, 0.513282f, 4.561110f],
        ],
        [
            [2.176490f, 5.043495f, 1.621035f, -2.105439f],
            [1.019306f, 9.027613f, -0.280860f, -0.162882f],
            [2.815110f, 1.597273f, -0.366471f, 10.300301f],
        ],
        [
            [4.338459f, 1.553635f, -5.767909f, 46.164397f],
            [3.839993f, 4.693183f, 9.795391f, -15.227561f],
            [2.791880f, 8.178137f, -3.048324f, 0.302959f],
            [1.342190f, 12.328289f, 0.010001f, 0.244650f],
        ],
        [
            [4.892608f, 1.685979f, -22.356787f, 85.912460f],
            [4.711870f, 4.998496f, 35.918936f, -28.875618f],
            [4.052795f, 8.244168f, -13.212253f, -1.578428f],
            [2.929212f, 11.900859f, 0.507991f, 1.816328f],
            [1.512961f, 16.116382f, 0.138051f, -0.010000f],
        ],
        [
            [5.143778f, 2.079813f, -82.326596f, 111.231024f],
            [5.612426f, 6.153387f, 113.878661f, 58.004879f],
            [5.982921f, 9.802895f, 39.479083f, -162.028887f],
            [6.505167f, 11.059237f, -71.286026f, 95.027069f],
            [3.869579f, 14.810520f, 1.405746f, -3.704914f],
            [2.201904f, 19.032909f, -0.152784f, -0.107988f],
        ],
    ];

    /// <summary>The number of supported components (1..6).</summary>
    public const int MaxComponents = 6;

    public static void BokehBlur<TPixel>(ImageFrame<TPixel> frame, Rectangle region, int radius, int components, float gamma)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        int width = region.Width;
        int height = region.Height;
        (float[] real, float[] imaginary)[] kernels = BuildKernels(radius, components, out float[] weightsA, out float[] weightsB, out float normalization);

        // Gamma-encode colour (alpha stays linear), 0..1 range.
        Rgba32[] pixels = RowProcessor.ReadRegion(frame, region);
        var source = new Vector4[pixels.Length];
        float[] gammaLut = BuildGammaLut(gamma);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int i = start * width; i < end * width; i++)
            {
                Rgba32 p = pixels[i];
                source[i] = new Vector4(gammaLut[p.R], gammaLut[p.G], gammaLut[p.B], p.A / 255f);
            }
        });

        var result = new Vector4[pixels.Length];
        var horizontalReal = new Vector4[pixels.Length];
        var horizontalImaginary = new Vector4[pixels.Length];
        int length = (2 * radius) + 1;
        int maxX = width - 1;
        int maxY = height - 1;

        for (int c = 0; c < kernels.Length; c++)
        {
            float[] kr = kernels[c].real;
            float[] ki = kernels[c].imaginary;
            float a = weightsA[c] * normalization;
            float b = weightsB[c] * normalization;

            // Horizontal complex pass.
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        Vector4 sumR = Vector4.Zero;
                        Vector4 sumI = Vector4.Zero;
                        for (int i = 0; i < length; i++)
                        {
                            Vector4 s = source[row + Math.Clamp(x + i - radius, 0, maxX)];
                            sumR += s * kr[i];
                            sumI += s * ki[i];
                        }

                        horizontalReal[row + x] = sumR;
                        horizontalImaginary[row + x] = sumI;
                    }
                }
            });

            // Vertical complex pass, accumulating A * Re + B * Im.
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        Vector4 sumR = Vector4.Zero;
                        Vector4 sumI = Vector4.Zero;
                        for (int i = 0; i < length; i++)
                        {
                            int index = (Math.Clamp(y + i - radius, 0, maxY) * width) + x;
                            Vector4 hr = horizontalReal[index];
                            Vector4 hi = horizontalImaginary[index];
                            sumR += (hr * kr[i]) - (hi * ki[i]);
                            sumI += (hr * ki[i]) + (hi * kr[i]);
                        }

                        result[row + x] += (sumR * a) + (sumI * b);
                    }
                }
            });
        }

        // Gamma-decode and write back.
        float inverseGamma = 1f / gamma;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int i = start * width; i < end * width; i++)
            {
                Vector4 v = Vector4.Clamp(result[i], Vector4.Zero, Vector4.One);
                pixels[i] = new Rgba32(
                    RowProcessor.ClampToByte(MathF.Pow(v.X, inverseGamma) * 255f),
                    RowProcessor.ClampToByte(MathF.Pow(v.Y, inverseGamma) * 255f),
                    RowProcessor.ClampToByte(MathF.Pow(v.Z, inverseGamma) * 255f),
                    RowProcessor.ClampToByte(v.W * 255f));
            }
        });

        RowProcessor.WriteRegion(frame, region, pixels);
    }

    /// <summary>
    /// Builds the 1-D complex kernels for every component plus the weights and the normalisation factor that
    /// makes the composite 2-D kernel sum to one.
    /// </summary>
    internal static (float[] Real, float[] Imaginary)[] BuildKernels(int radius, int components, out float[] weightsA, out float[] weightsB, out float normalization)
    {
        float[][] parameters = Components[components - 1];
        int length = (2 * radius) + 1;
        var kernels = new (float[], float[])[parameters.Length];
        weightsA = new float[parameters.Length];
        weightsB = new float[parameters.Length];
        double total = 0;
        for (int c = 0; c < parameters.Length; c++)
        {
            float a = parameters[c][0];
            float b = parameters[c][1];
            weightsA[c] = parameters[c][2];
            weightsB[c] = parameters[c][3];
            var real = new float[length];
            var imaginary = new float[length];
            for (int i = 0; i < length; i++)
            {
                float x = (i - radius) / (float)radius;
                float x2 = x * x;
                float envelope = MathF.Exp(-a * x2);
                real[i] = envelope * MathF.Cos(b * x2);
                imaginary[i] = envelope * MathF.Sin(b * x2);
            }

            kernels[c] = (real, imaginary);

            // Sum of the 2-D kernel: Re = rx ry - ix iy, Im = rx iy + ix ry, weighted by A and B.
            double sumReal = 0;
            double sumImaginary = 0;
            for (int i = 0; i < length; i++)
            {
                sumReal += real[i];
                sumImaginary += imaginary[i];
            }

            double re2D = (sumReal * sumReal) - (sumImaginary * sumImaginary);
            double im2D = 2 * sumReal * sumImaginary;
            total += (weightsA[c] * re2D) + (weightsB[c] * im2D);
        }

        normalization = (float)(1.0 / total);
        return kernels;
    }

    /// <summary>Evaluates the composite 2-D kernel at an offset (for verification).</summary>
    internal static float EvaluateKernel(int radius, int components, int dx, int dy)
    {
        (float[] real, float[] imaginary)[] kernels = BuildKernels(radius, components, out float[] a, out float[] b, out float normalization);
        float value = 0;
        for (int c = 0; c < kernels.Length; c++)
        {
            float rx = kernels[c].real[dx + radius];
            float ix = kernels[c].imaginary[dx + radius];
            float ry = kernels[c].real[dy + radius];
            float iy = kernels[c].imaginary[dy + radius];
            float re = (rx * ry) - (ix * iy);
            float im = (rx * iy) + (ix * ry);
            value += (a[c] * re) + (b[c] * im);
        }

        return value * normalization;
    }

    private static float[] BuildGammaLut(float gamma)
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = MathF.Pow(i / 255f, gamma);
        }

        return lut;
    }
}
