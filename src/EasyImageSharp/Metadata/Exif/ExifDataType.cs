namespace EasyImageSharp.Metadata.Exif;

/// <summary>The TIFF/EXIF field types (TIFF 6.0 section 2, EXIF 2.3 section 4.6.2).</summary>
public enum ExifDataType : ushort
{
    /// <summary>Not a valid type; used for entries the reader could not interpret.</summary>
    Unknown = 0,

    /// <summary>8-bit unsigned integer.</summary>
    Byte = 1,

    /// <summary>7-bit ASCII string terminated by NUL.</summary>
    Ascii = 2,

    /// <summary>16-bit unsigned integer.</summary>
    Short = 3,

    /// <summary>32-bit unsigned integer.</summary>
    Long = 4,

    /// <summary>Two 32-bit unsigned integers: numerator and denominator.</summary>
    Rational = 5,

    /// <summary>8-bit signed integer.</summary>
    SignedByte = 6,

    /// <summary>8-bit bytes with format-defined meaning.</summary>
    Undefined = 7,

    /// <summary>16-bit signed integer.</summary>
    SignedShort = 8,

    /// <summary>32-bit signed integer.</summary>
    SignedLong = 9,

    /// <summary>Two 32-bit signed integers: numerator and denominator.</summary>
    SignedRational = 10,

    /// <summary>IEEE 754 single-precision float.</summary>
    SingleFloat = 11,

    /// <summary>IEEE 754 double-precision float.</summary>
    DoubleFloat = 12,

    /// <summary>32-bit offset of a sub-directory (TIFF technical note 1); read like <see cref="Long"/>.</summary>
    Ifd = 13,
}

/// <summary>The image file directory an EXIF tag belongs to.</summary>
public enum ExifIfd : byte
{
    /// <summary>The primary image directory (TIFF tags such as Make, Model, Orientation, XResolution).</summary>
    Ifd0 = 0,

    /// <summary>The Exif private directory (camera settings such as ExposureTime, FNumber, DateTimeOriginal).</summary>
    Exif = 1,

    /// <summary>The GPS information directory.</summary>
    Gps = 2,

    /// <summary>The interoperability directory nested inside <see cref="Exif"/>.</summary>
    Interop = 3,

    /// <summary>The thumbnail directory (IFD1) that follows the primary directory.</summary>
    Ifd1 = 4,
}

/// <summary>Byte sizes of every <see cref="ExifDataType"/>.</summary>
internal static class ExifDataTypes
{
    /// <summary>Returns the size in bytes of one element of <paramref name="type"/>, or 0 for unknown types.</summary>
    public static int SizeOf(ExifDataType type) => type switch
    {
        ExifDataType.Byte or ExifDataType.Ascii or ExifDataType.SignedByte or ExifDataType.Undefined => 1,
        ExifDataType.Short or ExifDataType.SignedShort => 2,
        ExifDataType.Long or ExifDataType.SignedLong or ExifDataType.SingleFloat or ExifDataType.Ifd => 4,
        ExifDataType.Rational or ExifDataType.SignedRational or ExifDataType.DoubleFloat => 8,
        _ => 0,
    };
}
