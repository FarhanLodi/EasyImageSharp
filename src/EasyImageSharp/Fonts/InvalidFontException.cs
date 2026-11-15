namespace EasyImageSharp.Fonts;

/// <summary>
/// Thrown when data given to the font loader is not a font this library reads, or is recognised as a TrueType,
/// OpenType, collection, WOFF or WOFF2 file but is malformed, truncated or internally inconsistent.
/// </summary>
/// <remarks>
/// Font loading follows the same contract as the image decoders: malformed data throws
/// <see cref="InvalidFontException"/>; a format or feature the library recognises but does not implement (CFF2
/// variable outlines, PostScript Type 1 in sfnt, bitmap-only fonts) throws <see cref="NotSupportedException"/>.
/// Framework exceptions such as <see cref="IndexOutOfRangeException"/> never escape for malformed font data.
/// Glyph outlines are decoded lazily, when a glyph is first drawn or measured; a glyph whose data turns out to be
/// malformed at that point renders as an empty glyph instead of throwing, so one damaged glyph cannot fail a
/// drawing operation.
/// </remarks>
public sealed class InvalidFontException : Exception
{
    /// <summary>Initializes the exception with a message describing what is wrong with the font data.</summary>
    public InvalidFontException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and the exception that exposed the problem.</summary>
    public InvalidFontException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
