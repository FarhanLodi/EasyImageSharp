using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Metadata.Exif;

namespace EasyImageSharp.Metadata.Iptc;

/// <summary>
/// One Photoshop image resource: a signature (normally <c>8BIM</c>), a 16-bit resource identifier, an optional
/// Pascal-string name and the resource data. Instances are immutable.
/// </summary>
public sealed class PhotoshopResource
{
    private static readonly byte[] DefaultSignature = "8BIM"u8.ToArray();

    private readonly byte[] signature;
    private readonly byte[] name;
    private readonly byte[] data;

    /// <summary>Creates an unnamed <c>8BIM</c> resource.</summary>
    /// <param name="id">The resource identifier, e.g. 0x0404 for IPTC-NAA or 0x03ED for resolution info.</param>
    /// <param name="data">The resource data (copied).</param>
    public PhotoshopResource(ushort id, byte[] data)
        : this(id, string.Empty, data)
    {
    }

    /// <summary>Creates a named <c>8BIM</c> resource.</summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="name">The resource name: at most 255 characters, each in ISO 8859-1.</param>
    /// <param name="data">The resource data (copied).</param>
    /// <exception cref="ArgumentException">The name is longer than 255 characters or has a character outside ISO 8859-1.</exception>
    public PhotoshopResource(ushort id, string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        if (name.Length > 255)
        {
            throw new ArgumentException("A Photoshop resource name holds at most 255 characters.", nameof(name));
        }

        foreach (char c in name)
        {
            if (c > 0xFF)
            {
                throw new ArgumentException("A Photoshop resource name holds ISO 8859-1 characters only.", nameof(name));
            }
        }

        this.signature = DefaultSignature;
        this.Id = id;
        this.name = Encoding.Latin1.GetBytes(name);
        this.data = (byte[])data.Clone();
    }

    /// <summary>Wraps parsed parts without copying them.</summary>
    internal PhotoshopResource(byte[] signature, ushort id, byte[] name, byte[] data)
    {
        this.signature = signature;
        this.Id = id;
        this.name = name;
        this.data = data;
    }

    /// <summary>The four-character signature, normally <c>8BIM</c>.</summary>
    public string Signature => Encoding.ASCII.GetString(this.signature);

    /// <summary>The resource identifier.</summary>
    public ushort Id { get; }

    /// <summary>The resource name (usually empty), decoded as ISO 8859-1.</summary>
    public string Name => Encoding.Latin1.GetString(this.name);

    /// <summary>The length of the resource data in bytes.</summary>
    public int Length => this.data.Length;

    /// <summary>The resource data (not copied); for internal writers.</summary>
    internal ReadOnlySpan<byte> RawData => this.data;

    /// <summary>The number of bytes <see cref="WriteTo"/> produces: header, padded name, size and padded data.</summary>
    internal int SerializedLength => 4 + 2 + Pad(1 + this.name.Length) + 4 + Pad(this.data.Length);

    /// <summary>Returns a copy of the resource data.</summary>
    public byte[] ToByteArray() => (byte[])this.data.Clone();

    public override string ToString()
        => FormattableString.Invariant($"PhotoshopResource [ {this.Signature} 0x{this.Id:X4} '{this.Name}' {this.Length} bytes ]");

    /// <summary>Returns a resource with the same signature and name but different data.</summary>
    internal PhotoshopResource WithData(byte[] newData) => new(this.signature, this.Id, this.name, newData);

    /// <summary>Writes the resource in the image resource block layout; <paramref name="destination"/> must hold <see cref="SerializedLength"/> bytes.</summary>
    internal void WriteTo(Span<byte> destination)
    {
        this.signature.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], this.Id);
        destination[6] = (byte)this.name.Length;
        this.name.CopyTo(destination[7..]);
        int pos = 6 + Pad(1 + this.name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], (uint)this.data.Length);
        this.data.CopyTo(destination[(pos + 4)..]);
        destination.Slice(pos + 4 + this.data.Length, Pad(this.data.Length) - this.data.Length).Clear();
        destination.Slice(7 + this.name.Length, Pad(1 + this.name.Length) - 1 - this.name.Length).Clear();
    }

    private static int Pad(int length) => (length + 1) & ~1;
}

/// <summary>
/// A Photoshop image resource block: the container Adobe applications store in JPEG APP13 segments
/// (<c>"Photoshop 3.0"</c>) and in TIFF tag 34377, holding resources such as resolution info, slices, paths, the
/// IPTC-NAA record (0x0404) and its MD5 digest (0x0425).
/// </summary>
/// <remarks>
/// Decoders move the IPTC-NAA resource into <see cref="ImageMetadata.IptcProfile"/> and keep every other resource
/// here, in file order, so it can be written back. Encoders insert the current IPTC profile (as resource 0x0404 in
/// JPEG; TIFF stores IPTC in tag 33723 instead) and keep the IPTC digest (0x0425) honest: an unmodified profile keeps
/// its digest, a changed profile has the digest recomputed when the image has no XMP packet and removed when it has
/// one (this library does not synchronise XMP with IPTC, so it must not claim the two agree), and removing the
/// profile removes the digest. Set the property to <see langword="null"/> to drop the block entirely.
/// </remarks>
public sealed class PhotoshopResources : IDeepCloneable<PhotoshopResources>
{
    /// <summary>The resource identifier of the IPTC-NAA record.</summary>
    internal const ushort IptcResourceId = 0x0404;

    /// <summary>The resource identifier of the MD5 digest of the IPTC-NAA record.</summary>
    internal const ushort IptcDigestResourceId = 0x0425;

    /// <summary>
    /// The most resources a parse materializes; Adobe files carry tens. The cap keeps a block of minimal resources
    /// (twelve bytes each) from costing many times its own size.
    /// </summary>
    private const int MaxResources = 65536;

    private readonly List<PhotoshopResource> resources = new();

    /// <summary>Creates an empty block.</summary>
    public PhotoshopResources()
    {
    }

    /// <summary>
    /// Parses an image resource block (a sequence of <c>8BIM</c> resources, without the JPEG <c>"Photoshop 3.0"</c>
    /// identifier). Parsing is lenient: it stops at the first malformed or truncated resource, keeping the ones
    /// before it.
    /// </summary>
    public PhotoshopResources(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.Parse(data);
    }

    private PhotoshopResources(PhotoshopResources other) => this.resources.AddRange(other.resources);

    /// <summary>The resources in stored order.</summary>
    public IReadOnlyList<PhotoshopResource> Resources => this.resources.AsReadOnly();

    /// <summary>The number of resources.</summary>
    public int Count => this.resources.Count;

    /// <summary>True when a resource with <paramref name="id"/> is present.</summary>
    public bool Contains(ushort id) => this.IndexOf(id) >= 0;

    /// <summary>Returns the first resource with <paramref name="id"/>, or <see langword="null"/>.</summary>
    public PhotoshopResource? Get(ushort id)
    {
        int index = this.IndexOf(id);
        return index < 0 ? null : this.resources[index];
    }

    /// <summary>
    /// Adds a resource, replacing every resource with the same identifier (the first keeps its position). A new
    /// identifier is inserted before the first resource with a larger one, the order Adobe applications write.
    /// </summary>
    public void Set(PhotoshopResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        int first = this.IndexOf(resource.Id);
        if (first >= 0)
        {
            this.resources[first] = resource;
            for (int i = this.resources.Count - 1; i > first; i--)
            {
                if (this.resources[i].Id == resource.Id)
                {
                    this.resources.RemoveAt(i);
                }
            }

            return;
        }

        int index = 0;
        while (index < this.resources.Count && this.resources[index].Id <= resource.Id)
        {
            index++;
        }

        this.resources.Insert(index, resource);
    }

    /// <summary>Removes every resource with <paramref name="id"/>; returns false when there was none.</summary>
    public bool Remove(ushort id) => this.resources.RemoveAll(r => r.Id == id) > 0;

    /// <summary>Serializes the block: each resource with its padded name and padded data, in stored order.</summary>
    public byte[] ToByteArray()
    {
        long total = 0;
        foreach (PhotoshopResource resource in this.resources)
        {
            total += resource.SerializedLength;
        }

        var result = new byte[checked((int)total)];
        int pos = 0;
        foreach (PhotoshopResource resource in this.resources)
        {
            int length = resource.SerializedLength;
            resource.WriteTo(result.AsSpan(pos, length));
            pos += length;
        }

        return result;
    }

    public PhotoshopResources DeepClone() => new(this);

    public override string ToString() => FormattableString.Invariant($"PhotoshopResources [ {this.Count} resources ]");

    /// <summary>
    /// Parses a resource block for a decoder: returns <see langword="null"/> when no resource could be read. Only the
    /// size cap raises an exception.
    /// </summary>
    /// <exception cref="InvalidImageContentException">The block exceeds the metadata size cap.</exception>
    internal static PhotoshopResources? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length > ExifReader.MaxExifBytes)
        {
            throw new InvalidImageContentException(
                $"Photoshop image resource block of {data.Length:N0} bytes exceeds the {ExifReader.MaxExifBytes:N0} byte limit.");
        }

        var block = new PhotoshopResources();
        block.Parse(data);
        return block.Count > 0 ? block : null;
    }

    /// <summary>
    /// Splits a resource block read from a file into its IPTC profile (the first 0x0404 resource) and the remaining
    /// resources. Either part is <see langword="null"/> when absent or unreadable.
    /// </summary>
    internal static (IptcProfile? Iptc, PhotoshopResources? Others) SplitIptc(ReadOnlySpan<byte> data)
    {
        PhotoshopResources? block = TryParse(data);
        if (block is null)
        {
            return (null, null);
        }

        PhotoshopResource? iptcResource = block.Get(IptcResourceId);
        IptcProfile? iptc = iptcResource is null ? null : IptcProfile.TryParse(iptcResource.RawData);
        block.Remove(IptcResourceId);
        return (iptc, block.Count > 0 ? block : null);
    }

    /// <summary>
    /// Builds the resource block an encoder writes: <paramref name="resources"/> with the current IPTC data and an
    /// honest IPTC digest (see the class remarks), or <see langword="null"/> when nothing is left to write.
    /// </summary>
    /// <param name="resources">The image's resources, or null.</param>
    /// <param name="iptc">The image's IPTC profile, or null.</param>
    /// <param name="iim">The serialized IPTC profile the container will carry, or null when there is none.</param>
    /// <param name="hasXmp">True when the image also carries an XMP packet.</param>
    /// <param name="embedIptc">True for JPEG, where the IPTC data lives inside the block as resource 0x0404.</param>
    internal static PhotoshopResources? Compose(
        PhotoshopResources? resources, IptcProfile? iptc, byte[]? iim, bool hasXmp, bool embedIptc)
    {
        PhotoshopResources block = resources?.DeepClone() ?? new PhotoshopResources();
        if (iim is { Length: > 0 })
        {
            PhotoshopResource? existing = block.Get(IptcResourceId);
            if (existing is not null)
            {
                block.Set(existing.WithData(iim));
            }
            else if (embedIptc)
            {
                block.Set(new PhotoshopResource(IptcResourceId, iim));
            }
        }

        PhotoshopResource? digest = block.Get(IptcDigestResourceId);
        if (digest is not null)
        {
            // The digest describes the IPTC data the reader will find: the profile, or a 0x0404 the caller put in
            // the block themselves when there is no profile.
            byte[] described = iim is { Length: > 0 } ? iim : block.Get(IptcResourceId)?.ToByteArray() ?? Array.Empty<byte>();
            byte[]? md5 = TryMd5(described);
            bool current = md5 is not null && digest.RawData.SequenceEqual(md5);
            bool untouched = iptc is { IsUnmodifiedSource: true } && iim is { Length: > 0 };
            if (!current && !untouched)
            {
                if (md5 is not null && described.Length > 0 && !hasXmp)
                {
                    block.Set(digest.WithData(md5));
                }
                else
                {
                    block.Remove(IptcDigestResourceId);
                }
            }
        }

        return block.Count > 0 ? block : null;
    }

    /// <summary>
    /// The MD5 of <paramref name="data"/>, or null where MD5 is unavailable (browser WebAssembly, or an OpenSSL build
    /// in FIPS mode); the digest of a changed profile is then dropped, since it cannot be recomputed.
    /// </summary>
    private static byte[]? TryMd5(byte[] data)
    {
        try
        {
            return System.Security.Cryptography.MD5.HashData(data);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static bool IsKnownSignature(ReadOnlySpan<byte> signature)
        => signature.SequenceEqual("8BIM"u8) || signature.SequenceEqual("8B64"u8) || signature.SequenceEqual("PHUT"u8)
            || signature.SequenceEqual("AgHg"u8) || signature.SequenceEqual("DCSR"u8) || signature.SequenceEqual("MeSa"u8);

    private int IndexOf(ushort id)
    {
        for (int i = 0; i < this.resources.Count; i++)
        {
            if (this.resources[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (pos + 12 <= data.Length && this.resources.Count < MaxResources)
        {
            ReadOnlySpan<byte> signature = data.Slice(pos, 4);
            if (!IsKnownSignature(signature))
            {
                break;
            }

            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]);
            int nameLength = data[pos + 6];
            int nameField = (1 + nameLength + 1) & ~1;
            int sizeAt = pos + 6 + nameField;
            if (sizeAt + 4 > data.Length)
            {
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[sizeAt..]);
            int dataAt = sizeAt + 4;
            if (size > (uint)(data.Length - dataAt))
            {
                break; // Runs past the end of the block: truncated.
            }

            byte[] name = data.Slice(pos + 7, nameLength).ToArray();
            byte[] payload = data.Slice(dataAt, (int)size).ToArray();
            this.resources.Add(new PhotoshopResource(signature.ToArray(), id, name, payload));

            // The data is padded to an even length; a writer that omitted the final pad byte is tolerated.
            pos = (int)Math.Min((long)dataAt + ((size + 1) & ~1u), data.Length);
        }
    }
}
