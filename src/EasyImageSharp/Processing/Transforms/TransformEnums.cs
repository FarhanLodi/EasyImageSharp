namespace EasyImageSharp.Processing;

/// <summary>The side of the source rectangle that a taper shrinks.</summary>
public enum TaperSide
{
    /// <summary>The left edge shrinks vertically.</summary>
    Left,

    /// <summary>The top edge shrinks horizontally.</summary>
    Top,

    /// <summary>The right edge shrinks vertically.</summary>
    Right,

    /// <summary>The bottom edge shrinks horizontally.</summary>
    Bottom,
}

/// <summary>Which corner(s) of the tapered side move.</summary>
public enum TaperCorner
{
    /// <summary>Only the left (for top/bottom sides) or top (for left/right sides) corner moves.</summary>
    LeftOrTop,

    /// <summary>Only the right (for top/bottom sides) or bottom (for left/right sides) corner moves.</summary>
    RightOrBottom,

    /// <summary>Both corners move toward the middle of the side by half the taper each.</summary>
    Both,
}
