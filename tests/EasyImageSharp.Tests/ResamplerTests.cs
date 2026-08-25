using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Kernel maths of every shipped resampler, resize behaviour per kernel, and the Pillow resize references.</summary>
public class ResamplerTests
{
    public static IEnumerable<object[]> AllResamplers => GeometryTestSupport.AllResamplers.Select(r => new object[] { r.Name });

    public static IEnumerable<object[]> ResizeFixtures => GeometryTestSupport.FixtureNames("resize");

    [Theory]
    [MemberData(nameof(AllResamplers))]
    public void Kernel_IsSymmetricUnitAtZeroAndZeroBeyondRadius(string name)
    {
        IResampler sampler = GeometryTestSupport.Resampler(name);
        Assert.True(sampler.Radius > 0f);
        if (sampler is CubicResampler cubic)
        {
            // BC-splines peak at (6 - 2B) / 6: 1 for the interpolating members (B = 0), less for the blurring ones.
            Assert.Equal((6f - (2f * cubic.B)) / 6f, sampler.GetValue(0f), 5);
        }
        else if (sampler is not BoxResampler and not NearestNeighborResampler)
        {
            Assert.Equal(1f, sampler.GetValue(0f), 5);
        }

        for (float x = 0.05f; x < sampler.Radius + 2f; x += 0.35f)
        {
            Assert.Equal(sampler.GetValue(x), sampler.GetValue(-x), 6);
        }

        Assert.Equal(0f, sampler.GetValue(sampler.Radius + 0.01f));
        Assert.Equal(0f, sampler.GetValue(sampler.Radius + 5f));
    }

    [Fact]
    public void Bicubic_IsKeysCubicWithAMinusHalf()
    {
        static float Keys(float x)
        {
            const float a = -0.5f;
            x = MathF.Abs(x);
            if (x < 1f)
            {
                return ((a + 2f) * x * x * x) - ((a + 3f) * x * x) + 1f;
            }

            return x < 2f ? (a * x * x * x) - (5f * a * x * x) + (8f * a * x) - (4f * a) : 0f;
        }

        Assert.Equal(2f, KnownResamplers.Bicubic.Radius);
        for (float x = -2.5f; x <= 2.5f; x += 0.125f)
        {
            Assert.Equal(Keys(x), KnownResamplers.Bicubic.GetValue(x), 5);
            Assert.Equal(Keys(x), KnownResamplers.CatmullRom.GetValue(x), 5);
        }
    }

    [Theory]
    [InlineData("Bicubic")]
    [InlineData("CatmullRom")]
    [InlineData("MitchellNetravali")]
    [InlineData("Robidoux")]
    [InlineData("RobidouxSharp")]
    [InlineData("Spline")]
    [InlineData("Hermite")]
    [InlineData("Triangle")]
    [InlineData("Box")]
    public void CompactKernels_PartitionUnity(string name)
    {
        // Every BC-spline, the tent and the box sum to exactly 1 over the integer lattice at any offset.
        IResampler sampler = GeometryTestSupport.Resampler(name);
        for (float offset = 0f; offset < 1f; offset += 0.0625f)
        {
            float sum = 0f;
            for (int i = -6; i <= 6; i++)
            {
                sum += sampler.GetValue(i + offset);
            }

            Assert.Equal(1f, sum, 4);
        }
    }

    [Fact]
    public void CubicFamily_ExposesBAndC()
    {
        Assert.Equal(1f / 3f, ((CubicResampler)KnownResamplers.MitchellNetravali).B, 6);
        Assert.Equal(1f / 3f, ((CubicResampler)KnownResamplers.MitchellNetravali).C, 6);
        Assert.Equal(1f, ((CubicResampler)KnownResamplers.Spline).B);
        Assert.Equal(0f, ((CubicResampler)KnownResamplers.Spline).C);
        Assert.Equal(0.5f, ((CubicResampler)KnownResamplers.CatmullRom).C);
        Assert.Equal(0.3782158f, ((CubicResampler)KnownResamplers.Robidoux).B, 5);
        Assert.Equal(0.2620145f, ((CubicResampler)KnownResamplers.RobidouxSharp).B, 5);
    }

    [Fact]
    public void Lanczos_MatchesSincFormulaAndRadii()
    {
        Assert.Equal(2f, KnownResamplers.Lanczos2.Radius);
        Assert.Equal(3f, KnownResamplers.Lanczos3.Radius);
        Assert.Equal(5f, KnownResamplers.Lanczos5.Radius);
        Assert.Equal(8f, KnownResamplers.Lanczos8.Radius);
        Assert.Equal(3f, KnownResamplers.Welch.Radius);
        Assert.Same(KnownResamplers.Triangle, KnownResamplers.Bilinear);

        static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
        foreach ((IResampler sampler, int r) in new[] { (KnownResamplers.Lanczos2, 2), (KnownResamplers.Lanczos3, 3), (KnownResamplers.Lanczos5, 5), (KnownResamplers.Lanczos8, 8) })
        {
            for (float x = 0.1f; x < r; x += 0.3f)
            {
                Assert.Equal(Sinc(x) * Sinc(x / r), sampler.GetValue(x), 4);
            }
        }

        for (float x = 0.1f; x < 3f; x += 0.3f)
        {
            Assert.Equal(Sinc(x) * (1 - (x * x / 9.0)), KnownResamplers.Welch.GetValue(x), 4);
        }
    }

    [Theory]
    [MemberData(nameof(AllResamplers))]
    public void Resize_UniformImage_StaysUniformWithEveryKernel(string name)
    {
        IResampler sampler = GeometryTestSupport.Resampler(name);
        using var image = new Image<Rgb24>(53, 37, new Rgb24(90, 160, 220));
        image.Mutate(ctx => ctx.Resize(31, 47, sampler));
        image.Mutate(ctx => ctx.Resize(9, 5, sampler));
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(new Rgb24(90, 160, 220), image[x, y]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ResizeFixtures))]
    public void Resize_MatchesPillowReference(string name)
    {
        GeometryTestSupport.Entry entry = GeometryTestSupport.GetEntry(name);
        using Image<Rgba32> source = GeometryTestSupport.LoadSource();
        using Image<Rgba32> expected = GeometryTestSupport.LoadRgba(entry.Name, entry.Width, entry.Height);
        using Image<Rgba32> actual = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(entry.Width, entry.Height),
            Mode = ResizeMode.Stretch,
            Sampler = GeometryTestSupport.Resampler(entry.Filter),
        }));

        double psnr = GeometryTestSupport.Psnr(expected, actual);
        Assert.True(psnr > 40, $"{name}: PSNR {psnr:F2} dB vs Pillow (expected > 40 dB).");
        Assert.True(GeometryTestSupport.MaxAbsDifference(expected, actual) <= 3, $"{name}: max channel difference vs Pillow exceeds 3.");
    }

    [Fact]
    public void Resize_PremultipliedAlpha_KeepsColorOfTransparentNeighboursOut()
    {
        // Opaque red next to transparent black: with premultiplication the boundary keeps a red hue and only
        // alpha fades; without it the colour is dragged toward black.
        using var image = new Image<Rgba32>(64, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                image[x, y] = x < 32 ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 0, 0);
            }
        }

        using Image<Rgba32> premultiplied = image.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(16, 2), Sampler = KnownResamplers.Bicubic }));
        using Image<Rgba32> straight = image.Clone(ctx => ctx.Resize(new ResizeOptions { Size = new Size(16, 2), Sampler = KnownResamplers.Bicubic, PremultiplyAlpha = false }));

        Rgba32 boundaryPre = premultiplied[8, 0];
        Rgba32 boundaryStraight = straight[8, 0];
        Assert.True(boundaryPre.A is > 0 and < 255, $"expected partial alpha at the boundary, got {boundaryPre}");
        Assert.True(boundaryPre.R >= 250, $"premultiplied boundary should stay red, got {boundaryPre}");
        Assert.True(boundaryStraight.R < 200, $"straight-alpha boundary should darken, got {boundaryStraight}");
        Assert.Equal(new Rgba32(255, 0, 0, 255), premultiplied[2, 1]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), premultiplied[14, 1]);
    }

    [Fact]
    public void Resize_PremultipliedAlpha_TransparentResultIsTransparentBlack()
    {
        using var image = new Image<Rgba32>(20, 20, new Rgba32(200, 100, 50, 0));
        image.Mutate(ctx => ctx.Resize(7, 7, KnownResamplers.Lanczos3));
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[3, 3]);
    }

    [Fact]
    public void Resize_Compand_AveragesInLinearLight()
    {
        // A black/white checkerboard averaged in linear light is brighter than the naive sRGB midpoint (~128).
        using var image = new Image<Rgb24>(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                byte v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                image[x, y] = new Rgb24(v, v, v);
            }
        }

        using Image<Rgb24> gamma = image.Clone(ctx => ctx.Resize(2, 2, KnownResamplers.Box, compand: true));
        using Image<Rgb24> naive = image.Clone(ctx => ctx.Resize(2, 2, KnownResamplers.Box, compand: false));
        Assert.InRange(naive[0, 0].R, 126, 129);
        Assert.InRange(gamma[0, 0].R, 185, 190); // sRGB encode of 0.5 linear = 187.5
    }

    [Fact]
    public void Resize_Compand_LeavesUniformImageAndAlphaUntouched()
    {
        using var image = new Image<Rgba32>(30, 30, new Rgba32(37, 200, 99, 140));
        image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(11, 13), Compand = true, Sampler = KnownResamplers.Bicubic }));
        Rgba32 p = image[5, 6];
        Assert.InRange(p.R, 36, 38);
        Assert.InRange(p.G, 199, 201);
        Assert.InRange(p.B, 98, 100);
        Assert.Equal(140, p.A);
    }

    [Fact]
    public void Resize_OpaqueRgbaAndRgb_AgreeWithinRounding()
    {
        // The premultiplied path is a no-op for opaque pixels apart from float rounding of the un-premultiply.
        using Image<Rgb24> rgb = TestImages.Gradient(97, 61);
        using Image<Rgba32> rgba = rgb.CloneAs<Rgba32>();
        rgb.Mutate(ctx => ctx.Resize(50, 33, KnownResamplers.Lanczos3));
        rgba.Mutate(ctx => ctx.Resize(50, 33, KnownResamplers.Lanczos3));
        using Image<Rgb24> back = rgba.CloneAs<Rgb24>();
        Assert.True(TestImages.AveragePixelDifference(rgb, back) < 0.05);
    }

    [Fact]
    public void Resize_NullSampler_Throws()
    {
        using Image<Rgb24> image = TestImages.Gradient(10, 10);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(5, 5), Sampler = null! })));
    }
}
