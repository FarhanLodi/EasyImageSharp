namespace EasyImageSharp.Metadata;

/// <summary>An object that can produce an independent deep copy of itself.</summary>
/// <typeparam name="T">The type returned by <see cref="DeepClone"/>.</typeparam>
public interface IDeepCloneable<out T>
{
    /// <summary>Creates a deep copy: mutating the copy never affects the original and vice versa.</summary>
    T DeepClone();
}

/// <summary>
/// Marker for the format-specific metadata containers stored inside <see cref="ImageMetadata"/> and
/// <see cref="ImageFrameMetadata"/> (for example <see cref="JpegMetadata"/> or <see cref="GifFrameMetadata"/>).
/// </summary>
public interface IFormatMetadata : IDeepCloneable<IFormatMetadata>
{
}
