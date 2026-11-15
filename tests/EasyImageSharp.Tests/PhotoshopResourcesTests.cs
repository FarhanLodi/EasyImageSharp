using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EasyImageSharp.Metadata.Iptc;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The Photoshop image resource block: its layout (signature, id, padded Pascal name, size, padded data), lenient
/// parsing, ordering, and the rules encoders follow for the IPTC resource (0x0404) and its MD5 digest (0x0425).
/// </summary>
/// <remarks>Expected blocks are assembled by <see cref="Resource"/>, written from Adobe's documented layout.</remarks>
public class PhotoshopResourcesTests
{
    private const ushort Iptc = 0x0404;
    private const ushort Digest = 0x0425;
    private const ushort Resolution = 0x03ED;

    [Fact]
    public void Serialization_PadsNamesAndDataToEvenLengths()
    {
        var block = new PhotoshopResources();
        block.Set(new PhotoshopResource(Resolution, new byte[] { 1, 2, 3 }));
        block.Set(new PhotoshopResource(0x0FA0, "ab", new byte[] { 9, 8 }));

        byte[] expected = IptcProfileTests.Concat(
            Resource("8BIM", Resolution, string.Empty, new byte[] { 1, 2, 3 }),
            Resource("8BIM", 0x0FA0, "ab", new byte[] { 9, 8 }));
        Assert.Equal(expected, block.ToByteArray());

        var reread = new PhotoshopResources(expected);
        Assert.Equal(2, reread.Count);
        Assert.Equal("ab", reread.Resources[1].Name);
        Assert.Equal("8BIM", reread.Resources[1].Signature);
        Assert.Equal(new byte[] { 1, 2, 3 }, reread.Get(Resolution)!.ToByteArray());
        Assert.Equal(expected, reread.ToByteArray());
    }

    [Fact]
    public void Set_InsertsInIdentifierOrder_AndReplacesInPlace()
    {
        var block = new PhotoshopResources();
        block.Set(new PhotoshopResource(0x0FA0, new byte[] { 1 }));
        block.Set(new PhotoshopResource(Resolution, new byte[] { 2 }));
        block.Set(new PhotoshopResource(Iptc, new byte[] { 3 }));
        Assert.Equal(new ushort[] { Resolution, Iptc, 0x0FA0 }, block.Resources.Select(r => r.Id).ToArray());

        block.Set(new PhotoshopResource(Iptc, new byte[] { 4, 4 }));
        Assert.Equal(new ushort[] { Resolution, Iptc, 0x0FA0 }, block.Resources.Select(r => r.Id).ToArray());
        Assert.Equal(2, block.Get(Iptc)!.Length);
        Assert.True(block.Remove(Iptc));
        Assert.False(block.Remove(Iptc));
        Assert.False(block.Contains(Iptc));
        Assert.Null(block.Get(Iptc));
    }

    [Fact]
    public void Names_AreValidated()
    {
        Assert.Throws<ArgumentException>(() => new PhotoshopResource(1, new string('n', 256), Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new PhotoshopResource(1, "Ω", Array.Empty<byte>()));
        Assert.Equal("Café", new PhotoshopResource(1, "Café", Array.Empty<byte>()).Name);
    }

    [Fact]
    public void Parsing_StopsAtTheFirstMalformedResource_KeepingTheOnesBefore()
    {
        byte[] first = Resource("8BIM", Resolution, string.Empty, new byte[] { 5, 6 });
        byte[] badSignature = Resource("XXXX", Iptc, string.Empty, new byte[] { 7 });
        byte[] truncated = Resource("8BIM", Iptc, string.Empty, new byte[100]).AsSpan(0, 40).ToArray();

        Assert.Equal(1, new PhotoshopResources(IptcProfileTests.Concat(first, badSignature)).Count);
        Assert.Equal(1, new PhotoshopResources(IptcProfileTests.Concat(first, truncated)).Count);
        Assert.Equal(1, new PhotoshopResources(IptcProfileTests.Concat(first, new byte[] { 0x38, 0x42 })).Count);
        Assert.Null(PhotoshopResources.TryParse(badSignature));
        Assert.Equal(0, new PhotoshopResources(Array.Empty<byte>()).Count);
    }

    [Fact]
    public void Parsing_AcceptsTheOtherAdobeSignatures_AndAMissingFinalPadByte()
    {
        byte[] data = IptcProfileTests.Concat(
            Resource("MeSa", 0x0FA1, string.Empty, new byte[] { 1, 2 }),
            Resource("8BIM", Resolution, string.Empty, new byte[] { 1, 2, 3 }));
        data = data.AsSpan(0, data.Length - 1).ToArray(); // The writer omitted the last pad byte.
        var block = new PhotoshopResources(data);
        Assert.Equal(2, block.Count);
        Assert.Equal("MeSa", block.Resources[0].Signature);
        Assert.Equal(new byte[] { 1, 2, 3 }, block.Resources[1].ToByteArray());
    }

    [Fact]
    public void Parsing_StopsAtTheResourceCap()
    {
        byte[] one = Resource("8BIM", 0x0FA0, string.Empty, Array.Empty<byte>());
        var data = new byte[one.Length * 70_000];
        for (int i = 0; i < 70_000; i++)
        {
            one.CopyTo(data, i * one.Length);
        }

        Assert.Equal(65_536, new PhotoshopResources(data).Count);
    }

    [Fact]
    public void SplitIptc_MovesTheIptcResourceIntoAProfile()
    {
        byte[] iim = IptcProfileTests.Concat(
            IptcProfileTests.DataSet(2, 0, new byte[] { 0, 4 }), IptcProfileTests.DataSet(2, 5, Encoding.ASCII.GetBytes("t")));
        byte[] data = IptcProfileTests.Concat(
            Resource("8BIM", Resolution, string.Empty, new byte[] { 1 }),
            Resource("8BIM", Iptc, string.Empty, iim));

        (IptcProfile? profile, PhotoshopResources? others) = PhotoshopResources.SplitIptc(data);
        Assert.Equal("t", profile!.GetValue(IptcTag.ObjectName));
        Assert.Equal(new[] { Resolution }, others!.Resources.Select(r => r.Id));

        (profile, others) = PhotoshopResources.SplitIptc(Resource("8BIM", Iptc, string.Empty, iim));
        Assert.NotNull(profile);
        Assert.Null(others);
    }

    [Fact]
    public void Compose_KeepsTheDigestOfAnUnmodifiedProfile()
    {
        (IptcProfile profile, byte[] iim) = ParsedProfile();
        byte[] staleDigest = MD5.HashData(Encoding.ASCII.GetBytes("someone else's IPTC"));
        PhotoshopResources block = BlockWith(staleDigest);

        PhotoshopResources composed = PhotoshopResources.Compose(block, profile, profile.ToByteArray(), hasXmp: true, embedIptc: true)!;
        Assert.Equal(iim, composed.Get(Iptc)!.ToByteArray());
        Assert.Equal(staleDigest, composed.Get(Digest)!.ToByteArray()); // Untouched IPTC: the digest stays as read.
        Assert.Equal(new ushort[] { Resolution, Iptc, Digest }, composed.Resources.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Compose_RecomputesTheDigestOfAChangedProfile_WhenThereIsNoXmp()
    {
        (IptcProfile profile, byte[] iim) = ParsedProfile();
        PhotoshopResources block = BlockWith(MD5.HashData(iim));
        profile.SetValue(IptcTag.Headline, "edited");
        byte[] edited = profile.ToByteArray();

        PhotoshopResources composed = PhotoshopResources.Compose(block, profile, edited, hasXmp: false, embedIptc: true)!;
        Assert.Equal(edited, composed.Get(Iptc)!.ToByteArray());
        Assert.Equal(MD5.HashData(edited), composed.Get(Digest)!.ToByteArray());
    }

    [Fact]
    public void Compose_DropsTheDigestOfAChangedProfile_WhenXmpCouldDisagree()
    {
        (IptcProfile profile, byte[] iim) = ParsedProfile();
        PhotoshopResources block = BlockWith(MD5.HashData(iim));
        profile.SetValue(IptcTag.Headline, "edited");

        PhotoshopResources composed = PhotoshopResources.Compose(block, profile, profile.ToByteArray(), hasXmp: true, embedIptc: true)!;
        Assert.False(composed.Contains(Digest));
        Assert.True(composed.Contains(Iptc));
    }

    [Fact]
    public void Compose_KeepsADigestThatAlreadyMatches_EvenWithXmp()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcTag.Credit, "c");
        byte[] iim = profile.ToByteArray();
        PhotoshopResources composed = PhotoshopResources.Compose(BlockWith(MD5.HashData(iim)), profile, iim, hasXmp: true, embedIptc: true)!;
        Assert.Equal(MD5.HashData(iim), composed.Get(Digest)!.ToByteArray());
    }

    [Fact]
    public void Compose_RemovingTheProfileRemovesItsDigest_ButKeepsOtherResources()
    {
        (_, byte[] iim) = ParsedProfile();
        PhotoshopResources composed = PhotoshopResources.Compose(BlockWith(MD5.HashData(iim)), null, null, hasXmp: false, embedIptc: true)!;
        Assert.Equal(new[] { Resolution }, composed.Resources.Select(r => r.Id));

        Assert.Null(PhotoshopResources.Compose(null, null, null, hasXmp: false, embedIptc: true));
        Assert.Null(PhotoshopResources.Compose(new PhotoshopResources(), new IptcProfile(), Array.Empty<byte>(), false, true));
    }

    [Fact]
    public void Compose_ForTiff_DoesNotAddAnIptcResource_ButUpdatesOneThatIsThere()
    {
        (IptcProfile profile, _) = ParsedProfile();
        byte[] iim = profile.ToByteArray();
        PhotoshopResources withoutIptc = BlockWith(null);
        Assert.False(PhotoshopResources.Compose(withoutIptc, profile, iim, false, embedIptc: false)!.Contains(Iptc));

        PhotoshopResources withIptc = BlockWith(null);
        withIptc.Set(new PhotoshopResource(Iptc, new byte[] { 0x1C }));
        Assert.Equal(iim, PhotoshopResources.Compose(withIptc, profile, iim, false, embedIptc: false)!.Get(Iptc)!.ToByteArray());
    }

    [Fact]
    public void Compose_DoesNotModifyTheImagesOwnBlock()
    {
        (IptcProfile profile, _) = ParsedProfile();
        PhotoshopResources block = BlockWith(new byte[16]);
        _ = PhotoshopResources.Compose(block, profile, profile.ToByteArray(), false, true);
        Assert.Equal(new ushort[] { Resolution, Digest }, block.Resources.Select(r => r.Id).ToArray());
        Assert.Equal(new byte[16], block.Get(Digest)!.ToByteArray());
    }

    /// <summary>One image resource: signature, big-endian id, Pascal name padded to even, size, data padded to even.</summary>
    internal static byte[] Resource(string signature, ushort id, string name, byte[] data)
    {
        byte[] nameBytes = Encoding.Latin1.GetBytes(name);
        int nameField = (1 + nameBytes.Length + 1) & ~1;
        var result = new byte[4 + 2 + nameField + 4 + ((data.Length + 1) & ~1)];
        Encoding.ASCII.GetBytes(signature).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), id);
        result[6] = (byte)nameBytes.Length;
        nameBytes.CopyTo(result, 7);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(6 + nameField), (uint)data.Length);
        data.CopyTo(result, 10 + nameField);
        return result;
    }

    private static (IptcProfile Profile, byte[] Iim) ParsedProfile()
    {
        byte[] iim = IptcProfileTests.Concat(
            IptcProfileTests.DataSet(2, 0, new byte[] { 0, 4 }), IptcProfileTests.DataSet(2, 5, Encoding.ASCII.GetBytes("name")));
        return (new IptcProfile(iim), iim);
    }

    private static PhotoshopResources BlockWith(byte[]? digest)
    {
        var block = new PhotoshopResources();
        block.Set(new PhotoshopResource(Resolution, new byte[] { 0, 0x48, 0, 0, 0, 1, 0, 1 }));
        if (digest is not null)
        {
            block.Set(new PhotoshopResource(Digest, digest));
        }

        return block;
    }
}
