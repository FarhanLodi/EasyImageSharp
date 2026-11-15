using System.Globalization;
using System.Numerics;
using System.Text;

namespace EasyImageSharp.Processing;

/// <summary><c>.cube</c> parsing.</summary>
public sealed partial class ColorLut
{
    /// <summary>The longest line the parser accepts; real files have lines of a few dozen characters.</summary>
    private const int MaxLineLength = 4096;

    /// <summary>
    /// Parses the text of a <c>.cube</c> file (Adobe Cube LUT specification 1.0, plus the Resolve extensions).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Understood keywords: <c>TITLE "..."</c>, <c>LUT_1D_SIZE</c> (2-65536), <c>LUT_3D_SIZE</c> (2-256),
    /// <c>DOMAIN_MIN r g b</c> and <c>DOMAIN_MAX r g b</c> (both tables), and Resolve's
    /// <c>LUT_1D_INPUT_RANGE min max</c> / <c>LUT_3D_INPUT_RANGE min max</c>. A file may hold a 1D table, a 3D
    /// table, or both, in which case the 1D entries come first and act as a shaper in front of the 3D lattice.
    /// Lines starting with <c>#</c> and blank lines are ignored; keywords must precede the data, which are
    /// whitespace-separated triples in invariant-culture notation with red varying fastest.
    /// </para>
    /// <para>
    /// The parser is strict: unknown keywords, duplicate keywords, a size outside the allowed range or above
    /// <paramref name="maxCubeSize"/>, a domain whose minimum is not below its maximum, non-finite numbers,
    /// a line longer than 4096 characters and a data count that differs from the declared size are all rejected.
    /// The lattice is allocated only after the header has been validated, so its size is bounded by
    /// <paramref name="maxCubeSize"/>.
    /// </para>
    /// </remarks>
    /// <param name="text">The file content.</param>
    /// <param name="maxCubeSize">The largest 3D lattice size accepted (2-256); lower it to bound memory for untrusted input.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCubeSize"/> is outside 2-256.</exception>
    /// <exception cref="FormatException">The text is not a valid <c>.cube</c> file.</exception>
    public static ColorLut ParseCube(string text, int maxCubeSize = MaxCubeSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return ParseCube(reader, maxCubeSize);
    }

    /// <summary>Reads a <c>.cube</c> file from a UTF-8 (or ASCII) stream; see <see cref="ParseCube(string, int)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The content is not a valid <c>.cube</c> file.</exception>
    public static ColorLut LoadCube(Stream stream, int maxCubeSize = MaxCubeSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        return ParseCube(reader, maxCubeSize);
    }

    /// <summary>
    /// Reads a <c>.cube</c> file from disk with the default size limit; see <see cref="ParseCube(string, int)"/>. Use
    /// <see cref="LoadCube(Stream, int)"/> to set a lower limit for untrusted files.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The file is not a valid <c>.cube</c> file.</exception>
    public static ColorLut LoadCube(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return LoadCube(stream, MaxCubeSize);
    }

    private static ColorLut ParseCube(TextReader reader, int maxCubeSize)
    {
        if (maxCubeSize is < 2 or > MaxCubeSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCubeSize), maxCubeSize, "The 3D LUT size limit must be between 2 and 256.");
        }

        string? title = null;
        int size1D = 0, size3D = 0;
        Vector3? domainMin = null, domainMax = null;
        (float Min, float Max)? range1D = null, range3D = null;
        float[]? shaper = null, lattice = null;
        int expected = 0, filled = 0;
        int lineNumber = 0;
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        var line = new StringBuilder();
        Span<float> triple = stackalloc float[3];
        while (ReadLine(reader, line, ref lineNumber))
        {
            ReadOnlySpan<char> content = line.ToString().AsSpan().Trim();
            if (content.IsEmpty || content[0] == '#')
            {
                continue;
            }

            if (char.IsAsciiLetter(content[0]))
            {
                if (lattice is not null || shaper is not null)
                {
                    throw Error(lineNumber, "keywords must precede the table data");
                }

                int split = content.IndexOfAny(' ', '\t');
                string keyword = (split < 0 ? content : content[..split]).ToString();
                ReadOnlySpan<char> argument = split < 0 ? ReadOnlySpan<char>.Empty : content[(split + 1)..].Trim();
                if (!keywords.Add(keyword))
                {
                    throw Error(lineNumber, $"duplicate {keyword}");
                }

                switch (keyword)
                {
                    case "TITLE":
                        title = ParseTitle(argument, lineNumber);
                        break;
                    case "LUT_1D_SIZE":
                        size1D = ParseSize(argument, 2, MaxShaperSize, lineNumber, keyword);
                        break;
                    case "LUT_3D_SIZE":
                        size3D = ParseSize(argument, 2, maxCubeSize, lineNumber, keyword);
                        break;
                    case "DOMAIN_MIN":
                        ParseFloats(argument, triple, lineNumber, keyword);
                        domainMin = new Vector3(triple[0], triple[1], triple[2]);
                        break;
                    case "DOMAIN_MAX":
                        ParseFloats(argument, triple, lineNumber, keyword);
                        domainMax = new Vector3(triple[0], triple[1], triple[2]);
                        break;
                    case "LUT_1D_INPUT_RANGE":
                        ParseFloats(argument, triple[..2], lineNumber, keyword);
                        range1D = (triple[0], triple[1]);
                        break;
                    case "LUT_3D_INPUT_RANGE":
                        ParseFloats(argument, triple[..2], lineNumber, keyword);
                        range3D = (triple[0], triple[1]);
                        break;
                    default:
                        throw Error(lineNumber, $"unknown keyword '{keyword}'");
                }

                continue;
            }

            // The first data line closes the header: validate it and allocate the bounded tables.
            if (lattice is null && shaper is null)
            {
                if (size1D == 0 && size3D == 0)
                {
                    throw Error(lineNumber, "data before LUT_1D_SIZE or LUT_3D_SIZE");
                }

                expected = size1D + (size3D * size3D * size3D);
                shaper = size1D > 0 ? new float[size1D * 3] : Array.Empty<float>();
                lattice = size3D > 0 ? new float[size3D * size3D * size3D * 3] : Array.Empty<float>();
            }

            if (filled >= expected)
            {
                throw Error(lineNumber, $"more than the {expected} declared table entries");
            }

            ParseFloats(content, triple, lineNumber, "table entry");
            float[] target = filled < size1D ? shaper! : lattice!;
            int index = (filled < size1D ? filled : filled - size1D) * 3;
            target[index] = triple[0];
            target[index + 1] = triple[1];
            target[index + 2] = triple[2];
            filled++;
        }

        if (size1D == 0 && size3D == 0)
        {
            throw Error(lineNumber, "no LUT_1D_SIZE or LUT_3D_SIZE");
        }

        if (filled != expected || expected == 0)
        {
            throw Error(lineNumber, $"{filled} table entries, but the sizes declare {size1D + (size3D * size3D * size3D)}");
        }

        Vector3 min = domainMin ?? Vector3.Zero;
        Vector3 max = domainMax ?? Vector3.One;
        Vector3 min1D = range1D is { } r1 ? new Vector3(r1.Min) : min;
        Vector3 max1D = range1D is { } r1b ? new Vector3(r1b.Max) : max;
        Vector3 min3D = range3D is { } r3 ? new Vector3(r3.Min) : min;
        Vector3 max3D = range3D is { } r3b ? new Vector3(r3b.Max) : max;
        if (!(min1D.X < max1D.X && min1D.Y < max1D.Y && min1D.Z < max1D.Z
            && min3D.X < max3D.X && min3D.Y < max3D.Y && min3D.Z < max3D.Z))
        {
            throw Error(lineNumber, "every domain minimum must be below its maximum");
        }

        return new ColorLut(title, size1D > 0 ? shaper : null, size1D, min1D, max1D, size3D > 0 ? lattice : null, size3D, min3D, max3D);
    }

    /// <summary>Reads one line into <paramref name="line"/>, refusing lines longer than <see cref="MaxLineLength"/>.</summary>
    private static bool ReadLine(TextReader reader, StringBuilder line, ref int lineNumber)
    {
        line.Clear();
        int c = reader.Read();
        if (c < 0)
        {
            return false;
        }

        lineNumber++;
        while (c >= 0 && c != '\n')
        {
            if (c != '\r')
            {
                if (line.Length >= MaxLineLength)
                {
                    throw Error(lineNumber, $"line longer than {MaxLineLength} characters");
                }

                line.Append((char)c);
            }

            c = reader.Read();
        }

        return true;
    }

    private static string ParseTitle(ReadOnlySpan<char> argument, int lineNumber)
    {
        if (argument.Length < 2 || argument[0] != '"' || argument[^1] != '"')
        {
            throw Error(lineNumber, "TITLE must be a double-quoted string");
        }

        return argument[1..^1].ToString();
    }

    private static int ParseSize(ReadOnlySpan<char> argument, int min, int max, int lineNumber, string keyword)
    {
        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < min || size > max)
        {
            throw Error(lineNumber, $"{keyword} must be an integer between {min} and {max}");
        }

        return size;
    }

    private static void ParseFloats(ReadOnlySpan<char> text, Span<float> values, int lineNumber, string what)
    {
        int count = 0;
        while (true)
        {
            text = text.TrimStart();
            if (text.IsEmpty)
            {
                break;
            }

            int end = text.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> token = end < 0 ? text : text[..end];
            text = end < 0 ? ReadOnlySpan<char>.Empty : text[end..];
            if (count == values.Length
                || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
            {
                throw Error(lineNumber, $"{what} must be {values.Length} finite numbers");
            }

            values[count++] = (float)value;
        }

        if (count != values.Length)
        {
            throw Error(lineNumber, $"{what} must be {values.Length} finite numbers");
        }
    }

    private static FormatException Error(int lineNumber, string message)
        => new(FormattableString.Invariant($"Invalid .cube LUT (line {lineNumber}): {message}."));
}
