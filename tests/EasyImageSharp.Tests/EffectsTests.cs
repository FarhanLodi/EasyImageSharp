using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Effects (pixelate, oil paint, vignette/glow, swizzle, bokeh, thresholds) plus regression locks for the pre-existing filters.</summary>
public class EffectsTests
{
    // ----- Shared helpers -----

    /// <summary>A 64x48 RGBA image with gradients, a checkerboard and a semi-transparent right half.</summary>
    internal static Image<Rgba32> Synthetic(int width = 64, int height = 48)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x * 255) / (width - 1));
                byte g = (byte)((y * 255) / (height - 1));
                byte b = (byte)((x * 7 + y * 13) % 256);
                byte a = (byte)(x < width / 2 ? 255 : 128 + ((x * 3) % 100));
                if (((x / 8) + (y / 8)) % 2 == 0)
                {
                    r = (byte)(255 - r);
                }

                image[x, y] = new Rgba32(r, g, b, a);
            }
        }

        return image;
    }

    /// <summary>FNV-1a over the raw pixel bytes of every frame.</summary>
    internal static ulong Checksum<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ulong hash = 14695981039346656037UL;
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes<TPixel>(frame.GetRowSpan(y));
                foreach (byte b in bytes)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
        }

        return hash;
    }

    internal static Image<Rgba32> LoadFixture(string name) => Image.Load<Rgba32>(FixturePath.Get("effects/" + name));

    /// <summary>Largest absolute per-channel difference between two same-sized images.</summary>
    internal static int MaxDifference(Image<Rgba32> a, Image<Rgba32> b, bool includeAlpha = true, int border = 0)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int max = 0;
        for (int y = border; y < a.Height - border; y++)
        {
            for (int x = border; x < a.Width - border; x++)
            {
                Rgba32 p = a[x, y];
                Rgba32 q = b[x, y];
                max = Math.Max(max, Math.Abs(p.R - q.R));
                max = Math.Max(max, Math.Abs(p.G - q.G));
                max = Math.Max(max, Math.Abs(p.B - q.B));
                if (includeAlpha)
                {
                    max = Math.Max(max, Math.Abs(p.A - q.A));
                }
            }
        }

        return max;
    }

    internal static Image<Rgba32> TwoFrames(int width = 32, int height = 24)
    {
        Image<Rgba32> first = Synthetic(width, height);
        Image<Rgba32> second = first.Clone(c => c.Invert());
        var frames = new List<ImageFrame<Rgba32>> { first.Frames.RootFrame.Clone(), second.Frames.RootFrame.Clone() };
        return new Image<Rgba32>(frames);
    }

    // ----- Regression locks: outputs of the pre-existing filters must not change -----

    [Theory]
    [InlineData("blur15", 16702821983734622917UL)]
    [InlineData("blur3", 3821777651027991130UL)]
    [InlineData("sharpen", 2350710320178741225UL)]
    [InlineData("median1", 12881877148856117476UL)]
    [InlineData("median2", 2896386418161975287UL)]
    [InlineData("otsu", 17486075492618090293UL)]
    [InlineData("bradley", 6044586300718315373UL)]
    [InlineData("bradley0", 6044586300718315373UL)]
    [InlineData("sauvola", 2980768887658861940UL)]
    [InlineData("binary", 17486075492618090293UL)]
    [InlineData("rgb_blur15", 9321079096642659783UL)]
    [InlineData("rgb_sharpen", 15929787773538825507UL)]
    [InlineData("rgb_median1", 9256094577139438960UL)]
    [InlineData("l8_otsu", 13221367454131558017UL)]
    [InlineData("l8_sauvola", 12110289999230890836UL)]
    [InlineData("l8_bradley", 14408725340884349547UL)]
    public void LegacyFilters_ProduceLockedChecksums(string name, ulong expected)
    {
        using Image<Rgba32> src = Synthetic();
        ulong actual = name switch
        {
            "blur15" => Checksum(src.Clone(c => c.GaussianBlur(1.5f))),
            "blur3" => Checksum(src.Clone(c => c.GaussianBlur(3f))),
            "sharpen" => Checksum(src.Clone(c => c.GaussianSharpen(1.5f))),
            "median1" => Checksum(src.Clone(c => c.MedianBlur(1))),
            "median2" => Checksum(src.Clone(c => c.MedianBlur(2))),
            "otsu" => Checksum(src.Clone(c => c.OtsuThreshold())),
            "bradley" => Checksum(src.Clone(c => c.AdaptiveThreshold(15, 0.85f))),
            "bradley0" => Checksum(src.Clone(c => c.AdaptiveThreshold())),
            "sauvola" => Checksum(src.Clone(c => c.SauvolaThreshold(25, 0.2f))),
            "binary" => Checksum(src.Clone(c => c.BinaryThreshold(0.5f))),
            "rgb_blur15" => Checksum(src.CloneAs<Rgb24>().Clone(c => c.GaussianBlur(1.5f))),
            "rgb_sharpen" => Checksum(src.CloneAs<Rgb24>().Clone(c => c.GaussianSharpen(1.5f))),
            "rgb_median1" => Checksum(src.CloneAs<Rgb24>().Clone(c => c.MedianBlur(1))),
            "l8_otsu" => Checksum(src.CloneAs<L8>().Clone(c => c.OtsuThreshold())),
            "l8_sauvola" => Checksum(src.CloneAs<L8>().Clone(c => c.SauvolaThreshold(25, 0.2f))),
            "l8_bradley" => Checksum(src.CloneAs<L8>().Clone(c => c.AdaptiveThreshold(15, 0.85f))),
            _ => throw new ArgumentException(name),
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LegacyFilters_AreDeterministicUnderSingleThreadedConfiguration()
    {
        int previous = Configuration.Default.MaxDegreeOfParallelism;
        try
        {
            using Image<Rgba32> src = Synthetic();
            Configuration.Default.MaxDegreeOfParallelism = 1;
            ulong single = Checksum(src.Clone(c => c.GaussianBlur(1.5f)));
            Configuration.Default.MaxDegreeOfParallelism = 4;
            ulong multi = Checksum(src.Clone(c => c.GaussianBlur(1.5f)));
            Assert.Equal(16702821983734622917UL, single);
            Assert.Equal(single, multi);
        }
        finally
        {
            Configuration.Default.MaxDegreeOfParallelism = previous;
        }
    }

    // ----- Region overloads of the point operations -----

    [Fact]
    public void Grayscale_Rectangle_OnlyChangesRegionAndMatchesFullVersion()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> full = src.Clone(c => c.Grayscale());
        var rect = new Rectangle(10, 5, 20, 15);
        using Image<Rgba32> partial = src.Clone(c => c.Grayscale(rect));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Assert.Equal(rect.Contains(x, y) ? full[x, y] : src[x, y], partial[x, y]);
            }
        }
    }

    [Fact]
    public void Invert_Brightness_Contrast_Rectangle_MatchFullVersionsInsideRegion()
    {
        using Image<Rgba32> src = Synthetic();
        var rect = new Rectangle(-5, 3, 30, 100); // Partly outside: must be clamped.
        Rectangle clamped = Rectangle.Intersect(rect, new Rectangle(0, 0, src.Width, src.Height));

        using Image<Rgba32> inv = src.Clone(c => c.Invert(rect));
        using Image<Rgba32> invFull = src.Clone(c => c.Invert());
        using Image<Rgba32> bri = src.Clone(c => c.Brightness(1.3f, rect));
        using Image<Rgba32> briFull = src.Clone(c => c.Brightness(1.3f));
        using Image<Rgba32> con = src.Clone(c => c.Contrast(0.6f, rect));
        using Image<Rgba32> conFull = src.Clone(c => c.Contrast(0.6f));

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                bool inside = clamped.Contains(x, y);
                Assert.Equal(inside ? invFull[x, y] : src[x, y], inv[x, y]);
                Assert.Equal(inside ? briFull[x, y] : src[x, y], bri[x, y]);
                Assert.Equal(inside ? conFull[x, y] : src[x, y], con[x, y]);
            }
        }
    }

    [Fact]
    public void RegionOperations_WithRectangleOutsideImage_AreNoOps()
    {
        using Image<Rgba32> src = Synthetic();
        var outside = new Rectangle(500, 500, 10, 10);
        using Image<Rgba32> result = src.Clone(c => c
            .Invert(outside)
            .Grayscale(outside)
            .GaussianBlur(2f, outside)
            .BoxBlur(3, outside)
            .Pixelate(4, outside)
            .Filter(KnownFilterMatrices.CreateSepiaFilter(1f), outside));
        Assert.Equal(Checksum(src), Checksum(result));
    }

    // ----- Pixelate -----

    [Fact]
    public void Pixelate_FillsBlocksWithBlockAverage()
    {
        using var image = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                image[x, y] = new Rgba32((byte)(x * 30), (byte)(y * 30), 100, 255);
            }
        }

        image.Mutate(c => c.Pixelate(4));
        // Block (0..3, 0..3): mean R = (0+30+60+90)/4 = 45, mean G = 45.
        Assert.Equal(new Rgba32(45, 45, 100, 255), image[0, 0]);
        Assert.Equal(new Rgba32(45, 45, 100, 255), image[3, 3]);
        // Block (4..7, 0..3): mean R = (120+150+180+210)/4 = 165.
        Assert.Equal(new Rgba32(165, 45, 100, 255), image[5, 1]);
        Assert.Equal(image[4, 0], image[7, 3]);
    }

    [Fact]
    public void Pixelate_SizeOne_LeavesImageUnchanged()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> result = src.Clone(c => c.Pixelate(1));
        Assert.Equal(Checksum(src), Checksum(result));
    }

    [Fact]
    public void Pixelate_InvalidSize_Throws()
    {
        using Image<Rgba32> src = Synthetic();
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Mutate(c => c.Pixelate(0)));
    }

    // ----- Oil paint -----

    [Fact]
    public void OilPaint_UniformImage_Unchanged_AndTwoToneImageKeepsBothTones()
    {
        using var uniform = new Image<Rgb24>(20, 20, new Rgb24(10, 200, 30));
        uniform.Mutate(c => c.OilPaint(10, 3));
        Assert.Equal(new Rgb24(10, 200, 30), uniform[7, 11]);

        using var twoTone = new Image<Rgb24>(40, 20, new Rgb24(20, 20, 20));
        for (int y = 0; y < 20; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                twoTone[x, y] = new Rgb24(230, 230, 230);
            }
        }

        twoTone.Mutate(c => c.OilPaint(8, 2));
        Assert.Equal(new Rgb24(20, 20, 20), twoTone[5, 5]);
        Assert.Equal(new Rgb24(230, 230, 230), twoTone[35, 5]);
        foreach (Rgb24 p in new[] { twoTone[19, 3], twoTone[20, 3], twoTone[21, 3] })
        {
            Assert.True(p == new Rgb24(20, 20, 20) || p == new Rgb24(230, 230, 230), p.ToString());
        }
    }

    [Fact]
    public void OilPaint_PreservesAlphaAndRunsOnEveryFormat()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> result = src.Clone(c => c.OilPaint(6, 2));
        for (int y = 0; y < src.Height; y += 5)
        {
            for (int x = 0; x < src.Width; x += 7)
            {
                Assert.Equal(src[x, y].A, result[x, y].A);
            }
        }

        using Image<Bgr24> bgr = src.CloneAs<Bgr24>().Clone(c => c.OilPaint());
        using Image<L8> l8 = src.CloneAs<L8>().Clone(c => c.OilPaint(4, 1));
        Assert.Equal(64, bgr.Width);
        Assert.Equal(64, l8.Width);
    }

    // ----- Vignette / glow -----

    [Fact]
    public void Vignette_DarkensCornersMoreThanCentre_AndLeavesCentrePixelAlone()
    {
        using var image = new Image<Rgb24>(41, 31, new Rgb24(200, 200, 200));
        image.Mutate(c => c.Vignette());
        Rgb24 centre = image[20, 15];
        Rgb24 corner = image[0, 0];
        Rgb24 edge = image[0, 15];
        Assert.Equal(new Rgb24(200, 200, 200), centre);
        Assert.True(corner.R < edge.R, $"corner {corner} should be darker than edge midpoint {edge}");
        Assert.True(edge.R < 200);
        Assert.Equal(new Rgb24(0, 0, 0), corner); // Weight reaches 1 at the corner of the default ellipse.
    }

    [Fact]
    public void Vignette_BlendPercentage_ScalesEffect_AndColorIsHonoured()
    {
        using var image = new Image<Rgb24>(41, 31, new Rgb24(200, 200, 200));
        var half = new GraphicsOptions { BlendPercentage = 0.5f };
        image.Mutate(c => c.Vignette(Color.Blue, 0f, 0f, half));
        Rgb24 corner = image[0, 0];
        // 50 % blend of blue over grey at the corner: R,G = 100, B = 200*0.5 + 255*0.5 = 227.5.
        Assert.Equal(100, corner.R);
        Assert.Equal(100, corner.G);
        Assert.InRange(corner.B, 227, 228);
        Assert.Equal(new Rgb24(200, 200, 200), image[20, 15]);
    }

    [Fact]
    public void Glow_BrightensCentreAndFadesToZeroAtRadius()
    {
        using var image = new Image<Rgb24>(41, 41, new Rgb24(50, 50, 50));
        image.Mutate(c => c.Glow(Color.White, 10f));
        Assert.Equal(new Rgb24(255, 255, 255), image[20, 20]);
        Assert.Equal(new Rgb24(50, 50, 50), image[0, 0]);
        Assert.Equal(new Rgb24(50, 50, 50), image[20, 40]);
        Rgb24 mid = image[25, 20]; // Distance 5 of radius 10 -> weight 0.5.
        Assert.InRange(mid.R, 151, 154);
    }

    [Fact]
    public void Vignette_Rectangle_OnlyTouchesRegion()
    {
        using var image = new Image<Rgba32>(60, 40, new Rgba32(200, 200, 200, 255));
        var rect = new Rectangle(10, 10, 21, 21);
        image.Mutate(c => c.Vignette(Color.Black, rect));
        Assert.Equal(new Rgba32(200, 200, 200, 255), image[0, 0]);
        Assert.Equal(new Rgba32(200, 200, 200, 255), image[59, 39]);
        Assert.Equal(new Rgba32(200, 200, 200, 255), image[9, 10]);
        Assert.True(image[10, 10].R < 200);
        Assert.Equal(new Rgba32(200, 200, 200, 255), image[20, 20]); // Region centre untouched.
    }

    [Fact]
    public void PhotographicPresets_RunOnAllFormats()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> lomo = src.Clone(c => c.Lomograph());
        using Image<Rgba32> pola = src.Clone(c => c.Polaroid());
        using Image<Rgba32> koda = src.Clone(c => c.Kodachrome());
        using Image<Rgb24> rgb = src.CloneAs<Rgb24>().Clone(c => c.Polaroid().Lomograph().Kodachrome());
        Assert.NotEqual(Checksum(src), Checksum(lomo));
        Assert.NotEqual(Checksum(lomo), Checksum(pola));
        Assert.NotEqual(Checksum(pola), Checksum(koda));
        Assert.Equal(src.Width, rgb.Width);
    }

    // ----- Swizzle -----

    private sealed class TransposeSwizzler : ISwizzler
    {
        private readonly Size source;

        public TransposeSwizzler(Size source) => this.source = source;

        public Size DestinationSize => new(this.source.Height, this.source.Width);

        public Point Transform(Point point) => new(point.Y, point.X);
    }

    [Fact]
    public void Swizzle_TransposesImageOnEveryFrame()
    {
        using Image<Rgba32> src = TwoFrames(32, 24);
        using Image<Rgba32> result = src.Clone(c => c.Swizzle(new TransposeSwizzler(new Size(32, 24))));
        Assert.Equal(24, result.Width);
        Assert.Equal(32, result.Height);
        Assert.Equal(2, result.Frames.Count);
        for (int f = 0; f < 2; f++)
        {
            for (int y = 0; y < 24; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    Assert.Equal(src.Frames[f][x, y], result.Frames[f][y, x]);
                }
            }
        }
    }

    // ----- Bokeh blur -----

    [Fact]
    public void BokehBlur_CompositeKernelIsDiscShaped_ForEveryComponentCount()
    {
        const int radius = 16;
        for (int components = 1; components <= 6; components++)
        {
            float centre = BokehBlurOps.EvaluateKernel(radius, components, 0, 0);
            float inner = BokehBlurOps.EvaluateKernel(radius, components, 8, 0);
            float diagonal = BokehBlurOps.EvaluateKernel(radius, components, 11, 11); // Distance 0.97 r: inside.
            float corner = BokehBlurOps.EvaluateKernel(radius, components, 16, 16);   // Distance 1.41 r: outside.
            Assert.True(centre > 0, $"components={components}: centre {centre}");
            Assert.InRange(inner / centre, 0.6f, 1.6f);
            Assert.InRange(diagonal / centre, 0.6f, 1.4f);
            Assert.True(Math.Abs(corner) < 0.25f * centre, $"components={components}: corner {corner} vs centre {centre}");

            // The whole kernel sums to one.
            double sum = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    sum += BokehBlurOps.EvaluateKernel(radius, components, dx, dy);
                }
            }

            Assert.InRange(sum, 0.999, 1.001);
        }
    }

    [Fact]
    public void BokehBlur_UniformImageUnchanged_AndPointLightBloomsIntoDisc()
    {
        using var uniform = new Image<Rgb24>(40, 40, new Rgb24(90, 140, 30));
        uniform.Mutate(c => c.BokehBlur(6, 2, 3f));
        Assert.InRange(uniform[20, 20].R, 89, 91);
        Assert.InRange(uniform[0, 0].G, 139, 141);
        Assert.InRange(uniform[39, 39].B, 29, 31);

        using var point = new Image<Rgb24>(41, 41, new Rgb24(0, 0, 0));
        point[20, 20] = new Rgb24(255, 255, 255);
        point.Mutate(c => c.BokehBlur(8, 3, 3f));
        Rgb24 centre = point[20, 20];
        Rgb24 inside = point[26, 20];
        Rgb24 outside = point[32, 20];
        Assert.True(centre.R > 0);
        Assert.True(inside.R > 0);
        Assert.InRange(centre.R - inside.R, -20, 40); // Flat top: the disc is roughly uniform.
        Assert.True(outside.R < inside.R / 4, $"outside {outside.R} inside {inside.R}");
    }

    [Fact]
    public void BokehBlur_ArgumentsAreValidated_AndAllFormatsWork()
    {
        using Image<Rgba32> src = Synthetic();
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Clone(c => c.BokehBlur(0, 2, 3f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Clone(c => c.BokehBlur(4, 7, 3f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Clone(c => c.BokehBlur(4, 2, 0.5f)));
        using Image<Rgba32> a = src.Clone(c => c.BokehBlur(4));
        using Image<L8> b = src.CloneAs<L8>().Clone(c => c.BokehBlur(3, 1, 1f));
        using Image<Bgra32> d = src.CloneAs<Bgra32>().Clone(c => c.BokehBlur(3, 6, 2f, new Rectangle(5, 5, 20, 20)));
        Assert.Equal(src.Width, a.Width);
        Assert.Equal(src.Width, b.Width);
        Assert.Equal(src[0, 0], d[0, 0].ToRgba32());
    }

    // ----- Thresholds -----

    [Fact]
    public void BinaryThreshold_LuminanceModeWithWhiteBlack_MatchesClassicOverload()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> classic = src.Clone(c => c.BinaryThreshold(0.4f));
        using Image<Rgba32> modern = src.Clone(c => c.BinaryThreshold(0.4f, BinaryThresholdMode.Luminance));
        using Image<Rgba32> coloured = src.Clone(c => c.BinaryThreshold(0.4f, Color.White, Color.Black));
        Assert.Equal(Checksum(classic), Checksum(modern));
        Assert.Equal(Checksum(classic), Checksum(coloured));
    }

    [Fact]
    public void BinaryThreshold_CustomColors_AndSaturationAndChromaModes()
    {
        using var image = new Image<Rgba32>(4, 1);
        image[0, 0] = new Rgba32(255, 0, 0, 255);      // Saturated red: sat 1, chroma high.
        image[1, 0] = new Rgba32(128, 128, 128, 255);  // Grey: sat 0, chroma 0.
        image[2, 0] = new Rgba32(200, 180, 190, 255);  // Pale pink: low sat / chroma.
        image[3, 0] = new Rgba32(0, 0, 255, 255);      // Saturated blue.

        var upper = new Color(1, 2, 3);
        var lower = new Color(250, 251, 252);
        using Image<Rgba32> sat = image.Clone(c => c.BinaryThreshold(0.5f, upper, lower, BinaryThresholdMode.Saturation));
        Assert.Equal(upper.ToRgba32(), sat[0, 0]);
        Assert.Equal(lower.ToRgba32(), sat[1, 0]);
        Assert.Equal(lower.ToRgba32(), sat[2, 0]);
        Assert.Equal(upper.ToRgba32(), sat[3, 0]);

        using Image<Rgba32> chroma = image.Clone(c => c.BinaryThreshold(0.5f, upper, lower, BinaryThresholdMode.MaxChroma));
        Assert.Equal(upper.ToRgba32(), chroma[0, 0]);
        Assert.Equal(lower.ToRgba32(), chroma[1, 0]);
        Assert.Equal(lower.ToRgba32(), chroma[2, 0]);
        Assert.Equal(upper.ToRgba32(), chroma[3, 0]);

        using Image<Rgba32> lum = image.Clone(c => c.BinaryThreshold(0.5f, upper, lower, BinaryThresholdMode.Luminance, new Rectangle(0, 0, 2, 1)));
        Assert.Equal(lower.ToRgba32(), lum[0, 0]); // Red luminance 0.2126 < 0.5.
        Assert.Equal(upper.ToRgba32(), lum[1, 0]); // 128/255 >= 0.5.
        Assert.Equal(new Rgba32(200, 180, 190, 255), lum[2, 0]); // Outside rectangle: untouched.
    }

    [Fact]
    public void AdaptiveThreshold_WithColorsOnFullImage_MatchesClassicOverloadUpToColors()
    {
        using Image<Rgba32> src = Synthetic();
        using Image<Rgba32> classic = src.Clone(c => c.AdaptiveThreshold(0, 0.85f));
        using Image<Rgba32> coloured = src.Clone(c => c.AdaptiveThreshold(Color.Red, Color.Blue));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Rgba32 expected = classic[x, y] == Rgba32.White ? Color.Red.ToRgba32() : Color.Blue.ToRgba32();
                Assert.Equal(expected, coloured[x, y]);
            }
        }
    }

    [Fact]
    public void AdaptiveThreshold_Rectangle_ConfinesOutputToRegion()
    {
        using Image<Rgba32> src = Synthetic();
        var rect = new Rectangle(8, 8, 40, 30);
        using Image<Rgba32> result = src.Clone(c => c.AdaptiveThreshold(Color.White, Color.Black, 0.85f, rect));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                if (rect.Contains(x, y))
                {
                    Assert.True(result[x, y] == Rgba32.White || result[x, y] == Rgba32.Black);
                }
                else
                {
                    Assert.Equal(src[x, y], result[x, y]);
                }
            }
        }

        // The region is thresholded as its own image: equivalent to cropping first.
        using Image<Rgba32> cropped = src.Clone(c => c.Crop(rect).AdaptiveThreshold(Color.White, Color.Black, 0.85f));
        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width; x++)
            {
                Assert.Equal(cropped[x, y], result[rect.X + x, rect.Y + y]);
            }
        }
    }

    // ----- Multi-frame and pixel-format coverage -----

    [Fact]
    public void Effects_ApplyToEveryFrame()
    {
        using Image<Rgba32> src = TwoFrames();
        using Image<Rgba32> result = src.Clone(c => c.Sepia().BoxBlur(1).Pixelate(2).Vignette());
        Assert.Equal(2, result.Frames.Count);
        for (int f = 0; f < 2; f++)
        {
            using var single = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { src.Frames[f].Clone() });
            single.Mutate(c => c.Sepia().BoxBlur(1).Pixelate(2).Vignette());
            using var frameOnly = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { result.Frames[f].Clone() });
            Assert.Equal(Checksum(single), Checksum(frameOnly));
        }
    }

    [Fact]
    public void Effects_ProduceSameColorsForEveryOpaquePixelFormat()
    {
        using Image<Rgb24> rgb = Synthetic().CloneAs<Rgb24>();
        static void Pipeline(IImageProcessingContext c) => c.Sepia(0.7f).BoxBlur(2).OilPaint(5, 1).Pixelate(3).Glow(Color.Red, 10f);
        using Image<Rgb24> a = rgb.Clone(Pipeline);
        using Image<Rgba32> b = rgb.CloneAs<Rgba32>().Clone(Pipeline);
        using Image<Bgr24> d = rgb.CloneAs<Bgr24>().Clone(Pipeline);
        using Image<Bgra32> e = rgb.CloneAs<Bgra32>().Clone(Pipeline);
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba32 expected = a[x, y].ToRgba32();
                Assert.Equal(expected, b[x, y]);
                Assert.Equal(expected, d[x, y].ToRgba32());
                Assert.Equal(expected, e[x, y].ToRgba32());
            }
        }
    }
}
