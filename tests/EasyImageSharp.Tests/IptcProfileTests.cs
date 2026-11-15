using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Iptc;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The <see cref="IptcProfile"/> model on its own: parsing and serializing IPTC-IIM 4.2 streams, typed access,
/// repeatable datasets, length validation, the coded character set, extended datasets and lossless round trips.
/// </summary>
/// <remarks>
/// Every expected byte sequence here is assembled by <see cref="DataSet"/>, a three-line transcription of IIM 4.2
/// section 1.5 (tag marker 0x1C, record, dataset, then a two-byte length or the extended 0x8004 form), so no
/// expectation is derived from the code under test.
/// </remarks>
public class IptcProfileTests
{
    private static readonly byte[] Version4 = { 0x00, 0x04 };
    private static readonly byte[] EscUtf8 = { 0x1B, 0x25, 0x47 };

    [Fact]
    public void SetValue_WritesTheSpecLayoutWithTheRecordVersionFirst()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcTag.Headline, "Storm over the bay");

        byte[] expected = Concat(DataSet(2, 0, Version4), DataSet(2, 105, Ascii("Storm over the bay")));
        Assert.Equal(expected, profile.ToByteArray());
        Assert.False(profile.IsUtf8);
    }

    [Fact]
    public void Datasets_AreKeptInRecordAndDatasetOrder_AndRepeatsFollowTheirSiblings()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcTag.Caption, "caption");
        profile.AddValue(IptcTag.Keywords, "b");
        profile.SetValue(IptcTag.ObjectName, "name");
        profile.AddValue(IptcTag.Keywords, "a");
        profile.AddValue(IptcTag.Byline, "someone");

        IptcTag[] order = profile.Values.Select(v => v.Tag).ToArray();
        Assert.Equal(
            new[] { IptcTag.ApplicationRecordVersion, IptcTag.ObjectName, IptcTag.Keywords, IptcTag.Keywords, IptcTag.Byline, IptcTag.Caption },
            order);
        Assert.Equal(new[] { "b", "a" }, profile.GetValues(IptcTag.Keywords));
    }

    [Fact]
    public void SetValue_ReplacesEveryOccurrence_KeepingThePositionOfTheFirst()
    {
        var profile = new IptcProfile();
        profile.AddValue(IptcTag.Keywords, "one");
        profile.AddValue(IptcTag.Keywords, "two");
        profile.SetValue(IptcTag.Caption, "c");
        profile.SetValue(IptcTag.Keywords, "only");

        Assert.Equal(new[] { "only" }, profile.GetValues(IptcTag.Keywords));
        Assert.Equal("only", profile.GetValue(IptcTag.Keywords));
        Assert.Equal(3, profile.Values.Count);
    }

    [Fact]
    public void RemoveValue_RemovesAllOrOnlyMatchingOccurrences()
    {
        var profile = new IptcProfile();
        profile.AddValue(IptcTag.Keywords, "keep");
        profile.AddValue(IptcTag.Keywords, "drop");
        profile.AddValue(IptcTag.Keywords, "drop");

        Assert.True(profile.RemoveValue(IptcTag.Keywords, "drop"));
        Assert.Equal(new[] { "keep" }, profile.GetValues(IptcTag.Keywords));
        Assert.False(profile.RemoveValue(IptcTag.Keywords, "absent"));
        Assert.True(profile.RemoveValue(IptcTag.Keywords));
        Assert.False(profile.Contains(IptcTag.Keywords));
        Assert.False(profile.RemoveValue(IptcTag.Keywords));
        Assert.Null(profile.GetValue(IptcTag.Keywords));

        profile.Clear();
        Assert.Empty(profile.Values);
        Assert.Empty(profile.ToByteArray());
    }

    [Fact]
    public void Strict_RejectsValuesOutsideTheSpecifiedLengthsAndFormats()
    {
        var profile = new IptcProfile();
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.Keywords, new string('k', 65)));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.CountryCode, "PT"));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.SubjectReference, "too short"));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.DateCreated, "2026-1-1"));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.Urgency, "x"));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.ApplicationRecordVersion, "4"));
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.ObjectPreviewData, "text", strict: false));

        // A rejected value leaves the profile untouched, including its character set.
        Assert.Throws<ArgumentException>(() => profile.SetValue(IptcTag.Keywords, new string('é', 40)));
        Assert.Empty(profile.Values);
        Assert.False(profile.IsUtf8);

        // Exactly at the limit is fine; the limit counts octets, so 32 two-byte characters fill a 64-byte keyword.
        profile.SetValue(IptcTag.Keywords, new string('é', 32));
        Assert.Equal(64, profile.Values.Single(v => v.Tag == IptcTag.Keywords).Length);
    }

    [Fact]
    public void AddValue_RefusesASecondValueForANonRepeatableDataset_UnlessLenient()
    {
        var profile = new IptcProfile();
        profile.AddValue(IptcTag.Headline, "first");
        Assert.Throws<InvalidOperationException>(() => profile.AddValue(IptcTag.Headline, "second"));
        profile.AddValue(IptcTag.Headline, "second", strict: false);
        Assert.Equal(new[] { "first", "second" }, profile.GetValues(IptcTag.Headline));
    }

    [Fact]
    public void Lenient_TruncatesAtACharacterBoundary()
    {
        var profile = new IptcProfile();
        string value = "abc" + new string('ü', 40); // 3 + 80 octets in UTF-8.
        profile.SetValue(IptcTag.Keywords, value, strict: false);

        IptcValue stored = profile.Values.Single(v => v.Tag == IptcTag.Keywords);
        Assert.Equal(63, stored.Length); // 64 would split the 31st 'ü'.
        Assert.Equal("abc" + new string('ü', 30), stored.Value);
        Assert.Equal("abc" + new string('ü', 30), Encoding.UTF8.GetString(stored.ToByteArray()));
    }

    [Fact]
    public void NonAsciiText_SwitchesToUtf8_AndReencodesExistingLatin1Text()
    {
        // A profile without 1:90, holding ISO 8859-1 text that is not valid UTF-8.
        byte[] latin1 = Concat(DataSet(2, 0, Version4), DataSet(2, 90, Encoding.Latin1.GetBytes("Zürich")));
        var profile = new IptcProfile(latin1);
        Assert.False(profile.IsUtf8);
        Assert.Equal("Zürich", profile.GetValue(IptcTag.City));

        profile.SetValue(IptcTag.Caption, "東京");

        Assert.True(profile.IsUtf8);
        Assert.Equal("Zürich", profile.GetValue(IptcTag.City));
        byte[] expected = Concat(
            DataSet(1, 0, Version4),
            DataSet(1, 90, EscUtf8),
            DataSet(2, 0, Version4),
            DataSet(2, 90, Encoding.UTF8.GetBytes("Zürich")),
            DataSet(2, 120, Encoding.UTF8.GetBytes("東京")));
        Assert.Equal(expected, profile.ToByteArray());
    }

    [Fact]
    public void AsciiText_DoesNotDeclareACharacterSet()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcTag.Credit, "Agency");
        Assert.False(profile.IsUtf8);
        Assert.False(profile.Contains(IptcTag.CodedCharacterSet));
        Assert.False(profile.Contains(IptcTag.EnvelopeRecordVersion));
    }

    [Theory]
    [InlineData(new byte[] { 0x1B, 0x25, 0x47 }, true)]
    [InlineData(new byte[] { 0x1B, 0x25, 0x2F, 0x49 }, true)]
    [InlineData(new byte[] { 0x1B, 0x2E, 0x41 }, false)] // ISO 8859-1 designation.
    public void CodedCharacterSet_DecidesTheTextEncoding(byte[] declaration, bool utf8)
    {
        byte[] text = { 0xC3, 0xA9 }; // "é" in UTF-8; "Ã©" in ISO 8859-1.
        var profile = new IptcProfile(Concat(DataSet(1, 90, declaration), DataSet(2, 0, Version4), DataSet(2, 5, text)));
        Assert.Equal(utf8, profile.IsUtf8);
        Assert.Equal(utf8 ? "é" : "Ã©", profile.GetValue(IptcTag.ObjectName));
    }

    [Fact]
    public void UndeclaredText_IsReadAsUtf8WhenValidAndLatin1Otherwise()
    {
        var profile = new IptcProfile(Concat(
            DataSet(2, 0, Version4),
            DataSet(2, 5, new byte[] { 0xC3, 0xA9 }),
            DataSet(2, 90, new byte[] { 0x53, 0xE3, 0x6F })));
        Assert.Equal("é", profile.GetValue(IptcTag.ObjectName));
        Assert.Equal("São", profile.GetValue(IptcTag.City));
    }

    [Fact]
    public void ExtendedDatasets_UseTheLongLengthForm()
    {
        var preview = new byte[40000];
        for (int i = 0; i < preview.Length; i++)
        {
            preview[i] = (byte)(i * 13);
        }

        var profile = new IptcProfile();
        profile.SetValue(IptcTag.ObjectPreviewData, preview);
        byte[] bytes = profile.ToByteArray();

        byte[] expected = Concat(DataSet(2, 0, Version4), DataSet(2, 202, preview));
        Assert.Equal(expected, bytes);
        Assert.Equal(0x80, bytes[7 + 3]); // Extended flag in the high length byte...
        Assert.Equal(0x04, bytes[7 + 4]); // ...followed by a four-byte length.

        var reread = new IptcProfile(bytes);
        Assert.Equal(preview, reread.Values.Single(v => v.Tag == IptcTag.ObjectPreviewData).ToByteArray());
        Assert.Equal(string.Empty, reread.Values.Single(v => v.Tag == IptcTag.ObjectPreviewData).Value);
    }

    [Fact]
    public void ExtendedLengthsOfOtherWidthsAreRead()
    {
        // A two-byte extended length (0x8002) is legal even though writers normally use four.
        byte[] data = Concat(new byte[] { 0x1C, 0x02, 0x05, 0x80, 0x02, 0x00, 0x03 }, Ascii("abc"));
        var profile = new IptcProfile(data);
        Assert.Equal("abc", profile.GetValue(IptcTag.ObjectName));
        Assert.Equal(data, profile.ToByteArray()); // Unmodified: written back exactly as read.
    }

    [Fact]
    public void UnknownRecordsAndDatasets_RoundTripUnchanged()
    {
        byte[] data = Concat(
            DataSet(1, 0, Version4),
            DataSet(2, 0, Version4),
            DataSet(2, 231, Ascii("unnamed dataset")),
            DataSet(3, 10, new byte[] { 1, 2, 3 }),
            DataSet(9, 10, new byte[] { 0, 0, 0x10, 0 }));
        var profile = new IptcProfile(data);
        Assert.Equal(5, profile.Values.Count);
        Assert.Equal(3, profile.Values[3].Record);
        Assert.Equal(10, profile.Values[3].DataSet);
        Assert.Equal((IptcTag)((3 << 8) | 10), profile.Values[3].Tag);

        // Editing a named dataset rewrites the stream; the unknown ones are carried through in order.
        profile.SetValue(IptcTag.Headline, "added");
        byte[] expected = Concat(
            DataSet(1, 0, Version4),
            DataSet(2, 0, Version4),
            DataSet(2, 105, Ascii("added")),
            DataSet(2, 231, Ascii("unnamed dataset")),
            DataSet(3, 10, new byte[] { 1, 2, 3 }),
            DataSet(9, 10, new byte[] { 0, 0, 0x10, 0 }));
        Assert.Equal(expected, profile.ToByteArray());
    }

    [Fact]
    public void UnmodifiedProfile_IsWrittenBackByteForByte_WithoutTrailingPadding()
    {
        byte[] data = Concat(DataSet(2, 0, Version4), DataSet(2, 5, Ascii("odd")), new byte[] { 0, 0, 0 });
        var profile = new IptcProfile(data);
        Assert.Equal(data.AsSpan(0, data.Length - 3).ToArray(), profile.ToByteArray());
        Assert.True(profile.IsUnmodifiedSource);

        profile.SetValue(IptcTag.ObjectName, "odd");
        Assert.False(profile.IsUnmodifiedSource);
        Assert.Equal(data.AsSpan(0, data.Length - 3).ToArray(), profile.ToByteArray());
    }

    [Fact]
    public void TrailingNulInsideTheLastDataset_IsNotMistakenForPadding()
    {
        byte[] data = Concat(DataSet(2, 0, Version4), DataSet(2, 5, new byte[] { 0x61, 0x62, 0x00 }));
        var profile = new IptcProfile(data);
        Assert.Equal(data, profile.ToByteArray());
        Assert.Equal("ab", profile.GetValue(IptcTag.ObjectName));
        Assert.Equal(3, profile.Values[1].Length);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x42, 0x49, 0x4D })]
    [InlineData(new byte[] { 0x1C, 0x02 })]
    [InlineData(new byte[] { 0x1C, 0x02, 0x05, 0x00, 0x10, 0x61 })]
    [InlineData(new byte[] { 0x1C, 0x02, 0x05, 0x80, 0x00 })]
    [InlineData(new byte[] { 0x1C, 0x02, 0x05, 0x80, 0x09, 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData(new byte[] { 0x1C, 0x02, 0x05, 0x80, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void MalformedStreams_YieldAnEmptyProfileWithoutThrowing(byte[] data)
    {
        var profile = new IptcProfile(data);
        Assert.Empty(profile.Values);
        Assert.Null(IptcProfile.TryParse(data));
    }

    /// <summary>A block of empty datasets is capped, so bookkeeping cannot cost many times the block's own size.</summary>
    [Fact]
    public void Parsing_StopsAtTheDatasetCap()
    {
        byte[] one = DataSet(2, 25, Array.Empty<byte>());
        var data = new byte[one.Length * 70_000];
        for (int i = 0; i < 70_000; i++)
        {
            one.CopyTo(data, i * one.Length);
        }

        var profile = new IptcProfile(data);
        Assert.Equal(65_536, profile.GetValues(IptcTag.Keywords).Count);
        Assert.False(profile.IsUnmodifiedSource);
    }

    [Fact]
    public void DamagedStream_KeepsTheDatasetsBeforeTheDamage_AndIsRewrittenClean()
    {
        byte[] good = Concat(DataSet(2, 0, Version4), DataSet(2, 5, Ascii("kept")));
        byte[] damaged = Concat(good, new byte[] { 0x1C, 0x02, 0x69, 0x01, 0x00, 0x41 });
        var profile = new IptcProfile(damaged);
        Assert.Equal("kept", profile.GetValue(IptcTag.ObjectName));
        Assert.False(profile.Contains(IptcTag.Headline));
        Assert.Equal(good, profile.ToByteArray());
    }

    [Fact]
    public void DateTime_IsWrittenAsCcyymmddAndHhmmssWithOffset_AndReadBack()
    {
        var profile = new IptcProfile();
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromMinutes(-150));
        profile.SetDateTime(IptcTag.DateCreated, when);

        Assert.Equal("20260304", profile.GetValue(IptcTag.DateCreated));
        Assert.Equal("050607-0230", profile.GetValue(IptcTag.TimeCreated));
        Assert.True(profile.TryGetDateTime(IptcTag.DateCreated, out DateTimeOffset read));
        Assert.Equal(when, read);
        Assert.Equal(when.Offset, read.Offset);

        profile.SetDateTime(IptcTag.ReferenceDate, when);
        Assert.Equal("20260304", profile.GetValue(IptcTag.ReferenceDate));
        Assert.Throws<ArgumentException>(() => profile.SetDateTime(IptcTag.Caption, when));
    }

    [Theory]
    [InlineData("20260230", null, false)] // Not a calendar date.
    [InlineData("2026030", null, false)]
    [InlineData("20260304", null, true)]
    [InlineData("20260304", "1230", true)] // Unreadable time: midnight, offset zero.
    [InlineData("20260304", "123000", true)] // No offset: zero.
    public void TryGetDateTime_IsLenientAboutTheTimeButNotTheDate(string date, string? time, bool ok)
    {
        var data = new List<byte[]> { DataSet(2, 0, Version4), DataSet(2, 55, Ascii(date)) };
        if (time is not null)
        {
            data.Add(DataSet(2, 60, Ascii(time)));
        }

        var profile = new IptcProfile(Concat(data.ToArray()));
        Assert.Equal(ok, profile.TryGetDateTime(IptcTag.DateCreated, out DateTimeOffset value));
        if (ok)
        {
            Assert.Equal(TimeSpan.Zero, value.Offset);
            Assert.Equal(time == "123000" ? new TimeSpan(12, 30, 0) : TimeSpan.Zero, value.TimeOfDay);
        }
    }

    [Fact]
    public void DeepClone_IsIndependent()
    {
        var original = new IptcProfile();
        original.AddValue(IptcTag.Keywords, "a");
        IptcProfile clone = original.DeepClone();
        clone.AddValue(IptcTag.Keywords, "b");
        original.SetValue(IptcTag.Headline, "only in the original");

        Assert.Equal(new[] { "a" }, original.GetValues(IptcTag.Keywords));
        Assert.Equal(new[] { "a", "b" }, clone.GetValues(IptcTag.Keywords));
        Assert.False(clone.Contains(IptcTag.Headline));

        // A parsed profile's verbatim source travels with the clone.
        var parsed = new IptcProfile(Concat(DataSet(2, 0, Version4), DataSet(2, 5, Ascii("x"))));
        IptcProfile parsedClone = parsed.DeepClone();
        Assert.True(parsedClone.IsUnmodifiedSource);
        Assert.Equal(parsed.ToByteArray(), parsedClone.ToByteArray());
    }

    [Fact]
    public void ImageMetadataClone_CopiesTheIptcProfileAndPhotoshopResources()
    {
        var metadata = new ImageMetadata { IptcProfile = new IptcProfile(), PhotoshopResources = new PhotoshopResources() };
        metadata.IptcProfile.SetValue(IptcTag.Credit, "c");
        metadata.PhotoshopResources.Set(new PhotoshopResource(0x03ED, new byte[] { 1, 2 }));
        ImageMetadata copy = metadata.DeepClone();
        copy.IptcProfile!.SetValue(IptcTag.Credit, "changed");
        copy.PhotoshopResources!.Remove(0x03ED);

        Assert.Equal("c", metadata.IptcProfile.GetValue(IptcTag.Credit));
        Assert.True(metadata.PhotoshopResources.Contains(0x03ED));

        var frame = new ImageFrameMetadata { IptcProfile = metadata.IptcProfile, PhotoshopResources = metadata.PhotoshopResources };
        ImageFrameMetadata frameCopy = frame.DeepClone();
        Assert.NotSame(frame.IptcProfile, frameCopy.IptcProfile);
        Assert.Equal("c", frameCopy.IptcProfile!.GetValue(IptcTag.Credit));
        Assert.True(frameCopy.PhotoshopResources!.Contains(0x03ED));
    }

    [Fact]
    public void ValueSnapshots_DoNotChangeWhenTheProfileDoes()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcTag.City, "Porto");
        IptcValue snapshot = profile.Values.Single(v => v.Tag == IptcTag.City);
        profile.SetValue(IptcTag.City, "Braga");

        Assert.Equal("Porto", snapshot.Value);
        byte[] raw = snapshot.ToByteArray();
        raw[0] = (byte)'X';
        Assert.Equal("Porto", Encoding.ASCII.GetString(snapshot.ToByteArray()));
        Assert.Equal(2, snapshot.Record);
        Assert.Equal(90, snapshot.DataSet);
    }

    /// <summary>One IIM 4.2 dataset: 0x1C, record, dataset, a two-byte big-endian length or 0x8004 + four bytes.</summary>
    internal static byte[] DataSet(int record, int dataset, byte[] data)
    {
        bool extended = data.Length > 0x7FFF;
        var result = new byte[5 + (extended ? 4 : 0) + data.Length];
        result[0] = 0x1C;
        result[1] = (byte)record;
        result[2] = (byte)dataset;
        if (extended)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), 0x8004);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(5), (uint)data.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), (ushort)data.Length);
        }

        data.CopyTo(result, result.Length - data.Length);
        return result;
    }

    internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
