using System.Numerics;
using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// <c>.cube</c> parsing, Hald CLUTs and LUT application against Pillow's <c>Color3DLUT</c> and numpy
/// implementations of trilinear and tetrahedral interpolation (<c>Fixtures/gen_lut.py</c>).
/// </summary>
public class ColorLutTests
{
    public static TheoryData<string, string, string, int> Cases()
    {
        var data = new TheoryData<string, string, string, int>();
        using JsonDocument manifest = JsonDocument.Parse(FixturePath.Read("lut/manifest.json"));
        foreach (JsonElement c in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("lut").GetString()!, c.GetProperty("interpolation").GetString()!, c.GetProperty("expected").GetString()!, c.GetProperty("tolerance").GetInt32());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ApplyMatchesIndependentReference(string lutFile, string interpolation, string expectedFile, int tolerance)
    {
        ColorLut lut = Load(lutFile);
        LutInterpolation mode = interpolation == "trilinear" ? LutInterpolation.Trilinear : LutInterpolation.Tetrahedral;
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Get("lut/src.png"));
        using Image<Rgba32> expected = Image.Load<Rgba32>(FixturePath.Get("lut/" + expectedFile));
        source.Mutate(c => c.ApplyLut(lut, mode));
        Assert.True(EffectsTests.MaxDifference(expected, source) <= tolerance, $"max difference {EffectsTests.MaxDifference(expected, source)} > {tolerance}");
    }

    [Fact]
    public void ParsesHeaderFields()
    {
        ColorLut grade = ColorLut.LoadCube(FixturePath.Get("lut/grade17.cube"));
        Assert.Equal("EasyImageSharp test grade", grade.Title);
        Assert.Equal(17, grade.CubeSize);
        Assert.Equal(0, grade.ShaperSize);
        Assert.Equal(Vector3.Zero, grade.DomainMin);
        Assert.Equal(Vector3.One, grade.DomainMax);

        ColorLut domain = ColorLut.LoadCube(FixturePath.Get("lut/domain9.cube"));
        Assert.Equal(new Vector3(-0.25f), domain.DomainMin);
        Assert.Equal(new Vector3(1.25f, 1.5f, 1.25f), domain.DomainMax);

        ColorLut both = ColorLut.LoadCube(FixturePath.Get("lut/shaper3d.cube"));
        Assert.Equal(16, both.ShaperSize);
        Assert.Equal(9, both.CubeSize);
        Assert.Null(both.Title);
    }

    [Fact]
    public void IdentityLatticeIsExactForBothInterpolations()
    {
        ColorLut identity = ColorLut.CreateIdentity(33);
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Get("lut/src.png"));
        foreach (LutInterpolation mode in new[] { LutInterpolation.Tetrahedral, LutInterpolation.Trilinear })
        {
            using Image<Rgba32> mapped = source.Clone(c => c.ApplyLut(identity, mode));
            Assert.Equal(0, EffectsTests.MaxDifference(source, mapped));
        }

        Vector3 v = identity.Apply(new Vector3(0.2f, 0.7f, 0.95f));
        Assert.Equal(0.2f, v.X, 5);
        Assert.Equal(0.7f, v.Y, 5);
        Assert.Equal(0.95f, v.Z, 5);
    }

    [Fact]
    public void TetrahedralKeepsTheGreyAxisOnGreyLattices()
    {
        // A LUT that maps every lattice point to its own luminance-like grey keeps greys grey under tetrahedral
        // interpolation because the main diagonal is an edge of every tetrahedron.
        var text = new System.Text.StringBuilder("LUT_3D_SIZE 2\n");
        foreach ((int r, int g, int b) in new[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0), (0, 0, 1), (1, 0, 1), (0, 1, 1), (1, 1, 1) })
        {
            float v = (0.3f * r) + (0.6f * g) + (0.1f * b);
            text.Append(FormattableString.Invariant($"{v} {v} {v}\n"));
        }

        ColorLut lut = ColorLut.ParseCube(text.ToString());
        Vector3 grey = lut.Apply(new Vector3(0.4f), LutInterpolation.Tetrahedral);
        Assert.Equal(0.4f, grey.X, 5);
    }

    [Fact]
    public void AmountBlendsWithTheOriginal()
    {
        ColorLut lut = Load("grade17.cube");
        using Image<Rgba32> source = Image.Load<Rgba32>(FixturePath.Get("lut/src.png"));
        using Image<Rgba32> full = source.Clone(c => c.ApplyLut(lut));
        using Image<Rgba32> half = source.Clone(c => c.ApplyLut(lut, LutInterpolation.Tetrahedral, 0.5f));
        using Image<Rgba32> none = source.Clone(c => c.ApplyLut(lut, LutInterpolation.Tetrahedral, 0f));
        Assert.Equal(0, EffectsTests.MaxDifference(source, none));
        Rgba32 s = source[30, 20], f = full[30, 20], h = half[30, 20];
        Assert.InRange(h.R, Math.Min(s.R, f.R) - 1, Math.Max(s.R, f.R) + 1);
        Assert.InRange(Math.Abs(h.R - ((s.R + f.R) / 2.0)), 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Mutate(c => c.ApplyLut(lut, LutInterpolation.Tetrahedral, 1.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Mutate(c => c.ApplyLut(lut, (LutInterpolation)9, 1f)));
        Assert.Throws<ArgumentNullException>(() => source.Mutate(c => c.ApplyLut(null!)));
    }

    [Fact]
    public void WideFormatsAvoidTheEightBitStep()
    {
        ColorLut lut = Load("grade17.cube");
        using var wide = new Image<Rgba64>(4, 1);
        wide[0, 0] = new Rgba64(10000, 20000, 30000, 65535);
        wide[1, 0] = new Rgba64(10001, 20000, 30000, 65535);
        wide.Mutate(c => c.ApplyLut(lut));
        Vector3 expected = lut.Apply(new Vector3(10000 / 65535f, 20000 / 65535f, 30000 / 65535f));
        int expectedRed = (int)Math.Round(expected.X * 65535);
        Assert.InRange(wide[0, 0].R, expectedRed - 1, expectedRed + 1);
        Assert.NotEqual(wide[0, 0].R, wide[1, 0].R); // a one-step 16-bit input difference survives
        Assert.Equal(65535, wide[0, 0].A);
    }

    [Fact]
    public void AlphaIsPreserved()
    {
        ColorLut lut = Load("grade17.cube");
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(100, 150, 200, 77);
        image.Mutate(c => c.ApplyLut(lut));
        Assert.Equal(77, image[0, 0].A);
    }

    [Fact]
    public void HaldClutMatchesTheEquivalentLattice()
    {
        using Image<Rgb24> hald = Image.Load<Rgb24>(FixturePath.Get("lut/hald4.png"));
        ColorLut lut = ColorLut.FromHaldClut(hald);
        Assert.Equal(16, lut.CubeSize);
        Assert.Throws<ArgumentException>(() => ColorLut.FromHaldClut(new Image<Rgb24>(64, 63)));
        Assert.Throws<ArgumentException>(() => ColorLut.FromHaldClut(new Image<Rgb24>(50, 50)));
    }

    [Theory]
    [InlineData("", "no LUT_1D_SIZE")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "table entries")]
    [InlineData("LUT_3D_SIZE 1\n0 0 0\n", "LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 257\n", "LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE -4\n", "LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 99999999999\n", "LUT_3D_SIZE")]
    [InlineData("LUT_1D_SIZE 65537\n", "LUT_1D_SIZE")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n1 1 1\n", "more than")]
    [InlineData("LUT_1D_SIZE 2\n0 0\n1 1 1\n", "finite numbers")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0 0\n1 1 1\n", "finite numbers")]
    [InlineData("LUT_1D_SIZE 2\n0 NaN 0\n1 1 1\n", "finite numbers")]
    [InlineData("LUT_1D_SIZE 2\n0 1e999 0\n1 1 1\n", "finite numbers")]
    [InlineData("LUT_1D_SIZE 2\n0 0,5 0\n1 1 1\n", "finite numbers")]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\nLUT_3D_SIZE 2\n", "precede")]
    [InlineData("LUT_1D_SIZE 2\nLUT_1D_SIZE 2\n", "duplicate")]
    [InlineData("LUT_3D_SIZE 2\nFOO 1\n", "unknown keyword")]
    [InlineData("TITLE unquoted\nLUT_1D_SIZE 2\n0 0 0\n1 1 1\n", "TITLE")]
    [InlineData("0 0 0\n", "data before")]
    [InlineData("LUT_1D_SIZE 2\nDOMAIN_MIN 1 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1 1 1\n", "domain")]
    [InlineData("LUT_1D_SIZE 2\nLUT_1D_INPUT_RANGE 0\n0 0 0\n1 1 1\n", "finite numbers")]
    public void RejectsMalformedCubes(string text, string messageFragment)
    {
        FormatException error = Assert.Throws<FormatException>(() => ColorLut.ParseCube(text));
        Assert.Contains(messageFragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserLimitsAreEnforcedBeforeAllocation()
    {
        // A declared 129^3 lattice is refused by a caller-chosen limit before anything is allocated.
        Assert.Throws<FormatException>(() => ColorLut.ParseCube("LUT_3D_SIZE 129\n0 0 0\n", maxCubeSize: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorLut.ParseCube("LUT_3D_SIZE 2\n", maxCubeSize: 300));
        Assert.Throws<FormatException>(() => ColorLut.ParseCube("TITLE \"" + new string('x', 5000) + "\"\n"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorLut.CreateIdentity(1));
    }

    [Fact]
    public void AcceptsCommentsBlankLinesCrLfAndResolveRanges()
    {
        string text = "# comment\r\n\r\nTITLE \"t\"\r\nLUT_1D_SIZE 2\r\nLUT_1D_INPUT_RANGE 0.0 2.0\r\n  0 0 0  \r\n# mid-data comment\r\n1\t1\t1\r\n";
        ColorLut lut = ColorLut.ParseCube(text);
        Assert.Equal(new Vector3(2f), lut.DomainMax);
        Assert.Equal(0.25f, lut.Apply(new Vector3(0.5f)).X, 5);
    }

    private static ColorLut Load(string name)
    {
        if (name.EndsWith(".png", StringComparison.Ordinal))
        {
            using Image<Rgb24> hald = Image.Load<Rgb24>(FixturePath.Get("lut/" + name));
            return ColorLut.FromHaldClut(hald);
        }

        return ColorLut.LoadCube(FixturePath.Get("lut/" + name));
    }
}
