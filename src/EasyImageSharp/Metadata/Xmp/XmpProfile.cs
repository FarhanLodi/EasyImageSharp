using System.Text;

namespace EasyImageSharp.Metadata.Xmp;

/// <summary>
/// An embedded XMP packet (Adobe Extensible Metadata Platform), carried as the raw UTF-8 XML bytes so it can be
/// written back unchanged. The library does not interpret the packet's contents.
/// </summary>
public sealed class XmpProfile : IDeepCloneable<XmpProfile>
{
    private readonly byte[] data;

    /// <summary>Wraps raw packet bytes (UTF-8 XML, optionally with a byte-order mark).</summary>
    public XmpProfile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
    }

    /// <summary>Creates a profile from XML text; it is stored as UTF-8 without a byte-order mark.</summary>
    public XmpProfile(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        this.data = new UTF8Encoding(false).GetBytes(xml);
    }

    /// <summary>The length of the raw packet in bytes.</summary>
    public int Length => this.data.Length;

    /// <summary>Returns a copy of the raw packet bytes.</summary>
    public byte[] ToByteArray() => (byte[])this.data.Clone();

    /// <summary>Returns the packet as a string (UTF-8 decoded, byte-order mark removed).</summary>
    public string ToXml()
    {
        ReadOnlySpan<byte> span = this.data;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }

    /// <summary>The raw bytes (not copied); for internal writers.</summary>
    internal ReadOnlySpan<byte> RawData => this.data;

    internal byte[] RawArray => this.data;

    public XmpProfile DeepClone() => new((byte[])this.data.Clone());

    public override string ToString() => $"XmpProfile [ {this.Length} bytes ]";
}
