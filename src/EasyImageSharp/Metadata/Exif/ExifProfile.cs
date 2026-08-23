using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>
/// The EXIF metadata of an image: a set of typed tag values across the primary (IFD0), Exif, GPS,
/// Interoperability and thumbnail directories, plus the embedded JPEG thumbnail if any. Profiles are
/// read from JPEG APP1 segments, PNG eXIf chunks and TIFF directories, and serialized back with
/// <see cref="ToByteArray"/>. Unknown tags are preserved with their original type and round-trip unchanged.
/// </summary>
public sealed class ExifProfile : IDeepCloneable<ExifProfile>
{
    private static readonly byte[] JpegExifIdentifier = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };

    private readonly Dictionary<ExifTag, IExifValue> values = new();

    /// <summary>Creates an empty profile.</summary>
    public ExifProfile()
    {
    }

    /// <summary>
    /// Parses a TIFF-structured EXIF payload (optionally prefixed with the JPEG "Exif\0\0" identifier). Data
    /// that is not a valid TIFF structure yields an empty profile; individual malformed entries are skipped.
    /// </summary>
    public ExifProfile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.Parse(data);
    }

    /// <summary>Copy constructor.</summary>
    private ExifProfile(ExifProfile other)
    {
        foreach (KeyValuePair<ExifTag, IExifValue> pair in other.values)
        {
            this.values[pair.Key] = pair.Value.DeepClone();
        }

        this.Thumbnail = other.Thumbnail is null ? null : (byte[])other.Thumbnail.Clone();
        this.ByteOrder = other.ByteOrder;
    }

    /// <summary>All values in the profile, across every directory.</summary>
    public IReadOnlyCollection<IExifValue> Values => this.values.Values;

    /// <summary>The encoded JPEG bytes of the thumbnail stored in IFD1, or <see langword="null"/>. Written back verbatim by <see cref="ToByteArray"/>.</summary>
    public byte[]? Thumbnail { get; set; }

    /// <summary>The byte order of the data the profile was parsed from. <see cref="ToByteArray"/> always writes little-endian.</summary>
    public ByteOrder ByteOrder { get; private set; } = ByteOrder.LittleEndian;

    /// <summary>Returns the value of <paramref name="tag"/>, or <see langword="null"/> when the profile does not contain it (or contains it with an incompatible type).</summary>
    public IExifValue<TValueType>? GetValue<TValueType>(ExifTag<TValueType> tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return this.values.TryGetValue(tag, out IExifValue? value) ? value as IExifValue<TValueType> : null;
    }

    /// <summary>Tries to get the typed value of <paramref name="tag"/>. Setting <see cref="IExifValue{T}.Value"/> on the result updates the profile.</summary>
    public bool TryGetValue<TValueType>(ExifTag<TValueType> tag, [NotNullWhen(true)] out IExifValue<TValueType>? value)
    {
        value = this.GetValue(tag);
        return value is not null;
    }

    /// <summary>Tries to get the value of any tag, typed or not.</summary>
    public bool TryGetValue(ExifTag tag, [NotNullWhen(true)] out IExifValue? value)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return this.values.TryGetValue(tag, out value);
    }

    /// <summary>True when the profile contains a value for <paramref name="tag"/>.</summary>
    public bool Contains(ExifTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return this.values.ContainsKey(tag);
    }

    /// <summary>Sets (adding or replacing) the value of <paramref name="tag"/>.</summary>
    /// <exception cref="NotSupportedException">The tag's value type is not one of the supported EXIF value types.</exception>
    public void SetValue<TValueType>(ExifTag<TValueType> tag, TValueType value)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (!ExifValueConverter.IsSupportedType(typeof(TValueType)))
        {
            throw new NotSupportedException($"EXIF values of type {typeof(TValueType)} are not supported.");
        }

        if (this.values.TryGetValue(tag, out IExifValue? existing) && existing is IExifValue<TValueType> typed)
        {
            typed.Value = value;
            return;
        }

        this.values[tag] = new ExifValue<TValueType>(tag, tag.DefaultDataType, value);
    }

    /// <summary>Removes the value of <paramref name="tag"/>; returns false when it was not present.</summary>
    public bool RemoveValue(ExifTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return this.values.Remove(tag);
    }

    /// <summary>Removes every value and the thumbnail.</summary>
    public void Clear()
    {
        this.values.Clear();
        this.Thumbnail = null;
    }

    /// <summary>
    /// Serializes the profile as a little-endian TIFF structure ("II*\0", IFD0, Exif/GPS/Interop sub-directories,
    /// IFD1 with the thumbnail) without any container-specific prefix. Values that cannot be encoded are skipped.
    /// </summary>
    public byte[] ToByteArray()
    {
        IfdBuilder ifd0 = this.BuildDirectory(ExifIfd.Ifd0);
        IfdBuilder exif = this.BuildDirectory(ExifIfd.Exif);
        IfdBuilder gps = this.BuildDirectory(ExifIfd.Gps);
        IfdBuilder interop = this.BuildDirectory(ExifIfd.Interop);
        if (interop.Count > 0)
        {
            exif.AddSubIfd(0xA005, interop);
        }

        if (exif.Count > 0)
        {
            ifd0.AddSubIfd(0x8769, exif);
        }

        if (gps.Count > 0)
        {
            ifd0.AddSubIfd(0x8825, gps);
        }

        IfdBuilder? ifd1 = null;
        if (this.Thumbnail is { Length: > 0 })
        {
            ifd1 = this.BuildDirectory(ExifIfd.Ifd1);
            if (!ifd1.Contains(0x0103))
            {
                ifd1.AddShort(0x0103, 6); // Compression: JPEG (old style)
            }
        }

        const uint HeaderSize = 8;
        int ifd0Size = ifd0.Measure();
        uint ifd1Offset = ifd1 is null ? 0 : HeaderSize + (uint)ifd0Size;
        int ifd1Size = 0;
        if (ifd1 is not null)
        {
            // The pointer entries are inline (4 bytes) so adding them does not change the measured size.
            ifd1.AddLong(0x0201, 0);
            ifd1.AddLong(0x0202, (uint)this.Thumbnail!.Length);
            ifd1Size = ifd1.Measure();
            ifd1.AddLong(0x0201, ifd1Offset + (uint)ifd1Size);
        }

        int total = (int)HeaderSize + ifd0Size + ifd1Size + (this.Thumbnail?.Length ?? 0);
        var result = new byte[total];
        result[0] = (byte)'I';
        result[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), HeaderSize);
        ifd0.Serialize(HeaderSize, ifd1Offset).CopyTo(result, (int)HeaderSize);
        if (ifd1 is not null)
        {
            ifd1.Serialize(ifd1Offset, 0).CopyTo(result, (int)ifd1Offset);
            this.Thumbnail!.CopyTo(result, (int)ifd1Offset + ifd1Size);
        }

        return result;
    }

    public ExifProfile DeepClone() => new(this);

    /// <summary>Adds a value read by a decoder, keeping the first occurrence of a tag.</summary>
    internal void AddParsed(IExifValue value)
    {
        this.values.TryAdd(value.Tag, value);
    }

    /// <summary>Adds or replaces a value regardless of its CLR type.</summary>
    internal void SetOrReplace(IExifValue value) => this.values[value.Tag] = value;

    /// <summary>Builds the directory holding the profile's values for the given part.</summary>
    internal IfdBuilder BuildDirectory(ExifIfd part)
    {
        var builder = new IfdBuilder();
        foreach (IExifValue value in this.values.Values)
        {
            if (value.Tag.Ifd == part && value.Tag.Id is not (0x8769 or 0x8825 or 0xA005))
            {
                builder.TryAdd(value);
            }
        }

        return builder;
    }

    /// <summary>
    /// Parses EXIF bytes for a decoder: returns <see langword="null"/> when the payload does not start with a TIFF
    /// header, so a corrupt segment degrades to "no EXIF" instead of failing the decode.
    /// </summary>
    internal static ExifProfile? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length > ExifReader.MaxExifBytes)
        {
            throw new InvalidImageContentException($"EXIF payload of {data.Length:N0} bytes exceeds the {ExifReader.MaxExifBytes:N0} byte limit.");
        }

        var profile = new ExifProfile();
        return profile.Parse(data) ? profile : null;
    }

    private bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= JpegExifIdentifier.Length && data[..JpegExifIdentifier.Length].SequenceEqual(JpegExifIdentifier))
        {
            data = data[JpegExifIdentifier.Length..];
        }

        try
        {
            if (!ExifReader.TryParse(data, out List<IExifValue> parsed, out byte[]? thumbnail, out ByteOrder byteOrder))
            {
                return false;
            }

            foreach (IExifValue value in parsed)
            {
                this.AddParsed(value);
            }

            this.Thumbnail = thumbnail;
            this.ByteOrder = byteOrder;
            return true;
        }
        catch (Exception ex) when (Formats.DecoderGuard.IsMalformedInputSymptom(ex))
        {
            // Structurally broken directories: keep whatever was parsed before the damage.
            return this.values.Count > 0;
        }
    }
}
