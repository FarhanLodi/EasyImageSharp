using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// EXIF parsing and serialisation. The main corpus is <c>Fixtures/metadata/exif_alltypes*</c>: a hand-built
/// profile covering every TIFF field type across IFD0, the Exif, GPS and Interoperability sub-directories and
/// IFD1 (thumbnail), written both little- and big-endian, plus a Pillow-written profile for a second opinion.
/// </summary>
public class ExifTests
{
    private const string LittleEndianPayload = "metadata/exif_alltypes_le.bin";
    private const string BigEndianPayload = "metadata/exif_alltypes_be.bin";

    // ----- Header handling -----

    [Fact]
    public void ParsesTheByteOrderOfTheHeader()
    {
        Assert.Equal(ByteOrder.LittleEndian, Load(LittleEndianPayload).ByteOrder);
        Assert.Equal(ByteOrder.BigEndian, Load(BigEndianPayload).ByteOrder);
    }

    [Fact]
    public void ProfilesAreEmptyWhenTheDataIsNotTiffStructured()
    {
        Assert.Empty(new ExifProfile(Array.Empty<byte>()).Values);
        Assert.Empty(new ExifProfile(Encoding.ASCII.GetBytes("not a tiff header at all")).Values);
        Assert.Empty(new ExifProfile(new byte[] { (byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0 }).Values);
        Assert.Throws<ArgumentNullException>(() => new ExifProfile(null!));
    }

    [Fact]
    public void TheJpegExifIdentifierPrefixIsAccepted()
    {
        byte[] payload = FixturePath.Read(LittleEndianPayload);
        byte[] prefixed = Encoding.ASCII.GetBytes("Exif\0\0").Concat(payload).ToArray();
        prefixed[4] = 0;
        prefixed[5] = 0;

        var profile = new ExifProfile(prefixed);

        Assert.Equal("Test Camera", profile.GetValue(ExifTag.Model)!.Value);
    }

    // ----- Every value type -----

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    public void EveryTiffFieldTypeIsDecodedToItsClrShape(string payload)
    {
        ExifProfile profile = Load(payload);

        Assert.Equal("All EXIF types", profile.GetValue(ExifTag.ImageDescription)!.Value);
        Assert.Equal((ushort)1, profile.GetValue(ExifTag.Orientation)!.Value);
        Assert.Equal(new Rational(72, 1), profile.GetValue(ExifTag.XResolution)!.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, Value<byte[]>(profile, 0xC001, ExifIfd.Ifd0));
        Assert.Equal(new sbyte[] { -1, 2, -3 }, Value<sbyte[]>(profile, 0xC002, ExifIfd.Ifd0));
        Assert.Equal(new short[] { -1000, 1000 }, Value<short[]>(profile, 0xC003, ExifIfd.Ifd0));
        Assert.Equal(-123456, Value<int>(profile, 0xC004, ExifIfd.Ifd0));
        Assert.Equal(new[] { 1.5f, -2.25f }, Value<float[]>(profile, 0xC005, ExifIfd.Ifd0));
        Assert.Equal(Math.PI, Value<double>(profile, 0xC006, ExifIfd.Ifd0), 12);
        Assert.Equal(new[] { new SignedRational(-1, 3), new SignedRational(5, 2) }, Value<SignedRational[]>(profile, 0xC007, ExifIfd.Ifd0));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 }, Value<byte[]>(profile, 0xC008, ExifIfd.Ifd0));
        Assert.Equal(new uint[] { 1, 2, 3 }, Value<uint[]>(profile, 0xC009, ExifIfd.Ifd0));
        Assert.Equal("unknown ascii", Value<string>(profile, 0xC00A, ExifIfd.Ifd0));
        Assert.Equal((ushort)7, Value<ushort>(profile, 0xC00B, ExifIfd.Ifd0));
    }

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    public void FieldTypesAreRememberedForEveryValue(string payload)
    {
        ExifProfile profile = Load(payload);

        Assert.Equal(ExifDataType.Ascii, Entry(profile, 0x010E, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.Short, Entry(profile, 0x0112, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.Rational, Entry(profile, 0x011A, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.Byte, Entry(profile, 0xC001, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.SignedByte, Entry(profile, 0xC002, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.SignedShort, Entry(profile, 0xC003, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.SignedLong, Entry(profile, 0xC004, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.SingleFloat, Entry(profile, 0xC005, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.DoubleFloat, Entry(profile, 0xC006, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.SignedRational, Entry(profile, 0xC007, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.Undefined, Entry(profile, 0xC008, ExifIfd.Ifd0).DataType);
        Assert.Equal(ExifDataType.Long, Entry(profile, 0xC009, ExifIfd.Ifd0).DataType);

        Assert.True(Entry(profile, 0xC009, ExifIfd.Ifd0).IsArray);
        Assert.False(Entry(profile, 0xC004, ExifIfd.Ifd0).IsArray);
    }

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    public void SubDirectoriesArePlacedInTheirOwnIfd(string payload)
    {
        ExifProfile profile = Load(payload);

        Assert.Equal(new Rational(1, 125), profile.GetValue(ExifTag.ExposureTime)!.Value);
        Assert.Equal(new SignedRational(-1, 3), profile.GetValue(ExifTag.ExposureBiasValue)!.Value);
        Assert.Equal(new ushort[] { 200, 400 }, profile.GetValue(ExifTag.ISOSpeedRatings)!.Value);
        Assert.Equal(ExifIfd.Exif, Entry(profile, 0x829A, ExifIfd.Exif).Tag.Ifd);

        Assert.Equal("N", profile.GetValue(ExifTag.GPSLatitudeRef)!.Value);
        Assert.Equal(new[] { new Rational(51, 1), new Rational(30, 1), new Rational(0, 1) }, profile.GetValue(ExifTag.GPSLatitude)!.Value);
        Assert.Equal(new byte[] { 2, 3, 0, 0 }, profile.GetValue(ExifTag.GPSVersionID)!.Value);

        Assert.Equal("R98", Value<string>(profile, 0x0001, ExifIfd.Interop));
        Assert.Equal(Encoding.ASCII.GetBytes("0100"), Value<byte[]>(profile, 0x0002, ExifIfd.Interop));

        // The thumbnail directory shares IFD0's tag space but keeps its own identity.
        Assert.Equal((ushort)6, Value<ushort>(profile, 0x0103, ExifIfd.Ifd1));
        Assert.Equal(new Rational(72, 1), Value<Rational>(profile, 0x011A, ExifIfd.Ifd1));
    }

    [Fact]
    public void PointerTagsAreFollowedRatherThanStored()
    {
        ExifProfile profile = Load(LittleEndianPayload);

        Assert.False(profile.Contains(new ExifTag<uint>(0x8769, ExifIfd.Ifd0)));
        Assert.False(profile.Contains(new ExifTag<uint>(0x8825, ExifIfd.Ifd0)));
        Assert.False(profile.Contains(new ExifTag<uint>(0xA005, ExifIfd.Exif)));
    }

    [Fact]
    public void AsciiUserCommentsDropTheirCharacterCodePrefix()
    {
        Assert.Equal("Hello EXIF", Load(LittleEndianPayload).GetValue(ExifTag.UserComment)!.Value);
    }

    [Fact]
    public void UnicodeUserCommentsAreDecodedWithTheDirectoryByteOrder()
    {
        Assert.Equal("Héllo 日本", Load(BigEndianPayload).GetValue(ExifTag.UserComment)!.Value);
    }

    [Fact]
    public void UserCommentsTypedAsByteStillDropTheirCharacterCode()
    {
        // Pillow writes UserComment with field type BYTE although the specification says UNDEFINED.
        ExifProfile profile = Load("metadata/exif_pillow.bin");

        Assert.Equal("Pillow comment", profile.GetValue(ExifTag.UserComment)!.Value);
    }

    // ----- Thumbnail -----

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    public void ThumbnailBytesArePassedThroughUnchanged(string payload)
    {
        ExifProfile profile = Load(payload);

        Assert.NotNull(profile.Thumbnail);
        Assert.Equal(338, profile.Thumbnail!.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, profile.Thumbnail.Take(3));

        byte[] reparsed = new ExifProfile(profile.ToByteArray()).Thumbnail!;
        Assert.Equal(profile.Thumbnail, reparsed);

        // The pointer/length pair describing the thumbnail is regenerated, not exposed as ordinary values.
        Assert.False(profile.Contains(new ExifTag<uint>(0x0201, ExifIfd.Ifd1)));
        Assert.False(profile.Contains(new ExifTag<uint>(0x0202, ExifIfd.Ifd1)));
    }

    [Fact]
    public void ProfilesWithoutAThumbnailReportNone()
    {
        Assert.Null(Load("metadata/exif_pillow.bin").Thumbnail);
    }

    // ----- Serialisation round trips -----

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    [InlineData("metadata/exif_pillow.bin")]
    public void ToByteArrayReparsesToAnEqualProfile(string payload)
    {
        ExifProfile profile = Load(payload);

        var reparsed = new ExifProfile(profile.ToByteArray());

        Assert.Equal(profile.Values.Count, reparsed.Values.Count);
        foreach (IExifValue original in profile.Values)
        {
            Assert.True(reparsed.TryGetValue(original.Tag, out IExifValue? copy), $"{original.Tag.Ifd}/{original.Tag.Name} is missing.");
            AssertValueEqual(original.GetValue(), copy!.GetValue());
            Assert.Equal(original.Tag.Ifd, copy.Tag.Ifd);
        }
    }

    [Theory]
    [InlineData(LittleEndianPayload)]
    [InlineData(BigEndianPayload)]
    public void SerialisationReachesAFixedPoint(string payload)
    {
        byte[] first = Load(payload).ToByteArray();
        byte[] second = new ExifProfile(first).ToByteArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void SerialisationAlwaysWritesLittleEndian()
    {
        byte[] written = Load(BigEndianPayload).ToByteArray();

        Assert.Equal((byte)'I', written[0]);
        Assert.Equal((byte)'I', written[1]);
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(2)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(4)));
        Assert.Equal(ByteOrder.LittleEndian, new ExifProfile(written).ByteOrder);
    }

    [Fact]
    public void AnEmptyProfileSerialisesToAValidEmptyDirectory()
    {
        byte[] written = new ExifProfile().ToByteArray();

        var reparsed = new ExifProfile(written);
        Assert.Empty(reparsed.Values);
        Assert.Equal(14, written.Length); // Header (8) + entry count (2) + next-IFD pointer (4).
    }

    [Fact]
    public void EveryValueTypeSurvivesAWriteAndReadCycle()
    {
        var profile = new ExifProfile();
        profile.SetValue(new ExifTag<byte>(0xD001), (byte)200);
        profile.SetValue(new ExifTag<sbyte>(0xD002), (sbyte)-120);
        profile.SetValue(new ExifTag<ushort>(0xD003), (ushort)60000);
        profile.SetValue(new ExifTag<short>(0xD004), (short)-30000);
        profile.SetValue(new ExifTag<uint>(0xD005), 4000000000u);
        profile.SetValue(new ExifTag<int>(0xD006), -2000000000);
        profile.SetValue(new ExifTag<float>(0xD007), 1.25f);
        profile.SetValue(new ExifTag<double>(0xD008), -0.125);
        profile.SetValue(new ExifTag<Rational>(0xD009), new Rational(22, 7));
        profile.SetValue(new ExifTag<SignedRational>(0xD00A), new SignedRational(-22, 7));
        profile.SetValue(new ExifTag<string>(0xD00B), "text value");
        profile.SetValue(new ExifTag<byte[]>(0xD00C), new byte[] { 9, 8, 7, 6, 5 });
        profile.SetValue(new ExifTag<sbyte[]>(0xD00D), new sbyte[] { -1, 0, 1 });
        profile.SetValue(new ExifTag<ushort[]>(0xD00E), new ushort[] { 1, 65535 });
        profile.SetValue(new ExifTag<short[]>(0xD00F), new short[] { -32768, 32767 });
        profile.SetValue(new ExifTag<uint[]>(0xD010), new uint[] { 0, uint.MaxValue });
        profile.SetValue(new ExifTag<int[]>(0xD011), new[] { int.MinValue, int.MaxValue });
        profile.SetValue(new ExifTag<float[]>(0xD012), new[] { 0.5f, -0.5f });
        profile.SetValue(new ExifTag<double[]>(0xD013), new[] { 1e10, -1e-10 });
        profile.SetValue(new ExifTag<Rational[]>(0xD014), new[] { new Rational(1, 2), new Rational(3, 4) });
        profile.SetValue(new ExifTag<SignedRational[]>(0xD015), new[] { new SignedRational(-1, 2), new SignedRational(7, 3) });

        var reparsed = new ExifProfile(profile.ToByteArray());

        Assert.Equal(profile.Values.Count, reparsed.Values.Count);
        Assert.Equal((byte)200, Value<byte>(reparsed, 0xD001, ExifIfd.Ifd0));
        Assert.Equal((sbyte)-120, Value<sbyte>(reparsed, 0xD002, ExifIfd.Ifd0));
        Assert.Equal((ushort)60000, Value<ushort>(reparsed, 0xD003, ExifIfd.Ifd0));
        Assert.Equal((short)-30000, Value<short>(reparsed, 0xD004, ExifIfd.Ifd0));
        Assert.Equal(4000000000u, Value<uint>(reparsed, 0xD005, ExifIfd.Ifd0));
        Assert.Equal(-2000000000, Value<int>(reparsed, 0xD006, ExifIfd.Ifd0));
        Assert.Equal(1.25f, Value<float>(reparsed, 0xD007, ExifIfd.Ifd0));
        Assert.Equal(-0.125, Value<double>(reparsed, 0xD008, ExifIfd.Ifd0));
        Assert.Equal(new Rational(22, 7), Value<Rational>(reparsed, 0xD009, ExifIfd.Ifd0));
        Assert.Equal(new SignedRational(-22, 7), Value<SignedRational>(reparsed, 0xD00A, ExifIfd.Ifd0));
        Assert.Equal("text value", Value<string>(reparsed, 0xD00B, ExifIfd.Ifd0));
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, Value<byte[]>(reparsed, 0xD00C, ExifIfd.Ifd0));
        Assert.Equal(new sbyte[] { -1, 0, 1 }, Value<sbyte[]>(reparsed, 0xD00D, ExifIfd.Ifd0));
        Assert.Equal(new ushort[] { 1, 65535 }, Value<ushort[]>(reparsed, 0xD00E, ExifIfd.Ifd0));
        Assert.Equal(new short[] { -32768, 32767 }, Value<short[]>(reparsed, 0xD00F, ExifIfd.Ifd0));
        Assert.Equal(new uint[] { 0, uint.MaxValue }, Value<uint[]>(reparsed, 0xD010, ExifIfd.Ifd0));
        Assert.Equal(new[] { int.MinValue, int.MaxValue }, Value<int[]>(reparsed, 0xD011, ExifIfd.Ifd0));
        Assert.Equal(new[] { 0.5f, -0.5f }, Value<float[]>(reparsed, 0xD012, ExifIfd.Ifd0));
        Assert.Equal(new[] { 1e10, -1e-10 }, Value<double[]>(reparsed, 0xD013, ExifIfd.Ifd0));
        Assert.Equal(new[] { new Rational(1, 2), new Rational(3, 4) }, Value<Rational[]>(reparsed, 0xD014, ExifIfd.Ifd0));
        Assert.Equal(new[] { new SignedRational(-1, 2), new SignedRational(7, 3) }, Value<SignedRational[]>(reparsed, 0xD015, ExifIfd.Ifd0));
    }

    [Fact]
    public void SubDirectoriesAreRebuiltWhenSerialising()
    {
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Make, "Maker");
        profile.SetValue(ExifTag.FNumber, new Rational(28, 10));
        profile.SetValue(ExifTag.GPSLatitudeRef, "S");
        profile.SetValue(new ExifTag<string>(0x0001, ExifIfd.Interop), "R98");

        var reparsed = new ExifProfile(profile.ToByteArray());

        Assert.Equal("Maker", reparsed.GetValue(ExifTag.Make)!.Value);
        Assert.Equal(new Rational(28, 10), reparsed.GetValue(ExifTag.FNumber)!.Value);
        Assert.Equal("S", reparsed.GetValue(ExifTag.GPSLatitudeRef)!.Value);
        Assert.Equal("R98", Value<string>(reparsed, 0x0001, ExifIfd.Interop));
    }

    // ----- The value API -----

    [Fact]
    public void SetValueAddsReplacesAndKeepsTheTagType()
    {
        var profile = new ExifProfile();

        profile.SetValue(ExifTag.Make, "First");
        Assert.Equal("First", profile.GetValue(ExifTag.Make)!.Value);

        profile.SetValue(ExifTag.Make, "Second");
        Assert.Single(profile.Values);
        Assert.Equal("Second", profile.GetValue(ExifTag.Make)!.Value);
        Assert.Equal(ExifDataType.Ascii, profile.GetValue(ExifTag.Make)!.DataType);
    }

    [Fact]
    public void WritingThroughAReturnedValueUpdatesTheProfile()
    {
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)3);

        Assert.True(profile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? value));
        value!.Value = 8;

        Assert.Equal((ushort)8, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void GetValueReturnsNullForAbsentAndMistypedTags()
    {
        ExifProfile profile = Load(LittleEndianPayload);

        Assert.Null(profile.GetValue(new ExifTag<string>(0xDEAD)));
        Assert.False(profile.TryGetValue(new ExifTag<string>(0xDEAD), out IExifValue<string>? _));
        Assert.Null(profile.GetValue(new ExifTag<string>(0x0112, ExifIfd.Ifd0))); // Orientation is a ushort.
        Assert.True(profile.TryGetValue(new ExifTag<string>(0x0112, ExifIfd.Ifd0), out IExifValue? untyped));
        Assert.Equal((ushort)1, untyped!.GetValue());
    }

    [Fact]
    public void RemoveValueAndClearDropEntries()
    {
        ExifProfile profile = Load(LittleEndianPayload);
        int before = profile.Values.Count;

        Assert.True(profile.RemoveValue(ExifTag.Make));
        Assert.False(profile.RemoveValue(ExifTag.Make));
        Assert.Equal(before - 1, profile.Values.Count);
        Assert.False(profile.Contains(ExifTag.Make));

        profile.Clear();
        Assert.Empty(profile.Values);
        Assert.Null(profile.Thumbnail);
    }

    [Fact]
    public void TheValueApiRejectsNullTags()
    {
        var profile = new ExifProfile();

        Assert.Throws<ArgumentNullException>(() => profile.GetValue<string>(null!));
        Assert.Throws<ArgumentNullException>(() => profile.SetValue<string>(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => profile.RemoveValue(null!));
        Assert.Throws<ArgumentNullException>(() => profile.Contains(null!));
        Assert.Throws<ArgumentNullException>(() => profile.TryGetValue(null!, out IExifValue? _));
    }

    [Fact]
    public void SetValueRejectsUnsupportedClrTypes()
    {
        var profile = new ExifProfile();

        Assert.Throws<NotSupportedException>(() => profile.SetValue(new ExifTag<DateTime>(0xD000), DateTime.UnixEpoch));
    }

    [Fact]
    public void UntypedValuesConvertBetweenCompatibleShapes()
    {
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)1);
        IExifValue value = profile.GetValue(ExifTag.Orientation)!;

        Assert.True(value.TrySetValue(6));            // int -> ushort
        Assert.Equal((ushort)6, value.GetValue());
        Assert.False(value.TrySetValue(70000));       // Out of range for a ushort.
        Assert.False(value.TrySetValue("six"));       // Strings only convert to strings.
        Assert.Equal((ushort)6, value.GetValue());
    }

    [Fact]
    public void TagsCompareByNumberAndDirectory()
    {
        var a = new ExifTag<ushort>(0x0112, ExifIfd.Ifd0);
        var b = new ExifTag<ushort>(0x0112, ExifIfd.Ifd0);
        var c = new ExifTag<ushort>(0x0112, ExifIfd.Ifd1);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("Orientation", a.Name);
        Assert.Equal("0xDEAD", new ExifTag<ushort>(0xDEAD).Name);
        Assert.Equal(0x0112, (ushort)a);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void BareTagNumbersLandInTheirConventionalDirectory()
    {
        Assert.Equal(ExifIfd.Ifd0, new ExifTag<ushort>(0x0112).Ifd);
        Assert.Equal(ExifIfd.Exif, new ExifTag<Rational>(0x829A).Ifd);
        Assert.Equal(ExifIfd.Gps, new ExifTag<string>(0x0001).Ifd);
        Assert.Equal(ExifIfd.Ifd0, new ExifTag<ushort>(0xDEAD).Ifd);
    }

    [Fact]
    public void DeepCloneProducesAnIndependentProfile()
    {
        ExifProfile source = Load(LittleEndianPayload);

        ExifProfile clone = source.DeepClone();
        clone.SetValue(ExifTag.Make, "Changed");
        clone.RemoveValue(ExifTag.Model);
        Value<byte[]>(clone, 0xC001, ExifIfd.Ifd0)![0] = 99;
        clone.Thumbnail![0] = 0;

        Assert.Equal("EasyImageSharp", source.GetValue(ExifTag.Make)!.Value);
        Assert.Equal("Test Camera", source.GetValue(ExifTag.Model)!.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, Value<byte[]>(source, 0xC001, ExifIfd.Ifd0));
        Assert.Equal(0xFF, source.Thumbnail![0]);
        Assert.Equal(source.ByteOrder, clone.ByteOrder);
    }

    // ----- Rationals -----

    [Fact]
    public void RationalsApproximateDoublesAndConvertBack()
    {
        Assert.Equal(new Rational(72, 1), new Rational(72d));
        Assert.Equal(new Rational(5, 2), new Rational(2.5));
        Assert.Equal(0.001, new Rational(0.001).ToDouble(), 12);
        Assert.Equal(new Rational(0, 1), new Rational(0d));
        Assert.Equal(3u, new Rational(3).Numerator);
        Assert.Equal(1u, new Rational(3).Denominator);

        Assert.Equal(new SignedRational(-5, 2), new SignedRational(-2.5));
        Assert.Equal(-0.25, new SignedRational(-0.25).ToDouble(), 12);
        Assert.Equal(new SignedRational(-4, 1), new SignedRational(-4));
    }

    [Fact]
    public void RationalsWithAZeroDenominatorAreNotANumberOrInfinite()
    {
        Assert.True(double.IsNaN(new Rational(0, 0).ToDouble()));
        Assert.True(double.IsPositiveInfinity(new Rational(1, 0).ToDouble()));
        Assert.True(double.IsNaN(new SignedRational(0, 0).ToDouble()));
        Assert.True(double.IsNegativeInfinity(new SignedRational(-1, 0).ToDouble()));
    }

    [Fact]
    public void RationalsRejectValuesTheyCannotRepresent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(-1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignedRational(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignedRational(double.NegativeInfinity));
    }

    [Fact]
    public void RationalsCompareAndPrint()
    {
        Assert.True(new Rational(1, 2) == new Rational(1, 2));
        Assert.True(new Rational(1, 2) != new Rational(1, 3));
        Assert.Equal(new Rational(1, 2).GetHashCode(), new Rational(1, 2).GetHashCode());
        Assert.Equal("1/2", new Rational(1, 2).ToString());
        Assert.Equal("-1/2", new SignedRational(-1, 2).ToString());
        Assert.True(new SignedRational(1, 2) == new SignedRational(1, 2));
        Assert.True(new SignedRational(1, 2) != new SignedRational(1, 3));
    }

    // ----- Codec round trips -----

    [Fact]
    public void JpegKeepsTheExifProfileAcrossAnEncodeAndDecode()
    {
        using Image<Rgba32> source = MetadataTests.LoadFixture("metadata/exif_alltypes.jpg");

        using Image<Rgba32> decoded = MetadataTests.ReEncode(source, new JpegEncoder());

        AssertProfilesMatch(source.Metadata.ExifProfile!, decoded.Metadata.ExifProfile!);
    }

    [Fact]
    public void PngKeepsTheExifProfileAcrossAnEncodeAndDecode()
    {
        using Image<Rgba32> source = MetadataTests.LoadFixture("metadata/exif_alltypes_be.png");

        using Image<Rgba32> decoded = MetadataTests.ReEncode(source, new PngEncoder());

        AssertProfilesMatch(source.Metadata.ExifProfile!, decoded.Metadata.ExifProfile!);
    }

    [Fact]
    public void TiffKeepsTheExifProfileAcrossAnEncodeAndDecode()
    {
        using Image<Rgba32> source = MetadataTests.LoadFixture("metadata/exif_pillow.tif");

        using Image<Rgba32> decoded = MetadataTests.ReEncode(source, new TiffEncoder());

        Assert.Equal("EasyImageSharp", decoded.Metadata.ExifProfile!.GetValue(ExifTag.Make)!.Value);
        Assert.Equal("Pillow Writer", decoded.Metadata.ExifProfile.GetValue(ExifTag.Model)!.Value);
        Assert.Equal("TIFF description", decoded.Metadata.ExifProfile.GetValue(ExifTag.ImageDescription)!.Value);
    }

    [Fact]
    public void EncodersSynchroniseTheResolutionTagsWithTheMetadata()
    {
        using var image = new Image<Rgba32>(8, 8);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.XResolution, new Rational(1, 1));
        profile.SetValue(ExifTag.Make, "Maker");
        image.Metadata.ExifProfile = profile;
        image.Metadata.SetResolution(240, 240, PixelResolutionUnit.PixelsPerInch);

        using Image<Rgba32> decoded = MetadataTests.ReEncode(image, new PngEncoder());

        ExifProfile written = decoded.Metadata.ExifProfile!;
        Assert.Equal(240, written.GetValue(ExifTag.XResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal(240, written.GetValue(ExifTag.YResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal((ushort)2, written.GetValue(ExifTag.ResolutionUnit)!.Value);

        // The image's own profile is left alone; only the serialized copy carries the synchronised tags.
        Assert.Equal(new Rational(1, 1), profile.GetValue(ExifTag.XResolution)!.Value);
    }

    [Fact]
    public void ExifResolutionOverridesTheContainerDensity()
    {
        // exif_pillow.png has a 150x100 DPI pHYs chunk; the EXIF profile carries no resolution tags, so the
        // container density survives. Adding EXIF resolution tags must win instead.
        using var image = new Image<Rgba32>(8, 8);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.XResolution, new Rational(400, 1));
        profile.SetValue(ExifTag.YResolution, new Rational(400, 1));
        profile.SetValue(ExifTag.ResolutionUnit, (ushort)2);
        image.Metadata.ExifProfile = profile;
        image.Metadata.ApplyExifResolution(profile);

        Assert.Equal(400, image.Metadata.HorizontalResolution);
        Assert.Equal(PixelResolutionUnit.PixelsPerInch, image.Metadata.ResolutionUnits);
    }

    // ----- Hostile and corrupt input -----

    [Fact]
    public void GarbageAfterTheExifIdentifierLeavesTheImageDecodableWithoutExif()
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture("metadata/corrupt_exif_garbage.jpg");

        Assert.Equal(16, image.Width);
        Assert.Null(image.Metadata.ExifProfile);
    }

    [Fact]
    public void ATruncatedDirectoryYieldsTheEntriesThatFit()
    {
        using Image<Rgba32> image = MetadataTests.LoadFixture("metadata/corrupt_exif_truncated.jpg");

        Assert.Equal(16, image.Width);
        Assert.NotNull(image.Metadata.ExifProfile);
        Assert.Equal((ushort)6, image.Metadata.ExifProfile!.GetValue(ExifTag.Orientation)!.Value);
        Assert.False(image.Metadata.ExifProfile.Contains(ExifTag.Make));
    }

    [Fact]
    public void EntryCountsLargerThanThePayloadAreClamped()
    {
        // A header claiming 60000 entries in a 20-byte payload must not allocate or throw.
        byte[] data = TiffHeader().Concat(BitConverter.GetBytes((ushort)60000)).ToArray();

        var profile = new ExifProfile(data);

        Assert.Empty(profile.Values);
    }

    [Fact]
    public void ValueOffsetsPointingOutsideThePayloadAreSkipped()
    {
        byte[] data = BuildDirectory(
            Entry(0x010F, ExifDataType.Ascii, 1000, 0x7FFFFF00),   // Offset far past the end.
            Entry(0x0112, ExifDataType.Short, 1, 0x00000005));     // Inline, still readable.

        var profile = new ExifProfile(data);

        Assert.False(profile.Contains(ExifTag.Make));
        Assert.Equal((ushort)5, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void AbsurdElementCountsAreRejectedRatherThanAllocated()
    {
        // count * size overflows the payload many times over: the entry must be skipped, not materialized.
        byte[] data = BuildDirectory(
            Entry(0x010F, ExifDataType.Ascii, 0x7FFFFFFF, 26),
            Entry(0x0112, ExifDataType.Short, 1, 0x00000006));

        var profile = new ExifProfile(data);

        Assert.False(profile.Contains(ExifTag.Make));
        Assert.Equal((ushort)6, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void UnknownFieldTypesAreSkipped()
    {
        byte[] data = BuildDirectory(
            Entry(0x010F, (ExifDataType)999, 4, 0),
            Entry(0x0112, ExifDataType.Short, 1, 7));

        var profile = new ExifProfile(data);

        Assert.Single(profile.Values);
        Assert.Equal((ushort)7, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void DuplicateTagsInADirectoryKeepTheFirstOccurrence()
    {
        byte[] data = BuildDirectory(
            Entry(0x0112, ExifDataType.Short, 1, 3),
            Entry(0x0112, ExifDataType.Short, 1, 8));

        var profile = new ExifProfile(data);

        Assert.Equal((ushort)3, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void SelfReferentialDirectoriesTerminate()
    {
        // IFD0's Exif pointer points back at IFD0, and the next-IFD pointer does the same.
        var entries = new List<byte[]>
        {
            Entry(0x0112, ExifDataType.Short, 1, 4),
            Entry(0x8769, ExifDataType.Long, 1, 8),
        };
        byte[] data = BuildDirectory(nextIfd: 8, entries.ToArray());

        var profile = new ExifProfile(data);

        Assert.Equal((ushort)4, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public void DeeplyChainedSubDirectoriesAreBounded()
    {
        // Every directory points at the next one through an Exif pointer; the reader must stop, not recurse.
        const int Count = 64;
        var data = new byte[8 + (Count * 26)];
        TiffHeader().CopyTo(data, 0);
        for (int i = 0; i < Count; i++)
        {
            int offset = 8 + (i * 26);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), 1);
            Entry(0x8769, ExifDataType.Long, 1, (uint)(offset + 26)).CopyTo(data, offset + 2);
        }

        var profile = new ExifProfile(data);

        // Only the first pointer is followed (the reader caps its depth); the rest stay unread, so the parse
        // terminates with a handful of values instead of walking all 64 directories.
        Assert.InRange(profile.Values.Count, 0, 4);
    }

    [Fact]
    public void PayloadsLargerThanTheCapAreRejected()
    {
        // TryParse is the decoder entry point; it guards the payload size before anything is parsed.
        Assert.Throws<InvalidImageContentException>(
            () => ExifProfile.TryParse(new byte[ExifReader.MaxExifBytes + 1]));
    }

    [Fact]
    public void TryParseReturnsNullForDataThatIsNotTiffStructured()
    {
        Assert.Null(ExifProfile.TryParse(Encoding.ASCII.GetBytes("no header here")));
        Assert.NotNull(ExifProfile.TryParse(FixturePath.Read(LittleEndianPayload)));
    }

    [Fact]
    public void OversizedIccAndXmpEntriesInsideExifAreIgnoredRatherThanAllocated()
    {
        // IFD0 entries claiming a 32 MB ICC profile and a 128 MB XMP packet inside a 30-byte payload. The
        // declared data lies outside the payload, so the entries are dropped before a single byte is allocated.
        byte[] icc = BuildDirectory(Entry(0x8773, ExifDataType.Undefined, 32u * 1024 * 1024, 26));
        byte[] xmp = BuildDirectory(Entry(0x02BC, ExifDataType.Byte, 128u * 1024 * 1024, 26));

        Assert.Empty(new ExifProfile(icc).Values);
        Assert.Empty(new ExifProfile(xmp).Values);
    }

    [Fact]
    public void SingleElementArraysOfUnknownTagsDecodeToScalars()
    {
        // Nothing says how many elements an unknown tag holds, so a count of one yields the scalar shape.
        // Known array-typed tags are reshaped to an array regardless of the count the file used.
        byte[] data = BuildDirectory(
            Entry(0xE001, ExifDataType.Long, 1, 42),
            Entry(0x0102, ExifDataType.Short, 1, 8));

        var profile = new ExifProfile(data);

        Assert.Equal(42u, Value<uint>(profile, 0xE001, ExifIfd.Ifd0));
        Assert.Null(profile.GetValue(new ExifTag<uint[]>(0xE001, ExifIfd.Ifd0)));
        Assert.Equal(new ushort[] { 8 }, profile.GetValue(ExifTag.BitsPerSample)!.Value);
    }

    [Fact]
    public void ManyEntriesPointingAtTheSameBlockDoNotAllocateWithoutBound()
    {
        // 2000 entries all describing the same 400-byte block would materialize ~800 KB from a 24 KB file.
        // The reader's budget stops that; whatever it returns must be a small, well-formed profile.
        const int Entries = 2000;
        const int BlockSize = 400;
        int directorySize = 2 + (Entries * 12) + 4;
        var data = new byte[8 + directorySize + BlockSize];
        TiffHeader().CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), Entries);
        uint blockOffset = (uint)(8 + directorySize);
        for (int i = 0; i < Entries; i++)
        {
            Entry((ushort)(0xA000 + i), ExifDataType.Undefined, BlockSize, blockOffset).CopyTo(data, 10 + (i * 12));
        }

        var profile = new ExifProfile(data);

        long materialized = profile.Values.Sum(v => v.GetValue() is byte[] b ? b.Length : 0);
        Assert.True(materialized <= data.Length + 65536, $"{materialized:N0} bytes materialized from a {data.Length:N0} byte payload.");
    }

    [Fact]
    public void ThumbnailPointersOutsideThePayloadAreIgnored()
    {
        byte[] ifd0 = BuildDirectory(nextIfd: 34, Entry(0x0112, ExifDataType.Short, 1, 1));
        var data = new byte[34 + 2 + 24 + 4];
        ifd0.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 2);
        Entry(0x0201, ExifDataType.Long, 1, 0x7FFFFFF0).CopyTo(data, 36);
        Entry(0x0202, ExifDataType.Long, 1, 1024).CopyTo(data, 48);

        var profile = new ExifProfile(data);

        Assert.Null(profile.Thumbnail);
        Assert.Equal((ushort)1, profile.GetValue(ExifTag.Orientation)!.Value);
    }

    [Theory]
    [InlineData("metadata/exif_alltypes.jpg")]
    [InlineData("metadata/exif_alltypes_be.png")]
    public void TruncatingAFileNeverProducesAFrameworkException(string fixture)
    {
        byte[] data = FixturePath.Read(fixture);

        for (int length = 4; length < data.Length; length += 37)
        {
            byte[] truncated = data[..length];
            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(truncated);
            }
            catch (InvalidImageContentException)
            {
                // The documented outcome for damaged input.
            }
            catch (UnknownImageFormatException)
            {
                // Too little data to even recognise the format.
            }
        }
    }

    // ----- Helpers -----

    private static ExifProfile Load(string payload) => new(FixturePath.Read(payload));

    private static T? Value<T>(ExifProfile profile, ushort id, ExifIfd ifd)
        => profile.GetValue(new ExifTag<T>(id, ifd))!.Value;

    private static IExifValue Entry(ExifProfile profile, ushort id, ExifIfd ifd)
    {
        Assert.True(profile.TryGetValue(new ExifTag<byte>(id, ifd), out IExifValue? value), $"{ifd}/0x{id:X4} is missing.");
        return value!;
    }

    private static void AssertProfilesMatch(ExifProfile expected, ExifProfile actual)
    {
        foreach (IExifValue value in expected.Values)
        {
            // The encoders rewrite the resolution tags from the image metadata, so those are compared elsewhere.
            if (value.Tag.Id is 0x011A or 0x011B or 0x0128)
            {
                continue;
            }

            Assert.True(actual.TryGetValue(value.Tag, out IExifValue? copy), $"{value.Tag.Ifd}/{value.Tag.Name} is missing.");
            AssertValueEqual(value.GetValue(), copy!.GetValue());
        }
    }

    private static void AssertValueEqual(object? expected, object? actual)
    {
        if (expected is Array left && actual is Array right)
        {
            Assert.Equal(left.Length, right.Length);
            for (int i = 0; i < left.Length; i++)
            {
                Assert.Equal(left.GetValue(i), right.GetValue(i));
            }

            return;
        }

        Assert.Equal(expected, actual);
    }

    private static byte[] TiffHeader()
    {
        var header = new byte[8];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 8);
        return header;
    }

    private static byte[] Entry(ushort tag, ExifDataType type, uint count, uint value)
    {
        var entry = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), value);
        return entry;
    }

    private static byte[] BuildDirectory(params byte[][] entries) => BuildDirectory(0, entries);

    private static byte[] BuildDirectory(uint nextIfd, params byte[][] entries)
    {
        var data = new byte[8 + 2 + (entries.Length * 12) + 4];
        TiffHeader().CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].CopyTo(data, 10 + (i * 12));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10 + (entries.Length * 12)), nextIfd);
        return data;
    }
}
