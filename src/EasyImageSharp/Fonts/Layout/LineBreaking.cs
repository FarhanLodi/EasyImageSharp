namespace EasyImageSharp.Fonts.Layout;

/// <summary>
/// A small subset of the Unicode line breaking rules (UAX #14), enough for captions and watermarks in Latin,
/// Cyrillic, Greek and CJK text: breaks after spaces, hyphens and dashes, zero-width spaces and soft hyphens, and
/// between ideographs or kana, with the common no-break-before (closing punctuation, small kana, prolonged sound
/// mark) and no-break-after (opening punctuation) restrictions.
/// </summary>
internal static class LineBreaking
{
    public const int ZeroWidthSpace = 0x200B;
    public const int SoftHyphen = 0x00AD;

    private const string NoBreakBeforeChars =
        ")]}!?,.:;%\u00BB\u203A\u2019\u201D\u2010\u2013\u301C\uFF5E\u30FC\u3001\u3002\uFF0C\uFF0E\u30FB\uFF1A\uFF1B\uFF1F\uFF01"
        + "\u30FD\u30FE\u309D\u309E\u3005\u303B\u300D\u300F\uFF09\uFF3D\uFF5D\u3015\u3009\u300B\u3011\u3019\u3017\u301F\uFF60"
        + "\u3041\u3043\u3045\u3047\u3049\u3063\u3083\u3085\u3087\u308E\u3095\u3096"
        + "\u30A1\u30A3\u30A5\u30A7\u30A9\u30C3\u30E3\u30E5\u30E7\u30EE\u30F5\u30F6\u30A0";

    private const string NoBreakAfterChars =
        "([{\u00AB\u2039\u2018\u201C\u300C\u300E\uFF08\uFF3B\uFF5B\u3014\u3008\u300A\u3010\u3018\u3016\u301D\uFF5F";

    /// <summary>Spaces that offer a break opportunity after them (not the no-break spaces).</summary>
    public static bool IsBreakingSpace(int c)
        => c is 0x20 or 0x09 or 0x1680 or (>= 0x2000 and <= 0x2006) or (>= 0x2008 and <= 0x200A) or 0x205F or 0x3000;

    /// <summary>
    /// Characters that are never drawn and take no space: joiners, direction marks, variation selectors, the
    /// byte order mark, and other format controls this engine does not interpret.
    /// </summary>
    public static bool IsIgnorable(int c)
        => c is SoftHyphen or 0x034F or 0x061C or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5) or (>= 0x180B and <= 0x180F)
            or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3)
            or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF)
            or (< 0x20 and not 0x09) or (>= 0x7F and <= 0x9F);

    /// <summary>Ideographs, kana, Hangul, Yi and fullwidth forms: text a line may break around.</summary>
    public static bool IsIdeographicLike(int c)
        => c is (>= 0x2E80 and <= 0x2FFF) or (>= 0x3040 and <= 0x30FF) or (>= 0x3100 and <= 0x31FF) or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFF01 and <= 0xFF60) or (>= 0x20000 and <= 0x3FFFF);

    public static bool IsNoBreakBefore(int c) => c <= 0xFFFF && NoBreakBeforeChars.Contains((char)c);

    public static bool IsNoBreakAfter(int c) => c <= 0xFFFF && NoBreakAfterChars.Contains((char)c);

    /// <summary>Whether a line may break between <paramref name="before"/> and <paramref name="after"/> (adjacent visible characters).</summary>
    public static bool CanBreakBetween(int before, int after, int beforePrevious)
    {
        if (IsBreakingSpace(after))
        {
            // Spaces always stay on the line they follow.
            return false;
        }

        if (IsBreakingSpace(before) || before is ZeroWidthSpace or SoftHyphen)
        {
            return true;
        }

        if (IsNoBreakBefore(after) || IsNoBreakAfter(before) || after is 0xA0 or 0x202F or 0x2007 or 0x2060 or 0xFEFF
            || before is 0xA0 or 0x202F or 0x2007 or 0x2060 or 0xFEFF)
        {
            return false;
        }

        // Hyphens break after themselves, except a hyphen-minus that starts a word or precedes a number ("-5").
        if (before is 0x2010 or 0x2012 or 0x2013 || (before == '-' && !IsBreakingSpace(beforePrevious) && beforePrevious >= 0 && !char.IsAsciiDigit((char)Math.Min(after, 0xFFFF))))
        {
            return true;
        }

        // Em dashes and two-em dashes allow a break on either side.
        if (before is 0x2014 or 0x2E3A || after is 0x2014 or 0x2E3A)
        {
            return true;
        }

        return IsIdeographicLike(before) || IsIdeographicLike(after);
    }
}
