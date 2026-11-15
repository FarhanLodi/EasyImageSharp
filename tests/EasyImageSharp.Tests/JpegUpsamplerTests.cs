using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The chroma upsampling kernels against the triangle-filter definition (libjpeg's jdsample.c), written out
/// directly here, at every row length around the vector block size, on the vector and the scalar path.
/// </summary>
public class JpegUpsamplerTests
{
    public static IEnumerable<object[]> Lengths()
    {
        foreach (int n in new[] { 2, 3, 4, 15, 16, 17, 18, 19, 31, 32, 33, 34, 35, 47, 50, 64, 65, 100, 257 })
        {
            yield return new object[] { n };
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void H2V1_MatchesDefinitionOnEveryPath(int n)
    {
        var rng = new Random(n);
        byte[] input = new byte[n];
        rng.NextBytes(input);
        int[] expected = ExpectedH2V1(input.Select(b => (int)b).ToArray());

        foreach (bool scalar in new[] { false, true })
        {
            byte[] actual = RunWithScalarFallback(scalar, () =>
            {
                var result = new byte[2 * n];
                JpegUpsampler.UpsampleH2V1(input, result);
                return result;
            });
            Assert.Equal(expected, actual.Select(b => (int)b).ToArray());
        }

        var wide = new ushort[2 * n];
        JpegUpsampler.UpsampleH2V1(input.Select(b => (ushort)(b * 16)).ToArray(), wide);
        Assert.Equal(ExpectedH2V1(input.Select(b => b * 16).ToArray()), wide.Select(w => (int)w).ToArray());
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void H2V2_MatchesDefinitionOnEveryPath(int n)
    {
        var rng = new Random(1000 + n);
        byte[] near = new byte[n];
        byte[] far = new byte[n];
        rng.NextBytes(near);
        rng.NextBytes(far);
        if (n > 3)
        {
            near[1] = 255;
            far[1] = 255; // The largest column sums must not overflow the vector lanes.
            near[2] = 255;
            far[2] = 255;
        }

        int[] expected = ExpectedH2V2(near.Select(b => (int)b).ToArray(), far.Select(b => (int)b).ToArray());
        foreach (bool scalar in new[] { false, true })
        {
            byte[] actual = RunWithScalarFallback(scalar, () =>
            {
                var result = new byte[2 * n];
                JpegUpsampler.UpsampleH2V2(near, far, result);
                return result;
            });
            Assert.Equal(expected, actual.Select(b => (int)b).ToArray());
        }

        var wide = new ushort[2 * n];
        JpegUpsampler.UpsampleH2V2(near.Select(b => (ushort)(b * 257)).ToArray(), far.Select(b => (ushort)(b * 257)).ToArray(), wide);
        Assert.Equal(
            ExpectedH2V2(near.Select(b => b * 257).ToArray(), far.Select(b => b * 257).ToArray()),
            wide.Select(w => (int)w).ToArray());
    }

    [Theory]
    [InlineData("baseline_420")]
    [InlineData("baseline_422")]
    [InlineData("progressive_420_odd")]
    public void FusedPackingMatchesGenericConversion(string name)
    {
        byte[] data = FixturePath.Read($"jpeg/{name}.jpg");
        using Image<Rgba32> rgba = Image.Load<Rgba32>(data);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(data);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(data);
        for (int y = 0; y < rgba.Height; y++)
        {
            for (int x = 0; x < rgba.Width; x++)
            {
                Rgba32 a = rgba[x, y];
                Bgra32 b = bgra[x, y];
                Rgb24 c = rgb[x, y];
                Assert.True(
                    a.R == b.R && a.G == b.G && a.B == b.B && b.A == 255 && a.A == 255 && a.R == c.R && a.G == c.G && a.B == c.B,
                    $"({x},{y}): Rgba32 {a}, Bgra32 {b}, Rgb24 {c}");
            }
        }

        byte[] vector = RunWithScalarFallback(false, () => Pixels(data));
        byte[] scalarPixels = RunWithScalarFallback(true, () => Pixels(data));
        Assert.Equal(scalarPixels, vector);

        static byte[] Pixels(byte[] data)
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(data);
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Frames.RootFrame.PixelSpan).ToArray();
        }
    }

    private static int[] ExpectedH2V1(int[] input)
    {
        int n = input.Length;
        var output = new int[2 * n];
        for (int i = 0; i < n; i++)
        {
            int left = input[Math.Max(i - 1, 0)];
            int right = input[Math.Min(i + 1, n - 1)];
            output[2 * i] = i == 0 ? input[0] : ((3 * input[i]) + left + 1) >> 2;
            output[(2 * i) + 1] = i == n - 1 ? input[n - 1] : ((3 * input[i]) + right + 2) >> 2;
        }

        return output;
    }

    private static int[] ExpectedH2V2(int[] near, int[] far)
    {
        int n = near.Length;
        int[] sums = Enumerable.Range(0, n).Select(i => (3 * near[i]) + far[i]).ToArray();
        var output = new int[2 * n];
        for (int i = 0; i < n; i++)
        {
            output[2 * i] = i == 0 ? ((sums[0] * 4) + 8) >> 4 : ((3 * sums[i]) + sums[i - 1] + 8) >> 4;
            output[(2 * i) + 1] = i == n - 1 ? ((sums[n - 1] * 4) + 7) >> 4 : ((3 * sums[i]) + sums[i + 1] + 7) >> 4;
        }

        return output;
    }

    private static T RunWithScalarFallback<T>(bool forceScalar, Func<T> body)
    {
        bool previous = SimdConfig.ForceScalarFallback;
        SimdConfig.ForceScalarFallback = forceScalar;
        try
        {
            return body();
        }
        finally
        {
            SimdConfig.ForceScalarFallback = previous;
        }
    }
}
