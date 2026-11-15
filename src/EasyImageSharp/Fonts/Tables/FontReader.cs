using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// A bounds-checked big-endian cursor over a span of font data. Every read that would run past the end of
/// the span throws <see cref="InvalidFontException"/> instead of a framework exception, so table parsers can
/// be written as straight-line code without their own length checks.
/// </summary>
internal ref struct FontReader
{
    private readonly ReadOnlySpan<byte> data;
    private readonly string label;
    private int position;

    public FontReader(ReadOnlySpan<byte> data, string label)
    {
        this.data = data;
        this.label = label;
        this.position = 0;
    }

    /// <summary>The current offset from the start of the span.</summary>
    public int Position
    {
        readonly get => this.position;
        set
        {
            if ((uint)value > (uint)this.data.Length)
            {
                throw Fail(this.label, $"offset {value} lies outside its {this.data.Length}-byte table");
            }

            this.position = value;
        }
    }

    /// <summary>The length of the underlying span.</summary>
    public readonly int Length => this.data.Length;

    /// <summary>Bytes left after <see cref="Position"/>.</summary>
    public readonly int Remaining => this.data.Length - this.position;

    public void Skip(int count) => this.Position = checked(this.position + Math.Max(count, 0));

    public byte ReadUInt8()
    {
        this.Ensure(1);
        return this.data[this.position++];
    }

    public sbyte ReadInt8() => (sbyte)this.ReadUInt8();

    public ushort ReadUInt16()
    {
        this.Ensure(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(this.data.Slice(this.position, 2));
        this.position += 2;
        return value;
    }

    public short ReadInt16() => (short)this.ReadUInt16();

    public int ReadUInt24()
    {
        this.Ensure(3);
        int value = (this.data[this.position] << 16) | (this.data[this.position + 1] << 8) | this.data[this.position + 2];
        this.position += 3;
        return value;
    }

    public uint ReadUInt32()
    {
        this.Ensure(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(this.data.Slice(this.position, 4));
        this.position += 4;
        return value;
    }

    public int ReadInt32() => (int)this.ReadUInt32();

    /// <summary>Reads a 32-bit offset or length that must fit in a non-negative <see cref="int"/>.</summary>
    public int ReadOffset32()
    {
        uint value = this.ReadUInt32();
        if (value > int.MaxValue)
        {
            throw Fail(this.label, $"offset 0x{value:X8} is out of range");
        }

        return (int)value;
    }

    /// <summary>Reads a 16.16 fixed-point number.</summary>
    public float ReadFixed() => this.ReadInt32() / 65536f;

    /// <summary>Reads a 2.14 fixed-point number.</summary>
    public float ReadF2Dot14() => this.ReadInt16() / 16384f;

    public uint ReadTag() => this.ReadUInt32();

    /// <summary>Returns <paramref name="count"/> bytes starting at <see cref="Position"/> and advances past them.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0)
        {
            throw Fail(this.label, $"negative length {count}");
        }

        this.Ensure(count);
        ReadOnlySpan<byte> slice = this.data.Slice(this.position, count);
        this.position += count;
        return slice;
    }

    /// <summary>Returns a sub-span of the whole buffer without moving the cursor.</summary>
    public readonly ReadOnlySpan<byte> Slice(int offset, int count) => SliceOf(this.data, offset, count, this.label);

    /// <summary>Returns the rest of the buffer from <paramref name="offset"/> without moving the cursor.</summary>
    public readonly ReadOnlySpan<byte> SliceFrom(int offset)
    {
        if ((uint)offset > (uint)this.data.Length)
        {
            throw Fail(this.label, $"offset {offset} lies outside its {this.data.Length}-byte table");
        }

        return this.data[offset..];
    }

    /// <summary>Bounds-checked sub-span helper shared by parsers that index a buffer directly.</summary>
    public static ReadOnlySpan<byte> SliceOf(ReadOnlySpan<byte> data, int offset, int count, string label)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
        {
            throw Fail(label, $"range {offset}+{count} lies outside its {data.Length}-byte table");
        }

        return data.Slice(offset, count);
    }

    /// <summary>Reads a big-endian unsigned 16-bit value at an absolute offset, bounds-checked.</summary>
    public static ushort UInt16At(ReadOnlySpan<byte> data, int offset, string label)
    {
        if (offset < 0 || (long)offset + 2 > data.Length)
        {
            throw Fail(label, $"offset {offset} lies outside its {data.Length}-byte table");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    /// <summary>Reads a big-endian unsigned 32-bit value at an absolute offset, bounds-checked.</summary>
    public static uint UInt32At(ReadOnlySpan<byte> data, int offset, string label)
    {
        if (offset < 0 || (long)offset + 4 > data.Length)
        {
            throw Fail(label, $"offset {offset} lies outside its {data.Length}-byte table");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    /// <summary>Builds the exception every font parser throws for malformed data.</summary>
    public static InvalidFontException Fail(string label, string detail) => new($"Malformed {label}: {detail}.");

    /// <summary>Formats a table tag such as <c>0x676C7966</c> as <c>glyf</c>.</summary>
    public static string TagToString(uint tag)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(tag >> (24 - (8 * i)));
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(chars);
    }

    /// <summary>Packs a four-character table tag.</summary>
    public static uint Tag(string tag)
        => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void Ensure(int count)
    {
        if (count > this.data.Length - this.position)
        {
            throw Fail(this.label, $"read of {count} bytes at offset {this.position} runs past the end of its {this.data.Length}-byte table");
        }
    }
}
