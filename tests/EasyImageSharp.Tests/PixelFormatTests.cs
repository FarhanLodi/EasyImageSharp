using System.Numerics;
using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Per-format behaviour: memory layout, construction, equality, string form and the two conversion
/// contracts (<c>FromRgba32</c>/<c>ToRgba32</c> and <c>FromScaledVector4</c>/<c>ToScaledVector4</c>).
/// </summary>
public class PixelFormatTests
{
    public static TheoryData<Type, int> FormatSizes => new()
    {
        { typeof(Rgba32), 4 },
        { typeof(Rgb24), 3 },
        { typeof(Bgra32), 4 },
        { typeof(Bgr24), 3 },
        { typeof(L8), 1 },
        { typeof(A8), 1 },
        { typeof(La16), 2 },
        { typeof(L16), 2 },
        { typeof(La32), 4 },
        { typeof(Argb32), 4 },
        { typeof(Abgr32), 4 },
        { typeof(Rgb48), 6 },
        { typeof(Rgba64), 8 },
        { typeof(RgbaVector), 16 },
    };

    [Theory]
    [MemberData(nameof(FormatSizes))]
    public void Format_HasExpectedSize(Type format, int expectedSize)
        => Assert.Equal(expectedSize, Marshal.SizeOf(format));

    [Fact]
    public void ByteOrderedFormats_StoreComponentsInDeclaredOrder()
    {
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Bytes(new Rgba32(1, 2, 3, 4)));
        Assert.Equal(new byte[] { 3, 2, 1, 4 }, Bytes(new Bgra32(1, 2, 3, 4)));
        Assert.Equal(new byte[] { 4, 1, 2, 3 }, Bytes(new Argb32(1, 2, 3, 4)));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, Bytes(new Abgr32(1, 2, 3, 4)));
        Assert.Equal(new byte[] { 1, 2, 3 }, Bytes(new Rgb24(1, 2, 3)));
        Assert.Equal(new byte[] { 3, 2, 1 }, Bytes(new Bgr24(1, 2, 3)));
        Assert.Equal(new byte[] { 7, 9 }, Bytes(new La16(7, 9)));
    }

    [Fact]
    public void SixteenBitFormats_StoreComponentsInDeclaredOrder()
    {
        Assert.Equal(new ushort[] { 1, 2, 3 }, Components(new Rgb48(1, 2, 3)));
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, Components(new Rgba64(1, 2, 3, 4)));
        Assert.Equal(new ushort[] { 5, 6 }, Components(new La32(5, 6)));
    }

    [Fact]
    public void Constructors_DefaultAlphaToOpaque()
    {
        Assert.Equal(255, new Rgba32(1, 2, 3).A);
        Assert.Equal(255, new Bgra32(1, 2, 3).A);
        Assert.Equal(255, new Argb32(1, 2, 3).A);
        Assert.Equal(255, new Abgr32(1, 2, 3).A);
        Assert.Equal(255, new La16(7).A);
        Assert.Equal(ushort.MaxValue, new Rgba64(1, 2, 3).A);
        Assert.Equal(ushort.MaxValue, new La32(7).A);
        Assert.Equal(1f, new RgbaVector(0.1f, 0.2f, 0.3f).A);
    }

    [Fact]
    public void Rgba32_RoundTripsThroughEveryEightBitFormat()
    {
        var source = new Rgba32(10, 120, 250, 77);

        Assert.Equal(source, Rgba32.FromRgba32(source).ToRgba32());
        Assert.Equal(source, Bgra32.FromRgba32(source).ToRgba32());
        Assert.Equal(source, Argb32.FromRgba32(source).ToRgba32());
        Assert.Equal(source, Abgr32.FromRgba32(source).ToRgba32());

        // Formats without an alpha component report opaque.
        Assert.Equal(new Rgba32(10, 120, 250), Rgb24.FromRgba32(source).ToRgba32());
        Assert.Equal(new Rgba32(10, 120, 250), Bgr24.FromRgba32(source).ToRgba32());
    }

    [Fact]
    public void A8_KeepsOnlyAlpha()
    {
        A8 pixel = A8.FromRgba32(new Rgba32(10, 120, 250, 77));

        Assert.Equal(77, pixel.PackedValue);
        Assert.Equal(new Rgba32(0, 0, 0, 77), pixel.ToRgba32());
        Assert.Equal(new Vector4(0f, 0f, 0f, 77f / 255f), pixel.ToScaledVector4());
        Assert.Equal(new A8(128), A8.FromScaledVector4(new Vector4(1f, 1f, 1f, 128f / 255f)));
    }

    [Fact]
    public void LuminanceFormats_UseBt709Coefficients()
    {
        var source = new Rgba32(200, 100, 50, 128);
        byte expected = (byte)Math.Clamp((int)((200 * 0.2126f) + (100 * 0.7152f) + (50 * 0.0722f) + 0.5f), 0, 255);

        Assert.Equal(expected, L8.FromRgba32(source).PackedValue);
        Assert.Equal(expected, La16.FromRgba32(source).L);
        Assert.Equal(128, La16.FromRgba32(source).A);

        // The 16-bit variants carry the same luma with the sub-8-bit part kept.
        Assert.InRange(L16.FromRgba32(source).PackedValue, (ushort)((expected - 1) * 257), (ushort)((expected + 1) * 257));
        Assert.Equal(128 * 257, La32.FromRgba32(source).A);
    }

    [Fact]
    public void LuminanceFormats_RoundTripGrayValues()
    {
        for (int v = 0; v <= 255; v++)
        {
            var gray = new Rgba32((byte)v, (byte)v, (byte)v);

            Assert.Equal(v, L8.FromRgba32(gray).PackedValue);
            Assert.Equal(v, La16.FromRgba32(gray).L);
            Assert.Equal(v * 257, L16.FromRgba32(gray).PackedValue);
            Assert.Equal(v, L16.FromRgba32(gray).ToRgba32().R);
            Assert.Equal(v, La32.FromRgba32(gray).ToRgba32().R);
        }
    }

    [Fact]
    public void ScaledVector4_IsRedGreenBlueAlphaOrder()
    {
        Vector4 v = new Rgba32(0, 51, 102, 153).ToScaledVector4();

        Assert.Equal(0f, v.X, 6);
        Assert.Equal(51f / 255f, v.Y, 6);
        Assert.Equal(102f / 255f, v.Z, 6);
        Assert.Equal(153f / 255f, v.W, 6);
    }

    [Fact]
    public void ScaledVector4_FormatsWithoutAlphaReportOpaque()
    {
        Assert.Equal(1f, new Rgb24(1, 2, 3).ToScaledVector4().W);
        Assert.Equal(1f, new Bgr24(1, 2, 3).ToScaledVector4().W);
        Assert.Equal(1f, new L8(4).ToScaledVector4().W);
        Assert.Equal(1f, new L16(4).ToScaledVector4().W);
        Assert.Equal(1f, new Rgb48(1, 2, 3).ToScaledVector4().W);
    }

    [Fact]
    public void ScaledVector4_RoundTripsEveryEightBitValue()
    {
        for (int v = 0; v <= 255; v++)
        {
            var source = new Rgba32((byte)v, (byte)(255 - v), (byte)v, (byte)(255 - v));
            Vector4 scaled = source.ToScaledVector4();

            Assert.Equal(source, Rgba32.FromScaledVector4(scaled));
            Assert.Equal(source, Bgra32.FromScaledVector4(scaled).ToRgba32());
            Assert.Equal(source, Argb32.FromScaledVector4(scaled).ToRgba32());
            Assert.Equal(source, Abgr32.FromScaledVector4(scaled).ToRgba32());
        }
    }

    [Fact]
    public void ScaledVector4_ClampsOutOfRangeInputForIntegerFormats()
    {
        var wild = new Vector4(-3f, 4f, float.NaN, 0.5f);

        Assert.Equal(new Rgba32(0, 255, 0, 128), Rgba32.FromScaledVector4(wild));
        Assert.Equal(new Rgb24(0, 255, 0), Rgb24.FromScaledVector4(wild));
        Assert.Equal(new Rgba64(0, 65535, 0, 32768), Rgba64.FromScaledVector4(wild));
        Assert.Equal(new Rgb48(0, 65535, 0), Rgb48.FromScaledVector4(wild));
        Assert.Equal(new A8(128), A8.FromScaledVector4(wild));
    }

    [Fact]
    public void Equality_AndHashCode_AreComponentWise()
    {
        AssertEquality(new Rgba32(1, 2, 3, 4), new Rgba32(1, 2, 3, 4), new Rgba32(1, 2, 3, 5));
        AssertEquality(new Rgb24(1, 2, 3), new Rgb24(1, 2, 3), new Rgb24(1, 2, 4));
        AssertEquality(new Bgra32(1, 2, 3, 4), new Bgra32(1, 2, 3, 4), new Bgra32(1, 2, 3, 5));
        AssertEquality(new Bgr24(1, 2, 3), new Bgr24(1, 2, 3), new Bgr24(1, 2, 4));
        AssertEquality(new L8(1), new L8(1), new L8(2));
        AssertEquality(new A8(1), new A8(1), new A8(2));
        AssertEquality(new La16(1, 2), new La16(1, 2), new La16(1, 3));
        AssertEquality(new L16(1), new L16(1), new L16(2));
        AssertEquality(new La32(1, 2), new La32(1, 2), new La32(1, 3));
        AssertEquality(new Argb32(1, 2, 3, 4), new Argb32(1, 2, 3, 4), new Argb32(1, 2, 3, 5));
        AssertEquality(new Abgr32(1, 2, 3, 4), new Abgr32(1, 2, 3, 4), new Abgr32(1, 2, 3, 5));
        AssertEquality(new Rgb48(1, 2, 3), new Rgb48(1, 2, 3), new Rgb48(1, 2, 4));
        AssertEquality(new Rgba64(1, 2, 3, 4), new Rgba64(1, 2, 3, 4), new Rgba64(1, 2, 3, 5));
        AssertEquality(new RgbaVector(1f, 2f, 3f, 4f), new RgbaVector(1f, 2f, 3f, 4f), new RgbaVector(1f, 2f, 3f, 5f));
    }

    [Fact]
    public void Operators_MatchEquals()
    {
        Assert.True(new Rgb48(1, 2, 3) == new Rgb48(1, 2, 3));
        Assert.True(new Rgb48(1, 2, 3) != new Rgb48(1, 2, 4));
        Assert.True(new Rgba64(1, 2, 3, 4) == new Rgba64(1, 2, 3, 4));
        Assert.True(new Rgba64(1, 2, 3, 4) != new Rgba64(1, 2, 3, 5));
        Assert.True(new L16(1) == new L16(1));
        Assert.True(new L16(1) != new L16(2));
        Assert.True(new La16(1, 2) == new La16(1, 2));
        Assert.True(new La16(1, 2) != new La16(2, 2));
        Assert.True(new La32(1, 2) == new La32(1, 2));
        Assert.True(new La32(1, 2) != new La32(2, 2));
        Assert.True(new A8(1) == new A8(1));
        Assert.True(new A8(1) != new A8(2));
        Assert.True(new Argb32(1, 2, 3, 4) == new Argb32(1, 2, 3, 4));
        Assert.True(new Argb32(1, 2, 3, 4) != new Argb32(1, 2, 3, 5));
        Assert.True(new Abgr32(1, 2, 3, 4) == new Abgr32(1, 2, 3, 4));
        Assert.True(new Abgr32(1, 2, 3, 4) != new Abgr32(1, 2, 3, 5));
        Assert.True(new RgbaVector(1f, 2f, 3f) == new RgbaVector(1f, 2f, 3f));
        Assert.True(new RgbaVector(1f, 2f, 3f) != new RgbaVector(1f, 2f, 4f));
    }

    [Fact]
    public void ToString_NamesTheFormatAndListsComponentsInStorageOrder()
    {
        Assert.Equal("Rgba32(1, 2, 3, 4)", new Rgba32(1, 2, 3, 4).ToString());
        Assert.Equal("Rgb24(1, 2, 3)", new Rgb24(1, 2, 3).ToString());
        Assert.Equal("Bgra32(3, 2, 1, 4)", new Bgra32(1, 2, 3, 4).ToString());
        Assert.Equal("Bgr24(3, 2, 1)", new Bgr24(1, 2, 3).ToString());
        Assert.Equal("L8(9)", new L8(9).ToString());
        Assert.Equal("A8(9)", new A8(9).ToString());
        Assert.Equal("La16(9, 8)", new La16(9, 8).ToString());
        Assert.Equal("L16(9)", new L16(9).ToString());
        Assert.Equal("La32(9, 8)", new La32(9, 8).ToString());
        Assert.Equal("Argb32(4, 1, 2, 3)", new Argb32(1, 2, 3, 4).ToString());
        Assert.Equal("Abgr32(4, 3, 2, 1)", new Abgr32(1, 2, 3, 4).ToString());
        Assert.Equal("Rgb48(1, 2, 3)", new Rgb48(1, 2, 3).ToString());
        Assert.Equal("Rgba64(1, 2, 3, 4)", new Rgba64(1, 2, 3, 4).ToString());
        Assert.Equal("RgbaVector(0.5, 0.25, 0, 1)", new RgbaVector(0.5f, 0.25f, 0f, 1f).ToString());
    }

    [Fact]
    public void ToString_ForFloatComponentsIsCultureInvariant()
    {
        // Only this thread is touched, so parallel test collections are unaffected.
        System.Globalization.CultureInfo original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("RgbaVector(0.5, 0.25, 0, 1)", new RgbaVector(0.5f, 0.25f, 0f, 1f).ToString());
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Default_IsTransparentBlackForAlphaFormats()
    {
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Rgba32));
        Assert.Equal(Rgba32.Transparent, default(Rgba32));
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Bgra32).ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Argb32).ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Abgr32).ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Rgba64).ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(RgbaVector).ToRgba32());
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(A8).ToRgba32());
    }

    [Fact]
    public void KnownColors_MatchTheirComponents()
    {
        Assert.Equal(new Rgba32(0, 0, 0, 255), Rgba32.Black);
        Assert.Equal(new Rgba32(255, 255, 255, 255), Rgba32.White);
        Assert.Equal(default, Rgba32.Transparent);
    }

    private static void AssertEquality<T>(T value, T same, T different)
        where T : unmanaged, IEquatable<T>
    {
        Assert.True(value.Equals(same));
        Assert.False(value.Equals(different));
        Assert.True(value.Equals((object)same));
        Assert.False(value.Equals(null));
        // Equal values must hash equally; the reverse is not asserted because hash codes may collide.
        Assert.Equal(value.GetHashCode(), same.GetHashCode());
    }

    private static byte[] Bytes<T>(T value)
        where T : unmanaged
        => MemoryMarshal.AsBytes(new[] { value }.AsSpan()).ToArray();

    private static ushort[] Components<T>(T value)
        where T : unmanaged
        => MemoryMarshal.Cast<T, ushort>(new[] { value }.AsSpan()).ToArray();
}
