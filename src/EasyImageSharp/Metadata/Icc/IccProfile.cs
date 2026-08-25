using System.Buffers.Binary;
using System.Text;

namespace EasyImageSharp.Metadata.Icc;

/// <summary>
/// An embedded ICC colour profile, carried as raw bytes so it can be written back unchanged. The header
/// fields that can be read without a colour-management engine are exposed for inspection; the library does
/// not apply the profile to pixel data.
/// </summary>
public sealed class IccProfile : IDeepCloneable<IccProfile>
{
    private readonly byte[] data;

    /// <summary>Wraps profile bytes. The header is parsed leniently: unreadable headers leave the properties at their defaults.</summary>
    public IccProfile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
        this.Header = IccProfileHeader.Parse(data);
    }

    /// <summary>The parsed profile header.</summary>
    public IccProfileHeader Header { get; }

    /// <summary>The length of the raw profile in bytes.</summary>
    public int Length => this.data.Length;

    /// <summary>Returns a copy of the raw profile bytes.</summary>
    public byte[] ToByteArray() => (byte[])this.data.Clone();

    /// <summary>The raw bytes (not copied); for internal writers.</summary>
    internal ReadOnlySpan<byte> RawData => this.data;

    internal byte[] RawArray => this.data;

    public IccProfile DeepClone() => new((byte[])this.data.Clone());

    public override string ToString()
        => $"IccProfile [ {this.Length} bytes, {this.Header.ColorSpace}, {this.Header.Description ?? "(no description)"} ]";
}

/// <summary>The fields of an ICC profile header (ICC.1:2010 section 7.2) plus the profile description tag.</summary>
public sealed class IccProfileHeader
{
    private IccProfileHeader()
    {
    }

    /// <summary>The profile size declared in the header, or 0 when the header is unreadable.</summary>
    public uint Size { get; private set; }

    /// <summary>The preferred CMM signature (four characters), or an empty string.</summary>
    public string PreferredCmm { get; private set; } = string.Empty;

    /// <summary>The profile version, e.g. 2.1 or 4.3.</summary>
    public Version Version { get; private set; } = new(0, 0);

    /// <summary>The profile/device class signature ("mntr", "prtr", "scnr", "spac", ...), or an empty string.</summary>
    public string ProfileClass { get; private set; } = string.Empty;

    /// <summary>The data colour space signature ("RGB ", "GRAY", "CMYK", ...), or an empty string.</summary>
    public string ColorSpace { get; private set; } = string.Empty;

    /// <summary>The profile connection space signature ("XYZ " or "Lab "), or an empty string.</summary>
    public string ConnectionSpace { get; private set; } = string.Empty;

    /// <summary>The creation date and time stored in the header, or <see langword="null"/> when absent/invalid.</summary>
    public DateTime? CreationDate { get; private set; }

    /// <summary>The device manufacturer signature, or an empty string.</summary>
    public string DeviceManufacturer { get; private set; } = string.Empty;

    /// <summary>The device model signature, or an empty string.</summary>
    public string DeviceModel { get; private set; } = string.Empty;

    /// <summary>The profile description ('desc' tag, v2 textDescription or v4 multiLocalizedUnicode), or <see langword="null"/>.</summary>
    public string? Description { get; private set; }

    /// <summary>True when the header (128 bytes and the 'acsp' signature) was readable.</summary>
    public bool IsValid { get; private set; }

    internal static IccProfileHeader Parse(byte[] data)
    {
        var header = new IccProfileHeader();
        try
        {
            if (data.Length < 128)
            {
                return header;
            }

            ReadOnlySpan<byte> span = data;
            header.Size = BinaryPrimitives.ReadUInt32BigEndian(span);
            header.PreferredCmm = Signature(span, 4);
            header.Version = new Version(span[8], span[9] >> 4, span[9] & 0x0F);
            header.ProfileClass = Signature(span, 12);
            header.ColorSpace = Signature(span, 16);
            header.ConnectionSpace = Signature(span, 20);
            header.CreationDate = ReadDate(span[24..36]);
            header.IsValid = span.Slice(36, 4).SequenceEqual("acsp"u8);
            header.DeviceManufacturer = Signature(span, 48);
            header.DeviceModel = Signature(span, 52);
            header.Description = ReadDescription(span);
        }
        catch (Exception ex) when (Formats.DecoderGuard.IsMalformedInputSymptom(ex))
        {
            // Passthrough profiles may be arbitrary bytes; expose what was readable.
        }

        return header;
    }

    private static string Signature(ReadOnlySpan<byte> span, int offset)
    {
        ReadOnlySpan<byte> raw = span.Slice(offset, 4);
        int end = 4;
        while (end > 0 && raw[end - 1] == 0)
        {
            end--;
        }

        return Encoding.ASCII.GetString(raw[..end]);
    }

    private static DateTime? ReadDate(ReadOnlySpan<byte> span)
    {
        int year = BinaryPrimitives.ReadUInt16BigEndian(span);
        int month = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        int day = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        int hour = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        int minute = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        int second = BinaryPrimitives.ReadUInt16BigEndian(span[10..]);
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadDescription(ReadOnlySpan<byte> span)
    {
        if (span.Length < 132)
        {
            return null;
        }

        uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(span[128..]);
        long tableEnd = 132 + (tagCount * 12L);
        if (tagCount > 4096 || tableEnd > span.Length)
        {
            return null;
        }

        for (int i = 0; i < tagCount; i++)
        {
            int entry = 132 + (i * 12);
            if (!span.Slice(entry, 4).SequenceEqual("desc"u8))
            {
                continue;
            }

            uint offset = BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 4)..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 8)..]);
            if (offset + (long)size > span.Length || size < 8)
            {
                return null;
            }

            ReadOnlySpan<byte> tag = span.Slice((int)offset, (int)size);
            if (tag[..4].SequenceEqual("desc"u8))
            {
                // v2 textDescriptionType: sig(4) reserved(4) count(4) ascii[count] (count includes NUL).
                if (tag.Length < 12)
                {
                    return null;
                }

                uint count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (count == 0 || 12 + count > tag.Length)
                {
                    return null;
                }

                ReadOnlySpan<byte> text = tag.Slice(12, (int)count);
                int end = text.Length;
                while (end > 0 && text[end - 1] == 0)
                {
                    end--;
                }

                return Encoding.Latin1.GetString(text[..end]);
            }

            if (tag[..4].SequenceEqual("mluc"u8))
            {
                // v4 multiLocalizedUnicodeType: sig(4) reserved(4) records(4) recordSize(4) [lang(2) country(2) length(4) offset(4)]...
                if (tag.Length < 28)
                {
                    return null;
                }

                uint records = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (records == 0)
                {
                    return null;
                }

                uint length = BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
                uint textOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
                if (textOffset + (long)length > tag.Length)
                {
                    return null;
                }

                ReadOnlySpan<byte> text = tag.Slice((int)textOffset, (int)(length & ~1u));
                return Encoding.BigEndianUnicode.GetString(text).TrimEnd('\0');
            }

            return null;
        }

        return null;
    }
}
