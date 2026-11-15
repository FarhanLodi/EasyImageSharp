using System.Globalization;
using System.Numerics;
using System.Text;
using EasyImageSharp.Metadata;

namespace EasyImageSharp.Formats.Hdr;

/// <summary>
/// The parsed text header of a Radiance picture: the <c>#?</c> identifier line, the variable lines up to the
/// first empty line, and the resolution line that fixes the dimensions and the scanline orientation.
/// </summary>
internal sealed class HdrHeader
{
    /// <summary>Upper bound on a single header line; real headers carry short lines and the odd long command history.</summary>
    private const int MaxLineLength = 64 * 1024;

    private HdrHeader(HdrMetadata metadata, int width, int height, bool xMajor, bool firstDecreasing, bool secondDecreasing, int dataOffset)
    {
        this.Metadata = metadata;
        this.Width = width;
        this.Height = height;
        this.XMajor = xMajor;
        this.FirstDecreasing = firstDecreasing;
        this.SecondDecreasing = secondDecreasing;
        this.DataOffset = dataOffset;
    }

    /// <summary>The header facts reported through <see cref="HdrMetadata"/>.</summary>
    public HdrMetadata Metadata { get; }

    /// <summary>The upright image width (the size of the X axis).</summary>
    public int Width { get; }

    /// <summary>The upright image height (the size of the Y axis).</summary>
    public int Height { get; }

    /// <summary>True when scanlines run along Y (each scanline is a column), as in <c>+X 512 -Y 256</c>.</summary>
    public bool XMajor { get; }

    /// <summary>True when the first (scanline) axis carries a minus sign.</summary>
    public bool FirstDecreasing { get; }

    /// <summary>True when the second (within-scanline) axis carries a minus sign.</summary>
    public bool SecondDecreasing { get; }

    /// <summary>Offset of the first byte of pixel data.</summary>
    public int DataOffset { get; }

    /// <summary>The number of scanlines.</summary>
    public int ScanlineCount => this.XMajor ? this.Width : this.Height;

    /// <summary>The number of pixels in one scanline.</summary>
    public int ScanlineLength => this.XMajor ? this.Height : this.Width;

    /// <summary>
    /// True when <paramref name="data"/> starts like a Radiance picture: <c>#?</c>, an identifier of letters,
    /// digits, underscores or hyphens, and a newline. <c>#?RADIANCE</c> and <c>#?RGBE</c> are accepted outright;
    /// any other identifier also needs a <c>FORMAT=32-bit_rle_</c> line within the first kilobyte, so an
    /// arbitrary text file that happens to open with <c>#?</c> is not claimed.
    /// </summary>
    public static bool IsMatch(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != (byte)'#' || data[1] != (byte)'?')
        {
            return false;
        }

        int end = 2;
        while (end < data.Length && end < 66 && IsIdentifierByte(data[end]))
        {
            end++;
        }

        if (end == 2 || end >= data.Length || data[end] != (byte)'\n')
        {
            return false;
        }

        ReadOnlySpan<byte> identifier = data[2..end];
        if (identifier.SequenceEqual("RADIANCE"u8) || identifier.SequenceEqual("RGBE"u8))
        {
            return true;
        }

        ReadOnlySpan<byte> window = data[..Math.Min(data.Length, 1024)];
        return window.IndexOf("\nFORMAT=32-bit_rle_"u8) >= 0;
    }

    /// <summary>Parses the header, throwing <see cref="InvalidImageContentException"/> for anything malformed.</summary>
    public static HdrHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != (byte)'#' || data[1] != (byte)'?')
        {
            throw new InvalidImageContentException("Missing the Radiance HDR '#?' signature.");
        }

        var metadata = new HdrMetadata();
        int pos = 0;
        ReadOnlySpan<byte> line = ReadLine(data, ref pos);
        metadata.ProgramName = Encoding.Latin1.GetString(line[2..]).Trim();

        bool sawFormat = false;
        while (true)
        {
            line = ReadLine(data, ref pos);
            if (line.IsEmpty)
            {
                break;
            }

            ParseVariable(line, metadata, ref sawFormat);
        }

        // The resolution line follows the blank line that ends the variables.
        ReadOnlySpan<byte> resolution = ReadLine(data, ref pos);
        ParseResolution(resolution, out bool firstIsY, out bool firstDecreasing, out int firstSize, out bool secondDecreasing, out int secondSize);

        int width = firstIsY ? secondSize : firstSize;
        int height = firstIsY ? firstSize : secondSize;
        metadata.Orientation = string.Create(CultureInfo.InvariantCulture, $"{(firstDecreasing ? '-' : '+')}{(firstIsY ? 'Y' : 'X')} {(secondDecreasing ? '-' : '+')}{(firstIsY ? 'X' : 'Y')}");
        return new HdrHeader(metadata, width, height, !firstIsY, firstDecreasing, secondDecreasing, pos);
    }

    private static bool IsIdentifierByte(byte b)
        => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'-';

    /// <summary>Returns the next line without its terminator (a trailing carriage return is dropped too).</summary>
    private static ReadOnlySpan<byte> ReadLine(ReadOnlySpan<byte> data, ref int pos)
    {
        ReadOnlySpan<byte> rest = data[pos..];
        int newline = rest[..Math.Min(rest.Length, MaxLineLength + 1)].IndexOf((byte)'\n');
        if (newline < 0)
        {
            throw new InvalidImageContentException(rest.Length > MaxLineLength
                ? "Radiance HDR header line is too long."
                : "Radiance HDR header is truncated before the resolution line.");
        }

        pos += newline + 1;
        ReadOnlySpan<byte> line = rest[..newline];
        return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
    }

    private static void ParseVariable(ReadOnlySpan<byte> line, HdrMetadata metadata, ref bool sawFormat)
    {
        if (line[0] == (byte)'#')
        {
            return;
        }

        if (TryValue(line, "FORMAT="u8, out string? format))
        {
            HdrColorFormat declared = format switch
            {
                "32-bit_rle_rgbe" => HdrColorFormat.Rgbe,
                "32-bit_rle_xyze" => HdrColorFormat.Xyze,
                _ => throw new NotSupportedException($"Radiance HDR pixel format '{format}' is not supported; only 32-bit_rle_rgbe and 32-bit_rle_xyze are."),
            };

            // A repeated identical FORMAT line is harmless (headers grow as tools append to them); a
            // contradictory one leaves the pixel encoding undefined.
            if (sawFormat && declared != metadata.ColorFormat)
            {
                throw new InvalidImageContentException("Radiance HDR header declares conflicting FORMAT lines.");
            }

            metadata.ColorFormat = declared;
            sawFormat = true;
        }
        else if (TryValue(line, "EXPOSURE="u8, out string? exposure))
        {
            if (TryParsePositive(exposure, out double value))
            {
                metadata.Exposure *= value;
            }
        }
        else if (TryValue(line, "COLORCORR="u8, out string? colorCorrection))
        {
            string[] parts = colorCorrection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && TryParsePositive(parts[0], out double r) && TryParsePositive(parts[1], out double g) && TryParsePositive(parts[2], out double b))
            {
                metadata.ColorCorrection *= new Vector3((float)r, (float)g, (float)b);
            }
        }
        else if (TryValue(line, "PIXASPECT="u8, out string? aspect))
        {
            if (TryParsePositive(aspect, out double value))
            {
                metadata.PixelAspectRatio *= value;
            }
        }
        else if (TryValue(line, "PRIMARIES="u8, out string? primaries))
        {
            string[] parts = primaries.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var values = new float[8];
            bool ok = parts.Length == 8;
            for (int i = 0; ok && i < 8; i++)
            {
                ok = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) && float.IsFinite(values[i]);
            }

            if (ok)
            {
                metadata.Primaries = values;
            }
        }
        else if (TryValue(line, "SOFTWARE="u8, out string? software))
        {
            metadata.Software = software;
        }
    }

    private static bool TryValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        if (line.StartsWith(key))
        {
            value = Encoding.Latin1.GetString(line[key.Length..]).Trim();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParsePositive(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0;

    /// <summary>Parses <c>[+-][XY] size [+-][XY] size</c>, which must name one X and one Y axis.</summary>
    private static void ParseResolution(
        ReadOnlySpan<byte> line, out bool firstIsY, out bool firstDecreasing, out int firstSize, out bool secondDecreasing, out int secondSize)
    {
        int pos = 0;
        ParseAxis(line, ref pos, out bool firstY, out firstDecreasing, out firstSize);
        ParseAxis(line, ref pos, out bool secondY, out secondDecreasing, out secondSize);
        SkipSpaces(line, ref pos);
        if (pos != line.Length || firstY == secondY)
        {
            throw new InvalidImageContentException("Radiance HDR resolution line is malformed.");
        }

        firstIsY = firstY;
    }

    private static void ParseAxis(ReadOnlySpan<byte> line, ref int pos, out bool isY, out bool decreasing, out int size)
    {
        SkipSpaces(line, ref pos);
        if (pos + 2 > line.Length || line[pos] is not ((byte)'+' or (byte)'-') || line[pos + 1] is not ((byte)'X' or (byte)'Y'))
        {
            throw new InvalidImageContentException("Radiance HDR resolution line is malformed.");
        }

        decreasing = line[pos] == (byte)'-';
        isY = line[pos + 1] == (byte)'Y';
        pos += 2;
        SkipSpaces(line, ref pos);
        long value = 0;
        int digits = 0;
        while (pos < line.Length && line[pos] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (line[pos] - (byte)'0');
            if (value > int.MaxValue)
            {
                throw new InvalidImageContentException("Radiance HDR resolution is out of range.");
            }

            pos++;
            digits++;
        }

        if (digits == 0 || value == 0)
        {
            throw new InvalidImageContentException("Radiance HDR resolution line declares a missing or zero dimension.");
        }

        size = (int)value;
    }

    private static void SkipSpaces(ReadOnlySpan<byte> line, ref int pos)
    {
        while (pos < line.Length && line[pos] is (byte)' ' or (byte)'\t')
        {
            pos++;
        }
    }
}
