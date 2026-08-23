namespace EasyImageSharp.Metadata;

/// <summary>The unit in which <see cref="ImageMetadata.HorizontalResolution"/> and <see cref="ImageMetadata.VerticalResolution"/> are expressed.</summary>
public enum PixelResolutionUnit : byte
{
    /// <summary>No absolute unit; the two values only describe the pixel aspect ratio.</summary>
    AspectRatio = 0,

    /// <summary>Pixels per inch (DPI).</summary>
    PixelsPerInch = 1,

    /// <summary>Pixels per centimeter.</summary>
    PixelsPerCentimeter = 2,

    /// <summary>Pixels per meter (the unit used by BMP and PNG).</summary>
    PixelsPerMeter = 3,
}

/// <summary>Byte order of a binary structure.</summary>
public enum ByteOrder
{
    /// <summary>Least significant byte first ("II" in TIFF).</summary>
    LittleEndian,

    /// <summary>Most significant byte first ("MM" in TIFF).</summary>
    BigEndian,
}

/// <summary>Conversions between the units of <see cref="PixelResolutionUnit"/>. Shared by the codecs.</summary>
internal static class ResolutionConverter
{
    private const double InchesPerMeter = 39.3700787401574803;
    private const double CentimetersPerInch = 2.54;

    /// <summary>Converts a resolution between two units. <see cref="PixelResolutionUnit.AspectRatio"/> values pass through unchanged.</summary>
    public static double Convert(double value, PixelResolutionUnit from, PixelResolutionUnit to)
    {
        if (from == to || from == PixelResolutionUnit.AspectRatio || to == PixelResolutionUnit.AspectRatio)
        {
            return value;
        }

        // Normalise to pixels per inch, then to the target.
        double ppi = from switch
        {
            PixelResolutionUnit.PixelsPerCentimeter => value * CentimetersPerInch,
            PixelResolutionUnit.PixelsPerMeter => value / InchesPerMeter,
            _ => value,
        };

        return to switch
        {
            PixelResolutionUnit.PixelsPerCentimeter => ppi / CentimetersPerInch,
            PixelResolutionUnit.PixelsPerMeter => ppi * InchesPerMeter,
            _ => ppi,
        };
    }
}
