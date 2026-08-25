using System.Buffers.Binary;
using System.Text;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>
/// Builds a little-endian TIFF image file directory: entries (sorted by tag as the specification requires),
/// external value data, and nested sub-directories referenced through pointer entries. Used by
/// <see cref="ExifProfile.ToByteArray"/> and by the TIFF encoder for its page directories.
/// </summary>
internal sealed class IfdBuilder
{
    private readonly Dictionary<ushort, Entry> entries = new();
    private readonly Dictionary<ushort, IfdBuilder> subIfds = new();

    /// <summary>The number of entries, including sub-directory pointers.</summary>
    public int Count => this.entries.Count + this.subIfds.Count;

    /// <summary>Adds (or replaces) an entry with pre-encoded little-endian data.</summary>
    public void Add(ushort tag, ExifDataType type, uint count, byte[] data)
    {
        this.subIfds.Remove(tag);
        this.entries[tag] = new Entry(tag, type, count, data);
    }

    /// <summary>Adds a SHORT entry with a single value.</summary>
    public void AddShort(ushort tag, ushort value)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        this.Add(tag, ExifDataType.Short, 1, data);
    }

    /// <summary>Adds a SHORT entry with several values.</summary>
    public void AddShorts(ushort tag, ReadOnlySpan<ushort> values)
    {
        var data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), values[i]);
        }

        this.Add(tag, ExifDataType.Short, (uint)values.Length, data);
    }

    /// <summary>Adds a LONG entry with a single value.</summary>
    public void AddLong(ushort tag, uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        this.Add(tag, ExifDataType.Long, 1, data);
    }

    /// <summary>Adds a RATIONAL entry with a single value.</summary>
    public void AddRational(ushort tag, Rational value)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value.Numerator);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), value.Denominator);
        this.Add(tag, ExifDataType.Rational, 1, data);
    }

    /// <summary>Adds an ASCII entry.</summary>
    public void AddAscii(ushort tag, string value)
    {
        byte[] data = ExifValueEncoder.EncodeAscii(value);
        this.Add(tag, ExifDataType.Ascii, (uint)data.Length, data);
    }

    /// <summary>Adds an UNDEFINED (or BYTE) entry holding raw bytes.</summary>
    public void AddBytes(ushort tag, ExifDataType type, byte[] data) => this.Add(tag, type, (uint)data.Length, data);

    /// <summary>Encodes and adds an <see cref="IExifValue"/>. Returns false when the value cannot be represented.</summary>
    public bool TryAdd(IExifValue value)
    {
        if (!ExifValueEncoder.TryEncode(value, out ExifDataType type, out uint count, out byte[]? data) || data is null)
        {
            return false;
        }

        this.Add(value.Tag.Id, type, count, data);
        return true;
    }

    /// <summary>Attaches a sub-directory referenced by a LONG pointer entry with the given tag.</summary>
    public void AddSubIfd(ushort pointerTag, IfdBuilder sub)
    {
        this.entries.Remove(pointerTag);
        this.subIfds[pointerTag] = sub;
    }

    public bool Contains(ushort tag) => this.entries.ContainsKey(tag) || this.subIfds.ContainsKey(tag);

    public bool Remove(ushort tag) => this.entries.Remove(tag) | this.subIfds.Remove(tag);

    /// <summary>The number of bytes <see cref="Serialize"/> produces, including nested sub-directories.</summary>
    public int Measure()
    {
        long size = 2 + (12L * this.Count) + 4;
        foreach (Entry entry in this.entries.Values)
        {
            if (entry.Data.Length > 4)
            {
                size += (entry.Data.Length + 1) & ~1;
            }
        }

        foreach (IfdBuilder sub in this.subIfds.Values)
        {
            size += sub.Measure();
        }

        return checked((int)size);
    }

    /// <summary>
    /// Serializes the directory for placement at absolute file offset <paramref name="baseOffset"/>. All internal
    /// offsets (external values, sub-directories) are absolute; <paramref name="nextIfdOffset"/> is written as the
    /// next-directory pointer.
    /// </summary>
    public byte[] Serialize(uint baseOffset, uint nextIfdOffset)
    {
        int size = this.Measure();
        var buffer = new byte[size];
        this.SerializeInto(buffer, baseOffset, nextIfdOffset);
        return buffer;
    }

    private void SerializeInto(Span<byte> buffer, uint baseOffset, uint nextIfdOffset)
    {
        var sorted = new List<(ushort Tag, Entry? Entry, IfdBuilder? Sub)>(this.Count);
        foreach (Entry entry in this.entries.Values)
        {
            sorted.Add((entry.Tag, entry, null));
        }

        foreach ((ushort tag, IfdBuilder sub) in this.subIfds)
        {
            sorted.Add((tag, null, sub));
        }

        sorted.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));

        int count = sorted.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)count);
        int entryPos = 2;
        int dataPos = 2 + (12 * count) + 4;

        // External data follows the entry table; sub-directories follow the external data.
        long externalTotal = 0;
        foreach ((ushort _, Entry? entry, IfdBuilder? _) in sorted)
        {
            if (entry is not null && entry.Data.Length > 4)
            {
                externalTotal += (entry.Data.Length + 1) & ~1;
            }
        }

        int subPos = (int)(dataPos + externalTotal);
        foreach ((ushort tag, Entry? entry, IfdBuilder? sub) in sorted)
        {
            Span<byte> e = buffer.Slice(entryPos, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, tag);
            if (entry is not null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e[2..], (ushort)entry.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], entry.Count);
                if (entry.Data.Length <= 4)
                {
                    entry.Data.CopyTo(e[8..]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], baseOffset + (uint)dataPos);
                    entry.Data.CopyTo(buffer[dataPos..]);
                    dataPos += (entry.Data.Length + 1) & ~1;
                }
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e[2..], (ushort)ExifDataType.Long);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 1);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], baseOffset + (uint)subPos);
                int subSize = sub!.Measure();
                sub.SerializeInto(buffer.Slice(subPos, subSize), baseOffset + (uint)subPos, 0);
                subPos += subSize;
            }

            entryPos += 12;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[entryPos..], nextIfdOffset);
    }

    private sealed record Entry(ushort Tag, ExifDataType Type, uint Count, byte[] Data);
}

/// <summary>Encodes <see cref="IExifValue"/> instances into little-endian field data.</summary>
internal static class ExifValueEncoder
{
    /// <summary>Encodes an ASCII field: UTF-8 bytes followed by a NUL terminator.</summary>
    public static byte[] EncodeAscii(string? value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var data = new byte[text.Length + 1];
        text.CopyTo(data, 0);
        return data;
    }

    /// <summary>Encodes a text field stored as UNDEFINED with the EXIF character-code prefix.</summary>
    public static byte[] EncodeText(string? value)
    {
        value ??= string.Empty;
        bool ascii = true;
        foreach (char c in value)
        {
            if (c > 0x7F)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            byte[] text = Encoding.ASCII.GetBytes(value);
            var data = new byte[8 + text.Length];
            "ASCII\0\0\0"u8.CopyTo(data);
            text.CopyTo(data, 8);
            return data;
        }

        byte[] unicode = Encoding.Unicode.GetBytes(value);
        var result = new byte[8 + unicode.Length];
        "UNICODE\0"u8.CopyTo(result);
        unicode.CopyTo(result, 8);
        return result;
    }

    /// <summary>
    /// Encodes a value with its remembered field type when the data fits that type, or with the default type of
    /// its CLR representation otherwise. Returns false for null arrays and unsupported CLR types.
    /// </summary>
    public static bool TryEncode(IExifValue value, out ExifDataType type, out uint count, out byte[]? data)
    {
        type = ExifDataType.Unknown;
        count = 0;
        data = null;
        object? raw = value.GetValue();
        if (raw is null)
        {
            if (value.Tag.ValueType != typeof(string))
            {
                return false;
            }

            raw = string.Empty;
        }

        if (raw is string text)
        {
            // Text the specification types as UNDEFINED keeps that type (with its character-code prefix) even
            // when the file it came from used BYTE or declared no usable type.
            bool undefined = value.DataType == ExifDataType.Undefined
                || (value.DataType is ExifDataType.Unknown or ExifDataType.Byte && value.Tag.IsUndefinedByConvention);
            type = undefined ? ExifDataType.Undefined : ExifDataType.Ascii;
            data = undefined ? EncodeText(text) : EncodeAscii(text);
            count = (uint)data.Length;
            return true;
        }

        ExifDataType fallback = ExifValueConverter.DefaultDataType(raw.GetType(), value.Tag.IsUndefinedByConvention);
        if (fallback == ExifDataType.Unknown)
        {
            return false;
        }

        ExifDataType preferred = value.DataType;
        if (preferred is ExifDataType.Unknown or ExifDataType.Ascii or ExifDataType.Ifd)
        {
            preferred = fallback;
        }

        if (TryEncodeAs(raw, preferred, out count, out data))
        {
            type = preferred;
            return true;
        }

        if (TryEncodeAs(raw, fallback, out count, out data))
        {
            type = fallback;
            return true;
        }

        return false;
    }

    private static bool TryEncodeAs(object raw, ExifDataType type, out uint count, out byte[]? data)
    {
        count = 0;
        data = null;
        switch (type)
        {
            case ExifDataType.Byte:
            case ExifDataType.Undefined:
                return TryEncodeIntegers(raw, 1, byte.MinValue, byte.MaxValue, out count, out data);
            case ExifDataType.SignedByte:
                return TryEncodeIntegers(raw, 1, sbyte.MinValue, sbyte.MaxValue, out count, out data);
            case ExifDataType.Short:
                return TryEncodeIntegers(raw, 2, ushort.MinValue, ushort.MaxValue, out count, out data);
            case ExifDataType.SignedShort:
                return TryEncodeIntegers(raw, 2, short.MinValue, short.MaxValue, out count, out data);
            case ExifDataType.Long:
                return TryEncodeIntegers(raw, 4, uint.MinValue, uint.MaxValue, out count, out data);
            case ExifDataType.SignedLong:
                return TryEncodeIntegers(raw, 4, int.MinValue, int.MaxValue, out count, out data);
            case ExifDataType.Rational:
            {
                if (!ExifValueConverter.TryConvert(raw, out Rational[]? values) || values is null)
                {
                    return false;
                }

                data = new byte[values.Length * 8];
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 8), values[i].Numerator);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((i * 8) + 4), values[i].Denominator);
                }

                count = (uint)values.Length;
                return true;
            }

            case ExifDataType.SignedRational:
            {
                if (!ExifValueConverter.TryConvert(raw, out SignedRational[]? values) || values is null)
                {
                    return false;
                }

                data = new byte[values.Length * 8];
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 8), values[i].Numerator);
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan((i * 8) + 4), values[i].Denominator);
                }

                count = (uint)values.Length;
                return true;
            }

            case ExifDataType.SingleFloat:
            {
                if (!ExifValueConverter.TryConvert(raw, out float[]? values) || values is null)
                {
                    return false;
                }

                data = new byte[values.Length * 4];
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
                }

                count = (uint)values.Length;
                return true;
            }

            case ExifDataType.DoubleFloat:
            {
                if (!ExifValueConverter.TryConvert(raw, out double[]? values) || values is null)
                {
                    return false;
                }

                data = new byte[values.Length * 8];
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(i * 8), values[i]);
                }

                count = (uint)values.Length;
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryEncodeIntegers(object raw, int size, long min, long max, out uint count, out byte[]? data)
    {
        count = 0;
        data = null;
        if (!ExifValueConverter.TryConvert(raw, out long[]? values) || values is null)
        {
            return false;
        }

        foreach (long v in values)
        {
            if (v < min || v > max)
            {
                return false;
            }
        }

        data = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            switch (size)
            {
                case 1:
                    data[i] = (byte)values[i];
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), (ushort)values[i]);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), (uint)values[i]);
                    break;
            }
        }

        count = (uint)values.Length;
        return true;
    }
}
