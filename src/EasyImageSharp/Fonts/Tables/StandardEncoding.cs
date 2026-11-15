namespace EasyImageSharp.Fonts.Tables;

/// <summary>
/// The CFF Standard Encoding (Adobe Technical Note #5176, appendix B): character code to standard string id, used
/// by <c>seac</c> to name the base and accent glyphs of an accented character.
/// </summary>
internal static class StandardEncoding
{
    /// <summary>Codes 161-255 mapped to their string ids; 0 marks an unencoded code.</summary>
    private static readonly byte[] Upper =
    [
        96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 0, // 161-176
        111, 112, 113, 114, 0, 115, 116, 117, 118, 119, 120, 121, 122, 0, 123, // 177-191
        0, 124, 125, 126, 127, 128, 129, 130, 131, 0, 132, 133, 0, 134, 135, 136, // 192-207
        137, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 208-223
        0, 138, 0, 139, 0, 0, 0, 0, 140, 141, 142, 143, 0, 0, 0, 0, // 224-239
        0, 144, 0, 0, 0, 145, 0, 0, 146, 147, 148, 149, 0, 0, 0, 0, // 240-255
    ];

    /// <summary>The standard string id of <paramref name="code"/>, or 0 when the code is unencoded.</summary>
    public static int Sid(int code) => code switch
    {
        >= 32 and <= 126 => code - 31,
        >= 161 and <= 255 => Upper[code - 161],
        _ => 0,
    };
}
