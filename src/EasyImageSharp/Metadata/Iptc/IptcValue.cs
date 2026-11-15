namespace EasyImageSharp.Metadata.Iptc;

/// <summary>
/// One IPTC-IIM dataset as stored in an <see cref="IptcProfile"/>: its tag, its raw octets, and those octets
/// decoded as text in the profile's coded character set. Instances are snapshots: changing the profile afterwards
/// does not change a value already returned, and a value cannot be edited in place (use
/// <see cref="IptcProfile.SetValue(IptcTag, string, bool)"/> and friends).
/// </summary>
public sealed class IptcValue
{
    private readonly byte[] data;

    internal IptcValue(IptcTag tag, byte[] data, string text)
    {
        this.Tag = tag;
        this.data = data;
        this.Value = text;
    }

    /// <summary>The dataset's tag (record number in the high byte, dataset number in the low byte).</summary>
    public IptcTag Tag { get; }

    /// <summary>The record number, 1 (envelope) to 9.</summary>
    public int Record => (int)this.Tag >> 8;

    /// <summary>The dataset number within its record.</summary>
    public int DataSet => (int)this.Tag & 0xFF;

    /// <summary>
    /// The octets decoded as text (UTF-8 when the profile declares it, ISO 8859-1 otherwise; see
    /// <see cref="IptcProfile.IsUtf8"/>), with trailing NUL characters removed. Datasets the IIM specification
    /// defines as binary (record versions, the coded character set, preview data) yield an empty string; read them
    /// with <see cref="ToByteArray"/>.
    /// </summary>
    public string Value { get; }

    /// <summary>The number of octets in the dataset.</summary>
    public int Length => this.data.Length;

    /// <summary>Returns a copy of the dataset's raw octets.</summary>
    public byte[] ToByteArray() => (byte[])this.data.Clone();

    public override string ToString() => FormattableString.Invariant($"{this.Record}:{this.DataSet} {this.Tag} = {this.Value}");
}
