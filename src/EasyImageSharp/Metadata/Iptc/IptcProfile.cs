using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using EasyImageSharp.Metadata.Exif;

namespace EasyImageSharp.Metadata.Iptc;

/// <summary>
/// IPTC-IIM metadata (the IPTC Information Interchange Model 4.2 "IPTC core" block photo agencies and editors use for
/// captions, keywords, credits and copyright): an ordered list of datasets addressed by <see cref="IptcTag"/>.
/// Profiles are read from JPEG APP13 Photoshop image resources and TIFF tag 33723, and written back by the JPEG and
/// TIFF encoders.
/// </summary>
/// <remarks>
/// <para>
/// Text is decoded as UTF-8 when the envelope's coded character set (1:90) declares it with <c>ESC % G</c>. Without
/// that declaration, text that is valid UTF-8 is read as UTF-8 and anything else as ISO 8859-1 (Latin-1); a profile
/// that declares another character set is read as ISO 8859-1. Setting a value that is not plain ASCII switches the
/// profile to UTF-8: the 1:90 declaration is added and existing text datasets are re-encoded, so no reader has to
/// guess.
/// </para>
/// <para>
/// Lengths are validated in encoded octets, as the specification counts them, so a 64-byte keyword holds fewer than
/// 64 non-ASCII characters. Datasets longer than 32 767 octets use the extended length form. Datasets this library
/// does not name, including whole unknown records, are kept in order and round-trip unchanged, and a profile that is
/// read and not modified is written back byte for byte (without the zero padding some containers add).
/// </para>
/// </remarks>
public sealed class IptcProfile : IDeepCloneable<IptcProfile>
{
    /// <summary>The tag marker that starts every dataset.</summary>
    private const byte TagMarker = 0x1C;

    /// <summary>
    /// The most datasets a parse materializes. Real profiles hold tens to a few thousand; the cap stops a block of
    /// empty datasets (five bytes each) from costing many times its own size in bookkeeping.
    /// </summary>
    private const int MaxDataSets = 65536;

    private readonly List<Entry> entries = new();

    /// <summary>The bytes the profile was parsed from, while it is unmodified and the parse was clean.</summary>
    private byte[]? source;

    /// <summary>Creates an empty profile.</summary>
    public IptcProfile()
    {
    }

    /// <summary>
    /// Parses an IPTC-IIM stream (a sequence of <c>0x1C</c>-tagged datasets). Parsing is lenient: it stops at the
    /// first byte that does not start a dataset or at a dataset that runs past the end, keeping every dataset before
    /// it, so malformed data yields a partial (possibly empty) profile rather than an exception.
    /// </summary>
    /// <param name="data">The IIM bytes, for example the payload of Photoshop image resource 0x0404.</param>
    public IptcProfile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.Parse(data);
    }

    private IptcProfile(IptcProfile other)
    {
        foreach (Entry entry in other.entries)
        {
            this.entries.Add(new Entry(entry.Tag, (byte[])entry.Data.Clone()));
        }

        this.source = other.source is null ? null : (byte[])other.source.Clone();
    }

    private enum CharacterSet
    {
        Undeclared,
        Utf8,
        Other,
    }

    /// <summary>Every dataset in the profile, in stored order (records ascending, as the specification requires).</summary>
    public IReadOnlyList<IptcValue> Values
    {
        get
        {
            CharacterSet charset = this.GetCharacterSet();
            var result = new List<IptcValue>(this.entries.Count);
            foreach (Entry entry in this.entries)
            {
                result.Add(CreateValue(entry, charset));
            }

            return result;
        }
    }

    /// <summary>
    /// True when the envelope's coded character set (1:90) declares UTF-8 (<c>ESC % G</c>). Setting any non-ASCII
    /// text makes this true.
    /// </summary>
    public bool IsUtf8 => this.GetCharacterSet() == CharacterSet.Utf8;

    /// <summary>
    /// True when the profile was parsed from bytes and has not been modified since, so <see cref="ToByteArray"/>
    /// returns exactly those bytes. Encoders use it to keep the Photoshop IPTC digest of an untouched block.
    /// </summary>
    internal bool IsUnmodifiedSource => this.source is not null;

    /// <summary>True when the profile contains at least one dataset with <paramref name="tag"/>.</summary>
    public bool Contains(IptcTag tag) => this.IndexOf(tag) >= 0;

    /// <summary>
    /// Returns the text of the first dataset with <paramref name="tag"/>, or <see langword="null"/> when there is
    /// none. Binary datasets yield an empty string; see <see cref="IptcValue.Value"/>.
    /// </summary>
    public string? GetValue(IptcTag tag)
    {
        int index = this.IndexOf(tag);
        return index < 0 ? null : DecodeText(this.entries[index], this.GetCharacterSet());
    }

    /// <summary>Returns the text of every dataset with <paramref name="tag"/> (all keywords, all by-lines, ...), in stored order.</summary>
    public IReadOnlyList<string> GetValues(IptcTag tag)
    {
        CharacterSet charset = this.GetCharacterSet();
        var result = new List<string>();
        foreach (Entry entry in this.entries)
        {
            if (entry.Tag == tag)
            {
                result.Add(DecodeText(entry, charset));
            }
        }

        return result;
    }

    /// <summary>
    /// Sets <paramref name="tag"/> to a single text value, replacing every existing occurrence (the first keeps its
    /// position).
    /// </summary>
    /// <param name="tag">A text dataset.</param>
    /// <param name="value">The text. Non-ASCII text switches the profile to UTF-8.</param>
    /// <param name="strict">
    /// When true (the default), a value whose encoded length is outside the dataset's IIM 4.2 range, or that is not
    /// all digits for a numeric dataset, throws. When false, an over-long value is truncated to the maximum length at
    /// a character boundary and nothing else is checked.
    /// </param>
    /// <exception cref="ArgumentException">The dataset is binary, or (strict) the value violates the specification.</exception>
    public void SetValue(IptcTag tag, string value, bool strict = true)
    {
        byte[] data = this.PrepareText(tag, value, strict);
        this.Set(tag, data);
    }

    /// <summary>
    /// Appends a text value to a repeatable dataset such as <see cref="IptcTag.Keywords"/>, after its existing
    /// occurrences.
    /// </summary>
    /// <param name="tag">A text dataset.</param>
    /// <param name="value">The text. Non-ASCII text switches the profile to UTF-8.</param>
    /// <param name="strict">As for <see cref="SetValue(IptcTag, string, bool)"/>; additionally, when true, adding a second value to a non-repeatable dataset throws.</param>
    /// <exception cref="ArgumentException">The dataset is binary, or (strict) the value violates the specification.</exception>
    /// <exception cref="InvalidOperationException">(strict) The dataset is not repeatable and already has a value.</exception>
    public void AddValue(IptcTag tag, string value, bool strict = true)
    {
        IptcTagInfo info = IptcTagInfo.Get(tag, out _);
        if (strict && !info.Repeatable && this.Contains(tag))
        {
            throw new InvalidOperationException($"IPTC dataset {Describe(tag)} is not repeatable; use SetValue to replace it.");
        }

        byte[] data = this.PrepareText(tag, value, strict);
        this.Insert(tag, data);
    }

    /// <summary>
    /// Sets <paramref name="tag"/> to raw octets, replacing every existing occurrence. Nothing is validated or
    /// re-encoded: this is the way to write binary datasets such as <see cref="IptcTag.ObjectPreviewData"/>, or
    /// datasets in records this library does not interpret.
    /// </summary>
    public void SetValue(IptcTag tag, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.Set(tag, (byte[])data.Clone());
    }

    /// <summary>Appends raw octets as a further occurrence of <paramref name="tag"/>, without validation.</summary>
    public void AddValue(IptcTag tag, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.Insert(tag, (byte[])data.Clone());
    }

    /// <summary>
    /// Writes a date (and, for the datasets that have one, the paired time with its UTC offset): for example
    /// <see cref="IptcTag.DateCreated"/> receives <c>CCYYMMDD</c> and <see cref="IptcTag.TimeCreated"/> receives
    /// <c>HHMMSS±HHMM</c>. <see cref="IptcTag.ReferenceDate"/> has no paired time.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dateTag"/> is not a date dataset.</exception>
    public void SetDateTime(IptcTag dateTag, DateTimeOffset value)
    {
        if (!IptcTagInfo.IsDate(dateTag))
        {
            throw new ArgumentException($"IPTC dataset {Describe(dateTag)} is not a date dataset.", nameof(dateTag));
        }

        this.SetValue(dateTag, value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        if (IptcTagInfo.PairedTime(dateTag) is IptcTag timeTag)
        {
            TimeSpan offset = value.Offset;
            char sign = offset < TimeSpan.Zero ? '-' : '+';
            offset = offset.Duration();
            this.SetValue(
                timeTag,
                FormattableString.Invariant($"{value.Hour:00}{value.Minute:00}{value.Second:00}{sign}{offset.Hours:00}{offset.Minutes:00}"));
        }
    }

    /// <summary>
    /// Reads a date dataset (and its paired time, when the dataset has one and it is present and readable; otherwise
    /// midnight with a zero offset). Returns false when the date is missing or is not a valid <c>CCYYMMDD</c>
    /// calendar date.
    /// </summary>
    public bool TryGetDateTime(IptcTag dateTag, out DateTimeOffset value)
    {
        value = default;
        string? date = IptcTagInfo.IsDate(dateTag) ? this.GetValue(dateTag) : null;
        if (date is null || date.Length != 8 || !IsDigits(date)
            || !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
        {
            return false;
        }

        TimeSpan timeOfDay = TimeSpan.Zero;
        TimeSpan offset = TimeSpan.Zero;
        if (IptcTagInfo.PairedTime(dateTag) is IptcTag timeTag && this.GetValue(timeTag) is string time)
        {
            TryParseTime(time, ref timeOfDay, ref offset);
        }

        value = new DateTimeOffset(day + timeOfDay, offset);
        return true;
    }

    /// <summary>Removes every dataset with <paramref name="tag"/>; returns false when there was none.</summary>
    public bool RemoveValue(IptcTag tag)
    {
        int removed = this.entries.RemoveAll(e => e.Tag == tag);
        if (removed > 0)
        {
            this.source = null;
        }

        return removed > 0;
    }

    /// <summary>Removes the datasets with <paramref name="tag"/> whose text equals <paramref name="value"/> (ordinal); returns false when none matched.</summary>
    public bool RemoveValue(IptcTag tag, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CharacterSet charset = this.GetCharacterSet();
        int removed = this.entries.RemoveAll(e => e.Tag == tag && DecodeText(e, charset) == value);
        if (removed > 0)
        {
            this.source = null;
        }

        return removed > 0;
    }

    /// <summary>Removes every dataset.</summary>
    public void Clear()
    {
        this.entries.Clear();
        this.source = null;
    }

    /// <summary>
    /// Serializes the profile as an IPTC-IIM stream. A profile parsed cleanly and not modified since returns its
    /// original bytes, less any zero padding that followed the last dataset; otherwise datasets are written in stored
    /// order, each with the standard or (above 32 767 octets) extended length form.
    /// </summary>
    public byte[] ToByteArray()
    {
        if (this.source is not null)
        {
            return (byte[])this.source.Clone();
        }

        long total = 0;
        foreach (Entry entry in this.entries)
        {
            total += 5 + (entry.Data.Length > IptcTagInfo.MaxStandardLength ? 4 : 0) + entry.Data.Length;
        }

        var result = new byte[checked((int)total)];
        int pos = 0;
        foreach (Entry entry in this.entries)
        {
            result[pos] = TagMarker;
            result[pos + 1] = (byte)((int)entry.Tag >> 8);
            result[pos + 2] = (byte)entry.Tag;
            if (entry.Data.Length > IptcTagInfo.MaxStandardLength)
            {
                // Extended dataset (IIM 4.2 section 1.5.2): the high bit flags the form, the low bits give the
                // width of the length field that follows.
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 3), 0x8004);
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 5), (uint)entry.Data.Length);
                pos += 9;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 3), (ushort)entry.Data.Length);
                pos += 5;
            }

            entry.Data.CopyTo(result, pos);
            pos += entry.Data.Length;
        }

        return result;
    }

    public IptcProfile DeepClone() => new(this);

    public override string ToString() => FormattableString.Invariant($"IptcProfile [ {this.entries.Count} datasets ]");

    /// <summary>
    /// Parses IIM bytes for a decoder: returns <see langword="null"/> when no dataset could be read, so a corrupt block
    /// degrades to "no IPTC" instead of failing the decode. Only the size cap raises an exception.
    /// </summary>
    /// <exception cref="InvalidImageContentException">The block exceeds the metadata size cap.</exception>
    internal static IptcProfile? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length > ExifReader.MaxExifBytes)
        {
            throw new InvalidImageContentException($"IPTC block of {data.Length:N0} bytes exceeds the {ExifReader.MaxExifBytes:N0} byte limit.");
        }

        var profile = new IptcProfile();
        profile.Parse(data);
        return profile.entries.Count > 0 ? profile : null;
    }

    private static string Describe(IptcTag tag)
        => FormattableString.Invariant($"{(int)tag >> 8}:{(int)tag & 0xFF} ({tag})");

    private static bool IsDigits(string text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses <c>HHMMSS</c> optionally followed by <c>±HHMM</c>; leaves the outputs untouched when unreadable.</summary>
    private static void TryParseTime(string time, ref TimeSpan timeOfDay, ref TimeSpan offset)
    {
        if (time.Length < 6 || !IsDigits(time[..6]))
        {
            return;
        }

        int hours = int.Parse(time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int minutes = int.Parse(time.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int seconds = int.Parse(time.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return;
        }

        TimeSpan zone = TimeSpan.Zero;
        if (time.Length == 11 && time[6] is '+' or '-' && IsDigits(time[7..]))
        {
            int zoneHours = int.Parse(time.AsSpan(7, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            int zoneMinutes = int.Parse(time.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            if (zoneHours > 14 || zoneMinutes > 59)
            {
                return;
            }

            zone = new TimeSpan(zoneHours, zoneMinutes, 0);
            if (time[6] == '-')
            {
                zone = -zone;
            }
        }

        timeOfDay = new TimeSpan(hours, minutes, seconds);
        offset = zone;
    }

    /// <summary>Truncates UTF-8 (or single-byte) text to at most <paramref name="max"/> bytes at a character boundary.</summary>
    private static byte[] Truncate(byte[] data, int max, bool utf8)
    {
        if (data.Length <= max)
        {
            return data;
        }

        int cut = max;
        if (utf8)
        {
            // Back off over continuation bytes (10xxxxxx) so a multi-byte sequence is never split.
            while (cut > 0 && (data[cut] & 0xC0) == 0x80)
            {
                cut--;
            }
        }

        return data.AsSpan(0, cut).ToArray();
    }

    /// <summary>True when a 1:90 payload holds an ISO 2022 designation of UTF-8: ESC % G, or ESC % / G, H or I.</summary>
    private static bool DeclaresUtf8(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0x1B || data[i + 1] != 0x25)
            {
                continue;
            }

            if (data[i + 2] == 0x47 || (data[i + 2] == 0x2F && i + 3 < data.Length && data[i + 3] is 0x47 or 0x48 or 0x49))
            {
                return true;
            }
        }

        return false;
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        bool clean = false;
        while (true)
        {
            if (pos == data.Length)
            {
                clean = true;
                break;
            }

            if (data[pos] != TagMarker)
            {
                // Writers pad the block (TIFF LONG alignment, Photoshop even padding) with zeros; anything else is
                // damage, and the datasets before it are kept.
                clean = data[pos..].IndexOfAnyExcept((byte)0) < 0;
                break;
            }

            if (pos + 5 > data.Length || this.entries.Count == MaxDataSets)
            {
                break;
            }

            var tag = (IptcTag)((data[pos + 1] << 8) | data[pos + 2]);
            int lengthField = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
            pos += 5;
            long length = lengthField;
            if ((lengthField & 0x8000) != 0)
            {
                // Extended dataset: the low 15 bits give the width of the big-endian length that follows.
                int width = lengthField & 0x7FFF;
                if (width is 0 or > 8 || pos + width > data.Length)
                {
                    break;
                }

                length = 0;
                for (int i = 0; i < width; i++)
                {
                    if (length > (long.MaxValue >> 8))
                    {
                        length = long.MaxValue;
                        break;
                    }

                    length = (length << 8) | data[pos + i];
                }

                pos += width;
            }

            if (length > data.Length - pos)
            {
                break; // Runs past the end of the block: truncated.
            }

            this.entries.Add(new Entry(tag, data.Slice(pos, (int)length).ToArray()));
            pos += (int)length;
        }

        // Only a block that parsed cleanly is kept for a verbatim write-back, without the zero padding that followed
        // its last dataset; a damaged one is rewritten from the datasets that could be read.
        this.source = clean && this.entries.Count > 0 ? data[..pos].ToArray() : null;
    }

    private CharacterSet GetCharacterSet()
    {
        int index = this.IndexOf(IptcTag.CodedCharacterSet);
        if (index < 0)
        {
            return CharacterSet.Undeclared;
        }

        return DeclaresUtf8(this.entries[index].Data) ? CharacterSet.Utf8 : CharacterSet.Other;
    }

    private static IptcValue CreateValue(Entry entry, CharacterSet charset)
        => new(entry.Tag, (byte[])entry.Data.Clone(), DecodeText(entry, charset));

    /// <summary>The dataset as text: decoded in the profile's character set without trailing NULs, or empty for binary datasets.</summary>
    private static string DecodeText(Entry entry, CharacterSet charset)
        => IptcTagInfo.Get(entry.Tag, out _).Format == IptcValueFormat.Binary
            ? string.Empty
            : Decode(entry.Data, charset).TrimEnd('\0');

    private static string Decode(byte[] data, CharacterSet charset)
        => charset == CharacterSet.Other ? Encoding.Latin1.GetString(data) : ExifReader.DecodeUtf8OrLatin1(data);

    /// <summary>Encodes and validates a text value without changing the profile, switching it to UTF-8 only once the value is accepted.</summary>
    private byte[] PrepareText(IptcTag tag, string value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(value);
        IptcTagInfo info = IptcTagInfo.Get(tag, out _);
        if (info.Format == IptcValueFormat.Binary)
        {
            throw new ArgumentException($"IPTC dataset {Describe(tag)} is binary; set it with the byte[] overload.", nameof(tag));
        }

        bool utf8 = this.IsUtf8 || !IsAscii(value);
        byte[] data = utf8 ? Encoding.UTF8.GetBytes(value) : Encoding.ASCII.GetBytes(value);
        if (strict)
        {
            if (data.Length < info.MinLength || data.Length > info.MaxLength)
            {
                string range = info.MinLength == info.MaxLength
                    ? FormattableString.Invariant($"exactly {info.MaxLength}")
                    : FormattableString.Invariant($"{info.MinLength} to {info.MaxLength}");
                throw new ArgumentException(
                    FormattableString.Invariant($"IPTC dataset {Describe(tag)} must be {range} bytes long; the value encodes to {data.Length}."),
                    nameof(value));
            }

            if (info.Format == IptcValueFormat.Digits && !IsDigits(value))
            {
                throw new ArgumentException($"IPTC dataset {Describe(tag)} holds digits only.", nameof(value));
            }
        }
        else
        {
            data = Truncate(data, info.MaxLength, utf8);
        }

        if (utf8 && !this.IsUtf8)
        {
            this.SwitchToUtf8();
        }

        return data;
    }

    /// <summary>
    /// Declares UTF-8 in 1:90 and re-encodes every known text dataset from the way it currently decodes, so the
    /// text a caller reads back is unchanged. Unknown datasets are left as they are: their encoding is not ours to
    /// reinterpret.
    /// </summary>
    private void SwitchToUtf8()
    {
        CharacterSet current = this.GetCharacterSet();
        for (int i = 0; i < this.entries.Count; i++)
        {
            Entry entry = this.entries[i];
            if (IptcTagInfo.IsKnownText(entry.Tag))
            {
                this.entries[i] = entry with { Data = Encoding.UTF8.GetBytes(Decode(entry.Data, current)) };
            }
        }

        this.Set(IptcTag.CodedCharacterSet, new byte[] { 0x1B, 0x25, 0x47 });
    }

    private int IndexOf(IptcTag tag)
    {
        for (int i = 0; i < this.entries.Count; i++)
        {
            if (this.entries[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Replaces every occurrence of a tag with one dataset, which keeps the first occurrence's position.</summary>
    private void Set(IptcTag tag, byte[] data)
    {
        int first = this.IndexOf(tag);
        if (first < 0)
        {
            this.Insert(tag, data);
            return;
        }

        this.entries[first] = new Entry(tag, data);
        for (int i = this.entries.Count - 1; i > first; i--)
        {
            if (this.entries[i].Tag == tag)
            {
                this.entries.RemoveAt(i);
            }
        }

        this.source = null;
    }

    /// <summary>
    /// Inserts a dataset after the last one whose tag sorts at or before it, which keeps a sorted profile sorted and
    /// puts a repeated value after its siblings. The record's version dataset is added first when the record is new.
    /// </summary>
    private void Insert(IptcTag tag, byte[] data)
    {
        int record = (int)tag >> 8;
        if (record is 1 or 2 && ((int)tag & 0xFF) != 0 && !this.Contains((IptcTag)(record << 8)))
        {
            // Both records begin with a mandatory version dataset; IIM 4 is the version every reader expects.
            this.Insert((IptcTag)(record << 8), new byte[] { 0x00, 0x04 });
        }

        int index = this.entries.Count;
        while (index > 0 && this.entries[index - 1].Tag > tag)
        {
            index--;
        }

        this.entries.Insert(index, new Entry(tag, data));
        this.source = null;
    }

    private readonly record struct Entry(IptcTag Tag, byte[] Data);
}
