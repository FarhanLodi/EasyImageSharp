using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

public class ColorTests
{
    [Fact]
    public void Constructor_And_Factories_SetComponents()
    {
        var c = new Color(1, 2, 3);
        Assert.Equal((1, 2, 3, 255), ((int)c.R, (int)c.G, (int)c.B, (int)c.A));
        Assert.Equal(new Color(1, 2, 3, 255), Color.FromRgb(1, 2, 3));
        Assert.Equal(new Color(1, 2, 3, 4), Color.FromRgba(1, 2, 3, 4));
        Assert.Equal(new Color(1, 2, 3, 77), Color.FromRgb(1, 2, 3).WithAlpha(77));
    }

    [Theory]
    [InlineData("#F80", 0xFF, 0x88, 0x00, 0xFF)]
    [InlineData("F80", 0xFF, 0x88, 0x00, 0xFF)]
    [InlineData("#F808", 0xFF, 0x88, 0x00, 0x88)]
    [InlineData("f808", 0xFF, 0x88, 0x00, 0x88)]
    [InlineData("#FF7F50", 0xFF, 0x7F, 0x50, 0xFF)]
    [InlineData("ff7f50", 0xFF, 0x7F, 0x50, 0xFF)]
    [InlineData("#FF7F5080", 0xFF, 0x7F, 0x50, 0x80)]
    [InlineData("FF7F5080", 0xFF, 0x7F, 0x50, 0x80)]
    [InlineData("  #00000000  ", 0x00, 0x00, 0x00, 0x00)]
    public void ParseHex_AcceptsAllForms(string hex, int r, int g, int b, int a)
    {
        Assert.Equal(new Color((byte)r, (byte)g, (byte)b, (byte)a), Color.ParseHex(hex));
        Assert.True(Color.TryParseHex(hex, out Color viaTry));
        Assert.Equal(new Color((byte)r, (byte)g, (byte)b, (byte)a), viaTry);
        Assert.Equal(new Color((byte)r, (byte)g, (byte)b, (byte)a), Color.Parse(hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGG")]
    [InlineData("#12345G")]
    [InlineData("##123456")]
    [InlineData("0x123456")]
    public void ParseHex_RejectsInvalidInput(string hex)
    {
        Assert.False(Color.TryParseHex(hex, out Color color));
        Assert.Equal(default, color);
        Assert.Throws<FormatException>(() => Color.ParseHex(hex));
    }

    [Fact]
    public void ParseHex_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Color.ParseHex(null!));
        Assert.Throws<ArgumentNullException>(() => Color.Parse(null!));
        Assert.False(Color.TryParseHex(null, out _));
        Assert.False(Color.TryParse(null, out _));
    }

    [Fact]
    public void ToHex_IsUppercaseRrggbbaa_AndRoundTrips()
    {
        var c = new Color(0xAB, 0x0C, 0xDE, 0x7F);
        Assert.Equal("AB0CDE7F", c.ToHex());
        Assert.Equal(c, Color.ParseHex(c.ToHex()));
        Assert.Equal(c, Color.ParseHex("#" + c.ToHex()));
        Assert.Equal("FF000080", Color.Red.WithAlpha(128).ToHex());
        Assert.Equal("00000000", Color.Transparent.ToHex());
    }

    [Theory]
    [InlineData("Coral", 0xFF7F50)]
    [InlineData("RebeccaPurple", 0x663399)]
    [InlineData("Teal", 0x008080)]
    [InlineData("CornflowerBlue", 0x6495ED)]
    [InlineData("Lime", 0x00FF00)]
    [InlineData("Green", 0x008000)]
    [InlineData("Silver", 0xC0C0C0)]
    [InlineData("Gray", 0x808080)]
    [InlineData("Navy", 0x000080)]
    [InlineData("Olive", 0x808000)]
    [InlineData("Orange", 0xFFA500)]
    [InlineData("MediumSpringGreen", 0x00FA9A)]
    [InlineData("LightGoldenrodYellow", 0xFAFAD2)]
    [InlineData("WhiteSmoke", 0xF5F5F5)]
    public void NamedColors_MatchCssValues(string name, int rgb)
    {
        var expected = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        Assert.True(Color.TryParse(name, out Color parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void NamedColors_StaticProperties_HaveExpectedValues()
    {
        Assert.Equal(new Color(255, 127, 80), Color.Coral);
        Assert.Equal(new Color(102, 51, 153), Color.RebeccaPurple);
        Assert.Equal(new Color(0, 128, 128), Color.Teal);
        Assert.Equal(new Color(0, 0, 0, 0), Color.Transparent);
        Assert.Equal(new Color(0, 0, 0), Color.Black);
        Assert.Equal(new Color(255, 255, 255), Color.White);
        Assert.Equal(new Color(255, 0, 0), Color.Red);
        Assert.Equal(new Color(0, 0, 255), Color.Blue);
        Assert.Equal(new Color(255, 255, 0), Color.Yellow);
        Assert.Equal(new Color(0, 255, 255), Color.Cyan);
        Assert.Equal(new Color(255, 0, 255), Color.Magenta);
        Assert.Equal(new Color(128, 0, 0), Color.Maroon);
        Assert.Equal(new Color(128, 0, 128), Color.Purple);
        Assert.Equal(Color.Cyan, Color.Aqua);
        Assert.Equal(Color.Magenta, Color.Fuchsia);
        Assert.Equal(Color.Gray, Color.Grey);
    }

    [Theory]
    [InlineData("coral")]
    [InlineData("CORAL")]
    [InlineData("  Coral ")]
    public void TryParse_NamedColor_IsCaseInsensitive(string name)
    {
        Assert.True(Color.TryParse(name, out Color color));
        Assert.Equal(Color.Coral, color);
    }

    [Fact]
    public void TryParse_AcceptsHexAndTransparent()
    {
        Assert.True(Color.TryParse("#663399", out Color hex));
        Assert.Equal(Color.RebeccaPurple, hex);
        Assert.True(Color.TryParse("transparent", out Color transparent));
        Assert.Equal(Color.Transparent, transparent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAColor")]
    [InlineData("#ZZZZZZ")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("Red ish")]
    public void TryParse_RejectsUnknownInput(string value)
    {
        Assert.False(Color.TryParse(value, out Color color));
        Assert.Equal(default, color);
        Assert.Throws<FormatException>(() => Color.Parse(value));
    }

    [Fact]
    public void Conversions_ToAndFromPixelFormats()
    {
        var c = new Color(10, 20, 30, 40);

        Rgba32 rgba = c.ToRgba32();
        Assert.Equal(new Rgba32(10, 20, 30, 40), rgba);
        Assert.Equal(c, Color.FromPixel(rgba));

        Rgb24 rgb = c.ToPixel<Rgb24>();
        Assert.Equal(new Rgb24(10, 20, 30), rgb);
        Assert.Equal(new Color(10, 20, 30, 255), Color.FromPixel(rgb));

        Bgra32 bgra = c.ToPixel<Bgra32>();
        Assert.Equal(new Bgra32(10, 20, 30, 40), bgra);
        Assert.Equal(c, Color.FromPixel(bgra));

        L8 gray = Color.White.ToPixel<L8>();
        Assert.Equal(new L8(255), gray);
        Assert.Equal(Color.White, Color.FromPixel(gray));
        Assert.Equal(L8.FromRgba32(new Rgba32(10, 20, 30, 40)), c.ToPixel<L8>());
    }

    [Fact]
    public void ImplicitConversions_WithRgba32_WorkBothWays()
    {
        Rgba32 fromColor = Color.Coral;
        Assert.Equal(new Rgba32(255, 127, 80), fromColor);

        Color fromPixel = new Rgba32(1, 2, 3, 4);
        Assert.Equal(new Color(1, 2, 3, 4), fromPixel);

        // A Color can be written straight into an Rgba32 image.
        using var image = new Image<Rgba32>(2, 2);
        image[1, 1] = Color.Teal;
        Assert.Equal(new Rgba32(0, 128, 128), image[1, 1]);
    }

    [Fact]
    public void Equality_And_HashCode()
    {
        var a = new Color(1, 2, 3, 4);
        var b = new Color(1, 2, 3, 4);
        var c = new Color(1, 2, 3, 5);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals(null));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.Equal("Color [ R=1, G=2, B=3, A=4 ]", a.ToString());
        Assert.Equal(Color.Transparent, default(Color));
    }
}

public class PrimitivesTests
{
    [Fact]
    public void Point_Operators_And_Helpers()
    {
        var p = new Point(3, 4);
        Assert.Equal(new Point(8, 10), p + new Size(5, 6));
        Assert.Equal(new Point(-2, -2), p - new Size(5, 6));
        Assert.Equal(new Point(4, 6), p.Offset(1, 2));
        Assert.False(p.IsEmpty);
        Assert.True(Point.Empty.IsEmpty);

        (int x, int y) = p;
        Assert.Equal((3, 4), (x, y));
    }

    [Fact]
    public void Point_Round_Truncate_Ceiling()
    {
        Assert.Equal(new Point(2, -2), Point.Round(new PointF(1.5f, -1.5f)));
        Assert.Equal(new Point(1, -1), Point.Round(new PointF(1.4f, -1.4f)));
        Assert.Equal(new Point(1, -1), Point.Truncate(new PointF(1.9f, -1.9f)));
        Assert.Equal(new Point(2, -1), Point.Ceiling(new PointF(1.1f, -1.9f)));
    }

    [Fact]
    public void Size_Operators_And_Helpers()
    {
        var s = new Size(2, 3);
        Assert.Equal(new Size(3, 5), s + new Size(1, 2));
        Assert.Equal(new Size(1, 1), s - new Size(1, 2));
        Assert.Equal(new Size(6, 9), s * 3);
        Assert.Equal(new Size(6, 9), 3 * s);
        Assert.Equal(new Size(4), new Size(4, 4));

        Assert.Equal(new Size(2, 4), Size.Round(new SizeF(1.5f, 3.5f)));
        Assert.Equal(new Size(1, 3), Size.Truncate(new SizeF(1.9f, 3.9f)));
        Assert.Equal(new Size(2, 4), Size.Ceiling(new SizeF(1.1f, 3.1f)));

        (int w, int h) = s;
        Assert.Equal((2, 3), (w, h));
    }

    [Fact]
    public void Rectangle_FromLTRB_Inflate_Offset_Deconstruct()
    {
        Rectangle r = Rectangle.FromLTRB(10, 20, 30, 50);
        Assert.Equal(new Rectangle(10, 20, 20, 30), r);
        Assert.Equal((10, 20, 30, 50), (r.Left, r.Top, r.Right, r.Bottom));
        Assert.Equal(new Point(10, 20), r.Location);
        Assert.Equal(new Size(20, 30), r.Size);

        Assert.Equal(new Rectangle(8, 17, 24, 36), r.Inflate(2, 3));
        Assert.Equal(new Rectangle(12, 23, 16, 24), r.Inflate(-2, -3));
        Assert.Equal(new Rectangle(15, 15, 20, 30), r.Offset(5, -5));
        Assert.Equal(r, r.Inflate(0, 0).Offset(0, 0)); // Immutable: originals untouched.

        (int x, int y, int w, int h) = r;
        Assert.Equal((10, 20, 20, 30), (x, y, w, h));
    }

    [Fact]
    public void Rectangle_Union_Intersect_Contains()
    {
        var a = new Rectangle(0, 0, 10, 10);
        var b = new Rectangle(5, 5, 10, 10);

        Assert.Equal(new Rectangle(0, 0, 15, 15), Rectangle.Union(a, b));
        Assert.Equal(new Rectangle(5, 5, 5, 5), Rectangle.Intersect(a, b));
        Assert.Equal(b, Rectangle.Union(Rectangle.Empty, b));
        Assert.Equal(a, Rectangle.Union(a, Rectangle.Empty));
        Assert.Equal(Rectangle.Empty, Rectangle.Intersect(a, new Rectangle(20, 20, 5, 5)));

        Assert.True(a.Contains(0, 0));
        Assert.True(a.Contains(new Point(9, 9)));
        Assert.False(a.Contains(10, 10));
        Assert.True(a.Contains(new Rectangle(2, 2, 8, 8)));
        Assert.False(a.Contains(b));
        Assert.True(a.IntersectsWith(b));
        Assert.False(a.IntersectsWith(new Rectangle(10, 0, 5, 5)));
        Assert.True(Rectangle.Empty.IsEmpty);
        Assert.False(a.IsEmpty);
    }

    [Fact]
    public void RectangleF_Basics()
    {
        var r = new RectangleF(1.5f, 2.5f, 3f, 4f);
        Assert.Equal((1.5f, 2.5f, 4.5f, 6.5f), (r.Left, r.Top, r.Right, r.Bottom));
        Assert.Equal(new PointF(1.5f, 2.5f), r.Location);
        Assert.Equal(new SizeF(3f, 4f), r.Size);
        Assert.Equal(r, new RectangleF(new PointF(1.5f, 2.5f), new SizeF(3f, 4f)));
        Assert.Equal(r, RectangleF.FromLTRB(1.5f, 2.5f, 4.5f, 6.5f));
        Assert.False(r.IsEmpty);
        Assert.True(RectangleF.Empty.IsEmpty);

        Assert.True(r.Contains(1.5f, 2.5f));
        Assert.True(r.Contains(new PointF(4.4f, 6.4f)));
        Assert.False(r.Contains(4.5f, 6.5f));
        Assert.True(r.Contains(new RectangleF(2f, 3f, 1f, 1f)));

        Assert.Equal(new RectangleF(0.5f, 1.5f, 5f, 6f), r.Inflate(1f, 1f));
        Assert.Equal(new RectangleF(2.5f, 2f, 3f, 4f), r.Offset(1f, -0.5f));

        var other = new RectangleF(3f, 3f, 5f, 5f);
        Assert.Equal(new RectangleF(3f, 3f, 1.5f, 3.5f), RectangleF.Intersect(r, other));
        Assert.Equal(new RectangleF(1.5f, 2.5f, 6.5f, 5.5f), RectangleF.Union(r, other));
        Assert.Equal(other, RectangleF.Union(RectangleF.Empty, other));
        Assert.Equal(RectangleF.Empty, RectangleF.Intersect(r, new RectangleF(10f, 10f, 1f, 1f)));
        Assert.True(r.IntersectsWith(other));
        Assert.False(r.IntersectsWith(new RectangleF(4.5f, 0f, 1f, 1f)));

        (float x, float y, float w, float h) = r;
        Assert.Equal((1.5f, 2.5f, 3f, 4f), (x, y, w, h));

        Assert.True(r == new RectangleF(1.5f, 2.5f, 3f, 4f));
        Assert.True(r != other);
        Assert.Equal(r.GetHashCode(), new RectangleF(1.5f, 2.5f, 3f, 4f).GetHashCode());
        Assert.Equal("RectangleF [ X=1.5, Y=2.5, Width=3, Height=4 ]", r.ToString());
    }

    [Fact]
    public void PointF_And_SizeF_Basics()
    {
        var p = new PointF(1.5f, -2.5f);
        Assert.Equal(new PointF(3f, -1f), p + new SizeF(1.5f, 1.5f));
        Assert.Equal(new PointF(0f, -4f), p - new SizeF(1.5f, 1.5f));
        Assert.Equal(new PointF(2.5f, -2.5f), p.Offset(1f, 0f));
        Assert.True(PointF.Empty.IsEmpty);
        Assert.False(p.IsEmpty);
        Assert.True(p == new PointF(1.5f, -2.5f));
        Assert.True(p != PointF.Empty);
        Assert.Equal("PointF [ X=1.5, Y=-2.5 ]", p.ToString());

        var s = new SizeF(2f, 3f);
        Assert.Equal(new SizeF(3f, 5f), s + new SizeF(1f, 2f));
        Assert.Equal(new SizeF(1f, 1f), s - new SizeF(1f, 2f));
        Assert.Equal(new SizeF(1f, 1.5f), s * 0.5f);
        Assert.Equal(new SizeF(1f, 1.5f), 0.5f * s);
        Assert.Equal(new SizeF(4f), new SizeF(4f, 4f));
        Assert.True(SizeF.Empty.IsEmpty);
        Assert.Equal("SizeF [ Width=2, Height=3 ]", s.ToString());

        (float x, float y) = p;
        (float w, float h) = s;
        Assert.Equal((1.5f, -2.5f, 2f, 3f), (x, y, w, h));
    }

    [Fact]
    public void Rectangle_Round_Truncate_Ceiling()
    {
        var r = new RectangleF(1.5f, 2.4f, 3.5f, 4.6f);
        Assert.Equal(new Rectangle(2, 2, 4, 5), Rectangle.Round(r));
        Assert.Equal(new Rectangle(1, 2, 3, 4), Rectangle.Truncate(r));
        Assert.Equal(new Rectangle(2, 3, 4, 5), Rectangle.Ceiling(r));
        Assert.Equal(new Rectangle(-2, -3, 1, 1), Rectangle.Round(new RectangleF(-1.5f, -2.5f, 0.5f, 1.4f)));
        Assert.Equal(new Rectangle(-1, -2, 0, 1), Rectangle.Truncate(new RectangleF(-1.5f, -2.5f, 0.5f, 1.4f)));
        Assert.Equal(new Rectangle(-1, -2, 1, 2), Rectangle.Ceiling(new RectangleF(-1.5f, -2.5f, 0.5f, 1.4f)));
    }

    [Fact]
    public void ImplicitConversions_IntToFloat()
    {
        PointF p = new Point(3, 4);
        SizeF s = new Size(5, 6);
        RectangleF r = new Rectangle(1, 2, 3, 4);

        Assert.Equal(new PointF(3f, 4f), p);
        Assert.Equal(new SizeF(5f, 6f), s);
        Assert.Equal(new RectangleF(1f, 2f, 3f, 4f), r);

        // Int primitives are usable wherever float ones are expected.
        Assert.True(r.Contains(new Point(1, 2)));
        Assert.Equal(new RectangleF(1f, 2f, 3f, 4f), RectangleF.Union(RectangleF.Empty, new Rectangle(1, 2, 3, 4)));
        Assert.Equal(new Rectangle(1, 2, 3, 4), Rectangle.Round(new Rectangle(1, 2, 3, 4)));
    }
}
