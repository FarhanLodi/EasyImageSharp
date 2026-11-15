using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Iptc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// IPTC in real containers, driven by <c>Fixtures/iptc/manifest.json</c>: JPEG files written by Pillow with APP13
/// Photoshop image resources, and TIFF files written by tifffile with tag 33723 typed LONG, UNDEFINED and BYTE in
/// both byte orders, all read back by Pillow's or tifffile's own IPTC readers before they were committed. Then the
/// write side: lossless round trips, cross-format moves, the IPTC digest rules, and corrupt input.
/// </summary>
/// <remarks>
/// The JPEG decoder hands marker segments to <see cref="JpegMetadataReader"/>; the JPEG tests here drive that
/// reader directly over the file's segments, as the decoder does, so they exercise exactly the code that parses
/// APP13. <see cref="JpegDecoder_ReportsIptcFromApp13"/> checks the decoder end to end.
/// </remarks>
public class IptcFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static Manifest? cached;

    public static IEnumerable<object[]> JpegFixtures => Entries("jpeg");

    public static IEnumerable<object[]> TiffFixtures => Entries("tiff");

    [Fact]
    public void Corpus_IsPresent()
    {
        Assert.True(Load().Fixtures.Count >= 18, "Fixtures/iptc/manifest.json is incomplete; run Fixtures/generate.py.");
    }

    [Theory]
    [MemberData(nameof(JpegFixtures))]
    public void Jpeg_MetadataReaderReportsTheManifestFacts(string name)
    {
        FixtureEntry entry = Get(name);
        ImageMetadata metadata = ReadJpegMetadata(FixturePath.Read($"iptc/{entry.File}"));
        AssertMatches(entry, metadata.IptcProfile, metadata.PhotoshopResources);
    }

    [Theory]
    [MemberData(nameof(JpegFixtures))]
    public void Jpeg_PixelsStillDecode(string name)
    {
        FixtureEntry entry = Get(name);
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read($"iptc/{entry.File}"));
        Assert.Equal(entry.Width, image.Width);
        Assert.Equal(entry.Height, image.Height);
    }

    /// <summary>
    /// End to end through <see cref="Image.Identify(ReadOnlySpan{byte})"/> and <see cref="Image.Load{TPixel}(ReadOnlySpan{byte})"/>.
    /// This needs the JPEG decoder to pass APP13 (0xED) segments to <see cref="JpegMetadataReader"/>, as it does for
    /// APP0-APP2 and COM.
    /// </summary>
    [Fact]
    public void JpegDecoder_ReportsIptcFromApp13()
    {
        FixtureEntry entry = Get("jpeg_utf8");
        byte[] file = FixturePath.Read($"iptc/{entry.File}");
        ImageInfo info = Image.Identify(file);
        Assert.True(info.Metadata.IptcProfile is not null, "JpegDecoder.Identify did not pass the APP13 segment to JpegMetadataReader.");
        AssertMatches(entry, info.Metadata.IptcProfile, info.Metadata.PhotoshopResources);

        using Image<Rgba32> image = Image.Load<Rgba32>(file);
        Assert.True(image.Metadata.IptcProfile is not null, "JpegDecoder.Decode did not pass the APP13 segment to JpegMetadataReader.");
        AssertMatches(entry, image.Metadata.IptcProfile, image.Metadata.PhotoshopResources);
    }

    [Theory]
    [MemberData(nameof(TiffFixtures))]
    public void Tiff_DecodeAndIdentifyReportTheManifestFacts(string name)
    {
        FixtureEntry entry = Get(name);
        byte[] file = FixturePath.Read($"iptc/{entry.File}");
        using Image<Rgba32> image = Image.Load<Rgba32>(file);
        AssertMatches(entry, image.Metadata.IptcProfile, image.Metadata.PhotoshopResources);
        AssertMatches(entry, image.Frames.RootFrame.Metadata.IptcProfile, image.Frames.RootFrame.Metadata.PhotoshopResources);
        Assert.True(
            MemoryMarshal.AsBytes(image.Frames.RootFrame.PixelSpan).SequenceEqual(FixturePath.Read($"iptc/{entry.Rgba}")),
            $"{name}: pixels differ from the tifffile/Pillow ground truth.");

        ImageInfo info = Image.Identify(file);
        AssertMatches(entry, info.Metadata.IptcProfile, info.Metadata.PhotoshopResources);
    }

    /// <summary>The IPTC-NAA and Photoshop tags are not left in the EXIF profile, where they would be written twice.</summary>
    [Theory]
    [MemberData(nameof(TiffFixtures))]
    public void Tiff_IptcAndPhotoshopTagsDoNotLeakIntoExif(string name)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read($"iptc/{Get(name).File}"));
        foreach (EasyImageSharp.Metadata.Exif.IExifValue value in image.Metadata.ExifProfile?.Values ?? Array.Empty<EasyImageSharp.Metadata.Exif.IExifValue>())
        {
            Assert.NotEqual(33723, value.Tag.Id);
            Assert.NotEqual(34377, value.Tag.Id);
        }
    }

    [Fact]
    public void Jpeg_SplitResourceBlockMatchesItsSingleSegmentTwin()
    {
        ImageMetadata single = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_utf8.jpg"));
        ImageMetadata split = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_multisegment.jpg"));
        Assert.Equal(single.IptcProfile!.ToByteArray(), split.IptcProfile!.ToByteArray());
        Assert.Equal(single.PhotoshopResources!.ToByteArray(), split.PhotoshopResources!.ToByteArray());
    }

    /// <summary>An untouched block is written back byte for byte: the resources, their order, names and the digest.</summary>
    [Theory]
    [InlineData("jpeg_utf8")]
    [InlineData("jpeg_multisegment")]
    public void Jpeg_UnmodifiedResourceBlockRoundTripsExactly(string name)
    {
        FixtureEntry entry = Get(name);
        byte[] encoded = EncodeJpegWith(ReadJpegMetadata(FixturePath.Read($"iptc/{entry.File}")));
        (byte[] block, int segments) = JoinApp13(encoded);
        Assert.Equal(1, segments);
        Assert.Equal(Convert.FromHexString(entry.Irb!), block);
    }

    [Theory]
    [InlineData("jpeg_latin1")]
    [InlineData("jpeg_unknown_records")]
    [InlineData("jpeg_extended")]
    [InlineData("jpeg_stale_digest")]
    public void Jpeg_IptcRoundTripsLosslessly(string name)
    {
        FixtureEntry entry = Get(name);
        ImageMetadata source = ReadJpegMetadata(FixturePath.Read($"iptc/{entry.File}"));
        ImageMetadata reread = ReadJpegMetadata(EncodeJpegWith(source));
        Assert.Equal(source.IptcProfile!.ToByteArray(), reread.IptcProfile!.ToByteArray());
        AssertMatches(entry, reread.IptcProfile, reread.PhotoshopResources);
    }

    [Theory]
    [InlineData("tiff_le_long", false)]
    [InlineData("tiff_be_long", true)]
    [InlineData("tiff_le_undefined", false)]
    [InlineData("tiff_be_byte", true)]
    [InlineData("tiff_photoshop_only", false)]
    public void Tiff_IptcAndPhotoshopTagsRoundTrip(string name, bool bigTiff)
    {
        FixtureEntry entry = Get(name);
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read($"iptc/{entry.File}"));
        byte[] iim = image.Metadata.IptcProfile!.ToByteArray();
        byte[] encoded = Encode(image, new TiffEncoder { Format = bigTiff ? TiffFormatType.BigTiff : TiffFormatType.Classic });

        // At the byte level, read with the spec helper rather than the decoder: 33723 is LONG, holding the block.
        Dictionary<int, TiffTestReader.Field> fields = TiffTestReader.FirstDirectory(encoded);
        TiffTestReader.Field iptcField = fields[33723];
        Assert.Equal(4, iptcField.Type);
        byte[] stored = iptcField.Data;
        Assert.Equal(0, stored.Length % 4);
        Assert.Equal(iim, stored.AsSpan(0, iim.Length).ToArray());
        Assert.True(stored.AsSpan(iim.Length).IndexOfAnyExcept((byte)0) < 0, "The LONG padding is not zero.");
        if (entry.Irb is not null)
        {
            Assert.Equal(Convert.FromHexString(entry.Irb), fields[34377].Data); // Untouched: byte for byte.
        }

        using Image<Rgba32> reread = Image.Load<Rgba32>(encoded);
        AssertMatches(entry, reread.Metadata.IptcProfile, reread.Metadata.PhotoshopResources);
    }

    [Fact]
    public void TiffToJpeg_MovesIptcIntoTheResourceBlock_KeepingTheDigest()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Read("iptc/tiff_le_long.tif"));
        byte[] iim = image.Metadata.IptcProfile!.ToByteArray();
        (byte[] block, _) = JoinApp13(Encode(image, new JpegEncoder()));
        var resources = new PhotoshopResources(block);
        Assert.Equal(new ushort[] { 0x03ED, 0x0404, 0x0425 }, resources.Resources.Select(r => r.Id).ToArray());
        Assert.Equal(iim, resources.Get(0x0404)!.ToByteArray());
        Assert.Equal(MD5.HashData(iim), resources.Get(0x0425)!.ToByteArray());
    }

    [Fact]
    public void JpegToTiff_WritesTag33723AndTheOtherResourcesAsTag34377()
    {
        ImageMetadata metadata = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_utf8.jpg"));
        using var image = new Image<Rgba32>(4, 3);
        CopyMetadata(metadata, image.Metadata);
        Dictionary<int, TiffTestReader.Field> fields = TiffTestReader.FirstDirectory(Encode(image, new TiffEncoder()));
        Assert.Equal(metadata.IptcProfile!.ToByteArray(), fields[33723].Data.AsSpan(0, metadata.IptcProfile.ToByteArray().Length).ToArray());
        var resources = new PhotoshopResources(fields[34377].Data);
        Assert.Equal(new ushort[] { 0x03ED, 0x0425, 0x0FA0 }, resources.Resources.Select(r => r.Id).ToArray());
        Assert.Equal("EIS plug-in", resources.Get(0x0FA0)!.Name);
    }

    [Fact]
    public void EditedIptc_GetsAFreshDigest_WhenTheImageHasNoXmp()
    {
        ImageMetadata metadata = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_utf8.jpg"));
        metadata.IptcProfile!.AddValue(IptcTag.Keywords, "acrescentado");
        PhotoshopResources written = WrittenResources(metadata);
        byte[] iim = written.Get(0x0404)!.ToByteArray();
        Assert.Equal(metadata.IptcProfile.ToByteArray(), iim);
        Assert.Equal(MD5.HashData(iim), written.Get(0x0425)!.ToByteArray());
        Assert.Equal(new ushort[] { 0x03ED, 0x0404, 0x0425, 0x0FA0 }, written.Resources.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void EditedIptc_LosesItsDigest_WhenTheImageHasXmp()
    {
        ImageMetadata metadata = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_utf8.jpg"));
        metadata.XmpProfile = new XmpProfile("<x:xmpmeta xmlns:x='adobe:ns:meta/'/>");
        metadata.IptcProfile!.SetValue(IptcTag.City, "Porto");
        PhotoshopResources written = WrittenResources(metadata);
        Assert.False(written.Contains(0x0425));
        Assert.Equal("Porto", new IptcProfile(written.Get(0x0404)!.ToByteArray()).GetValue(IptcTag.City));
    }

    [Fact]
    public void RemovingIptc_RemovesTheResourceAndTheDigest_AndRemovingTheBlockRemovesApp13()
    {
        ImageMetadata metadata = ReadJpegMetadata(FixturePath.Read("iptc/jpeg_utf8.jpg"));
        metadata.IptcProfile = null;
        Assert.Equal(new ushort[] { 0x03ED, 0x0FA0 }, WrittenResources(metadata).Resources.Select(r => r.Id).ToArray());

        metadata.PhotoshopResources = null;
        Assert.Equal(0, JoinApp13(EncodeJpegWith(metadata)).Segments);
    }

    [Fact]
    public void NewIptcProfile_IsWrittenAsTheOnlyResource()
    {
        var metadata = new ImageMetadata { IptcProfile = new IptcProfile() };
        metadata.IptcProfile.SetValue(IptcTag.Byline, "Ana Sá");
        PhotoshopResources written = WrittenResources(metadata);
        Assert.Equal(new ushort[] { 0x0404 }, written.Resources.Select(r => r.Id).ToArray());
        Assert.Equal("Ana Sá", new IptcProfile(written.Get(0x0404)!.ToByteArray()).GetValue(IptcTag.Byline));

        // An empty profile writes nothing at all.
        metadata.IptcProfile.Clear();
        Assert.Equal(0, JoinApp13(EncodeJpegWith(metadata)).Segments);
    }

    /// <summary>A block larger than one segment is split over several APP13 segments and joined again on read.</summary>
    [Fact]
    public void LargeResourceBlock_SpansSeveralApp13Segments()
    {
        var metadata = new ImageMetadata { IptcProfile = new IptcProfile(), PhotoshopResources = new PhotoshopResources() };
        var preview = new byte[150_000];
        new Random(7).NextBytes(preview);
        metadata.IptcProfile.SetValue(IptcTag.ObjectPreviewData, preview);
        var small = new byte[1000];
        metadata.PhotoshopResources.Set(new PhotoshopResource(0x03ED, small));
        metadata.PhotoshopResources.Set(new PhotoshopResource(0x0FA0, small));

        byte[] encoded = EncodeJpegWith(metadata);
        (byte[] block, int segments) = JoinApp13(encoded);
        Assert.True(segments >= 3, $"{segments} APP13 segment(s) for a {block.Length}-byte block.");
        Assert.Equal(new ushort[] { 0x03ED, 0x0404, 0x0FA0 }, new PhotoshopResources(block).Resources.Select(r => r.Id).ToArray());

        ImageMetadata reread = ReadJpegMetadata(encoded);
        Assert.Equal(preview, reread.IptcProfile!.Values.Single(v => v.Tag == IptcTag.ObjectPreviewData).ToByteArray());
        Assert.Equal(2, reread.PhotoshopResources!.Count);
    }

    /// <summary>
    /// Mutations of every fixture: decode and identify may only succeed or throw the documented exceptions, and the
    /// APP13 reader on its own may only raise its size cap.
    /// </summary>
    [Fact]
    public void MutatedFixtures_KeepTheExceptionContract()
    {
        var random = new Random(4242);
        foreach (FixtureEntry entry in Load().Fixtures)
        {
            byte[] original = FixturePath.Read($"iptc/{entry.File}");
            for (int i = 0; i < 24; i++)
            {
                byte[] mutated = (byte[])original.Clone();
                int start = Math.Min(2 + random.Next(Math.Min(600, original.Length - 2)), original.Length - 1);
                switch (i % 3)
                {
                    case 0:
                        mutated = mutated.AsSpan(0, start).ToArray();
                        break;
                    case 1:
                        for (int k = 0; k < 6 && start + k < mutated.Length; k++)
                        {
                            mutated[start + k] = (byte)random.Next(256);
                        }

                        break;
                    default:
                        mutated[start] = 0xFF;
                        if (start + 1 < mutated.Length)
                        {
                            mutated[start + 1] = 0x7F;
                        }

                        break;
                }

                AssertContract(() => Image.Identify(mutated), entry.Name, i);
                AssertContract(() => Image.Load<Rgba32>(mutated).Dispose(), entry.Name, i);
                if (entry.Container == "jpeg")
                {
                    AssertContract(() => ReadJpegMetadata(mutated), entry.Name, i);
                }
            }
        }
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private static void AssertContract(Action action, string name, int mutation)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
        }
        catch (Exception ex)
        {
            Assert.Fail($"{name} mutation {mutation}: {ex.GetType().Name} escaped: {ex.Message}");
        }
    }

    private static void AssertMatches(FixtureEntry entry, IptcProfile? profile, PhotoshopResources? resources)
    {
        if (entry.Iptc is null)
        {
            Assert.True(profile is null, $"{entry.Name}: expected no IPTC profile. [{entry.Notes}]");
        }
        else
        {
            Assert.True(profile is not null, $"{entry.Name}: no IPTC profile was read. [{entry.Notes}]");
            Assert.Equal(entry.Iptc.Utf8, profile!.IsUtf8);
            IReadOnlyList<IptcValue> values = profile.Values;
            Assert.True(
                values.Count == entry.Iptc.Datasets.Count,
                $"{entry.Name}: {values.Count} dataset(s), manifest lists {entry.Iptc.Datasets.Count}. [{entry.Notes}]");
            for (int i = 0; i < values.Count; i++)
            {
                DataSetEntry expected = entry.Iptc.Datasets[i];
                IptcValue got = values[i];
                Assert.True(
                    got.Record == expected.Record && got.DataSet == expected.Dataset,
                    $"{entry.Name} #{i}: {got.Record}:{got.DataSet}, manifest says {expected.Record}:{expected.Dataset}.");
                byte[] raw = got.ToByteArray();
                if (expected.Hex is not null)
                {
                    Assert.Equal(expected.Hex, Convert.ToHexString(raw).ToLowerInvariant());
                }
                else
                {
                    Assert.Equal(expected.Length, raw.Length);
                    Assert.Equal(expected.ContentSha256, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
                }

                if (expected.Text is not null)
                {
                    Assert.Equal(expected.Text, got.Value);
                }
            }
        }

        List<ResourceEntry> expectedResources = entry.Resources ?? new List<ResourceEntry>();
        if (expectedResources.Count == 0)
        {
            Assert.True(resources is null, $"{entry.Name}: expected no other Photoshop resources, got {resources}.");
            return;
        }

        Assert.True(resources is not null, $"{entry.Name}: the other Photoshop resources were not kept.");
        Assert.Equal(expectedResources.Select(r => (ushort)r.Id), resources!.Resources.Select(r => r.Id));
        for (int i = 0; i < expectedResources.Count; i++)
        {
            Assert.Equal(expectedResources[i].Name, resources.Resources[i].Name);
            Assert.Equal(expectedResources[i].Hex, Convert.ToHexString(resources.Resources[i].ToByteArray()).ToLowerInvariant());
        }
    }

    /// <summary>Walks a JPEG's marker segments up to the first scan and feeds them to the metadata reader, as the decoder does.</summary>
    internal static ImageMetadata ReadJpegMetadata(byte[] jpeg)
    {
        var reader = new JpegMetadataReader();
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xDA)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            int available = Math.Min(Math.Max(length - 2, 0), jpeg.Length - pos - 4);
            if (marker is 0xE0 or 0xE1 or 0xE2 or 0xED or 0xFE && available > 0)
            {
                reader.ProcessSegment(marker, jpeg.AsSpan(pos + 4, available));
            }

            pos += 2 + length;
        }

        return reader.Finish();
    }

    /// <summary>Concatenates the payloads (after "Photoshop 3.0\0") of every APP13 segment, read straight from the bytes.</summary>
    internal static (byte[] Block, int Segments) JoinApp13(byte[] jpeg)
    {
        var block = new List<byte>();
        int segments = 0;
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] != 0xDA)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            ReadOnlySpan<byte> payload = jpeg.AsSpan(pos + 4, length - 2);
            if (jpeg[pos + 1] == 0xED && payload.StartsWith("Photoshop 3.0\0"u8))
            {
                block.AddRange(payload[14..].ToArray());
                segments++;
            }

            pos += 2 + length;
        }

        return (block.ToArray(), segments);
    }

    private static PhotoshopResources WrittenResources(ImageMetadata metadata)
    {
        (byte[] block, int segments) = JoinApp13(EncodeJpegWith(metadata));
        Assert.True(segments > 0, "No APP13 segment was written.");
        return new PhotoshopResources(block);
    }

    private static byte[] EncodeJpegWith(ImageMetadata metadata)
    {
        using var image = new Image<Rgba32>(8, 8);
        CopyMetadata(metadata, image.Metadata);
        return Encode(image, new JpegEncoder());
    }

    private static void CopyMetadata(ImageMetadata from, ImageMetadata to)
    {
        to.IptcProfile = from.IptcProfile?.DeepClone();
        to.PhotoshopResources = from.PhotoshopResources?.DeepClone();
        to.XmpProfile = from.XmpProfile?.DeepClone();
    }

    private static byte[] Encode(Image image, IImageEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static IEnumerable<object[]> Entries(string container)
        => Load().Fixtures.Where(f => f.Container == container).Select(f => new object[] { f.Name });

    private static FixtureEntry Get(string name)
        => Load().Fixtures.SingleOrDefault(f => f.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"iptc/{name} is not in manifest.json; run Fixtures/generate.py.");

    private static Manifest Load()
        => cached ??= JsonSerializer.Deserialize<Manifest>(FixturePath.Read("iptc/manifest.json"), JsonOptions)
            ?? throw new InvalidOperationException("iptc/manifest.json is empty.");

    private sealed record Manifest(List<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(
        string Name, string File, string Container, int Width, int Height, string? Rgba, IptcEntry? Iptc,
        List<ResourceEntry>? Resources, string? Irb, string Notes);

    private sealed record IptcEntry(bool Utf8, List<DataSetEntry> Datasets);

    private sealed record DataSetEntry(int Record, int Dataset, string? Hex, string? Text, int? Length, string? ContentSha256);

    private sealed record ResourceEntry(int Id, string Name, string Hex);
}

/// <summary>A minimal reader of a TIFF's first directory, classic or BigTIFF, written from the specifications.</summary>
internal static class TiffTestReader
{
    public static Dictionary<int, Field> FirstDirectory(byte[] file) => Directories(file)[0];

    /// <summary>Every directory in the main chain, each as tag → (type, count, raw value bytes in file order).</summary>
    public static List<Dictionary<int, Field>> Directories(byte[] file)
    {
        bool be = file[0] == (byte)'M';
        bool big = U16(file, 2, be) == 43;
        long offset = big ? (long)U64(file, 8, be) : U32(file, 4, be);
        var result = new List<Dictionary<int, Field>>();
        while (offset != 0 && result.Count < 1000)
        {
            result.Add(ReadDirectory(file, offset, be, big, out offset));
        }

        return result;
    }

    public static Dictionary<int, Field> ReadDirectory(byte[] file, long offset, bool be, bool big, out long next)
    {
        long count = big ? (long)U64(file, (int)offset, be) : U16(file, (int)offset, be);
        int entrySize = big ? 20 : 12;
        int first = (int)offset + (big ? 8 : 2);
        var fields = new Dictionary<int, Field>();
        for (int i = 0; i < count; i++)
        {
            int e = first + (i * entrySize);
            int tag = U16(file, e, be);
            int type = U16(file, e + 2, be);
            long n = big ? (long)U64(file, e + 4, be) : U32(file, e + 4, be);
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, _ => 8 };
            long total = size * n;
            int valueField = e + (big ? 12 : 8);
            long at = total <= (big ? 8 : 4) ? valueField : big ? (long)U64(file, valueField, be) : U32(file, valueField, be);
            fields[tag] = new Field(type, n, file.AsSpan((int)at, (int)total).ToArray(), at);
        }

        int nextAt = first + (int)(count * entrySize);
        next = big ? (long)U64(file, nextAt, be) : U32(file, nextAt, be);
        return fields;
    }

    public static int U16(byte[] b, int o, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

    public static uint U32(byte[] b, int o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    public static ulong U64(byte[] b, int o, bool be) => be ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));

    /// <summary>One directory entry: the field type, the element count, the raw value bytes and where they sit.</summary>
    public readonly record struct Field(int Type, long Count, byte[] Data, long Offset)
    {
        /// <summary>The value as unsigned integers of the field's width, in the file's byte order.</summary>
        public ulong[] Integers(bool bigEndian)
        {
            int size = this.Type switch { 1 or 7 => 1, 3 => 2, 4 or 13 => 4, _ => 8 };
            var values = new ulong[this.Data.Length / size];
            for (int i = 0; i < values.Length; i++)
            {
                ReadOnlySpan<byte> s = this.Data.AsSpan(i * size, size);
                values[i] = size switch
                {
                    1 => s[0],
                    2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                    4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                    _ => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
                };
            }

            return values;
        }
    }
}
