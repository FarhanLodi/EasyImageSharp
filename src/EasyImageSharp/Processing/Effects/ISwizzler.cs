namespace EasyImageSharp.Processing;

/// <summary>
/// A pixel-shuffling transform: maps every source pixel coordinate to a destination coordinate. Used by
/// <c>Swizzle</c> to build a new image of <see cref="DestinationSize"/> where each source pixel is copied to
/// <see cref="Transform"/> of its position; destination pixels not targeted by any source pixel stay
/// transparent black.
/// </summary>
public interface ISwizzler
{
    /// <summary>The size of the image produced by the swizzle.</summary>
    Size DestinationSize { get; }

    /// <summary>Maps a source pixel position to its destination position.</summary>
    Point Transform(Point point);
}
