using System.Buffers.Binary;
using System.Numerics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

public class ColorSpaceTests
{
    [Fact]
    public void SrgbPrimaryAndWhite_MatchD65Reference()
    {
        Close(new(0.4123908f, 0.212639f, 0.0193308f), ColorSpaceConverter.SrgbToXyz(Vector3.UnitX), 0.000001f);
        Close(new(0.9504559f, 1, 1.0890578f), ColorSpaceConverter.SrgbToXyz(Vector3.One), 0.000001f);
        Close(new(100, 0, 0), ColorSpaceConverter.XyzToLab(ColorSpaceConverter.SrgbToXyz(Vector3.One)), 0.0001f);
        Close(new(53.2371f, 80.0901f, 67.2033f), ColorSpaceConverter.XyzToLab(ColorSpaceConverter.SrgbToXyz(Vector3.UnitX)), 0.002f);
        Close(new(50, 0, 0), ColorSpaceConverter.LchToLab(new(50, 0, 123)), 0.00001f);
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 0f)]
    [InlineData(0f, 1f, 0f, 120f)]
    [InlineData(0f, 0f, 1f, 240f)]
    public void HueSpaces_MatchPrimaryReferences(float r, float g, float b, float hue)
    {
        var rgb = new Vector3(r, g, b);
        Close(new(hue, 1, 1), ColorSpaceConverter.SrgbToHsv(rgb), 0.00001f);
        Close(new(hue, 1, 0.5f), ColorSpaceConverter.SrgbToHsl(rgb), 0.00001f);
        Close(rgb, ColorSpaceConverter.HsvToSrgb(new(hue, 1, 1)), 0.00001f);
        Close(rgb, ColorSpaceConverter.HslToSrgb(new(hue - 360, 1, 0.5f)), 0.00001f);
    }

    [Fact]
    public void ColorRoundtrips_PreserveExtendedRangeAndChromaticity()
    {
        Vector3 rgb = new(-0.2f, 0.42f, 1.3f);
        Close(rgb, ColorSpaceConverter.XyzToSrgb(ColorSpaceConverter.SrgbToXyz(rgb)), 0.000005f);
        Vector3 xyz = ColorSpaceConverter.SrgbToXyz(new(0.2f, 0.6f, 0.9f));
        Close(xyz, ColorSpaceConverter.LabToXyz(ColorSpaceConverter.XyzToLab(xyz)), 0.000005f);
        Close(xyz, ColorSpaceConverter.XyzD50ToD65(ColorSpaceConverter.XyzD65ToD50(xyz)), 0.000005f);
        Vector3 lab = new(60, -25, 40);
        Close(lab, ColorSpaceConverter.LchToLab(ColorSpaceConverter.LabToLch(lab)), 0.00005f);
    }

    [Fact]
    public void InvalidColorInputs_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorSpaceConverter.SrgbToXyz(new(float.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorSpaceConverter.HsvToSrgb(new(0, 2, 0.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorSpaceConverter.SrgbToHsl(new(-1, 0, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IccMatrixCurves_ProduceKnownLinearValuesAndInverse(bool parametric)
    {
        IccMatrixProfile profile = IccMatrixProfile.Create(Profile(parametric));
        Vector3 xyz = profile.ToXyzD50(new(0.5f, 0.25f, 0.75f));
        // Synthetic identity colorant matrix with exact gamma 2: PCS is independently x squared.
        Close(new(0.25f, 0.0625f, 0.5625f), xyz, 0.000001f);
        Close(new(0.5f, 0.25f, 0.75f), profile.FromXyzD50(xyz), 0.000001f);
    }

    [Fact]
    public void IccConversion_PreservesAlphaSourceAndClearsStaleProfiles()
    {
        IccProfile profile = Profile(false);
        using var image = new Image<RgbaVector>(1, 1, new RgbaVector(0.5f, 0.25f, 0.75f, 0.123456f));
        image.Metadata.IccProfile = profile;
        image.Frames.RootFrame.Metadata.IccProfile = profile;
        IccMatrixProfile converter = IccMatrixProfile.Create(profile);
        using Image<RgbaVector> result = converter.ConvertToSrgb(image);
        Assert.Equal(0.123456f, result[0, 0].A);
        Assert.Equal(0.5f, image[0, 0].R);
        Assert.Null(result.Metadata.IccProfile);
        Assert.Null(result.Frames.RootFrame.Metadata.IccProfile);
        Assert.NotNull(image.Metadata.IccProfile);
    }

    [Theory]
    [InlineData(0, 0.25f)]
    [InlineData(1, 0.25f)]
    [InlineData(2, 0.375f)]
    [InlineData(3, 0.25f)]
    [InlineData(4, 0.375f)]
    public void IccParametricCurveTypes_MatchIndependentEquations(int kind, float expected)
    {
        float[] parameters = kind switch
        {
            0 => [2], 1 => [2, 1, 0], 2 => [2, 1, 0, 0.125f],
            3 => [2, 1, 0, 0.25f, 0.25f], _ => [2, 1, 0, 0.25f, 0.25f, 0.125f, 0.125f],
        };
        byte[] curve = new byte[12 + (4 * parameters.Length)];
        "para"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(8), (ushort)kind);
        for (int i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(12 + (4 * i)), (int)(parameters[i] * 65536));
        IccMatrixProfile profile = IccMatrixProfile.Create(ProfileWithCurve(curve));
        Close(new(expected), profile.ToXyzD50(new(0.5f)), 0.000001f);
        Close(new(0.5f), profile.FromXyzD50(new(expected)), 0.000001f);
        if (kind is 3 or 4)
            Close(new((0.25f * 0.125f) + (kind == 4 ? 0.125f : 0)), profile.ToXyzD50(new(0.125f)), 0.000001f);
    }

    [Fact]
    public void IccSampledCurve_InterpolatesAndRejectsDecreasingSamples()
    {
        byte[] curve = new byte[18];
        "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(14), 16384);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(16), 65535);
        IccMatrixProfile profile = IccMatrixProfile.Create(ProfileWithCurve(curve));
        Close(new(8192 / 65535f), profile.ToXyzD50(new(0.25f)), 0.000001f);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(16), 8192);
        Assert.Throws<NotSupportedException>(() => IccMatrixProfile.Create(ProfileWithCurve(curve)));
    }

    private static IccProfile ProfileWithCurve(byte[] curve)
    {
        const int offset = 132 + (6 * 12) + 60;
        byte[] bytes = new byte[offset + curve.Length];
        Profile(false).ToByteArray().AsSpan(0, offset).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        curve.CopyTo(bytes, offset);
        for (int i = 3; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(132 + (12 * i) + 4), offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(132 + (12 * i) + 8), (uint)curve.Length);
        }
        return new(bytes);
    }

    [Fact]
    public void IccUnrelatedSharedPayloads_DoNotAmplifyAllocation()
    {
        const int extraTags = 128;
        const int payloadSize = 1024 * 1024;
        byte[] original = Profile(false).ToByteArray();
        const int originalTableEnd = 132 + (6 * 12);
        int extraTableBytes = extraTags * 12;
        byte[] bytes = new byte[original.Length + extraTableBytes + payloadSize];
        original.AsSpan(0, originalTableEnd).CopyTo(bytes);
        original.AsSpan(originalTableEnd).CopyTo(bytes.AsSpan(originalTableEnd + extraTableBytes));
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 6 + extraTags);
        for (int i = 0; i < 6; i++)
        {
            Span<byte> offset = bytes.AsSpan(132 + (12 * i) + 4);
            BinaryPrimitives.WriteUInt32BigEndian(offset, BinaryPrimitives.ReadUInt32BigEndian(offset) + (uint)extraTableBytes);
        }
        int payloadOffset = original.Length + extraTableBytes;
        for (int i = 0; i < extraTags; i++)
        {
            int entry = originalTableEnd + (12 * i);
            System.Text.Encoding.ASCII.GetBytes($"z{i:000}", bytes.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)payloadOffset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), payloadSize);
        }
        var profile = new IccProfile(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        IccMatrixProfile.Create(profile);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Parsing allocated {allocated} bytes for shared payloads.");
    }

    [Fact]
    public void IccUnsupportedAndMalformedProfiles_AreExplicitlyRejected()
    {
        byte[] bytes = Profile(false).ToByteArray();
        "CMYK"u8.CopyTo(bytes.AsSpan(16));
        Assert.Throws<NotSupportedException>(() => IccMatrixProfile.Create(new IccProfile(bytes)));
        bytes = Profile(false).ToByteArray();
        "A2B0"u8.CopyTo(bytes.AsSpan(132));
        Assert.Throws<NotSupportedException>(() => IccMatrixProfile.Create(new IccProfile(bytes)));
        bytes = Profile(false).ToByteArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136), uint.MaxValue);
        Assert.Throws<ArgumentException>(() => IccMatrixProfile.Create(new IccProfile(bytes)));
        Assert.Throws<ArgumentException>(() => IccMatrixProfile.Create(new IccProfile(new byte[16])));
    }

    private static IccProfile Profile(bool parametric)
    {
        const int tableEnd = 132 + (6 * 12);
        int curveSize = parametric ? 16 : 14;
        byte[] bytes = new byte[tableEnd + 60 + (3 * 16)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        bytes[8] = 4;
        "mntr"u8.CopyTo(bytes.AsSpan(12));
        "RGB "u8.CopyTo(bytes.AsSpan(16));
        "XYZ "u8.CopyTo(bytes.AsSpan(20));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 6);
        string[] names = ["rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"];
        for (int i = 0; i < 6; i++)
        {
            int entry = 132 + (12 * i), offset = i < 3 ? tableEnd + (20 * i) : tableEnd + 60 + (16 * (i - 3));
            System.Text.Encoding.ASCII.GetBytes(names[i], bytes.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)(i < 3 ? 20 : curveSize));
            if (i < 3)
            {
                "XYZ "u8.CopyTo(bytes.AsSpan(offset));
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset + 8 + (4 * i)), 65536);
            }
            else if (parametric)
            {
                "para"u8.CopyTo(bytes.AsSpan(offset));
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset + 12), 2 * 65536);
            }
            else
            {
                "curv"u8.CopyTo(bytes.AsSpan(offset));
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 1);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 12), 2 * 256);
            }
        }
        return new(bytes);
    }

    private static void Close(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
