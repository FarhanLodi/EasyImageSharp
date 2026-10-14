namespace EasyImageSharp.Formats;

/// <summary>Tests an encoded prefix for a custom format. Must not retain the span.</summary>
public delegate bool ImageFormatMatcher(ReadOnlySpan<byte> data);

public sealed partial class ImageFormat
{
    /// <summary>Creates an opt-in format provider. Core has no dependency on the provider's native libraries.</summary>
    public static ImageFormat Create(string name, string mimeType, IEnumerable<string> fileExtensions,
        ImageFormatMatcher matcher, Func<IImageDecoder> decoderFactory, Func<IImageEncoder>? encoderFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentNullException.ThrowIfNull(fileExtensions);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(decoderFactory);
        string[] extensions = fileExtensions.Select(extension =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extension);
            string normalized = extension.TrimStart('.').ToLowerInvariant();
            if (normalized.Length == 0 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c)))
                throw new ArgumentException("Extensions must contain only letters and digits.", nameof(fileExtensions));
            return normalized;
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (extensions.Length == 0) throw new ArgumentException("At least one extension is required.", nameof(fileExtensions));
        return new ImageFormat(name, mimeType, extensions, data => matcher(data), decoderFactory, encoderFactory);
    }
}
