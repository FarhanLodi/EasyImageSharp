using System.Buffers.Binary;
using System.Text;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>
/// Parses TIFF-structured directories (EXIF blobs from JPEG APP1 / PNG eXIf and the page directories of TIFF
/// files) into <see cref="IExifValue"/> instances. The reader is lenient: entries it cannot interpret are
/// skipped, truncated directories yield the entries that fit, and pointer loops are ignored. Only values that
/// exceed the hard size caps raise <see cref="InvalidImageContentException"/>.
/// </summary>
internal static class ExifReader
{
    /// <summary>The largest EXIF payload accepted (64 MB).</summary>
    public const long MaxExifBytes = 64L * 1024 * 1024;

    /// <summary>The largest ICC profile accepted (16 MB).</summary>
    public const long MaxIccBytes = 16L * 1024 * 1024;

    /// <summary>The largest XMP packet accepted (64 MB).</summary>
    public const long MaxXmpBytes = 64L * 1024 * 1024;

    private const int MaxEntriesPerDirectory = 4096;
    private const int MaxDirectoryDepth = 3;

    private const ushort ExifPointerTag = 0x8769;
    private const ushort GpsPointerTag = 0x8825;
    private const ushort InteropPointerTag = 0xA005;
    private const ushort JpegInterchangeFormatTag = 0x0201;
    private const ushort JpegInterchangeFormatLengthTag = 0x0202;
    private const ushort IccProfileTag = 0x8773;
    private const ushort XmpTag = 0x02BC;

    /// <summary>
    /// Parses a complete EXIF payload: the TIFF header ("II*\0" or "MM\0*"), IFD0 with its Exif/GPS/Interop
    /// sub-directories, and IFD1 (thumbnail). Returns false when the header is not a TIFF header.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out List<IExifValue> values, out byte[]? thumbnail, out ByteOrder byteOrder)
    {
        values = new List<IExifValue>();
        thumbnail = null;
        byteOrder = ByteOrder.LittleEndian;
        if (data.Length < 8)
        {
            return false;
        }

        bool bigEndian;
        if (data[0] == (byte)'I' && data[1] == (byte)'I')
        {
            bigEndian = false;
        }
        else if (data[0] == (byte)'M' && data[1] == (byte)'M')
        {
            bigEndian = true;
        }
        else
        {
            return false;
        }

        if (ReadU16(data, 2, bigEndian) != 42)
        {
            return false;
        }

        byteOrder = bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
        long ifd0 = ReadU32(data, 4, bigEndian);
        var state = new State(data.Length);
        ReadDirectory(data, ifd0, bigEndian, bigTiff: false, ExifIfd.Ifd0, values, state, out long next, 0);

        if (next != 0)
        {
            var ifd1 = new List<IExifValue>();
            ReadDirectory(data, next, bigEndian, bigTiff: false, ExifIfd.Ifd1, ifd1, state, out _, 0);
            thumbnail = ExtractThumbnail(data, ifd1);
            if (thumbnail is not null)
            {
                foreach (IExifValue value in ifd1)
                {
                    if (value.Tag.Id is not (JpegInterchangeFormatTag or JpegInterchangeFormatLengthTag))
                    {
                        values.Add(value);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads one directory (with its Exif/GPS/Interop sub-directories) at <paramref name="ifdOffset"/> of a TIFF
    /// file whose values are already validated by the caller to lie inside <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The whole buffered file.</param>
    /// <param name="ifdOffset">The offset of the directory to read.</param>
    /// <param name="bigEndian">True when the file is big-endian.</param>
    /// <param name="bigTiff">
    /// True when the file is BigTIFF (version 43), so the directory uses an 8-byte entry count, 20-byte entries,
    /// 8-byte value offsets and the 64-bit field types. Defaults to false, the classic version-42 layout that
    /// every standalone EXIF payload (JPEG APP1, PNG eXIf) uses.
    /// </param>
    public static List<IExifValue> ReadDirectoryTree(ReadOnlySpan<byte> data, long ifdOffset, bool bigEndian, bool bigTiff = false)
    {
        var values = new List<IExifValue>();
        ReadDirectory(data, ifdOffset, bigEndian, bigTiff, ExifIfd.Ifd0, values, new State(data.Length), out _, 0);
        return values;
    }

    /// <summary>Applies the hard size caps to a value about to be materialized.</summary>
    private static void EnsureWithinCaps(ushort tag, ExifIfd part, long totalBytes)
    {
        if (part == ExifIfd.Ifd0 && tag == IccProfileTag && totalBytes > MaxIccBytes)
        {
            throw new InvalidImageContentException($"Embedded ICC profile of {totalBytes:N0} bytes exceeds the {MaxIccBytes:N0} byte limit.");
        }

        if (part == ExifIfd.Ifd0 && tag == XmpTag && totalBytes > MaxXmpBytes)
        {
            throw new InvalidImageContentException($"Embedded XMP packet of {totalBytes:N0} bytes exceeds the {MaxXmpBytes:N0} byte limit.");
        }

        if (totalBytes > MaxExifBytes)
        {
            throw new InvalidImageContentException($"EXIF value of {totalBytes:N0} bytes exceeds the {MaxExifBytes:N0} byte limit.");
        }
    }

    /// <summary>
    /// Reads one directory and its sub-directories. The container's directory layout is resolved into locals
    /// before the entry loop starts, so a classic directory is walked exactly as it was before BigTIFF was
    /// understood and neither loop ever tests which container version is being read.
    /// </summary>
    private static void ReadDirectory(
        ReadOnlySpan<byte> data, long offset, bool bigEndian, bool bigTiff, ExifIfd part, List<IExifValue> values,
        State state, out long nextIfd, int depth)
    {
        // Classic TIFF: a 2-byte entry count, 12-byte entries and a 4-byte value field at +8 holding values of
        // up to 4 bytes. BigTIFF: an 8-byte count, 20-byte entries and an 8-byte value field at +12.
        int countBytes = bigTiff ? 8 : 2;
        int entryBytes = bigTiff ? 20 : 12;
        int offsetBytes = bigTiff ? 8 : 4;
        int inlineMax = offsetBytes;
        int valueField = 4 + offsetBytes;

        nextIfd = 0;
        if (offset < 0 || offset + countBytes > data.Length || !state.Visited.Add(offset))
        {
            return;
        }

        long entryCount = countBytes == 2
            ? ReadU16(data, (int)offset, bigEndian)
            : (long)ReadU64(data, (int)offset, bigEndian);
        long entriesStart = offset + countBytes;
        long available = (data.Length - entriesStart) / entryBytes;
        if (entryCount < 0)
        {
            entryCount = 0; // A 64-bit count with the sign bit set is not a count: the directory holds nothing.
        }
        else if (entryCount > available)
        {
            entryCount = available; // Truncated directory: read the entries that fit.
        }

        int count = (int)Math.Min(entryCount, MaxEntriesPerDirectory);
        var seen = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            int entry = (int)(entriesStart + (i * (long)entryBytes));
            ushort tag = (ushort)ReadU16(data, entry, bigEndian);
            var type = (ExifDataType)ReadU16(data, entry + 2, bigEndian);
            long elementCount = offsetBytes == 4
                ? ReadU32(data, entry + 4, bigEndian)
                : (long)ReadU64(data, entry + 4, bigEndian);
            int size = ExifDataTypes.SizeOf(type);
            if (size == 0 || !seen.Add(tag))
            {
                continue;
            }

            // An element count larger than the whole payload cannot describe a value that fits inside it, so the
            // entry is skipped either way; rejecting it here keeps the size multiplication well inside long.
            if (elementCount < 0 || elementCount > data.Length)
            {
                continue;
            }

            long total = size * elementCount;
            long valueOffset = total <= inlineMax
                ? entry + valueField
                : offsetBytes == 4
                    ? ReadU32(data, entry + valueField, bigEndian)
                    : (long)ReadU64(data, entry + valueField, bigEndian);
            if (valueOffset < 0 || valueOffset + total > data.Length)
            {
                continue;
            }

            // Sub-directory pointers are followed rather than stored. A pointer is one offset wide, so it is a
            // LONG or IFD in a classic directory and a LONG8 or IFD8 in a BigTIFF one; a BigTIFF writer that
            // keeps the classic 4-byte form for these three tags is followed as well, rather than having its
            // sub-directory read as an ordinary integer value.
            if (depth < MaxDirectoryDepth && (total == offsetBytes || (bigTiff && total == 4)))
            {
                ExifIfd? subPart = (part, tag) switch
                {
                    (ExifIfd.Ifd0, ExifPointerTag) => ExifIfd.Exif,
                    (ExifIfd.Ifd0, GpsPointerTag) => ExifIfd.Gps,
                    (ExifIfd.Exif, InteropPointerTag) => ExifIfd.Interop,
                    _ => null,
                };
                if (subPart is not null)
                {
                    long subOffset = total == 4
                        ? ReadU32(data, (int)valueOffset, bigEndian)
                        : (long)ReadU64(data, (int)valueOffset, bigEndian);
                    ReadDirectory(data, subOffset, bigEndian, bigTiff, subPart.Value, values, state, out _, depth + 1);
                    continue;
                }
            }

            EnsureWithinCaps(tag, part, total);
            if (total > state.Budget)
            {
                continue; // The directory references more bytes than the payload contains: hostile overlap.
            }

            state.Budget -= total;
            object? raw = Decode(type, (uint)elementCount, data.Slice((int)valueOffset, (int)total), bigEndian);
            if (raw is null)
            {
                continue;
            }

            values.Add(CreateValue(tag, part, type, raw, bigEndian));
        }

        long nextPointer = entriesStart + (count * (long)entryBytes);
        if (nextPointer + offsetBytes <= data.Length)
        {
            if (offsetBytes == 4)
            {
                nextIfd = ReadU32(data, (int)nextPointer, bigEndian);
            }
            else
            {
                ulong wide = ReadU64(data, (int)nextPointer, bigEndian);
                nextIfd = wide > long.MaxValue ? 0 : (long)wide;
            }
        }
    }

    private static byte[]? ExtractThumbnail(ReadOnlySpan<byte> data, List<IExifValue> ifd1)
    {
        long offset = -1;
        long length = -1;
        foreach (IExifValue value in ifd1)
        {
            if (value.Tag.Id == JpegInterchangeFormatTag && ExifValueConverter.TryConvert(value.GetValue(), out uint o))
            {
                offset = o;
            }
            else if (value.Tag.Id == JpegInterchangeFormatLengthTag && ExifValueConverter.TryConvert(value.GetValue(), out uint l))
            {
                length = l;
            }
        }

        if (offset < 0 || length <= 0 || offset + length > data.Length)
        {
            return null;
        }

        return data.Slice((int)offset, (int)length).ToArray();
    }

    /// <summary>Wraps decoded raw data in a value of the tag's known CLR type, or a raw-typed value when unknown/incompatible.</summary>
    private static IExifValue CreateValue(ushort tag, ExifIfd part, ExifDataType type, object raw, bool bigEndian)
    {
        ExifTag? known = ExifTag.TryGetKnown(part, tag);
        if (known is not null)
        {
            if (known.ValueType == typeof(string) && raw is byte[] textBytes)
            {
                // UserComment and friends carry an 8-byte character code. The specification types them as
                // UNDEFINED, but writers exist that use BYTE instead; the prefix applies either way.
                ExifDataType textType = known.IsUndefinedByConvention ? ExifDataType.Undefined : type;
                raw = DecodeText(textBytes, textType, bigEndian);
            }

            IExifValue? typed = known.TryCreateValue(type, raw);
            if (typed is not null)
            {
                return typed;
            }
        }

        return CreateRawTag(tag, part, raw).CreateValue(type, raw);
    }

    /// <summary>
    /// Creates a tag whose CLR type matches the decoded value exactly. The type is compared rather than pattern
    /// matched because the runtime treats arrays of same-sized primitives as assignment compatible (an
    /// <see cref="sbyte"/>[] matches <c>is byte[]</c>), which would give signed arrays an unsigned tag type.
    /// </summary>
    private static ExifTag CreateRawTag(ushort id, ExifIfd part, object raw)
    {
        Type type = raw.GetType();
        if (type == typeof(byte)) { return new ExifTag<byte>(id, part); }
        if (type == typeof(byte[])) { return new ExifTag<byte[]>(id, part); }
        if (type == typeof(sbyte)) { return new ExifTag<sbyte>(id, part); }
        if (type == typeof(sbyte[])) { return new ExifTag<sbyte[]>(id, part); }
        if (type == typeof(string)) { return new ExifTag<string>(id, part); }
        if (type == typeof(ushort)) { return new ExifTag<ushort>(id, part); }
        if (type == typeof(ushort[])) { return new ExifTag<ushort[]>(id, part); }
        if (type == typeof(short)) { return new ExifTag<short>(id, part); }
        if (type == typeof(short[])) { return new ExifTag<short[]>(id, part); }
        if (type == typeof(uint)) { return new ExifTag<uint>(id, part); }
        if (type == typeof(uint[])) { return new ExifTag<uint[]>(id, part); }
        if (type == typeof(int)) { return new ExifTag<int>(id, part); }
        if (type == typeof(int[])) { return new ExifTag<int[]>(id, part); }
        if (type == typeof(ulong)) { return new ExifTag<ulong>(id, part); }
        if (type == typeof(ulong[])) { return new ExifTag<ulong[]>(id, part); }
        if (type == typeof(long)) { return new ExifTag<long>(id, part); }
        if (type == typeof(long[])) { return new ExifTag<long[]>(id, part); }
        if (type == typeof(Rational)) { return new ExifTag<Rational>(id, part); }
        if (type == typeof(Rational[])) { return new ExifTag<Rational[]>(id, part); }
        if (type == typeof(SignedRational)) { return new ExifTag<SignedRational>(id, part); }
        if (type == typeof(SignedRational[])) { return new ExifTag<SignedRational[]>(id, part); }
        if (type == typeof(float)) { return new ExifTag<float>(id, part); }
        if (type == typeof(float[])) { return new ExifTag<float[]>(id, part); }
        if (type == typeof(double)) { return new ExifTag<double>(id, part); }
        if (type == typeof(double[])) { return new ExifTag<double[]>(id, part); }
        throw new InvalidOperationException($"Unexpected raw EXIF value type {type}.");
    }

    // ----- Raw decoding -----

    /// <summary>Decodes the bytes of one field into a scalar (count 1) or an array of the field type's CLR type.</summary>
    internal static object? Decode(ExifDataType type, uint count, ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        int n = (int)count;
        switch (type)
        {
            case ExifDataType.Byte:
                return n == 1 ? bytes[0] : bytes.ToArray();
            case ExifDataType.Undefined:
                return bytes.ToArray();
            case ExifDataType.Ascii:
                return DecodeAscii(bytes);
            case ExifDataType.SignedByte:
            {
                if (n == 1)
                {
                    return (sbyte)bytes[0];
                }

                var result = new sbyte[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = (sbyte)bytes[i];
                }

                return result;
            }

            case ExifDataType.Short:
            {
                if (n == 1)
                {
                    return (ushort)ReadU16(bytes, 0, bigEndian);
                }

                var result = new ushort[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = (ushort)ReadU16(bytes, i * 2, bigEndian);
                }

                return result;
            }

            case ExifDataType.SignedShort:
            {
                if (n == 1)
                {
                    return (short)ReadU16(bytes, 0, bigEndian);
                }

                var result = new short[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = (short)ReadU16(bytes, i * 2, bigEndian);
                }

                return result;
            }

            case ExifDataType.Long:
            case ExifDataType.Ifd:
            {
                if (n == 1)
                {
                    return ReadU32(bytes, 0, bigEndian);
                }

                var result = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = ReadU32(bytes, i * 4, bigEndian);
                }

                return result;
            }

            case ExifDataType.SignedLong:
            {
                if (n == 1)
                {
                    return (int)ReadU32(bytes, 0, bigEndian);
                }

                var result = new int[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = (int)ReadU32(bytes, i * 4, bigEndian);
                }

                return result;
            }

            case ExifDataType.Rational:
            {
                if (n == 1)
                {
                    return new Rational(ReadU32(bytes, 0, bigEndian), ReadU32(bytes, 4, bigEndian));
                }

                var result = new Rational[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = new Rational(ReadU32(bytes, i * 8, bigEndian), ReadU32(bytes, (i * 8) + 4, bigEndian));
                }

                return result;
            }

            case ExifDataType.SignedRational:
            {
                if (n == 1)
                {
                    return new SignedRational((int)ReadU32(bytes, 0, bigEndian), (int)ReadU32(bytes, 4, bigEndian));
                }

                var result = new SignedRational[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = new SignedRational((int)ReadU32(bytes, i * 8, bigEndian), (int)ReadU32(bytes, (i * 8) + 4, bigEndian));
                }

                return result;
            }

            case ExifDataType.SingleFloat:
            {
                if (n == 1)
                {
                    return BitConverter.Int32BitsToSingle((int)ReadU32(bytes, 0, bigEndian));
                }

                var result = new float[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = BitConverter.Int32BitsToSingle((int)ReadU32(bytes, i * 4, bigEndian));
                }

                return result;
            }

            case ExifDataType.DoubleFloat:
            {
                if (n == 1)
                {
                    return BitConverter.Int64BitsToDouble((long)ReadU64(bytes, 0, bigEndian));
                }

                var result = new double[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = BitConverter.Int64BitsToDouble((long)ReadU64(bytes, i * 8, bigEndian));
                }

                return result;
            }

            case ExifDataType.Long8:
            case ExifDataType.Ifd8:
            {
                if (n == 1)
                {
                    return ReadU64(bytes, 0, bigEndian);
                }

                var result = new ulong[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = ReadU64(bytes, i * 8, bigEndian);
                }

                return result;
            }

            case ExifDataType.SignedLong8:
            {
                if (n == 1)
                {
                    return (long)ReadU64(bytes, 0, bigEndian);
                }

                var result = new long[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = (long)ReadU64(bytes, i * 8, bigEndian);
                }

                return result;
            }

            default:
                return null;
        }
    }

    /// <summary>Decodes an ASCII field: trailing NULs are dropped; UTF-8 is accepted, anything else is read as Latin-1.</summary>
    internal static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        return DecodeUtf8OrLatin1(bytes[..end]);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static string DecodeUtf8OrLatin1(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Decodes a text tag stored with the UNDEFINED type (UserComment and friends): an 8-byte character code
    /// ("ASCII\0\0\0", "UNICODE\0", "JIS\0\0\0\0\0" or all zero) followed by the text.
    /// </summary>
    internal static string DecodeText(byte[] bytes, ExifDataType type, bool bigEndian)
    {
        if (type != ExifDataType.Undefined || bytes.Length < 8)
        {
            return DecodeAscii(bytes);
        }

        ReadOnlySpan<byte> prefix = bytes.AsSpan(0, 8);
        ReadOnlySpan<byte> body = bytes.AsSpan(8);
        if (prefix.SequenceEqual("ASCII\0\0\0"u8))
        {
            return DecodeAscii(body);
        }

        if (prefix.SequenceEqual("UNICODE\0"u8))
        {
            bool bodyBigEndian = bigEndian;
            if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
            {
                bodyBigEndian = false;
                body = body[2..];
            }
            else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            {
                bodyBigEndian = true;
                body = body[2..];
            }

            body = body[..(body.Length & ~1)];
            string text = bodyBigEndian ? Encoding.BigEndianUnicode.GetString(body) : Encoding.Unicode.GetString(body);
            return text.TrimEnd('\0');
        }

        bool allZero = true;
        foreach (byte b in prefix)
        {
            if (b != 0)
            {
                allZero = false;
                break;
            }
        }

        // JIS (Shift-JIS) needs an encoding provider that is not available; read it as Latin-1 like the "undefined" code.
        return allZero || prefix.SequenceEqual("JIS\0\0\0\0\0"u8) ? DecodeAscii(body) : DecodeAscii(bytes);
    }

    // ----- Primitive readers -----

    internal static uint ReadU16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    internal static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    internal static ulong ReadU64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    private sealed class State
    {
        public State(int dataLength)
        {
            // Legitimate directories never reference more bytes than the payload holds; hostile ones point
            // thousands of entries at the same large block. The budget bounds what gets materialized.
            this.Budget = (long)dataLength + 65536;
        }

        public HashSet<long> Visited { get; } = new();

        public long Budget { get; set; }
    }
}
