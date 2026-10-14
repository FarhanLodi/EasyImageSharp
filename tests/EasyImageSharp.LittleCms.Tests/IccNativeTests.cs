using System.Text.Json;
using EasyImageSharp.PixelFormats;
using CoreProfile = EasyImageSharp.Metadata.Icc.IccProfile;
using Xunit;

namespace EasyImageSharp.LittleCms.Tests;

public class IccNativeTests
{
    public static IEnumerable<object[]> ReferenceCases()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "transicc-reference.json")));
        foreach (JsonElement value in document.RootElement.EnumerateArray())
            yield return [value.GetProperty("source").GetString()!, value.GetProperty("destination").GetString()!,
                value.GetProperty("intent").GetInt32(), value.GetProperty("blackPointCompensation").GetBoolean(),
                value.GetProperty("input").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
                value.GetProperty("expected").EnumerateArray().Select(v => v.GetSingle()).ToArray()];
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void MatrixCmykAndLut_MatchIndependentUpstreamCli(string inputProfile, string outputProfile, int intent, bool bpc, float[] input, float[] expected)
    {
        using IccTransform transform = IccTransform.Create(Profile(inputProfile), Profile(outputProfile),
            new IccTransformOptions { Intent = (IccRenderingIntent)intent, BlackPointCompensation = bpc });
        Assert.Equal(input.Length, transform.InputChannels);
        Assert.Equal(expected.Length, transform.OutputChannels);
        float[] actual = Enumerable.Repeat(-999f, expected.Length + 2).ToArray();
        transform.Transform(input, actual, 1);
        // CLI uses doubles; float pipelines differ slightly for extended negative RGB values.
        float tolerance = outputProfile == "test1.icc" ? 0.035f : expected.Any(v => v < 0) ? 0.001f : 0.0004f;
        for (int i = 0; i < expected.Length; i++) Assert.InRange(actual[i], expected[i] - tolerance, expected[i] + tolerance);
        Assert.Equal(-999, actual[^1]);
        Assert.Equal(-999, actual[^2]);
    }

    [Fact]
    public void NativeRuntimeIsLoadedAndSupportsModernIcc()
    {
        Assert.True(IccTransform.NativeVersion >= 2140);
        Assert.Equal("RGB ", IccTransform.CreateSrgbProfile().Header.ColorSpace);
    }

    [Fact]
    public void RgbImageConversion_PreservesAlphaSourceAndDestinationProfile()
    {
        CoreProfile srgb = Profile("srgb");
        using var image = new Image<Rgba64>(2, 1);
        image[0, 0] = new Rgba64(10001, 20003, 30007, 50009);
        image[1, 0] = new Rgba64(10002, 20004, 30008, 12345);
        using Image<Rgba64> converted = image.ConvertWithIcc(srgb, srgb);
        Assert.InRange((int)converted[0, 0].R, 10000, 10002);
        Assert.Equal(image[0, 0].A, converted[0, 0].A);
        Assert.Equal(image[1, 0].A, converted[1, 0].A);
        Assert.Equal(srgb.ToByteArray(), converted.Metadata.IccProfile!.ToByteArray());
        Assert.NotNull(converted.Frames.RootFrame.Metadata.IccProfile);
        Assert.Null(image.Metadata.IccProfile);
    }

    [Fact]
    public void CmykRowsWithPadding_ProduceExpectedOpaqueImage()
    {
        float[] samples = [0, 0, 0, 0, -999, -999, 10, 25, 50, 5];
        using Image<RgbaVector> image = IccImageExtensions.FromCmyk<RgbaVector>(samples, 1, 2, Profile("test1.icc"), Profile("srgb"), rowStride: 6);
        Assert.InRange(image[0, 0].R, 0.999f, 1.001f);
        Assert.InRange(image[0, 1].R, (217.3839f / 255) - 0.0004f, (217.3839f / 255) + 0.0004f);
        Assert.Equal(1f, image[0, 1].A);
        Assert.NotNull(image.Metadata.IccProfile);
    }

    [Fact]
    public void InvalidBuffersProfilesAndDisposedTransforms_FailBeforeWrites()
    {
        CoreProfile srgb = Profile("srgb");
        using IccTransform transform = IccTransform.Create(srgb, srgb);
        float[] destination = [42, 42, 42];
        Assert.Throws<ArgumentException>(() => transform.Transform(new float[] { 1, 2 }, destination, 1));
        Assert.Throws<ArgumentException>(() => transform.Transform(new float[] { float.NaN, 0, 0 }, destination, 1));
        Assert.Throws<ArgumentException>(() => transform.Transform(destination, destination, 1));
        Assert.Throws<OperationCanceledException>(() => transform.Transform(new float[3], destination, 1, new CancellationToken(true)));
        Assert.All(destination, value => Assert.Equal(42, value));
        Assert.Throws<ArgumentException>(() => IccTransform.Create(new CoreProfile(new byte[128]), srgb));
        Assert.Throws<ArgumentOutOfRangeException>(() => IccTransform.Create(srgb, srgb, new IccTransformOptions { Intent = (IccRenderingIntent)99 }));
        transform.Dispose();
        Assert.Throws<ObjectDisposedException>(() => transform.Transform(new float[3], destination, 1));
    }

    [Fact]
    public void ConcurrentCallsOnOneTransform_AreStable()
    {
        CoreProfile srgb = Profile("srgb");
        using IccTransform transform = IccTransform.Create(srgb, srgb);
        Parallel.For(0, 20, _ =>
        {
            float[] destination = new float[3];
            transform.Transform(new float[] { 0.1f, 0.2f, 0.3f }, destination, 1);
            Assert.InRange(destination[0], 0.09999f, 0.10001f);
        });
    }

    [Fact]
    public unsafe void GrayLabAndXyz_HaveDocumentedFloatUnitsAndD50White()
    {
        CoreProfile lab = SerializeNative(NativeCms.CreateLab(IntPtr.Zero));
        CoreProfile xyz = SerializeNative(NativeCms.CreateXyz());
        double* white = stackalloc double[3] { 0.3457, 0.3585, 1 };
        IntPtr gamma = NativeCms.BuildGamma(IntPtr.Zero, 1);
        CoreProfile gray;
        try { gray = SerializeNative(NativeCms.CreateGray(white, gamma)); }
        finally { NativeCms.FreeCurve(gamma); }
        using IccTransform grayToLab = IccTransform.Create(gray, lab);
        float[] neutral = new float[3];
        grayToLab.Transform(new float[] { 0.18f }, neutral, 1);
        Assert.InRange(neutral[0], 49.49f, 49.51f);
        Assert.InRange(Math.Abs(neutral[1]), 0, 0.02f);
        Assert.InRange(Math.Abs(neutral[2]), 0, 0.02f);
        using IccTransform labToXyz = IccTransform.Create(lab, xyz);
        float[] result = new float[3];
        labToXyz.Transform(new float[] { 100, 0, 0 }, result, 1);
        Assert.InRange(result[0], 0.9640f, 0.9644f);
        Assert.InRange(result[1], 0.9999f, 1.0001f);
        Assert.InRange(result[2], 0.8247f, 0.8251f);
    }

    private static unsafe CoreProfile SerializeNative(IntPtr native)
    {
        Assert.NotEqual(IntPtr.Zero, native);
        try
        {
            uint size = 0;
            Assert.NotEqual(0, NativeCms.SaveProfile(native, null, ref size));
            byte[] bytes = new byte[size];
            fixed (byte* pointer = bytes) Assert.NotEqual(0, NativeCms.SaveProfile(native, pointer, ref size));
            return new CoreProfile(bytes);
        }
        finally { NativeCms.CloseProfile(native); }
    }

    private static CoreProfile Profile(string name) => name == "srgb" ? IccTransform.CreateSrgbProfile()
        : new CoreProfile(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
}
