namespace EasyImageSharp;

/// <summary>Base exception for errors encountered while reading or writing image data.</summary>
/// <remarks>
/// Decoders follow one contract: input that is recognized but malformed, truncated or internally
/// inconsistent throws <see cref="InvalidImageContentException"/>; input in a format or feature the
/// library recognizes but does not implement throws <see cref="NotSupportedException"/>; input that
/// exceeds a configured <see cref="Formats.DecoderOptions"/> limit throws
/// <see cref="ImageSizeLimitExceededException"/>. Framework exceptions such as
/// <see cref="IndexOutOfRangeException"/> never escape a decoder for malformed input.
/// </remarks>
public class ImageFormatException : Exception
{
    public ImageFormatException(string message)
        : base(message)
    {
    }

    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when the encoded image data does not match any registered format.</summary>
public sealed class UnknownImageFormatException : ImageFormatException
{
    public UnknownImageFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when encoded image data is recognized but malformed, truncated or internally inconsistent.</summary>
public sealed class InvalidImageContentException : ImageFormatException
{
    public InvalidImageContentException(string message)
        : base(message)
    {
    }

    public InvalidImageContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an image declares dimensions that exceed the limits configured through
/// <see cref="Formats.DecoderOptions"/>. Raised before any pixel memory is allocated.
/// </summary>
public sealed class ImageSizeLimitExceededException : ImageFormatException
{
    public ImageSizeLimitExceededException(string message)
        : base(message)
    {
    }
}
