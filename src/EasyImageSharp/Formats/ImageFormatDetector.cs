namespace EasyImageSharp.Formats;

/// <summary>Detects an image's format from its magic bytes and maps file extensions to formats.</summary>
internal static class ImageFormatDetector
{
    public static ImageFormat? Detect(ReadOnlySpan<byte> data)
    {
        foreach (ImageFormat format in ImageFormat.All)
        {
            if (format.Matches(data))
            {
                return format;
            }
        }

        return null;
    }

    public static ImageFormat DetectOrThrow(ReadOnlySpan<byte> data)
        => Detect(data) ?? throw new UnknownImageFormatException(
            $"The image data was not recognized as a supported format ({string.Join(", ", ImageFormat.All.Select(f => f.Name))}).");

    /// <summary>Resolves the format for a file path based on its extension.</summary>
    public static ImageFormat FormatForPath(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        foreach (ImageFormat format in ImageFormat.All)
        {
            foreach (string candidate in format.FileExtensions)
            {
                if (candidate == ext)
                {
                    return format;
                }
            }
        }

        throw new NotSupportedException($"No format is registered for the file extension '.{ext}'.");
    }
}
